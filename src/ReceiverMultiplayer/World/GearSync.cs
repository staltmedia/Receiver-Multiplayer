using System;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// What each player carries, shown on their pill: a drawn gun floats where they hold it (moving parts,
    /// inserted magazine and chambered round mirrored live), a holstered gun hangs on the right hip muzzle-down,
    /// magazines and the flashlight ride down the left side in slot order, loose rounds sit across the back.
    /// A held flashlight shines for everyone. Items glide between belt and hand.
    /// The owner also reports items that leave their possession (drops, ejected rounds and casings) and
    /// world items they pick up, so the shared world stays in step.
    /// </summary>
    static class GearSync
    {
        // places
        const byte HandGun = 0, HandMag = 1, HandFlashlight = 2, Attached = 3, SlotBase = 10;
        public const byte CarryBase = 40, CarryHandleBase = 200;
        const float EyeHeight = 0.8f;      // CharacterController height; eye = height·crouch − 0.1
        const int MaxItems = 32, MaxParts = 40;
        const float HeartbeatSeconds = 1f;

        // ---- private game fields ----
        public static readonly AccessTools.FieldRef<AimScript, GameObject> GunInstance = AccessTools.FieldRefAccess<AimScript, GameObject>("gun_instance");
        public static readonly AccessTools.FieldRef<AimScript, GameObject> MagInHand = AccessTools.FieldRefAccess<AimScript, GameObject>("magazine_instance_in_hand");
        public static readonly AccessTools.FieldRef<AimScript, GameObject> HeldFlashlight = AccessTools.FieldRefAccess<AimScript, GameObject>("held_flashlight");
        public static readonly AccessTools.FieldRef<AimScript, WeaponSlot[]> Slots = AccessTools.FieldRefAccess<AimScript, WeaponSlot[]>("weapon_slots");
        static readonly AccessTools.FieldRef<AimScript, List<GameObject>> BeingPickedUp = AccessTools.FieldRefAccess<AimScript, List<GameObject>>("items_being_picked_up");
        public static readonly AccessTools.FieldRef<AimScript, int> UnplayedTapes = AccessTools.FieldRefAccess<AimScript, int>("unplayed_tapes");

        public static Pose CameraFrame(Vector3 bodyPos, float crouch, Quaternion look) =>
            new Pose(bodyPos + Vector3.up * (EyeHeight * crouch - 0.1f), look);

        // =====================================================================
        // Owner
        // =====================================================================

        sealed class Owned
        {
            public byte Handle;
            public int Prefab;
            public Transform[] Parts;   // aligned with the prefab's transform order (null where missing)
            public Pose[] Rest;
            public HashSet<Transform> PartSet;
        }

        static readonly Dictionary<GameObject, Owned> _owned = new Dictionary<GameObject, Owned>();
        static readonly HashSet<GameObject> _seenThisTick = new HashSet<GameObject>();
        static readonly List<GameObject> _scratch = new List<GameObject>();
        static readonly List<Transform> _descendants = new List<Transform>();
        static readonly NetWriter _w = new NetWriter(1200);
        static readonly NetWriter _relay = new NetWriter(1200);
        static byte[] _lastSent = new byte[0];
        static int _lastSentLength = -1;
        static float _time, _lastSendAt;
        static byte _nextHandle = 1;
        static int _ownWeapon = -2;

        /// <summary>Index of the local player's gun in the gun list (-1 if unknown).</summary>
        public static int OwnWeapon()
        {
            if (_ownWeapon != -2) return _ownWeapon;
            var names = RunStarter.LocalGunNames();
            return _ownWeapon = names.IndexOf(RunStarter.Active?.Gun ?? "");
        }

        public static void Reset()
        {
            _owned.Clear();
            _lastSentLength = -1;
            _ownWeapon = -2;
            foreach (var v in _views.Values) v.Destroy();
            _views.Clear();
        }

        static Owned TrackObject(GameObject go, int preferWeapon)
        {
            if (_owned.TryGetValue(go, out var o)) { _seenThisTick.Add(go); return o; }
            int prefab = PrefabRegistry.IndexOf(go, preferWeapon);
            if (prefab < 0) return null;
            // A world item that came into our possession by any path (pickup, grabbing an ejected magazine
            // back, ...) is gone for everyone else; otherwise each drop-and-regrab would leave them a copy.
            if (go.GetComponent<ItemTag>() != null) ItemSync.OnLocalTake(go);
            o = new Owned { Handle = NextHandle(), Prefab = prefab };
            if (PrefabRegistry.TryGet(prefab, out var entry) && entry.Kind == PrefabKind.Gun)
                MapParts(o, go, entry.Prefab);
            _owned[go] = o;
            _seenThisTick.Add(go);
            return o;
        }

        static byte NextHandle()
        {
            for (int guard = 0; guard < 256; guard++)
            {
                byte h = _nextHandle++;
                if (_nextHandle >= CarryHandleBase) _nextHandle = 1; // 200+ are carried items of other guns
                bool used = false;
                foreach (var o in _owned.Values) if (o.Handle == h) { used = true; break; }
                if (!used) return h;
            }
            return 1;
        }

        /// <summary>Line the instance's transforms up with the prefab's (by hierarchy path), so parts mirror by index.</summary>
        static void MapParts(Owned o, GameObject instance, GameObject prefab)
        {
            var prefabParts = prefab.GetComponentsInChildren<Transform>(true);
            int n = Math.Min(prefabParts.Length, 255);
            o.Parts = new Transform[n];
            o.Rest = new Pose[n];
            o.PartSet = new HashSet<Transform>();
            for (int i = 0; i < n; i++)
            {
                var p = prefabParts[i];
                o.Rest[i] = new Pose(p.localPosition, p.localRotation);
                var t = i == 0 ? instance.transform : instance.transform.Find(PathOf(p, prefab.transform));
                o.Parts[i] = t;
                if (t != null) o.PartSet.Add(t);
            }
        }

        static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }

        /// <summary>Owner, every net tick: write the gear message, detect drops/ejections.</summary>
        public static void NetTick(NetSession s)
        {
            _time += Protocol.WorldTick;
            var me = LocalPlayer.Get();
            if (me == null) return;
            _seenThisTick.Clear();

            var t = me.transform;
            var cam = me.main_camera != null ? me.main_camera.transform : t;
            var frame = CameraFrame(t.position, t.localScale.y,
                Quaternion.Euler(Mathf.DeltaAngle(0f, cam.eulerAngles.x), cam.eulerAngles.y, 0f));
            var invRot = Quaternion.Inverse(frame.rotation);
            int weapon = OwnWeapon();

            var w = WorldSync.Begin(_w, Msg.Gear);
            w.U16(WorldSync.Tick16); // same clock as the pose: viewers draw both at the same moment
            int countAt = w.Length;
            w.U8(0);
            int count = 0;

            void Hand(GameObject go, byte place)
            {
                if (go == null || count >= MaxItems) return;
                var o = TrackObject(go, weapon);
                if (o == null) return;
                WriteItem(w, o, place, 0, invRot * (go.transform.position - frame.position), invRot * go.transform.rotation, go);
                count++;
                if (o.Parts != null) count += WriteAttachments(w, o, go, weapon, MaxItems - count);
            }

            Hand(GunInstance(me), HandGun);
            Hand(MagInHand(me), HandMag);
            Hand(HeldFlashlight(me), HandFlashlight);

            var slots = Slots(me);
            if (slots != null)
                for (int i = 0; i < slots.Length && i < 10 && count < MaxItems; i++)
                {
                    var slot = slots[i];
                    if (slot == null || slot.obj == null) continue;
                    if (slot.type != WeaponSlotType.GUN && slot.type != WeaponSlotType.MAGAZINE && slot.type != WeaponSlotType.FLASHLIGHT) continue;
                    var o = TrackObject(slot.obj, weapon);
                    if (o == null) continue;
                    WriteItem(w, o, (byte)(SlotBase + i), 0, Vector3.zero, Quaternion.identity, slot.obj);
                    count++;
                    if (o.Parts != null) count += WriteAttachments(w, o, slot.obj, weapon, MaxItems - count);
                }
            count += CarrySync.WriteCarriedMags(w, MaxItems - count);
            w.Buffer[countAt] = (byte)count;

            // loose rounds (own type) and carried rounds of other guns
            w.U8((byte)Mathf.Min(me.loose_bullets != null ? me.loose_bullets.Count : 0, 255));
            int roundPrefab = PrefabRegistry.IndexOf(PrefabKind.Round, weapon);
            w.U16(roundPrefab < 0 ? ushort.MaxValue : (ushort)roundPrefab);
            CarrySync.WriteCarriedRounds(w);

            DetectReleases();

            bool changed = w.Length != _lastSentLength || !SameBytes(w);
            if (changed || _time - _lastSendAt >= HeartbeatSeconds)
            {
                Remember(w);
                _lastSendAt = _time;
                if (s.IsHost) Relay(s, s.LocalId, w, 3, -1);
                else s.SendToHost(w, ChannelFor(w.Length));
            }
        }

        /// <summary>Gear is idempotent, so it normally goes unreliable; a rare oversized one goes reliable instead.</summary>
        static Channel ChannelFor(int length) => length <= Protocol.MaxUnreliableBytes - 32 ? Channel.Unreliable : Channel.Reliable;

        static void WriteItem(NetWriter w, Owned o, byte place, byte parent, Vector3 pos, Quaternion rot, GameObject go)
        {
            w.U8(o.Handle);
            w.U16((ushort)o.Prefab);
            w.U8(place);
            if (place <= HandFlashlight)
            {
                Codec.Pos(w, pos);
                w.U32(Codec.Quat(rot));
            }
            else if (place == Attached)
            {
                w.U8(parent);
                Codec.Pos(w, pos);
                w.U32(Codec.Quat(rot));
            }
            var mag = go.GetComponent<mag_script>();
            w.U8(mag != null ? (byte)Mathf.Clamp(mag.NumRounds(), 0, 254) : (byte)255);
            WriteLights(w, go);
            WriteParts(w, o);
        }

        /// <summary>An item with no pose, lights or moving parts (a carried magazine of another gun).</summary>
        public static void WriteSimpleItem(NetWriter w, byte handle, int prefab, byte place, int rounds)
        {
            w.U8(handle);
            w.U16((ushort)prefab);
            w.U8(place);
            w.U8(rounds < 0 ? (byte)255 : (byte)Mathf.Clamp(rounds, 0, 254));
            w.U8(0);
            w.U8(0);
            w.U8(0);
        }

        /// <summary>Flashlight: point and spot intensity (0 = off), so the beam shows for everyone.</summary>
        static void WriteLights(NetWriter w, GameObject go)
        {
            byte point = 0, spot = 0;
            var fl = go.GetComponent<FlashlightScript>();
            if (fl != null && fl.switch_on)
                foreach (var l in go.GetComponentsInChildren<Light>(true))
                {
                    if (!l.enabled) continue;
                    if (l.type == UnityEngine.LightType.Spot) spot = Codec.UnitByte(l.intensity, 10f);
                    else point = Codec.UnitByte(l.intensity, 10f);
                }
            w.U8(point);
            w.U8(spot);
        }

        static void WriteParts(NetWriter w, Owned o)
        {
            if (o.Parts == null) { w.U8(0); return; }
            int countAt = w.Length;
            w.U8(0);
            int n = 0;
            for (int i = 1; i < o.Parts.Length && n < MaxParts; i++)
            {
                var p = o.Parts[i];
                if (p == null) continue;
                var rest = o.Rest[i];
                if ((p.localPosition - rest.position).sqrMagnitude < 0.0002f * 0.0002f && Quaternion.Angle(p.localRotation, rest.rotation) < 0.15f)
                    continue;
                w.U8((byte)i);
                Codec.Pos(w, p.localPosition);
                w.U32(Codec.Quat(p.localRotation));
                n++;
            }
            w.Buffer[countAt] = (byte)n;
        }

        /// <summary>Magazine, chambered round, cylinder rounds: registry items parented anywhere under the gun.</summary>
        static int WriteAttachments(NetWriter w, Owned gun, GameObject gunGo, int weapon, int room)
        {
            int n = 0;
            var root = gunGo.transform;
            root.GetComponentsInChildren(true, _descendants);
            var invRot = Quaternion.Inverse(root.rotation);
            foreach (var d in _descendants)
            {
                if (n >= room) break;
                if (d == root || gun.PartSet.Contains(d)) continue;
                if (d.parent != null && !gun.PartSet.Contains(d.parent)) continue; // only top-level attachments
                var local = root.InverseTransformPoint(d.position);
                if (local.sqrMagnitude > 4f) continue; // momentarily away from the gun (being ejected/dropped)
                var o = TrackObject(d.gameObject, weapon);
                if (o == null) continue;
                WriteItem(w, o, Attached, gun.Handle, local, invRot * d.rotation, d.gameObject);
                n++;
            }
            return n;
        }

        /// <summary>Anything we held last tick that is now a free physics object was dropped or ejected.</summary>
        static void DetectReleases()
        {
            _scratch.Clear();
            foreach (var kv in _owned)
                if (!_seenThisTick.Contains(kv.Key)) _scratch.Add(kv.Key);
            foreach (var go in _scratch)
            {
                var o = _owned[go];
                _owned.Remove(go);
                if (go == null) continue; // destroyed (round loaded into a mag, etc.)
                var rb = go.GetComponent<Rigidbody>();
                if (rb == null || rb.isKinematic || IsInInventory(go.transform)) continue;
                ItemSync.OnLocalRelease(go, o.Prefab);
            }
        }

        static bool IsInInventory(Transform t)
        {
            for (var x = t.parent; x != null; x = x.parent)
                if (x.name == "PlayerInventory") return true;
            return false;
        }

        /// <summary>Unchanged since the last send? (Ignores the tick at bytes 3–4, which always changes.)</summary>
        static bool SameBytes(NetWriter w)
        {
            if (_lastSent.Length < w.Length) return false;
            for (int i = 0; i < w.Length; i++)
                if ((i < 3 || i > 4) && _lastSent[i] != w.Buffer[i]) return false;
            return true;
        }

        static void Remember(NetWriter w)
        {
            if (_lastSent.Length < w.Length) _lastSent = new byte[w.Buffer.Length];
            Buffer.BlockCopy(w.Buffer, 0, _lastSent, 0, w.Length);
            _lastSentLength = w.Length;
        }

        /// <summary>Host: forward a gear payload (from byte <paramref name="payloadAt"/> of <paramref name="src"/>) with its owner id.</summary>
        static void Relay(NetSession s, ushort owner, NetWriter src, int payloadAt, int except)
        {
            var r = WorldSync.Begin(_relay, Msg.Gear);
            r.U16(owner);
            r.Raw(src.Buffer, payloadAt, src.Length - payloadAt);
            s.SendToClients(r, ChannelFor(r.Length), except);
        }

        // ---- receiving ----

        public static void HostReceive(NetSession s, ushort from, ref NetReader rd)
        {
            var copy = new NetWriter(rd.Remaining + 8);
            rd.CopyRestTo(copy);
            Read(from, ref rd);                 // validates; throws on garbage before anything is relayed
            var r = WorldSync.Begin(_relay, Msg.Gear);
            r.U16(from);
            r.Raw(copy.Buffer, 0, copy.Length);
            s.SendToClients(r, ChannelFor(r.Length), from);
        }

        public static void ClientReceive(NetSession s, ref NetReader rd)
        {
            ushort owner = rd.U16();
            if (owner == s.LocalId) { return; }
            Read(owner, ref rd);
        }

        static readonly View _discard = new View();

        static void Read(ushort owner, ref NetReader rd)
        {
            ushort rawTick = rd.U16();
            var real = GetView(owner);
            long tick = PlayerSync.UnwrapNear(owner, rawTick, ref real.Unwrap);
            bool stale = tick < 0 || tick <= real.LastTick;
            var v = stale ? _discard : real; // an out-of-order packet is read (validated) but not applied
            if (!stale) real.LastTick = tick;
            v.BeginUpdate();
            int n = rd.U8();
            if (n > MaxItems) throw new ProtocolException("too many items");
            for (int i = 0; i < n; i++)
            {
                byte handle = rd.U8();
                int prefab = rd.U16();
                byte place = rd.U8();
                // An unknown prefab index just isn't drawn (our level may still be loading).
                if (place > Attached && (place < SlotBase || place >= SlotBase + 10) && (place < CarryBase || place >= CarryBase + 16))
                    throw new ProtocolException("bad gear place");
                var item = v.Item(handle, prefab);
                item.Place = place;
                var sample = item.NextSample(tick);
                if (place <= HandFlashlight)
                {
                    sample.Pos = Codec.Pos(ref rd);
                    sample.Rot = Codec.Quat(rd.U32());
                }
                else if (place == Attached)
                {
                    item.Parent = rd.U8();
                    sample.Pos = Codec.Pos(ref rd);
                    sample.Rot = Codec.Quat(rd.U32());
                }
                // Odd but well-formed values (an item far from its gun or hand mid-animation) are clamped, never
                // a reason to disconnect: only structural errors are protocol errors.
                float limit = place == Attached ? 2f : 3f;
                if (place <= Attached && sample.Pos.sqrMagnitude > limit * limit) sample.Pos = sample.Pos.normalized * limit;
                byte rounds = rd.U8();
                sample.Rounds = rounds == 255 ? -1 : rounds; // shown at the owner's render time, with the hands
                item.Point = Codec.UnitByte(rd.U8(), 10f);
                item.Spot = Codec.UnitByte(rd.U8(), 10f);
                int parts = rd.U8();
                if (parts > MaxParts) throw new ProtocolException("too many parts");
                sample.PartCount = parts;
                for (int k = 0; k < parts; k++)
                {
                    sample.Idx[k] = rd.U8();
                    sample.PPos[k] = Codec.Pos(ref rd);
                    sample.PRot[k] = Codec.Quat(rd.U32());
                    if (sample.PPos[k].sqrMagnitude > 4f) sample.PPos[k] = sample.PPos[k].normalized * 2f;
                }
            }
            v.LooseRounds = rd.U8();
            v.AddLoose(tick, v.LooseRounds);
            int roundPrefab = rd.U16();
            v.RoundPrefab = roundPrefab == ushort.MaxValue ? -1 : roundPrefab;
            CarrySync.ReadCarriedRounds(ref rd, v.Carried);
            rd.End();
            v.EndUpdate();
            if (stale) _discard.Destroy();
        }

        // =====================================================================
        // Viewer
        // =====================================================================

        /// <summary>One received state of an item: its pose (hand or gun relative) and moving parts.</summary>
        sealed class Sample
        {
            public long Tick;
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public int Rounds = -1;
            public int PartCount;
            public readonly byte[] Idx = new byte[MaxParts];
            public readonly Vector3[] PPos = new Vector3[MaxParts];
            public readonly Quaternion[] PRot = new Quaternion[MaxParts];
        }

        sealed class Item
        {
            const int History = 10; // ~0.5 s at 20 Hz: covers the 150 ms render delay with room to spare

            public byte Handle;
            public int Prefab;
            public byte Place, Parent;
            public int Rounds = -1;        // at the render time (a magazine's count)
            public float Point, Spot;
            public bool Seen;
            public Replica Replica;
            public List<(int round, Renderer renderer)> RoundRenderers;

            // a round going into this magazine (the game's mag_script load animation, replayed)
            public int ShownRounds = -1;
            public float LoadStart = -1f, ReloadTime;
            public int StartLoadPart = -1, LoadPart = -1;
            public int[] RoundParts;       // part index of "round_k" at [k]
            public bool EverShown;

            readonly Sample[] _hist = new Sample[History];
            int _count;

            // Interpolated at the owner's render time; what placement draws.
            public Vector3 Pos;
            public Quaternion Rot = Quaternion.identity;
            public int PartCount;
            public readonly byte[] PartIndex = new byte[MaxParts];
            public readonly Vector3[] PartPos = new Vector3[MaxParts];
            public readonly Quaternion[] PartRot = new Quaternion[MaxParts];

            /// <summary>
            /// Drawn only once the render time has reached its first state, so something that appears (a round in a
            /// chamber) does so with the hands, not when its packet arrives.
            /// </summary>
            public bool StartedBy(double renderTick) => _count > 0 && _hist[0].Tick <= renderTick + 0.5;

            public Sample NextSample(long tick)
            {
                if (_count == History)
                {
                    var oldest = _hist[0];
                    Array.Copy(_hist, 1, _hist, 0, History - 1);
                    _hist[History - 1] = oldest;
                    _count--;
                }
                var s = _hist[_count] ?? (_hist[_count] = new Sample());
                _count++;
                s.Tick = tick;
                s.PartCount = 0;
                return s;
            }

            /// <summary>
            /// Pose and parts at <paramref name="renderTick"/>, between the two samples around it, so a held gun
            /// moves as smoothly as the player's own pose (drawn at the same moment). <paramref name="rest"/>
            /// gives the resting pose of parts that only one of the two samples moved.
            /// </summary>
            public void Interpolate(double renderTick, Pose[] rest)
            {
                if (_count == 0) return;
                int b = 0;
                while (b < _count && _hist[b].Tick < renderTick) b++;
                Sample sa, sb;
                float t;
                if (b == _count) { sa = sb = _hist[_count - 1]; t = 0f; }
                else if (b == 0) { sa = sb = _hist[0]; t = 0f; }
                else
                {
                    sa = _hist[b - 1];
                    sb = _hist[b];
                    t = (float)((renderTick - sa.Tick) / Math.Max(1, sb.Tick - sa.Tick));
                }
                Pos = Vector3.Lerp(sa.Pos, sb.Pos, t);
                Rot = Quaternion.Slerp(sa.Rot, sb.Rot, t);
                Rounds = t >= 1f ? sb.Rounds : sa.Rounds;

                int n = 0;
                for (int i = 0; i < sa.PartCount && n < MaxParts; i++)
                {
                    int idx = sa.Idx[i];
                    int j = Find(sb, idx);
                    Vector3 toPos;
                    Quaternion toRot;
                    if (j >= 0) { toPos = sb.PPos[j]; toRot = sb.PRot[j]; }
                    else if (rest != null && idx < rest.Length) { toPos = rest[idx].position; toRot = rest[idx].rotation; }
                    else { toPos = sa.PPos[i]; toRot = sa.PRot[i]; }
                    PartIndex[n] = (byte)idx;
                    PartPos[n] = Vector3.Lerp(sa.PPos[i], toPos, t);
                    PartRot[n] = Quaternion.Slerp(sa.PRot[i], toRot, t);
                    n++;
                }
                for (int j = 0; j < sb.PartCount && n < MaxParts; j++)
                {
                    int idx = sb.Idx[j];
                    if (Find(sa, idx) >= 0) continue;
                    var from = rest != null && idx < rest.Length ? rest[idx] : new Pose(sb.PPos[j], sb.PRot[j]);
                    PartIndex[n] = (byte)idx;
                    PartPos[n] = Vector3.Lerp(from.position, sb.PPos[j], t);
                    PartRot[n] = Quaternion.Slerp(from.rotation, sb.PRot[j], t);
                    n++;
                }
                PartCount = n;
            }

            static int Find(Sample s, int idx)
            {
                for (int i = 0; i < s.PartCount; i++) if (s.Idx[i] == idx) return i;
                return -1;
            }
        }

        sealed class View
        {
            public TickUnwrapper Unwrap;
            public long LastTick = long.MinValue;
            public readonly Dictionary<byte, Item> Items = new Dictionary<byte, Item>();
            public readonly List<Replica> Rounds = new List<Replica>();
            public readonly List<(int prefab, int count)> Carried = new List<(int, int)>();
            public int LooseRounds, RoundPrefab = -1;
            readonly List<byte> _drop = new List<byte>();

            // Loose round count by tick, so the counter changes at the render time, with the hands.
            const int LooseHistory = 16;
            readonly long[] _looseTick = new long[LooseHistory];
            readonly int[] _looseCount = new int[LooseHistory];
            int _looseN;
            public int ShownLoose = -1;

            // This frame: a round went into the held magazine, or a round appeared in the held gun.
            public Item LoadedMag, NewGunRound;
            public readonly List<Flight> Flights = new List<Flight>();

            public void AddLoose(long tick, int count)
            {
                if (_looseN == LooseHistory)
                {
                    Array.Copy(_looseTick, 1, _looseTick, 0, LooseHistory - 1);
                    Array.Copy(_looseCount, 1, _looseCount, 0, LooseHistory - 1);
                    _looseN--;
                }
                _looseTick[_looseN] = tick;
                _looseCount[_looseN] = count;
                _looseN++;
            }

            public int LooseAt(double renderTick)
            {
                if (_looseN == 0) return LooseRounds;
                int best = 0;
                for (int i = 0; i < _looseN; i++) if (_looseTick[i] <= renderTick) best = i;
                return _looseCount[best];
            }

            public Item Item(byte handle, int prefab)
            {
                if (Items.TryGetValue(handle, out var it) && it.Prefab != prefab)
                {
                    it.Replica?.Destroy();
                    Items.Remove(handle);
                    it = null;
                }
                if (it == null) Items[handle] = it = new Item { Handle = handle, Prefab = prefab };
                it.Seen = true;
                return it;
            }

            public void BeginUpdate()
            {
                foreach (var it in Items.Values) it.Seen = false;
            }

            public void EndUpdate()
            {
                _drop.Clear();
                foreach (var kv in Items) if (!kv.Value.Seen) _drop.Add(kv.Key);
                foreach (var h in _drop)
                {
                    Items[h].Replica?.Destroy();
                    Items.Remove(h);
                }
            }

            public void Destroy()
            {
                foreach (var it in Items.Values) it.Replica?.Destroy();
                Items.Clear();
                foreach (var r in Rounds) r.Destroy();
                Rounds.Clear();
                foreach (var f in Flights) f.Rep?.Destroy();
                Flights.Clear();
            }
        }

        /// <summary>A loose round on its way from the spectated player's counter into their magazine or gun.</summary>
        sealed class Flight
        {
            public Replica Rep;
            public Pose From;
            public Transform Target;
            public float Start;
        }

        const float FlightSeconds = 0.14f;

        static readonly Dictionary<ushort, View> _views = new Dictionary<ushort, View>();
        static readonly List<(Item item, int order)> _left = new List<(Item, int)>();

        static View GetView(ushort owner)
        {
            if (!_views.TryGetValue(owner, out var v)) _views[owner] = v = new View();
            return v;
        }

        /// <summary>Where this player's hands are (their held gun, magazine or flashlight), for their handling sounds.</summary>
        public static bool TryGetHandPosition(ushort owner, out Vector3 pos)
        {
            pos = default;
            if (!_views.TryGetValue(owner, out var v)) return false;
            Item best = null;
            foreach (var it in v.Items.Values)
                if (it.Place <= HandFlashlight && it.Replica?.Go != null && it.Replica.Go.activeSelf && (best == null || it.Place < best.Place))
                    best = it;
            if (best == null) return false;
            pos = best.Replica.Go.transform.position;
            return true;
        }

        public static void Hide(ushort owner)
        {
            if (!_views.TryGetValue(owner, out var v)) return;
            foreach (var it in v.Items.Values)
                if (it.Replica?.Go != null && it.Replica.Go.activeSelf) it.Replica.Go.SetActive(false);
            foreach (var r in v.Rounds)
                if (r.Go != null) r.Go.SetActive(false);
        }

        public static void RemoveView(ushort owner)
        {
            if (_views.TryGetValue(owner, out var v)) v.Destroy();
            _views.Remove(owner);
        }

        /// <summary>
        /// Every frame (late, after the camera has moved). <paramref name="firstPerson"/>: we're spectating this
        /// player through <paramref name="cam"/>, so their view is mirrored: what's in their hands where they hold
        /// it, their inventory along the bottom of the screen and their loose rounds top-left, as the game shows
        /// them to the player themselves.
        /// </summary>
        public static void Place(ushort owner, Transform body, PoseSample pose, bool visible, bool firstPerson, float now, Camera cam, double renderTick)
        {
            if (!_views.TryGetValue(owner, out var v)) return;
            var frame = CameraFrame(pose.Pos, pose.F0, pose.R1);
            bool hud = firstPerson && cam != null;
            v.LoadedMag = null;
            v.NewGunRound = null;

            // Left side order: slots first (by slot), then carried magazines.
            _left.Clear();
            foreach (var it in v.Items.Values)
            {
                bool gunInSlot = it.Place >= SlotBase && it.Place < SlotBase + 10 && IsGun(it.Prefab);
                if ((it.Place >= SlotBase && it.Place < SlotBase + 10 && !gunInSlot) || it.Place >= CarryBase)
                    _left.Add((it, it.Place));
            }
            _left.Sort((a, b) => a.order.CompareTo(b.order));

            // Pass 1: everything but attachments.
            foreach (var it in v.Items.Values)
            {
                if (it.Place == Attached) continue;
                bool inHand = it.Place <= HandFlashlight;
                bool inSlot = it.Place >= SlotBase && it.Place < SlotBase + 10;
                bool show = visible && (inHand || !hud || inSlot) && it.StartedBy(renderTick); // carried mags of other guns aren't on their HUD
                if (!Ensure(it, show)) continue;
                it.Interpolate(renderTick, it.Replica.Rest);
                if (IsMagazine(it.Prefab))
                {
                    // A round went into the magazine in their hand: replay the game's load animation (when
                    // spectating, after the round has come down from their counter).
                    if (inHand && it.ShownRounds >= 0 && it.Rounds > it.ShownRounds)
                    {
                        StartLoad(it, now, hud ? FlightSeconds : 0f);
                        v.LoadedMag = it;
                    }
                    it.ShownRounds = it.Rounds;
                }

                Pose world;
                float scale = 1f;
                object placement = (int)it.Place;
                if (inHand)
                {
                    world = new Pose(frame.position + frame.rotation * it.Pos, frame.rotation * it.Rot);
                }
                else if (hud)
                {
                    world = BeltLayout.HudSlot(cam, it.Place - SlotBase);
                    scale = BeltLayout.HudScale;
                    placement = 1000 + it.Place;
                }
                else
                {
                    Pose local;
                    if (IsGun(it.Prefab) && it.Place < CarryBase) local = BeltLayout.Holster;
                    else
                    {
                        int index = _left.FindIndex(x => x.item == it);
                        local = BeltLayout.LeftSide(Math.Max(0, index), Math.Max(_left.Count, 4), IsFlashlight(it.Prefab));
                    }
                    world = new Pose(body.TransformPoint(local.position), body.rotation * local.rotation);
                }
                it.Replica.SetScale(scale);
                it.Replica.SetShadows(!hud || inHand);
                it.Replica.Place(world, placement, now);
                ApplyDetail(it, inHand, now);
            }

            // Pass 2: attachments follow their gun.
            foreach (var it in v.Items.Values)
            {
                if (it.Place != Attached) continue;
                v.Items.TryGetValue(it.Parent, out var parent);
                bool show = visible && parent?.Replica != null && parent.Replica.Go.activeSelf && it.StartedBy(renderTick);
                if (!Ensure(it, show)) continue;
                it.Interpolate(renderTick, it.Replica.Rest);
                var pt = parent.Replica.Go.transform;
                // Attachments inherit the gun's scale (a holstered gun on the HUD is drawn at 0.3).
                it.Replica.SetScale(pt.localScale.x / Mathf.Max(1e-4f, PrefabScale(parent.Prefab)));
                it.Replica.Place(new Pose(pt.TransformPoint(it.Pos), pt.rotation * it.Rot), (int)Attached * 1000 + it.Parent, now);
                ApplyDetail(it, false, now);
                // A round that just appeared in the gun in their hand (a revolver chamber, ...).
                if (!it.EverShown && hud && parent.Place == HandGun && IsRound(it.Prefab)) v.NewGunRound = it;
                it.EverShown = true;
            }

            PlaceRounds(owner, v, body, visible, hud ? cam : null, renderTick, now);
        }

        static bool IsMagazine(int prefab) => PrefabRegistry.TryGet(prefab, out var e) && e.Kind == PrefabKind.Magazine;
        static bool IsRound(int prefab) => PrefabRegistry.TryGet(prefab, out var e) && e.Kind == PrefabKind.Round;

        static readonly AccessTools.FieldRef<mag_script, float> BulletReloadTime = AccessTools.FieldRefAccess<mag_script, float>("bulletReloadTime");
        static readonly Dictionary<int, float> _reloadTimes = new Dictionary<int, float>();

        /// <summary>How long each of the two load stages takes for this magazine (mag_script.bulletReloadTime).</summary>
        static float ReloadTimeOf(int prefab)
        {
            if (_reloadTimes.TryGetValue(prefab, out float t)) return t;
            t = 0.1f;
            try
            {
                var mag = PrefabRegistry.TryGet(prefab, out var e) ? e.Prefab.GetComponent<mag_script>() : null;
                if (mag != null && BulletReloadTime(mag) > 0f) t = BulletReloadTime(mag);
            }
            catch { /* keep the default */ }
            return _reloadTimes[prefab] = t;
        }

        static void StartLoad(Item it, float now, float delay)
        {
            it.LoadStart = now + delay;
            it.ReloadTime = ReloadTimeOf(it.Prefab);
        }

        /// <summary>
        /// mag_script's load, on the copy: while the stack shifts down a slot, the new top round ("round_1") moves
        /// from "point_start_load" to "point_load"; then it settles from there into its slot. Each stage takes
        /// bulletReloadTime. Before it starts (the round is still flying down from the counter) it isn't drawn.
        /// </summary>
        static void AnimateMagLoad(Item it, float now)
        {
            if (it.LoadStart < 0f || it.Replica == null) return;
            var r = it.Replica;
            if (it.RoundParts == null)
            {
                var rounds = new List<(int n, int index)>();
                for (int i = 0; i < r.Parts.Length; i++)
                {
                    var p = r.Parts[i];
                    if (p == null) continue;
                    if (p.name == "point_start_load") it.StartLoadPart = i;
                    else if (p.name == "point_load") it.LoadPart = i;
                    else if (p.name.StartsWith("round_", StringComparison.Ordinal) && int.TryParse(p.name.Substring(6), out int n) && n >= 1 && n < 256)
                        rounds.Add((n, i));
                }
                int max = 0;
                foreach (var (n, _) in rounds) if (n > max) max = n;
                it.RoundParts = new int[max + 1];
                for (int k = 0; k <= max; k++) it.RoundParts[k] = -1;
                foreach (var (n, index) in rounds) it.RoundParts[n] = index;
            }
            if (it.StartLoadPart < 0 || it.LoadPart < 0 || it.RoundParts.Length < 2 || it.RoundParts[1] < 0) { it.LoadStart = -1f; return; }

            float stage = Mathf.Max(0.01f, it.ReloadTime);
            float t = (now - it.LoadStart) / stage;
            if (t >= 2f) { it.LoadStart = -1f; return; }
            var top = r.Parts[it.RoundParts[1]];
            if (t < 0f)
            {
                var rend = top.GetComponent<Renderer>();
                if (rend != null) rend.enabled = false;
                return;
            }
            var startLoad = r.Rest[it.StartLoadPart];
            var load = r.Rest[it.LoadPart];
            if (t < 1f)
            {
                top.localPosition = Vector3.Lerp(startLoad.position, load.position, t);
                top.localRotation = Quaternion.Slerp(startLoad.rotation, load.rotation, t);
                for (int k = 2; k < it.RoundParts.Length && k <= it.Rounds; k++)
                {
                    int cur = it.RoundParts[k], prev = it.RoundParts[k - 1];
                    if (cur < 0 || prev < 0) continue;
                    r.Parts[cur].localPosition = Vector3.Lerp(r.Rest[prev].position, r.Rest[cur].position, t);
                    r.Parts[cur].localRotation = Quaternion.Slerp(r.Rest[prev].rotation, r.Rest[cur].rotation, t);
                }
            }
            else
            {
                float p = t - 1f;
                var slot = r.Rest[it.RoundParts[1]];
                top.localPosition = Vector3.Lerp(load.position, slot.position, p);
                top.localRotation = Quaternion.Slerp(load.rotation, slot.rotation, p);
            }
        }

        static float PrefabScale(int prefab) => PrefabRegistry.TryGet(prefab, out var e) ? e.Prefab.transform.localScale.x : 1f;

        static bool Ensure(Item it, bool show)
        {
            if (it.Replica != null && it.Replica.Go == null) it.Replica = null; // destroyed with the scene
            if (!show)
            {
                if (it.Replica != null && it.Replica.Go.activeSelf) it.Replica.Go.SetActive(false);
                return false;
            }
            if (it.Replica == null)
            {
                it.Replica = Replica.Create(it.Prefab, null);
                if (it.Replica == null) return false;
                it.RoundRenderers = null;
            }
            if (!it.Replica.Go.activeSelf) it.Replica.Go.SetActive(true);
            return true;
        }

        static void ApplyDetail(Item it, bool held, float now)
        {
            var r = it.Replica;
            if (it.PartCount > 0 || r.Parts.Length > 1)
            {
                r.ResetParts();
                for (int k = 0; k < it.PartCount; k++)
                {
                    int i = it.PartIndex[k];
                    if (i <= 0 || i >= r.Parts.Length || r.Parts[i] == null) continue;
                    r.Parts[i].localPosition = it.PartPos[k];
                    r.Parts[i].localRotation = it.PartRot[k];
                }
            }
            if (it.Rounds >= 0) ShowRounds(it);
            AnimateMagLoad(it, now);
            foreach (var l in r.Lights)
            {
                bool spot = l.type == UnityEngine.LightType.Spot;
                float intensity = spot ? it.Spot : it.Point;
                l.enabled = held && intensity > 0f;
                if (l.enabled) { l.intensity = intensity; if (!spot) l.range = 10f; }
            }
        }

        /// <summary>Magazines show their rounds as children "round_1".."round_N".</summary>
        static void ShowRounds(Item it)
        {
            if (it.RoundRenderers == null)
            {
                it.RoundRenderers = new List<(int, Renderer)>();
                foreach (var t in it.Replica.Parts)
                {
                    if (t == null || !t.name.StartsWith("round_", StringComparison.Ordinal)) continue;
                    if (!int.TryParse(t.name.Substring(6), out int n)) continue;
                    var rend = t.GetComponent<Renderer>();
                    if (rend != null) it.RoundRenderers.Add((n, rend));
                }
            }
            foreach (var (n, rend) in it.RoundRenderers) rend.enabled = n <= it.Rounds;
        }

        /// <summary>Loose rounds: across the back of the belt, or top-left of the screen when spectating them (<paramref name="hudCam"/>).</summary>
        static void PlaceRounds(ushort owner, View v, Transform body, bool show, Camera hudCam, double renderTick, float now)
        {
            // The count at the render time, so a round leaves the counter as their hand loads it.
            int loose = v.LooseAt(renderTick);
            if (hudCam != null && show && v.RoundPrefab >= 0 && v.ShownLoose > loose)
            {
                // Into the magazine in their hand (it starts its load where the round arrives), or a gun chamber.
                Transform target = null;
                if (v.LoadedMag?.Replica != null && v.LoadedMag.StartLoadPart >= 0) target = v.LoadedMag.Replica.Parts[v.LoadedMag.StartLoadPart];
                else if (v.LoadedMag?.Replica != null) target = v.LoadedMag.Replica.Go.transform;
                else if (v.NewGunRound?.Replica != null) target = v.NewGunRound.Replica.Go.transform;
                // A gun with a built-in magazine (a tube, ...) takes rounds without showing them: unless the round
                // was dropped on the ground, it went into the gun in their hand.
                else if (!DroppedRoundRecently(owner)) target = GunLoadPoint(v);
                if (target != null) StartFlight(v, hudCam, loose, target, now);
            }
            v.ShownLoose = show ? loose : -1;
            UpdateFlights(v, hudCam, now);

            int total = 0;
            if (show)
            {
                total = v.RoundPrefab >= 0 ? loose : 0;
                if (hudCam == null) foreach (var c in v.Carried) total += c.count; // their HUD shows only their own rounds
            }
            int wanted = Math.Min(total, BeltLayout.MaxVisibleRounds);
            v.Rounds.RemoveAll(r => r.Go == null); // destroyed with the scene
            while (v.Rounds.Count > wanted) { v.Rounds[v.Rounds.Count - 1].Destroy(); v.Rounds.RemoveAt(v.Rounds.Count - 1); }
            int index = 0;
            void Add(int prefab, int count)
            {
                for (int i = 0; i < count && index < wanted; i++, index++)
                {
                    if (index >= v.Rounds.Count || v.Rounds[index].Prefab != prefab)
                    {
                        var rep = Replica.Create(prefab, null);
                        if (rep == null) return;
                        if (index < v.Rounds.Count) { v.Rounds[index].Destroy(); v.Rounds[index] = rep; }
                        else v.Rounds.Add(rep);
                    }
                    var rep2 = v.Rounds[index];
                    if (!rep2.Go.activeSelf) rep2.Go.SetActive(true);
                    if (hudCam != null)
                    {
                        var p = BeltLayout.HudRound(hudCam, index);
                        rep2.SetShadows(false);
                        rep2.SetScale(BeltLayout.HudRoundScale);
                        rep2.Go.transform.SetPositionAndRotation(p.position, p.rotation);
                    }
                    else
                    {
                        var local = BeltLayout.Rear(index, wanted);
                        rep2.SetShadows(true);
                        rep2.SetScale(1f);
                        rep2.Go.transform.SetPositionAndRotation(body.TransformPoint(local.position), body.rotation * local.rotation);
                    }
                }
            }
            if (show)
            {
                if (v.RoundPrefab >= 0) Add(v.RoundPrefab, loose);
                if (hudCam == null) foreach (var c in v.Carried) Add(c.prefab, c.count);
            }
        }

        static readonly Dictionary<ushort, float> _roundDroppedAt = new Dictionary<ushort, float>();

        /// <summary>This player dropped a loose round (so their next counter drop isn't a round going into the gun).</summary>
        public static void OnRoundDropped(ushort owner) => _roundDroppedAt[(ushort)(owner & 0x7FFF)] = Time.realtimeSinceStartup;

        static bool DroppedRoundRecently(ushort owner) =>
            _roundDroppedAt.TryGetValue((ushort)(owner & 0x7FFF), out float at) && Time.realtimeSinceStartup - at < 1.2f;

        /// <summary>Where a round goes into the gun in their hand: its built-in magazine's load point, else the gun.</summary>
        static Transform GunLoadPoint(View v)
        {
            foreach (var it in v.Items.Values)
            {
                if (it.Place != HandGun || it.Replica?.Go == null || !it.Replica.Go.activeSelf) continue;
                foreach (var p in it.Replica.Parts)
                    if (p != null && p.name == "point_start_load") return p;
                return it.Replica.Go.transform;
            }
            return null;
        }

        /// <summary>The round that just left the counter (the last one, at <paramref name="index"/>) on its way down.</summary>
        static void StartFlight(View v, Camera hudCam, int index, Transform target, float now)
        {
            var rep = Replica.Create(v.RoundPrefab, null);
            if (rep == null) return;
            rep.SetShadows(false);
            rep.SetScale(BeltLayout.HudRoundScale);
            var from = BeltLayout.HudRound(hudCam, index);
            rep.Go.transform.SetPositionAndRotation(from.position, from.rotation);
            v.Flights.Add(new Flight { Rep = rep, From = from, Target = target, Start = now });
        }

        /// <summary>Flights glide (eased) from the counter to the moving magazine or gun, growing to full size.</summary>
        static void UpdateFlights(View v, Camera hudCam, float now)
        {
            for (int i = v.Flights.Count - 1; i >= 0; i--)
            {
                var f = v.Flights[i];
                float k = (now - f.Start) / FlightSeconds;
                if (hudCam == null || f.Rep?.Go == null || f.Target == null || k >= 1f)
                {
                    f.Rep?.Destroy();
                    v.Flights.RemoveAt(i);
                    continue;
                }
                float e = k * k * (3f - 2f * k);
                f.Rep.Go.transform.SetPositionAndRotation(Vector3.Lerp(f.From.position, f.Target.position, e), Quaternion.Slerp(f.From.rotation, f.Target.rotation, e));
                f.Rep.SetScale(Mathf.Lerp(BeltLayout.HudRoundScale, 1f, e));
            }
        }

        static bool IsGun(int prefab) => PrefabRegistry.TryGet(prefab, out var e) && e.Kind == PrefabKind.Gun;
        static bool IsFlashlight(int prefab) => PrefabRegistry.TryGet(prefab, out var e) && e.Kind == PrefabKind.Flashlight;

        // =====================================================================
        // Pickups (owner): world items we take are gone for everyone
        // =====================================================================

        [HarmonyPatch(typeof(AimScript), nameof(AimScript.HandleGetControl))]
        static class PickupPatch
        {
            static void Prefix(AimScript __instance, out (GameObject mag, GameObject light, int count) __state)
            {
                var list = BeingPickedUp(__instance);
                __state = (MagInHand(__instance), HeldFlashlight(__instance), list != null ? list.Count : 0);
            }

            static void Postfix(AimScript __instance, (GameObject mag, GameObject light, int count) __state)
            {
                if (!WorldSync.InRun || !__instance.main_client_control) return;
                var mag = MagInHand(__instance);
                if (mag != null && mag != __state.mag) ItemSync.OnLocalTake(mag);
                var light = HeldFlashlight(__instance);
                if (light != null && light != __state.light) ItemSync.OnLocalTake(light);
                var list = BeingPickedUp(__instance);
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                        if (list[i] != null && list[i].GetComponent<ItemTag>() != null) ItemSync.OnLocalTake(list[i]);
                CarrySync.PickUpOtherGunsItems(__instance);
            }
        }

        /// <summary>A tape reached the player: share it with the team and revive the dead.</summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.UpdatePickupMagnet))]
        static class TapePatch
        {
            static void Prefix(AimScript __instance, out int __state) => __state = UnplayedTapes(__instance);

            static void Postfix(AimScript __instance, int __state)
            {
                if (!WorldSync.InRun || !__instance.main_client_control) return;
                if (UnplayedTapes(__instance) > __state)
                    LifeSync.OnLocalTapePickup(__instance.transform.position, __instance.transform.localScale.y);
            }
        }
    }
}
