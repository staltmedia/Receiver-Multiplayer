using System;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.Events;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>A projectile this machine is only replaying (another player's shot) or rebuilt from where it came to rest.</summary>
    sealed class RemoteProjectile : MonoBehaviour
    {
    }

    /// <summary>Which shot a projectile is, the same on every machine.</summary>
    sealed class ProjectileTag : MonoBehaviour
    {
        public uint Id;
        public ushort Gun;
        public float Born;
        public bool RestSent;
    }

    /// <summary>
    /// Guns that fire physics projectiles instead of bullets (the game's ProjectileScript: GG 9000's glue blobs, Nerf
    /// Maverick's suction darts, TP Gun's paper). The game spawns the projectile at the muzzle and gives it an impulse
    /// of <c>exit_velocity</c>; on a hit it can stick where it landed and change form (the glue blob turns into a solid,
    /// climbable lump). Everyone replays the shot with the same physics; the shooter then reports where each
    /// projectile came to rest (stuck to the level, to an enemy or to a player, or settled), and every copy is put
    /// exactly there. Those resting places are kept for the run: a projectile lost with its level tile (or never seen,
    /// by a player who joins later) is rebuilt in place when the tile is there again. Replayed projectiles never
    /// damage enemies (the shooter's own copy reports its hits to the host, like bullets) and hurt players only with
    /// friendly fire on.
    /// </summary>
    static class ProjectileSync
    {
        const uint DynamicBit = 0x80000000u;
        const int MaxRests = 2048;
        const float SettleSeconds = 8f;   // a projectile that never sticks reports where it is after this long
        const float MaxLocalOffset = 5f;  // a pose relative to an enemy or a player must be this close to it

        enum Kind : byte { World = 0, Settled = 1, Enemy = 2, Player = 3, Max = 3 }

        struct Rest
        {
            public uint Id;
            public ushort Gun;
            public Kind Kind;
            public Vector3 Pos;
            public Quaternion Rot;
            public uint Enemy;
            public ushort Player;
            public Vector3 LocalPos;
            public Quaternion LocalRot;
        }

        static readonly Dictionary<uint, Rest> _rests = new Dictionary<uint, Rest>();
        static readonly Dictionary<uint, GameObject> _objects = new Dictionary<uint, GameObject>();
        static readonly List<ProjectileTag> _local = new List<ProjectileTag>();
        static readonly NetWriter _w = new NetWriter(512);
        static readonly List<(uint id, Vector3 pos, Quaternion rot)> _incoming = new List<(uint, Vector3, Quaternion)>();
        static ushort _counter;
        static float _time, _nextCheck;

        static readonly AccessTools.FieldRef<ProjectileScript, UnityEvent> OnCollision = AccessTools.FieldRefAccess<ProjectileScript, UnityEvent>("on_collision");

        public static void Reset()
        {
            _rests.Clear();
            _objects.Clear();
            _local.Clear();
        }

        static uint NewId()
        {
            ushort owner = NetSession.Current?.LocalId ?? 0;
            return DynamicBit | ((uint)(owner & 0x7FFF) << 16) | _counter++;
        }

        static bool Mine(uint id, ushort player) => (id & DynamicBit) != 0 && ((id >> 16) & 0x7FFF) == (uint)(player & 0x7FFF);

        // ================= the shooter =================

        /// <summary>Our gun just fired these projectiles: tag them and tell everyone.</summary>
        public static void OnLocalShot(ushort gun, Vector3 flashPos, Quaternion flashRot, List<ProjectileScript> fired)
        {
            var s = NetSession.Current;
            if (s == null || fired.Count == 0) return;
            int n = Math.Min(fired.Count, Protocol.MaxPellets);
            var w = WorldSync.Begin(_w, Msg.Projectile);
            if (s.IsHost) w.U16(s.LocalId);
            w.U16(gun);
            Codec.Pos(w, flashPos);
            w.U32(Codec.Quat(flashRot));
            w.U8((byte)n);
            for (int i = 0; i < n; i++)
            {
                var p = fired[i];
                var tag = p.gameObject.AddComponent<ProjectileTag>();
                tag.Id = NewId();
                tag.Gun = gun;
                tag.Born = _time;
                _objects[tag.Id] = p.gameObject;
                _local.Add(tag);
                w.U32(tag.Id);
                Codec.Pos(w, p.transform.position);
                w.U32(Codec.Quat(p.transform.rotation));
            }
            SyncStats.Count("projectiles sent");
            if (s.IsHost) s.SendToClients(w, Channel.Reliable);
            else s.SendToHost(w, Channel.Reliable);
        }

        /// <summary>Our own projectile just hit something; if that stuck it in place, say where.</summary>
        static void AfterLocalCollision(ProjectileTag tag, ushort victim)
        {
            if (tag == null || tag.RestSent) return;
            var go = tag.gameObject;
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) return; // it bounced; it reports where it settles later
            var me = LocalPlayer.Get();
            if (me != null && me.main_camera != null && go.transform.IsChildOf(me.main_camera.transform)) { tag.RestSent = true; return; } // hit ourselves
            var rest = new Rest { Id = tag.Id, Gun = tag.Gun, Kind = Kind.World, Pos = go.transform.position, Rot = go.transform.rotation };
            var robot = go.GetComponentInParent<RobotScript>();
            if (robot != null && RobotSync.TryGetId(robot, out uint enemy))
            {
                rest.Kind = Kind.Enemy;
                rest.Enemy = enemy;
                Relative(robot.transform, go.transform, out rest.LocalPos, out rest.LocalRot);
            }
            else if (victim != ushort.MaxValue && PlayerSync.TryGetBody(victim, out var body))
            {
                // The game sticks it to the level where the teammate stood; it belongs on them (where it is on
                // their own screen, stuck to their view).
                go.transform.SetParent(body, true);
                rest.Kind = Kind.Player;
                rest.Player = victim;
                Relative(body, go.transform, out rest.LocalPos, out rest.LocalRot);
            }
            SendRest(rest);
            tag.RestSent = true;
        }

        static void Relative(Transform parent, Transform child, out Vector3 pos, out Quaternion rot)
        {
            pos = parent.InverseTransformPoint(child.position);
            rot = Quaternion.Inverse(parent.rotation) * child.rotation;
        }

        static void SendRest(Rest r)
        {
            var s = NetSession.Current;
            if (s == null) return;
            if (r.Kind != Kind.Player) Store(r);
            var w = WorldSync.Begin(_w, Msg.ProjectileRest);
            WriteRest(w, r);
            SyncStats.Count("projectile rests sent");
            if (s.IsHost) s.SendToClients(w, Channel.Reliable);
            else s.SendToHost(w, Channel.Reliable);
        }

        /// <summary>Every frame in a run: projectiles that never stick report where they settled; lost ones come back.</summary>
        public static void Frame(float dt)
        {
            _time += dt;
            if (_time < _nextCheck) return;
            _nextCheck = _time + 0.5f;
            for (int i = _local.Count - 1; i >= 0; i--)
            {
                var tag = _local[i];
                if (tag == null) { _local.RemoveAt(i); continue; }
                if (tag.RestSent) { _local.RemoveAt(i); continue; }
                var rb = tag.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic) continue; // sticks on its next hit, reported then
                if (!rb.IsSleeping() && _time - tag.Born < SettleSeconds) continue;
                var t = tag.transform;
                SendRest(new Rest { Id = tag.Id, Gun = tag.Gun, Kind = Kind.Settled, Pos = t.position, Rot = t.rotation });
                tag.RestSent = true;
                _local.RemoveAt(i);
            }
            RebuildMissing();
        }

        // ================= wire =================

        static void WriteRest(NetWriter w, Rest r)
        {
            w.U32(r.Id);
            w.U16(r.Gun);
            w.U8((byte)r.Kind);
            Codec.Pos(w, r.Pos);
            w.U32(Codec.Quat(r.Rot));
            if (r.Kind == Kind.Enemy) w.U32(r.Enemy);
            if (r.Kind == Kind.Player) w.U16(r.Player);
            if (r.Kind == Kind.Enemy || r.Kind == Kind.Player)
            {
                Codec.Pos(w, r.LocalPos);
                w.U32(Codec.Quat(r.LocalRot));
            }
        }

        static Rest ReadRest(ref NetReader rd)
        {
            var r = new Rest { Id = rd.U32(), Gun = rd.U16() };
            byte kind = rd.U8();
            if (kind > (byte)Kind.Max) throw new ProtocolException("bad projectile rest");
            r.Kind = (Kind)kind;
            r.Pos = Codec.Pos(ref rd);
            r.Rot = Codec.Quat(rd.U32());
            if (r.Kind == Kind.Enemy) r.Enemy = rd.U32();
            if (r.Kind == Kind.Player) r.Player = rd.U16();
            if (r.Kind == Kind.Enemy || r.Kind == Kind.Player)
            {
                r.LocalPos = Codec.Pos(ref rd);
                r.LocalRot = Codec.Quat(rd.U32());
                if (r.LocalPos.sqrMagnitude > MaxLocalOffset * MaxLocalOffset) r.LocalPos = r.LocalPos.normalized * MaxLocalOffset;
            }
            rd.End();
            if ((r.Id & DynamicBit) == 0) throw new ProtocolException("bad projectile id");
            return r;
        }

        static void ReadShot(ref NetReader rd, out ushort gun, out Vector3 flashPos, out Quaternion flashRot)
        {
            gun = rd.U16();
            flashPos = Codec.Pos(ref rd);
            flashRot = Codec.Quat(rd.U32());
            int n = rd.U8();
            if (n == 0 || n > Protocol.MaxPellets) throw new ProtocolException("bad projectile count");
            _incoming.Clear();
            for (int i = 0; i < n; i++)
            {
                uint id = rd.U32();
                if ((id & DynamicBit) == 0) throw new ProtocolException("bad projectile id");
                _incoming.Add((id, Codec.Pos(ref rd), Codec.Quat(rd.U32())));
            }
            rd.End();
        }

        static void WriteShot(NetWriter w, ushort gun, Vector3 flashPos, Quaternion flashRot)
        {
            w.U16(gun);
            Codec.Pos(w, flashPos);
            w.U32(Codec.Quat(flashRot));
            w.U8((byte)_incoming.Count);
            foreach (var (id, pos, rot) in _incoming)
            {
                w.U32(id);
                Codec.Pos(w, pos);
                w.U32(Codec.Quat(rot));
            }
        }

        // ================= host =================

        public static void HostReceiveShot(NetSession s, ushort from, ref NetReader rd)
        {
            ReadShot(ref rd, out ushort gun, out var flashPos, out var flashRot);
            if (!Valid(gun, "projectile")) return;
            if (!PlayerSync.TryGetPosition(from, out var body) || (flashPos - body).sqrMagnitude > Protocol.MaxShotOffset * Protocol.MaxShotOffset)
            {
                SyncStats.Note("projectile: far from shooter", $"a projectile shot started {(flashPos - body).magnitude:0.0} m from the shooter and was ignored");
                return;
            }
            foreach (var p in _incoming)
            {
                if (!Mine(p.id, from)) throw new ProtocolException("projectile id not yours");
                if ((p.pos - flashPos).sqrMagnitude > 4f) { SyncStats.Note("projectile: far from muzzle", "a projectile started far from its muzzle and was ignored"); return; }
            }
            Replay(from, gun, flashPos, flashRot);
            var w = WorldSync.Begin(_w, Msg.Projectile);
            w.U16(from);
            WriteShot(w, gun, flashPos, flashRot);
            s.SendToClients(w, Channel.Reliable, from);
        }

        public static void HostReceiveRest(NetSession s, ushort from, ref NetReader rd)
        {
            var r = ReadRest(ref rd);
            if (!Mine(r.Id, from)) throw new ProtocolException("projectile id not yours");
            if (!Valid(r.Gun, "projectile rest")) return;
            if (r.Kind != Kind.Player && !_rests.ContainsKey(r.Id) && _rests.Count >= MaxRests)
            {
                SyncStats.Note("projectile: too many", $"more than {MaxRests} projectiles have come to rest this run; later ones aren't kept");
                return;
            }
            OnRest(r);
            var w = WorldSync.Begin(_w, Msg.ProjectileRest);
            WriteRest(w, r);
            s.SendToClients(w, Channel.Reliable, from);
        }

        /// <summary>Host: someone joined mid-run; tell them where every projectile so far came to rest.</summary>
        public static void HostSendRestsTo(NetSession s, ushort playerId)
        {
            foreach (var r in _rests.Values)
            {
                var w = WorldSync.Begin(new NetWriter(96), Msg.ProjectileRest);
                WriteRest(w, r);
                s.SendToPlayer(playerId, w, Channel.Reliable);
            }
        }

        static bool Valid(ushort gun, string what)
        {
            if (gun < GunCache.Count) return true;
            SyncStats.Note(what + ": gun index", $"a {what} named gun {gun}, but only {GunCache.Count} guns are known here");
            return false;
        }

        // ================= client =================

        public static void ClientReceiveShot(ref NetReader rd)
        {
            ushort shooter = rd.U16();
            ReadShot(ref rd, out ushort gun, out var flashPos, out var flashRot);
            if (shooter == NetSession.Current?.LocalId) return;
            Replay(shooter, gun, flashPos, flashRot);
        }

        public static void ClientReceiveRest(ref NetReader rd) => OnRest(ReadRest(ref rd));

        // ================= everyone =================

        static void Replay(ushort shooter, ushort gun, Vector3 flashPos, Quaternion flashRot)
        {
            SyncStats.Count("projectiles received");
            if (LocalPlayer.Get() == null) return; // our level isn't up yet; where they come to rest still arrives
            var fc = GunCache.Get(gun);
            if (fc == null || fc.bullet_obj == null || fc.bullet_obj.GetComponent<ProjectileScript>() == null)
            {
                SyncStats.Note("projectile: no gun data", $"no projectile data for gun {gun} here");
                return;
            }
            ShotSync.PlayDischarge(fc, flashPos, flashRot);
            Collider shooterBody = PlayerSync.TryGetBody(shooter, out var body) ? body.GetComponent<Collider>() : null;
            foreach (var (id, pos, rot) in _incoming)
            {
                if (_objects.TryGetValue(id, out var existing) && existing != null) continue;
                var go = Object.Instantiate(fc.bullet_obj, pos, rot);
                go.AddComponent<RemoteProjectile>();
                var tag = go.AddComponent<ProjectileTag>();
                tag.Id = id;
                tag.Gun = gun;
                tag.Born = _time;
                tag.RestSent = true;
                _objects[id] = go;
                if (shooterBody != null)
                    foreach (var c in go.GetComponentsInChildren<Collider>(true)) Physics.IgnoreCollision(c, shooterBody, true);
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) rb.AddForce(go.transform.forward * fc.exit_velocity, ForceMode.Impulse); // as the game fires it
                if (_rests.TryGetValue(id, out var rest)) Place(go, rest); // it already landed for the shooter
                SyncStats.Count("projectiles drawn");
            }
        }

        static void Store(Rest r)
        {
            if (r.Kind == Kind.Player) return; // follows that player; nothing to rebuild
            if (_rests.ContainsKey(r.Id) || _rests.Count < MaxRests) _rests[r.Id] = r;
        }

        static void OnRest(Rest r)
        {
            Store(r);
            if (_objects.TryGetValue(r.Id, out var go) && go != null) Place(go, r);
            else if (r.Kind != Kind.Player) TryRebuild(r);
        }

        /// <summary>Put a projectile where it came to rest for the shooter, in its stuck form if it stuck.</summary>
        static void Place(GameObject go, Rest r)
        {
            var me = LocalPlayer.Get();
            // It hit us here: it stays stuck to our view, as the game does, rather than where it hit our body there.
            if (me != null && me.main_camera != null && go.transform.IsChildOf(me.main_camera.transform)) return;
            var rb = go.GetComponent<Rigidbody>();
            if (r.Kind == Kind.Settled)
            {
                if (rb != null && !rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                go.transform.SetPositionAndRotation(r.Pos, r.Rot);
                if (rb != null) rb.Sleep();
                return;
            }
            bool flying = rb != null && !rb.isKinematic;
            if (flying)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
            }
            Transform parent = null;
            if (r.Kind == Kind.Enemy) parent = RobotSync.Find(r.Enemy)?.transform;
            else if (r.Kind == Kind.Player && PlayerSync.TryGetBody(r.Player, out var body)) parent = body;
            if (r.Kind == Kind.Player && me != null && me.main_camera != null && r.Player == NetSession.Current?.LocalId)
            {
                // It stuck to us on the shooter's screen but missed us here: on us it goes, stuck to our view as the
                // game sticks things that hit you.
                go.transform.position = me.transform.TransformPoint(r.LocalPos);
                go.transform.rotation = me.transform.rotation * r.LocalRot;
                go.transform.SetParent(me.main_camera.transform, true);
            }
            else if (parent != null)
            {
                go.transform.SetParent(parent, false);
                go.transform.localPosition = r.LocalPos;
                go.transform.localRotation = r.LocalRot;
            }
            else
            {
                var level = Object.FindObjectOfType<LevelCreatorScript>();
                var tileParent = level != null ? level.GetPositionTileDecalsParent(r.Pos) : null;
                go.transform.SetParent(tileParent, true);
                go.transform.SetPositionAndRotation(r.Pos, r.Rot);
            }
            // Still flying here (or just rebuilt): turn it into what it became on the shooter's hit (the glue blob's
            // solid lump, the dart's stuck state) by running the projectile's own on-hit actions.
            if (flying)
            {
                var ps = go.GetComponent<ProjectileScript>();
                var ev = ps != null ? OnCollision(ps) : null;
                try { ev?.Invoke(); }
                catch (Exception e) { SyncStats.Note("projectile: on-hit failed", "a projectile's on-hit actions failed: " + e.Message); }
            }
        }

        static void RebuildMissing()
        {
            if (_rests.Count == 0 || LocalPlayer.Get() == null) return;
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            foreach (var r in _rests.Values)
                if (!_objects.TryGetValue(r.Id, out var go) || go == null)
                    TryRebuild(r, level);
        }

        static void TryRebuild(Rest r) => TryRebuild(r, Object.FindObjectOfType<LevelCreatorScript>());

        /// <summary>A projectile this machine doesn't have (lost with its tile, or from before we joined): make it, in place.</summary>
        static void TryRebuild(Rest r, LevelCreatorScript level)
        {
            if (LocalPlayer.Get() == null) return;
            if (r.Kind == Kind.Enemy && RobotSync.Find(r.Enemy) == null) return; // that enemy isn't loaded here
            if (level != null && r.Kind != Kind.Enemy)
            {
                var tile = level.GetTileAtPosition(r.Pos);
                if (tile == null || tile.tile_object == null) return; // not built here yet
            }
            var fc = GunCache.Get(r.Gun);
            if (fc == null || fc.bullet_obj == null) return;
            var go = Object.Instantiate(fc.bullet_obj, r.Pos, r.Rot);
            go.AddComponent<RemoteProjectile>();
            var tag = go.AddComponent<ProjectileTag>();
            tag.Id = r.Id;
            tag.Gun = r.Gun;
            tag.RestSent = true;
            _objects[r.Id] = go;
            Place(go, r);
            SyncStats.Count("projectiles rebuilt in place");
        }

        // ================= hits =================

        /// <summary>The projectile whose collision the game is handling right now (so hit handlers know whose it is).</summary>
        public static ProjectileScript Current { get; private set; }

        public static bool IsReplayed(ProjectileScript p) => p != null && p.GetComponent<RemoteProjectile>() != null;

        [HarmonyPatch(typeof(ProjectileScript), "OnCollisionEnter")]
        static class CollisionPatch
        {
            static void Prefix(ProjectileScript __instance, Collision col, out ushort __state)
            {
                Current = __instance;
                __state = ushort.MaxValue;
                if (!WorldSync.InRun || col == null || col.gameObject == null) return;
                var body = col.gameObject.GetComponentInParent<RemotePlayerBody>();
                if (body != null) __state = body.PlayerId;
            }

            static Exception Finalizer(Exception __exception, ProjectileScript __instance, ushort __state)
            {
                Current = null;
                if (__exception != null || __instance == null || !WorldSync.InRun || IsReplayed(__instance)) return __exception;
                try { AfterLocalCollision(__instance.GetComponent<ProjectileTag>(), __state); }
                catch (Exception e) { SyncStats.Note("projectile: rest failed", "couldn't report where a projectile stuck: " + e.Message); }
                return null;
            }
        }
    }
}
