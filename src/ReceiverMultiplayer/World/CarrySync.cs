using System;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Net;
using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Carrying ammo for teammates. Pickup (G) also takes rounds and magazines made for other guns; they're
    /// carried (shown on your belt) but can't be loaded into your gun. Ctrl + mouse wheel brings up the game's own
    /// loose-round display (as picking up a round does) and steps the choice through your rounds, other guns'
    /// rounds and other guns' magazines; the chosen one pops and stays slightly enlarged. The middle mouse
    /// button drops it. The plain wheel stays the game's (revolver cylinder).
    /// </summary>
    static class CarrySync
    {
        const float PickupRadius = 2f;
        const int PickupLayerMask = 256; // the game's pickup layer (8)

        static readonly Dictionary<int, int> _rounds = new Dictionary<int, int>();   // round prefab → count
        static readonly List<(int prefab, int rounds)> _mags = new List<(int, int)>();
        static readonly Collider[] _hits = new Collider[64];
        static readonly HashSet<GameObject> _takenThisCall = new HashSet<GameObject>();
        static readonly HashSet<GameObject> _taken = new HashSet<GameObject>();
        static readonly List<Entry> _entries = new List<Entry>();
        static int _selected;

        enum Kind { OwnRound, Round, Magazine }

        /// <summary>One item in the row: each loose round is its own entry, so the wheel steps round by round.</summary>
        struct Entry
        {
            public Kind Kind;
            public int Prefab;
            public int Index;  // own round: index in loose_bullets; magazine: index in _mags
            public int Count;  // magazine: rounds in it
        }

        public static void Reset()
        {
            _rounds.Clear();
            _mags.Clear();
            _selected = 0;
            foreach (var x in _extras) x.Replica?.Destroy();
            _extras.Clear();
            _lastChosen = -1;
        }

        // ---------------- gear message ----------------

        public static int WriteCarriedMags(NetWriter w, int room)
        {
            int n = 0;
            for (int k = 0; k < _mags.Count && k < 16 && n < room; k++, n++)
                GearSync.WriteSimpleItem(w, (byte)(GearSync.CarryHandleBase + k), _mags[k].prefab, (byte)(GearSync.CarryBase + k), _mags[k].rounds);
            return n;
        }

        public static void WriteCarriedRounds(NetWriter w)
        {
            int groups = 0;
            foreach (var kv in _rounds) if (kv.Value > 0) groups++;
            groups = Math.Min(groups, 8);
            w.U8((byte)groups);
            int written = 0;
            foreach (var kv in _rounds)
            {
                if (kv.Value <= 0 || written >= groups) continue;
                w.U16((ushort)kv.Key);
                w.U8((byte)Math.Min(kv.Value, 255));
                written++;
            }
        }

        public static void ReadCarriedRounds(ref NetReader rd, List<(int prefab, int count)> into)
        {
            into.Clear();
            int n = rd.U8();
            if (n > 8) throw new ProtocolException("too many carried round types");
            for (int i = 0; i < n; i++)
            {
                int prefab = rd.U16();
                int count = rd.U8();
                into.Add((prefab, count)); // unknown prefabs simply aren't drawn
            }
        }

        // ---------------- pickup ----------------

        /// <summary>While Pickup is held: take nearby rounds and magazines made for other guns.</summary>
        public static void PickUpOtherGunsItems(AimScript me)
        {
            if (me.main_camera == null || LocalPlayer.Dead(me)) return;
            int own = GearSync.OwnWeapon();
            int n = Physics.OverlapSphereNonAlloc(me.main_camera.transform.position, PickupRadius, _hits, PickupLayerMask);
            _takenThisCall.Clear();
            _taken.RemoveWhere(g => g == null);
            for (int i = 0; i < n; i++)
            {
                var rb = _hits[i].attachedRigidbody;
                if (rb == null) continue;
                var go = rb.gameObject;
                // An item has several colliders, and Destroy only takes effect at the end of the frame:
                // take each item once.
                if (!_takenThisCall.Add(go) || _taken.Contains(go)) continue;
                // Just taken into the hand by the game this frame (its Rigidbody is destroyed only at frame end).
                if (go == GearSync.MagInHand(me) || go == GearSync.HeldFlashlight(me) || InInventory(go.transform)) continue;
                if (!go.name.EndsWith("(Clone)", StringComparison.Ordinal)) continue;
                int prefab = PrefabRegistry.IndexOf(go, own);
                if (prefab < 0 || !PrefabRegistry.TryGet(prefab, out var entry) || entry.Weapon == own || entry.Weapon < 0) continue;
                if (entry.Kind == PrefabKind.Round || entry.Kind == PrefabKind.FullCasing)
                {
                    int roundPrefab = PrefabRegistry.IndexOf(PrefabKind.Round, entry.Weapon);
                    if (roundPrefab < 0) roundPrefab = prefab;
                    _rounds.TryGetValue(roundPrefab, out int c);
                    _rounds[roundPrefab] = c + 1;
                }
                else if (entry.Kind == PrefabKind.Magazine)
                {
                    if (_mags.Count >= 16) continue;
                    var mag = go.GetComponent<mag_script>();
                    _mags.Add((prefab, mag != null ? mag.NumRounds() : 0));
                }
                else continue;
                ItemSync.OnLocalTake(go);
                _taken.Add(go);
                go.SetActive(false); // gone this frame, not just at the end of it
                Object.Destroy(go);
                ShowAmmo(me, true); // like the game does when you pick up a round
            }
        }

        static bool InInventory(Transform t)
        {
            for (var x = t.parent; x != null; x = x.parent)
                if (x.name == "PlayerInventory") return true;
            return false;
        }

        /// <summary>A round of another gun goes into your carried rounds (unloaded from its magazine).</summary>
        public static void AddCarriedRound(int weapon)
        {
            int prefab = PrefabRegistry.IndexOf(PrefabKind.Round, weapon);
            if (prefab < 0) return;
            _rounds.TryGetValue(prefab, out int c);
            _rounds[prefab] = c + 1;
            var me = LocalPlayer.Get();
            if (me != null) ShowAmmo(me, true);
        }

        /// <summary>Take one carried round of another gun (to load its magazine). False if you have none.</summary>
        public static bool TakeCarriedRound(int weapon)
        {
            int prefab = PrefabRegistry.IndexOf(PrefabKind.Round, weapon);
            if (prefab < 0 || !_rounds.TryGetValue(prefab, out int c) || c <= 0) return false;
            _rounds[prefab] = c - 1;
            var me = LocalPlayer.Get();
            if (me != null) ShowAmmo(me, false);
            return true;
        }

        // ---------------- choosing and dropping ----------------

        /// <summary>The row in display order: your loose rounds, then other guns' rounds, then their magazines.</summary>
        static void Rebuild(AimScript me)
        {
            _entries.Clear();
            int own = GearSync.OwnWeapon();
            int ownPrefab = PrefabRegistry.IndexOf(PrefabKind.Round, own);
            int ownRounds = me.loose_bullets != null ? me.loose_bullets.Count : 0;
            for (int i = 0; i < ownRounds; i++)
                _entries.Add(new Entry { Kind = Kind.OwnRound, Prefab = ownPrefab, Index = i });
            foreach (var kv in _rounds)
                for (int i = 0; i < kv.Value; i++)
                    _entries.Add(new Entry { Kind = Kind.Round, Prefab = kv.Key, Index = i });
            for (int k = 0; k < _mags.Count; k++)
                _entries.Add(new Entry { Kind = Kind.Magazine, Prefab = _mags[k].prefab, Count = _mags[k].rounds, Index = k });
            _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _entries.Count - 1));
        }

        /// <summary>Every frame in a run (local player alive, menu closed).</summary>
        public static void Frame(AimScript me)
        {
            if (me == null || LocalPlayer.Dead(me) || optionsmenuscript.show_menu) return;
            var mouse = Mouse.current;
            if (mouse == null) return;

            // Ctrl + wheel: the plain wheel stays the game's (it spins a revolver's cylinder).
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && CtrlHeld())
            {
                Rebuild(me);
                // Wheel down steps right along the row, wheel up steps left; it stops at the ends.
                if (_entries.Count > 0)
                    _selected = Mathf.Clamp(_selected + (scroll < 0f ? 1 : -1), 0, _entries.Count - 1);
                ShowAmmo(me, true); // even with nothing to choose: scrolling is how you check your rounds
            }

            if (mouse.middleButton.wasPressedThisFrame)
            {
                Rebuild(me);
                if (_entries.Count > 0) Drop(me, _entries[_selected]);
                Rebuild(me);
                ShowAmmo(me, true);
            }
        }

        /// <summary>Bring up the game's own loose-round display, exactly as picking up a round does (2 s).</summary>
        static void ShowAmmo(AimScript me, bool pop)
        {
            PickedUpDelay(me) = 2f;
            if (pop) _popPending = true;
        }

        public static bool CtrlHeld()
        {
            var kb = Keyboard.current;
            return kb != null && kb.ctrlKey.isPressed;
        }

        /// <summary>While Ctrl is held the wheel picks ammo, so it doesn't also spin the cylinder.</summary>
        [HarmonyPatch(typeof(GunScript), nameof(GunScript.RotateCylinder))]
        static class NoCylinderSpinWithCtrl
        {
            static bool Prefix() => !(WorldSync.InRun && CtrlHeld());
        }

        static void Drop(AimScript me, Entry e)
        {
            var cam = me.main_camera != null ? me.main_camera.transform : me.transform;
            var pos = cam.position + cam.forward * 0.35f - Vector3.up * 0.15f;
            var vel = cam.forward * 1.2f + Vector3.up * 0.5f;
            switch (e.Kind)
            {
                case Kind.OwnRound:
                {
                    int i = e.Index;
                    if (i < 0 || i >= me.loose_bullets.Count) return;
                    var hud = me.loose_bullets[i];
                    me.loose_bullets.RemoveAt(i);
                    var springs = LooseSprings(me);
                    if (springs != null && springs.Count > i) springs.RemoveAt(i);
                    if (hud != null) Object.Destroy(hud);
                    SpawnDropped(e.Prefab, pos, vel, -1);
                    break;
                }
                case Kind.Round:
                    _rounds.TryGetValue(e.Prefab, out int left);
                    _rounds[e.Prefab] = Math.Max(0, left - 1);
                    SpawnDropped(e.Prefab, pos, vel, -1);
                    break;
                case Kind.Magazine:
                    if (e.Index < 0 || e.Index >= _mags.Count) return;
                    _mags.RemoveAt(e.Index);
                    SpawnDropped(e.Prefab, pos, vel, e.Count);
                    break;
            }
        }

        static void SpawnDropped(int prefab, Vector3 pos, Vector3 vel, int magRounds)
        {
            if (!PrefabRegistry.TryGet(prefab, out var entry)) return;
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            var go = Object.Instantiate(entry.Prefab, pos, UnityEngine.Random.rotation);
            var parent = level != null ? level.GetPositionTileItemParent(pos) : null;
            if (parent != null) go.transform.SetParent(parent, true);
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.velocity = vel;
            ItemSync.OnLocalRelease(go, prefab, magRounds);
        }

        // ---------------- the ammo display: the game's own loose-round row, top left ----------------
        //
        // The game shows your loose rounds in a row at the top left when you pick one up: a 2-second timer
        // (picked_up_bullet_delay) slides the row in on show_bullet_spring, and each round has a spring at 0.3
        // scale that a new pickup kicks (vel 3) so it pops bigger and settles. Scrolling uses exactly that:
        // it sets the same timer and pops the chosen round, which stays a little enlarged while the row shows.
        // Rounds and magazines of other guns you carry continue the same row, with the same layout and springs.

        const float NormalScale = 0.3f, ChosenScale = 0.39f, Kick = 3f;
        const float RoundStep = 0.006f;   // the game's spacing between rounds
        const int MagSteps = 5;           // a magazine takes the room of five rounds

        static readonly AccessTools.FieldRef<AimScript, float> PickedUpDelay = AccessTools.FieldRefAccess<AimScript, float>("picked_up_bullet_delay");
        static readonly AccessTools.FieldRef<AimScript, Spring> ShowSpring = AccessTools.FieldRefAccess<AimScript, Spring>("show_bullet_spring");
        static readonly AccessTools.FieldRef<AimScript, List<Spring>> LooseSprings = AccessTools.FieldRefAccess<AimScript, List<Spring>>("loose_bullet_spring");

        sealed class Extra
        {
            public Replica Replica;
            public Spring Spring = new Spring(NormalScale, NormalScale, 100f, 1e-5f);
        }

        static readonly List<Extra> _extras = new List<Extra>();
        static readonly List<(int prefab, bool mag, int entry)> _layout = new List<(int, bool, int)>();
        static bool _popPending;
        static int _lastChosen = -1;

        public static void HidePicker()
        {
            foreach (var x in _extras) if (x.Replica?.Go != null && x.Replica.Go.activeSelf) x.Replica.Go.SetActive(false);
        }

        public static void LateFrame(AimScript me)
        {
            var cam = Camera.main;
            if (me == null || cam == null || me.main_camera == null || LocalPlayer.Dead(me) || Spectator.Active)
            {
                HidePicker();
                return;
            }
            var springs = LooseSprings(me);
            var show = ShowSpring(me);
            bool showing = show != null && (show.state > 0.02f || PickedUpDelay(me) > 0f);
            Rebuild(me);

            // Which item is "chosen": the round that would drop next (the last of its group), or the magazine.
            int ownCount = me.loose_bullets != null ? me.loose_bullets.Count : 0;
            int chosenOwn = -1, chosenExtra = -1;
            _layout.Clear();
            for (int g = 0; g < _entries.Count; g++)
            {
                var e = _entries[g];
                if (e.Kind == Kind.OwnRound) { if (g == _selected) chosenOwn = e.Index; continue; }
                if (g == _selected) chosenExtra = _layout.Count;
                _layout.Add((e.Prefab, e.Kind == Kind.Magazine, g));
            }
            int chosenKey = _entries.Count == 0 ? -1 : _selected;
            bool pop = _popPending || chosenKey != _lastChosen;
            _popPending = false;
            _lastChosen = chosenKey;

            // Your own rounds: the game draws them; we only hold the chosen one a little larger, and pop it.
            if (springs != null)
                for (int i = 0; i < springs.Count; i++)
                {
                    bool chosen = showing && i == chosenOwn;
                    springs[i].target_state = chosen ? ChosenScale : NormalScale;
                    if (chosen && pop) springs[i].vel = Kick;
                }

            // Carried items of other guns: same row, same formula as AimScript.UpdateLooseBulletDisplay.
            _extras.RemoveAll(x => x.Replica == null || x.Replica.Go == null);
            var camT = me.main_camera.transform;
            var rot = camT.rotation;
            var dir = cam.ScreenPointToRay(new Vector3(0f, cam.pixelHeight, 0f)).direction;
            float slide = show != null ? show.state : 0f;
            int step = ownCount;
            for (int k = 0; k < _layout.Count; k++)
            {
                var (prefab, mag, entry) = _layout[k];
                if (k >= _extras.Count || _extras[k].Replica.Prefab != prefab)
                {
                    var rep = Replica.Create(prefab, null);
                    if (rep == null) continue;
                    rep.SetShadows(false);
                    var x = new Extra { Replica = rep };
                    if (k < _extras.Count) { _extras[k].Replica.Destroy(); _extras[k] = x; }
                    else _extras.Add(x);
                }
                var ex = _extras[k];
                bool chosen = showing && k == chosenExtra;
                ex.Spring.target_state = chosen ? ChosenScale : NormalScale;
                if (chosen && pop) ex.Spring.vel = Kick;
                ex.Spring.Update();

                var go = ex.Replica.Go;
                bool visible = slide > 0.02f;
                if (go.activeSelf != visible) go.SetActive(visible);
                if (!visible) { step += mag ? MagSteps : 1; continue; }
                float offset = RoundStep * (step + (mag ? MagSteps * 0.5f : 0f));
                var pos = camT.position + dir * 0.3f
                          + rot * new Vector3(0.02f, -0.01f, 0f)
                          + rot * new Vector3(offset, 0f, 0f)
                          + rot * new Vector3(-0.03f, 0.03f, 0f) * (1f - slide);
                var orient = mag ? rot * Quaternion.AngleAxis(90f, Vector3.up) : rot * Quaternion.AngleAxis(90f, new Vector3(-1f, 0f, 0f));
                go.transform.SetPositionAndRotation(pos, orient);
                ex.Replica.SetScale(ex.Spring.state); // 0.3 normally, like the game's rounds (and its inventory items)
                step += mag ? MagSteps : 1;
            }
            for (int k = _layout.Count; k < _extras.Count; k++)
                if (_extras[k].Replica.Go.activeSelf) _extras[k].Replica.Go.SetActive(false);
        }
    }
}
