using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReceiverMultiplayer.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ReceiverMultiplayer.Session
{
    /// <summary>The run every player is in: same seed and settings everywhere, each with their own gun.</summary>
    sealed class RunInfo
    {
        public int Seed;
        public string Gun;
        /// <summary>The scene everyone is in: the main level, or another the game's menu offers (the gun range).</summary>
        public string Scene = SceneCatalog.GameScene;
        public SharedSettings Settings;
        /// <summary>Indices (into the gun list) of every player's starting gun; level ammo is drawn from these.</summary>
        public List<int> Guns = new List<int>();
    }

    /// <summary>
    /// Starts a seeded match through the game's own path: GUISkinHolder.Awake reads the
    /// "generation_seed" and "selected_gun_index" prefs, then LevelCreatorScript builds the tower.
    /// </summary>
    static class RunStarter
    {
        const float FadeOutSeconds = 0.6f;
        const float FadeInSeconds = 0.8f;
        const float FadeInFallbackSeconds = 10f;

        /// <summary>True only while the mod itself is loading a run (lets the scene-change guards through).</summary>
        public static bool Loading { get; private set; }
        public static RunInfo Active { get; private set; }

        static bool _starting;
        static bool _awaitingSpawn;

        public static List<string> LocalGunNames()
        {
            var holder = Object.FindObjectOfType<GUISkinHolder>();
            if (holder == null || holder.weapons == null)
            {
                // The session checked at join that everyone's gun list matches; use that copy.
                var known = NetSession.Current?.GunNames;
                if (known != null && known.Count > 0) return new List<string>(known);
                Plugin.Log.LogWarning("Gun list unavailable (no GUISkinHolder in this scene).");
                return new List<string>();
            }
            return holder.weapons.Select(Guns.NameOf).ToList();
        }

        /// <summary>Fade to black, then load a fresh match. Fades back in once the player has spawned.</summary>
        public static void Begin(int seed, string gun, SharedSettings settings, List<int> guns, string scene)
        {
            World.WorldSync.OnRunStarting();
            Active = new RunInfo
            {
                Seed = seed, Gun = gun, Settings = settings, Guns = guns ?? new List<int>(),
                Scene = string.IsNullOrEmpty(scene) ? SceneCatalog.GameScene : scene,
            };
            // A second start during the fade-out (the host restarted again at once) replaces the first: the load
            // after the fade reads the newest run. It used to be ignored, leaving this player in the older level.
            if (_starting) return;
            _starting = true;
            Runner.Instance.StartCoroutine(StartRoutine());
        }

        static IEnumerator StartRoutine()
        {
            yield return ScreenFader.FadeTo(1f, FadeOutSeconds);
            try
            {
                var run = Active;
                var names = LocalGunNames();
                int index = names.IndexOf(run.Gun);
                if (index < 0)
                {
                    // Always pass an explicit gun: a random pick would draw from SeededRNG and desync the tower.
                    Plugin.Log.LogWarning($"Gun '{run.Gun}' isn't installed here; using '{(names.Count > 0 ? names[0] : "?")}'.");
                    index = 0;
                }
                PlayerPrefs.SetString("generation_seed", run.Seed.ToString(CultureInfo.InvariantCulture));
                PlayerPrefs.SetInt("selected_gun_index", index);
                _loadingGunIndex = index;

                var menu = Object.FindObjectOfType<optionsmenuscript>();
                if (menu != null && optionsmenuscript.show_menu) menu.HideMenu();
                Time.timeScale = 1f;

                _awaitingSpawn = true;
                Loading = true;
                SceneManager.LoadScene(run.Scene);
            }
            finally
            {
                Loading = false;
                _starting = false;
            }
            Runner.Instance.StartCoroutine(FadeInFallback());
        }

        static int _loadingGunIndex;

        /// <summary>
        /// The level reads its seed (and starting gun) from settings as it wakes, and clears the seed once read. Set
        /// both again right then, so the run's own values are read even if something cleared or changed them since
        /// the load began (two copies of the game on one PC share those settings and raced each other for them).
        /// </summary>
        [HarmonyLib.HarmonyPatch(typeof(GUISkinHolder), nameof(GUISkinHolder.Awake))]
        static class SeedAtWake
        {
            static void Prefix()
            {
                if (!_awaitingSpawn || Active == null) return;
                PlayerPrefs.SetString("generation_seed", Active.Seed.ToString(CultureInfo.InvariantCulture));
                PlayerPrefs.SetInt("selected_gun_index", _loadingGunIndex);
            }
        }

        /// <summary>Called when the local player object starts in the new scene.</summary>
        public static void OnPlayerSpawned(AimScript player)
        {
            if (!_awaitingSpawn) return;
            _awaitingSpawn = false;
            Runner.Instance.StartCoroutine(ScreenFader.FadeTo(0f, FadeInSeconds));
            Runner.Instance.StartCoroutine(LogRunCheck());
        }

        static IEnumerator FadeInFallback()
        {
            yield return new WaitForSecondsRealtime(FadeInFallbackSeconds);
            if (!_awaitingSpawn) yield break;
            _awaitingSpawn = false;
            yield return ScreenFader.FadeTo(0f, FadeInSeconds);
        }

        /// <summary>
        /// Log seed and the first tiles so two players can confirm they're in the same tower, and what this
        /// player started with (so a generous or stingy start can be checked from the logs).
        /// </summary>
        static IEnumerator LogRunCheck()
        {
            yield return new WaitForSecondsRealtime(3f);
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            var gunNames = LocalGunNames();
            string runGuns = Active == null ? "" : string.Join(", ", Active.Guns.Select(g => g >= 0 && g < gunNames.Count ? gunNames[g] : "?"));
            var sb = new StringBuilder($"Run check: scene='{SceneManager.GetActiveScene().name}' seed={SeededRNG.GetSeed()} (expected {Active?.Seed}) gun='{Active?.Gun}' ammo guns=[{runGuns}] tiles=");
            if (level != null && level.tiles != null)
                sb.Append(string.Join(", ", level.tiles
                    .OrderBy(t => t.tile_position)
                    .Select(t => $"{t.tile_position}:{(t.tile_object != null ? t.tile_object.name.Replace("(Clone)", "") : "-")}")));
            else sb.Append("none");
            sb.Append(" | start kit: ").Append(StartingKit(World.LocalPlayer.Get()));
            Plugin.Log.LogInfo(sb.ToString());
        }

        static string StartingKit(AimScript me)
        {
            if (me == null) return "-";
            var parts = new List<string> { $"{me.loose_bullets?.Count ?? 0} loose rounds" };
            var gun = World.GearSync.GunInstance(me);
            if (gun != null)
                foreach (var m in gun.GetComponentsInChildren<mag_script>(true))
                    parts.Add($"{m.NumRounds()}/{m.kMaxRounds} in the gun's magazine");
            var slots = World.GearSync.Slots(me);
            if (slots != null)
                foreach (var s in slots)
                {
                    var m = s != null && s.obj != null ? s.obj.GetComponent<mag_script>() : null;
                    if (m != null) parts.Add($"spare magazine {m.NumRounds()}/{m.kMaxRounds}");
                }
            return string.Join(", ", parts);
        }

        public static void Clear()
        {
            Active = null;
        }
    }
}
