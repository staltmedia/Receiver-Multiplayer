using System;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// The run's shared clock: game seconds (scaled time, so slow motion slows it) since the host's level began. The
    /// host sends its clock every second; a client adds half its round trip and keeps a smoothed offset to its own.
    /// Things the level moves on a timer of their own (add-on elevators) are set from it, so they're in step for
    /// everyone instead of each starting whenever that part of the level loaded on each machine.
    /// </summary>
    static class RunClock
    {
        const float SendEvery = 1f, SnapBeyond = 0.25f;

        static double _local, _offset;
        static bool _synced;
        static float _nextSend;
        static readonly NetWriter _w = new NetWriter(16);

        public static bool Ready => WorldSync.IsHost || _synced;
        public static double Now => WorldSync.IsHost ? _local : _local + _offset;

        public static void Reset()
        {
            _local = 0;
            _offset = 0;
            _synced = false;
            _nextSend = 0f;
        }

        /// <summary>Every frame in a run.</summary>
        public static void Frame(NetSession s)
        {
            _local += Time.deltaTime;
            if (!s.IsHost || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + SendEvery;
            var w = WorldSync.Begin(_w, Msg.Clock);
            w.U32((uint)Math.Min(uint.MaxValue, _local * 1000.0));
            s.SendToClients(w, Channel.Unreliable);
        }

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            double host = rd.U32() / 1000.0;
            rd.End();
            double estimate = host + Math.Max(0f, s.RoundTrip) * 0.5 * Time.timeScale;
            double offset = estimate - _local;
            if (!_synced || Math.Abs(offset - _offset) > SnapBeyond) _offset = offset;
            else _offset += (offset - _offset) * 0.1;
            _synced = true;
        }
    }

    /// <summary>Puts a level Animator (an elevator, its floor indicator) where the shared clock says it is.</summary>
    sealed class ClockedAnimator : MonoBehaviour
    {
        const float DriftLimit = 0.25f, CheckEvery = 5f;

        public Animator Anim;
        public float Period;

        bool _pending = true;
        double _start, _played;
        float _nextCheck;

        void OnEnable() => _pending = true; // the game's Animator starts over whenever its tile comes back

        void Update()
        {
            if (Anim == null || !WorldSync.InRun) return;
            if (!_pending)
            {
                _played += Time.deltaTime * Anim.speed;
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + CheckEvery;
                    if (Math.Abs(RunClock.Now - _start - _played) > DriftLimit) _pending = true;
                }
            }
            if (_pending && RunClock.Ready) Align();
        }

        void Align()
        {
            _pending = false;
            double now = RunClock.Now, t;
            try
            {
                var timeline = AnimatorClock.TimelineFor(Anim);
                if (timeline == null)
                {
                    // Doesn't repeat (plays once, or waits for something): left to the game.
                    Anim.Rebind();
                    Anim.Update(0f);
                    enabled = false;
                    return;
                }
                Period = timeline.Period;
                t = now % Period;
                if (t < 0) t += Period;
                timeline.Apply(Anim, (float)t);
            }
            catch (Exception e)
            {
                SyncStats.Note("level animation: align failed", $"couldn't set '{name}' to the shared clock: {e.Message}");
                enabled = false;
                return;
            }
            _start = now - t;
            _played = t;
            _nextCheck = Time.unscaledTime + CheckEvery;
        }
    }

    /// <summary>Finds the level's own Animators (in tiles, not enemies or projectiles) and puts them on the shared clock.</summary>
    static class AnimatorClock
    {
        const float MaxPeriod = 600f;
        const float TimelineStep = 1f / 60f, MaxTimelinePeriod = 120f;

        static readonly HashSet<int> _scanned = new HashSet<int>();
        static readonly Dictionary<RuntimeAnimatorController, Timeline> _timelines = new Dictionary<RuntimeAnimatorController, Timeline>();
        static float _next;

        public static void Reset()
        {
            _scanned.Clear();
            _timelines.Clear();
        }

        /// <summary>
        /// Which state an Animator of this controller is in, and how far through it, at every moment of one cycle from
        /// its start: played once in frame-sized steps, the way it runs in the game. Lining an Animator up is then one
        /// jump to the right state and time. (Stepping it forward in big chunks lost up to a chunk at every change of
        /// state, so an elevator realigned mid-run ran half a second behind.)
        /// </summary>
        public sealed class Timeline
        {
            public int[] Hash;
            public float[] Norm, Length;
            public float Period; // measured: when it starts its first state again (blends make it longer than its clips)

            public void Apply(Animator a, float t)
            {
                int i = Mathf.Clamp((int)(t / TimelineStep), 0, Hash.Length - 1);
                float extra = t - i * TimelineStep;
                float norm = Norm[i] + (Length[i] > 1e-4f ? extra / Length[i] : 0f);
                a.Rebind();
                a.Play(Hash[i], 0, norm);
                a.Update(0f);
            }
        }

        /// <summary>
        /// One cycle of this controller, played from its start. The cycle ends when it begins its first state again
        /// (a transition into it, or that state starting over); null if it never does within the limit (a one-off).
        /// </summary>
        public static Timeline TimelineFor(Animator a)
        {
            var ctl = a.runtimeAnimatorController;
            if (ctl == null) return null;
            if (_timelines.TryGetValue(ctl, out var known)) return known;
            int max = Mathf.CeilToInt(MaxTimelinePeriod / TimelineStep);
            var hash = new List<int>();
            var norm = new List<float>();
            var length = new List<float>();
            a.Rebind();
            a.Update(0f);
            int first = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
            float lastNorm = 0f;
            bool left = false, cycled = false;
            for (int i = 0; i < max; i++)
            {
                var info = a.GetCurrentAnimatorStateInfo(0);
                bool intoFirst = a.IsInTransition(0) && a.GetNextAnimatorStateInfo(0).fullPathHash == first;
                bool restarted = info.fullPathHash == first && info.normalizedTime < lastNorm - 0.5f;
                if (i > 0 && (intoFirst || restarted || (left && info.fullPathHash == first))) { cycled = true; break; }
                if (info.fullPathHash != first) left = true;
                lastNorm = info.fullPathHash == first ? info.normalizedTime : 0f;
                hash.Add(info.fullPathHash);
                norm.Add(info.normalizedTime);
                length.Add(info.length);
                a.Update(TimelineStep);
            }
            Timeline tl = null;
            if (cycled && hash.Count > 1)
                tl = new Timeline { Hash = hash.ToArray(), Norm = norm.ToArray(), Length = length.ToArray(), Period = hash.Count * TimelineStep };
            _timelines[ctl] = tl;
            if (tl != null) Plugin.Log.LogInfo($"Level animation '{ctl.name}' repeats every {tl.Period:0.00} s; kept in step for everyone.");
            return tl;
        }

        public static void Scan()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            if (level == null || level.tiles == null) return;
            foreach (var tile in level.tiles)
            {
                var root = tile?.tile_object;
                if (root == null || !_scanned.Add(root.GetInstanceID())) continue;
                foreach (var a in root.GetComponentsInChildren<Animator>(true))
                {
                    if (a == null || a.GetComponent<ClockedAnimator>() != null) continue;
                    if (a.GetComponentInParent<RobotScript>() != null || a.GetComponentInParent<ProjectileScript>() != null) continue;
                    var ctl = a.runtimeAnimatorController;
                    if (ctl == null) continue;
                    float period = 0f;
                    foreach (var clip in ctl.animationClips) if (clip != null) period += clip.length;
                    if (period < 0.1f || period > MaxPeriod) continue;
                    var c = a.gameObject.AddComponent<ClockedAnimator>();
                    c.Anim = a;
                    c.Period = period;
                    SyncStats.Count("level animations on the shared clock");
                }
            }
        }
    }

    /// <summary>
    /// Tiles may pick one of several prop groups with the game's unseeded random (PropGroupRandomizer); in a session
    /// that pick is seeded from the run and the group's position, so every player gets the same props.
    /// </summary>
    [HarmonyPatch(typeof(PropGroupRandomizer), "Start")]
    static class SeededPropGroups
    {
        static void Prefix(PropGroupRandomizer __instance, out Random.State? __state)
        {
            __state = null;
            var run = RunStarter.Active;
            if (!WorldSync.InRun || run == null) return;
            __state = Random.state;
            var p = __instance.transform.position;
            unchecked
            {
                uint h = (uint)run.Seed ^ 0x6A09E667u;
                h = (h ^ (uint)Mathf.RoundToInt(p.x * 100f)) * 0x85EBCA6Bu;
                h = (h ^ (uint)Mathf.RoundToInt(p.y * 100f)) * 0xC2B2AE35u;
                h = (h ^ (uint)Mathf.RoundToInt(p.z * 100f)) * 0x27D4EB2Fu;
                h ^= h >> 15;
                Random.InitState((int)h);
            }
        }

        static Exception Finalizer(Exception __exception, Random.State? __state)
        {
            if (__state.HasValue) Random.state = __state.Value;
            return __exception;
        }
    }
}
