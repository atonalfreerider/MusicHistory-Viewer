#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using MusicHistory.Playback;
using MusicHistory.Walkthrough;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The melody graph of a melody mosaic (DESIGN.md §17), in both layouts: a timeline of the mix's
    /// beats, loop after loop, panning right to left under the fixed centre playhead (one loop per
    /// plot width in landscape, three times larger in portrait), loop starts marked and tagged with
    /// their section; the chord strip the target loop's chords, the chord sounding lit.
    /// <list type="bullet">
    /// <item>Original loops: the target's melody, bright with a point of bloom light while it sings.</item>
    /// <item>Mosaic loops: the target's melody stays as a faint guide line; each piece is a span in
    /// its song's own colour (a distinct colour per song), labelled with the song's title and year,
    /// its notes drawn as heard (shifted and warped); the playing piece is bright with its light.</item>
    /// <item>Harmony loops: the target's melody and each harmony voice as lines of their own
    /// colours, every one with its light while it sounds.</item>
    /// </list>
    /// Only the loop playing is bright; the loops before and after it show muted.
    /// </summary>
    public sealed partial class MelodyGraphPanel
    {
        /// <summary>The target's colour (warm white); every other song of a mosaic takes the next colour of <see cref="MosaicPalette"/>.</summary>
        public static readonly Color TargetColor = SongPalette.Hex("#fff1d6");

        /// <summary>A mosaic's song colours (the target's apart): distinct for up to this many songs.</summary>
        static readonly Color[] MosaicPalette =
        {
            SongPalette.Hex("#62c3ff"), SongPalette.Hex("#ff9a62"), SongPalette.Hex("#c39bff"), SongPalette.Hex("#6fe3a1"),
            SongPalette.Hex("#ffd45e"), SongPalette.Hex("#ff7fb4"), SongPalette.Hex("#79e6e0"), SongPalette.Hex("#ff6b6b"),
            SongPalette.Hex("#b5e655"), SongPalette.Hex("#9db4ff"), SongPalette.Hex("#e0a96d")
        };

        /// <summary>Distinct colours a mosaic's songs get (beyond the target's) before they repeat.</summary>
        public static int MosaicColorCount => MosaicPalette.Length;

        /// <summary>The colour of a mosaic's song (its colour slot, <see cref="MosaicSong.ColorSlot"/>): the target's warm white, then the palette.</summary>
        public static Color MosaicColor(int slot) =>
            slot <= 0 ? TargetColor : MosaicPalette[MosaicTimeline.PaletteIndex(slot, MosaicPalette.Length + 1) - 1];

        static Color MosaicMuted(Color c, float toward, float alpha)
        {
            Color m = Color.Lerp(c, new Color(.16f, .17f, .19f, 1f), toward);
            m.a = alpha;
            return m;
        }

        /// <summary>One piece span as drawn (content px; add <see cref="ContentShift"/> for the panel).</summary>
        public readonly struct MosaicSpan
        {
            public readonly int Piece, Loop;
            public readonly float X0, X1;
            /// <summary>The song's colour (its full colour, also when muted).</summary>
            public readonly Color Color;
            /// <summary>The piece playing now (bright).</summary>
            public readonly bool Lit;
            /// <summary>The label shown on it ("Fireflies 2009", "Fireflies", or "" when too narrow).</summary>
            public readonly string Label;

            public MosaicSpan(int piece, int loop, float x0, float x1, Color color, bool lit, string label)
            {
                Piece = piece;
                Loop = loop;
                X0 = x0;
                X1 = x1;
                Color = color;
                Lit = lit;
                Label = label;
            }
        }

        Mosaic? shownMosaic;
        UiShapes? spanShapes;
        RectTransform? spanLabelRoot;
        readonly List<TextMeshProUGUI> spanLabels = new(), loopTags = new();
        readonly List<MosaicSpan> spans = new();
        readonly List<string> loopTagTexts = new();
        // Line lookups: the target's line of each loop, a piece's / a harmony voice's line per loop.
        readonly List<int> targetLine = new();
        readonly Dictionary<(int loop, int piece), int> pieceLine = new();
        readonly Dictionary<(int loop, int voice), int> harmonyLine = new();
        MosaicTimeline.State mosaicNow;

        /// <summary>The melody mosaic shown (null while a mix, a duet loop or nothing shows).</summary>
        public Mosaic? ShownMosaic => Showing ? shownMosaic : null;
        /// <summary>Where the shown mosaic is: mix beat, loop, section, piece (as of the last refresh).</summary>
        public MosaicTimeline.State MosaicNow => mosaicNow;
        /// <summary>The mosaic's mix beat under the playhead (beats from the first, across the loops).</summary>
        public double MosaicBeat => mosaicNow.Beat;
        /// <summary>The piece spans as last drawn (content px).</summary>
        public IReadOnlyList<MosaicSpan> Spans => spans;
        /// <summary>The loop tags as last drawn ("LOOP 3 · MOSAIC").</summary>
        public IReadOnlyList<string> LoopTagTexts => loopTagTexts;

        // ------------------------------------------------------------------ build

        /// <summary>The spans and their labels, between the bar lines and the melodies (Build).</summary>
        void BuildMosaicLayers()
        {
            spanShapes = Shapes("Piece Spans", content);
            spanLabelRoot = UiKit.Rect("Piece Labels", content);
            spanLabels.Clear();
            loopTags.Clear();
        }

        void LayoutMosaicLayers()
        {
            if (spanShapes != null) UiKit.Place(spanShapes.rectTransform, 0, 0, PanelWidth, height);
            if (spanLabelRoot != null) UiKit.Place(spanLabelRoot, 0, 0, PanelWidth, height);
        }

        /// <summary>Another view (a mix, a duet loop) takes the graph: the mosaic's spans and tags go.</summary>
        void ForgetMosaic()
        {
            shownMosaic = null;
            spans.Clear();
            loopTagTexts.Clear();
            targetLine.Clear();
            pieceLine.Clear();
            harmonyLine.Clear();
            mosaicNow = default;
            if (spanShapes != null)
            {
                spanShapes.Clear();
                spanShapes.Commit();
            }
            foreach (TextMeshProUGUI t in spanLabels) UiKit.Show(t, false);
            foreach (TextMeshProUGUI t in loopTags) UiKit.Show(t, false);
        }

        void RebuildMosaic(Mosaic mo, bool portraitLayout)
        {
            shown = null;
            shownDuet = null;
            ForgetMosaic();
            shownMosaic = mo;
            portrait = portraitLayout;
            scrolling = true;
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
            double lb = mo.LoopBeats > 0 ? mo.LoopBeats : 16;
            period = lb;
            phraseBeats = lb;
            beatsPerBar = Math.Max(1, mo.BeatsPerBar);
            LayoutChrome();
            Rect plot = Plot;
            // One loop per plot width in landscape, three times larger in portrait.
            PixelsPerBeat = (float)(plot.width / lb) * (portrait ? PortraitScale : 1f);

            if (mo.PitchRange(out float lo, out float hi))
            {
                lo = Mathf.Floor(lo - 1.5f);
                hi = Mathf.Ceil(hi + 2.5f);   // room for the span labels along the top
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
            UiKit.SetText(kicker, "MELODY MOSAIC");
            UiKit.SetText(subtitle, $"loop of {mo.LoopBars} bars · {GraphHud.Esc(mo.Key)} · {mo.Bpm.ToString("0.#", CultureInfo.InvariantCulture)} BPM · in C major / A minor");
            UiKit.Place(subtitle.rectTransform, 190, 11, 340, 20);
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
            List<MosaicTimeline.LineSpec> specs = MosaicTimeline.Lines(mo);
            for (int i = 0; i < specs.Count; i++)
            {
                MosaicTimeline.LineSpec spec = specs[i];
                LineView l = new() { Song = spec.Song, MosaicLine = spec, Sung = spec.Sung };
                string what = spec.Kind == MosaicTimeline.Voice.Piece ? $"Piece {spec.Piece + 1}" : spec.Kind == MosaicTimeline.Voice.Harmony ? $"Harmony {spec.Harmony + 1}" : "Target";
                l.Glow = Shapes($"Glow {i + 1}", linesRoot);
                l.Line = Shapes($"Loop {spec.Loop + 1} {what}", linesRoot);
                l.Glow.Feather = 6f;
                l.Line.Feather = 1.1f;
                l.Playing = false;
                switch (spec.Kind)
                {
                    case MosaicTimeline.Voice.Target:
                        while (targetLine.Count <= spec.Loop) targetLine.Add(-1);
                        targetLine[spec.Loop] = i;
                        break;
                    case MosaicTimeline.Voice.Piece:
                        pieceLine[(spec.Loop, spec.Piece)] = i;
                        break;
                    default:
                        harmonyLine[(spec.Loop, spec.Harmony)] = i;
                        break;
                }
                lines.Add(l);
            }
            // Lights: the target, the playing piece, each harmony voice.
            int lights = 2 + mo.Harmonies.Count;
            for (int i = 0; i < lights; i++)
            {
                DotView d = new() { Song = i };
                d.Halo = UiKit.Image($"Halo {i + 1}", dotsRoot, Color.white);
                d.Halo.sprite = UiKit.SoftDot();
                d.Halo.type = Image.Type.Simple;
                d.Core = UiKit.Image($"Light {i + 1}", dotsRoot, Color.white);
                d.Core.sprite = UiKit.Circle();
                d.Core.type = Image.Type.Simple;
                UiKit.Show(d.Core, false);
                UiKit.Show(d.Halo, false);
                dots.Add(d);
            }
            rig?.EnsureLights(lights);
            UiKit.Show(emptyText, mo.Notes.Count == 0);
            bars.Clear();
            bars.Commit();
            BuildMosaicLegend(mo);
            windowDirty = true;
            anchor = 0;
        }

        // ------------------------------------------------------------------ follow the mix

        void RefreshMosaic(WalkthroughDirector d, Mosaic mo)
        {
            AttachCamera();
            bool portraitNow = loader.Layout != null && loader.Layout.Format == ScreenFormat.Vertical;
            if (!ReferenceEquals(mo, shownMosaic) || portraitNow != portrait || !scrolling) RebuildMosaic(mo, portraitNow);
            double lb = period > 0 ? period : 16;
            MosaicTimeline.State st = MosaicTimeline.At(mo, d.MosaicSeconds);
            // The playhead stays on the mix (its first beat at the start, the last loop's end at the end).
            double g = Math.Max(0, Math.Min(mo.TotalBeats, st.Beat));
            if (g != st.Beat) st = MosaicTimeline.AtBeat(mo, g);
            mosaicNow = st;
            double beat = st.LoopBeat;
            PhraseBeat = beat;
            if (!ReferenceEquals(stripChords, mo.Chords)) SetStrip(0, mo.Chords, ChordKey.TonicOf(mo.Chords, mo.Key));
            if (vocalStripSong != -1) SetVocalStrip(-1, null);
            MashupChord? chord = mo.ChordAt(beat);

            // Styles: only the voices sounding in the loop playing are bright.
            bool restyled = false;
            foreach (LineView l in lines)
            {
                if (l.MosaicLine == null) continue;
                bool playing = MosaicTimeline.Playing(l.MosaicLine, st);
                if (playing == l.Playing) continue;
                l.Playing = playing;
                restyled = true;
            }
            if (restyled)
            {
                // Guides at the bottom, then the muted lines, the playing ones on top.
                foreach (LineView l in lines) if (l.MosaicLine != null && l.MosaicLine.Guide) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.MosaicLine != null && !l.MosaicLine.Guide && !l.Playing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                foreach (LineView l in lines) if (l.Playing) { l.Glow.transform.SetAsLastSibling(); l.Line.transform.SetAsLastSibling(); }
                RefreshMosaicLegend(mo, st);
                windowDirty = true;
            }

            // Panning: the content shifts under the fixed playhead; it is redrawn a few beats on.
            double drift = g - anchor;
            if (windowDirty || Math.Abs(drift) * PixelsPerBeat > RedrawPixels)
            {
                anchor = g;
                drift = 0;
                BuildMosaicWindow(mo, st);
            }
            PlaceContent(-(float)(drift * PixelsPerBeat));

            // The chord sounding (in the loop playing) keeps its colour and its light; the others are muted.
            if (!ReferenceEquals(chord, litChord) || litVocal != null || st.Loop != litImage)
            {
                litChord = chord;
                litVocal = null;
                litImage = st.Loop;
                DrawStripsWindow();
            }

            // The playhead through the graph and the strip, at the panel's centre.
            Rect plot = Plot;
            float x = ScrollHeadX;
            PlayheadX = x;
            UiKit.Place(playhead.rectTransform, x - 1f, plot.yMin - 4f, 2f, ChordTop + ChordStripHeight + 3f - (plot.yMin - 4f));
            UiKit.Place(playheadCap.rectTransform, x - 4f, plot.yMin - 8f, 8f, 8f);
            int bar = (int)Math.Floor(Math.Min(beat, lb - 1e-6) / Math.Max(1, beatsPerBar)) + 1;
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
                double here = anchor + drift;
                float x0 = ContentX(here - (beat - chord.Start)), x1 = ContentX(here + (Math.Min(chord.End, lb) - beat));
                UiKit.Show(chordCurrent, false);
                UiKit.Show(chordUnderline, true);
                UiKit.Place(chordUnderline.rectTransform, x0 + 1f, ChordTop + ChordStripHeight + 2f, Mathf.Max(1f, x1 - x0 - 2f), 3f);
                float g0 = Mathf.Max(plot.xMin, x0 + ContentShift), g1 = Mathf.Min(plot.xMax, x1 + ContentShift);
                // A long chord (a one-chord loop) glows only near the playhead, not across the plot.
                if (g1 - g0 > MaxChordGlowWidth)
                {
                    float head = Mathf.Clamp(PlayheadX, g0 + MaxChordGlowWidth * .5f, g1 - MaxChordGlowWidth * .5f);
                    (g0, g1) = (head - MaxChordGlowWidth * .5f, head + MaxChordGlowWidth * .5f);
                }
                bool glow = g1 - g0 > 1f;
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

            // The lights: the target (original and harmony loops), the playing piece, each harmony voice.
            bool tinted = false;
            for (int i = 0; i < dots.Count; i++)
            {
                DotView dot = dots[i];
                int index = -1;
                if (i == 0)
                {
                    if (st.Kind != MosaicSectionKind.Mosaic && st.Loop >= 0 && st.Loop < targetLine.Count) index = targetLine[st.Loop];
                }
                else if (i == 1)
                {
                    if (st.Kind == MosaicSectionKind.Mosaic && st.Piece >= 0 && pieceLine.TryGetValue((st.Loop, st.Piece), out int pl)) index = pl;
                }
                else if (st.Kind == MosaicSectionKind.Harmony && harmonyLine.TryGetValue((st.Loop, i - 2), out int hl)) index = hl;
                float p = float.NaN;
                bool v = false;
                bool on = index >= 0 && lines[index].Playing && lines[index].MosaicLine is MosaicTimeline.LineSpec spec &&
                          MosaicTimeline.LightPitch(spec, g, beat, out p, out v);
                dot.Active = on;
                dot.Backing = false;
                dot.Line = on ? index : -1;
                UiKit.Show(dot.Core, on);
                UiKit.Show(dot.Halo, on);
                if (!on)
                {
                    rig?.SetLight(i, false, default, 0, 0, Color.black, 0);
                    continue;
                }
                Color bright = MosaicColor(lines[index].Song);
                dot.Voiced = v;
                dot.Pitch = p;
                Vector2 at = new(x, PitchY(p));
                dot.Position = at;
                float core = v ? 7f : 5f, halo = v ? 64f : 36f;
                UiKit.Place(dot.Core.rectTransform, at.x - core / 2, at.y - core / 2, core, core);
                UiKit.Place(dot.Halo.rectTransform, at.x - halo / 2, at.y - halo / 2, halo, halo);
                dot.Core.color = v ? Color.white : new Color(1, 1, 1, .6f);
                dot.Halo.color = new Color(bright.r, bright.g, bright.b, v ? .42f : .2f);
                rig?.SetLight(i, true, at, v ? 12f : 8f, v ? 46f : 28f, bright, v ? 1f : RestLevel);
                if (!tinted)
                {
                    rig?.SetTint(bright);
                    tinted = true;
                }
            }
            rig?.Sync(true, PixelScale(), false);
        }

        // ------------------------------------------------------------------ the panning window

        Color MosaicLineColor(LineView l)
        {
            MosaicTimeline.LineSpec spec = l.MosaicLine!;
            if (spec.Guide) return new Color(1f, 1f, 1f, spec.Loop == mosaicNow.Loop ? .3f : .2f);
            Color c = MosaicColor(spec.Song);
            if (l.Playing) return c;
            return spec.Kind == MosaicTimeline.Voice.Target ? MosaicMuted(c, .45f, .7f) : MosaicMuted(c, spec.Loop == mosaicNow.Loop ? .22f : .4f, .88f);
        }

        static float MosaicLineWidth(LineView l)
        {
            MosaicTimeline.LineSpec spec = l.MosaicLine!;
            if (spec.Guide) return 1.5f;
            if (l.Playing) return 3.4f;
            return spec.Kind == MosaicTimeline.Voice.Target ? 1.8f : 2.2f;
        }

        /// <summary>
        /// Draws everything that moves with the beat in content px around <see cref="anchor"/> (mix beats),
        /// for the plot's width plus a margin each side: beat ticks, bar lines and loop starts (their
        /// section tagged), the chord strip, the piece spans with their labels, every line.
        /// </summary>
        void BuildMosaicWindow(Mosaic mo, MosaicTimeline.State st)
        {
            windowDirty = false;
            WindowBuilds++;
            Rect plot = Plot;
            double ppb = Math.Max(1e-3, PixelsPerBeat);
            double margin = RedrawPixels / ppb + 1;
            double lo = anchor - (ScrollHeadX - plot.xMin) / ppb - margin;
            double hi = anchor + (plot.xMax - ScrollHeadX) / ppb + margin;
            double total = mo.TotalBeats, lb = period > 0 ? period : 16;
            int lbBeats = Math.Max(1, (int)Math.Round(lb));
            int bpb = Math.Max(1, beatsPerBar);

            // Beat ticks, bar lines, loop starts (a section's first loop brighter, in the accent), the mix's end.
            bars.Clear();
            int tags = 0;
            loopTagTexts.Clear();
            for (int u = Math.Max(0, (int)Math.Ceiling(lo)); u <= Math.Min(total, Math.Floor(hi)); u++)
            {
                float x = ContentX(u);
                int q = u % lbBeats;
                if (q == 0 || u >= total - 1e-6)
                {
                    int loop = u / lbBeats;
                    bool sectionStart = u < total - 1e-6 && mo.Sections.Exists(s => s.FirstLoop == loop);
                    Color c = sectionStart ? UiKit.WithAlpha(Accent, .55f) : new Color(1, 1, 1, .3f);
                    bars.AddRect(x - 1f, plot.yMin, 2f, ChordTop + ChordStripHeight - plot.yMin, c);
                    if (u < total - 1e-6)
                    {
                        MosaicSectionKind kind = mo.KindOfLoop(loop);
                        string text = $"LOOP {loop + 1} · {MosaicSection.KindName(kind).ToUpperInvariant()}";
                        TextMeshProUGUI tag = LoopTag(tags++);
                        UiKit.SetText(tag, text);
                        tag.color = loop == st.Loop ? new Color(.85f, .87f, .9f, 1f) : DimColor;
                        UiKit.Place(tag.rectTransform, x + 5f, plot.yMax - 15f, 150f, 12f);
                        loopTagTexts.Add(text);
                    }
                }
                else if (q % bpb == 0) bars.AddRect(x - .5f, plot.yMin, 1f, ChordTop + ChordStripHeight - plot.yMin, new Color(1, 1, 1, .07f));
                else bars.AddRect(x - .5f, plot.yMax - 4f, 1f, 4f, new Color(1, 1, 1, .08f));
            }
            for (int i = tags; i < loopTags.Count; i++) UiKit.Show(loopTags[i], false);
            bars.Commit();

            // The chord strip, over the mix only.
            windowLo = Math.Max(0, lo);
            windowHi = Math.Max(windowLo, Math.Min(total, hi));
            DrawStripsWindow();

            // The piece spans of the mosaic loops in the window, each labelled with its song.
            spans.Clear();
            int labels = 0;
            if (spanShapes != null)
            {
                spanShapes.Clear();
                foreach (LineView l in lines)
                {
                    MosaicTimeline.LineSpec? spec = l.MosaicLine;
                    if (spec == null || spec.Kind != MosaicTimeline.Voice.Piece) continue;
                    MosaicPiece piece = mo.Pieces[spec.Piece];
                    double a = spec.Loop * lb + piece.Start, b = spec.Loop * lb + piece.End;
                    if (b < lo || a > hi) continue;
                    float x0 = ContentX(Math.Max(a, lo)), x1 = ContentX(Math.Min(b, hi));
                    Color full = MosaicColor(spec.Song);
                    bool lit = l.Playing;
                    bool current = spec.Loop == st.Loop;
                    Color fill = UiKit.WithAlpha(full, lit ? .13f : current ? .07f : .045f);
                    spanShapes.AddRect(x0 + .5f, plot.yMin, Mathf.Max(.5f, x1 - x0 - 1f), plot.height, fill);
                    spanShapes.AddRect(x0 + .5f, plot.yMin, Mathf.Max(.5f, x1 - x0 - 1f), lit ? 4f : 3f, UiKit.WithAlpha(full, lit ? 1f : current ? .7f : .45f));
                    string label = "";
                    float avail = x1 - x0 - 8f;
                    if (avail >= 24f && x0 <= ContentX(a) + .01f)
                    {
                        TextMeshProUGUI t = SpanLabel(labels);
                        t.fontStyle = lit ? FontStyles.Bold : FontStyles.Normal;
                        string year = piece.Year > 0 ? $" <size=82%><color={(lit ? "#ffffff" : GraphHud.Muted)}>{piece.Year}</color></size>" : "";
                        foreach (string candidate in new[] { GraphHud.Esc(piece.Title) + year, GraphHud.Esc(piece.Title) })
                        {
                            UiKit.SetText(t, candidate);
                            UiKit.FitWidth(t, avail, 12f, 9f);
                            if (t.GetPreferredValues(t.text, 100000f, 0f).x <= avail + .5f)
                            {
                                label = candidate == GraphHud.Esc(piece.Title) ? piece.Title : piece.Year > 0 ? $"{piece.Title} {piece.Year}" : piece.Title;
                                break;
                            }
                        }
                        if (label.Length == 0) UiKit.Show(t, false);
                        else
                        {
                            Color text = lit ? Color.white : Color.Lerp(full, Color.white, .25f);
                            if (!current) text.a = .65f;
                            t.color = text;
                            UiKit.Place(t.rectTransform, x0 + 4f, plot.yMin + 5f, avail, 14f);
                            labels++;
                        }
                    }
                    spans.Add(new MosaicSpan(spec.Piece, spec.Loop, x0, x1, full, lit, label));
                }
                spanShapes.Commit();
            }
            for (int i = labels; i < spanLabels.Count; i++) UiKit.Show(spanLabels[i], false);

            // The lines.
            foreach (LineView l in lines)
            {
                if (l.MosaicLine == null) continue;
                l.Color = MosaicLineColor(l);
                l.Width = MosaicLineWidth(l);
                l.Line.Clear();
                l.Glow.Clear();
                l.Points.Clear();
                l.Pieces.Clear();
                if (l.MosaicLine.Last >= lo && l.MosaicLine.First <= hi)
                    foreach (MelodyScroll.Piece piece in l.Sung)
                    {
                        if (piece.Last < lo || piece.First > hi) continue;
                        scratch.Clear();
                        for (int i = 0; i < piece.Count; i++)
                        {
                            double u = piece.Beats[i];
                            bool inside = u >= lo && u <= hi;
                            bool nextInside = i + 1 < piece.Count && piece.Beats[i + 1] >= lo;
                            bool previousInside = i > 0 && piece.Beats[i - 1] <= hi;
                            if (!inside && !(u < lo && nextInside) && !(u > hi && previousInside)) continue;
                            scratch.Add(new Vector2(ContentX(u), PitchY(piece.Pitches[i])));
                        }
                        if (scratch.Count == 0) continue;
                        if (scratch.Count == 1) scratch.Add(scratch[0] + new Vector2(2f, 0f));
                        List<Vector2> drawn = new(scratch);
                        l.Pieces.Add(drawn);
                        l.Points.AddRange(drawn);
                        l.Line.AddPolyline(drawn, l.Width, l.Color);
                        if (l.Playing)
                        {
                            Color c = MosaicColor(l.Song);
                            l.Glow.AddPolyline(drawn, 4f, new Color(c.r, c.g, c.b, .22f));
                        }
                    }
                l.Line.Commit();
                l.Glow.Commit();
            }
        }

        TextMeshProUGUI SpanLabel(int i)
        {
            while (spanLabels.Count <= i)
                spanLabels.Add(UiKit.Text($"Piece Label {spanLabels.Count + 1}", spanLabelRoot != null ? spanLabelRoot : content, 12, TextColor, TextAlignmentOptions.MidlineLeft));
            TextMeshProUGUI t = spanLabels[i];
            UiKit.Show(t, true);
            return t;
        }

        TextMeshProUGUI LoopTag(int i)
        {
            while (loopTags.Count <= i)
            {
                TextMeshProUGUI t = UiKit.Text($"Loop Tag {loopTags.Count + 1}", spanLabelRoot != null ? spanLabelRoot : content, 10, DimColor, TextAlignmentOptions.MidlineLeft, bold: true);
                t.characterSpacing = 3;
                loopTags.Add(t);
            }
            TextMeshProUGUI tag = loopTags[i];
            UiKit.Show(tag, true);
            return tag;
        }

        // ------------------------------------------------------------------ legend

        /// <summary>The target and the harmony voices on the title row (the pieces are labelled on their spans).</summary>
        void BuildMosaicLegend(Mosaic mo)
        {
            int n = 1 + mo.Harmonies.Count;
            while (legend.Count < n)
            {
                Image swatch = UiKit.Image($"Legend Swatch {legend.Count + 1}", panel, Color.white, 1);
                TextMeshProUGUI text = UiKit.Text($"Legend {legend.Count + 1}", panel, 13, MutedColor);
                legend.Add((swatch, text));
            }
            for (int i = 0; i < legend.Count; i++)
            {
                UiKit.Show(legend[i].swatch, i < n);
                UiKit.Show(legend[i].text, i < n);
            }
            RefreshMosaicLegend(mo, mosaicNow);
        }

        void RefreshMosaicLegend(Mosaic mo, MosaicTimeline.State st)
        {
            List<(MosaicSong song, string role, bool playing)> entries = new() { (mo.Target, "melody", st.Kind != MosaicSectionKind.Mosaic) };
            foreach (MosaicHarmony h in mo.Harmonies)
                if (h.Song >= 0) entries.Add((mo.Songs[h.Song], "harmony", st.Kind == MosaicSectionKind.Harmony));
            float right = PanelWidth - 20f, left = 540f, gap = 22f, size = 13f;
            List<float> widths = new();
            for (int pass = 0; pass < 3; pass++)
            {
                widths.Clear();
                float total = 0;
                for (int i = 0; i < entries.Count && i < legend.Count; i++)
                {
                    TextMeshProUGUI t = legend[i].text;
                    t.fontSize = size;
                    (MosaicSong song, string role, bool playing) = entries[i];
                    string year = song.Year > 0 ? song.Year.ToString(CultureInfo.InvariantCulture) : "";
                    string title = GraphHud.Esc(song.Title.Length > 0 ? song.Title : song.WorkId);
                    t.text = playing ? $"<color={GraphHud.Muted}>{year}</color>  <b>{title}</b> <size=80%>({role})</size>" : $"{year}  {title} <size=80%>({role})</size>";
                    float w = Mathf.Min(300f, t.GetPreferredValues(t.text, 2000, 0).x) + 22f;
                    widths.Add(w);
                    total += w + (i > 0 ? gap : 0);
                }
                if (total <= right - left || size <= 11f) break;
                size -= 1f;
            }
            float x = right;
            for (int i = Math.Min(entries.Count, legend.Count) - 1; i >= 0; i--)
            {
                (Image swatch, TextMeshProUGUI text) = legend[i];
                bool playing = entries[i].playing;
                Color c = MosaicColor(entries[i].song.ColorSlot);
                float w = widths[i];
                x -= w;
                UiKit.Place(swatch.rectTransform, Mathf.Max(left, x), 19f, 16f, playing ? 4f : 3f);
                swatch.color = playing ? c : MosaicMuted(c, .45f, .8f);
                text.color = playing ? TextColor : MutedColor;
                UiKit.Place(text.rectTransform, Mathf.Max(left, x) + 22f, 11f, w - 22f, 20f);
                x -= gap;
            }
        }
    }
}
