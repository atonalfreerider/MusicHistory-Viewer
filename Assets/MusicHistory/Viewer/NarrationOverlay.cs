#nullable enable
using System.Collections.Generic;
using MusicHistory.Playback;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The narration on screen while a narrated path plays (DESIGN.md §15; captions only, there is no
    /// voiceover): the line as a clean subtitle (our own narration, never lyrics), held for its
    /// reading time (<see cref="NarrationPlayer.CaptionCue"/>), and a photo popup card for the cue's
    /// artist image — the photo, whom it shows and, always, the credit line
    /// "Photo: &lt;author&gt;, &lt;license&gt; (Wikimedia Commons)" under it. Both fade and slide in
    /// and out. Placement comes from <see cref="ViewerLayout.Compute"/>: horizontal, the caption sits
    /// above the melody graph (above the strip when the graph is hidden) and the card in the column
    /// right of the strip; vertical, both share the band between the strip and the melody graph
    /// (card left, caption right). Neither overlaps the melody graph, the now-playing strip or the
    /// featured-paths panel; their rects join the HUD's keep-clear list for labels.
    /// </summary>
    [DefaultExecutionOrder(60)]   // after ViewerLayout (50) set the canvases for this frame
    public sealed class NarrationOverlay : MonoBehaviour
    {
        [Min(.01f)] public float CaptionFadeIn = .22f;
        [Min(.01f)] public float CaptionFadeOut = .18f;
        [Min(.01f)] public float CardFadeIn = .38f;
        [Min(.01f)] public float CardFadeOut = .26f;
        [Tooltip("How long the 'Captions on/off' notice shows after N.")]
        [Min(0f)] public float ToastSeconds = 1.5f;

        static readonly Color CaptionBack = new(.02f, .024f, .03f, .86f);
        static readonly Color CardBack = new(.03f, .035f, .045f, .95f);
        static readonly Color TextColor = new(.96f, .97f, .98f, 1f);
        static readonly Color SubjectColor = new(.94f, .95f, .97f, 1f);
        static readonly Color CreditColor = new(.66f, .70f, .74f, 1f);

        SongGraphLoader loader = null!;
        RectTransform? root;
        RectTransform caption = null!, card = null!, toast = null!;
        CanvasGroup captionGroup = null!, cardGroup = null!, toastGroup = null!;
        TextMeshProUGUI captionText = null!, subject = null!, credit = null!, toastText = null!;
        RawImage photo = null!;
        Image photoFrame = null!;

        string shownLine = "";
        float captionV, cardV, toastV, toastLeft;
        Vector2 captionSize;
        float captionMeasuredWidth = -1f;
        ScreenFormat captionMeasuredFormat;
        ArtistImage? shownImage;
        float cardHeight;
        float cardMeasuredWidth = -1f;
        NarrationPath? preloaded;
        NarrationPlayer? subscribed;

        /// <summary>The line on screen (plain text; "" when none).</summary>
        public string CaptionLine => CaptionShowing ? shownLine : "";
        public bool CaptionShowing => root != null && caption.gameObject.activeSelf && captionV > 0f;
        public bool CardShowing => root != null && card.gameObject.activeSelf && cardV > 0f;
        /// <summary>The photo on the card (null when no card shows).</summary>
        public ArtistImage? ShownImage => CardShowing ? shownImage : null;
        /// <summary>The credit line under the photo (plain text).</summary>
        public string CreditText => CardShowing && shownImage != null ? shownImage.Credit : "";
        public string SubjectText => CardShowing && shownImage != null ? shownImage.Subject : "";
        public RectTransform? CaptionRect => root != null ? caption : null;
        public RectTransform? CardRect => root != null ? card : null;
        /// <summary>The layout used this frame.</summary>
        public LayoutFrame Frame { get; private set; }

        /// <summary>The credit as the card shows it (TMP markup-safe).</summary>
        public static string CreditMarkup(ArtistImage image) => GraphHud.Esc(image.Credit);

        // ------------------------------------------------------------------ build

        public void Build(SongGraphLoader owner)
        {
            loader = owner;
            Discard();
            if (owner.Hud == null || owner.Hud.Canvas == null) return;
            root = UiKit.Rect("Narration", owner.Hud.Canvas.transform);
            UiKit.Fill(root);

            caption = UiKit.Rect("Narration Caption", root);
            caption.gameObject.AddComponent<CanvasRenderer>();
            Image back = caption.gameObject.AddComponent<Image>();
            back.sprite = UiKit.Rounded(16);
            back.type = Image.Type.Sliced;
            back.color = CaptionBack;
            back.raycastTarget = false;
            captionGroup = caption.gameObject.AddComponent<CanvasGroup>();
            captionGroup.blocksRaycasts = false;
            captionGroup.interactable = false;
            captionText = UiKit.Text("Line", caption, 26, TextColor, TextAlignmentOptions.Center, wrap: true);
            captionText.lineSpacing = 2;

            card = UiKit.Rect("Narration Photo Card", root);
            card.gameObject.AddComponent<CanvasRenderer>();
            Image cardBack = card.gameObject.AddComponent<Image>();
            cardBack.sprite = UiKit.Rounded(14);
            cardBack.type = Image.Type.Sliced;
            cardBack.color = CardBack;
            cardBack.raycastTarget = false;
            cardGroup = card.gameObject.AddComponent<CanvasGroup>();
            cardGroup.blocksRaycasts = false;
            cardGroup.interactable = false;
            photoFrame = UiKit.Image("Photo Frame", card, new Color(1f, 1f, 1f, .08f), 6);
            RectTransform photoRect = UiKit.Rect("Photo", card);
            photoRect.gameObject.AddComponent<CanvasRenderer>();
            photo = photoRect.gameObject.AddComponent<RawImage>();
            photo.raycastTarget = false;
            subject = UiKit.Text("Subject", card, 17, SubjectColor, TextAlignmentOptions.TopLeft, bold: true, wrap: true);
            credit = UiKit.Text("Credit", card, 12.5f, CreditColor, TextAlignmentOptions.TopLeft, wrap: true);
            credit.lineSpacing = 1;

            toast = UiKit.Rect("Narration Toast", root);
            toast.gameObject.AddComponent<CanvasRenderer>();
            Image toastBack = toast.gameObject.AddComponent<Image>();
            toastBack.sprite = UiKit.Rounded(12);
            toastBack.type = Image.Type.Sliced;
            toastBack.color = new Color(.03f, .035f, .045f, .92f);
            toastBack.raycastTarget = false;
            toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
            toastGroup.blocksRaycasts = false;
            toastText = UiKit.Text("Label", toast, 15, TextColor, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(toastText.rectTransform);
            toastText.characterSpacing = 4;

            UiKit.Show(caption, false);
            UiKit.Show(card, false);
            UiKit.Show(toast, false);
            shownLine = "";
            shownImage = null;
            captionV = cardV = toastV = toastLeft = 0f;
            captionMeasuredWidth = cardMeasuredWidth = -1f;
            preloaded = null;

            if (subscribed != null) subscribed.Toggled -= OnToggled;
            subscribed = owner.Narration;
            if (subscribed != null) subscribed.Toggled += OnToggled;
        }

        /// <summary>Destroys the overlay's objects (the loader clears or rebuilds; the HUD canvas may already be gone).</summary>
        public void Discard()
        {
            if (root != null)
            {
                if (Application.isPlaying) Destroy(root.gameObject);
                else DestroyImmediate(root.gameObject);
            }
            root = null;
            shownImage = null;
            shownLine = "";
            preloaded = null;
        }

        void OnDestroy()
        {
            if (subscribed != null) subscribed.Toggled -= OnToggled;
            subscribed = null;
        }

        void OnToggled(bool on)
        {
            if (root == null) return;
            toastText.text = on ? "CAPTIONS ON  ·  N" : "CAPTIONS OFF  ·  N";
            toastLeft = ToastSeconds;
        }

        /// <summary>The caption and the card while showing (labels keep clear of them).</summary>
        public void AddVisibleRects(List<RectTransform> into)
        {
            if (root == null) return;
            if (caption.gameObject.activeInHierarchy) into.Add(caption);
            if (card.gameObject.activeInHierarchy) into.Add(card);
        }

        // ------------------------------------------------------------------ per frame

        void LateUpdate() => Refresh(Time.unscaledDeltaTime);

        /// <summary>Follows the narration by <paramref name="dt"/> seconds (public so validation can drive it).</summary>
        public void Refresh(float dt)
        {
            if (root == null || loader == null) return;
            if (loader.Hud == null || loader.Hud.Canvas == null)
            {
                root = null;   // the canvas went with a rebuild
                return;
            }
            NarrationPlayer? player = loader.Narration;
            bool pathPlaying = loader.Paths != null && loader.Paths.State == FeaturedPathsPanel.PanelState.Playing;
            NarrationPath? path = player != null && pathPlaying ? player.CurrentPath : null;
            ArtistImages images = loader.ArtistImages;
            if (!ReferenceEquals(path, preloaded))
            {
                // Decode the path's photos as it starts, not in the middle of a line.
                preloaded = path;
                if (path != null)
                    foreach (NarrationCue c in path.Cues)
                        if (c.HasImage && images.Find(c.Image) is ArtistImage a && a.Showable) images.Texture(a.Id);
            }
            NarrationCue? cue = path != null && player!.NarrationOn ? player.CaptionCue : null;
            string line = cue != null ? cue.Text : "";
            ArtistImage? image = cue != null && cue.HasImage ? images.Find(cue.Image) : null;
            if (image != null && (!image.Showable || images.Texture(image.Id) == null)) image = null;

            float legendH = loader.Hud.LegendVisible ? loader.Hud.LegendRect.sizeDelta.y : 0f;
            float infoH = loader.Hud.EdgeCardVisible ? loader.Hud.InfoRect.sizeDelta.y : 0f;
            bool melody = loader.MelodyGraph != null && loader.MelodyGraph.Showing;
            bool photoNow = image != null || (shownImage != null && cardV > 0f);
            Vector2Int screen = ViewerLayout.CurrentScreen();
            LayoutFrame frame = ViewerLayout.Compute(screen.x, screen.y, melody, photoNow, path != null, legendH, infoH,
                ViewerLayout.DefaultTourView, ViewerLayout.DefaultMashupView);
            Frame = frame;

            // Caption: fade the old line out, then the new one in.
            if (line != shownLine)
            {
                if (shownLine.Length > 0 && captionV > 0f) captionV = Mathf.Max(0f, captionV - dt / CaptionFadeOut);
                if (captionV <= 0f || shownLine.Length == 0)
                {
                    shownLine = line;
                    captionV = 0f;
                    captionMeasuredWidth = -1f;
                }
            }
            else if (shownLine.Length > 0) captionV = Mathf.Min(1f, captionV + dt / CaptionFadeIn);
            bool showCaption = shownLine.Length > 0;
            if (showCaption && (captionMeasuredWidth != frame.CaptionSlot.width || captionMeasuredFormat != frame.Format)) MeasureCaption(frame);
            UiKit.Show(caption, showCaption && captionV > 0f);
            if (showCaption && captionV > 0f)
            {
                float u = DuckEnvelope.Smooth(captionV);
                Rect r = frame.PlaceCaption(captionSize);
                r.y -= (1f - u) * 10f;
                PlaceBottomLeft(caption, r);
                captionGroup.alpha = u;
            }

            // Photo card: out with the old photo, in with the new.
            if (!ReferenceEquals(image, shownImage))
            {
                if (shownImage != null && cardV > 0f) cardV = Mathf.Max(0f, cardV - dt / CardFadeOut);
                if (cardV <= 0f || shownImage == null)
                {
                    shownImage = image;
                    cardV = 0f;
                    cardMeasuredWidth = -1f;
                }
            }
            else if (shownImage != null) cardV = Mathf.Min(1f, cardV + dt / CardFadeIn);
            bool showCard = shownImage != null;
            if (showCard && cardMeasuredWidth != frame.CardSlot.width) LayoutCard(frame);
            UiKit.Show(card, showCard && cardV > 0f);
            if (showCard && cardV > 0f)
            {
                float u = DuckEnvelope.Smooth(cardV);
                Rect r = frame.PlaceCard(cardHeight);
                r.y -= (1f - u) * 24f;
                PlaceBottomLeft(card, r);
                cardGroup.alpha = u;
                float s = Mathf.Lerp(.97f, 1f, u);
                card.localScale = new Vector3(s, s, 1f);
            }

            // "Captions on / off" after N, above the caption's place.
            toastLeft = Mathf.Max(0f, toastLeft - dt);
            toastV = Mathf.MoveTowards(toastV, toastLeft > 0f && pathPlaying ? 1f : 0f, dt / .15f);
            UiKit.Show(toast, toastV > 0f);
            if (toastV > 0f)
            {
                float w = toastText.GetPreferredValues(toastText.text, 1000f, 0f).x + 36f;
                Rect slot = frame.CaptionSlot;
                float y = showCaption && captionV > 0f ? frame.PlaceCaption(captionSize).yMax + 10f : slot.yMin;
                PlaceBottomLeft(toast, new Rect(slot.center.x - w * .5f, y, w, 34f));
                toastGroup.alpha = toastV;
            }
        }

        void MeasureCaption(LayoutFrame frame)
        {
            bool vertical = frame.Vertical;
            float padX = vertical ? 34f : 26f, padY = vertical ? 20f : 14f;
            float maxW = frame.CaptionSlot.width;
            int maxLines = vertical ? 7 : 3;
            string text = GraphHud.Esc(shownLine);
            captionText.text = text;
            float size = frame.CaptionFont;
            // The caption fits its slot: at most maxLines, and never taller than the slot.
            float room = frame.CaptionSlot.height - 2 * padY;
            for (; ; size -= 1f)
            {
                captionText.fontSize = size;
                float h = captionText.GetPreferredValues(text, maxW - 2 * padX, 0f).y;
                if ((h <= maxLines * size * 1.34f + 1f && h <= room + 1f) || size <= frame.CaptionMinFont) break;
            }
            float single = captionText.GetPreferredValues(text, 100000f, 0f).x;
            float w = Mathf.Min(maxW, single + 2 * padX + 2f);
            float height = captionText.GetPreferredValues(text, w - 2 * padX, 0f).y + 2 * padY;
            captionSize = new Vector2(w, Mathf.Min(frame.CaptionSlot.height, Mathf.Ceil(height)));
            RectTransform t = captionText.rectTransform;
            t.anchorMin = Vector2.zero;
            t.anchorMax = Vector2.one;
            t.pivot = new Vector2(.5f, .5f);
            t.offsetMin = new Vector2(padX, padY);
            t.offsetMax = new Vector2(-padX, -padY);
            captionMeasuredWidth = frame.CaptionSlot.width;
            captionMeasuredFormat = frame.Format;
        }

        void LayoutCard(LayoutFrame frame)
        {
            ArtistImage image = shownImage!;
            Texture2D? tex = loader.ArtistImages.Texture(image.Id);
            bool vertical = frame.Vertical;
            float w = frame.CardSlot.width;
            float pad = vertical ? 16f : 12f, inner = w - 2 * pad;
            subject.fontSize = frame.SubjectFont;
            credit.fontSize = frame.CreditFont;
            subject.text = GraphHud.Esc(image.Subject);
            credit.text = CreditMarkup(image);
            float subjectH = image.Subject.Length > 0 ? Mathf.Ceil(subject.GetPreferredValues(subject.text, inner, 0f).y) : 0f;
            float creditH = Mathf.Ceil(credit.GetPreferredValues(credit.text, inner, 0f).y);
            float fixedH = pad + 10f + (subjectH > 0 ? subjectH + 4f : 0f) + creditH + pad;
            float aspect = tex != null && tex.height > 0 ? tex.width / (float)tex.height : 1f;
            float maxPhoto = vertical ? 330f : 300f;
            float photoH = Mathf.Max(48f, Mathf.Min(maxPhoto, inner / Mathf.Max(.2f, aspect), frame.CardSlot.height - fixedH));
            float photoW = Mathf.Min(inner, photoH * aspect);
            photo.texture = tex;
            photo.uvRect = new Rect(0, 0, 1, 1);
            UiKit.Place(photoFrame.rectTransform, pad + (inner - photoW) * .5f - 1f, pad - 1f, photoW + 2f, photoH + 2f);
            UiKit.Place(photo.rectTransform, pad + (inner - photoW) * .5f, pad, photoW, photoH);
            float y = pad + photoH + 10f;
            UiKit.Show(subject, subjectH > 0);
            if (subjectH > 0)
            {
                UiKit.Place(subject.rectTransform, pad, y, inner, subjectH);
                y += subjectH + 4f;
            }
            UiKit.Place(credit.rectTransform, pad, y, inner, creditH);
            cardHeight = Mathf.Ceil(y + creditH + pad);
            card.pivot = Vector2.zero;
            cardMeasuredWidth = w;
        }

        static void PlaceBottomLeft(RectTransform r, Rect rect)
        {
            r.anchorMin = r.anchorMax = Vector2.zero;
            r.pivot = Vector2.zero;
            r.anchoredPosition = rect.position;
            r.sizeDelta = rect.size;
        }
    }
}
