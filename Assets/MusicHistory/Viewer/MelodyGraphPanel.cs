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
    /// The melody graph shown while a featured path plays its mashup mix (M or the strip's
    /// "Melody" button toggles it). x is the position in the shared phrase (bars marked, the
    /// chord progression's roman numerals in the chord strip), y the sung pitch in the normalized
    /// C major / A minor frame (note-name ticks). Every song's melody of the path is drawn on top
    /// of the others in its own muted colour (legend: title, year); the melodies whose vocal is
    /// audible right now are bright and thicker, each with a point of bloom light riding on it at
    /// the current phrase beat and the melody's pitch there. Along the bottom, the chord colour
    /// strip of the instrumental playing (the viewer's key palette: circle-of-fifths hue, minor
    /// darker), and during a changeover a thin strip of the vocal's own chords above it; a
    /// playhead runs through the graph and the strips.
    ///
    /// The panel is on its own Screen Space - Camera canvas on the view camera, directly above the
    /// now-playing strip, the same width, clear of the HUD. uGUI is drawn after URP's
    /// post-processing, so the light points come from <see cref="MelodyLightRig"/>: HDR quads that
    /// a dedicated camera renders with its own bloom into a texture, added onto the panel exactly
    /// where the lights ride (the graph's own bloom and look are untouched).
    /// </summary>
    public sealed class MelodyGraphPanel : MonoBehaviour
    {
        public const float PanelWidth = 1240f;
        public const float PanelHeight = 272f;
        /// <summary>Gap between the now-playing strip and this panel.</summary>
        public const float Gap = 10f;
        /// <summary>Bottom of the panel above the screen's bottom edge (reference px).</summary>
        public static float PanelBottom => 16f + FeaturedPathsPanel.StripHeight + Gap;

        const float PlotLeft = 64f, PlotRightPad = 22f, PlotTop = 52f;
        const float ChordStripHeight = 26f, VocalStripHeight = 5f, BottomPad = 14f;
        static float ChordTop => PanelHeight - BottomPad - ChordStripHeight;
        static float VocalTop => ChordTop - 3f - VocalStripHeight;
        static float PlotBottom => VocalTop - 6f;

        [Tooltip("Bloom intensity of the light points' own camera (the graph's bloom is not changed).")]
        [Min(0f)] public float LightBloom = 4f;
        [Tooltip("How bright a light point stays while the voice rests between phrases (1 = singing).")]
        [Range(0f, 1f)] public float RestLevel = .35f;
        [Tooltip("Brightness of the backing's own melody's light during a changeover (1 = the vocal singing over it).")]
        [Range(0f, 1f)] public float BackingLevel = .45f;

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
            /// <summary>Points drawn (panel px, y down), every piece.</summary>
            public readonly List<Vector2> Points = new();
            public readonly List<List<Vector2>> Pieces = new();
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
        }

        SongGraphLoader loader = null!;
        Canvas? canvas;
        RectTransform? root;
        RectTransform panel = null!;
        TextMeshProUGUI kicker = null!, subtitle = null!, playheadTag = null!, chordsLabel = null!, vocalLabel = null!, emptyText = null!;
        UiShapes grid = null!, chordStrip = null!, vocalStrip = null!;
        Image playhead = null!, playheadCap = null!, chordUnderline = null!, chordCurrent = null!;
        readonly List<TextMeshProUGUI> pitchLabels = new();
        readonly List<TextMeshProUGUI> romanLabels = new();
        readonly List<(Image swatch, TextMeshProUGUI text)> legend = new();
        readonly List<LineView> lines = new();
        readonly List<DotView> dots = new();
        MelodyLightRig? rig;
        RectTransform linesRoot = null!, dotsRoot = null!;

        Mashup? shown;
        float pitchLo = 55, pitchHi = 79;
        int stripSong = -2, vocalStripSong = -2, backingSong = -1;
        int tagBar = -1;
        string tagRoman = "";
        readonly List<int> vocals = new(), instrumentals = new();

        /// <summary>The user's toggle (M, the strip's button): the graph shows while a mashup plays.</summary>
        public bool UserVisible { get; private set; } = true;
        public bool Showing => root != null && root.gameObject.activeSelf;
        public Mashup? Shown => Showing ? shown : null;
        public RectTransform? PanelRect => root != null ? panel : null;
        public Canvas? Canvas => canvas;
        public IReadOnlyList<LineView> Lines => lines;
        public IReadOnlyList<DotView> Dots => dots;
        /// <summary>Phrase beat under the playhead.</summary>
        public double PhraseBeat { get; private set; }
        /// <summary>Playhead x in panel px.</summary>
        public float PlayheadX { get; private set; }
        /// <summary>Mashup song whose chords the strip shows (the instrumental playing), -1 when none.</summary>
        public int StripSong => stripSong;
        /// <summary>Song whose own chords the thin strip shows during a changeover (-1 when hidden).</summary>
        public int VocalStripSong => vocalStripSong;
        /// <summary>Song whose own melody is highlighted as the backing's during a changeover (-1 otherwise).</summary>
        public int BackingSong => backingSong;
        public float PitchLow => pitchLo;
        public float PitchHigh => pitchHi;
        /// <summary>The bloomed light points (null without the MusicHistory/MelodyGlow shader).</summary>
        public MelodyLightRig? Lights => rig;

        /// <summary>The plot area in panel px (y down).</summary>
        public static Rect PlotArea => new(PlotLeft, PlotTop, PanelWidth - PlotLeft - PlotRightPad, PlotBottom - PlotTop);
        public static Rect ChordArea => new(PlotLeft, ChordTop, PanelWidth - PlotLeft - PlotRightPad, ChordStripHeight);

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

        /// <summary>The roman numerals in the chord strip, in order (for validation).</summary>
        public string ChordText
        {
            get
            {
                StringBuilder b = new();
                foreach (TextMeshProUGUI t in romanLabels)
                    if (t.gameObject.activeSelf) b.Append(t.text).Append(' ');
                return b.ToString().Trim();
            }
        }

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

            panel = UiKit.Rect("Melody Graph", root);
            panel.anchorMin = panel.anchorMax = new Vector2(.5f, 0f);
            panel.pivot = new Vector2(.5f, 0f);
            panel.anchoredPosition = new Vector2(0, PanelBottom);
            panel.sizeDelta = new Vector2(PanelWidth, PanelHeight);
            panel.gameObject.AddComponent<CanvasRenderer>();
            Image back = panel.gameObject.AddComponent<Image>();
            back.sprite = UiKit.Rounded(14);
            back.type = Image.Type.Sliced;
            back.color = SheetColor;
            back.raycastTarget = false;

            kicker = UiKit.Text("Kicker", panel, 12, Accent, TextAlignmentOptions.MidlineLeft, bold: true);
            kicker.characterSpacing = 6;
            kicker.text = "MELODY GRAPH";
            UiKit.Place(kicker.rectTransform, 20, 11, 160, 20);
            subtitle = UiKit.Text("Subtitle", panel, 13, MutedColor);
            UiKit.Place(subtitle.rectTransform, 160, 11, 300, 20);

            grid = Shapes("Grid", panel);
            chordStrip = Shapes("Chord Strip", panel);
            vocalStrip = Shapes("Vocal Chord Strip", panel);
            chordCurrent = UiKit.Image("Current Chord", panel, new Color(1, 1, 1, .16f));
            chordUnderline = UiKit.Image("Current Chord Underline", panel, new Color(1, 1, 1, .9f), 1);
            chordsLabel = UiKit.Text("Chords Label", panel, 10, DimColor, TextAlignmentOptions.MidlineRight);
            chordsLabel.text = "chords";
            UiKit.Place(chordsLabel.rectTransform, 4, ChordTop, PlotLeft - 10, ChordStripHeight);
            vocalLabel = UiKit.Text("Vocal Label", panel, 9, DimColor, TextAlignmentOptions.MidlineRight);
            vocalLabel.text = "vocal's own";
            UiKit.Place(vocalLabel.rectTransform, 4, VocalTop - 4, PlotLeft - 10, VocalStripHeight + 8);

            linesRoot = UiKit.Rect("Melodies", panel);
            UiKit.Place(linesRoot, 0, 0, PanelWidth, PanelHeight);
            playhead = UiKit.Image("Playhead", panel, new Color(1, 1, 1, .72f));
            playheadCap = UiKit.Dot("Playhead Cap", panel, new Color(1, 1, 1, .9f));
            playheadTag = UiKit.Text("Playhead Tag", panel, 11, TextColor, TextAlignmentOptions.Center, bold: true);
            dotsRoot = UiKit.Rect("Light Points", panel);
            UiKit.Place(dotsRoot, 0, 0, PanelWidth, PanelHeight);
            emptyText = UiKit.Text("Empty", panel, 14, MutedColor, TextAlignmentOptions.Center);
            UiKit.Place(emptyText.rectTransform, PlotLeft, PlotTop, PlotArea.width, PlotArea.height);
            emptyText.text = "No sung melody was tracked for these songs.";

            Shader? glow = Shader.Find(GlowShaderName);
            if (glow != null) rig = new MelodyLightRig(owner.transform, panel, PanelWidth, PanelHeight, glow) { BloomIntensity = LightBloom };
            else Debug.LogWarning($"MusicHistory: shader {GlowShaderName} not found; the melody graph's light points will not bloom.");
            shown = null;
            root.gameObject.SetActive(false);
        }

        public const string GlowShaderName = "MusicHistory/MelodyGlow";

        /// <summary>HDR intensity of a glow material (0 when it has none).</summary>
        public static float IntensityOf(Material? m) => m != null && m.HasProperty("_Intensity") ? m.GetFloat("_Intensity") : 0f;

        static UiShapes Shapes(string name, Transform parent)
        {
            RectTransform r = UiKit.Rect(name, parent);
            UiKit.Place(r, 0, 0, PanelWidth, PanelHeight);
            r.gameObject.AddComponent<CanvasRenderer>();
            UiShapes s = r.gameObject.AddComponent<UiShapes>();
            s.raycastTarget = false;
            return s;
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

        /// <summary>Follows the mashup playing now (public so edit-mode validation can drive it).</summary>
        public void Refresh()
        {
            if (root == null || loader == null) return;
            WalkthroughDirector? d = loader.Director;
            Mashup? m = d != null ? d.CurrentMashup : null;
            bool show = m != null && UserVisible;
            if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
            if (!show || m == null || d == null)
            {
                rig?.Sync(false, 1f, false);
                return;
            }
            AttachCamera();
            if (!ReferenceEquals(m, shown)) Rebuild(m);

            double t = d.MixSeconds;
            m.AudibleAt(t, vocals, instrumentals);
            MashupSegment? seg = m.SegmentAt(t);
            double beat = m.PhraseBeatAt(t);
            PhraseBeat = beat;

            // Chord strips: the instrumental playing; during a changeover, the vocal's own chords above it.
            int inst = seg != null ? seg.InstrumentalSong : instrumentals.Count > 0 ? instrumentals[0] : -1;
            if (inst != stripSong) BuildChordStrip(m, inst);
            int vocalOwn = seg != null && seg.Kind == MashupSegmentKind.Changeover && seg.VocalSong >= 0 && seg.VocalSong != inst ? seg.VocalSong : -1;
            if (vocalOwn != vocalStripSong) BuildVocalStrip(m, vocalOwn);

            // During a changeover the backing's own melody (the tune this instrumental was made for)
            // is highlighted too, a step below the vocal singing over it: two melodies on one track.
            backingSong = seg != null && seg.Kind == MashupSegmentKind.Changeover && seg.InstrumentalSong >= 0 &&
                          !vocals.Contains(seg.InstrumentalSong) ? seg.InstrumentalSong : -1;

            // Melody styles.
            bool restyled = false;
            foreach (LineView l in lines)
            {
                bool playing = vocals.Contains(l.Song);
                bool backing = !playing && l.Song == backingSong;
                if (playing == l.Playing && backing == l.Backing) continue;
                l.Playing = playing;
                l.Backing = backing;
                StyleLine(l);
                restyled = true;
            }
            if (restyled)
            {
                // Muted melodies at the bottom, then the backing's, the playing ones on top.
                foreach (LineView l in lines) if (!l.Playing && !l.Backing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.Backing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.Playing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                RefreshLegend(m);
            }

            // Playhead through the graph and the strips.
            Rect plot = PlotArea;
            float x = BeatX(beat);
            PlayheadX = x;
            UiKit.Place(playhead.rectTransform, x - 1f, plot.yMin - 4f, 2f, ChordTop + ChordStripHeight + 3f - (plot.yMin - 4f));
            UiKit.Place(playheadCap.rectTransform, x - 4f, plot.yMin - 8f, 8f, 8f);
            int bar = m.BeatsPerBar > 0 ? (int)Math.Floor(beat / m.BeatsPerBar) + 1 : 1;
            MashupChord? chord = inst >= 0 ? m.Songs[inst].ChordAt(beat) : null;
            // The tag text changes once a bar or chord (no string per frame).
            string roman = chord != null ? chord.Roman : "";
            if (bar != tagBar || !ReferenceEquals(roman, tagRoman))
            {
                tagBar = bar;
                tagRoman = roman;
                UiKit.SetText(playheadTag, roman.Length > 0 ? $"bar {bar} · {GraphHud.Esc(roman)}" : $"bar {bar}");
            }
            float tagW = 90f;
            UiKit.Place(playheadTag.rectTransform, Mathf.Clamp(x - tagW / 2, plot.xMin, plot.xMax - tagW), plot.yMin - 18f, tagW, 13f);
            if (chord != null)
            {
                float x0 = BeatX(chord.Start), x1 = BeatX(Math.Min(chord.End, m.PhraseBeats));
                UiKit.Show(chordCurrent, true);
                UiKit.Show(chordUnderline, true);
                UiKit.Place(chordCurrent.rectTransform, x0, ChordTop, Mathf.Max(1f, x1 - x0), ChordStripHeight);
                UiKit.Place(chordUnderline.rectTransform, x0 + 1f, ChordTop + ChordStripHeight + 2f, Mathf.Max(1f, x1 - x0 - 2f), 3f);
            }
            else
            {
                UiKit.Show(chordCurrent, false);
                UiKit.Show(chordUnderline, false);
            }

            // A point of light on every playing melody, at this beat and the melody's pitch here
            // (and a dimmer one on the backing's own melody during a changeover).
            bool tinted = false;
            foreach (DotView dot in dots)
            {
                MashupSong song = m.Songs[dot.Song];
                float p = float.NaN;
                bool v = false;
                bool backing = dot.Song == backingSong;
                bool on = (vocals.Contains(dot.Song) || backing) && song.PitchAt(beat, out p, out v);
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
                Vector2 at = PanelPoint(beat, p);
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

        /// <summary>Screen pixels per reference pixel on the view camera (as the HUD's scale-with-screen-size).</summary>
        float PixelScale()
        {
            Camera? cam = loader != null ? loader.ViewCamera : null;
            if (cam == null || loader.Hud == null) return 1f;
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

        float BeatX(double beat)
        {
            Rect plot = PlotArea;
            double p = shown != null && shown.PhraseBeats > 0 ? shown.PhraseBeats : 32;
            return plot.xMin + (float)(Math.Max(0, Math.Min(p, beat)) / p) * plot.width;
        }

        float PitchY(float pitch)
        {
            Rect plot = PlotArea;
            float u = Mathf.InverseLerp(pitchLo, pitchHi, pitch);
            return plot.yMax - u * plot.height;
        }

        /// <summary>Where (phrase beat, pitch) sits on the panel (px from its top-left, y down).</summary>
        public Vector2 PanelPoint(double beat, float pitch) => new(BeatX(beat), PitchY(pitch));

        /// <summary>
        /// Screen position (pixels, origin bottom-left) of panel point <paramref name="p"/> on a
        /// <paramref name="width"/> x <paramref name="height"/> screen (scale-with-screen-size, as the HUD).
        /// </summary>
        public Vector2 ScreenPoint(Vector2 p, float width, float height)
        {
            float scale = loader != null && loader.Hud != null ? loader.Hud.ScaleFor(width, height) : height / 1080f;
            float left = width * .5f - PanelWidth * .5f * scale;
            float bottom = PanelBottom * scale;
            return new Vector2(left + p.x * scale, bottom + (PanelHeight - p.y) * scale);
        }

        // ------------------------------------------------------------------ rebuild for a mashup

        void Rebuild(Mashup m)
        {
            shown = m;
            tagBar = -1;
            stripSong = -2;
            vocalStripSong = -2;
            if (m.PitchRange(out float lo, out float hi))
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
            UiKit.SetText(subtitle, $"{m.Bars} bar{(m.Bars == 1 ? "" : "s")} · {m.BeatsPerBar}/4 · in C major / A minor");
            BuildGrid(m);

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
            for (int i = 0; i < m.Songs.Count; i++)
            {
                MashupSong song = m.Songs[i];
                LineView l = new() { Song = i };
                l.Glow = Shapes($"Glow {i + 1}", linesRoot);
                l.Line = Shapes($"Melody {i + 1} {song.WorkId}", linesRoot);
                l.Glow.Feather = 6f;
                l.Line.Feather = 1.1f;
                List<Vector2>? piece = null;
                double lastBeat = double.NegativeInfinity;
                foreach (MelodyPoint p in song.Melody)
                {
                    if (!p.Voiced)
                    {
                        piece = null;
                        continue;
                    }
                    anyVoiced = true;
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
                l.Playing = false;
                StyleLine(l);
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
            rig?.EnsureLights(m.Songs.Count);
            UiKit.Show(emptyText, !anyVoiced);
            BuildLegend(m);
            RefreshLegend(m);
        }

        void StyleLine(LineView l)
        {
            Color bright = BrightColor(l.Song);
            l.Color = l.Playing ? bright : l.Backing ? BackingColor(l.Song) : MutedSongColor(l.Song);
            l.Width = l.Playing ? 3.2f : l.Backing ? 2.3f : 1.6f;
            l.Line.Clear();
            l.Glow.Clear();
            foreach (List<Vector2> piece in l.Pieces)
            {
                l.Line.AddPolyline(piece, l.Width, l.Color);
                if (l.Playing) l.Glow.AddPolyline(piece, 4f, new Color(bright.r, bright.g, bright.b, .22f));
                else if (l.Backing) l.Glow.AddPolyline(piece, 3f, new Color(bright.r, bright.g, bright.b, .10f));
            }
            l.Line.Commit();
            l.Glow.Commit();
        }

        static readonly int[] WhiteKeys = { 0, 2, 4, 5, 7, 9, 11 };
        static readonly string[] PitchNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public static string NoteName(int midi) => PitchNames[((midi % 12) + 12) % 12] + (midi / 12 - 1).ToString(CultureInfo.InvariantCulture);

        void BuildGrid(Mashup m)
        {
            Rect plot = PlotArea;
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
            // Bar lines (the phrase's first and last stronger), beat ticks along the top.
            int bpb = Math.Max(1, m.BeatsPerBar);
            for (int b = 0; b <= Math.Round(m.PhraseBeats); b++)
            {
                float x = BeatX(b);
                bool barLine = b % bpb == 0;
                bool edge = b == 0 || b >= m.PhraseBeats - 1e-6;
                if (barLine) grid.AddRect(x - .5f, plot.yMin, 1f, ChordTop + ChordStripHeight - plot.yMin, new Color(1, 1, 1, edge ? .16f : .07f));
                else grid.AddRect(x - .5f, plot.yMax - 4f, 1f, 4f, new Color(1, 1, 1, .08f));
            }
            grid.Commit();
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
                romanLabels.Add(UiKit.Text($"Chord {romanLabels.Count + 1}", panel, 13, TextColor, TextAlignmentOptions.Center, bold: true));
            TextMeshProUGUI t = romanLabels[i];
            UiKit.Show(t, true);
            return t;
        }

        /// <summary>Colour of a chord cell: the key palette (circle-of-fifths hue, minor darker) at its root.</summary>
        public static Color ChordColor(MashupChord c) => SongPalette.KeyColor(c.RootPc, c.Minor);

        void BuildChordStrip(Mashup m, int song)
        {
            stripSong = song;
            chordStrip.Clear();
            Rect area = ChordArea;
            chordStrip.AddRect(area.xMin, area.yMin, area.width, area.height, new Color(1, 1, 1, .05f));
            int label = 0;
            if (song >= 0)
            {
                foreach (MashupChord c in m.Songs[song].Chords)
                {
                    float x0 = BeatX(c.Start), x1 = BeatX(Math.Min(c.End, m.PhraseBeats));
                    if (x1 - x0 < .5f) continue;
                    Color col = ChordColor(c);
                    Color top = Color.Lerp(col, Color.white, .08f), bottom = Color.Lerp(col, Color.black, .18f);
                    top.a = bottom.a = .95f;
                    // A hairline between neighbouring cells.
                    chordStrip.AddGradientRect(x0 + .5f, area.yMin, Mathf.Max(.5f, x1 - x0 - 1f), area.height, top, bottom);
                    if (x1 - x0 < 18f || c.Roman.Length == 0) continue;
                    TextMeshProUGUI t = RomanLabel(label++);
                    t.text = GraphHud.Esc(c.Roman);
                    float lum = .299f * col.r + .587f * col.g + .114f * col.b;
                    t.color = lum > .55f ? new Color(.06f, .07f, .08f, .92f) : new Color(1, 1, 1, .95f);
                    UiKit.Place(t.rectTransform, x0, area.yMin, x1 - x0, area.height);
                }
            }
            for (int i = label; i < romanLabels.Count; i++) UiKit.Show(romanLabels[i], false);
            chordStrip.Commit();
        }

        void BuildVocalStrip(Mashup m, int song)
        {
            vocalStripSong = song;
            vocalStrip.Clear();
            UiKit.Show(vocalLabel, song >= 0);
            if (song >= 0)
            {
                foreach (MashupChord c in m.Songs[song].Chords)
                {
                    float x0 = BeatX(c.Start), x1 = BeatX(Math.Min(c.End, m.PhraseBeats));
                    if (x1 - x0 < .5f) continue;
                    Color col = ChordColor(c);
                    col.a = .9f;
                    vocalStrip.AddRect(x0 + .5f, VocalTop, Mathf.Max(.5f, x1 - x0 - 1f), VocalStripHeight, col);
                }
            }
            vocalStrip.Commit();
        }

        void BuildLegend(Mashup m)
        {
            while (legend.Count < m.Songs.Count)
            {
                Image swatch = UiKit.Image($"Legend Swatch {legend.Count + 1}", panel, Color.white, 1);
                TextMeshProUGUI text = UiKit.Text($"Legend {legend.Count + 1}", panel, 13, MutedColor);
                legend.Add((swatch, text));
            }
            for (int i = 0; i < legend.Count; i++)
            {
                bool on = i < m.Songs.Count;
                UiKit.Show(legend[i].swatch, on);
                UiKit.Show(legend[i].text, on);
            }
        }

        void RefreshLegend(Mashup m)
        {
            // Right-aligned on the title row: "▬ 1974 No Woman, No Cry", the playing ones bright.
            float right = PanelWidth - 20f, left = 470f, gap = 22f;
            float size = 13f;
            List<float> widths = new();
            for (int pass = 0; pass < 3; pass++)
            {
                widths.Clear();
                float total = 0;
                for (int i = 0; i < m.Songs.Count; i++)
                {
                    TextMeshProUGUI t = legend[i].text;
                    t.fontSize = size;
                    t.text = LegendEntry(m.Songs[i], lines.Count > i && lines[i].Playing, lines.Count > i && lines[i].Backing);
                    float w = Mathf.Min(260f, t.GetPreferredValues(t.text, 2000, 0).x) + 22f;
                    widths.Add(w);
                    total += w + (i > 0 ? gap : 0);
                }
                if (total <= right - left || size <= 11f) break;
                size -= 1f;
            }
            float x = right;
            for (int i = m.Songs.Count - 1; i >= 0; i--)
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

        static string LegendEntry(MashupSong s, bool playing)
        {
            string year = s.Year > 0 ? s.Year.ToString(CultureInfo.InvariantCulture) : "";
            string title = GraphHud.Esc(s.Title.Length > 0 ? s.Title : s.WorkId);
            return playing
                ? $"<color={GraphHud.Muted}>{year}</color>  <b>{title}</b>"
                : $"{year}  {title}";
        }
    }
}
