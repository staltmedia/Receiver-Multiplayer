using ReceiverMultiplayer.Session;
using UnityEngine;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>
    /// The Multiplayer screen: Host Game / Join Game. Lives inside the pause menu next to the
    /// game's Options window, and carries the game's CancelableMenuWindow so Escape closes it.
    /// </summary>
    sealed class MultiplayerWindow : MonoBehaviour
    {
        const string WindowName = "MP_MultiplayerWindow";
        const float Padding = 24f;
        const float Spacing = 10f;

        Text _status;

        public static MultiplayerWindow Create(optionsmenuscript menuScript, Button template)
        {
            var parent = WindowParent(menuScript);
            var existing = parent.Find(WindowName);
            if (existing != null)
                return existing.GetComponent<MultiplayerWindow>();

            var templateRect = ((RectTransform)template.transform).rect;
            var buttonSize = new Vector2(Mathf.Max(templateRect.width, 160f), Mathf.Max(templateRect.height, 30f));

            // Build inactive so CancelableMenuWindow.OnEnable only runs when the window is actually opened.
            var go = new GameObject(WindowName, typeof(RectTransform));
            go.SetActive(false);
            go.layer = template.gameObject.layer;

            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(buttonSize.x + Padding * 2f + 80f, 0f);

            UiFactory.ApplyWindowBackground(go.AddComponent<Image>(), menuScript.menuOptions);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset((int)Padding, (int)Padding, (int)Padding, (int)Padding);
            layout.spacing = Spacing;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var w = go.AddComponent<MultiplayerWindow>();

            var labelStyle = UiFactory.LabelStyleOf(template);
            UiFactory.CreateText(go.transform, "Title", "Multiplayer", labelStyle, 1.25f, TextAnchor.MiddleCenter, buttonSize.y * 1.25f);
            AddButton(go.transform, template, "MP_HostButton", "Host Game", w.OnHost, buttonSize);
            AddButton(go.transform, template, "MP_JoinButton", "Join Game", w.OnJoin, buttonSize);
            AddButton(go.transform, template, "MP_BackButton", "Back", w.Hide, buttonSize);
            w._status = UiFactory.CreateText(go.transform, "Status", "", labelStyle, 0.7f, TextAnchor.MiddleCenter, buttonSize.y * 1.6f);
            w._status.supportRichText = false;

            Skins.SkinPanel.Build(go.transform,
                (parent, label, onClick, height) =>
                {
                    var b = UiFactory.CloneButton(template, parent, "MP_Skin_" + label, label, onClick);
                    var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
                    le.minHeight = le.preferredHeight = height;
                    le.flexibleWidth = 1f;
                    return b;
                },
                (parent, text, height) =>
                {
                    var t = UiFactory.CreateText(parent, "MP_SkinText", text, labelStyle, 0.75f, TextAnchor.MiddleCenter, height);
                    t.supportRichText = false;
                    return t;
                });

            go.AddComponent<CancelableMenuWindow>();
            return w;
        }

        static void AddButton(Transform parent, Button template, string name, string label,
            UnityEngine.Events.UnityAction onClick, Vector2 size)
        {
            var b = UiFactory.CloneButton(template, parent, name, label, onClick);
            var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = size.y;
            le.preferredWidth = size.x;
        }

        internal static Transform WindowParent(optionsmenuscript menuScript)
        {
            // Sit beside the game's Options window when it is part of the pause menu, so we hide with it.
            var options = menuScript.menuOptions;
            if (options != null && options.transform.parent != null && options.transform.IsChildOf(menuScript.menu.transform))
                return options.transform.parent;
            return menuScript.menu.transform;
        }

        public void Show(string status)
        {
            if (_status != null) _status.text = status ?? "";
            gameObject.SetActive(true);
            transform.SetAsLastSibling();
        }

        public void Hide() => gameObject.SetActive(false);

        void OnHost()
        {
            NetSession.Host(); // opens the waiting room via SessionUi
        }

        void OnJoin()
        {
            SessionUi.OpenJoin();
        }
    }
}
