using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReceiverMultiplayer.UI
{
    /// <summary>Builds UI out of the game's own widgets so new screens match the existing menu style.</summary>
    static class UiFactory
    {
        public static Button CloneButton(Button template, Transform parent, string name, string label, UnityAction onClick)
        {
            var go = Object.Instantiate(template.gameObject, parent, false);
            go.name = name;
            go.SetActive(true);

            var button = go.GetComponent<Button>();
            // Replacing the event drops the template's persistent (scene-serialized) listeners.
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(onClick);

            foreach (var t in go.GetComponentsInChildren<Text>(true)) t.text = label;
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = label;
            return button;
        }

        /// <summary>
        /// Place <paramref name="item"/> on the same row as <paramref name="anchor"/>, immediately to its
        /// right, without moving any other widget. A parent layout group is told to ignore the item.
        /// </summary>
        public static void PlaceRightOf(RectTransform item, RectTransform anchor, float gap)
        {
            item.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            item.anchorMin = anchor.anchorMin;
            item.anchorMax = anchor.anchorMax;
            item.pivot = anchor.pivot;
            item.sizeDelta = anchor.sizeDelta;
            item.localRotation = anchor.localRotation;
            item.localScale = anchor.localScale;
            item.anchoredPosition = anchor.anchoredPosition + new Vector2(anchor.rect.width + gap, 0f);

            if (item.parent.GetComponent<LayoutGroup>() != null)
            {
                var le = item.GetComponent<LayoutElement>() ?? item.gameObject.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
            }
        }

        /// <summary>The first legacy Text label on the template button, for copying font, size and colour.</summary>
        public static Text LabelStyleOf(Button template) => template.GetComponentsInChildren<Text>(true).FirstOrDefault();

        public static Text CreateText(Transform parent, string name, string value, Text style, float sizeScale,
            TextAnchor alignment, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            if (style != null)
            {
                text.font = style.font;
                text.fontStyle = style.fontStyle;
                text.fontSize = Mathf.Max(10, Mathf.RoundToInt(style.fontSize * sizeScale));
                text.color = style.color;
                text.lineSpacing = style.lineSpacing;
            }
            else
            {
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                text.fontSize = Mathf.RoundToInt(24 * sizeScale);
                text.color = Color.white;
            }
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            text.text = value;

            var le = go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            return text;
        }

        /// <summary>Copy the look of the game's Options window background; fall back to a dark panel.</summary>
        public static void ApplyWindowBackground(Image target, GameObject styleSource)
        {
            var source = styleSource != null ? styleSource.GetComponent<Image>() : null;
            if (source != null)
            {
                target.sprite = source.sprite;
                target.type = source.type;
                target.color = source.color;
                target.material = source.material;
                target.pixelsPerUnitMultiplier = source.pixelsPerUnitMultiplier;
            }
            else
            {
                target.color = new Color(0f, 0f, 0f, 0.85f);
            }
            target.raycastTarget = true; // block clicks to the menu behind the window
        }
    }
}
