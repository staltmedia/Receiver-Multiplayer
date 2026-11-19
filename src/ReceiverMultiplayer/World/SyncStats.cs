using System.Collections.Generic;
using System.Linq;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Counters for world sync, logged once a minute during a run, plus a one-time note the first time each
    /// kind of problem happens. Lets two players' logs show where something stops arriving.
    /// </summary>
    static class SyncStats
    {
        const float IntervalSeconds = 60f;

        static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        static readonly HashSet<string> _noted = new HashSet<string>();
        static float _time, _next = IntervalSeconds;

        public static void Count(string key, int n = 1)
        {
            _counts.TryGetValue(key, out int c);
            _counts[key] = c + n;
        }

        /// <summary>Log a problem the first time it happens (per kind), and count every time.</summary>
        public static void Note(string key, string detail)
        {
            Count(key);
            if (_noted.Add(key)) Plugin.Log.LogWarning($"Sync: {detail}");
        }

        public static void Frame(float dt)
        {
            _time += dt;
            if (_time < _next) return;
            _next = _time + IntervalSeconds;
            if (_counts.Count == 0) return;
            Plugin.Log.LogInfo("Sync stats (last minute): " + string.Join(", ", _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")));
            _counts.Clear();
        }

        public static void Reset()
        {
            _counts.Clear();
            _noted.Clear();
            _next = _time + IntervalSeconds;
        }
    }
}
