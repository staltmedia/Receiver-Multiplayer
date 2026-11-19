using System.Collections.Generic;
using UnityEngine;

namespace ReceiverMultiplayer.World
{
    /// <summary>
    /// A display-only copy of an item prefab (gun, magazine, round, flashlight): same meshes and materials,
    /// with every game script, collider, rigidbody and sound removed, so it can never act on the world.
    /// Its transforms are listed in a fixed order (prefab hierarchy order) so moving parts can be mirrored by index.
    /// </summary>
    sealed class Replica
    {
        static Transform _staging;

        public int Prefab;
        public GameObject Go;
        public Transform[] Parts;
        public Light[] Lights;
        public Pose[] Rest;
        Vector3 _baseScale = Vector3.one;
        float _scale = 1f;
        bool _shadows = true;
        Renderer[] _renderers;

        // smooth moves between belt and hand
        Pose _from;
        float _blendStart = -100f;
        object _placement;
        const float BlendSeconds = 0.3f;

        public static Replica Create(int prefabIndex, Transform parent)
        {
            if (!PrefabRegistry.TryGet(prefabIndex, out var entry)) return null;
            if (_staging == null)
            {
                var s = new GameObject("MP_ReplicaStaging");
                s.SetActive(false); // children instantiated here don't run Awake/Start
                Object.DontDestroyOnLoad(s);
                s.hideFlags = HideFlags.HideAndDontSave;
                _staging = s.transform;
            }
            var go = Object.Instantiate(entry.Prefab, _staging, false);
            go.name = "MP_" + PrefabRegistry.BaseName(entry.Prefab.name);
            Strip(go);
            go.transform.SetParent(parent, false);
            SetLayerRecursive(go, 0);
            var r = new Replica { Prefab = prefabIndex, Go = go };
            r.Parts = go.GetComponentsInChildren<Transform>(true);
            r.Rest = new Pose[r.Parts.Length];
            for (int i = 0; i < r.Parts.Length; i++) r.Rest[i] = new Pose(r.Parts[i].localPosition, r.Parts[i].localRotation);
            r.Lights = go.GetComponentsInChildren<Light>(true);
            foreach (var l in r.Lights) l.enabled = false;
            r._baseScale = go.transform.localScale;
            r._renderers = go.GetComponentsInChildren<Renderer>(true);
            return r;
        }

        /// <summary>Uniform scale relative to the prefab's own (HUD copies are drawn smaller).</summary>
        public void SetScale(float factor)
        {
            if (Mathf.Approximately(factor, _scale)) return;
            _scale = factor;
            Go.transform.localScale = _baseScale * factor;
        }

        public void SetShadows(bool on)
        {
            if (on == _shadows) return;
            _shadows = on;
            var mode = on ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach (var r in _renderers) if (r != null) r.shadowCastingMode = mode;
        }

        static void Strip(GameObject go)
        {
            // Keep only what draws: meshes, renderers, lights. Scripts go first (they may require other
            // components), then joints, then colliders/audio/etc., then rigidbodies.
            var doomed = new List<Component>();
            foreach (var c in go.GetComponentsInChildren<Component>(true))
                if (c != null && !(c is Transform) && !(c is Renderer) && !(c is MeshFilter) && !(c is Light) && !(c is LensFlare))
                    doomed.Add(c);
            foreach (var c in doomed) if (c is MonoBehaviour) Object.DestroyImmediate(c);
            foreach (var c in doomed) if (c != null && c is Joint) Object.DestroyImmediate(c);
            foreach (var c in doomed) if (c != null && !(c is Rigidbody)) Object.DestroyImmediate(c);
            foreach (var c in doomed) if (c != null) Object.DestroyImmediate(c);
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        }

        /// <summary>Move toward a world pose; when the placement changes (belt ↔ hand), glide there instead of snapping.</summary>
        public void Place(Pose world, object placement, float now)
        {
            var t = Go.transform;
            if (!Equals(placement, _placement))
            {
                if (_placement != null) { _from = new Pose(t.position, t.rotation); _blendStart = now; }
                _placement = placement;
            }
            float k = Mathf.Clamp01((now - _blendStart) / BlendSeconds);
            if (k < 1f)
            {
                k = k * k * (3f - 2f * k);
                t.SetPositionAndRotation(Vector3.Lerp(_from.position, world.position, k), Quaternion.Slerp(_from.rotation, world.rotation, k));
            }
            else
            {
                t.SetPositionAndRotation(world.position, world.rotation);
            }
        }

        public void ResetParts()
        {
            for (int i = 1; i < Parts.Length; i++)
            {
                if (Parts[i] == null) continue;
                Parts[i].localPosition = Rest[i].position;
                Parts[i].localRotation = Rest[i].rotation;
            }
        }

        public void Destroy()
        {
            if (Go != null) Object.Destroy(Go);
        }
    }
}
