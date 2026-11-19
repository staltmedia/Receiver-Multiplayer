using System;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Compact, bounds-checked encodings for poses. Values are made valid where they're written (a broken number
    /// becomes 0, an absurd one is clamped), and anything odd that still arrives is clamped where it's read, never
    /// a reason to disconnect: only a malformed message is a protocol error. (A position or speed out of range
    /// used to end the session, and a player whose dead body had fallen into the void, or an enemy that fell off
    /// the level, could get a teammate thrown out mid-run.)
    /// </summary>
    static class Codec
    {
        const float PosLimit = Protocol.MaxWorldCoord * 0.99f, SpeedLimit = Protocol.MaxBulletSpeed * 0.99f;

        static float Clean(float v, float limit) => float.IsNaN(v) || float.IsInfinity(v) ? 0f : Mathf.Clamp(v, -limit, limit);

        public static void Pos(NetWriter w, Vector3 p)
        {
            w.F32(Clean(p.x, PosLimit));
            w.F32(Clean(p.y, PosLimit));
            w.F32(Clean(p.z, PosLimit));
        }

        /// <summary>A velocity: finite and no faster than the fastest bullet.</summary>
        public static void Vel(NetWriter w, Vector3 v)
        {
            v = new Vector3(Clean(v.x, SpeedLimit), Clean(v.y, SpeedLimit), Clean(v.z, SpeedLimit));
            Pos(w, Vector3.ClampMagnitude(v, SpeedLimit));
        }

        static float Raw(ref NetReader rd) => new FloatBits { U = rd.U32() }.F;

        /// <summary>Reads a position: a broken number reads as 0 and one beyond ±100 km is clamped (both noted once).</summary>
        public static Vector3 Pos(ref NetReader rd)
        {
            float x = Raw(ref rd), y = Raw(ref rd), z = Raw(ref rd);
            if (Ok(x) && Ok(y) && Ok(z)) return new Vector3(x, y, z);
            SyncStats.Note("bad value: position", "a position arrived broken or beyond ±100 km; it was clamped instead of ending the session");
            return new Vector3(Clean(x, Protocol.MaxWorldCoord), Clean(y, Protocol.MaxWorldCoord), Clean(z, Protocol.MaxWorldCoord));
        }

        static bool Ok(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Mathf.Abs(v) <= Protocol.MaxWorldCoord;

        public static Vector3 Velocity(ref NetReader rd)
        {
            float x = Raw(ref rd), y = Raw(ref rd), z = Raw(ref rd);
            bool finite = !float.IsNaN(x) && !float.IsInfinity(x) && !float.IsNaN(y) && !float.IsInfinity(y) && !float.IsNaN(z) && !float.IsInfinity(z);
            var v = finite ? new Vector3(x, y, z) : Vector3.zero;
            if (finite && v.sqrMagnitude <= Protocol.MaxBulletSpeed * Protocol.MaxBulletSpeed) return v;
            SyncStats.Note("bad value: velocity", "a velocity arrived broken or faster than 2000 m/s; it was clamped instead of ending the session");
            return Vector3.ClampMagnitude(v, Protocol.MaxBulletSpeed);
        }

        /// <summary>A plain number from the network: a broken one reads as <paramref name="fallback"/>.</summary>
        public static float Number(ref NetReader rd, float fallback)
        {
            float f = Raw(ref rd);
            return float.IsNaN(f) || float.IsInfinity(f) ? fallback : f;
        }

        public static ushort Yaw(float degrees) => (ushort)Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 360f * 65535f);
        public static float Yaw(ushort v) => v / 65535f * 360f;

        public static short Pitch(float degrees)
        {
            float d = Mathf.DeltaAngle(0f, degrees); // -180..180
            return (short)Mathf.RoundToInt(Mathf.Clamp(d, -180f, 180f) / 180f * 32767f);
        }

        public static float Pitch(short v) => v / 32767f * 180f;

        /// <summary>Smallest-three quaternion in 32 bits (2-bit index, three 10-bit components).</summary>
        public static uint Quat(Quaternion q)
        {
            float x = q.x, y = q.y, z = q.z, w = q.w;
            float mag = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (mag < 1e-6f) return 3u << 30 | Q(0f) << 20 | Q(0f) << 10 | Q(0f); // identity
            x /= mag; y /= mag; z /= mag; w /= mag;
            float ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z), aw = Math.Abs(w);
            uint index = 0;
            float max = ax;
            if (ay > max) { index = 1; max = ay; }
            if (az > max) { index = 2; max = az; }
            if (aw > max) { index = 3; }
            float[] c = { x, y, z, w };
            if (c[index] < 0f) { x = -x; y = -y; z = -z; w = -w; c = new[] { x, y, z, w }; }
            uint packed = index << 30;
            int shift = 20;
            for (int i = 0; i < 4; i++)
            {
                if (i == index) continue;
                packed |= Q(c[i]) << shift;
                shift -= 10;
            }
            return packed;
        }

        public static Quaternion Quat(uint packed)
        {
            uint index = packed >> 30;
            float a = D((packed >> 20) & 1023u), b = D((packed >> 10) & 1023u), c = D(packed & 1023u);
            float big = Mathf.Sqrt(Mathf.Max(0f, 1f - a * a - b * b - c * c));
            switch (index)
            {
                case 0: return new Quaternion(big, a, b, c);
                case 1: return new Quaternion(a, big, b, c);
                case 2: return new Quaternion(a, b, big, c);
                default: return new Quaternion(a, b, c, big);
            }
        }

        const float Range = 0.70710678f; // the three smallest components lie in ±1/√2

        static uint Q(float v) => (uint)Mathf.RoundToInt((Mathf.Clamp(v, -Range, Range) + Range) / (2f * Range) * 1023f);
        static float D(uint v) => v / 1023f * (2f * Range) - Range;

        public static byte UnitByte(float v, float max) => (byte)Mathf.RoundToInt(Mathf.Clamp01(v / max) * 255f);
        public static float UnitByte(byte v, float max) => v / 255f * max;
    }

    /// <summary>Unwraps a 16-bit tick counter into a monotonic one; rejects stale or duplicate samples.</summary>
    struct TickUnwrapper
    {
        bool _has;
        ushort _raw;
        long _value;

        /// <returns>The unwrapped tick, or -1 when the sample is not newer than the last one.</returns>
        public long Next(ushort raw)
        {
            if (!_has)
            {
                _has = true;
                _raw = raw;
                _value = raw;
                return _value;
            }
            short delta = unchecked((short)(raw - _raw));
            if (delta <= 0) return -1;
            _raw = raw;
            _value += delta;
            return _value;
        }

        public void Reset() => _has = false;
    }

    struct PoseSample
    {
        public long Tick;
        public Vector3 Pos;
        public Quaternion R0, R1, R2;
        public float F0;
    }

    /// <summary>
    /// Snapshot interpolation: renders a remote entity about 100 ms in the past, between the two samples
    /// around the render time. Holds the newest sample when updates stop (no extrapolation).
    /// </summary>
    sealed class PoseBuffer
    {
        const int Capacity = 24;
        const double DelayTicks = 3.0;   // 150 ms at 20 Hz: room for jitter on the host-relayed (two-hop) path
        const double SnapTicks = 10.0;
        const double MaxExtrapolateTicks = 2.0; // a late packet: keep moving for up to 100 ms instead of freezing

        readonly PoseSample[] _s = new PoseSample[Capacity];
        int _count;
        double _render;
        bool _started;
        public TickUnwrapper Unwrap;

        public bool HasData => _count > 0;
        public PoseSample Newest => _s[_count - 1];

        /// <summary>The sender tick the last <see cref="Sample"/> rendered (fractional).</summary>
        public double RenderTick => _render;

        public void Clear()
        {
            _count = 0;
            _started = false;
        }

        public void Add(PoseSample sample)
        {
            if (_count > 0 && sample.Tick <= _s[_count - 1].Tick) return;
            if (_count == Capacity)
            {
                Array.Copy(_s, 1, _s, 0, Capacity - 1);
                _count--;
            }
            _s[_count++] = sample;
        }

        /// <summary>Velocity from the two newest samples (m/s).</summary>
        public Vector3 Velocity
        {
            get
            {
                if (_count < 2) return Vector3.zero;
                var a = _s[_count - 2];
                var b = _s[_count - 1];
                float dt = (b.Tick - a.Tick) * Protocol.WorldTick;
                return dt > 0f ? (b.Pos - a.Pos) / dt : Vector3.zero;
            }
        }

        public bool Sample(float dt, out PoseSample pose)
        {
            pose = default;
            if (_count == 0) return false;
            double newest = _s[_count - 1].Tick;
            double target = newest - DelayTicks;
            if (!_started || Math.Abs(_render - target) > SnapTicks)
            {
                _render = target;
                _started = true;
            }
            else
            {
                _render += dt / Protocol.WorldTick;
                _render += (target - _render) * Math.Min(1.0, dt * 1.0); // gentle: no visible speed-ups
            }

            if (_count == 1) { pose = _s[0]; return true; }
            if (_render >= newest)
            {
                pose = _s[_count - 1];
                float ahead = (float)Math.Min(_render - newest, MaxExtrapolateTicks) * Protocol.WorldTick;
                if (ahead > 0f) pose.Pos += Velocity * ahead;
                return true;
            }
            if (_render <= _s[0].Tick) { pose = _s[0]; return true; }
            for (int i = _count - 2; i >= 0; i--)
            {
                if (_s[i].Tick > _render) continue;
                var a = _s[i];
                var b = _s[i + 1];
                float t = (float)((_render - a.Tick) / (b.Tick - a.Tick));
                pose = new PoseSample
                {
                    Tick = a.Tick,
                    Pos = Vector3.Lerp(a.Pos, b.Pos, t),
                    R0 = Quaternion.Slerp(a.R0, b.R0, t),
                    R1 = Quaternion.Slerp(a.R1, b.R1, t),
                    R2 = Quaternion.Slerp(a.R2, b.R2, t),
                    F0 = Mathf.Lerp(a.F0, b.F0, t),
                };
                return true;
            }
            pose = _s[_count - 1];
            return true;
        }
    }
}
