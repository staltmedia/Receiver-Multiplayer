using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// A slowly turning, see-through player pill showing your skin, drawn into a RawImage in the menu. It lives on
    /// a hidden stage on layer 31 (which the game's camera never draws), lit and filmed by its own light and
    /// camera, which only run while a preview is on screen.
    /// </summary>
    sealed class SkinPreview : MonoBehaviour
    {
        const int Layer = 31;
        const int Width = 192, Height = 288;
        const float TurnDegreesPerSecond = 40f;
        const float Alpha = 0.6f;

        static GameObject _stage;
        static Transform _pill;
        static Camera _camera;
        static Material _material;
        static RenderTexture _texture;
        static int _shownVersion = -1;
        static int _visible;

        RawImage _image;

        public static SkinPreview Create(Transform parent, float height)
        {
            var go = new GameObject("MP_SkinPreview", typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            le.preferredWidth = height * Width / Height;
            le.flexibleWidth = 0f;
            le.minWidth = le.preferredWidth;
            var p = go.AddComponent<SkinPreview>();
            p._image = go.AddComponent<RawImage>();
            p._image.raycastTarget = false;
            return p;
        }

        void OnEnable()
        {
            EnsureStage();
            _image.texture = _texture;
            _visible++;
            if (_camera != null) _camera.enabled = true;
        }

        void OnDisable()
        {
            _visible = Mathf.Max(0, _visible - 1);
            if (_visible == 0 && _camera != null) _camera.enabled = false;
        }

        void Update()
        {
            if (_pill == null) return;
            if (_image.texture != _texture) _image.texture = _texture;
            _pill.Rotate(0f, TurnDegreesPerSecond * Time.unscaledDeltaTime, 0f, Space.World); // the menu pauses time
            if (_shownVersion != SkinStore.Version)
            {
                _shownVersion = SkinStore.Version;
                _material.mainTexture = SkinStore.Texture; // null: the plain, empty pill
            }
        }

        static void EnsureStage()
        {
            if (_stage != null) return;
            _stage = new GameObject("MP_SkinStage") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(_stage);
            _stage.transform.position = new Vector3(0f, -10000f, 0f);

            var capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule); // the same mesh as the player's pill
            Object.Destroy(capsule.GetComponent<Collider>());
            capsule.name = "Pill";
            capsule.layer = Layer;
            capsule.transform.SetParent(_stage.transform, false);
            capsule.transform.localScale = new Vector3(0.396f, 0.67f, 0.396f);
            _pill = capsule.transform;

            var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Sprites/Default");
            _material = new Material(shader) { color = new Color(1f, 1f, 1f, Alpha) };
            var renderer = capsule.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var lightGo = new GameObject("Light") { layer = Layer };
            lightGo.transform.SetParent(_stage.transform, false);
            lightGo.transform.rotation = Quaternion.Euler(30f, -35f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = UnityEngine.LightType.Directional; // the game has its own LightType
            light.intensity = 1.1f;
            light.cullingMask = 1 << Layer;

            _texture = new RenderTexture(Width, Height, 16, RenderTextureFormat.ARGB32) { name = "MP_SkinPreview" };
            var camGo = new GameObject("Camera");
            camGo.transform.SetParent(_stage.transform, false);
            camGo.transform.localPosition = new Vector3(0f, 0.05f, -3.1f);
            camGo.transform.LookAt(_stage.transform.position + Vector3.up * 0.05f);
            camGo.AddComponent<NoFog>();
            _camera = camGo.AddComponent<Camera>();
            _camera.cullingMask = 1 << Layer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent: the window shows through
            _camera.fieldOfView = 30f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 10f;
            _camera.targetTexture = _texture;
            _camera.enabled = false;
            _shownVersion = -1;
        }

        /// <summary>The level's fog would grey the preview; switch it off just for this camera's render.</summary>
        sealed class NoFog : MonoBehaviour
        {
            bool _fog;
            void OnPreRender() { _fog = RenderSettings.fog; RenderSettings.fog = false; }
            void OnPostRender() => RenderSettings.fog = _fog;
        }
    }
}
