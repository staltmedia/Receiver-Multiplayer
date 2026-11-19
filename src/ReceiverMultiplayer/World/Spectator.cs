using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using ReceiverMultiplayer.Session;
using ReceiverMultiplayer.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// After dying (once the game's own death fade has gone to black) you watch a living teammate through their
    /// eyes: their camera position and look, their gun in their hands. Click to switch to the next teammate.
    /// The level streams around whoever you watch.
    /// </summary>
    static class Spectator
    {
        const float StartAfterSeconds = 2.6f;  // the death plays out first (the game's own fade is black by ~3.3 s)
        const float FadeOutToSpectate = 0.8f, FadeInToSpectate = 1.0f;
        const float SwitchOut = 0.25f, SwitchIn = 0.4f;

        public static bool Active { get; private set; }
        public static ushort Target { get; private set; }

        /// <summary>A fade-out → change → fade-in is running (no other spectate change until it's done).</summary>
        public static bool Transitioning { get; set; }

        /// <summary>A revive is fading out and back in; spectating transitions in flight give way to it.</summary>
        public static bool Reviving { get; set; }

        static float _time, _deadSince = -1f;
        static int _epoch; // bumped whenever spectating stops; a running transition from an older epoch gives up
        static readonly List<Renderer> _hidden = new List<Renderer>();
        static readonly List<Light> _hiddenLights = new List<Light>();

        static readonly AccessTools.FieldRef<AimScript, float> DeadFade = AccessTools.FieldRefAccess<AimScript, float>("dead_fade");

        public static void Reset()
        {
            if (Active) Stop();
            _epoch++;
            _deadSince = -1f;
            _holdAt = null;
            Transitioning = false;
            Reviving = false;
        }

        public static void Frame(float dt)
        {
            _time += dt;
            var me = LocalPlayer.Get();
            bool dead = WorldSync.InRun && me != null && LocalPlayer.Dead(me);
            if (!dead)
            {
                if (Active) Stop();
                _deadSince = -1f;
                _holdAt = null;
                return;
            }
            me.inputJump = false; // the game lets a dead body jump
            if (_deadSince < 0f) _deadSince = _time;
            HoldBody(me);

            if (Transitioning) return;
            if (!Active)
            {
                if (_time - _deadSince >= StartAfterSeconds && PickNext(-1, out ushort first))
                    Runner.Instance.StartCoroutine(Begin(me, first));
                return;
            }

            // The watched teammate died or left: move on to the next one (nobody left: hold until the restart).
            if (!PlayerSync.IsAlive(Target) && PickNext(Target, out ushort next) && next != Target)
            {
                Runner.Instance.StartCoroutine(Switch(next));
                return;
            }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame && !optionsmenuscript.show_menu
                && PickNext(Target, out ushort other) && other != Target)
                Runner.Instance.StartCoroutine(Switch(other));
        }

        const float HoldAfterSeconds = 3f;
        static Vector3? _holdAt;

        /// <summary>
        /// Once the death has played out, the dead body stays where it lies. Watching a teammate, the level around
        /// it can unload here, and the body then fell forever: tens of kilometres down within minutes, which fed
        /// absurd positions into what this player sent.
        /// </summary>
        static void HoldBody(AimScript me)
        {
            if (_time - _deadSince < HoldAfterSeconds) return;
            var pos = me.transform.position;
            if (_holdAt == null) { _holdAt = pos; return; }
            if ((pos - _holdAt.Value).sqrMagnitude < 0.25f) return;
            var cc = me.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            me.transform.position = _holdAt.Value;
            if (cc != null) cc.enabled = true;
            me.SetVelocity(Vector3.zero);
        }

        static bool PickNext(int after, out ushort id)
        {
            id = 0;
            var alive = PlayerSync.LiveIds();
            if (alive.Count == 0) return false;
            alive.Sort();
            foreach (var x in alive)
                if (x > after) { id = x; return true; }
            id = alive[0];
            return true;
        }

        /// <summary>Death → spectating: fade to black over the end of the death, switch to the teammate's eyes, fade in.</summary>
        static IEnumerator Begin(AimScript me, ushort target)
        {
            int epoch = _epoch;
            Transitioning = true;
            yield return ScreenFader.FadeTo(1f, FadeOutToSpectate);
            if (Reviving) yield break;
            if (epoch != _epoch || me == null || !LocalPlayer.Dead(me)) { Transitioning = false; yield break; }
            if (!PlayerSync.IsAlive(target) && !PickNext(-1, out target)) { Transitioning = false; yield break; }
            Target = target;
            Active = true;
            HideOwnGear(me);
            WipeScreenDecals(me);
            yield return null; // let the camera move to the teammate before the view opens
            Transitioning = false;
            yield return ScreenFader.FadeTo(0f, FadeInToSpectate);
        }

        /// <summary>To another teammate: a quick fade out and in.</summary>
        static IEnumerator Switch(ushort target)
        {
            int epoch = _epoch;
            Transitioning = true;
            yield return ScreenFader.FadeTo(1f, SwitchOut);
            if (Reviving) yield break;
            if (epoch != _epoch || !Active) { Transitioning = false; yield break; }
            Target = target;
            yield return null;
            Transitioning = false;
            yield return ScreenFader.FadeTo(0f, SwitchIn);
        }

        /// <summary>
        /// End of frame while spectating: the game's FlashlightScript switches its lights back on every frame, so
        /// the dead player's own flashlight is kept dark here (after it ran, before rendering); and bullet holes
        /// the game sticks on the camera when the body is hit are cleared.
        /// </summary>
        public static void LateFrame()
        {
            if (!Active) return;
            var me = LocalPlayer.Get();
            if (me == null) return;
            foreach (var l in _hiddenLights) if (l != null && l.enabled) l.enabled = false;
            void Dark(GameObject go)
            {
                if (go == null) return;
                foreach (var l in go.GetComponentsInChildren<Light>(true)) if (l.enabled) l.enabled = false;
            }
            Dark(GearSync.HeldFlashlight(me));
            var slots = GearSync.Slots(me);
            if (slots != null) foreach (var s in slots) if (s != null) Dark(s.obj);
            WipeScreenDecals(me);
        }

        static readonly HashSet<string> _holeNames = new HashSet<string>();

        /// <summary>
        /// When a bullet hits the player, the game parents a bullet hole to their camera (the hole "in your
        /// screen"). They stay on the camera, which now shows a teammate's view, so they're removed.
        /// </summary>
        static void WipeScreenDecals(AimScript me)
        {
            if (me.main_camera == null) return;
            if (_holeNames.Count == 0) CollectHoleNames();
            var cam = me.main_camera.transform;
            for (int i = cam.childCount - 1; i >= 0; i--)
            {
                var child = cam.GetChild(i);
                if (_holeNames.Contains(PrefabRegistry.BaseName(child.name))) Object.Destroy(child.gameObject);
            }
        }

        /// <summary>Names of every bullet-hole prefab the game's bullets (players' and turrets') can leave.</summary>
        static void CollectHoleNames()
        {
            void From(GameObject bulletPrefab)
            {
                var b = bulletPrefab != null ? bulletPrefab.GetComponent<BulletScript>() : null;
                if (b == null) return;
                foreach (var hole in new[] { b.bullet_hole_obj, b.glass_bullet_hole_obj, b.metal_bullet_hole_obj,
                                             b.bullet_hole_decal_obj, b.glass_bullet_hole_decal_obj, b.metal_bullet_hole_decal_obj })
                    if (hole != null) _holeNames.Add(PrefabRegistry.BaseName(hole.name));
            }
            for (int g = 0; g < GunCache.Count; g++) From(GunCache.Get(g)?.bullet_obj);
            var level = Object.FindObjectOfType<LevelCreatorScript>();
            if (level != null && level.turret != null) From(level.turret.GetComponent<RobotScript>()?.bullet_obj);
            if (level != null && level.drone != null) From(level.drone.GetComponent<RobotScript>()?.bullet_obj);
        }

        public static void Stop()
        {
            Active = false;
            _epoch++;
            foreach (var r in _hidden) if (r != null) r.enabled = true;
            foreach (var l in _hiddenLights) if (l != null) l.enabled = true;
            _hidden.Clear();
            _hiddenLights.Clear();
        }

        /// <summary>The dead player's own held and stowed items are drawn relative to the camera; hide them while it's elsewhere.</summary>
        static void HideOwnGear(AimScript me)
        {
            void Hide(GameObject go)
            {
                if (go == null) return;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r.enabled) { r.enabled = false; _hidden.Add(r); }
                foreach (var l in go.GetComponentsInChildren<Light>(true)) if (l.enabled) { l.enabled = false; _hiddenLights.Add(l); }
            }
            Hide(GearSync.GunInstance(me));
            Hide(GearSync.MagInHand(me));
            Hide(GearSync.HeldFlashlight(me));
            var slots = GearSync.Slots(me);
            if (slots != null) foreach (var s in slots) if (s != null) Hide(s.obj);
            if (me.loose_bullets != null) foreach (var b in me.loose_bullets) Hide(b);
        }

        // ---------------- patches ----------------

        static bool Spectating(AimScript a) => Active && a != null && a == LocalPlayer.Get();

        /// <summary>While spectating, the camera follows the teammate instead of the dead body.</summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.UpdateCameraAndPlayerTransformation))]
        static class CameraPatch
        {
            static bool Prefix(AimScript __instance)
            {
                if (!Spectating(__instance) || __instance.main_camera == null) return true;
                if (PlayerSync.TryGetCameraPose(Target, out var pose))
                    __instance.main_camera.transform.SetPositionAndRotation(pose.position, pose.rotation);
                return false;
            }
        }

        /// <summary>Don't pose the dead player's own items in front of the spectator view.</summary>
        [HarmonyPatch(typeof(AimScript))]
        static class HeldItemPatches
        {
            [HarmonyPrefix]
            [HarmonyPatch(nameof(AimScript.UpdateGunTransformation))]
            [HarmonyPatch(nameof(AimScript.UpdateFlashlightTransformation))]
            [HarmonyPatch(nameof(AimScript.UpdateMagazineTransformation))]
            [HarmonyPatch(nameof(AimScript.UpdateInventoryTransformation))]
            [HarmonyPatch(nameof(AimScript.UpdateLooseBulletDisplay))]
            static bool Prefix(AimScript __instance) => !Spectating(__instance);
        }

        /// <summary>No death blackout or muted audio while watching a teammate.</summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.Update))]
        static class UpdatePatch
        {
            static void Postfix(AimScript __instance)
            {
                if (!Spectating(__instance)) return;
                DeadFade(__instance) = 0f;
                AudioListener.volume = Preferences.master_volume;
            }
        }

        // ---------------- HUD ----------------

        static GUIStyle _name, _hint;

        /// <summary>Top right: whose eyes you're looking through (names are display-only, sanitized by the session).</summary>
        public static void OnGUI()
        {
            if (!Active) return;
            if (_name == null)
            {
                _name = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontSize = 20, richText = false };
                _hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontSize = 13, richText = false };
            }
            var player = NetSession.Current?.Players.Find(p => p.Id == Target);
            string name = player != null ? player.Name : "Teammate";
            string hint = PlayerSync.LiveIds().Count > 1 ? "Spectating · click to switch" : "Spectating";
            Shadowed(new Rect(0, 16, Screen.width - 20, 28), name, _name);
            Shadowed(new Rect(0, 44, Screen.width - 20, 22), hint, _hint);
        }

        static void Shadowed(Rect rect, string text, GUIStyle style)
        {
            var c = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, style);
            GUI.color = new Color(1, 1, 1, 0.92f);
            GUI.Label(rect, text, style);
            GUI.color = c;
        }
    }

    /// <summary>
    /// Bringing a dead player back, with everything they died with, inside the teammate who picked up the tape
    /// (players don't collide, and the teammate's body is by definition standing somewhere open).
    /// </summary>
    static class Revival
    {
        const float FloorWaitSeconds = 2.5f;

        static readonly AccessTools.FieldRef<AimScript, float> Health = AccessTools.FieldRefAccess<AimScript, float>("health");
        static readonly AccessTools.FieldRef<AimScript, bool> Dying = AccessTools.FieldRefAccess<AimScript, bool>("dying");
        static readonly AccessTools.FieldRef<AimScript, float> DeadFade = AccessTools.FieldRefAccess<AimScript, float>("dead_fade");
        static readonly AccessTools.FieldRef<AimScript, float> DeadVolumeFade = AccessTools.FieldRefAccess<AimScript, float>("dead_volume_fade");
        static readonly AccessTools.FieldRef<AimScript, float> ViewX = AccessTools.FieldRefAccess<AimScript, float>("view_rotation_x");
        static readonly AccessTools.FieldRef<AimScript, float> ViewY = AccessTools.FieldRefAccess<AimScript, float>("view_rotation_y");
        static readonly AccessTools.FieldRef<AimScript, float> RotX = AccessTools.FieldRefAccess<AimScript, float>("rotation_x");
        static readonly AccessTools.FieldRef<AimScript, float> RotY = AccessTools.FieldRefAccess<AimScript, float>("rotation_y");

        static int _running, _epoch;

        /// <summary>A revive is fading out, moving the body or settling it (the player is still down until it does).</summary>
        public static bool InProgress => _running > 0;

        /// <summary>Shot and bleeding out: the game counts health down for up to a second before the player is down.</summary>
        public static bool IsDying(AimScript me) => me != null && Dying(me) && !LocalPlayer.Dead(me);

        /// <summary>A new run: revives still running from the old one no longer count.</summary>
        public static void Reset()
        {
            _epoch++;
            _running = 0;
        }

        const float JoinWaitSeconds = 10f, JoinFloorWaitSeconds = 10f;

        /// <summary>
        /// Joining a run under way: the level loads at its start, but you start inside a teammate who's alive, placed
        /// as a revive places you. The screen stays black until you're there. With nobody alive to join (or no word
        /// from them within a few seconds) you stay at the start.
        /// </summary>
        public static void JoinAtTeammate() => Runner.Instance.StartCoroutine(JoinRoutine());

        static IEnumerator JoinRoutine()
        {
            int epoch = _epoch;
            var me = LocalPlayer.Get();
            ScreenFader.SetAlpha(1f); // still black from the level loading: stays so until we're in place
            float giveUp = Time.realtimeSinceStartup + JoinWaitSeconds;
            ushort target = 0;
            bool found = false;
            while (epoch == _epoch && me != null && Time.realtimeSinceStartup < giveUp)
            {
                var live = PlayerSync.LiveIds();
                if (live.Count > 0) { live.Sort(); target = live[0]; found = true; break; }
                yield return null;
            }
            if (epoch != _epoch || me == null) yield break; // a new run started meanwhile
            if (found && !LocalPlayer.Dead(me) && PlayerSync.TryGetLivePose(target, out var pos, out var height))
            {
                var spot = new RevivePlacement.Spot(pos, height);
                bool crouch = spot.Height < RevivePlacement.CrouchedBelow;
                RevivePlacement.Put(me, spot, crouch);
                LocalPlayer.MarkTeleported();
                // Just joined, the level around the teammate is usually still being built here (the screen is black
                // meanwhile): hold the body there until there's a floor under it, then make sure it stays up.
                float floorBy = Time.realtimeSinceStartup + JoinFloorWaitSeconds;
                while (me != null && !RevivePlacement.HasFloor(me, spot.Centre) && Time.realtimeSinceStartup < floorBy)
                {
                    yield return null;
                    if (me != null) RevivePlacement.Put(me, spot, crouch);
                }
                bool floor = me != null && RevivePlacement.HasFloor(me, spot.Centre);
                var name = NetSession.Current?.Players.Find(p => p.Id == target)?.Name ?? "a teammate";
                if (me != null) Settle(me, new List<RevivePlacement.Spot> { spot, spot }, $"Joined the run inside {name} at {spot.Centre.ToString("F1")}" + (floor ? "" : ", though the level there hadn't loaded yet"));
                for (float until = Time.realtimeSinceStartup + 1f; me != null && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                    if (me != null && me.transform.position.y < spot.Centre.y - 1.5f) { RevivePlacement.Put(me, spot, crouch); LocalPlayer.MarkTeleported(); }
                }
            }
            else Plugin.Log.LogInfo("Joined the run at its start: no teammate was alive to start beside.");
            yield return ScreenFader.FadeTo(0f, 0.9f);
        }

        /// <param name="finder">The teammate who picked up the tape.</param>
        /// <param name="pos">Their body's centre when they picked it up.</param>
        /// <param name="height">Their crouch height then (body's vertical scale).</param>
        public static void ReviveLocal(ushort finder, Vector3 pos, float height)
        {
            var me = LocalPlayer.Get();
            if (me == null || !LocalPlayer.Dead(me)) return;
            Runner.Instance.StartCoroutine(Routine(me, finder, pos, height));
        }

        /// <summary>Fade out of the spectator view, come back inside the finder, fade in on your own eyes.</summary>
        static IEnumerator Routine(AimScript me, ushort finder, Vector3 pos, float height)
        {
            int epoch = _epoch;
            _running++;
            try
            {
                foreach (var step in Steps(me, finder, pos, height)) yield return step;
            }
            finally
            {
                if (epoch == _epoch && _running > 0) _running--;
            }
        }

        static IEnumerable Steps(AimScript me, ushort finder, Vector3 pos, float height)
        {
            Spectator.Reviving = true;
            Spectator.Transitioning = true;
            yield return ScreenFader.FadeTo(1f, 0.5f);

            // Where the finder is now (as drawn here), or failing that where they picked up the tape. Their body
            // may have moved on since the pickup, or the floor under it (lifts), so "now" goes first.
            var spots = new List<RevivePlacement.Spot>(2);
            if (PlayerSync.TryGetLivePose(finder, out var livePos, out var liveHeight))
                spots.Add(new RevivePlacement.Spot(livePos, liveHeight));
            spots.Add(new RevivePlacement.Spot(pos, height));

            bool revived = me != null && LocalPlayer.Dead(me) && Revive(me, spots[0]);
            if (revived)
            {
                // Watching someone else, the level around the finder may not have been built here yet: hold the
                // body there (still behind the black screen) until there's a floor under it.
                float giveUp = Time.realtimeSinceStartup + FloorWaitSeconds;
                while (me != null && !RevivePlacement.HasFloor(me, spots[0].Centre) && Time.realtimeSinceStartup < giveUp)
                {
                    yield return null;
                    if (me != null) RevivePlacement.Put(me, spots[0], crouch: spots[0].Height < RevivePlacement.CrouchedBelow);
                }
                if (me != null) Settle(me, spots, "Revived inside the teammate who found the tape");
            }
            else if (me != null && LocalPlayer.Dead(me))
                Plugin.Log.LogWarning("A revive didn't bring you back; you're still down (reported to the host again).");
            Spectator.Transitioning = false;
            Spectator.Reviving = false;
            yield return null; // the camera is back on our own eyes before the view opens
            yield return ScreenFader.FadeTo(0f, 0.9f);
        }

        static bool Revive(AimScript me, RevivePlacement.Spot first)
        {
            try
            {
                Spectator.Stop();
                Health(me) = 1f;
                Dying(me) = false;
                me.SetDead(false);
                DeadFade(me) = 0f;
                DeadVolumeFade(me) = 1f;
                ViewY(me) = 0f;
                RotX(me) = ViewX(me);
                RotY(me) = 0f;
                RevivePlacement.Put(me, first, crouch: first.Height < RevivePlacement.CrouchedBelow);
                LocalPlayer.MarkTeleported();
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Revive failed: {e}");
                return false;
            }
        }

        /// <summary>The final spot: standing if there's room, else crouched like the finder, clear of the floor.</summary>
        static void Settle(AimScript me, List<RevivePlacement.Spot> spots, string what)
        {
            try
            {
                var place = RevivePlacement.Choose(me, spots, out string how);
                RevivePlacement.Put(me, place, crouch: place.Height < RevivePlacement.CrouchedBelow);
                LocalPlayer.MarkTeleported();
                Plugin.Log.LogInfo($"{what} ({how}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Revive placement failed: {e}");
            }
        }
    }
}
