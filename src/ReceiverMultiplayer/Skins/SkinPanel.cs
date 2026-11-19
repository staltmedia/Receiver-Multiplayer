using System;
using ReceiverMultiplayer.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ReceiverMultiplayer.Skins
{
    /// <summary>
    /// The skin section of the Multiplayer screen and the waiting room: a turning preview of your pill, then
    /// Apply (opens the Windows file picker for a PNG/JPG) and Remove, a switch for showing other players'
    /// skins, and a status line. Built from whichever screen's own widgets are passed in.
    /// </summary>
    static class SkinPanel
    {
        public delegate Button MakeButton(Transform parent, string label, UnityAction onClick, float height);
        public delegate Text MakeLabel(Transform parent, string text, float height);

        public static void Build(Transform parent, MakeButton button, MakeLabel label, float previewHeight = 170f)
        {
            label(parent, "Skin", 26f).alignment = TextAnchor.MiddleCenter;

            var previewRow = Row(parent, previewHeight, TextAnchor.MiddleCenter, false);
            SkinPreview.Create(previewRow, previewHeight);

            var row = Row(parent, 30f, TextAnchor.MiddleCenter, true);
            button(row, "Apply skin…", SkinStore.Choose, 30f);
            button(row, "Remove skin", SkinStore.Remove, 30f);

            Button others = null;
            others = button(parent, OthersLabel(), () =>
            {
                Plugin.ShowOtherSkins.Value = !Plugin.ShowOtherSkins.Value;
                UiKit.SetLabel(others.gameObject, OthersLabel());
                SkinSync.OnShowOthersChanged();
            }, 30f);

            var status = label(parent, "", 34f);
            status.alignment = TextAnchor.MiddleCenter;
            status.gameObject.AddComponent<StatusLine>().Text = status;
        }

        static string OthersLabel() => Plugin.ShowOtherSkins.Value ? "Player skins: Enabled" : "Player skins: Disabled";

        static RectTransform Row(Transform parent, float height, TextAnchor align, bool stretch)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            go.transform.SetParent(parent, false);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6f;
            h.childAlignment = align;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = stretch;
            h.childForceExpandHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = height;
            le.flexibleWidth = 1f;
            return (RectTransform)go.transform;
        }

        sealed class StatusLine : MonoBehaviour
        {
            public Text Text;
            void Update()
            {
                string s = FileDialog.IsOpen ? "Choose an image in the window that opened." : SkinStore.Status;
                if (Text != null && Text.text != s) Text.text = s;
            }
        }
    }
}
