#nullable enable
using MusicHistory.Walkthrough;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>Screen shape the HUD is laid out for.</summary>
    public enum ScreenFormat
    {
        /// <summary>Landscape (1920x1080 and the like): today's layout.</summary>
        Horizontal,
        /// <summary>Portrait (1080x1920): graph on top, melody graph, caption and photo, now-playing strip.</summary>
        Vertical
    }

    /// <summary>
    /// Where everything goes for one screen size, in canvas reference pixels (origin bottom-left),
    /// plus the normalized viewports the walkthrough frames songs into. Pure data: computed by
    /// <see cref="ViewerLayout.Compute"/>, used at runtime and by edit-mode validation.
    /// </summary>
    public readonly struct LayoutFrame
    {
        public readonly ScreenFormat Format;
        /// <summary>Canvas size in reference pixels.</summary>
        public readonly Vector2 Canvas;
        /// <summary>Screen pixels per reference pixel.</summary>
        public readonly float Scale;
        public readonly Rect Strip, Melody, Button, Legend, Info;
        /// <summary>
        /// Horizontal: the caption box's widest extent, bottom-anchored (it is centered in x and
        /// grows up). Vertical: the area the caption is centered in (beside the photo or full width).
        /// </summary>
        public readonly Rect CaptionSlot;
        /// <summary>The photo card's width and its tallest extent (bottom-anchored in horizontal, centered in vertical).</summary>
        public readonly Rect CardSlot;
        /// <summary>The narration band (vertical: between the strip and the melody graph; horizontal: caption slot).</summary>
        public readonly Rect Band;
        /// <summary>Normalized viewport a mashup step is framed into (above the melody graph).</summary>
        public readonly Rect MashupView;
        /// <summary>Normalized viewport other tour steps are framed into.</summary>
        public readonly Rect TourView;
        public readonly float CaptionFont, CaptionMinFont, SubjectFont, CreditFont;
        /// <summary>Horizontal only: no room right of the strip, so the card sits beside the caption (at the strip's right edge).</summary>
        public readonly bool CardBesideCaption;
        /// <summary>
        /// The chord wheel's square (<see cref="GraphHud.WheelSize"/>): top right in landscape; top
        /// centre in portrait (centred in the room right of the legend while the legend shows).
        /// </summary>
        public readonly Rect Wheel;
        /// <summary>The playing path's name, top left: its top edge and width (it grows down if it wraps).</summary>
        public readonly Rect PathTitle;
        public readonly float PathTitleFont;

        public LayoutFrame(ScreenFormat format, Vector2 canvas, float scale, Rect strip, Rect melody, Rect button, Rect legend, Rect info,
            Rect captionSlot, Rect cardSlot, Rect band, Rect mashupView, Rect tourView,
            float captionFont, float captionMinFont, float subjectFont, float creditFont, bool cardBesideCaption,
            Rect wheel = default, Rect pathTitle = default, float pathTitleFont = 44f)
        {
            Wheel = wheel;
            PathTitle = pathTitle;
            PathTitleFont = pathTitleFont;
            Format = format;
            Canvas = canvas;
            Scale = scale;
            Strip = strip;
            Melody = melody;
            Button = button;
            Legend = legend;
            Info = info;
            CaptionSlot = captionSlot;
            CardSlot = cardSlot;
            Band = band;
            MashupView = mashupView;
            TourView = tourView;
            CaptionFont = captionFont;
            CaptionMinFont = captionMinFont;
            SubjectFont = subjectFont;
            CreditFont = creditFont;
            CardBesideCaption = cardBesideCaption;
        }

        public bool Vertical => Format == ScreenFormat.Vertical;

        /// <summary>The caption box of <paramref name="size"/> (reference px).</summary>
        public Rect PlaceCaption(Vector2 size)
        {
            float w = Mathf.Min(size.x, CaptionSlot.width), h = Mathf.Min(size.y, CaptionSlot.height);
            float x = CaptionSlot.center.x - w * .5f;
            float y = Vertical ? CaptionSlot.center.y - h * .5f : CaptionSlot.yMin;
            return new Rect(x, y, w, h);
        }

        /// <summary>The photo card of height <paramref name="height"/> (its width is the slot's).</summary>
        public Rect PlaceCard(float height)
        {
            float h = Mathf.Min(height, CardSlot.height);
            float y = Vertical ? CardSlot.center.y - h * .5f : CardSlot.yMin;
            return new Rect(CardSlot.xMin, y, CardSlot.width, h);
        }
    }

    /// <summary>
    /// Lays the HUD out for the screen's shape, at runtime from Screen.width / Screen.height (so it
    /// follows a resized Game view and the Unity Recorder's output size). Horizontal keeps the
    /// 1920x1080 layout. Vertical (portrait, height &gt; 1.2 x width) scales the canvases to a
    /// 1280-wide reference (the 1240-wide strip and melody graph fit with 20 px margins) and stacks,
    /// from the top: the graph view, the melody graph (three times larger than in landscape and
    /// panning under a centred playhead, <see cref="MelodyGraphPanel.PortraitPanelHeight"/>), the
    /// narration band (photo card beside the caption; only while a narrated mix plays, otherwise the
    /// graph sits right above the strip) and the now-playing strip; the Paths button moves to the
    /// top right and the legend / edge card start under it. The chord wheel (2.5 times the old
    /// song card's ring, no backdrop) floats top right in landscape and top centre in portrait (in
    /// the room right of the legend while the legend shows); the songs are framed clear of it. A
    /// playing path's name sits top left in both. <see cref="RecordingMode"/> hides the legend and
    /// the Paths button.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public sealed class ViewerLayout : MonoBehaviour
    {
        /// <summary>Reference width of the vertical canvas (match width).</summary>
        public const float VerticalCanvasWidth = 1280f;
        public const float Margin = 20f;
        public const float StripBottom = 16f;
        /// <summary>Vertical: height of the narration band between the strip and the melody graph.</summary>
        public const float BandHeight = 330f;
        public const float BandGap = 14f;
        /// <summary>Vertical: the photo card's width (the caption takes the rest of the band).</summary>
        public const float VerticalCardWidth = 400f;
        /// <summary>Horizontal: the photo card's width beside the strip (at most).</summary>
        public const float HorizontalCardWidth = 300f;
        public const float HorizontalCaptionHeight = 140f;
        public const float HorizontalCardMaxHeight = 470f;
        /// <summary>Legend / song info top offset below the top edge in the vertical layout (under the Paths button).</summary>
        public const float VerticalPanelTop = 72f;
        /// <summary>Usual height of the legend and the edge card (the vertical framing keeps clear of it).</summary>
        public const float NominalPanelHeight = 330f;
        /// <summary>The path name's largest and smallest type (reference px).</summary>
        public const float PathTitleMax = 46f, PathTitleMin = 26f, PathTitleHeight = 56f;
        /// <summary>The path name's top edge below the screen's top (reference px).</summary>
        public const float PathTitleTop = 12f;

        [Tooltip("Hide the legend and the Paths button (video recording).")]
        public bool RecordingMode;

        public ScreenFormat Format { get; private set; } = ScreenFormat.Horizontal;
        public LayoutFrame Frame { get; private set; }
        public Vector2Int ScreenSize { get; private set; }
        /// <summary>Times the layout changed format (resizes, recorder).</summary>
        public int FormatChanges { get; private set; }

        SongGraphLoader? loader;
        Rect baseTour, baseMashup;
        bool captured, applied, recordingApplied;

        /// <summary>
        /// Lay out for this screen size instead of Screen.width / Screen.height (edit-mode validation
        /// renders portrait frames without a Game view of that size); null = the real screen.
        /// </summary>
        public static Vector2Int? ScreenOverride;

        /// <summary>The screen size the layout follows: <see cref="ScreenOverride"/>, else Screen.width / Screen.height.</summary>
        public static Vector2Int CurrentScreen() =>
            ScreenOverride ?? new Vector2Int(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));

        public static ScreenFormat FormatFor(float width, float height) =>
            height > width * 1.2f ? ScreenFormat.Vertical : ScreenFormat.Horizontal;

        /// <summary>Canvas scale (screen px per reference px) for <paramref name="format"/>, as the CanvasScalers compute it.</summary>
        public static float ScaleFor(float width, float height, ScreenFormat format)
        {
            width = Mathf.Max(1f, width);
            height = Mathf.Max(1f, height);
            if (format == ScreenFormat.Vertical) return width / VerticalCanvasWidth;
            return Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(width / 1920f, 2f), Mathf.Log(height / 1080f, 2f), .5f));
        }

        public static Vector2 CanvasSize(float width, float height, ScreenFormat format)
        {
            float s = ScaleFor(width, height, format);
            return new Vector2(Mathf.Max(1f, width) / s, Mathf.Max(1f, height) / s);
        }

        /// <summary>The walkthrough's default viewports (its inspector values).</summary>
        public static readonly Rect DefaultTourView = new(.14f, .24f, .72f, .46f);
        public static readonly Rect DefaultMashupView = new(.16f, .5f, .68f, .245f);

        /// <summary>
        /// The layout of a <paramref name="width"/> x <paramref name="height"/> screen. <paramref name="melody"/>:
        /// the melody graph shows; <paramref name="photo"/>: a photo card shows (vertical: the caption
        /// moves beside it); <paramref name="narrated"/>: a narrated mashup plays (horizontal: the
        /// songs are framed above the caption). Legend and info heights are their current panel
        /// heights (0 when hidden).
        /// </summary>
        public static LayoutFrame Compute(float width, float height, bool melody, bool photo, bool narrated,
            float legendHeight, float infoHeight, Rect baseTour, Rect baseMashup)
        {
            ScreenFormat f = FormatFor(width, height);
            float scale = ScaleFor(width, height, f);
            Vector2 c = CanvasSize(width, height, f);
            float W = c.x, H = c.y;
            float stripW = FeaturedPathsPanel.StripWidth, stripH = FeaturedPathsPanel.StripHeight;
            float melodyH = MelodyGraphPanel.HeightFor(f == ScreenFormat.Vertical);
            Rect strip = new(W * .5f - stripW * .5f, StripBottom, stripW, stripH);
            float stripTop = strip.yMax;
            float S = GraphHud.WheelSize;

            if (f == ScreenFormat.Vertical)
            {
                Rect band = new(strip.xMin, stripTop + BandGap, stripW, BandHeight);
                // The narration band only while a narrated mix plays; otherwise the graph sits on the strip.
                Rect melodyRect = new(strip.xMin, (narrated ? band.yMax : stripTop) + BandGap, stripW, melodyH);
                Rect button = new(W - Margin - 252f, H - Margin - 40f, 252f, 40f);
                Rect legend = new(Margin, H - VerticalPanelTop - legendHeight, 660f, legendHeight);
                Rect info = new(W - Margin - 540f, H - VerticalPanelTop - infoHeight, 540f, infoHeight);
                // The wheel: top centre, under the Paths button's row; while the legend shows, centred
                // in the room right of it (or under it when there is no room).
                Rect centred = new(W * .5f - S * .5f, H - VerticalPanelTop - S, S, S);
                Rect wheel = centred;
                if (legendHeight > 0 && centred.Overlaps(legend))
                {
                    float room = W - Margin - legend.xMax;
                    wheel = room >= S + 16f
                        ? new Rect(legend.xMax + Mathf.Max(16f, (room - S) * .5f), centred.yMin, S, S)
                        : new Rect(centred.xMin, legend.yMin - 16f - S, S, S);
                }
                Rect title = new(Margin, H - PathTitleTop - PathTitleHeight, Mathf.Max(200f, button.xMin - 24f - Margin), PathTitleHeight);
                Rect card = new(band.xMin, band.yMin, VerticalCardWidth, band.height);
                Rect caption = photo
                    ? new Rect(card.xMax + 24f, band.yMin, band.xMax - card.xMax - 24f, band.height)
                    : band;
                // Under the top panels and the centred wheel at their usual size (not their live
                // height: the framing would move at every song change).
                float top = Mathf.Min(H - VerticalPanelTop - NominalPanelHeight, centred.yMin) - 16f;
                float y0 = (melodyRect.yMax + 30f) / H;
                Rect mashupView = new(.05f, y0, .9f, Mathf.Max(.12f, top / H - y0));
                float tourY0 = ((narrated ? band.yMax : stripTop) + 30f) / H;
                Rect tourView = new(.05f, tourY0, .9f, Mathf.Max(.12f, top / H - tourY0));
                return new LayoutFrame(f, c, scale, strip, melodyRect, button, legend, info, caption, card, band, mashupView, tourView,
                    38f, 28f, 25f, 16f, false, wheel, title, PathTitleMax);
            }
            else
            {
                Rect melodyRect = new(strip.xMin, stripTop + MelodyGraphPanel.Gap, stripW, melodyH);
                Rect button = new(W * .5f - 126f, H - Margin - 40f, 252f, 40f);
                Rect legend = new(Margin, H - Margin - legendHeight, 660f, legendHeight);
                Rect info = new(W - Margin - 540f, H - Margin - infoHeight, 540f, infoHeight);
                // The wheel: top right. The path's name: top left, up to the Paths button.
                Rect wheel = new(W - Margin - S, H - Margin - S, S, S);
                // Short, wide screens (21:9): nudge it right, into the margin, rather than touch the melody graph.
                if (wheel.yMin < melodyRect.yMax + 8f && wheel.xMin < strip.xMax + 8f) wheel.x = Mathf.Min(W - S, strip.xMax + 8f);
                Rect title = new(Margin, H - PathTitleTop - PathTitleHeight, Mathf.Max(200f, button.xMin - 24f - Margin), PathTitleHeight);
                float captionBase = (melody ? melodyRect.yMax : stripTop) + 12f;
                float captionTop = captionBase + HorizontalCaptionHeight;
                // The photo card stays under the wheel's place (always reserved: it shows whenever a mix plays).
                float ceiling = Mathf.Min(infoHeight > 0 ? info.yMin - 12f : H - Margin, wheel.yMin - 12f);
                // The photo card: in the column right of the strip, under the song info; on
                // narrower screens (4:3) beside the caption, at the strip's right edge.
                float colLeft = strip.xMax + Margin, colW = W - Margin - colLeft;
                bool beside = colW < 200f;
                // Beside the caption (narrow screens): at the strip's right edge, under the wheel; at
                // its left edge instead when the wheel leaves too little room (under the legend and
                // the path's name).
                float leftCeiling = Mathf.Min(legendHeight > 0 ? legend.yMin - 12f : H - Margin, title.yMin - 12f);
                bool cardLeft = beside && ceiling - captionBase < 280f && leftCeiling - captionBase > ceiling - captionBase;
                Rect card = beside
                    ? new Rect(cardLeft ? strip.xMin : strip.xMax - HorizontalCardWidth, captionBase, HorizontalCardWidth,
                        Mathf.Max(120f, Mathf.Min(HorizontalCardMaxHeight, (cardLeft ? leftCeiling : ceiling) - captionBase)))
                    : new Rect(W - Margin - Mathf.Min(HorizontalCardWidth, colW), StripBottom, Mathf.Min(HorizontalCardWidth, colW),
                        Mathf.Max(120f, Mathf.Min(HorizontalCardMaxHeight, ceiling - StripBottom)));
                // The caption spans the strip's width, clear of the card beside it and of a legend,
                // edge card or the wheel reaching down to it (wide, short screens).
                float left = cardLeft ? card.xMax + 24f : strip.xMin, right = beside && !cardLeft ? card.xMin - 24f : strip.xMax;
                if (legendHeight > 0 && legend.yMin < captionTop) left = Mathf.Max(left, legend.xMax + 16f);
                if (infoHeight > 0 && info.yMin < captionTop) right = Mathf.Min(right, info.xMin - 16f);
                if (wheel.yMin < captionTop) right = Mathf.Min(right, wheel.xMin - 16f);
                if (right - left < 480f)
                {
                    left = cardLeft ? card.xMax + 24f : strip.xMin;
                    right = beside && !cardLeft ? card.xMin - 24f : strip.xMax;
                }
                Rect caption = new(left, captionBase, right - left, HorizontalCaptionHeight);
                Rect mashupView = baseMashup;
                if (narrated)
                {
                    float y0 = Mathf.Max(baseMashup.yMin, (captionBase + 100f) / H);
                    mashupView = new Rect(baseMashup.xMin, y0, baseMashup.width, Mathf.Max(.15f, baseMashup.yMax + .05f - y0));
                }
                // The songs are framed clear of the wheel (left of it, where the views reach its height).
                Rect tourView = ClearOf(baseTour, wheel, W, H);
                mashupView = ClearOf(mashupView, wheel, W, H);
                return new LayoutFrame(f, c, scale, strip, melodyRect, button, legend, info, caption, card, caption, mashupView, tourView,
                    26f, 20f, 17f, 12.5f, beside, wheel, title, PathTitleMax - 2f);
            }
        }

        /// <summary>
        /// <paramref name="view"/> (normalized) narrowed so its right edge stays left of
        /// <paramref name="obstacle"/> (reference px) when the view reaches up to it.
        /// </summary>
        static Rect ClearOf(Rect view, Rect obstacle, float W, float H)
        {
            if (view.yMax * H <= obstacle.yMin) return view;
            float right = Mathf.Min(view.xMax, (obstacle.xMin - 16f) / W);
            if (right - view.xMin < .2f) return view;
            return Rect.MinMaxRect(view.xMin, view.yMin, right, view.yMax);
        }

        // ------------------------------------------------------------------ runtime

        public void Bind(SongGraphLoader owner)
        {
            loader = owner;
            applied = false;
        }

        void LateUpdate() => Apply(false);

        /// <summary>Lays out for the current screen now (the recorder calls it once the Game view has its output size).</summary>
        public void ApplyNow() => Apply(true);

        void Apply(bool force)
        {
            SongGraphLoader? l = loader;
            if (l == null || l.Hud == null || l.Hud.Canvas == null) return;
            WalkthroughDirector? d = l.Director;
            if (d != null && !captured)
            {
                captured = true;
                baseTour = d.TourViewport;
                baseMashup = d.MashupTourViewport;
            }
            // The path's name and the legend as they will show this frame (the wheel's place depends on the legend).
            l.Hud.RefreshPathTitle();
            Vector2Int screen = CurrentScreen();
            int w = screen.x, h = screen.y;
            bool narrated = l.Narration != null && l.Narration.NarrationOn && l.Narration.CurrentPath != null;
            bool melody = l.MelodyGraph != null && l.MelodyGraph.Showing;
            bool photo = l.NarrationOverlay != null && l.NarrationOverlay.CardShowing;
            // The open paths list takes the legend's place (the portrait wheel keeps clear of either).
            float legendH = l.Hud.LegendVisible ? l.Hud.LegendRect.sizeDelta.y
                : l.Paths != null && l.Paths.ListVisible && l.Paths.ListRect != null ? l.Paths.ListRect.sizeDelta.y : 0f;
            float infoH = l.Hud.EdgeCardVisible ? l.Hud.InfoRect.sizeDelta.y : 0f;
            LayoutFrame frame = Compute(w, h, melody, photo, narrated, legendH, infoH,
                captured ? baseTour : DefaultTourView, captured ? baseMashup : DefaultMashupView);
            Frame = frame;
            bool changed = force || !applied || frame.Format != Format || ScreenSize.x != w || ScreenSize.y != h;
            if (applied && frame.Format != Format) FormatChanges++;
            Format = frame.Format;
            ScreenSize = new Vector2Int(w, h);
            bool vertical = Format == ScreenFormat.Vertical;

            // Rebuilt pieces (a new mashup catalog rebuilds the melody canvas, a new path catalog the
            // Paths button) come back in the horizontal layout: re-apply when they differ.
            bool stale = !ScalerMatches(l.Hud.Canvas, vertical)
                         || l.MelodyGraph != null && l.MelodyGraph.Canvas != null && !ScalerMatches(l.MelodyGraph.Canvas, vertical)
                         || l.Paths != null && l.Paths.ButtonRect is RectTransform pb && !Mathf.Approximately(pb.anchorMin.x, vertical ? 1f : .5f);
            if (changed || stale)
            {
                SetScaler(l.Hud.Canvas, vertical);
                if (l.MelodyGraph != null && l.MelodyGraph.Canvas != null) SetScaler(l.MelodyGraph.Canvas, vertical);
                if (l.Paths != null && l.Paths.ButtonRect is RectTransform b)
                {
                    b.anchorMin = b.anchorMax = vertical ? new Vector2(1f, 1f) : new Vector2(.5f, 1f);
                    b.pivot = vertical ? new Vector2(1f, 1f) : new Vector2(.5f, 1f);
                    b.anchoredPosition = vertical ? new Vector2(-Margin, -Margin) : new Vector2(0f, -Margin);
                }
                float top = vertical ? VerticalPanelTop : Margin;
                l.Hud.LegendRect.anchoredPosition = new Vector2(Margin, -top);
                l.Hud.InfoRect.anchoredPosition = new Vector2(-Margin, -top);
            }
            // The chord wheel and the path's name (the wheel follows the legend in portrait).
            RectTransform wheel = l.Hud.WheelRect;
            if (wheel.anchorMin != Vector2.zero || wheel.pivot != Vector2.zero)
            {
                wheel.anchorMin = wheel.anchorMax = Vector2.zero;
                wheel.pivot = Vector2.zero;
            }
            if (wheel.anchoredPosition != frame.Wheel.position) wheel.anchoredPosition = frame.Wheel.position;
            l.Hud.PlacePathTitle(frame.PathTitle, frame.Canvas.y, frame.PathTitleFont, PathTitleMin);

            // The melody graph's place (it is rebuilt with each mashup catalog: set it every frame it differs).
            if (l.MelodyGraph != null && l.MelodyGraph.PanelRect is RectTransform melodyRect)
            {
                float bottom = vertical ? frame.Melody.yMin : MelodyGraphPanel.PanelBottom;
                if (!Mathf.Approximately(melodyRect.anchoredPosition.y, bottom)) melodyRect.anchoredPosition = new Vector2(0f, bottom);
            }

            // Recording: no legend, no Paths button.
            if (RecordingMode || recordingApplied)
            {
                if (RecordingMode) l.Hud.SetLegendVisible(false);
                else l.Hud.SetLegendVisible(l.Paths == null || !l.Paths.IsOpen);
                if (l.Paths != null && l.Paths.ButtonRect is RectTransform b) UiKit.Show(b, !RecordingMode);
                recordingApplied = RecordingMode;
            }

            // Where the walkthrough frames songs.
            if (d != null)
            {
                Rect before = d.FramingViewport;
                d.TourViewport = frame.TourView;
                d.MashupTourViewport = frame.MashupView;
                if (d.IsTouring && !Approximately(before, d.FramingViewport)) d.Reframe(immediate: force);
            }
            applied = true;
        }

        static bool Approximately(Rect a, Rect b) =>
            Mathf.Abs(a.x - b.x) < 1e-4f && Mathf.Abs(a.y - b.y) < 1e-4f && Mathf.Abs(a.width - b.width) < 1e-4f && Mathf.Abs(a.height - b.height) < 1e-4f;

        static bool ScalerMatches(Canvas canvas, bool vertical)
        {
            CanvasScaler s = canvas.GetComponent<CanvasScaler>();
            if (s == null) return true;
            return vertical
                ? Mathf.Approximately(s.matchWidthOrHeight, 0f) && Mathf.Approximately(s.referenceResolution.x, VerticalCanvasWidth)
                : Mathf.Approximately(s.matchWidthOrHeight, .5f) && Mathf.Approximately(s.referenceResolution.x, 1920f);
        }

        static void SetScaler(Canvas canvas, bool vertical)
        {
            CanvasScaler s = canvas.GetComponent<CanvasScaler>();
            if (s == null) return;
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            Vector2 reference = vertical ? new Vector2(VerticalCanvasWidth, 1080f) : new Vector2(1920f, 1080f);
            float match = vertical ? 0f : .5f;
            if (s.referenceResolution != reference) s.referenceResolution = reference;
            if (!Mathf.Approximately(s.matchWidthOrHeight, match)) s.matchWidthOrHeight = match;
        }
    }
}
