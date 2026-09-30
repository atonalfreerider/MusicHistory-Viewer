#nullable enable
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// Small uGUI building blocks for the HUD: procedural anti-aliased rounded-rect and circle
    /// sprites (no texture assets), and rect/image/text factories laid out from the parent's
    /// top-left corner in reference pixels (1920x1080 canvas).
    /// </summary>
    public static class UiKit
    {
        static readonly Dictionary<int, Sprite> rounded = new();
        static Sprite? circle;

        /// <summary>A 9-sliced white rounded rectangle with corner radius <paramref name="radius"/> px.</summary>
        public static Sprite Rounded(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 64);
            if (rounded.TryGetValue(radius, out Sprite? s) && s != null) return s;
            int size = radius * 2 + 4;
            Texture2D tex = new(size, size, TextureFormat.RGBA32, false)
            {
                name = $"UiKit Rounded {radius}",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color32[] px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fx = x + .5f, fy = y + .5f;
                    float cx = Mathf.Clamp(fx, radius, size - radius), cy = Mathf.Clamp(fy, radius, size - radius);
                    float d = Mathf.Sqrt((fx - cx) * (fx - cx) + (fy - cy) * (fy - cy)) - radius;
                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(.5f - d) * 255f);
                    px[y * size + x] = new Color32(255, 255, 255, a);
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            float border = radius + 1;
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            s.name = tex.name;
            s.hideFlags = HideFlags.DontSave;
            rounded[radius] = s;
            return s;
        }

        /// <summary>A white anti-aliased disc (use with Image.Type.Simple).</summary>
        public static Sprite Circle()
        {
            if (circle != null) return circle;
            const int size = 48;
            Texture2D tex = new(size, size, TextureFormat.RGBA32, false)
            {
                name = "UiKit Circle",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            Color32[] px = new Color32[size * size];
            float r = size * .5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x + .5f - size * .5f, dy = y + .5f - size * .5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) - r;
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(.5f - d) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect);
            circle.name = tex.name;
            circle.hideFlags = HideFlags.DontSave;
            return circle;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            return r;
        }

        /// <summary>Places <paramref name="r"/> at (x, y) from the parent's top-left corner, size w x h.</summary>
        public static void Place(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0, 1);
            r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
        }

        /// <summary>Stretches <paramref name="r"/> over its parent, inset by <paramref name="inset"/>.</summary>
        public static void Fill(RectTransform r, float inset = 0)
        {
            r.anchorMin = Vector2.zero;
            r.anchorMax = Vector2.one;
            r.pivot = new Vector2(.5f, .5f);
            r.offsetMin = new Vector2(inset, inset);
            r.offsetMax = new Vector2(-inset, -inset);
        }

        public static Image Image(string name, Transform parent, Color color, int radius = 0, bool raycast = false)
        {
            RectTransform r = Rect(name, parent);
            r.gameObject.AddComponent<CanvasRenderer>();
            Image image = r.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = raycast;
            if (radius > 0)
            {
                image.sprite = Rounded(radius);
                image.type = UnityEngine.UI.Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 1f;
            }
            return image;
        }

        public static Image Dot(string name, Transform parent, Color color)
        {
            Image image = Image(name, parent, color);
            image.sprite = Circle();
            image.type = UnityEngine.UI.Image.Type.Simple;
            image.preserveAspect = true;
            return image;
        }

        public static TextMeshProUGUI Text(string name, Transform parent, float size, Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft, bool bold = false, bool wrap = false)
        {
            RectTransform r = Rect(name, parent);
            r.gameObject.AddComponent<CanvasRenderer>();
            TextMeshProUGUI t = r.gameObject.AddComponent<TextMeshProUGUI>();
            t.fontSize = size;
            t.color = color;
            t.alignment = alignment;
            t.richText = true;
            t.raycastTarget = false;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            // LiberationSans SDF has no ellipsis glyph: TMP would fall back to Truncate anyway, with a
            // console warning per text. Titles that must stay whole use FitWidth instead.
            t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Truncate;
            t.margin = Vector4.zero;
            return t;
        }

        /// <summary>
        /// One-line text at <paramref name="maxSize"/>, shrunk (not below <paramref name="minSize"/>)
        /// until it fits <paramref name="width"/>; returns the size used.
        /// </summary>
        public static float FitWidth(TMP_Text t, float width, float maxSize, float minSize)
        {
            t.fontSize = maxSize;
            float preferred = t.GetPreferredValues(t.text, 100000f, 0f).x;
            if (preferred > width && preferred > 0f)
                t.fontSize = Mathf.Max(minSize, Mathf.Floor(maxSize * width / preferred * 4f) / 4f);
            return t.fontSize;
        }

        public static void SetText(TMP_Text t, string value)
        {
            if (t.text != value) t.text = value;
        }

        public static void Show(Component? c, bool visible)
        {
            if (c != null && c.gameObject.activeSelf != visible) c.gameObject.SetActive(visible);
        }

        public static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, a);

        public static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
