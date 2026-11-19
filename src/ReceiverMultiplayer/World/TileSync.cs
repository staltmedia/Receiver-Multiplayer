using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// The level is a line of 20 m tiles generated around the camera as you move. In single-player every tile
    /// draws from one shared random stream, so a tile's contents depend on the order tiles were built, which
    /// differs per player. In a shared run each tile gets its own stream seeded from the run seed and the tile's
    /// position, so every player builds identical tiles wherever they go, in any order.
    /// The host also keeps tiles loaded around every player, so it can run the enemies near each of them.
    /// </summary>
    static class TileSync
    {
        public static int TileSeed(int runSeed, int tile)
        {
            unchecked
            {
                uint h = (uint)runSeed;
                h ^= (uint)tile * 0x9E3779B1u;
                h = (h ^ (h >> 16)) * 0x85EBCA6Bu;
                h = (h ^ (h >> 13)) * 0xC2B2AE35u;
                h ^= h >> 16;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        [HarmonyPatch]
        static class PerTileRandom
        {
            static readonly Type Coroutine = AccessTools.FirstInner(typeof(LevelCreatorScript), t => t.Name.StartsWith("<SpawnTile>"));
            static readonly AccessTools.FieldRef<object, int> Where = AccessTools.FieldRefAccess<int>(Coroutine, "where_cs1");
            static readonly FieldInfo Rng = AccessTools.Field(typeof(SeededRNG), "rng");
            static readonly ConditionalWeakTable<object, System.Random> Streams = new ConditionalWeakTable<object, System.Random>();

            static MethodBase TargetMethod() => AccessTools.Method(Coroutine, "MoveNext");

            static void Prefix(object __instance, out System.Random __state)
            {
                __state = null;
                var run = RunStarter.Active;
                if (!WorldSync.InRun || run == null) return;
                var stream = Streams.GetValue(__instance, c => new System.Random(TileSeed(run.Seed, Where(c))));
                __state = (System.Random)Rng.GetValue(null);
                Rng.SetValue(null, stream);
            }

            static Exception Finalizer(Exception __exception, System.Random __state)
            {
                if (__state != null) Rng.SetValue(null, __state);
                return __exception;
            }
        }

        /// <summary>
        /// Host: the game's LevelCreatorScript.Update, with "distance to the camera" replaced by "distance to the
        /// nearest player". Tiles within 3 of any player stay enabled, enemies within 2, tiles beyond 7 are deleted.
        /// </summary>
        [HarmonyPatch(typeof(LevelCreatorScript), nameof(LevelCreatorScript.Update))]
        static class HostLoadsAroundEveryone
        {
            static readonly List<int> Centers = new List<int>();

            static int TileOf(float z) => (int)(z / 20f + 0.5f); // same rounding as the game

            static readonly Dictionary<ushort, (int tile, float at)> Followed = new Dictionary<ushort, (int, float)>();
            static LevelCreatorScript _level;
            const float StepSeconds = 0.25f;

            /// <summary>
            /// The tile the host builds around for this player. It follows them one tile at a time (four a second,
            /// faster than anyone runs), so a player reporting far-flung places every tick makes the host build a
            /// tile now and then, not seven at every report. A first sighting (a join, a new level) is taken as is.
            /// </summary>
            static int Follow(ushort id, int tile)
            {
                float now = Time.unscaledTime;
                if (!Followed.TryGetValue(id, out var f) || now - f.at > 5f) { Followed[id] = (tile, now); return tile; }
                if (f.tile == tile) { Followed[id] = (tile, now); return tile; }
                if (now - f.at < StepSeconds) return f.tile;
                int next = f.tile + Math.Sign(tile - f.tile);
                Followed[id] = (next, now);
                return next;
            }

            static bool Prefix(LevelCreatorScript __instance)
            {
                if (!WorldSync.IsHost || __instance.tiles == null) return true;
                var cam = Camera.main;
                if (cam == null) return true;

                if (_level != __instance) { _level = __instance; Followed.Clear(); } // a new level: everyone starts afresh
                Centers.Clear();
                Centers.Add(TileOf(cam.transform.position.z));
                foreach (var p in PlayerSync.RemotePositions())
                {
                    int c = Follow(p.Key, TileOf(p.Value.z));
                    if (!Centers.Contains(c)) Centers.Add(c);
                }
                if (Centers.Count == 1) return true; // nobody elsewhere: the game's own code

                var tiles = __instance.tiles;
                for (int i = tiles.Count - 1; i >= 0; i--)
                {
                    var tile = tiles[i];
                    if (tile.locked) continue;
                    int d = int.MaxValue;
                    foreach (int c in Centers) d = Math.Min(d, Math.Abs(c - tile.tile_position));

                    bool wantEnemies = d <= 2;
                    if (tile.enemies_enabled != wantEnemies)
                    {
                        if (tile.enemies_enabled) __instance.DisableEnemies(tile);
                        else __instance.EnableEnemies(tile);
                    }
                    switch (tile.state)
                    {
                        case LevelCreatorScript.TileInstanceState.Disabled:
                            if (d <= 3) __instance.EnableTile(tile);
                            if (d > 7) __instance.DeleteTile(tile);
                            break;
                        case LevelCreatorScript.TileInstanceState.Enabled:
                            if (d > 3) __instance.DisableTile(tile);
                            break;
                    }
                }
                foreach (int c in Centers)
                    for (int o = -3; o <= 3; o++)
                        __instance.CreateTileIfNeeded(c + o, false);

                UpdateShadowedLights(__instance, cam.transform);
                return false;
            }

            /// <summary>The tail of the game's Update: fade dynamic shadows in near the camera.</summary>
            static void UpdateShadowedLights(LevelCreatorScript level, Transform cam)
            {
                if (PlayerPrefs.GetInt("shadowed_lights", 1) != 0 || level.shadowed_lights == null) return;
                foreach (var light in level.shadowed_lights)
                {
                    if (light == null) continue;
                    float dist = Vector3.Distance(cam.position, light.transform.position);
                    float max = Mathf.Min(30f, light.range * 2f);
                    float fade = max * 0.75f;
                    if (dist < max)
                    {
                        light.shadows = LightShadows.Hard;
                        light.shadowStrength = Mathf.Min(1f, 1f - (fade - dist) / (fade - max));
                    }
                    else
                    {
                        light.shadows = LightShadows.None;
                    }
                }
            }
        }
    }
}
