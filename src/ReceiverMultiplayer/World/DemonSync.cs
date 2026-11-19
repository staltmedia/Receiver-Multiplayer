using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// The Green Demon modifier, one demon per player. In single-player, when a level starts the game puts a
    /// spawner 0.8 m in front of the player: a warning light that goes green to orange over 8 s, then the demon
    /// (a drone, RobotScript of type GREEN_DEMON) that hunts the player and kills on touch.
    /// In a session the host runs it for everyone instead: each living player gets their own spawner in front of
    /// them, shown to everyone, and their demon hunts only them (the host runs its AI like every enemy; the others
    /// see a puppet). A player who dies loses their demon (it got them); when revived, a new one starts 0.8 m in
    /// front of them, as a fresh level would. Late joiners get theirs too, and see everyone else's.
    /// </summary>
    static class DemonSync
    {
        const uint IdBase = 0x01000000u;   // above every tile enemy id (those fit in 24 bits)
        const float SettleSeconds = 1.5f;  // alive in the level this long before the warning light starts
        const float ClaimDistance = 0.3f;

        sealed class Demon
        {
            public uint Id;
            public ushort Owner;
            public GameObject Spawner;
            public RobotScript Robot;
        }

        static readonly Dictionary<uint, Demon> _byId = new Dictionary<uint, Demon>();
        static readonly Dictionary<ushort, Demon> _byOwner = new Dictionary<ushort, Demon>();   // host
        static readonly Dictionary<RobotScript, Demon> _byRobot = new Dictionary<RobotScript, Demon>();
        static readonly Dictionary<ushort, float> _aliveSince = new Dictionary<ushort, float>(); // host
        static readonly List<(uint id, Vector3 pos)> _pending = new List<(uint, Vector3)>();
        static readonly List<ushort> _scratch = new List<ushort>();
        static readonly NetWriter _w = new NetWriter(64);
        static bool _enabled;
        static byte _generation;
        static float _time;

        public static void Reset()
        {
            foreach (var d in _byId.Values) DestroyLocal(d);
            _byId.Clear();
            _byOwner.Clear();
            _byRobot.Clear();
            _aliveSince.Clear();
            _pending.Clear();
            _queued.Clear();
            _enabled = false;
            _time = 0f;
        }

        // ---------------- the game's own spawner is replaced in a session ----------------

        /// <summary>
        /// LevelCreatorScript.Start puts one spawner in front of "the" player. In a run that's skipped (the host
        /// makes one per player instead); whether the modifier is on for this level is noted here, as the game
        /// reads it here.
        /// </summary>
        [HarmonyPatch(typeof(LevelCreatorScript), nameof(LevelCreatorScript.Start))]
        static class SkipGameSpawner
        {
            const string Key = "modifier_green_demon";

            static void Prefix(out int __state)
            {
                __state = int.MinValue;
                if (!WorldSync.InRun) return;
                _enabled = PlayerPrefs.GetInt(Key, 0) == 1;
                if (!_enabled) return;
                __state = PlayerPrefs.GetInt(Key, 0);
                PlayerPrefs.SetInt(Key, 0);
            }

            static Exception Finalizer(Exception __exception, int __state)
            {
                if (__state != int.MinValue) PlayerPrefs.SetInt(Key, __state);
                return __exception;
            }
        }

        // ---------------- host ----------------

        public static void HostFrame(NetSession s, float dt)
        {
            _time += dt;
            if (!_enabled) return;
            var me = LocalPlayer.Get();
            if (me == null || SpawnerPrefab() == null) return;

            foreach (var p in s.Players)
            {
                bool alive;
                Vector3 pos;
                Quaternion facing;
                if (p.Id == s.LocalId)
                {
                    alive = LocalPlayer.IsAlive(me);
                    pos = me.transform.position;
                    facing = Quaternion.Euler(0f, me.transform.eulerAngles.y, 0f);
                }
                else
                {
                    alive = PlayerSync.TryGetBody(p.Id, out var body) && PlayerSync.IsAlive(p.Id);
                    pos = body != null ? body.position : default;
                    facing = body != null ? Quaternion.Euler(0f, body.eulerAngles.y, 0f) : Quaternion.identity;
                }

                if (!alive)
                {
                    _aliveSince.Remove(p.Id);
                    if (_byOwner.TryGetValue(p.Id, out var gone)) HostRemove(s, gone, "its player died");
                    continue;
                }
                if (!_aliveSince.TryGetValue(p.Id, out float since)) { _aliveSince[p.Id] = _time; continue; }
                if (_byOwner.ContainsKey(p.Id) || _time - since < SettleSeconds) continue;
                HostSpawn(s, p.Id, pos + facing * Vector3.forward * 0.8f, facing);
            }

            // Players who left take their demon with them.
            _scratch.Clear();
            foreach (var owner in _byOwner.Keys)
                if (!s.Players.Exists(p => p.Id == owner)) _scratch.Add(owner);
            foreach (var owner in _scratch) HostRemove(s, _byOwner[owner], "its player left");
        }

        static void HostSpawn(NetSession s, ushort owner, Vector3 pos, Quaternion rot)
        {
            uint id = IdBase | ((uint)owner << 8) | _generation++;
            var d = CreateLocal(id, owner, pos, rot, live: false);
            if (d == null) return;
            _byOwner[owner] = d;
            var name = s.Players.Find(p => p.Id == owner)?.Name ?? "a player";
            Plugin.Log.LogInfo($"Green Demon for {name}: warning light up, the demon comes in 8 s.");
            Broadcast(s, d, pos, rot, live: false, only: null);
        }

        static void HostRemove(NetSession s, Demon d, string why)
        {
            _byOwner.Remove(d.Owner);
            _byId.Remove(d.Id);
            DestroyLocal(d);
            var w = WorldSync.Begin(_w, Msg.DemonGone);
            w.U32(d.Id);
            s.SendToClients(w, Channel.Reliable);
            Plugin.Log.LogInfo($"Green Demon removed ({why}).");
        }

        /// <summary>Host: a player joined mid-run; show them every demon (and warning light) that exists.</summary>
        public static void HostSendAllTo(NetSession s, ushort id)
        {
            foreach (var d in _byId.Values)
            {
                if (d.Robot != null) Broadcast(s, d, d.Robot.transform.position, d.Robot.transform.rotation, live: true, only: id);
                else if (d.Spawner != null) Broadcast(s, d, d.Spawner.transform.position, d.Spawner.transform.rotation, live: false, only: id);
            }
        }

        static void Broadcast(NetSession s, Demon d, Vector3 pos, Quaternion rot, bool live, ushort? only)
        {
            var w = WorldSync.Begin(_w, Msg.DemonSpawn);
            w.U32(d.Id);
            w.U16(d.Owner);
            Codec.Pos(w, pos);
            w.U16(Codec.Yaw(rot.eulerAngles.y));
            w.Bool(live);
            if (only.HasValue) s.SendToPlayer(only.Value, w, Channel.Reliable);
            else s.SendToClients(w, Channel.Reliable);
        }

        // ---------------- client ----------------

        struct Queued
        {
            public uint Id;
            public ushort Owner;
            public Vector3 Pos;
            public Quaternion Rot;
            public bool Live;
            public float At;
        }

        const float SpawnDelaySeconds = 8f; // GreenDemonSpawner.spawnDelay
        static readonly List<Queued> _queued = new List<Queued>();
        const int MaxDemons = 256;

        public static void ClientReceiveSpawn(ref NetReader rd)
        {
            uint id = rd.U32();
            ushort owner = rd.U16();
            Vector3 pos = Codec.Pos(ref rd);
            float yaw = Codec.Yaw(rd.U16());
            bool live = rd.Bool();
            rd.End();
            if ((id & 0xFF000000u) != IdBase) throw new ProtocolException("bad demon id");
            if (_byId.ContainsKey(id) || _queued.Exists(q => q.Id == id)) return;
            if (_queued.Count + _byId.Count >= MaxDemons) return; // a level has a handful: a host making up ids can't pile them up
            // This level may still be loading here: hold it until the level (and its spawner prefab) exists.
            _queued.Add(new Queued { Id = id, Owner = owner, Pos = pos, Rot = Quaternion.Euler(0f, yaw, 0f), Live = live, At = _time });
            ClientFrame(0f);
        }

        public static void ClientReceiveGone(ref NetReader rd)
        {
            uint id = rd.U32();
            rd.End();
            _queued.RemoveAll(q => q.Id == id);
            if (!_byId.TryGetValue(id, out var d)) return;
            _byId.Remove(id);
            DestroyLocal(d);
        }

        /// <summary>Client, every frame: create demons announced while this level was still loading.</summary>
        public static void ClientFrame(float dt)
        {
            _time += dt;
            if (_queued.Count == 0 || LocalPlayer.Get() == null || SpawnerPrefab() == null) return;
            foreach (var q in _queued)
            {
                // A warning light that already ran out on the host: the demon is out, so make it directly.
                bool live = q.Live || _time - q.At >= SpawnDelaySeconds;
                CreateLocal(q.Id, q.Owner, q.Pos, q.Rot, live);
            }
            _queued.Clear();
        }

        // ---------------- both ----------------

        static GameObject SpawnerPrefab()
        {
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            return level != null ? level.green_demon_spawner : null;
        }

        /// <summary>
        /// The game's own spawner (its warning light and countdown; it then creates the demon itself), or for a
        /// late joiner the demon straight away. Either way the demon is claimed by id when it wakes up.
        /// </summary>
        static Demon CreateLocal(uint id, ushort owner, Vector3 pos, Quaternion rot, bool live)
        {
            var prefab = SpawnerPrefab();
            if (prefab == null) return null;
            var d = new Demon { Id = id, Owner = owner };
            _byId[id] = d;
            _pending.Add((id, pos));
            if (live)
            {
                var demonPrefab = prefab.GetComponent<GreenDemonSpawner>()?.greenDemonPrefab;
                if (demonPrefab != null) Object.Instantiate(demonPrefab, pos, rot);
            }
            else d.Spawner = Object.Instantiate(prefab, pos, rot);
            return d;
        }

        static void DestroyLocal(Demon d)
        {
            if (d.Spawner != null) Object.Destroy(d.Spawner);
            if (d.Robot != null)
            {
                _byRobot.Remove(d.Robot);
                Object.Destroy(d.Robot.gameObject);
            }
            _pending.RemoveAll(p => p.id == d.Id);
        }

        /// <summary>A Green Demon woke up here (RobotScript.Awake): which one is it?</summary>
        public static bool TryClaim(RobotScript robot, out uint id)
        {
            id = 0;
            if (robot == null || robot.robot_type != RobotType.GREEN_DEMON) return false;
            var pos = robot.transform.position;
            int best = -1;
            float bestDist = ClaimDistance * ClaimDistance;
            for (int i = 0; i < _pending.Count; i++)
            {
                float dist = (_pending[i].pos - pos).sqrMagnitude;
                if (dist <= bestDist) { bestDist = dist; best = i; }
            }
            if (best < 0)
            {
                // Not one of ours (the game's own, which a session skips): keep the world to one per player.
                Plugin.Log.LogWarning("A Green Demon appeared that the session didn't create; removed.");
                Object.Destroy(robot.gameObject);
                return false;
            }
            id = _pending[best].id;
            _pending.RemoveAt(best);
            if (_byId.TryGetValue(id, out var d))
            {
                d.Robot = robot;
                _byRobot[robot] = d;
            }
            return true;
        }

        /// <summary>Host: a demon hunts only its own player.</summary>
        public static bool TryGetQuarry(RobotScript robot, out Transform target)
        {
            target = null;
            if (robot == null || !_byRobot.TryGetValue(robot, out var d)) return false;
            var s = NetSession.Current;
            if (s == null) return false;
            if (d.Owner == s.LocalId)
            {
                var me = LocalPlayer.Get();
                target = me != null ? me.transform : null;
            }
            else if (PlayerSync.TryGetBody(d.Owner, out var body)) target = body;
            return target != null;
        }
    }
}
