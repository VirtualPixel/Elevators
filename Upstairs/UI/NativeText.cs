using TMPro;
using UnityEngine;

namespace Upstairs.UI
{
    // Builds text that matches the game HUD by cloning the font and material off a live game UI element
    // (every SemiUI carries a TextMeshProUGUI in `uiText`). We never guess a font asset.
    internal static class NativeText
    {
        private static TMP_FontAsset? _font;
        private static Material? _material;

        public static bool TryCaptureFont()
        {
            if (_font != null)
            {
                return true;
            }

            TextMeshProUGUI? src = GrabSource();
            if (src == null)
            {
                return false;
            }

            _font = src.font;
            _material = src.fontSharedMaterial;
            return _font != null;
        }

        // Runs once per session at most, since the font is cached the moment it resolves.
        private static TextMeshProUGUI? GrabSource()
        {
            if (BigMessageUI.instance != null && BigMessageUI.instance.uiText != null)
            {
                return BigMessageUI.instance.uiText;
            }
            if (HaulUI.instance != null && HaulUI.instance.uiText != null)
            {
                return HaulUI.instance.uiText;
            }
            SemiUI? any = Object.FindObjectOfType<SemiUI>();
            if (any != null && any.uiText != null)
            {
                return any.uiText;
            }
            return Object.FindObjectOfType<TextMeshProUGUI>();
        }

        // A label in the world: a hand-sized world-space canvas carrying the same UI text the HUD
        // uses, since the 3D text component cannot load a font in this build.
        public static Transform CreateWorld(Transform parent, string label, float widthMetres)
        {
            var go = new GameObject("label " + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(100f, 30f);
            go.transform.localScale = Vector3.one * (widthMetres / 100f);
            TextMeshProUGUI text = Create(go.transform, "text", 22f, TextAlignmentOptions.Center);
            text.text = label;
            text.color = new Color(1f, 0.9f, 0.7f);
            RectTransform tr = text.rectTransform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            return go.transform;
        }

        public static TextMeshProUGUI Create(Transform parent, string name, float fontSize, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            TryCaptureFont();
            if (_font != null)
            {
                tmp.font = _font;
            }
            if (_material != null)
            {
                tmp.fontSharedMaterial = _material;
            }
            tmp.fontSize = fontSize;
            tmp.alignment = align;
            tmp.richText = true;
            tmp.raycastTarget = false;
            return tmp;
        }
    }
}
