using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// Where a remote player's carried items sit on their pill, in the pill's local space (+z forward, +x right,
    /// origin at the player's centre). Belt at the waist: holstered gun on the right hip pointing straight down,
    /// magazines and the flashlight down the left side in inventory-slot order, loose rounds across the back.
    /// </summary>
    static class BeltLayout
    {
        public const float WaistY = 0.12f;        // ~0.52 m above the feet
        public const float Radius = 0.215f;       // just outside the pill's surface

        /// <summary>Holster on the right hip: muzzle down, top of the slide facing forward, grip toward the back.</summary>
        public static Pose Holster => new Pose(OnRing(95f, -0.02f), Quaternion.LookRotation(Vector3.down, Vector3.forward));

        /// <summary>Left side, front to back by slot: magazines stand feed-lips up; the flashlight hangs lens down.</summary>
        public static Pose LeftSide(int slotIndex, int slotCount, bool flashlight)
        {
            int n = Mathf.Max(1, slotCount);
            float t = n == 1 ? 0.5f : slotIndex / (float)(n - 1);
            float angle = Mathf.Lerp(-55f, -135f, t); // left-front to left-back
            var pos = OnRing(angle, flashlight ? -0.04f : -0.01f);
            var outward = Outward(angle);
            var rot = flashlight
                ? Quaternion.LookRotation(Vector3.down, outward)          // lens down
                : Quaternion.LookRotation(Tangent(angle), Vector3.up);    // standing, edge-on along the belt
            return new Pose(pos, rot);
        }

        /// <summary>Loose rounds across the back in a pouch grid, standing upright, packed close.</summary>
        public static Pose Rear(int roundIndex, int roundCount)
        {
            const int perRow = 14;
            int row = roundIndex / perRow, col = roundIndex % perRow;
            int inRow = Mathf.Min(perRow, roundCount - row * perRow);
            float spread = 6.5f; // degrees between rounds (~2.4 cm at the belt)
            float angle = 180f + (col - (inRow - 1) * 0.5f) * spread;
            var pos = OnRing(angle, 0.015f - row * 0.026f) + Outward(angle) * 0.01f;
            return new Pose(pos, Quaternion.LookRotation(Vector3.up, Outward(angle))); // tip up
        }

        public const int MaxVisibleRounds = 42;

        static Vector3 OnRing(float angleDeg, float dy)
        {
            var o = Outward(angleDeg);
            return new Vector3(o.x * Radius, WaistY + dy, o.z * Radius);
        }

        /// <summary>Angle measured clockwise from forward when seen from above (90 = right, -90 = left, 180 = back).</summary>
        static Vector3 Outward(float angleDeg)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
        }

        static Vector3 Tangent(float angleDeg)
        {
            float a = angleDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(a), 0f, -Mathf.Sin(a));
        }

        // ---- the spectator's view of the watched player's inventory, placed like the game's own HUD ----

        /// <summary>Inventory slot i along the bottom of the screen (as AimScript.UpdateInventoryTransformation does), 0.3 scale.</summary>
        public static Pose HudSlot(Camera cam, int slot)
        {
            var ray = cam.ScreenPointToRay(new Vector3(cam.pixelWidth * (0.05f + 0.15f * slot), cam.pixelHeight * 0.17f, 0f));
            var rot = cam.transform.rotation;
            return new Pose(ray.GetPoint(0.3f), rot * Quaternion.AngleAxis(90f, Vector3.up));
        }

        /// <summary>Loose round i in the top-left corner (as AimScript.UpdateLooseBulletDisplay does).</summary>
        public static Pose HudRound(Camera cam, int index)
        {
            var dir = cam.ScreenPointToRay(new Vector3(0f, cam.pixelHeight, 0f)).direction;
            var rot = cam.transform.rotation;
            var pos = cam.transform.position + dir * 0.3f + rot * new Vector3(0.02f + 0.006f * index, -0.01f, 0f);
            return new Pose(pos, rot * Quaternion.AngleAxis(90f, Vector3.left));
        }

        public const float HudRoundScale = 0.3f; // the game's loose-round spring rests at 0.3

        public const float HudScale = 0.3f;
    }
}
