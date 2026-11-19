using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ReceiverMultiplayer.UI
{
    /// <summary>A scrollable window cloned from the game's Options window.</summary>
    sealed class UiWindow
    {
        public GameObject Root;
        public RectTransform Content;
    }

    /// <summary>
    /// Builds new UI out of the pause menu's own widgets (Options window frame, toggle, slider, dropdown,
    /// input field, buttons, labels) so every new screen matches the game. Clones are stripped of the game's
    /// scripts and serialized listeners, so they can never write the game's own settings.
    /// </summary>
    sealed class UiKit
    {
        public optionsmenuscript Menu;
        public Transform WindowParent;
        GameObject _frame;
        Button _button;
        Toggle _toggle;
        Slider _slider;
        Dropdown _dropdown;
        InputField _input;
        Text _header;
        Text _label;

        static readonly System.Reflection.Assembly GameAssembly = typeof(optionsmenuscript).Assembly;

        public static UiKit Create(optionsmenuscript menu, Button fallbackButton, Transform windowParent)
        {
            var kit = new UiKit
            {
                Menu = menu,
                WindowParent = windowParent,
                _frame = menu.menuOptions,
                _button = Find<Button>(menu.menuOptions, "Reset_Settings") ?? fallbackButton,
                _toggle = Find<Toggle>(menu.menuOptions, "invert_mouse"),
                _slider = Find<Slider>(menu.menuOptions, "master_volume"),
                _dropdown = Find<Dropdown>(menu.menuOptions, "graphics_setting"),
                _input = Find<InputField>(menu.menuModifiers, "generation_seed"),
                _header = Find<Text>(menu.menuOptions, "Label Sounds"),
            };
            if (kit._slider != null)
                kit._label = kit._slider.transform.Find("Label")?.GetComponent<Text>();

            var missing = new List<string>();
            if (kit._frame == null) missing.Add("window");
            if (kit._toggle == null) missing.Add("toggle");
            if (kit._slider == null) missing.Add("slider");
            if (kit._dropdown == null) missing.Add("dropdown");
            if (kit._input == null) missing.Add("input field");
            if (kit._header == null || kit._label == null) missing.Add("labels");
            if (missing.Count > 0)
                Plugin.Log.LogWarning("Pause-menu widgets not found: " + string.Join(", ", missing) + ". Some multiplayer UI will be missing.");
            return kit;
        }

        public bool IsComplete => _frame != null && _toggle != null && _slider != null && _dropdown != null && _input != null && _header != null && _label != null;

        static T Find<T>(GameObject root, string name) where T : Component =>
            root == null ? null : root.GetComponentsInChildren<T>(true).FirstOrDefault(c => c.name == name);

        // ---------- windows ----------

        public UiWindow CreateWindow(string name, float width)
        {
            var go = Object.Instantiate(_frame, WindowParent, false);
            go.name = name;
            go.SetActive(false);
            StripGameScripts(go);

            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);

            var scroll = go.GetComponent<ScrollRect>();
            var content = scroll.content;
            for (int i = content.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(content.GetChild(i).gameObject);

            var layout = content.GetComponent<VerticalLayoutGroup>() ?? content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 10, 12);
            layout.spacing = 6f;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = content.GetComponent<ContentSizeFitter>() ?? content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.verticalNormalizedPosition = 1f;

            go.AddComponent<CancelableMenuWindow>(); // Escape closes it, like the game's dialogs
            return new UiWindow { Root = go, Content = content };
        }

        // ---------- widgets ----------

        public Text Header(Transform parent, string text)
        {
            var t = _header != null ? Clone(_header, parent, "Header").GetComponent<Text>() : UiFactory.CreateText(parent, "Header", "", null, 1f, TextAnchor.MiddleLeft, 30f);
            t.text = text;
            t.supportRichText = false;
            Layout(t.gameObject, 30f);
            return t;
        }

        public Text Label(Transform parent, string text, float height = 20f, float width = -1f, TextAnchor alignment = TextAnchor.MiddleLeft)
        {
            var t = _label != null ? Clone(_label, parent, "Label").GetComponent<Text>() : UiFactory.CreateText(parent, "Label", "", null, 0.6f, alignment, height);
            t.text = text;
            t.alignment = alignment;
            t.supportRichText = false; // labels can show player names
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            Layout(t.gameObject, height, width);
            return t;
        }

        public Button Button(Transform parent, string label, UnityAction onClick, float height = 30f, float width = -1f)
        {
            var go = Clone(_button, parent, "Button");
            var b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(onClick);
            SetLabel(go, label);
            Layout(go, height, width);
            return b;
        }

        public static void SetLabel(GameObject go, string label)
        {
            foreach (var t in go.GetComponentsInChildren<Text>(true)) { t.text = label; t.supportRichText = false; }
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = label;
        }

        public Toggle Toggle(Transform parent, string label, bool value, UnityAction<bool> onChange)
        {
            var go = Clone(_toggle, parent, "Toggle");
            var t = go.GetComponent<Toggle>();
            t.onValueChanged = new Toggle.ToggleEvent();
            t.group = null;
            t.SetIsOnWithoutNotify(value);
            t.onValueChanged.AddListener(onChange);
            var text = go.transform.Find("Label")?.GetComponent<Text>();
            if (text != null) { text.text = label; text.supportRichText = false; }
            Layout(go, 22f);
            return t;
        }

        /// <summary>Whole-number slider with a value readout and the game's default marker moved to <paramref name="defaultValue"/>.</summary>
        public Slider Slider(Transform parent, string label, int min, int max, int value, int defaultValue,
            Func<float, string> format, UnityAction<float> onChange)
        {
            var go = Clone(_slider, parent, "Slider");
            var s = go.GetComponent<Slider>();
            s.onValueChanged = new Slider.SliderEvent();
            s.minValue = min;
            s.maxValue = max;
            s.wholeNumbers = true;
            s.SetValueWithoutNotify(value);

            var labelText = go.transform.Find("Label")?.GetComponent<Text>();
            if (labelText != null) labelText.text = label;
            var valueText = go.transform.Find("Value")?.GetComponent<Text>();
            void Show(float v) { if (valueText != null) valueText.text = format(v); }
            Show(value);
            s.onValueChanged.AddListener(v => { Show(v); onChange(v); });

            if (go.transform.Find("Default Marker") is RectTransform marker && max > min)
            {
                // The track is inset 10px each side: centre = 10 + n * (width - 20).
                float n = Mathf.Clamp01((defaultValue - min) / (float)(max - min));
                marker.anchorMin = new Vector2(n, marker.anchorMin.y);
                marker.anchorMax = new Vector2(n, marker.anchorMax.y);
                marker.anchoredPosition = new Vector2(10f - 20f * n, marker.anchoredPosition.y);
            }

            Layout(go, 40f);
            return s;
        }

        public Dropdown Dropdown(Transform parent, IList<string> options, int value, UnityAction<int> onChange, float width = -1f, float height = 30f)
        {
            var go = Clone(_dropdown, parent, "Dropdown");
            var d = go.GetComponent<Dropdown>();
            d.onValueChanged = new Dropdown.DropdownEvent();
            SetOptions(d, options, value);
            d.onValueChanged.AddListener(onChange);
            Layout(go, height, width);
            return d;
        }

        public static void SetOptions(Dropdown d, IList<string> options, int value)
        {
            d.ClearOptions();
            d.AddOptions(options.ToList());
            d.SetValueWithoutNotify(Mathf.Clamp(value, 0, Math.Max(0, options.Count - 1)));
            d.RefreshShownValue();
        }

        public InputField Input(Transform parent, string label, string placeholder, string value,
            InputField.ContentType type, int characterLimit, UnityAction<string> onEndEdit)
        {
            var go = Clone(_input, parent, "Input");
            var f = go.GetComponent<InputField>();
            f.onEndEdit = new InputField.EndEditEvent();
            f.onValueChanged = new InputField.OnChangeEvent();
            f.contentType = type;
            f.characterLimit = characterLimit;
            f.SetTextWithoutNotify(value ?? "");
            if (f.placeholder is Text ph) ph.text = placeholder;
            var labelText = go.transform.Find("Label")?.GetComponent<Text>();
            if (labelText != null) labelText.text = label;
            f.onEndEdit.AddListener(onEndEdit);
            Layout(go, 45f);
            return f;
        }

        public static RectTransform Row(Transform parent, float height, float spacing = 6f)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            Layout(go, height);
            return (RectTransform)go.transform;
        }

        public static RectTransform Column(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<VerticalLayoutGroup>();
            v.spacing = 6f;
            v.childControlWidth = true;
            v.childControlHeight = true;
            v.childForceExpandWidth = true;
            v.childForceExpandHeight = false;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return (RectTransform)go.transform;
        }

        public static LayoutElement Layout(GameObject go, float height, float width = -1f)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.ignoreLayout = false;
            le.minHeight = le.preferredHeight = height;
            if (width > 0f)
            {
                le.minWidth = le.preferredWidth = width;
                le.flexibleWidth = 0f;
            }
            else
            {
                le.flexibleWidth = 1f;
            }
            return le;
        }

        // ---------- cloning ----------

        static GameObject Clone(Component template, Transform parent, string name)
        {
            var go = Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            go.SetActive(true);
            StripGameScripts(go);
            return go;
        }

        /// <summary>Remove every game script (Assembly-CSharp) from a clone, keeping only Unity UI components.</summary>
        static void StripGameScripts(GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Assembly == GameAssembly)
                    Object.DestroyImmediate(mb);
        }
    }
}
