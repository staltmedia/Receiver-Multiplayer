using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Magazines made for another player's gun can be handled like your own, so you can top them up and pass
    /// them on: with your hand empty, Pickup takes the nearest one into your off hand (through the game's own
    /// pickup code); Z loads it from the rounds you carry for that gun and R unloads it back into them; it stows
    /// into inventory slots and drops (E) as usual. It never goes into your own gun: an insert is refused and the
    /// magazine eases back into your hand.
    /// </summary>
    static class ForeignMags
    {
        const float PickupRadius = 2f;
        const int PickupLayerMask = 256;

        static readonly AccessTools.FieldRef<AimScript, GameObject> MagazineFilter = AccessTools.FieldRefAccess<AimScript, GameObject>("magazine_obj");
        static readonly AccessTools.FieldRef<AimScript, HandMagStage> MagStage = AccessTools.FieldRefAccess<AimScript, HandMagStage>("mag_stage");
        static readonly Collider[] _hits = new Collider[64];

        /// <summary>The gun-list index this magazine is for, if it isn't for the local player's gun; else -1.</summary>
        public static int ForeignWeapon(GameObject mag)
        {
            if (mag == null || !WorldSync.InRun) return -1;
            int own = GearSync.OwnWeapon();
            int prefab = PrefabRegistry.IndexOf(mag, own);
            if (prefab < 0 || !PrefabRegistry.TryGet(prefab, out var e)) return -1;
            return e.Kind == PrefabKind.Magazine && e.Weapon >= 0 && e.Weapon != own ? e.Weapon : -1;
        }

        static bool IsForeign(GameObject mag) => ForeignWeapon(mag) >= 0;

        // ---------------- pickup into the off hand ----------------

        /// <summary>
        /// The game only picks up magazines named like its own (magazine_obj). With an empty hand, if the nearest
        /// magazine is another gun's, point that filter at its prefab for this one call; the game's own code then
        /// takes it into the hand exactly as it would its own.
        /// </summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.HandleGetControl))]
        static class PickupIntoHand
        {
            static void Prefix(AimScript __instance, out GameObject __state)
            {
                __state = null;
                if (!WorldSync.InRun || !__instance.main_client_control || __instance.main_camera == null) return;
                if (GearSync.MagInHand(__instance) != null || MagStage(__instance) != HandMagStage.EMPTY) return;
                var nearest = NearestMagazine(__instance, out int prefab);
                if (nearest == null || !IsForeign(nearest) || !PrefabRegistry.TryGet(prefab, out var entry)) return;
                __state = MagazineFilter(__instance);
                MagazineFilter(__instance) = entry.Prefab;
            }

            static void Postfix(AimScript __instance, GameObject __state)
            {
                if (__state != null) MagazineFilter(__instance) = __state;
            }

            static GameObject NearestMagazine(AimScript me, out int prefab)
            {
                prefab = -1;
                GameObject best = null;
                float bestDist = float.MaxValue;
                var from = me.main_camera.transform.position;
                int own = GearSync.OwnWeapon();
                int n = Physics.OverlapSphereNonAlloc(from, PickupRadius, _hits, PickupLayerMask);
                for (int i = 0; i < n; i++)
                {
                    var rb = _hits[i].attachedRigidbody;
                    if (rb == null) continue;
                    int p = PrefabRegistry.IndexOf(rb.gameObject, own);
                    if (p < 0 || !PrefabRegistry.TryGet(p, out var e) || e.Kind != PrefabKind.Magazine) continue;
                    float d = (rb.position - from).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = rb.gameObject; prefab = p; }
                }
                return best;
            }
        }

        // ---------------- loading and unloading ----------------

        /// <summary>
        /// The game's Z/R handling (in AimScript.HandleControls) loads the magazine in hand from your own loose
        /// rounds and unloads into them. For another gun's magazine those calls are routed here: Z is left to
        /// <see cref="Frame"/> (which uses the rounds carried for that gun), R puts the round into that carry.
        /// </summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.HandleControls))]
        static class LoadingShims
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var add = AccessTools.Method(typeof(mag_script), nameof(mag_script.AddRound));
                var remove = AccessTools.Method(typeof(mag_script), nameof(mag_script.RemoveRoundAnimated));
                var list = instructions.ToList();
                int adds = 0, removes = 0;
                foreach (var ins in list)
                {
                    if (ins.Calls(add)) { ins.opcode = OpCodes.Call; ins.operand = AccessTools.Method(typeof(ForeignMags), nameof(AddRoundShim)); adds++; }
                    else if (ins.Calls(remove)) { ins.opcode = OpCodes.Call; ins.operand = AccessTools.Method(typeof(ForeignMags), nameof(RemoveRoundShim)); removes++; }
                }
                Plugin.Log.LogInfo($"Magazine loading shims: add {adds}, remove {removes}.");
                return list;
            }
        }

        public static bool AddRoundShim(mag_script mag)
        {
            if (mag != null && IsForeign(mag.gameObject)) return false; // your own rounds don't fit it
            return mag != null && mag.AddRound();
        }

        public static bool RemoveRoundShim(mag_script mag)
        {
            if (mag == null) return false;
            int weapon = ForeignWeapon(mag.gameObject);
            if (weapon < 0) return mag.RemoveRoundAnimated();
            if (mag.RemoveRoundAnimated()) CarrySync.AddCarriedRound(weapon);
            return false; // not into your own loose rounds
        }

        /// <summary>Z with another gun's magazine in hand (gun holstered): load one of the rounds you carry for it.</summary>
        public static void Frame(AimScript me)
        {
            if (me == null || optionsmenuscript.show_menu || LocalPlayer.Dead(me)) return;
            if (GearSync.GunInstance(me) != null || MagStage(me) != HandMagStage.HOLD) return;
            var mag = GearSync.MagInHand(me);
            int weapon = ForeignWeapon(mag);
            if (weapon < 0) return;
            if (!RInput.GetButtonDown(RInput.player.Magazine.InsertRound)) return;
            var script = mag.GetComponent<mag_script>();
            if (script == null || script.IsFull() || !CarrySync.TakeCarriedRound(weapon)) return;
            if (!script.AddRound()) CarrySync.AddCarriedRound(weapon);
        }

        // ---------------- never into your own gun ----------------

        /// <summary>
        /// AimScript.HandleGunControls starts an insert (hold pose → 0, mag_stage = HOLD_TO_INSERT) when Insert
        /// Magazine is pressed with a magazine in hand, and finishes it by calling GunScript.InsertMag and then
        /// emptying the hand without checking the result. So the start itself is guarded: for another gun's
        /// magazine the insert never begins, and the magazine simply stays in your hand.
        /// </summary>
        [HarmonyPatch(typeof(AimScript), nameof(AimScript.HandleGunControls))]
        static class BlockInsertStart
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var stage = AccessTools.Field(typeof(AimScript), "mag_stage");
                var list = instructions.ToList();
                int guards = 0;
                for (int i = 1; i < list.Count; i++)
                {
                    // "mag_stage = HOLD_TO_INSERT" ...
                    if (!(list[i].opcode == OpCodes.Stfld && Equals(list[i].operand, stage) && list[i - 1].opcode == OpCodes.Ldc_I4_1)) continue;
                    // ... is guarded by the nearest conditional branch before it: AND our check into that condition.
                    for (int j = i - 2; j >= 0; j--)
                    {
                        var op = list[j].opcode;
                        if (op != OpCodes.Brfalse && op != OpCodes.Brfalse_S) continue;
                        // Anything that jumped to the branch (with the condition on the stack) now lands on our check.
                        var load = new CodeInstruction(OpCodes.Ldarg_0);
                        load.labels.AddRange(list[j].labels);
                        list[j].labels.Clear();
                        list.InsertRange(j, new[]
                        {
                            load,
                            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ForeignMags), nameof(AllowInsert))),
                        });
                        guards++;
                        i += 2;
                        break;
                    }
                }
                Plugin.Log.LogInfo($"Magazine insert guard: {guards}.");
                return list;
            }
        }

        /// <summary>The insert condition, unless the magazine in hand is for another gun.</summary>
        public static bool AllowInsert(bool condition, AimScript me) =>
            condition && (me == null || !IsForeign(GearSync.MagInHand(me)));

        /// <summary>
        /// Backstop if an insert of another gun's magazine is ever reached anyway: the game empties the hand right
        /// after, so rather than leave the magazine floating, drop it as a normal world item.
        /// </summary>
        [HarmonyPatch(typeof(GunScript), nameof(GunScript.InsertMag))]
        static class RefuseInsert
        {
            static bool Prefix(GameObject mag, ref bool __result)
            {
                if (!IsForeign(mag)) return true;
                __result = false;
                var level = Object.FindObjectOfType<LevelCreatorScript>();
                var parent = level != null ? level.GetPositionTileItemParent(mag.transform.position) : null;
                mag.transform.SetParent(parent, true);
                var rb = mag.GetComponent<Rigidbody>() ?? mag.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = true;
                return false;
            }
        }
    }
}
