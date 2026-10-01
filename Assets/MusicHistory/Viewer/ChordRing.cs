#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MusicHistory.Playback;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The chord colours, shared by the melody graph's chord strip and the chord ring: the viewer's
    /// key palette at the chord's root (circle-of-fifths hue, minor and diminished darker), roots in
    /// the normalized C major / A minor frame.
    /// </summary>
    public static class ChordPalette
    {
        public static Color Of(int rootPc, bool minor) => SongPalette.KeyColor(rootPc, minor);
        public static Color Of(MashupChord c) => Of(c.RootPc, c.Minor);

        /// <summary>Dark text on a light cell, white on a dark one.</summary>
        public static Color LabelOn(Color fill)
        {
            float lum = .299f * fill.r + .587f * fill.g + .114f * fill.b;
            return lum > .55f ? new Color(.06f, .07f, .08f, .92f) : new Color(1, 1, 1, .95f);
        }
    }

    /// <summary>One chord of a ring: where it starts and how long it lasts (any unit), its root and roman numeral.</summary>
    public readonly struct RingChord
    {
        public readonly double Start, Length;
        public readonly int RootPc;
        public readonly bool Minor;
        public readonly string Roman;

        public RingChord(double start, double length, int rootPc, bool minor, string roman)
        {
            Start = start;
            Length = length;
            RootPc = ((rootPc % 12) + 12) % 12;
            Minor = minor;
            Roman = roman;
        }

        public Color Color => ChordPalette.Of(RootPc, Minor);
    }

    /// <summary>
    /// A song's main loop as the graph stores it (song_node.main_loop): roman numerals joined by
    /// "-", e.g. "I-V-vi-IV", "bVII-IV-I", "ii-V-I-#ivo"; a minor song adds its own minor-key
    /// reading in parentheses ("III-vi-III-vii (V-i-V-ii)"), and the first reading — relative to the
    /// major, so in the normalized C major frame like the melody graph's chords — is the one used.
    /// Upper case is major, lower case minor, "o" diminished, "+" augmented; "b" / "#" move the root.
    /// Unreadable tokens are skipped. Pure C#.
    /// </summary>
    public static class MainLoop
    {
        static readonly (string numeral, int pc)[] Numerals =
        {
            ("VII", 11), ("VI", 9), ("IV", 5), ("V", 7), ("III", 4), ("II", 2), ("I", 0)
        };

        /// <summary>The chords of <paramref name="mainLoop"/>, one beat each (the stored loop has no durations).</summary>
        public static List<RingChord> Parse(string? mainLoop)
        {
            List<RingChord> chords = new();
            if (string.IsNullOrWhiteSpace(mainLoop)) return chords;
            string text = mainLoop!;
            int paren = text.IndexOf('(');
            if (paren >= 0) text = text.Substring(0, paren);
            foreach (string raw in text.Split(new[] { '-', '–', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (TryChord(raw.Trim(), out int pc, out bool minor, out string roman))
                    chords.Add(new RingChord(chords.Count, 1, pc, minor, roman));
            return chords;
        }

        /// <summary>One roman numeral token: its root pitch class (C = I) and whether it is minor or diminished.</summary>
        public static bool TryChord(string token, out int rootPc, out bool minor, out string roman)
        {
            rootPc = 0;
            minor = false;
            roman = token;
            if (token.Length == 0) return false;
            int i = 0, shift = 0;
            while (i < token.Length && (token[i] == 'b' || token[i] == '#' || token[i] == '♭' || token[i] == '♯'))
            {
                shift += token[i] == '#' || token[i] == '♯' ? 1 : -1;
                i++;
            }
            string rest = token.Substring(i);
            foreach ((string numeral, int pc) in Numerals)
            {
                if (!rest.StartsWith(numeral, StringComparison.OrdinalIgnoreCase)) continue;
                string written = rest.Substring(0, numeral.Length);
                bool upper = written == numeral, lower = written == numeral.ToLowerInvariant();
                if (!upper && !lower) continue;
                string suffix = rest.Substring(numeral.Length);
                bool dim = suffix.StartsWith("o", StringComparison.Ordinal) || suffix.StartsWith("°", StringComparison.Ordinal);
                rootPc = ((pc + shift) % 12 + 12) % 12;
                minor = lower || dim;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A chord progression drawn as a ring (donut): one arc per chord, sized by its length, in the
    /// chord palette (<see cref="ChordPalette"/>), its roman numeral beside it; the cycle's start
    /// marked at 12 o'clock, running clockwise. With a position (<see cref="SetPointer"/>) a hand
    /// marks where the music is and the chord under it stands out. uGUI (<see cref="UiShapes"/>),
    /// crisp at any canvas scale. Not a component: its owner places <see cref="Root"/>.
    /// </summary>
    public sealed class ChordRingView
    {
        /// <summary>Side of the square the ring and its labels need (reference px).</summary>
        public readonly float Size;
        public readonly float OuterRadius, InnerRadius;
        /// <summary>Gap between neighbouring arcs (degrees).</summary>
        public const float GapDegrees = 2.2f;

        public RectTransform Root { get; }
        readonly UiShapes shapes;
        readonly Image startMark;
        readonly RectTransform hand;
        readonly Image handBar;
        readonly TextMeshProUGUI center, centerSmall;
        readonly List<TextMeshProUGUI> labels = new();
        readonly List<RingChord> chords = new();
        readonly List<(float start, float end)> arcs = new();
        double total = 1;
        int current = -2;
        object? sourceKey;

        public IReadOnlyList<RingChord> Chords => chords;
        /// <summary>Each chord's arc (degrees clockwise from 12 o'clock, gaps excluded).</summary>
        public IReadOnlyList<(float start, float end)> Arcs => arcs;
        /// <summary>The chord under the hand (-1 without a position).</summary>
        public int Current => current < 0 ? -1 : current;
        /// <summary>The hand's angle (degrees clockwise from 12 o'clock; NaN when hidden).</summary>
        public float PointerDegrees { get; private set; } = float.NaN;
        public bool StartMarked => startMark.gameObject.activeSelf;
        public bool Visible => Root.gameObject.activeSelf;
        public string CenterText => center.text + (centerSmall.text.Length > 0 ? " " + centerSmall.text : "");
        /// <summary>What the ring was last filled from (owners skip refilling the same thing).</summary>
        public object? SourceKey => sourceKey;

        /// <summary>The roman numerals shown, in order.</summary>
        public string RomanText
        {
            get
            {
                StringBuilder b = new();
                foreach (RingChord c in chords) b.Append(c.Roman).Append(' ');
                return b.ToString().Trim();
            }
        }

        public ChordRingView(Transform parent, float size)
        {
            Size = size;
            OuterRadius = size * .5f - 24f;
            InnerRadius = OuterRadius - Mathf.Max(12f, size * .11f);
            Root = UiKit.Rect("Chord Ring", parent);
            UiKit.Place(Root, 0, 0, size, size);
            RectTransform s = UiKit.Rect("Arcs", Root);
            UiKit.Place(s, 0, 0, size, size);
            s.gameObject.AddComponent<CanvasRenderer>();
            shapes = s.gameObject.AddComponent<UiShapes>();
            shapes.raycastTarget = false;
            shapes.Feather = 1f;
            Vector2 c = new(size * .5f, size * .5f);
            startMark = UiKit.Image("Start", Root, new Color(1, 1, 1, .85f));
            UiKit.Place(startMark.rectTransform, c.x - 1f, c.y - OuterRadius - 9f, 2f, 7f);
            hand = UiKit.Rect("Hand", Root);
            UiKit.Place(hand, c.x, c.y, 0f, 0f);
            handBar = UiKit.Image("Bar", hand, Color.white);
            UiKit.Place(handBar.rectTransform, -1.5f, -(OuterRadius + 4f), 3f, OuterRadius - InnerRadius + 8f);
            center = UiKit.Text("Center", Root, 20, new Color(.94f, .95f, .97f, 1f), TextAlignmentOptions.Center, bold: true);
            UiKit.Place(center.rectTransform, c.x - InnerRadius, c.y - 16f, 2 * InnerRadius, 24f);
            centerSmall = UiKit.Text("Center Note", Root, 10, new Color(.60f, .64f, .68f, 1f), TextAlignmentOptions.Center);
            centerSmall.characterSpacing = 4;
            UiKit.Place(centerSmall.rectTransform, c.x - InnerRadius, c.y + 6f, 2 * InnerRadius, 14f);
            UiKit.Show(hand, false);
        }

        /// <summary>
        /// Shows <paramref name="progression"/> over a cycle of <paramref name="cycle"/> (the sum of
        /// the lengths when 0), from <paramref name="key"/> (owners skip refilling the same source).
        /// </summary>
        public void Set(IReadOnlyList<RingChord> progression, double cycle, string centerText, string note, object? key = null)
        {
            sourceKey = key;
            chords.Clear();
            chords.AddRange(progression);
            double sum = 0;
            foreach (RingChord ch in chords) sum = Math.Max(sum, ch.Start + ch.Length);
            total = cycle > 0 ? cycle : Math.Max(1e-6, sum);
            arcs.Clear();
            foreach (RingChord ch in chords)
            {
                float a0 = (float)(ch.Start / total * 360.0), a1 = (float)((ch.Start + ch.Length) / total * 360.0);
                float gap = chords.Count > 1 ? GapDegrees * .5f : 0f;
                arcs.Add((a0 + gap, Mathf.Max(a0 + gap + .5f, a1 - gap)));
            }
            UiKit.SetText(center, centerText);
            UiKit.SetText(centerSmall, note);
            current = -2;
            Draw();
        }

        /// <summary>The text in the ring's middle and the small note under it.</summary>
        public void SetCenter(string main, string note)
        {
            UiKit.SetText(center, main);
            UiKit.SetText(centerSmall, note);
        }

        /// <summary>The music's position in the cycle (null hides the hand).</summary>
        public void SetPointer(double? position)
        {
            if (position is not double p || chords.Count == 0)
            {
                PointerDegrees = float.NaN;
                UiKit.Show(hand, false);
                if (current != -1)
                {
                    current = -1;
                    Draw();
                }
                return;
            }
            double u = ((p % total) + total) % total;
            float deg = (float)(u / total * 360.0);
            PointerDegrees = deg;
            UiKit.Show(hand, true);
            hand.localRotation = Quaternion.Euler(0f, 0f, -deg);
            int at = -1;
            for (int i = 0; i < chords.Count; i++)
                if (u >= chords[i].Start && u < chords[i].Start + chords[i].Length) at = i;
            if (at != current)
            {
                current = at;
                Draw();
            }
        }

        void Draw()
        {
            shapes.Clear();
            Vector2 c = new(Size * .5f, Size * .5f);
            // A faint track under the arcs (gaps read as gaps, an empty ring still reads as a ring).
            shapes.AddArc(c, InnerRadius, OuterRadius, 0f, 359.99f, new Color(1, 1, 1, .05f));
            int label = 0;
            for (int i = 0; i < chords.Count; i++)
            {
                RingChord ch = chords[i];
                (float a0, float a1) = arcs[i];
                bool now = i == current;
                Color col = ch.Color;
                col.a = current >= 0 && !now ? .78f : 1f;
                shapes.AddArc(c, InnerRadius - (now ? 3f : 0f), OuterRadius + (now ? 4f : 0f), a0, a1, col);
                // A crowded ring (a bed changing chords every half bar) keeps its numerals small and skips the slivers.
                bool crowded = chords.Count > 10;
                if (a1 - a0 < (crowded ? 17f : 14f) || ch.Roman.Length == 0) continue;
                // The numeral just outside its arc's middle.
                float mid = (a0 + a1) * .5f * Mathf.Deg2Rad;
                float r = OuterRadius + (crowded ? 11f : 13f);
                Vector2 at = c + new Vector2(Mathf.Sin(mid), -Mathf.Cos(mid)) * r;
                TextMeshProUGUI t = Label(label++);
                UiKit.SetText(t, GraphHud.Esc(ch.Roman));
                t.fontStyle = now ? FontStyles.Bold : FontStyles.Normal;
                t.fontSize = crowded ? 10f : 12f;
                t.color = now ? Color.white : new Color(.82f, .85f, .88f, 1f);
                UiKit.Place(t.rectTransform, at.x - 22f, at.y - 8f, 44f, 16f);
            }
            for (int i = label; i < labels.Count; i++) UiKit.Show(labels[i], false);
            shapes.Commit();
            UiKit.Show(startMark, chords.Count > 0);
        }

        TextMeshProUGUI Label(int i)
        {
            while (labels.Count <= i)
                labels.Add(UiKit.Text($"Roman {labels.Count + 1}", Root, 12, Color.white, TextAlignmentOptions.Center));
            TextMeshProUGUI t = labels[i];
            UiKit.Show(t, true);
            return t;
        }

        /// <summary>
        /// The chords of <paramref name="list"/> that sound within the cycle
        /// [<paramref name="cycleStart"/>, <paramref name="cycleStart"/> + <paramref name="cycle"/>),
        /// relative to its start and clipped to it (a duet's bed over its current phrase).
        /// </summary>
        public static List<RingChord> Window(IReadOnlyList<MashupChord> list, double cycleStart, double cycle)
        {
            List<RingChord> result = new();
            double end = cycleStart + cycle;
            foreach (MashupChord ch in list)
            {
                double a = Math.Max(ch.Start, cycleStart), b = Math.Min(ch.End, end);
                if (b - a <= 1e-6) continue;
                result.Add(new RingChord(a - cycleStart, b - a, ch.RootPc, ch.Minor, ch.Roman));
            }
            return result;
        }
    }
}
