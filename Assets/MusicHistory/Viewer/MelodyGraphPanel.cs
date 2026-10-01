#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MusicHistory.Playback;
using MusicHistory.Walkthrough;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The melody graph shown while a featured path plays its mashup mix or its duet loop (M or the
    /// strip's "Melody" button toggles it). y is the sung pitch in the normalized C major / A minor
    /// frame (note-name ticks). Every song's melody of the path is drawn on top of the others in its
    /// own muted colour (legend: title, year); the melodies whose vocal is audible right now are
    /// bright and thicker, each with a point of bloom light riding on it at the playhead and the
    /// melody's pitch there. Along the bottom, the chord timeline: one cell per chord in the chord
    /// colours (<see cref="ChordPalette"/>, Resonance's tonal colours against the key's tonic), named
    /// by what it is to the key ("Key", "Fifth", "Minor 3rd" ...); the chord sounding keeps its full
    /// colour and is lit with bloom (a glow shape on <see cref="MelodyLightRig"/>), every other chord
    /// is muted. During a changeover a thin strip of the vocal's own chords sits above it, lit and
    /// muted the same way.
    ///
    /// Three ways to show time (DESIGN.md §14, §16):
    /// <list type="bullet">
    /// <item>A narrated mix, landscape: x is the position in the shared phrase, the whole phrase
    /// across the panel; the playhead runs through it.</item>
    /// <item>A narrated mix, portrait (<see cref="ViewerLayout"/>'s vertical layout): the graph is
    /// <see cref="PortraitScale"/> times larger (pixels per beat and per semitone) and pans right to
    /// left under a playhead fixed at the panel's centre, which is the screen's centre; the
    /// phrase-folded x wraps around the phrase as it pans (<see cref="MelodyScroll"/>).</item>
    /// <item>A duet loop, either layout: a timeline of mix beats panning the same way and wrapping
    /// seamlessly at the loop point (portrait again three times larger); the two singing melodies
    /// bright with their lights, the chord strip the bed's chords.</item>
    /// <item>A melody mosaic, either layout: a timeline of the mix's beats, loop after loop, panning
    /// the same way (one loop per plot width in landscape, three times larger in portrait); the
    /// target's melody, the pieces as coloured spans labelled by song, the harmony voices (see
    /// MelodyGraphPanel.Mosaic.cs).</item>
    /// </list>
    /// A panning graph is drawn into a masked content strip around an anchor beat and only shifted
    /// per frame; it is redrawn when the playhead has moved a few beats from the anchor.
    ///
    /// The panel is on its own Screen Space - Camera canvas on the view camera, directly above the
    /// now-playing strip, the same width, clear of the HUD. uGUI is drawn after URP's
    /// post-processing, so the light points come from <see cref="MelodyLightRig"/>: HDR quads that
    /// a dedicated camera renders with its own bloom into a texture, added onto the panel exactly
    /// where the lights ride (the graph's own bloom and look are untouched).
    /// </summary>
    public sealed partial class MelodyGraphPanel : MonoBehaviour
    {
        public const float PanelWidth = 1240f;
        /// <summary>Height of the landscape panel.</summary>
        public const float PanelHeight = 272f;
        /// <summary>The portrait graph is this many times larger: pixels per beat and per semitone.</summary>
        public const float PortraitScale = 3f;
        /// <summary>Gap between the now-playing strip and this panel.</summary>
        public const float Gap = 10f;
        /// <summary>Bottom of the panel above the screen's bottom edge (reference px, landscape).</summary>
        public static float PanelBottom => 16f + FeaturedPathsPanel.StripHeight + Gap;

        const float PlotLeft = 64f, PlotRightPad = 22f, PlotTop = 52f;
        const float ChordStripHeight = 26f, VocalStripHeight = 5f, BottomPad = 14f;
        static float ChordTopFor(float h) => h - BottomPad - ChordStripHeight;
        static float VocalTopFor(float h) => ChordTopFor(h) - 3f - VocalStripHeight;
        static float PlotBottomFor(float h) => VocalTopFor(h) - 6f;
        /// <summary>The landscape plot's height (the portrait one is <see cref="PortraitScale"/> times it).</summary>
        public static float LandscapePlotHeight => PlotBottomFor(PanelHeight) - PlotTop;
        /// <summary>Height of the portrait panel: the same chrome around a plot three times as tall.</summary>
        public static float PortraitPanelHeight => PanelHeight + (PortraitScale - 1f) * LandscapePlotHeight;
        /// <summary>Panel height for a layout.</summary>
        public static float HeightFor(bool portrait) => portrait ? PortraitPanelHeight : PanelHeight;
        /// <summary>The playhead's x while the graph pans: the panel's centre, which is the screen's centre.</summary>
        public const float ScrollHeadX = PanelWidth * .5f;

        [Tooltip("Bloom intensity of the light points' own camera (the graph's bloom is not changed).")]
        [Min(0f)] public float LightBloom = 4f;
        [Tooltip("How bright a light point stays while the voice rests between phrases (1 = singing).")]
        [Range(0f, 1f)] public float RestLevel = .35f;
        [Tooltip("Brightness of the backing's own melody's light during a changeover (1 = the vocal singing over it).")]
        [Range(0f, 1f)] public float BackingLevel = .45f;
        [Tooltip("A panning graph is redrawn when the playhead is this many pixels from where it was drawn.")]
        [Min(40f)] public float RedrawPixels = 360f;

        static readonly Color SheetColor = new(.03f, .035f, .045f, .94f);
        static readonly Color TextColor = new(.94f, .95f, .97f, 1f);
        static readonly Color MutedColor = new(.60f, .64f, .68f, 1f);
        static readonly Color DimColor = new(.42f, .45f, .49f, 1f);
        static readonly Color Accent = SongPalette.Hex("#ffcf4a");

        /// <summary>Melody colours by song (bright); the muted version is mixed toward the panel.</summary>
        static readonly Color[] SongColors =
        {
            SongPalette.Hex("#62c3ff"), SongPalette.Hex("#ff9a62"), SongPalette.Hex("#c39bff"), SongPalette.Hex("#6fe3a1"),
            SongPalette.Hex("#ffd45e"), SongPalette.Hex("#ff7fb4"), SongPalette.Hex("#79e6e0"), SongPalette.Hex("#e8e8e8")
        };

        public static Color BrightColor(int song) => SongColors[((song % SongColors.Length) + SongColors.Length) % SongColors.Length];
        /// <summary>The backing's own melody during a changeover: between muted and bright.</summary>
        public static Color BackingColor(int song)
        {
            Color c = Color.Lerp(BrightColor(song), new Color(.16f, .17f, .19f, 1f), .22f);
            c.a = .9f;
            return c;
        }

        public static Color MutedSongColor(int song)
        {
            Color c = Color.Lerp(BrightColor(song), new Color(.16f, .17f, .19f, 1f), .5f);
            c.a = .8f;
            return c;
        }

        /// <summary>One song's melody line.</summary>
        public sealed class LineView
        {
            public int Song;
            public UiShapes Line = null!;
            public UiShapes Glow = null!;
            public bool Playing;
            /// <summary>
            /// During a changeover: the melody the playing instrumental was made for (its song's own
            /// vocal, not sounding now), highlighted a step below the vocal singing over that track.
            /// </summary>
            public bool Backing;
            public float Width;
            public Color Color;
            /// <summary>
            /// Points drawn (y down): panel px for the static graph; content px for a panning one
            /// (add <see cref="MelodyGraphPanel.ContentShift"/> to x for the panel).
            /// </summary>
            public readonly List<Vector2> Points = new();
            public readonly List<List<Vector2>> Pieces = new();
            /// <summary>The melody in unwrapped beats (panning graphs).</summary>
            public List<MelodyScroll.Piece> Sung = new();
            /// <summary>A melody mosaic's line: which voice in which loop (null otherwise).</summary>
            public MosaicTimeline.LineSpec? MosaicLine;
        }

        /// <summary>A point of light riding on a playing melody.</summary>
        public sealed class DotView
        {
            public int Song;
            /// <summary>A small crisp disc on the panel (the bloomed light is added over it by <see cref="MelodyLightRig"/>).</summary>
            public Image Core = null!;
            /// <summary>A wide soft halo in the melody's colour.</summary>
            public Image Halo = null!;
            public bool Active;
            public bool Voiced;
            /// <summary>The (dimmer) light on the backing's own melody during a changeover.</summary>
            public bool Backing;
            /// <summary>Centre in panel px (y down).</summary>
            public Vector2 Position;
            public float Pitch;
            /// <summary>A melody mosaic's light: the index into <see cref="Lines"/> of the line it rides (-1 when off or otherwise).</summary>
            public int Line = -1;
        }

        SongGraphLoader loader = null!;
        Canvas? canvas;
        RectTransform? root;
        RectTransform panel = null!;
        TextMeshProUGUI kicker = null!, subtitle = null!, playheadTag = null!, chordsLabel = null!, vocalLabel = null!, emptyText = null!;
        UiShapes grid = null!, bars = null!, chordStrip = null!, vocalStrip = null!;
        Image playhead = null!, playheadCap = null!, chordUnderline = null!, chordCurrent = null!, background = null!;
        readonly List<TextMeshProUGUI> pitchLabels = new();
        readonly List<TextMeshProUGUI> romanLabels = new();
        readonly List<(Image swatch, TextMeshProUGUI text)> legend = new();
        readonly List<LineView> lines = new();
        readonly List<DotView> dots = new();
        MelodyLightRig? rig;
        RectTransform viewport = null!, content = null!, linesRoot = null!, dotsRoot = null!;
        RectMask2D mask = null!;

        // What is shown: a narrated mix or a duet loop.
        Mashup? shown;
        DuetLoop? shownDuet;
        readonly List<MashupSong> songs = new();
        float height = PanelHeight;
        bool portrait, scrolling;
        double period = 32, phraseBeats = 32;
        int beatsPerBar = 4;
        float pitchLo = 55, pitchHi = 79;
        int stripSong = -2, vocalStripSong = -2, backingSong = -1;
        IReadOnlyList<MashupChord>? stripChords, vocalChords;
        int stripTonic, vocalTonic;
        // The chord sounding (and, panning, which image of it: unwrapped beat = beat + litImage · period).
        MashupChord? litChord, litVocal;
        int litImage;
        double windowLo, windowHi;
        readonly List<string> labelTexts = new();
        readonly List<StripCell> cells = new();
        int tagBar = -1;
        string tagRoman = "";
        readonly List<int> vocals = new(), instrumentals = new();
        // Panning: the beat the content was drawn around, and whether it must be drawn again.
        double anchor;
        bool windowDirty = true;
        readonly List<Vector2> scratch = new();

        /// <summary>The user's toggle (M, the strip's button): the graph shows while a mix or a duet loop plays.</summary>
        public bool UserVisible { get; private set; } = true;
        public bool Showing => root != null && root.gameObject.activeSelf;
        /// <summary>The narrated mix shown (null while a duet loop or nothing shows).</summary>
        public Mashup? Shown => Showing ? shown : null;
        /// <summary>The duet loop shown (null while a narrated mix or nothing shows).</summary>
        public DuetLoop? ShownDuet => Showing ? shownDuet : null;
        public RectTransform? PanelRect => root != null ? panel : null;
        public Canvas? Canvas => canvas;
        public IReadOnlyList<LineView> Lines => lines;
        public IReadOnlyList<DotView> Dots => dots;
        /// <summary>Position on the graph's beat axis under the playhead: the phrase beat of a mix, the mix beat of a duet loop.</summary>
        public double PhraseBeat { get; private set; }
        /// <summary>Playhead x in panel px.</summary>
        public float PlayheadX { get; private set; }
        /// <summary>Mashup song whose chords the strip shows (the instrumental playing; a duet's root), -1 when none.</summary>
        public int StripSong => stripSong;
        /// <summary>Song whose own chords the thin strip shows during a changeover (-1 when hidden).</summary>
        public int VocalStripSong => vocalStripSong;
        /// <summary>Song whose own melody is highlighted as the backing's during a changeover (-1 otherwise).</summary>
        public int BackingSong => backingSong;
        public float PitchLow => pitchLo;
        public float PitchHigh => pitchHi;
        /// <summary>The bloomed light points (null without the MusicHistory/MelodyGlow shader).</summary>
        public MelodyLightRig? Lights => rig;
        /// <summary>The panel's current height (reference px): <see cref="PanelHeight"/> or <see cref="PortraitPanelHeight"/>.</summary>
        public float Height => height;
        /// <summary>Laid out for the vertical layout (three times larger).</summary>
        public bool Portrait => portrait;
        /// <summary>The graph pans under a fixed playhead (portrait, or a duet loop).</summary>
        public bool Scrolling => scrolling;
        /// <summary>The beat axis' period: the phrase (a mix) or the loop's beats (a duet).</summary>
        public double Period => period;
        /// <summary>Horizontal scale, reference px per beat.</summary>
        public float PixelsPerBeat { get; private set; }
        /// <summary>Vertical scale, reference px per semitone.</summary>
        public float PixelsPerSemitone => Plot.height / Mathf.Max(1f, pitchHi - pitchLo);
        /// <summary>Panning graphs: content x + this = panel x.</summary>
        public float ContentShift { get; private set; }
        /// <summary>Panning graphs: the beat the content is drawn around.</summary>
        public double Anchor => anchor;
        /// <summary>Times a panning graph's content was drawn (validation, profiling).</summary>
        public int WindowBuilds { get; private set; }

        /// <summary>The landscape plot area in panel px (y down).</summary>
        public static Rect PlotArea => PlotFor(PanelHeight);
        public static Rect ChordArea => ChordFor(PanelHeight);
        public static Rect PlotFor(float h) => new(PlotLeft, PlotTop, PanelWidth - PlotLeft - PlotRightPad, PlotBottomFor(h) - PlotTop);
        public static Rect ChordFor(float h) => new(PlotLeft, ChordTopFor(h), PanelWidth - PlotLeft - PlotRightPad, ChordStripHeight);
        /// <summary>The current plot area in panel px (y down).</summary>
        public Rect Plot => PlotFor(height);
        public Rect ChordRect => ChordFor(height);
        float ChordTop => ChordTopFor(height);
        float VocalTop => VocalTopFor(height);

        /// <summary>Legend text (titles and years, the playing ones marked), for validation.</summary>
        public string LegendText
        {
            get
            {
                StringBuilder b = new();
                foreach ((Image swatch, TextMeshProUGUI text) in legend)
                    if (swatch.gameObject.activeSelf) b.Append(text.text).Append('\n');
                return b.ToString();
            }
        }

        /// <summary>The chord names in the chord strip, in drawing order (plain text, for validation).</summary>
        public string ChordText
        {
            get
            {
                StringBuilder b = new();
                for (int i = 0; i < romanLabels.Count && i < labelTexts.Count; i++)
                    if (romanLabels[i].gameObject.activeSelf) b.Append(labelTexts[i]).Append(" | ");
                return b.ToString().TrimEnd(' ', '|');
            }
        }

        /// <summary>One drawn cell of the chord strip (content px).</summary>
        public readonly struct StripCell
        {
            public readonly MashupChord Chord;
            public readonly float X0, X1;
            public readonly bool Lit;
            public readonly Color Color;

            public StripCell(MashupChord chord, float x0, float x1, bool lit, Color color)
            {
                Chord = chord;
                X0 = x0;
                X1 = x1;
                Lit = lit;
                Color = color;
            }
        }

        /// <summary>The main strip's cells as last drawn.</summary>
        public IReadOnlyList<StripCell> StripCells => cells;
        /// <summary>The chord sounding on the main strip (null between chords).</summary>
        public MashupChord? LitChord => litChord;
        /// <summary>The tonic the main strip's chords are coloured and named against (normalized frame).</summary>
        public int StripTonic => stripTonic;
        /// <summary>HDR intensity of the current chord's light on the strip.</summary>
        [Min(0f)] public float ChordGlow = .6f;

        public string PitchLabelText
        {
            get
            {
                StringBuilder b = new();
                foreach (TextMeshProUGUI t in pitchLabels)
                    if (t.gameObject.activeSelf) b.Append(t.text).Append(' ');
                return b.ToString().Trim();
            }
        }

        public string KickerText => kicker != null ? kicker.text : "";
        public string PlayheadTagText => playheadTag != null ? playheadTag.text : "";

        // ------------------------------------------------------------------ build

        public void Build(SongGraphLoader owner)
        {
            loader = owner;
            Discard();
            GameObject go = new("MusicHistory Melody Graph", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(owner.transform, false);
            canvas = go.GetComponent<Canvas>();
            canvas.sortingOrder = 250;   // behind the HUD (300) when both are camera canvases (captures)
            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            AttachCamera();
            root = (RectTransform)go.transform;

            height = PanelHeight;
            portrait = scrolling = false;
            panel = UiKit.Rect("Melody Graph", root);
            panel.anchorMin = panel.anchorMax = new Vector2(.5f, 0f);
            panel.pivot = new Vector2(.5f, 0f);
            panel.anchoredPosition = new Vector2(0, PanelBottom);
            panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panel.gameObject.AddComponent<CanvasRenderer>();
            background = panel.gameObject.AddComponent<Image>();
            background.sprite = UiKit.Rounded(14);
            background.type = Image.Type.Sliced;
            background.color = SheetColor;
            background.raycastTarget = false;

            kicker = UiKit.Text("Kicker", panel, 12, Accent, TextAlignmentOptions.MidlineLeft, bold: true);
            kicker.characterSpacing = 6;
            kicker.text = "MELODY GRAPH";
            UiKit.Place(kicker.rectTransform, 20, 11, 160, 20);
            subtitle = UiKit.Text("Subtitle", panel, 13, MutedColor);
            UiKit.Place(subtitle.rectTransform, 160, 11, 300, 20);

            grid = Shapes("Grid", panel);
            // Everything that moves with the beat lives in the content strip, clipped to the plot when it pans.
            viewport = UiKit.Rect("Viewport", panel);
            mask = viewport.gameObject.AddComponent<RectMask2D>();
            content = UiKit.Rect("Content", viewport);
            bars = Shapes("Bar Lines", content);
            BuildMosaicLayers();
            chordStrip = Shapes("Chord Strip", content);
            vocalStrip = Shapes("Vocal Chord Strip", content);
            chordCurrent = UiKit.Image("Current Chord", content, new Color(1, 1, 1, .16f));
            chordUnderline = UiKit.Image("Current Chord Underline", content, new Color(1, 1, 1, .9f), 1);
            linesRoot = UiKit.Rect("Melodies", content);
            chordsLabel = UiKit.Text("Chords Label", panel, 10, DimColor, TextAlignmentOptions.MidlineRight);
            chordsLabel.text = "chords";
            vocalLabel = UiKit.Text("Vocal Label", panel, 9, DimColor, TextAlignmentOptions.MidlineRight);
            vocalLabel.text = "vocal's own";

            playhead = UiKit.Image("Playhead", panel, new Color(1, 1, 1, .72f));
            playheadCap = UiKit.Dot("Playhead Cap", panel, new Color(1, 1, 1, .9f));
            playheadTag = UiKit.Text("Playhead Tag", panel, 11, TextColor, TextAlignmentOptions.Center, bold: true);
            dotsRoot = UiKit.Rect("Light Points", panel);
            emptyText = UiKit.Text("Empty", panel, 14, MutedColor, TextAlignmentOptions.Center);
            emptyText.text = "No sung melody was tracked for these songs.";

            Shader? glow = Shader.Find(GlowShaderName);
            if (glow != null) rig = new MelodyLightRig(owner.transform, panel, PanelWidth, PanelHeight, glow) { BloomIntensity = LightBloom };
            else Debug.LogWarning($"MusicHistory: shader {GlowShaderName} not found; the melody graph's light points will not bloom.");
            LayoutChrome();
            shown = null;
            shownDuet = null;
            root.gameObject.SetActive(false);
        }

        public const string GlowShaderName = "MusicHistory/MelodyGlow";

        /// <summary>HDR intensity of a glow material (0 when it has none).</summary>
        public static float IntensityOf(Material? m) => m != null && m.HasProperty("_Intensity") ? m.GetFloat("_Intensity") : 0f;

        UiShapes Shapes(string name, Transform parent)
        {
            RectTransform r = UiKit.Rect(name, parent);
            UiKit.Place(r, 0, 0, PanelWidth, height);
            r.gameObject.AddComponent<CanvasRenderer>();
            UiShapes s = r.gameObject.AddComponent<UiShapes>();
            s.raycastTarget = false;
            return s;
        }

        /// <summary>Sizes the panel and places the parts that depend on its height and on panning.</summary>
        void LayoutChrome()
        {
            panel.sizeDelta = new Vector2(PanelWidth, height);
            Rect plot = Plot;
            foreach (UiShapes s in new[] { grid, bars, chordStrip, vocalStrip }) UiKit.Place(s.rectTransform, 0, 0, PanelWidth, height);
            LayoutMosaicLayers();
            UiKit.Place(linesRoot, 0, 0, PanelWidth, height);
            UiKit.Place(dotsRoot, 0, 0, PanelWidth, height);
            foreach (LineView l in lines)
            {
                UiKit.Place(l.Line.rectTransform, 0, 0, PanelWidth, height);
                UiKit.Place(l.Glow.rectTransform, 0, 0, PanelWidth, height);
            }
            UiKit.Place(chordsLabel.rectTransform, 4, ChordTop, PlotLeft - 10, ChordStripHeight);
            UiKit.Place(vocalLabel.rectTransform, 4, VocalTop - 4, PlotLeft - 10, VocalStripHeight + 8);
            UiKit.Place(emptyText.rectTransform, PlotLeft, PlotTop, plot.width, plot.height);
            if (scrolling)
            {
                // The plot's columns only, from the plot's top to under the chord strip's underline.
                float top = plot.yMin - 2f, bottom = ChordTop + ChordStripHeight + 6f;
                UiKit.Place(viewport, plot.xMin, top, plot.width, bottom - top);
                mask.enabled = true;
            }
            else
            {
                UiKit.Place(viewport, 0, 0, PanelWidth, height);
                mask.enabled = false;
            }
            PlaceContent(0f);
            rig?.Resize(PanelWidth, height);
        }

        /// <summary>Content at panel coordinates, shifted by <paramref name="shift"/> px (panning).</summary>
        void PlaceContent(float shift)
        {
            ContentShift = shift;
            Vector2 at = scrolling ? new Vector2(-viewport.anchoredPosition.x + shift, viewport.anchoredPosition.y) : Vector2.zero;
            // Viewport is placed top-left at (x, -y): the content's top-left goes back to the panel's.
            UiKit.Place(content, at.x, at.y, PanelWidth, height);
        }

        /// <summary>Puts the canvas on the view camera (Screen Space - Camera, so bloom reaches it); overlay without one.</summary>
        void AttachCamera()
        {
            if (canvas == null) return;
            Camera? cam = loader != null ? loader.ViewCamera : null;
            if (cam != null)
            {
                if (canvas.renderMode != RenderMode.ScreenSpaceCamera) canvas.renderMode = RenderMode.ScreenSpaceCamera;
                if (canvas.worldCamera != cam) canvas.worldCamera = cam;
                // The light points' layer is for their own camera only.
                if ((cam.cullingMask & (1 << MelodyLightRig.Layer)) != 0) cam.cullingMask &= ~(1 << MelodyLightRig.Layer);
                float plane = cam.nearClipPlane + .12f;
                if (!Mathf.Approximately(canvas.planeDistance, plane)) canvas.planeDistance = plane;
            }
            else if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }
        }

        /// <summary>Destroys the canvas (the loader clears or rebuilds).</summary>
        public void Discard()
        {
            if (root != null) Kill(root.gameObject);
            root = null;
            canvas = null;
            shown = null;
            shownDuet = null;
            ForgetMosaic();
            songs.Clear();
            lines.Clear();
            dots.Clear();
            pitchLabels.Clear();
            romanLabels.Clear();
            legend.Clear();
            rig?.Dispose();
            rig = null;
        }

        static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDestroy() => Discard();

        // ------------------------------------------------------------------ state

        /// <summary>M or the strip's button: shows or hides the graph (the camera reframes the songs).</summary>
        public void Toggle() => SetUserVisible(!UserVisible);

        public void SetUserVisible(bool visible)
        {
            if (UserVisible == visible) return;
            UserVisible = visible;
            Refresh();
            if (loader != null && loader.Director != null) loader.Director.Reframe();
        }

        void LateUpdate() => Refresh();

        /// <summary>Follows the mix or the duet loop playing now (public so edit-mode validation can drive it).</summary>
        public void Refresh()
        {
            if (root == null || loader == null) return;
            WalkthroughDirector? d = loader.Director;
            Mashup? m = d != null ? d.CurrentMashup : null;
            DuetLoop? duet = d != null ? d.CurrentDuet : null;
            Mosaic? mosaic = d != null ? d.CurrentMosaic : null;
            bool show = (m != null || duet != null || mosaic != null) && UserVisible;
            if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
            if (!show || d == null)
            {
                rig?.Sync(false, 1f, false);
                return;
            }
            if (mosaic != null)
            {
                RefreshMosaic(d, mosaic);
                return;
            }
            AttachCamera();
            bool portraitNow = loader.Layout != null && loader.Layout.Format == ScreenFormat.Vertical;
            bool scrollNow = duet != null || portraitNow;
            if (!ReferenceEquals(duet, shownDuet) || (duet == null && !ReferenceEquals(m, shown)) || portraitNow != portrait || scrollNow != scrolling)
                Rebuild(duet == null ? m : null, duet, portraitNow);

            // Where the playhead is, who sings, which chords sound.
            double beat;
            int inst, vocalOwn;
            MashupChord? chord, vocalChord;
            if (duet != null)
            {
                double t = d.LoopSeconds;
                duet.AudibleAt(t, vocals, instrumentals);
                beat = duet.BeatAt(t);
                inst = duet.RootSong;
                vocalOwn = -1;
                backingSong = -1;
                if (!ReferenceEquals(stripChords, duet.Chords) || stripSong != inst) SetStrip(inst, duet.Chords, ChordKey.TonicOf(duet.Chords, duet.Key));
                if (vocalStripSong != -1) SetVocalStrip(-1, null);
                chord = duet.ChordAt(beat);
                vocalChord = null;
            }
            else
            {
                double t = d.MixSeconds;
                m!.AudibleAt(t, vocals, instrumentals);
                MashupSegment? seg = m.SegmentAt(t);
                beat = m.PhraseBeatAt(t);
                // Chord strips: the instrumental playing; during a changeover, the vocal's own chords above it.
                inst = seg != null ? seg.InstrumentalSong : instrumentals.Count > 0 ? instrumentals[0] : -1;
                if (inst != stripSong) SetStrip(inst, inst >= 0 ? m.Songs[inst].Chords : null, inst >= 0 ? ChordKey.TonicOf(m.Songs[inst].Chords, seg?.Key) : 0);
                vocalOwn = seg != null && seg.Kind == MashupSegmentKind.Changeover && seg.VocalSong >= 0 && seg.VocalSong != inst ? seg.VocalSong : -1;
                if (vocalOwn != vocalStripSong) SetVocalStrip(vocalOwn, vocalOwn >= 0 ? m.Songs[vocalOwn].Chords : null);
                vocalChord = vocalOwn >= 0 ? m.Songs[vocalOwn].ChordAt(beat) : null;
                // During a changeover the backing's own melody (the tune this instrumental was made for)
                // is highlighted too, a step below the vocal singing over it: two melodies on one track.
                backingSong = seg != null && seg.Kind == MashupSegmentKind.Changeover && seg.InstrumentalSong >= 0 &&
                              !vocals.Contains(seg.InstrumentalSong) ? seg.InstrumentalSong : -1;
                chord = inst >= 0 ? m.Songs[inst].ChordAt(beat) : null;
            }
            PhraseBeat = beat;

            // Melody styles.
            bool restyled = false;
            foreach (LineView l in lines)
            {
                bool playing = vocals.Contains(l.Song);
                bool backing = !playing && l.Song == backingSong;
                if (playing == l.Playing && backing == l.Backing) continue;
                l.Playing = playing;
                l.Backing = backing;
                if (!scrolling) StyleLine(l);
                restyled = true;
            }
            if (restyled)
            {
                // Muted melodies at the bottom, then the backing's, the playing ones on top.
                foreach (LineView l in lines) if (!l.Playing && !l.Backing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.Backing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.Playing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                RefreshLegend();
                windowDirty = true;
            }

            // Panning: shift the content under the fixed playhead; redraw it a few beats on.
            double drift = 0;
            if (scrolling)
            {
                drift = MelodyScroll.WrapNearest(beat - anchor, period);
                if (windowDirty || Math.Abs(drift) * PixelsPerBeat > RedrawPixels)
                {
                    anchor = beat;
                    drift = 0;
                    BuildWindow();
                }
                PlaceContent(-(float)(drift * PixelsPerBeat));
            }

            // The chord sounding keeps its colour (and its light); the others are muted.
            int image = scrolling && period > 0 ? (int)Math.Round((anchor + drift - beat) / period) : 0;
            if (!ReferenceEquals(chord, litChord) || !ReferenceEquals(vocalChord, litVocal) || image != litImage)
            {
                litChord = chord;
                litVocal = vocalChord;
                litImage = image;
                if (scrolling) DrawStripsWindow();
                else
                {
                    BuildChordStrip();
                    BuildVocalStrip();
                }
            }

            // Playhead through the graph and the strips.
            Rect plot = Plot;
            float x = scrolling ? ScrollHeadX : BeatX(beat);
            PlayheadX = x;
            UiKit.Place(playhead.rectTransform, x - 1f, plot.yMin - 4f, 2f, ChordTop + ChordStripHeight + 3f - (plot.yMin - 4f));
            UiKit.Place(playheadCap.rectTransform, x - 4f, plot.yMin - 8f, 8f, 8f);
            int bar = beatsPerBar > 0 ? (int)Math.Floor(beat / beatsPerBar) + 1 : 1;
            // The tag text changes once a bar or chord (no string per frame).
            string roman = chord != null ? ChordNames.Label(chord, stripTonic) : "";
            if (bar != tagBar || roman != tagRoman)
            {
                tagBar = bar;
                tagRoman = roman;
                UiKit.SetText(playheadTag, roman.Length > 0 ? $"bar {bar} · {GraphHud.Esc(roman)}" : $"bar {bar}");
            }
            float tagW = 150f;
            UiKit.Place(playheadTag.rectTransform, Mathf.Clamp(x - tagW / 2, plot.xMin, plot.xMax - tagW), plot.yMin - 18f, tagW, 13f);
            if (chord != null)
            {
                float x0, x1;
                if (scrolling)
                {
                    // In content px around the anchor: the chord's stretch that holds the playhead.
                    double here = anchor + drift;
                    x0 = ContentX(here - (beat - chord.Start));
                    x1 = ContentX(here + (Math.Min(chord.End, period) - beat));
                }
                else
                {
                    x0 = BeatX(chord.Start);
                    x1 = BeatX(Math.Min(chord.End, period));
                }
                UiKit.Show(chordCurrent, false);
                UiKit.Show(chordUnderline, true);
                UiKit.Place(chordUnderline.rectTransform, x0 + 1f, ChordTop + ChordStripHeight + 2f, Mathf.Max(1f, x1 - x0 - 2f), 3f);
                // Its light, on the panel (clipped to the plot while the graph pans).
                float shift = scrolling ? ContentShift : 0f;
                float g0 = Mathf.Max(plot.xMin, x0 + shift), g1 = Mathf.Min(plot.xMax, x1 + shift);
                bool glow = g1 - g0 > 1f;
                // As bright for every chord: its brightest channel reaches ChordGlow (dark minor colours too).
                Color glowColor = ChordPalette.Of(chord, stripTonic);
                rig?.SetGlowRect(0, glow, new Rect(g0 + 1f, ChordTop + 1f, Mathf.Max(1f, g1 - g0 - 2f), ChordStripHeight - 2f), 6f,
                    glowColor, ChordGlow / Mathf.Max(.2f, Mathf.Max(glowColor.r, glowColor.g, glowColor.b)));
            }
            else
            {
                UiKit.Show(chordCurrent, false);
                UiKit.Show(chordUnderline, false);
                rig?.SetGlowRect(0, false, default, 0f, Color.black, 0f);
            }

            // A point of light on every playing melody, at the playhead and the melody's pitch there
            // (and a dimmer one on the backing's own melody during a changeover).
            bool tinted = false;
            foreach (DotView dot in dots)
            {
                MashupSong song = songs[dot.Song];
                float p = float.NaN;
                bool v = false;
                bool backing = dot.Song == backingSong;
                bool on = (vocals.Contains(dot.Song) || backing) && PitchHere(dot.Song, song, beat, out p, out v);
                dot.Active = on;
                dot.Backing = on && backing;
                UiKit.Show(dot.Core, on);
                UiKit.Show(dot.Halo, on);
                if (!on)
                {
                    rig?.SetLight(dot.Song, false, default, 0, 0, Color.black, 0);
                    continue;
                }
                dot.Voiced = v;
                dot.Pitch = p;
                Vector2 at = new(x, PitchY(p));
                dot.Position = at;
                float core = backing ? (v ? 5f : 4f) : v ? 7f : 5f;
                float halo = backing ? (v ? 38f : 24f) : v ? 64f : 36f;
                UiKit.Place(dot.Core.rectTransform, at.x - core / 2, at.y - core / 2, core, core);
                UiKit.Place(dot.Halo.rectTransform, at.x - halo / 2, at.y - halo / 2, halo, halo);
                Color bright = BrightColor(dot.Song);
                dot.Core.color = backing ? new Color(1, 1, 1, v ? .75f : .45f) : v ? Color.white : new Color(1, 1, 1, .6f);
                dot.Halo.color = new Color(bright.r, bright.g, bright.b, backing ? (v ? .24f : .12f) : v ? .42f : .2f);
                // The bloomed light itself: full while singing, dimmer in a breath; the backing's a step below.
                float level = (v ? 1f : RestLevel) * (backing ? BackingLevel : 1f);
                rig?.SetLight(dot.Song, true, at, backing ? (v ? 9f : 7f) : v ? 12f : 8f, backing ? (v ? 32f : 22f) : v ? 46f : 28f, bright, level);
                if (!tinted && !backing)
                {
                    rig?.SetTint(bright);
                    tinted = true;
                }
            }
            rig?.Sync(true, PixelScale(), false);
        }

        /// <summary>
        /// The pitch the light rides at: on a panning graph exactly the drawn line's (between its
        /// points), else, in a breath, the melody's nearest sung pitch; on the static graph the
        /// melody's own lookup, as before.
        /// </summary>
        bool PitchHere(int index, MashupSong song, double beat, out float pitch, out bool voiced)
        {
            if (scrolling && index < lines.Count && MelodyScroll.LineAt(lines[index].Sung, beat, period, out pitch))
            {
                voiced = true;
                return true;
            }
            return song.PitchAt(beat, out pitch, out voiced);
        }

        /// <summary>Screen pixels per reference pixel on the view camera (as the HUD's scale-with-screen-size).</summary>
        float PixelScale()
        {
            Camera? cam = loader != null ? loader.ViewCamera : null;
            if (cam == null || loader == null || loader.Hud == null) return 1f;
            return loader.Hud.ScaleFor(cam.pixelWidth, cam.pixelHeight);
        }

        /// <summary>Text meshes and layout now (edit-mode captures).</summary>
        public void ForceUpdate()
        {
            Refresh();
            if (root == null || !root.gameObject.activeSelf) return;
            rig?.Sync(true, PixelScale(), true);
            foreach (TMP_Text t in root.GetComponentsInChildren<TMP_Text>(false)) t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>The static graph's x of a phrase beat.</summary>
        float BeatX(double beat)
        {
            Rect plot = Plot;
            double p = period > 0 ? period : 32;
            return plot.xMin + (float)(Math.Max(0, Math.Min(p, beat)) / p) * plot.width;
        }

        /// <summary>A panning graph's content x of unwrapped beat <paramref name="u"/> (the anchor sits under the playhead).</summary>
        float ContentX(double u) => ScrollHeadX + (float)((u - anchor) * PixelsPerBeat);

        float PitchY(float pitch)
        {
            Rect plot = Plot;
            float u = Mathf.InverseLerp(pitchLo, pitchHi, pitch);
            return plot.yMax - u * plot.height;
        }

        /// <summary>
        /// Where (beat, pitch) sits on the panel (px from its top-left, y down): on the static graph
        /// at its place in the phrase; on a panning graph relative to the playhead now (the nearest image).
        /// </summary>
        public Vector2 PanelPoint(double beat, float pitch) =>
            new(scrolling ? (float)MelodyScroll.X(beat, PhraseBeat, period, PixelsPerBeat, ScrollHeadX) : BeatX(beat), PitchY(pitch));

        /// <summary>
        /// Screen position (pixels, origin bottom-left) of panel point <paramref name="p"/> on a
        /// <paramref name="width"/> x <paramref name="height"/> screen (scale-with-screen-size, as the HUD).
        /// </summary>
        public Vector2 ScreenPoint(Vector2 p, float width, float height)
        {
            float scale = loader != null && loader.Hud != null ? loader.Hud.ScaleFor(width, height) : height / 1080f;
            float left = width * .5f - PanelWidth * .5f * scale;
            // Where the panel actually sits (ViewerLayout raises it in the vertical layout).
            float bottom = (root != null ? panel.anchoredPosition.y : PanelBottom) * scale;
            return new Vector2(left + p.x * scale, bottom + (this.height - p.y) * scale);
        }

        // ------------------------------------------------------------------ rebuild for a mix or a loop

        void Rebuild(Mashup? m, DuetLoop? duet, bool portraitLayout)
        {
            shown = m;
            shownDuet = duet;
            ForgetMosaic();
            portrait = portraitLayout;
            scrolling = duet != null || portraitLayout;
            height = HeightFor(portrait);
            tagBar = -1;
            tagRoman = "";
            litChord = litVocal = null;
            litImage = 0;
            stripSong = -2;
            vocalStripSong = -2;
            stripChords = vocalChords = null;
            backingSong = -1;
            songs.Clear();
            if (duet != null)
            {
                foreach (DuetSong s in duet.Songs) songs.Add(s.Line);
                period = duet.LoopBeats > 0 ? duet.LoopBeats : 32;
                phraseBeats = duet.PhraseBeats > 0 ? duet.PhraseBeats : 32;
                beatsPerBar = Math.Max(1, duet.BeatsPerBar);
            }
            else if (m != null)
            {
                songs.AddRange(m.Songs);
                period = phraseBeats = m.PhraseBeats > 0 ? m.PhraseBeats : 32;
                beatsPerBar = Math.Max(1, m.BeatsPerBar);
            }
            LayoutChrome();
            Rect plot = Plot;
            // Landscape: the whole phrase across the plot. Panning: the same scale (one phrase per
            // plot width), three times larger in portrait.
            PixelsPerBeat = (float)(plot.width / phraseBeats) * (portrait ? PortraitScale : 1f);

            bool any = duet != null ? duet.PitchRange(out float lo, out float hi) : m!.PitchRange(out lo, out hi);
            if (any)
            {
                lo = Mathf.Floor(lo - 1.5f);
                hi = Mathf.Ceil(hi + 1.5f);
                if (hi - lo < 12f)
                {
                    float extra = 12f - (hi - lo);
                    lo -= Mathf.Floor(extra / 2f);
                    hi = lo + 12f;
                }
            }
            else
            {
                lo = 57;
                hi = 76;
            }
            pitchLo = lo;
            pitchHi = hi;
            if (duet != null)
            {
                UiKit.SetText(kicker, "DUET MELODIES");
                UiKit.SetText(subtitle, $"loop of {duet.LoopBars} bars · {GraphHud.Esc(duet.Key)} · {duet.Bpm.ToString("0.#", CultureInfo.InvariantCulture)} BPM · in C major / A minor");
            }
            else
            {
                UiKit.SetText(kicker, "MELODY GRAPH");
                UiKit.SetText(subtitle, $"{m!.Bars} bar{(m.Bars == 1 ? "" : "s")} · {m.BeatsPerBar}/4 · in C major / A minor");
            }
            UiKit.Place(subtitle.rectTransform, duet != null ? 190 : 160, 11, duet != null ? 330 : 300, 20);
            BuildGrid();

            foreach (LineView l in lines)
            {
                Kill(l.Line.gameObject);
                Kill(l.Glow.gameObject);
            }
            lines.Clear();
            foreach (DotView d in dots)
            {
                Kill(d.Core.gameObject);
                Kill(d.Halo.gameObject);
            }
            dots.Clear();
            bool anyVoiced = false;
            for (int i = 0; i < songs.Count; i++)
            {
                MashupSong song = songs[i];
                LineView l = new() { Song = i };
                l.Glow = Shapes($"Glow {i + 1}", linesRoot);
                l.Line = Shapes($"Melody {i + 1} {song.WorkId}", linesRoot);
                l.Glow.Feather = 6f;
                l.Line.Feather = 1.1f;
                IReadOnlyList<MelodyPoint> sung = duet != null ? duet.Songs[i].Melody : song.Melody;
                foreach (MelodyPoint p in sung)
                    if (p.Voiced)
                    {
                        anyVoiced = true;
                        break;
                    }
                if (scrolling)
                {
                    // Pieces in unwrapped beats; drawn per window (BuildWindow).
                    l.Sung = MelodyScroll.Pieces(sung, period, duet != null, MashupSong.MaxGapBeats);
                }
                else
                {
                    List<Vector2>? piece = null;
                    double lastBeat = double.NegativeInfinity;
                    foreach (MelodyPoint p in song.Melody)
                    {
                        if (!p.Voiced)
                        {
                            piece = null;
                            continue;
                        }
                        // A new piece after a gap, and where the window wraps past the phrase's end back to its start.
                        if (piece == null || p.Beat - lastBeat > MashupSong.MaxGapBeats || p.Beat < lastBeat)
                        {
                            piece = new List<Vector2>();
                            l.Pieces.Add(piece);
                        }
                        Vector2 at = PanelPoint(p.Beat, p.Pitch);
                        piece.Add(at);
                        l.Points.Add(at);
                        lastBeat = p.Beat;
                    }
                    // A lone point still shows: a tiny dash.
                    foreach (List<Vector2> pc in l.Pieces)
                        if (pc.Count == 1) pc.Add(pc[0] + new Vector2(2f, 0f));
                }
                l.Playing = false;
                if (!scrolling) StyleLine(l);
                lines.Add(l);

                DotView d = new() { Song = i };
                d.Halo = UiKit.Image($"Halo {i + 1}", dotsRoot, BrightColor(i));
                d.Halo.sprite = UiKit.SoftDot();
                d.Halo.type = Image.Type.Simple;
                d.Core = UiKit.Image($"Light {i + 1}", dotsRoot, Color.white);
                d.Core.sprite = UiKit.Circle();
                d.Core.type = Image.Type.Simple;
                UiKit.Show(d.Core, false);
                UiKit.Show(d.Halo, false);
                dots.Add(d);
            }
            rig?.EnsureLights(songs.Count);
            UiKit.Show(emptyText, !anyVoiced);
            if (!scrolling)
            {
                bars.Clear();
                BuildStaticBars();
            }
            BuildLegend();
            RefreshLegend();
            windowDirty = true;
            anchor = 0;
        }

        void StyleLine(LineView l)
        {
            l.Color = LineColor(l);
            l.Width = LineWidth(l);
            l.Line.Clear();
            l.Glow.Clear();
            foreach (List<Vector2> piece in l.Pieces) AddStyled(l, piece);
            l.Line.Commit();
            l.Glow.Commit();
        }

        static Color LineColor(LineView l) => l.Playing ? BrightColor(l.Song) : l.Backing ? BackingColor(l.Song) : MutedSongColor(l.Song);
        static float LineWidth(LineView l) => l.Playing ? 3.2f : l.Backing ? 2.3f : 1.6f;

        static void AddStyled(LineView l, IReadOnlyList<Vector2> piece)
        {
            Color bright = BrightColor(l.Song);
            l.Line.AddPolyline(piece, l.Width, l.Color);
            if (l.Playing) l.Glow.AddPolyline(piece, 4f, new Color(bright.r, bright.g, bright.b, .22f));
            else if (l.Backing) l.Glow.AddPolyline(piece, 3f, new Color(bright.r, bright.g, bright.b, .10f));
        }

        static readonly int[] WhiteKeys = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly string[] PitchNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static string NoteName(int midi) => PitchNames[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The fixed part of the grid: the plot's ground, the pitch lines and their note names.</summary>
        void BuildGrid()
        {
            Rect plot = Plot;
            grid.Clear();
            // Plot ground, slightly lifted from the sheet.
            grid.AddRect(plot.xMin, plot.yMin, plot.width, plot.height, new Color(1, 1, 1, .018f));
            float perSemitone = plot.height / Mathf.Max(1f, pitchHi - pitchLo);
            int label = 0;
            for (int p = Mathf.CeilToInt(pitchLo); p <= Mathf.FloorToInt(pitchHi); p++)
            {
                int pc = ((p % 12) + 12) % 12;
                if (Array.IndexOf(WhiteKeys, pc) < 0) continue;
                float y = PitchY(p);
                bool c = pc == 0;
                grid.AddRect(plot.xMin, y - .5f, plot.width, 1f, new Color(1, 1, 1, c ? .10f : .035f));
                bool show = perSemitone >= 11f || (perSemitone >= 5.5f ? pc is 0 or 4 or 7 or 9 : pc is 0 or 7);
                if (!show) continue;
                TextMeshProUGUI t = PitchLabel(label++);
                t.text = NoteName(p);
                t.color = c ? new Color(.82f, .85f, .88f, 1f) : DimColor;
                t.fontStyle = c ? FontStyles.Bold : FontStyles.Normal;
                UiKit.Place(t.rectTransform, 6, y - 7f, PlotLeft - 14f, 14f);
            }
            for (int i = label; i < pitchLabels.Count; i++) UiKit.Show(pitchLabels[i], false);
            grid.Commit();
        }

        /// <summary>The static graph's bar lines (the phrase's first and last stronger) and beat ticks along the top.</summary>
        void BuildStaticBars()
        {
            Rect plot = Plot;
            int bpb = Math.Max(1, beatsPerBar);
            for (int b = 0; b <= Math.Round(period); b++)
            {
                float x = BeatX(b);
                bool barLine = b % bpb == 0;
                bool edge = b == 0 || b >= period - 1e-6;
                if (barLine) bars.AddRect(x - .5f, plot.yMin, 1f, ChordTop + ChordStripHeight - plot.yMin, new Color(1, 1, 1, edge ? .16f : .07f));
                else bars.AddRect(x - .5f, plot.yMax - 4f, 1f, 4f, new Color(1, 1, 1, .08f));
            }
            bars.Commit();
        }

        TextMeshProUGUI PitchLabel(int i)
        {
            while (pitchLabels.Count <= i)
                pitchLabels.Add(UiKit.Text($"Pitch {pitchLabels.Count + 1}", panel, 11, DimColor, TextAlignmentOptions.MidlineRight));
            TextMeshProUGUI t = pitchLabels[i];
            UiKit.Show(t, true);
            return t;
        }

        TextMeshProUGUI RomanLabel(int i)
        {
            while (romanLabels.Count <= i)
                romanLabels.Add(UiKit.Text($"Chord {romanLabels.Count + 1}", content, 13, TextColor, TextAlignmentOptions.Center, bold: true));
            while (labelTexts.Count <= i) labelTexts.Add("");
            TextMeshProUGUI t = romanLabels[i];
            UiKit.Show(t, true);
            return t;
        }

        /// <summary>Colour of a chord cell against <paramref name="tonicPc"/>: the shared chord palette (<see cref="ChordPalette"/>, also the wheel's).</summary>
        public static Color ChordColor(MashupChord c, int tonicPc) => ChordPalette.Of(c, tonicPc);

        void SetStrip(int song, IReadOnlyList<MashupChord>? chords, int tonicPc)
        {
            stripSong = song;
            stripChords = chords;
            stripTonic = tonicPc;
            if (scrolling) windowDirty = true;
            else BuildChordStrip();
        }

        void SetVocalStrip(int song, IReadOnlyList<MashupChord>? chords)
        {
            vocalStripSong = song;
            vocalChords = chords;
            vocalTonic = chords != null ? ChordKey.TonicOf(chords, null) : 0;
            UiKit.Show(vocalLabel, song >= 0);
            if (scrolling) windowDirty = true;
            else BuildVocalStrip();
        }

        void BuildChordStrip()
        {
            chordStrip.Clear();
            cells.Clear();
            Rect area = ChordRect;
            chordStrip.AddRect(area.xMin, area.yMin, area.width, area.height, new Color(1, 1, 1, .05f));
            int label = 0;
            if (stripChords != null)
                foreach (MashupChord c in stripChords)
                    label = AddChordCell(c, BeatX(c.Start), BeatX(Math.Min(c.End, period)), ReferenceEquals(c, litChord), label);
            for (int i = label; i < romanLabels.Count; i++) UiKit.Show(romanLabels[i], false);
            chordStrip.Commit();
        }

        /// <summary>
        /// One chord cell between x0 and x1 (content px), named when it is wide enough (the full name,
        /// shrunk to fit, else the short form). <paramref name="lit"/>: the chord sounding (full colour);
        /// while one sounds, the others are muted.
        /// </summary>
        int AddChordCell(MashupChord c, float x0, float x1, bool lit, int label)
        {
            if (x1 - x0 < .5f) return label;
            Rect area = ChordRect;
            Color full = ChordColor(c, stripTonic);
            Color col = lit || litChord == null ? full : ChordPalette.Muted(full);
            cells.Add(new StripCell(c, x0, x1, lit, col));
            Color top = Color.Lerp(col, Color.white, lit ? .12f : .06f), bottom = Color.Lerp(col, Color.black, .18f);
            top.a = bottom.a = lit || litChord == null ? .97f : .9f;
            // A hairline between neighbouring cells.
            chordStrip.AddGradientRect(x0 + .5f, area.yMin, Mathf.Max(.5f, x1 - x0 - 1f), area.height, top, bottom);
            float avail = x1 - x0 - 6f;
            if (avail < 16f) return label;
            string suffix = ChordNames.Suffix(c.Quality);
            TextMeshProUGUI t = RomanLabel(label);
            t.fontStyle = lit ? FontStyles.Bold : FontStyles.Normal;
            string plain = "";
            foreach (string name in new[] { ChordNames.Name(c.RootPc, stripTonic, c.Quality), ChordNames.ShortName(c.RootPc, stripTonic) })
            {
                UiKit.SetText(t, ChordNames.Rich(name, suffix, 75));
                UiKit.FitWidth(t, avail, 13f, 9.5f);
                if (t.GetPreferredValues(t.text, 100000f, 0f).x <= avail + .5f)
                {
                    plain = ChordNames.Plain(name, suffix);
                    break;
                }
            }
            if (plain.Length == 0)
            {
                UiKit.Show(t, false);
                return label;
            }
            labelTexts[label] = plain;
            // The lit cell glows bright: white with a dark outline reads on it.
            if (lit) UiKit.Outline(t);
            else if (t.font != null && t.fontSharedMaterial != t.font.material) t.fontSharedMaterial = t.font.material;
            Color text = lit ? Color.white : ChordPalette.LabelOn(col);
            if (!lit && litChord != null) text.a *= .8f;
            t.color = text;
            UiKit.Place(t.rectTransform, x0, area.yMin, x1 - x0, area.height);
            return label + 1;
        }

        void BuildVocalStrip()
        {
            vocalStrip.Clear();
            if (vocalChords != null)
                foreach (MashupChord c in vocalChords)
                    AddVocalCell(c, BeatX(c.Start), BeatX(Math.Min(c.End, period)), ReferenceEquals(c, litVocal));
            vocalStrip.Commit();
        }

        void AddVocalCell(MashupChord c, float x0, float x1, bool lit)
        {
            if (x1 - x0 < .5f) return;
            Color col = ChordColor(c, vocalTonic);
            if (!lit && litVocal != null) col = ChordPalette.Muted(col);
            col.a = .92f;
            vocalStrip.AddRect(x0 + .5f, VocalTop, Mathf.Max(.5f, x1 - x0 - 1f), VocalStripHeight, col);
        }

        // ------------------------------------------------------------------ the panning window

        /// <summary>
        /// Draws everything that moves with the beat, in content px around <see cref="anchor"/>, for
        /// the plot's width plus a margin each side: bar lines, the chord strips, every melody (each
        /// sung piece at every image u + k·P that reaches the window).
        /// </summary>
        void BuildWindow()
        {
            windowDirty = false;
            WindowBuilds++;
            Rect plot = Plot;
            double ppb = Math.Max(1e-3, PixelsPerBeat);
            double margin = RedrawPixels / ppb + 1;
            double lo = anchor - (ScrollHeadX - plot.xMin) / ppb - margin;
            double hi = anchor + (plot.xMax - ScrollHeadX) / ppb + margin;
            double p = period > 0 ? period : 32;

            // Bar lines and beat ticks; the phrase's / the loop's start stronger.
            bars.Clear();
            int bpb = Math.Max(1, beatsPerBar);
            int beatsInPeriod = (int)Math.Ceiling(p - 1e-6);
            (int k0, int k1) = MelodyScroll.Images(0, p, lo, hi, p);
            for (int k = k0; k <= k1; k++)
                for (int q = 0; q < beatsInPeriod; q++)
                {
                    double u = q + k * p;
                    if (u < lo || u > hi) continue;
                    float x = ContentX(u);
                    if (q % bpb == 0)
                    {
                        bool edge = q == 0;
                        bars.AddRect(x - (edge ? 1f : .5f), plot.yMin, edge ? 2f : 1f, ChordTop + ChordStripHeight - plot.yMin, new Color(1, 1, 1, edge ? (shownDuet != null ? .3f : .16f) : .07f));
                    }
                    else bars.AddRect(x - .5f, plot.yMax - 4f, 1f, 4f, new Color(1, 1, 1, .08f));
                }
            bars.Commit();

            // Chord strips.
            windowLo = lo;
            windowHi = hi;
            DrawStripsWindow();

            // Melodies.
            foreach (LineView l in lines)
            {
                l.Color = LineColor(l);
                l.Width = LineWidth(l);
                l.Line.Clear();
                l.Glow.Clear();
                l.Points.Clear();
                l.Pieces.Clear();
                foreach (MelodyScroll.Piece piece in l.Sung)
                {
                    (int a, int b) = MelodyScroll.Images(piece.First, piece.Last, lo, hi, p);
                    for (int k = a; k <= b; k++)
                    {
                        scratch.Clear();
                        double shift = k * p;
                        for (int i = 0; i < piece.Count; i++)
                        {
                            double u = piece.Beats[i] + shift;
                            // The points in the window, and one beyond each edge so the line runs out of it.
                            bool inside = u >= lo && u <= hi;
                            bool nextInside = i + 1 < piece.Count && piece.Beats[i + 1] + shift >= lo;
                            bool previousInside = i > 0 && piece.Beats[i - 1] + shift <= hi;
                            if (!inside && !(u < lo && nextInside) && !(u > hi && previousInside)) continue;
                            scratch.Add(new Vector2(ContentX(u), PitchY(piece.Pitches[i])));
                        }
                        if (scratch.Count == 0) continue;
                        if (scratch.Count == 1) scratch.Add(scratch[0] + new Vector2(2f, 0f));   // a lone point: a tiny dash
                        List<Vector2> drawn = new(scratch);
                        l.Pieces.Add(drawn);
                        l.Points.AddRange(drawn);
                        AddStyled(l, drawn);
                    }
                }
                l.Line.Commit();
                l.Glow.Commit();
            }
        }

        /// <summary>
        /// The panning graph's chord strips over the drawn window (every image of every chord that
        /// reaches it); only the sounding chord's image under the playhead is lit.
        /// </summary>
        void DrawStripsWindow()
        {
            double lo = windowLo, hi = windowHi, p = period > 0 ? period : 32;
            chordStrip.Clear();
            cells.Clear();
            Rect area = ChordRect;
            chordStrip.AddRect(ContentX(lo), area.yMin, ContentX(hi) - ContentX(lo), area.height, new Color(1, 1, 1, .05f));
            int label = 0;
            vocalStrip.Clear();
            foreach ((IReadOnlyList<MashupChord>? list, bool vocal) in new[] { (stripChords, false), (vocalChords, true) })
            {
                if (list == null) continue;
                MashupChord? lit = vocal ? litVocal : litChord;
                foreach (MashupChord c in list)
                {
                    double end = Math.Min(c.End, p);
                    (int a, int b) = MelodyScroll.Images(c.Start, end, lo, hi, p);
                    for (int k = a; k <= b; k++)
                    {
                        float x0 = ContentX(Math.Max(lo, c.Start + k * p)), x1 = ContentX(Math.Min(hi, end + k * p));
                        bool on = ReferenceEquals(c, lit) && k == litImage;
                        if (vocal) AddVocalCell(c, x0, x1, on);
                        else label = AddChordCell(c, x0, x1, on, label);
                    }
                }
            }
            for (int i = label; i < romanLabels.Count; i++) UiKit.Show(romanLabels[i], false);
            chordStrip.Commit();
            vocalStrip.Commit();
        }

        // ------------------------------------------------------------------ legend

        void BuildLegend()
        {
            while (legend.Count < songs.Count)
            {
                Image swatch = UiKit.Image($"Legend Swatch {legend.Count + 1}", panel, Color.white, 1);
                TextMeshProUGUI text = UiKit.Text($"Legend {legend.Count + 1}", panel, 13, MutedColor);
                legend.Add((swatch, text));
            }
            for (int i = 0; i < legend.Count; i++)
            {
                bool on = i < songs.Count;
                UiKit.Show(legend[i].swatch, on);
                UiKit.Show(legend[i].text, on);
            }
        }

        void RefreshLegend()
        {
            // Right-aligned on the title row: "▬ 1974 No Woman, No Cry", the playing ones bright.
            float right = PanelWidth - 20f, left = shownDuet != null ? 530f : 470f, gap = 22f;
            float size = 13f;
            List<float> widths = new();
            for (int pass = 0; pass < 3; pass++)
            {
                widths.Clear();
                float total = 0;
                for (int i = 0; i < songs.Count; i++)
                {
                    TextMeshProUGUI t = legend[i].text;
                    t.fontSize = size;
                    t.text = LegendEntry(songs[i], lines.Count > i && lines[i].Playing, lines.Count > i && lines[i].Backing);
                    float w = Mathf.Min(260f, t.GetPreferredValues(t.text, 2000, 0).x) + 22f;
                    widths.Add(w);
                    total += w + (i > 0 ? gap : 0);
                }
                if (total <= right - left || size <= 11f) break;
                size -= 1f;
            }
            float x = right;
            for (int i = songs.Count - 1; i >= 0; i--)
            {
                (Image swatch, TextMeshProUGUI text) = legend[i];
                bool playing = lines.Count > i && lines[i].Playing;
                float w = widths[i];
                x -= w;
                UiKit.Place(swatch.rectTransform, Mathf.Max(left, x), 19f, 16f, playing ? 4f : 3f);
                swatch.color = playing ? BrightColor(i) : MutedSongColor(i);
                text.color = playing ? TextColor : MutedColor;
                UiKit.Place(text.rectTransform, Mathf.Max(left, x) + 22f, 11f, w - 22f, 20f);
                x -= gap;
            }
        }

        /// <summary>"1974  No Woman, No Cry": the playing melody's title bold, the backing's (changeovers) marked.</summary>
        static string LegendEntry(MashupSong s, bool playing, bool backing = false)
        {
            string year = s.Year > 0 ? s.Year.ToString(CultureInfo.InvariantCulture) : "";
            string title = GraphHud.Esc(s.Title.Length > 0 ? s.Title : s.WorkId);
            if (playing) return $"<color={GraphHud.Muted}>{year}</color>  <b>{title}</b>";
            return backing ? $"{year}  {title} <size=80%>(backing)</size>" : $"{year}  {title}";
        }
    }
}
