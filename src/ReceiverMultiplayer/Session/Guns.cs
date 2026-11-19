using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace ReceiverMultiplayer.Session
{
    /// <summary>
    /// The level's gun list (GUISkinHolder.weapons): the base game's guns, then add-on guns. An add-on gun starts
    /// as an empty placeholder (a WeaponHolder pointing at its mod) and the game fills in its prefabs
    /// (WeaponHolder.Load, which copies them from the add-on, already in memory) only for the gun you carry. In a
    /// session every player's gun must be complete on every machine: the shared item list, the replayed shots
    /// (trails, holes) and the ammo piles all come from it. Otherwise each machine knew only its own add-on gun,
    /// so another player's showed up as yours, and their shots replayed nothing.
    /// </summary>
    static class Guns
    {
        /// <summary>
        /// A gun's name as the session knows it, the same on every machine and whether or not it's been filled in
        /// (Load replaces an add-on gun's list name, its Workshop title, with the name inside the add-on).
        /// </summary>
        public static string NameOf(GameObject entry)
        {
            if (entry == null) return "?";
            var wh = entry.GetComponent<WeaponHolder>();
            if (wh == null) return entry.name;
            var mod = wh.mod;
            if (mod != null)
            {
                try
                {
                    string title = mod.steamworksItem != null ? mod.steamworksItem.GetName() : null;
                    if (!string.IsNullOrEmpty(title)) return title.Trim();
                }
                catch { /* fall back below */ }
                if (!string.IsNullOrEmpty(mod.path)) return Path.GetFileName(mod.path.TrimEnd('/', '\\'));
            }
            return !string.IsNullOrEmpty(wh.display_name) ? wh.display_name : entry.name;
        }

        /// <summary>Fill in every add-on gun of this level's list (cheap: it copies five references).</summary>
        public static void LoadAll(GUISkinHolder holder)
        {
            if (holder == null || holder.weapons == null) return;
            int loaded = 0;
            foreach (var entry in holder.weapons)
            {
                var wh = entry != null ? entry.GetComponent<WeaponHolder>() : null;
                if (wh == null || wh.mod == null || wh.gun_object != null) continue;
                try
                {
                    wh.Load();
                    loaded++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Couldn't load add-on gun '{NameOf(entry)}': {e.Message}");
                }
            }
            if (loaded > 0) Plugin.Log.LogInfo($"Loaded {loaded} add-on gun(s) so every player's gun is known here.");
        }

        /// <summary>
        /// The gun you start a run with: the game reads it from the saved "selected_gun_index" setting and takes
        /// that entry of the new level's list. In a run it's taken by name from the run instead (the setting is
        /// still cleared, as the game does), so it can't land on another gun if the list or the setting differ.
        /// </summary>
        [HarmonyPatch(typeof(GUISkinHolder), "GetGunHolder")]
        static class RunGun
        {
            static bool Prefix(GUISkinHolder __instance, ref GameObject __result)
            {
                var run = RunStarter.Active;
                var s = NetSession.Current;
                if (run == null || s == null || s.Phase != LobbyPhase.InRun || __instance.weapons == null || string.IsNullOrEmpty(run.Gun)) return true;
                foreach (var entry in __instance.weapons)
                {
                    if (entry == null || NameOf(entry) != run.Gun) continue;
                    PlayerPrefs.DeleteKey("selected_gun_index");
                    __result = entry;
                    return false;
                }
                Plugin.Log.LogWarning($"The run's gun '{run.Gun}' isn't in this level's gun list; the game picks.");
                return true;
            }
        }

        /// <summary>A level starting in a session: complete its gun list before anything (ammo piles) uses it.</summary>
        [HarmonyPatch(typeof(GUISkinHolder), nameof(GUISkinHolder.Awake))]
        static class LevelStart
        {
            static void Postfix(GUISkinHolder __instance)
            {
                if (NetSession.Current != null) LoadAll(__instance);
            }
        }
    }
}
