using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.Session
{
    /// <summary>
    /// The game's modifiers and mod settings live in each player's PlayerPrefs, but they change what spawns, how
    /// enemies behave, the fog, and which guns, tapes and level tiles a level is built from, so in a session
    /// everyone uses the host's values: sent on joining, with every run, and whenever the host flips a modifier in
    /// the pause menu's Modifiers window. A client's own values are put back when the session ends.
    /// </summary>
    static class ModifierSync
    {
        public static readonly string[] Keys =
        {
            "modifier_deadly_enemies",
            "modifier_green_demon",
            "modifier_spawn_magazines",
            "modifier_tougher_enemies",
            "limit_tape_count",
            // Mods: on or off, and for guns, tapes and tiles whether add-ons are added to the base game's
            // (0), replace them (1) or are left out (2). GUISkinHolder reads these when a level loads.
            "mods_enabled",
            "mod_gun_loading",
            "mod_tape_loading",
            "mod_tile_loading",
            // Thick Fog: the level's fog switched on (the toggle also calls optionsmenuscript.SetFog).
            "modifier_fog",
        };

        /// <summary>The ones in the pause menu's Modifiers window (and the tape limit): shared live.</summary>
        static bool IsGameplayModifier(string key) => key.StartsWith("modifier_") || key == "limit_tape_count";

        public const int Unset = int.MinValue;

        static Dictionary<string, int> _saved; // the client's own values (Unset = key absent)

        /// <summary>Host: its current values, in <see cref="Keys"/> order.</summary>
        public static int[] Read()
        {
            var values = new int[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
                values[i] = PlayerPrefs.HasKey(Keys[i]) ? PlayerPrefs.GetInt(Keys[i]) : Unset;
            return values;
        }

        // The host's values are used exactly as they are (not clamped): the game reads them loosely (a loading
        // setting is "left out" at 2, "replace" at 1, "added" at anything else), and a client must build the very
        // same level and gun list as the host.

        // The player's own values, also kept in their settings while a session uses the host's, so that a crash
        // mid-session doesn't leave the host's choices behind: they're put back at the next launch.
        const string BackupKey = "receiver_multiplayer_own_settings";

        /// <summary>Client: use the host's values for this run (remembering ours the first time).</summary>
        public static void Apply(int[] values)
        {
            if (values == null || values.Length != Keys.Length) return;
            if (_saved == null)
            {
                _saved = new Dictionary<string, int>();
                foreach (var k in Keys) _saved[k] = PlayerPrefs.HasKey(k) ? PlayerPrefs.GetInt(k) : Unset;
                PlayerPrefs.SetString(BackupKey, string.Join(";", _saved.Select(kv => kv.Key + "=" + kv.Value)));
                PlayerPrefs.Save();
            }
            for (int i = 0; i < Keys.Length; i++)
            {
                if (values[i] == Unset) PlayerPrefs.DeleteKey(Keys[i]);
                else PlayerPrefs.SetInt(Keys[i], values[i]);
            }
        }

        public static void Restore()
        {
            if (_saved == null) return;
            Put(_saved);
            _saved = null;
            ApplyLive();
        }

        static void Put(Dictionary<string, int> values)
        {
            foreach (var kv in values)
            {
                if (kv.Value == Unset) PlayerPrefs.DeleteKey(kv.Key);
                else PlayerPrefs.SetInt(kv.Key, kv.Value);
            }
            PlayerPrefs.DeleteKey(BackupKey);
            PlayerPrefs.Save();
        }

        /// <summary>At launch: the game closed mid-session last time; put the player's own settings back.</summary>
        public static void RecoverAfterCrash()
        {
            try
            {
                if (!PlayerPrefs.HasKey(BackupKey)) return;
                var values = new Dictionary<string, int>();
                foreach (var pair in PlayerPrefs.GetString(BackupKey, "").Split(';'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0 || !int.TryParse(pair.Substring(eq + 1), out int v)) continue;
                    string key = pair.Substring(0, eq);
                    if (Keys.Contains(key)) values[key] = v;
                }
                Put(values);
                Plugin.Log.LogInfo("The game closed during a session last time: your own modifier and mod settings are back.");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Couldn't put your own settings back: {e.Message}"); }
        }

        /// <summary>
        /// Make what's already on screen match the values now in effect: the fog, and the Modifiers window's
        /// checkboxes. (The rest, like the Green Demon or tougher enemies, take effect when a level loads, as in
        /// single-player.)
        /// </summary>
        public static void ApplyLive()
        {
            bool fogWanted = PlayerPrefs.GetInt("modifier_fog", 0) == 1;
            bool sawFogToggle = false;
            foreach (var t in Resources.FindObjectsOfTypeAll<Toggle>())
            {
                if (t == null || !t.gameObject.scene.IsValid() || !Keys.Contains(t.name) || !IsGameplayModifier(t.name)) continue;
                bool want = PlayerPrefs.GetInt(t.name, 0) == 1;
                if (t.name == "modifier_fog")
                {
                    sawFogToggle = true;
                    // Like clicking it: a level's own fog is left alone unless the checkbox actually changes.
                    if (t.isOn != want) RenderSettings.fog = want;
                }
                t.SetIsOnWithoutNotify(want);
            }
            if (!sawFogToggle && fogWanted) RenderSettings.fog = true;
        }

        /// <summary>
        /// The Modifiers window's checkboxes save their value through optionsmenuscript.UpdateInt. In a session
        /// the host's changes go to everyone at once; a client's are undone with a note (the host decides).
        /// </summary>
        [HarmonyPatch(typeof(optionsmenuscript), nameof(optionsmenuscript.UpdateInt))]
        static class ToggleChanged
        {
            static bool Prefix(Toggle toggle)
            {
                var s = NetSession.Current;
                if (s == null || s.IsHost || !s.Joined || toggle == null || !IsGameplayModifier(toggle.name)) return true;
                bool hostValue = PlayerPrefs.GetInt(toggle.name, 0) == 1;
                if (toggle.isOn == hostValue) return true; // the menu setting itself up from the saved values, not a click
                toggle.SetIsOnWithoutNotify(hostValue);
                UI.Notice.Show("In a multiplayer session the host sets the modifiers.");
                return false;
            }

            static void Postfix(Toggle toggle)
            {
                var s = NetSession.Current;
                if (s == null || !s.IsHost || toggle == null || !IsGameplayModifier(toggle.name)) return;
                s.BroadcastModifiers();
            }
        }

        /// <summary>The Thick Fog checkbox also switches the fog directly; on a client it follows the host's value.</summary>
        [HarmonyPatch(typeof(optionsmenuscript), nameof(optionsmenuscript.SetFog))]
        static class FogChanged
        {
            static bool Prefix()
            {
                var s = NetSession.Current;
                if (s == null || s.IsHost || !s.Joined) return true;
                RenderSettings.fog = PlayerPrefs.GetInt("modifier_fog", 0) == 1;
                return false;
            }
        }
    }
}
