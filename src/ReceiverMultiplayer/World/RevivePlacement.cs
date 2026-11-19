using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Exactly where a revived body goes: inside the teammate who found the tape, feet on their feet. Players
    /// don't collide, and the teammate's body is standing somewhere open, so that spot is safe, as long as the
    /// new body really fits there. It may not: the teammate may be crouched (a standing body there sticks into
    /// the floor), their pose here is a few ticks old, or the floor under them is a moving lift. A body that
    /// starts partly inside the floor gets pushed out sideways by the game's character controller, into the
    /// walls. So the spot is checked against the level first, and the body is stood up only where it fits.
    /// </summary>
    static class RevivePlacement
    {
        /// <summary>Body heights (vertical scale) below this count as crouched: the game crouches to 0.5.</summary>
        public const float CrouchedBelow = 0.75f;

        const float RaiseStep = 0.05f, MaxRaise = 0.6f;
        const float Clearance = 0.03f;  // the test body is this much slimmer, so touching a wall or floor isn't "inside"
        const float FloorProbe = 4f;

        public readonly struct Spot
        {
            public readonly Vector3 Centre; // the body object's position (the controller's centre at (0,0,0))
            public readonly float Height;   // its vertical scale

            public Spot(Vector3 centre, float height)
            {
                Centre = centre;
                Height = height;
            }
        }

        static readonly AccessTools.FieldRef<AimScript, bool> Crouching = AccessTools.FieldRefAccess<AimScript, bool>("crouching");
        static readonly AccessTools.FieldRef<AimScript, Spring> HeightSpring = AccessTools.FieldRefAccess<AimScript, Spring>("height_spring");
        static readonly AccessTools.FieldRef<Spring, float> SpringState = AccessTools.FieldRefAccess<Spring, float>("state");
        static readonly AccessTools.FieldRef<Spring, float> SpringVel = AccessTools.FieldRefAccess<Spring, float>("vel");

        static readonly Collider[] _overlaps = new Collider[32];
        static readonly RaycastHit[] _rays = new RaycastHit[16];

        /// <summary>Move the body there at that height, crouched or not, at rest.</summary>
        public static void Put(AimScript me, Spot spot, bool crouch)
        {
            var t = me.transform;
            var cc = me.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            var scale = t.localScale;
            scale.y = spot.Height;
            t.localScale = scale;
            t.position = spot.Centre;
            if (cc != null) cc.enabled = true;
            Physics.SyncTransforms();
            me.SetVelocity(Vector3.zero);
            // The game eases the body's height toward standing or crouched with this spring; start it where we are.
            Crouching(me) = crouch;
            var spring = HeightSpring(me);
            if (spring != null)
            {
                SpringState(spring) = spot.Height;
                SpringVel(spring) = 0f;
            }
        }

        /// <summary>Something solid under this spot (the level there has been built).</summary>
        public static bool HasFloor(AimScript me, Vector3 centre)
        {
            int n = Physics.RaycastNonAlloc(centre, Vector3.down, _rays, FloorProbe, MaskFor(me), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (Blocks(_rays[i].collider, me)) return true;
            return false;
        }

        /// <summary>
        /// For each of the finder's spots in turn: standing with feet on theirs, else at their crouch height; each
        /// nudged up a little if the floor is in the way. The first one that fits wins.
        /// </summary>
        public static Spot Choose(AimScript me, List<Spot> spots, out string how)
        {
            var body = Body.Of(me);
            int mask = MaskFor(me);
            for (int i = 0; i < spots.Count; i++)
            {
                var finder = spots[i];
                string where = i == 0 && spots.Count > 1 ? "where they are now" : "where they picked it up";

                float feet = finder.Centre.y + body.BottomOffset(finder.Height);
                var standing = new Spot(new Vector3(finder.Centre.x, feet - body.BottomOffset(1f), finder.Centre.z), 1f);
                if (TryRaise(me, body, mask, standing, out var spot, out float raised))
                {
                    how = Describe("standing", where, raised);
                    return spot;
                }
                if (finder.Height < 1f - 0.01f && TryRaise(me, body, mask, finder, out spot, out raised))
                {
                    how = Describe(finder.Height < CrouchedBelow ? "crouched like them" : "at their height", where, raised);
                    return spot;
                }
            }
            how = "no clear spot found, so exactly where they are";
            return spots[0];
        }

        static string Describe(string pose, string where, float raised) =>
            raised > 0f ? $"{pose}, {where}, lifted {raised:0.00} m off the floor" : $"{pose}, {where}";

        /// <summary>The spot if the body fits there, else a little higher (never up through a ceiling).</summary>
        static bool TryRaise(AimScript me, Body body, int mask, Spot at, out Spot found, out float raised)
        {
            for (int step = 0; step * RaiseStep <= MaxRaise + 1e-4f; step++)
            {
                raised = step * RaiseStep;
                if (step > 0 && CeilingWithin(me, mask, at.Centre, raised + body.TopOffset(at.Height))) break;
                var c = at.Centre + Vector3.up * raised;
                if (Fits(me, body, mask, c, at.Height))
                {
                    found = new Spot(c, at.Height);
                    return true;
                }
            }
            found = default;
            raised = 0f;
            return false;
        }

        static bool CeilingWithin(AimScript me, int mask, Vector3 from, float distance)
        {
            int n = Physics.RaycastNonAlloc(from, Vector3.up, _rays, distance, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (Blocks(_rays[i].collider, me)) return true;
            return false;
        }

        static bool Fits(AimScript me, Body body, int mask, Vector3 centre, float height)
        {
            float half = Mathf.Max(body.Height * 0.5f * height, body.Radius);
            var mid = centre + Vector3.up * (body.CentreY * height);
            var seg = Vector3.up * (half - body.Radius);
            int n = Physics.OverlapCapsuleNonAlloc(mid + seg, mid - seg, body.Radius - Clearance, _overlaps, mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (Blocks(_overlaps[i], me)) return false;
            return true;
        }

        /// <summary>Level geometry and other solid things; not us, not other players' bodies, not loose props.</summary>
        static bool Blocks(Collider c, AimScript me)
        {
            if (c == null || c.isTrigger || !c.enabled) return false;
            if (c.transform.IsChildOf(me.transform)) return false;
            if (c.GetComponentInParent<RemotePlayerBody>() != null) return false;
            var rb = c.attachedRigidbody;
            return rb == null || rb.isKinematic;
        }

        /// <summary>Every layer the player's body collides with.</summary>
        static int MaskFor(AimScript me)
        {
            int layer = me.gameObject.layer, mask = 0;
            for (int i = 0; i < 32; i++)
                if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
            return mask;
        }

        /// <summary>The player's character controller, unscaled (height scales with the body; radius doesn't).</summary>
        readonly struct Body
        {
            public readonly float Height, Radius, CentreY;

            Body(float height, float radius, float centreY)
            {
                Height = height;
                Radius = radius;
                CentreY = centreY;
            }

            public static Body Of(AimScript me)
            {
                var cc = me.GetComponent<CharacterController>();
                return cc != null ? new Body(cc.height, cc.radius, cc.center.y) : new Body(0.8f, 0.2f, 0f);
            }

            float Half(float scale) => Mathf.Max(Height * 0.5f * scale, Radius);

            /// <summary>From the body's position to its feet (negative), at this height.</summary>
            public float BottomOffset(float scale) => CentreY * scale - Half(scale);

            /// <summary>From the body's position to the top of its head, at this height.</summary>
            public float TopOffset(float scale) => CentreY * scale + Half(scale);
        }
    }
}
