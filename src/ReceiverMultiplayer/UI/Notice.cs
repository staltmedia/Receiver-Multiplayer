using UnityEngine;

namespace ReceiverMultiplayer.UI
{
    /// <summary>A short line of text near the top of the screen that fades away (drawn over the pause menu too).</summary>
    static class Notice
    {
        const float ShowSeconds = 3.5f, FadeSeconds = 0.8f;

        static string _text;
        static float _shownAt = -100f;
        static GUIStyle _style;

        public static void Show(string text)
        {
            _text = text;
            _shownAt = Time.realtimeSinceStartup;
        }

        public static void OnGUI()
        {
            if (string.IsNullOrEmpty(_text)) return;
            float age = Time.realtimeSinceStartup - _shownAt;
            if (age > ShowSeconds + FadeSeconds) { _text = null; return; }
            float alpha = age <= ShowSeconds ? 1f : 1f - (age - ShowSeconds) / FadeSeconds;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 18, richText = false, wordWrap = true };
            var rect = new Rect(Screen.width * 0.15f, Screen.height * 0.12f, Screen.width * 0.7f, 60f);
            var c = GUI.color;
            int depth = GUI.depth;
            GUI.depth = -1000;
            GUI.color = new Color(0f, 0f, 0f, 0.85f * alpha);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), _text, _style);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(rect, _text, _style);
            GUI.color = c;
            GUI.depth = depth;
        }
    }
}
