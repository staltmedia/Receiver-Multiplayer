using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>Full-screen black overlay that survives scene loads; used for match start and, later, death/respawn.</summary>
    static class ScreenFader
    {
        static Image _image;

        static Image Overlay
        {
            get
            {
                if (_image != null) return _image;
                var go = new GameObject("ReceiverMultiplayer.Fade") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(go);
                var canvas = go.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue; // above the pause menu and HUD
                var child = new GameObject("Black", typeof(RectTransform)) { hideFlags = HideFlags.HideAndDontSave };
                child.transform.SetParent(go.transform, false);
                var rt = (RectTransform)child.transform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                _image = child.AddComponent<Image>();
                _image.color = new Color(0f, 0f, 0f, 0f);
                _image.raycastTarget = false;
                return _image;
            }
        }

        static int _generation;

        public static float Alpha => Overlay.color.a;

        /// <summary>Fade the overlay from where it is now. A newer fade supersedes an older one still running.</summary>
        public static IEnumerator FadeTo(float alpha, float seconds)
        {
            int mine = ++_generation;
            var img = Overlay;
            float start = img.color.a;
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                if (mine != _generation) yield break;
                float k = t / seconds;
                SetAlpha(img, Mathf.Lerp(start, alpha, k * k * (3f - 2f * k))); // ease in and out
                yield return null;
            }
            if (mine == _generation) SetAlpha(img, alpha);
        }

        public static void SetAlpha(float a)
        {
            _generation++; // stops any fade in progress
            SetAlpha(Overlay, a);
        }

        static void SetAlpha(Image img, float a)
        {
            img.color = new Color(0f, 0f, 0f, a);
            img.enabled = a > 0.001f;
        }
    }
}
