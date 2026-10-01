#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using MusicHistory.Playback;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The chord colours, shared by the chord wheel and the melody graph's chord strip: Resonance's
    /// tonal colour field, so a chord's colour is what it is to the key. The key's own chord is blue,
    /// the fourth red, the fifth green and the other degrees take fixed blends between them; a minor
    /// chord is pulled toward violet and darkened. Colours are relative to the key's tonic, so the
    /// chord roots (normalized C major / A minor frame) are read against the tonic of the key heard:
    /// C for a major song, A for a minor one (<see cref="ChordKey"/>).
    ///
    /// Ported from Resonance-2, Assets/TonalColorField.cs (<c>Pitch</c>, <c>Chord</c>), which
    /// CyclicOrrery.ChordColor calls with the chord's root, the current key and
    /// <c>Quality.StartsWith("m") &amp;&amp; !Quality.StartsWith("maj")</c>; its key is the tonic
    /// pitch class of the mode (A for A minor). Same constants, same maths.
    /// </summary>
    public static class ChordPalette
    {
        public static readonly Color Tonic = new(.06f, .16f, 1f);
        public static readonly Color Subdominant = new(1f, .045f, .09f);
        public static readonly Color Dominant = new(.07f, 1f, .16f);
        static readonly Color MinorPull = new(.7f, .06f, .85f);

        static int Mod12(int n) => ((n % 12) + 12) % 12;

        /// <summary>Resonance's TonalColorField.Pitch: the colour of pitch class <paramref name="pitch"/> in the key whose tonic is <paramref name="key"/>.</summary>
        public static Color Pitch(int pitch, int key)
        {
            // Explicit key-relative hues keep remote/minor degrees from inheriting V's green.
            return Mod12(pitch - key) switch
            {
                0 => Tonic, 5 => Subdominant, 7 => Dominant,
                2 => new Color(.58f, .16f, .9f), 4 => new Color(.24f, .24f, .86f), 9 => new Color(.76f, .09f, .55f),
                1 => new Color(.42f, .16f, .25f), 3 => new Color(.48f, .13f, .60f),
                6 => new Color(.3f, .22f, .38f), 8 => new Color(.43f, .1f, .38f),
                10 => new Color(.35f, .18f, .5f), _ => new Color(.24f, .3f, .42f)
            };
        }

        /// <summary>Resonance's TonalColorField.Chord (alpha included: a minor chord's is .82, as there).</summary>
        public static Color Chord(int root, int key, bool minor)
        {
            Color c = Pitch(root, key);
            return minor ? Color.Lerp(c, MinorPull, .55f) * .82f : c;
        }

        /// <summary>Resonance's minor test on a chord quality: starts with "m" but not "maj" ("min" yes; "dim", "aug", "sus" no).</summary>
        public static bool MinorColour(string? quality) =>
            quality != null && quality.StartsWith("m", StringComparison.Ordinal) && !quality.StartsWith("maj", StringComparison.Ordinal);

        /// <summary>The chord's colour, opaque, against tonic <paramref name="tonicPc"/>.</summary>
        public static Color Of(int rootPc, string? quality, int tonicPc)
        {
            Color c = Chord(rootPc, tonicPc, MinorColour(quality));
            c.a = 1f;
            return c;
        }

        public static Color Of(MashupChord c, int tonicPc) => Of(c.RootPc, c.Quality, tonicPc);

        /// <summary>
        /// The muted version of a chord colour (the chords not sounding while one does): mostly
        /// desaturated and darker, its hue still recognisable.
        /// </summary>
        public static Color Muted(Color c)
        {
            float l = .299f * c.r + .587f * c.g + .114f * c.b;
            Color m = Color.Lerp(new Color(l, l, l), c, .42f) * .5f;
            m.a = .88f;
            return m;
        }

        /// <summary>Dark text on a light cell, white on a dark one.</summary>
        public static Color LabelOn(Color fill)
        {
            float lum = .299f * fill.r + .587f * fill.g + .114f * fill.b;
            return lum > .55f ? new Color(.06f, .07f, .08f, .92f) : new Color(1, 1, 1, .95f);
        }
    }

    /// <summary>
    /// Plain names for chords, by the root's interval above the key's tonic (Resonance's torus label
    /// vocabulary: "Key", "Neapolitan", "2nd maj dom", "min 3rd", ...), with the quality as a small
    /// suffix: m (minor), ° (diminished), + (augmented), sus. The second is named for its usual role,
    /// the major chord on it that acts as V of V ("2nd Maj Dom"); any other chord there is "2nd".
    /// Narrow places use the short forms ("5th", "min3", "Neap").
    /// </summary>
    public static class ChordNames
    {
        static readonly string[] Full =
        {
            "Key", "Neapolitan", "2nd Maj Dom", "Minor 3rd", "Major 3rd", "Fourth",
            "Tritone", "Fifth", "Minor 6th", "Major 6th", "Minor 7th", "Major 7th"
        };

        static readonly string[] Short = { "Key", "Neap", "2nd", "min3", "maj3", "4th", "Trit", "5th", "min6", "maj6", "min7", "maj7" };

        public static int Interval(int rootPc, int tonicPc) => ((rootPc - tonicPc) % 12 + 12) % 12;

        static bool Major(string? quality) => quality == null || quality.Length == 0 || quality.StartsWith("maj", StringComparison.Ordinal) || quality == "7";

        /// <summary>"Fifth", "Minor 3rd", "2nd Maj Dom" (the major chord on the second; "2nd" otherwise).</summary>
        public static string Name(int rootPc, int tonicPc, string? quality)
        {
            int i = Interval(rootPc, tonicPc);
            if (i == 2 && !Major(quality)) return "2nd";
            return Full[i];
        }

        public static string ShortName(int rootPc, int tonicPc) => Short[Interval(rootPc, tonicPc)];

        /// <summary>The quality's mark: "m", "°", "+", "sus" or "" (major and anything else).</summary>
        public static string Suffix(string? quality) => quality switch
        {
            "min" => "m",
            "dim" => "°",
            "aug" => "+",
            "sus" => "sus",
            _ => ""
        };

        /// <summary>Name and suffix as one plain string ("Major 6th m").</summary>
        public static string Plain(string name, string suffix) => suffix.Length > 0 ? name + " " + suffix : name;

        /// <summary>Name with the suffix small (TMP rich text).</summary>
        public static string Rich(string name, string suffix, int suffixPercent = 70) =>
            suffix.Length > 0 ? $"{name}<size={suffixPercent}%> {suffix}</size>" : name;

        public static string Label(MashupChord c, int tonicPc) => Plain(Name(c.RootPc, tonicPc, c.Quality), Suffix(c.Quality));
    }

    /// <summary>The key a chord list is read in: the tonic pitch class its roman numerals imply (normalized frame).</summary>
    public static class ChordKey
    {
        /// <summary>
        /// The tonic the chords' roman numerals point to (root minus the numeral's degree; most votes
        /// win), else from <paramref name="keyName"/>: A (9) for a minor key, C (0) otherwise. The
        /// numerals are written in each song's own mode ("i" on A, "bVII" on G), so a minor song
        /// reads against A, as Resonance reads a minor key against its minor tonic.
        /// </summary>
        public static int TonicOf(IEnumerable<MashupChord>? chords, string? keyName)
        {
            int[] votes = new int[12];
            int total = 0;
            if (chords != null)
                foreach (MashupChord c in chords)
                    if (MainLoop.TryChord(c.Roman, out int degree, out string _, out _))
                    {
                        votes[((c.RootPc - degree) % 12 + 12) % 12]++;
                        total++;
                    }
            if (total > 0)
            {
                int best = 0;
                for (int i = 1; i < 12; i++) if (votes[i] > votes[best]) best = i;
                return best;
            }
            return FromKeyName(keyName);
        }

        /// <summary>A (9) for "… minor", C (0) otherwise: the normalized frame's tonic.</summary>
        public static int FromKeyName(string? keyName) =>
            keyName != null && keyName.IndexOf("minor", StringComparison.OrdinalIgnoreCase) >= 0 ? 9 : 0;

        /// <summary>A song's tonic in the normalized frame: A for a minor song, C for a major one.</summary>
        public static int ForMode(bool minor) => minor ? 9 : 0;
    }

    /// <summary>One chord of a ring: where it starts and how long it lasts (any unit), its root, quality and roman numeral.</summary>
    public readonly struct RingChord
    {
        public readonly double Start, Length;
        public readonly int RootPc;
        /// <summary>"maj", "min", "dim", "aug", "sus", "other" (as in mashups.json).</summary>
        public readonly string Quality;
        public readonly string Roman;

        public RingChord(double start, double length, int rootPc, string quality, string roman)
        {
            Start = start;
            Length = length;
            RootPc = ((rootPc % 12) + 12) % 12;
            Quality = quality ?? "";
            Roman = roman;
        }

        /// <summary>Minor or diminished.</summary>
        public bool Minor => Quality == "min" || Quality == "dim";

        /// <summary>The chord's colour against <paramref name="tonicPc"/>.</summary>
        public Color ColorIn(int tonicPc) => ChordPalette.Of(RootPc, Quality, tonicPc);
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
                if (TryChord(raw.Trim(), out int pc, out string quality, out string roman))
                    chords.Add(new RingChord(chords.Count, 1, pc, quality, roman));
            return chords;
        }

        /// <summary>One roman numeral token: its root pitch class (C = I) and whether it is minor or diminished.</summary>
        public static bool TryChord(string token, out int rootPc, out bool minor, out string roman)
        {
            bool ok = TryChord(token, out rootPc, out string quality, out roman);
            minor = quality == "min" || quality == "dim";
            return ok;
        }

        /// <summary>One roman numeral token: its root pitch class (C = I) and its quality ("maj", "min", "dim", "aug").</summary>
        public static bool TryChord(string token, out int rootPc, out string quality, out string roman)
        {
            rootPc = 0;
            quality = "maj";
            roman = token ?? "";
            if (string.IsNullOrEmpty(token)) return false;
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
                bool aug = suffix.StartsWith("+", StringComparison.Ordinal);
                rootPc = ((pc + shift) % 12 + 12) % 12;
                quality = dim ? "dim" : aug ? "aug" : lower ? "min" : "maj";
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// A chord progression drawn as a wheel floating over the graph (no panel behind it): one arc per
    /// chord, sized by its length, in the chord colours (<see cref="ChordPalette"/>) against the key's
    /// tonic, each named inside its arc by what it is to the key (<see cref="ChordNames"/>: "Key",
    /// "Fifth", "Minor 3rd" ...; a narrow arc gets the short form or no name); the cycle's start
    /// marked at 12 o'clock, running clockwise. With a position (<see cref="SetPointer"/>) a hand
    /// marks where the music is: the chord under it keeps its full colour, grows and is lit with
    /// bloom (a <see cref="MelodyLightRig"/> of its own on <see cref="MelodyLightRig.WheelLayer"/>),
    /// and every other chord is muted; the middle names it. Without a position every chord shows its
    /// full colour, unlit. Thin dark under-strokes and outlined text keep it legible over any
    /// background. uGUI (<see cref="UiShapes"/>), crisp at any canvas scale. Not a component: its
    /// owner places <see cref="Root"/>.
    /// </summary>
    public sealed class ChordRingView
    {
        /// <summary>Side of the square the wheel and its glow need (reference px).</summary>
        public readonly float Size;
        public readonly float OuterRadius, InnerRadius;
        /// <summary>Gap between neighbouring arcs (degrees).</summary>
        public const float GapDegrees = 2.2f;
        /// <summary>How far the current chord's arc grows inward and outward (px).</summary>
        public const float GrowIn = 7f, GrowOut = 10f;
        /// <summary>How far the light's texture reaches beyond the wheel's square (px).</summary>
        public const float GlowPad = 110f;
        /// <summary>HDR intensity of the current chord's light (its bloom camera's threshold is 0.4).</summary>
        public float GlowIntensity = 1.3f;
        /// <summary>Widest arc that glows at once (degrees); a longer chord glows around the hand.</summary>
        public const float MaxGlowDegrees = 90f;
        public float NameSize = 19f, CurrentNameSize = 22f, MinNameSize = 12f, CenterSize = 34f;

        public RectTransform Root { get; }
        readonly UiShapes shadow, shapes;
        readonly Image startMark, startShadow;
        readonly RectTransform hand;
        readonly TextMeshProUGUI center;
        readonly List<TextMeshProUGUI> labels = new();
        readonly List<RingChord> chords = new();
        readonly List<(float start, float end)> arcs = new();
        readonly List<string> names = new();
        readonly List<(int chord, float halfDegrees, string text)> placedLabels = new();
        string centerPlain = "";
        readonly MelodyLightRig? bloom;
        double total = 1;
        int current = -2;
        int tonic;
        object? sourceKey;

        public IReadOnlyList<RingChord> Chords => chords;
        /// <summary>Each chord's arc (degrees clockwise from 12 o'clock, gaps excluded).</summary>
        public IReadOnlyList<(float start, float end)> Arcs => arcs;
        /// <summary>The chord under the hand (-1 without a position).</summary>
        public int Current => current < 0 ? -1 : current;
        /// <summary>The hand's angle (degrees clockwise from 12 o'clock; NaN when hidden).</summary>
        public float PointerDegrees { get; private set; } = float.NaN;
        public bool StartMarked => startMark.gameObject.activeSelf;
        public bool Visible => Root.gameObject.activeInHierarchy;
        /// <summary>The middle's text (the current chord's name; empty without a position).</summary>
        public string CenterText => center.gameObject.activeSelf ? centerPlain : "";
        /// <summary>What the ring was last filled from (owners skip refilling the same thing).</summary>
        public object? SourceKey => sourceKey;
        /// <summary>The key's tonic the chords are coloured and named against (normalized frame).</summary>
        public int TonicPc => tonic;
        /// <summary>The bloom rig lighting the current chord (null without the MusicHistory/MelodyGlow shader).</summary>
        public MelodyLightRig? Bloom => bloom;
        /// <summary>Each chord's full name ("Major 6th m"), in order.</summary>
        public IReadOnlyList<string> Names => names;
        /// <summary>Labels drawn: the chord each names, its half-width along the arc (degrees, measured at the arc's middle radius) and its plain text.</summary>
        public IReadOnlyList<(int chord, float halfDegrees, string text)> PlacedLabels => placedLabels;

        /// <summary>The roman numerals of the chords, in order.</summary>
        public string RomanText
        {
            get
            {
                StringBuilder b = new();
                foreach (RingChord c in chords) b.Append(c.Roman).Append(' ');
                return b.ToString().Trim();
            }
        }

        /// <summary>The names drawn on the wheel (plain text, in drawing order).</summary>
        public string LabelText
        {
            get
            {
                StringBuilder b = new();
                foreach (var (_, _, text) in placedLabels) b.Append(text).Append(" | ");
                return b.ToString().TrimEnd(' ', '|');
            }
        }

        /// <summary>The colour chord <paramref name="i"/> is drawn in now (muted while another one sounds).</summary>
        public Color DrawnColor(int i)
        {
            Color c = chords[i].ColorIn(tonic);
            return current >= 0 && i != current ? ChordPalette.Muted(c) : c;
        }

        public ChordRingView(Transform parent, float size, Transform? rigParent = null)
        {
            Size = size;
            OuterRadius = size * .5f - 50f;
            InnerRadius = OuterRadius - 58f;
            Root = UiKit.Rect("Chord Wheel", parent);
            UiKit.Place(Root, 0, 0, size, size);
            shadow = Shapes("Under-strokes", 5f);
            shapes = Shapes("Arcs", 1.2f);
            Vector2 c = new(size * .5f, size * .5f);
            Shader? glow = rigParent != null ? Shader.Find(MelodyGraphPanel.GlowShaderName) : null;
            if (glow != null)
            {
                // Far from the melody graph's rig, on a layer of its own: neither camera sees the other.
                // Its light is rendered over a square larger than the wheel, so the bloom fades out
                // well before the texture's edge (no visible square over the graph).
                RectTransform area = UiKit.Rect("Glow Area", Root);
                UiKit.Place(area, -GlowPad, -GlowPad, size + 2f * GlowPad, size + 2f * GlowPad);
                bloom = new MelodyLightRig(rigParent!, area, size + 2f * GlowPad, size + 2f * GlowPad, glow, MelodyLightRig.WheelLayer, new Vector3(100000f, -100000f, 0f))
                {
                    BloomIntensity = 1.1f,
                    BloomScatter = .45f,
                    EdgeFade = .15f
                };
                bloom.EnsureLights(0);
            }
            startShadow = UiKit.Image("Start Shadow", Root, new Color(0, 0, 0, .55f));
            UiKit.Place(startShadow.rectTransform, c.x - 3f, c.y - OuterRadius - 24f, 6f, 19f);
            startMark = UiKit.Image("Start", Root, new Color(1, 1, 1, .9f));
            UiKit.Place(startMark.rectTransform, c.x - 1.5f, c.y - OuterRadius - 22f, 3f, 15f);
            hand = UiKit.Rect("Hand", Root);
            UiKit.Place(hand, c.x, c.y, 0f, 0f);
            // Two ticks, outside and inside the ring, so the lit chord's name stays readable.
            foreach ((float from, float to) in new[] { (OuterRadius + GrowOut + 18f, OuterRadius + 2f), (InnerRadius - 2f, InnerRadius - GrowIn - 14f) })
            {
                Image tickShadow = UiKit.Image("Tick Shadow", hand, new Color(0, 0, 0, .55f));
                UiKit.Place(tickShadow.rectTransform, -4f, -from - 2f, 8f, from - to + 4f);
                Image tick = UiKit.Image("Tick", hand, Color.white);
                UiKit.Place(tick.rectTransform, -2f, -from, 4f, from - to);
            }
            center = UiKit.Text("Center", Root, CenterSize, new Color(.96f, .97f, .98f, 1f), TextAlignmentOptions.Center, bold: true);
            UiKit.Outline(center);
            UiKit.Place(center.rectTransform, c.x - InnerRadius + 10f, c.y - 28f, 2 * InnerRadius - 20f, 56f);
            UiKit.Show(hand, false);
            UiKit.Show(center, false);
        }

        UiShapes Shapes(string name, float feather)
        {
            RectTransform s = UiKit.Rect(name, Root);
            UiKit.Place(s, 0, 0, Size, Size);
            s.gameObject.AddComponent<CanvasRenderer>();
            UiShapes shape = s.gameObject.AddComponent<UiShapes>();
            shape.raycastTarget = false;
            shape.Feather = feather;
            return shape;
        }

        /// <summary>
        /// Shows <paramref name="progression"/> over a cycle of <paramref name="cycle"/> (the sum of
        /// the lengths when 0), read against tonic <paramref name="tonicPc"/>, from
        /// <paramref name="key"/> (owners skip refilling the same source).
        /// </summary>
        public void Set(IReadOnlyList<RingChord> progression, double cycle, int tonicPc, object? key = null)
        {
            sourceKey = key;
            tonic = ((tonicPc % 12) + 12) % 12;
            chords.Clear();
            chords.AddRange(progression);
            names.Clear();
            foreach (RingChord ch in chords) names.Add(ChordNames.Plain(ChordNames.Name(ch.RootPc, tonic, ch.Quality), ChordNames.Suffix(ch.Quality)));
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
            current = -2;
            Draw();
        }

        /// <summary>The music's position in the cycle (null hides the hand and shows every chord in full colour).</summary>
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

        /// <summary>Radii of chord <paramref name="i"/>'s arc as drawn (the current one is larger).</summary>
        public (float inner, float outer) RadiiOf(int i) =>
            i == current && current >= 0 ? (InnerRadius - GrowIn, OuterRadius + GrowOut) : (InnerRadius, OuterRadius);

        void Draw()
        {
            shapes.Clear();
            shadow.Clear();
            placedLabels.Clear();
            Vector2 c = new(Size * .5f, Size * .5f);
            // A faint track (gaps read as gaps, an empty ring still reads as a ring) over a soft dark under-stroke.
            shadow.AddArc(c, InnerRadius - 4f, OuterRadius + 4f, 0f, 359.99f, new Color(0, 0, 0, .32f));
            shapes.AddArc(c, InnerRadius, OuterRadius, 0f, 359.99f, new Color(1, 1, 1, .05f));
            int label = 0;
            for (int i = 0; i < chords.Count; i++)
            {
                (float a0, float a1) = arcs[i];
                bool now = i == current && current >= 0;
                Color col = DrawnColor(i);
                (float inner, float outer) = RadiiOf(i);
                shadow.AddArc(c, inner - 3f, outer + 3f, a0 - .4f, a1 + .4f, new Color(0, 0, 0, now ? .6f : .45f));
                shapes.AddArc(c, inner, outer, a0, a1, col);
                label = PlaceName(i, a0, a1, inner, outer, now, label);
            }
            for (int i = label; i < labels.Count; i++) UiKit.Show(labels[i], false);
            shadow.Commit();
            shapes.Commit();
            UiKit.Show(startMark, chords.Count > 0);
            UiKit.Show(startShadow, chords.Count > 0);
            // The middle names the chord sounding.
            bool showCenter = current >= 0;
            UiKit.Show(center, showCenter);
            if (showCenter)
            {
                RingChord ch = chords[current];
                string name = ChordNames.Name(ch.RootPc, tonic, ch.Quality), suffix = ChordNames.Suffix(ch.Quality);
                centerPlain = ChordNames.Plain(name, suffix);
                UiKit.SetText(center, ChordNames.Rich(name, suffix, 62));
                UiKit.FitWidth(center, 2 * InnerRadius - 26f, CenterSize, 20f);
            }
        }

        /// <summary>
        /// The chord's name inside its arc, along the arc's tangent (upright on the lower half): the
        /// full name if it fits the arc's chord length, shrunk down to <see cref="MinNameSize"/>, else
        /// the short form, else nothing. Each name stays within its own arc, so names never overlap.
        /// </summary>
        int PlaceName(int i, float a0, float a1, float inner, float outer, bool now, int label)
        {
            RingChord ch = chords[i];
            float r = (inner + outer) * .5f;
            float span = Mathf.Min(a1 - a0, 359f);
            // The straight chord across the arc at its middle radius, less a little padding each end.
            float avail = (span >= 180f ? 2f * r : 2f * r * Mathf.Sin(span * .5f * Mathf.Deg2Rad)) - 14f;
            float height = outer - inner - 6f;
            if (avail < 18f) return label;
            TextMeshProUGUI t = Label(label);
            string suffix = ChordNames.Suffix(ch.Quality);
            t.fontStyle = now ? FontStyles.Bold : FontStyles.Normal;
            float max = now ? CurrentNameSize : NameSize;
            float used = 0f;
            string plain = "";
            foreach (string name in new[] { ChordNames.Name(ch.RootPc, tonic, ch.Quality), ChordNames.ShortName(ch.RootPc, tonic) })
            {
                UiKit.SetText(t, ChordNames.Rich(name, suffix));
                float size = UiKit.FitWidth(t, avail, max, MinNameSize);
                float w = t.GetPreferredValues(t.text, 100000f, 0f).x;
                if (w <= avail + .5f && size <= height)
                {
                    used = w;
                    plain = ChordNames.Plain(name, suffix);
                    break;
                }
            }
            if (used <= 0f)
            {
                UiKit.Show(t, false);
                return label;
            }
            t.color = now ? Color.white : current >= 0 ? new Color(.86f, .88f, .91f, .92f) : new Color(.97f, .98f, 1f, 1f);
            float mid = (a0 + a1) * .5f;
            float rad = mid * Mathf.Deg2Rad;
            Vector2 at = new Vector2(Size * .5f, Size * .5f) + new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad)) * r;
            RectTransform rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(.5f, .5f);
            rt.anchoredPosition = new Vector2(at.x, -at.y);
            rt.sizeDelta = new Vector2(avail, height);
            // Along the tangent; on the lower half turned half a turn so it reads upright.
            float turn = mid > 90f && mid < 270f ? mid - 180f : mid;
            rt.localRotation = Quaternion.Euler(0f, 0f, -turn);
            placedLabels.Add((i, used * .5f / Mathf.Max(1f, r) * Mathf.Rad2Deg, plain));
            return label + 1;
        }

        TextMeshProUGUI Label(int i)
        {
            while (labels.Count <= i)
            {
                TextMeshProUGUI t = UiKit.Text($"Name {labels.Count + 1}", Root, NameSize, Color.white, TextAlignmentOptions.Center);
                UiKit.Outline(t);
                t.overflowMode = TextOverflowModes.Overflow;
                labels.Add(t);
            }
            TextMeshProUGUI l = labels[i];
            UiKit.Show(l, true);
            // Under the hand and the middle text.
            l.transform.SetSiblingIndex(hand.GetSiblingIndex());
            return l;
        }

        /// <summary>
        /// Lights the current chord with bloom while the wheel shows and a chord sounds (hidden
        /// otherwise); <paramref name="pixelScale"/> screen pixels per reference pixel;
        /// <paramref name="renderNow"/> renders at once (edit mode, captures).
        /// </summary>
        public void SyncBloom(float pixelScale, bool renderNow)
        {
            if (bloom == null) return;
            bool on = Visible && current >= 0 && current < chords.Count;
            if (on)
            {
                (float a0, float a1) = arcs[current];
                // A long chord (a one-chord loop fills the whole ring) glows only around the hand,
                // so the light stays a point of colour rather than flooding the wheel's square.
                if (a1 - a0 > MaxGlowDegrees && !float.IsNaN(PointerDegrees))
                {
                    float mid = Mathf.Clamp(PointerDegrees, a0 + MaxGlowDegrees * .5f, a1 - MaxGlowDegrees * .5f);
                    (a0, a1) = (mid - MaxGlowDegrees * .5f, mid + MaxGlowDegrees * .5f);
                }
                (float inner, float outer) = RadiiOf(current);
                Color col = chords[current].ColorIn(tonic);
                // As bright for every chord: its brightest channel reaches GlowIntensity (dark minor colours too).
                float level = GlowIntensity / Mathf.Max(.2f, Mathf.Max(col.r, col.g, col.b));
                bloom.SetGlowArc(0, true, new Vector2(Size * .5f + GlowPad, Size * .5f + GlowPad), inner, outer, a0, a1, 6f, col, level);
                bloom.SetTint(col);
            }
            else bloom.SetGlowArc(0, false, default, 0, 0, 0, 0, 0, Color.black, 0);
            bloom.Sync(on, pixelScale, renderNow && on);
        }

        /// <summary>The current chord's light intensity (0 when unlit).</summary>
        public float GlowLevel => bloom != null && bloom.Active ? bloom.GlowIntensityOf(0) : 0f;

        public void Dispose() => bloom?.Dispose();

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
                result.Add(new RingChord(a - cycleStart, b - a, ch.RootPc, ch.Quality, ch.Roman));
            }
            return result;
        }
    }
}
