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
    /// <summary>Identifies a world item (round, magazine, flashlight, tape, casing) the same way on every machine.</summary>
    sealed class ItemTag : MonoBehaviour
    {
        public uint Id;
        public int Rounds = -1; // magazines spawned from the network: round count to apply after mag_script.Start
    }

    /// <summary>
    /// Items in the world are the same for everyone:
    /// - Ammo piles roll their contents from the run seed and their position (the game rolls them with an
    ///   unseeded random), stock the ammo of one of the run's guns, and give every item an id from the pile.
    ///   Magazine round counts are rolled the same way.
    /// - Picking an item up removes it for everyone (also for players who load that tile later).
    /// - Dropping or ejecting something (magazine, flashlight, live round, empty casing) creates it for everyone.
    /// </summary>
    static class ItemSync
    {
        const uint DynamicBit = 0x80000000u;
        const int MaxIdsPerMessage = 1024;

        static readonly Dictionary<uint, GameObject> _items = new Dictionary<uint, GameObject>();
        static readonly HashSet<uint> _taken = new HashSet<uint>();
        static readonly Dictionary<uint, Pending> _pending = new Dictionary<uint, Pending>(); // by id: a repeat replaces, never adds
        const int MaxPending = 2048;
        static readonly NetWriter _w = new NetWriter(512);
        static ushort _counter;
        static float _time, _nextPending;

        struct Pending
        {
            public uint Id;
            public int Prefab, Rounds;
            public Vector3 Pos, Vel, AngVel;
            public Quaternion Rot;
            public float At;
        }

        public static void Reset()
        {
            _items.Clear();
            _taken.Clear();
            _pending.Clear();
        }

        static uint Hash(int seed, Vector3 pos, uint salt)
        {
            unchecked
            {
                uint h = (uint)seed ^ 0x51ED270Bu ^ (salt * 0x9E3779B1u);
                h = (h ^ (uint)Mathf.RoundToInt(pos.x * 100f)) * 0x85EBCA6Bu;
                h = (h ^ (uint)Mathf.RoundToInt(pos.y * 100f)) * 0xC2B2AE35u;
                h = (h ^ (uint)Mathf.RoundToInt(pos.z * 100f)) * 0x27D4EB2Fu;
                h ^= h >> 15;
                return h;
            }
        }

        // ================= tagging =================

        static void Tag(GameObject go, uint id, int rounds = -1)
        {
            if (go == null) return;
            if (_taken.Contains(id)) { Object.Destroy(go); return; }
            var tag = go.GetComponent<ItemTag>() ?? go.AddComponent<ItemTag>();
            tag.Id = id;
            tag.Rounds = rounds;
            _items[id] = go;
        }

        public static uint NewDynamicId()
        {
            ushort owner = NetSession.Current?.LocalId ?? 0;
            return DynamicBit | ((uint)(owner & 0x7FFF) << 16) | _counter++;
        }

        /// <summary>The player took this item into their inventory: it's gone for everyone else.</summary>
        public static void OnLocalTake(GameObject go)
        {
            if (go == null) return;
            var tag = go.GetComponent<ItemTag>();
            if (tag == null) return;
            uint id = tag.Id;
            Object.Destroy(tag); // it's ours now; if dropped again it gets a new id
            _items.Remove(id);
            _taken.Add(id);
            var s = NetSession.Current;
            if (s == null) return;
            var w = WorldSync.Begin(_w, Msg.ItemTaken);
            w.U16(1);
            w.U32(id);
            if (s.IsHost) s.SendToClients(w, Channel.Reliable);
            else s.SendToHost(w, Channel.Reliable);
        }

        public static void HostReceiveTaken(NetSession s, ushort from, ref NetReader rd)
        {
            int n = rd.U16();
            if (n != 1) throw new ProtocolException("bad take");
            uint id = rd.U32();
            rd.End();
            if (!_taken.Add(id)) return; // someone else got it first; both keep theirs (rare race)
            RemoveLocal(id);
            var w = WorldSync.Begin(_w, Msg.ItemTaken);
            w.U16(1);
            w.U32(id);
            s.SendToClients(w, Channel.Reliable, from);
        }

        public static void ClientReceiveTaken(ref NetReader rd)
        {
            int n = rd.U16();
            if (n > MaxIdsPerMessage) throw new ProtocolException("too many items");
            for (int i = 0; i < n; i++)
            {
                uint id = rd.U32();
                _taken.Add(id);
                RemoveLocal(id);
            }
            rd.End();
        }

        static void RemoveLocal(uint id)
        {
            if (!_items.TryGetValue(id, out var go)) return;
            _items.Remove(id);
            if (go == null) return;
            var tag = go.GetComponent<ItemTag>();
            if (tag != null && tag.Id == id) Object.Destroy(go); // still a world item here
        }

        /// <summary>Host: a player joined mid-run; tell them everything already taken.</summary>
        public static void HostSendTakenTo(NetSession s, ushort playerId)
        {
            var ids = new List<uint>(_taken);
            for (int start = 0; start < ids.Count; start += MaxIdsPerMessage)
            {
                int n = Math.Min(MaxIdsPerMessage, ids.Count - start);
                var w = WorldSync.Begin(new NetWriter(16 + n * 4), Msg.ItemTaken);
                w.U16((ushort)n);
                for (int i = 0; i < n; i++) w.U32(ids[start + i]);
                s.SendToPlayer(playerId, w, Channel.Reliable);
            }
        }

        // ================= spawning (drops, ejections) =================

        /// <summary>The local player dropped or ejected <paramref name="go"/>: create it for everyone.</summary>
        public static void OnLocalRelease(GameObject go, int prefab, int newMagRounds = -1)
        {
            var s = NetSession.Current;
            if (s == null || go == null || prefab < 0) return;
            uint id = NewDynamicId();
            int rounds = newMagRounds;
            var mag = go.GetComponent<mag_script>();
            if (mag != null && rounds < 0) rounds = mag.NumRounds();
            Tag(go, id, newMagRounds); // a freshly made magazine gets its count after mag_script.Start
            var rb = go.GetComponent<Rigidbody>();
            var w = WorldSync.Begin(_w, Msg.ItemSpawned);
            WriteSpawn(w, id, prefab, rounds, go.transform.position, go.transform.rotation,
                rb != null ? rb.velocity : Vector3.zero, rb != null ? rb.angularVelocity : Vector3.zero);
            if (s.IsHost) s.SendToClients(w, Channel.Reliable);
            else s.SendToHost(w, Channel.Reliable);
        }

        static void WriteSpawn(NetWriter w, uint id, int prefab, int rounds, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 ang)
        {
            w.U32(id);
            w.U16((ushort)prefab);
            w.U8((byte)Mathf.Clamp(rounds < 0 ? 255 : rounds, 0, 255));
            Codec.Pos(w, pos);
            w.U32(Codec.Quat(rot));
            Codec.Vel(w, vel);
            Codec.Pos(w, Vector3.ClampMagnitude(ang, 200f));
        }

        static Pending ReadSpawn(ref NetReader rd)
        {
            var p = new Pending
            {
                Id = rd.U32(),
                Prefab = rd.U16(),
                Rounds = rd.U8(),
                Pos = Codec.Pos(ref rd),
                Rot = Codec.Quat(rd.U32()),
                Vel = Codec.Velocity(ref rd),
                AngVel = Codec.Pos(ref rd),
                At = _time,
            };
            rd.End();
            if (p.Rounds == 255) p.Rounds = -1;
            // An unknown prefab index is skipped at spawn time, not rejected: our level may still be loading.
            if (p.AngVel.sqrMagnitude > 200f * 200f) p.AngVel = Vector3.ClampMagnitude(p.AngVel, 200f); // odd, not malformed
            if ((p.Id & DynamicBit) == 0) throw new ProtocolException("bad item id");
            return p;
        }

        public static void HostReceiveSpawned(NetSession s, ushort from, ref NetReader rd)
        {
            var p = ReadSpawn(ref rd);
            if (((p.Id >> 16) & 0x7FFF) != (uint)(from & 0x7FFF)) throw new ProtocolException("item id not yours");
            // Only near the sender, and only once the host knows where they are.
            if (!PlayerSync.TryGetPosition(from, out var body) || (body - p.Pos).sqrMagnitude > Protocol.MaxShotOffset * Protocol.MaxShotOffset)
                return;
            if (IsRepeat(p.Id)) return;
            Arrive(p);
            var w = WorldSync.Begin(_w, Msg.ItemSpawned);
            WriteSpawn(w, p.Id, p.Prefab, p.Rounds, p.Pos, p.Rot, p.Vel, p.AngVel);
            s.SendToClients(w, Channel.Reliable, from);
        }

        public static void ClientReceiveSpawned(ref NetReader rd)
        {
            var p = ReadSpawn(ref rd);
            if (!IsRepeat(p.Id)) Arrive(p);
        }

        static bool IsRepeat(uint id) => _taken.Contains(id) || _items.ContainsKey(id) || _pending.ContainsKey(id);

        /// <summary>A new item from another player, counted once (however long it then waits for its tile).</summary>
        static void Arrive(Pending p)
        {
            // A player let go of a loose round: their counter drops without the round going into a gun.
            if (PrefabRegistry.TryGet(p.Prefab, out var dropped) && dropped.Kind == PrefabKind.Round)
                GearSync.OnRoundDropped((ushort)((p.Id >> 16) & 0x7FFF));
            Spawn(p, Object.FindObjectOfType<LevelCreatorScript>());
        }

        static void Wait(Pending p)
        {
            if (_pending.Count < MaxPending || _pending.ContainsKey(p.Id)) _pending[p.Id] = p;
        }

        static void Spawn(Pending p, LevelCreatorScript level)
        {
            if (_taken.Contains(p.Id) || _items.ContainsKey(p.Id)) return;
            // The main level is built in tiles around the players; scenes without that (the gun range) are whole.
            if (LocalPlayer.Get() == null || (level != null && !TileLoaded(level, p.Pos)))
            {
                Wait(p); // its tile isn't built here yet: create it when it is
                return;
            }
            if (!PrefabRegistry.TryGet(p.Prefab, out var entry))
            {
                if (PrefabRegistry.Count == 0) Wait(p); // gun list not loaded yet
                return;
            }
            var go = Object.Instantiate(entry.Prefab, p.Pos, p.Rot);
            var parent = level != null ? level.GetPositionTileItemParent(p.Pos) : null;
            if (parent != null) go.transform.SetParent(parent, true);
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.velocity = p.Vel;
            rb.angularVelocity = p.AngVel;
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = true;
            Tag(go, p.Id, p.Rounds);
        }

        static bool TileLoaded(LevelCreatorScript level, Vector3 pos)
        {
            var tile = level.GetTileAtPosition(pos);
            return tile != null && tile.tile_object != null;
        }

        public static void Frame(float dt)
        {
            _time += dt;
            if (_pending.Count == 0 || _time < _nextPending) return;
            _nextPending = _time + 1f;
            var batch = new List<Pending>(_pending.Values);
            _pending.Clear();
            var level = Object.FindObjectOfType<LevelCreatorScript>(); // once for the whole batch
            foreach (var p in batch)
                if (_time - p.At < 600f) Spawn(p, level); // waits again if still not loadable
        }

        // ================= patches =================

        /// <summary>Ammo piles: seeded contents, one of the run's guns, ids for everything spawned.</summary>
        [HarmonyPatch(typeof(BulletPileScript), nameof(BulletPileScript.Start))]
        static class PilePatch
        {
            struct State
            {
                public bool Active, HadFlashlight;
                public Random.State Rng;
                public GUISkinHolder Holder;
                public GameObject Weapon;
                public Transform Parent;
                public int Before;
                public uint PileId;
            }

            static void Prefix(BulletPileScript __instance, out State __state)
            {
                __state = default;
                var run = RunStarter.Active;
                if (!WorldSync.InRun || run == null) return;
                var pos = __instance.transform.position;
                uint pileId = Hash(run.Seed, pos, 1);
                __state.Active = true;
                __state.PileId = pileId;
                __state.Rng = Random.state;
                Random.InitState((int)pileId);

                var holderGo = GameObject.Find("gui_skin_holder");
                var holder = holderGo != null ? holderGo.GetComponent<GUISkinHolder>() : null;
                if (holder != null)
                {
                    __state.Holder = holder;
                    __state.Weapon = holder.weapon;
                    __state.HadFlashlight = holder.has_flashlight;
                    holder.has_flashlight = false; // roll the flashlight the same way on every machine
                    if (run.Guns.Count > 0 && holder.weapons != null)
                    {
                        int g = run.Guns[(int)(pileId % (uint)run.Guns.Count)];
                        if (g >= 0 && g < holder.weapons.Length && holder.weapons[g] != null)
                        {
                            holder.weapon = holder.weapons[g];
                            var names = NetSession.Current?.GunNames;
                            SyncStats.Count("ammo pile for " + (names != null && g < names.Count ? names[g] : "gun " + g));
                        }
                    }
                    else SyncStats.Note("ammo pile: no run guns", "this run has no gun list; ammo piles stock only your own gun's ammo");
                }
                var level = Object.FindObjectOfType<LevelCreatorScript>();
                __state.Parent = level != null ? level.GetPositionTileItemParent(pos) : null;
                __state.Before = __state.Parent != null ? __state.Parent.childCount : 0;
            }

            static Exception Finalizer(Exception __exception, State __state)
            {
                if (!__state.Active) return __exception;
                Random.state = __state.Rng;
                if (__state.Holder != null)
                {
                    __state.Holder.weapon = __state.Weapon;
                    __state.Holder.has_flashlight = __state.HadFlashlight;
                }
                if (__state.Parent != null)
                    for (int i = __state.Before; i < __state.Parent.childCount; i++)
                        Tag(__state.Parent.GetChild(i).gameObject, (__state.PileId ^ ((uint)(i - __state.Before + 1) * 0x2545F491u)) & ~DynamicBit);
                return __exception;
            }
        }

        /// <summary>Magazine round counts: rolled from the seed and position, or set to what the network said.</summary>
        [HarmonyPatch(typeof(mag_script), "Start")]
        static class MagStartPatch
        {
            struct State
            {
                public bool Active;
                public Random.State Rng;
            }

            static void Prefix(mag_script __instance, out State __state)
            {
                __state = new State { Rng = Random.state, Active = WorldSync.InRun && RunStarter.Active != null };
                if (__state.Active) Random.InitState((int)Hash(RunStarter.Active.Seed, __instance.transform.position, 2));
            }

            static Exception Finalizer(Exception __exception, mag_script __instance, State __state)
            {
                if (!__state.Active) return __exception;
                Random.state = __state.Rng;
                var tag = __instance.GetComponent<ItemTag>();
                if (tag != null && tag.Rounds >= 0) __instance.SetRoundCount(Mathf.Min(tag.Rounds, __instance.kMaxRounds));
                return __exception;
            }
        }
    }
}
