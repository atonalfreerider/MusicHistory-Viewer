#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using MusicHistory.Walkthrough;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Duet loops (DESIGN.md §16), the vertical melody graph and the photos on the bubbles, in edit
    /// mode (no audio: the loop keeps time on its main-thread clock). MusicHistory › Run Duet
    /// Validation, or <c>-executeMethod MusicHistory.EditorTools.DuetValidation.Run</c> (exits with 0
    /// when every check passes). Writes data/screens/duet_validation.json and the screenshots
    /// duet_landscape.png, duet_portrait.png, melody_portrait.png and bubble_photos.png.
    ///
    /// <list type="bullet">
    /// <item>Pure: the duets.json v1 contract on an inline loop (segments, pairs, beats, chords,
    /// melodies, audible spans), the clock (segment and pair lookup, wrapping, the mix beat rising
    /// continuously through the loop point), two vocals audible everywhere, contract violations,
    /// binding; the panning maths (<see cref="MelodyScroll"/>).</item>
    /// <item>Scene, for a synthetic catalog built from the featured paths (always), the real
    /// data/audio/duets/duets.json (when the pipeline wrote it) and -validationDuets &lt;file&gt;: the list
    /// offers each duet; K plays it (no narration); a whole cycle and past the wrap on the clock:
    /// segments and pairs, both singers glowing with the edge between them lit and a handoff's
    /// borrowed instrumental marked, the camera on the pair; the melody graph panning under a
    /// playhead at the screen's centre (±0.5 px) with every light on it and on its drawn melody,
    /// the beat and the drawn content continuous through the loop point; the strip text; Next /
    /// Back round the loop, pause, M, K to the narrated mix and back, Esc.</item>
    /// <item>Portrait (1080x1920 through <see cref="ViewerLayout.ScreenOverride"/>): the melody graph
    /// three times larger (pixels per beat and per semitone), panning, the playhead at the screen's
    /// centre, the layout clear; a narrated mix wraps round its phrase as it pans; landscape unchanged.</item>
    /// <item>Photos on bubbles: a synthetic artists.json (always) and the real one: the mapping by
    /// work_ids and by artist name, lazy decoding, shared textures and materials, the state's dimming,
    /// picking through the photo, the photo drawn on the bubble (a capture with and without it).</item>
    /// </list>
    /// </summary>
    public static class DuetValidation
    {
        [MenuItem("MusicHistory/Run Duet Validation (writes data/screens)")]
        public static void RunFromMenu() => Validation.ExecuteDuets(exitWhenDone: false);

        public static void Run() => Validation.ExecuteDuets(exitWhenDone: true);
    }

    public static partial class Validation
    {
        internal static void ExecuteDuets(bool exitWhenDone)
        {
            Report report = new();
            string outDir = Arg("-validationOut") ?? Path.Combine(SongGraphLoader.RepoRoot(), "data", "screens");
            Directory.CreateDirectory(outDir);
            Vector2Int? screenBefore = ViewerLayout.ScreenOverride;
            try
            {
                ValidateDuetsPure(report);
                ValidateScrollMath(report);
                int width = int.TryParse(Arg("-validationWidth"), out int w) ? w : 1920;
                int height = int.TryParse(Arg("-validationHeight"), out int h) ? h : 1080;
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                SongGraphLoader? loader = Object.FindAnyObjectByType<SongGraphLoader>();
                Camera? cam = Camera.main;
                if (report.Check("duets: scene has SongGraphLoader and a camera", loader != null && cam != null))
                {
                    cam!.aspect = (float)width / height;
                    string graphDir = Path.Combine(SongGraphLoader.RepoRoot(), "data", "graph");
                    string dbPath = PathArg("-validationLineageDb") ?? FirstExisting(Path.Combine(graphDir, "music_graph.db"), Path.Combine(graphDir, "demo_graph.db")) ?? "";
                    if (report.Check("duets: graph database exists", File.Exists(dbPath), dbPath))
                    {
                        loader!.Build(dbPath);
                        ValidateDuetScenes(report, loader, cam, outDir, width, height);
                        ValidatePortraitMashup(report, loader, cam, outDir);
                        ValidateBubblePhotos(report, loader, cam, outDir, width, height);
                    }
                    loader!.Clear();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check("no exception", false, e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Replace("\n", " | "));
            }
            finally
            {
                ViewerLayout.ScreenOverride = screenBefore;
            }
            WriteJson(report, Path.Combine(outDir, "duet_validation.json"));
            Debug.Log($"[validation] {report.Checks.Count - report.Failures}/{report.Checks.Count} checks passed; report in {outDir}");
            if (exitWhenDone) EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }

        static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string Q(string s) => "\"" + J(s) + "\"";

        // ------------------------------------------------------------------ pure: the contract and the clock

        /// <summary>
        /// A 3-song loop at 120 BPM (a bar = 2 s): pairs of 4 bars, 2-bar handoffs centred on the
        /// boundaries, the one at the loop point split over the file's end and start; 24 s, 48 beats.
        /// </summary>
        public static string DuetFixtureJson(string id = "fixture", string file = "fixture/loop.mp3", string[]? works = null, string[]? titles = null)
        {
            works ??= new[] { "Q1", "Q2", "Q3" };
            titles ??= new[] { "One", "Two", "Three" };
            StringBuilder b = new();
            b.Append("{\"version\": 1, \"generated_at\": \"validation\", \"frame\": \"C major / A minor\", \"paths\": [{");
            b.Append($"\"id\": {Q(id)}, \"title\": \"Fixture\", \"file\": {Q(file)}, \"seconds\": 24, \"loops\": true, \"root\": {Q(works[0])}, ");
            b.Append("\"key\": \"D major\", \"bpm\": 120, \"beats_per_bar\": 4, \"phrase_beats\": 16, \"segments\": [");
            (double s, double e, string kind, int inst, int va, int vb, int enter, int leave, string match)[] segs =
            {
                (0, 2, "handoff", 1, 0, 1, 1, 2, "0.75"), (2, 6, "duet", 0, 0, 1, -1, -1, "0.875"),
                (6, 10, "handoff", 2, 1, 2, 2, 0, "0.8"), (10, 14, "duet", 0, 1, 2, -1, -1, "1"),
                (14, 18, "handoff", 1, 2, 0, 0, 1, "0.7"), (18, 22, "duet", 0, 2, 0, -1, -1, "0.9"),
                (22, 24, "handoff", 1, 0, 1, 1, 2, "0.75")
            };
            b.Append(string.Join(", ", segs.Select(g =>
                $"{{\"start\": {Inv(g.s)}, \"end\": {Inv(g.e)}, \"kind\": \"{g.kind}\", \"instrumental\": {Q(works[g.inst])}, \"vocals\": [{Q(works[g.va])}, {Q(works[g.vb])}], " +
                $"\"entering\": {(g.enter >= 0 ? Q(works[g.enter]) : "null")}, \"leaving\": {(g.leave >= 0 ? Q(works[g.leave]) : "null")}, \"chord_match\": {g.match}}}")));
            b.Append("], \"beats\": [");
            b.Append(string.Join(", ", Enumerable.Range(0, 48).Select(i => $"[{Inv(i * .5)}, {i}]")));
            b.Append("], \"chords\": [");
            (int root, string q, string roman)[] axis = { (2, "maj", "I"), (9, "maj", "V"), (11, "min", "vi"), (7, "maj", "IV") };
            b.Append(string.Join(", ", Enumerable.Range(0, 12).Select(i => $"[{i * 4}, {i * 4 + 4}, {axis[i % 4].root}, \"{axis[i % 4].q}\", \"{axis[i % 4].roman}\"]")));
            b.Append("], \"songs\": [");
            // Song j sings pair j-1 and pair j: beats from its entering handoff to its leaving one (round the loop).
            (double from, double to)[] sing = { (28, 68), (-4, 36), (12, 52) };
            (string v, string i)[] spans =
            {
                ("[[14, 24], [0, 10]]", "[[2, 6], [10, 14], [18, 22]]"),
                ("[[22, 24], [0, 18]]", "[[0, 2], [14, 18], [22, 24]]"),
                ("[[6, 24], [0, 2]]", "[[6, 10]]")
            };
            for (int j = 0; j < 3; j++)
            {
                if (j > 0) b.Append(", ");
                List<(double beat, string pitch)> pts = new();
                for (double u = sing[j].from; u < sing[j].to - 1e-9; u += .5)
                {
                    double beat = ((u % 48) + 48) % 48;
                    // A breath once a bar, at its last half beat (not at the loop point).
                    bool breath = Math.Abs(u % 4 - 3.5) < 1e-9 && Math.Abs(beat - 47.5) > 1e-9;
                    pts.Add((beat, breath ? "null" : Inv(60 + 2 * j + 3 * Math.Sin(u * .7 + j))));
                }
                pts.Sort((x, y) => x.beat.CompareTo(y.beat));
                b.Append($"{{\"work_id\": {Q(works[j])}, \"title\": {Q(titles[j])}, \"artist\": \"Artist {j + 1}\", \"year\": {1960 + 10 * j}, \"step\": {j}, ");
                b.Append($"\"shift_semitones\": {(j == 0 ? 0 : 2 - j)}, \"tempo_ratio\": {(j == 0 ? "1" : "1.05")}, \"melody\": [");
                b.Append(string.Join(", ", pts.Select(p => $"[{Inv(p.beat)}, {p.pitch}]")));
                b.Append($"], \"vocal_audible\": {spans[j].v}, \"instrumental_audible\": {spans[j].i}}}");
            }
            b.Append("]}]}");
            return b.ToString();
        }

        static void ValidateDuetsPure(Report report)
        {
            DuetCatalog c = DuetCatalog.Parse(DuetFixtureJson(), Path.Combine(Path.GetTempPath(), "musichistory-validation-duets"), "inline");
            DuetLoop? l = c.Loops.FirstOrDefault();
            report.Check("duets.json v1 parses: loop, root, key, tempo, segments (duet / handoff, vocals, entering / leaving, chord match), beats, chords, songs",
                c.Loaded && c.Version == 1 && l != null && l.Seconds == 24 && l.Loops && l.Root == "Q1" && l.RootSong == 0 && l.Key == "D major" && l.Bpm == 120 &&
                l.BeatsPerBar == 4 && l.PhraseBeats == 16 && l.Segments.Count == 7 && l.BeatTimes.Length == 48 && Math.Abs(l.LoopBeats - 48) < 1e-9 &&
                l.Chords.Count == 12 && l.Chords[2].Roman == "vi" && l.Chords[2].Minor && l.Songs.Count == 3 && l.Segments[0].IsHandoff &&
                l.Segments[0].EnteringSong == 1 && l.Segments[0].LeavingSong == 2 && l.Segments[0].InstrumentalSong == 1 && l.Segments[1].Kind == DuetSegmentKind.Duet &&
                l.Segments[1].ChordMatch == .875 && l.Songs[1].TempoRatio == 1.05 && l.Songs[0].VocalAudible.Count == 2 && !l.FileExists,
                $"{c.Status}; {string.Join(" | ", c.Problems)}");
            report.Check("duets.json: a missing loop file is listed as a problem, not thrown", c.Problems.Count == 1 && c.Problems[0].Contains("loop missing"),
                string.Join(" | ", c.Problems));
            if (l == null) return;
            report.Check("duet pairs: pair k = S_k + S_(k+1); a handoff belongs to the pair it opens (the wrap's halves both to pair 1)",
                l.Pairs == 3 && l.Segments.Select(g => g.Pair).SequenceEqual(new[] { 0, 0, 1, 1, 2, 2, 0 }) && l.PairSongs(2) == (2, 0) &&
                l.PairStartSeconds(0) == 0 && l.PairStartSeconds(1) == 6 && l.PairStartSeconds(2) == 14 && l.PairStartSeconds(3) == 0,
                string.Join(",", l.Segments.Select(g => g.Pair)));
            bool lookup = l.SegmentIndexAt(0) == 0 && l.SegmentIndexAt(1.999) == 0 && l.SegmentIndexAt(2) == 1 && l.SegmentIndexAt(23.9) == 6 &&
                          l.SegmentIndexAt(24) == 0 && l.SegmentIndexAt(24 + 7) == 2 && l.SegmentIndexAt(-1) == 6 && l.PairAt(12) == 1 && l.PairAt(48 + 20) == 2;
            report.Check("duet clock: a boundary belongs to the next segment; times wrap round the loop (t + n·length, negative t)", lookup);
            // The mix beat: on the beats, between them, and continuously through the loop point.
            double worst = 0, worstWrap = 0;
            double prev = l.BeatAt(0);
            for (double t = .01; t < 48; t += .01)
            {
                double beat = l.BeatAt(t);
                double step = MelodyScroll.WrapNearest(beat - prev, l.LoopBeats);
                double err = Math.Abs(step - .02);
                worst = Math.Max(worst, err);
                if (Math.Abs(l.Wrap(t) - 24) < .05 || l.Wrap(t) < .05) worstWrap = Math.Max(worstWrap, err);
                prev = beat;
            }
            report.Check("duet mix beat: exact on the beats, interpolated between them, rising continuously through the loop point to 48 and on from 0",
                Math.Abs(l.BeatAt(3.5) - 7) < 1e-9 && Math.Abs(l.BeatAt(3.75) - 7.5) < 1e-9 && Math.Abs(l.BeatAt(23.75) - 47.5) < 1e-9 &&
                Math.Abs(l.BeatAt(24.25) - .5) < 1e-9 && worst < 1e-6 && worstWrap < 1e-6, $"worst step error {worst:0.0000000} beats (at the wrap {worstWrap:0.0000000})");
            List<string> coverage = DuetCatalog.CheckCoverage(l, .05);
            report.Check("duet loop: two vocals audible at every moment, the root's instrumental under every duet segment", coverage.Count == 0, string.Join(" | ", coverage));
            List<int> vocals = new(), instrumentals = new();
            string Set(double t)
            {
                l.AudibleAt(t, vocals, instrumentals);
                return $"{string.Join(",", vocals)}/{string.Join(",", instrumentals)}";
            }
            report.Check("duet audible sets: three voices in a handoff (leaving, staying, entering), two in a duet; the borrowed instrumental in a handoff",
                Set(1) == "0,1,2/1" && Set(4) == "0,1/0" && Set(8) == "0,1,2/2" && Set(12) == "1,2/0" && Set(24 + 4) == "0,1/0", $"{Set(1)} {Set(4)} {Set(8)} {Set(12)}");
            // The melody across the loop point: Song 1 sings beats 28 → 48 → 20, its line continuous.
            bool pitch = l.Songs[0].Line.PitchAt(47.75, out float p1, out bool v1) && v1 && l.Songs[0].Line.PitchAt(.25, out float p2, out bool v2) && v2 &&
                         Math.Abs(p1 - p2) < 3f && !l.Songs[2].Line.PitchAt(8, out _, out _);
            report.Check("duet melody: looked up across the loop point (the end bridged to the start), nothing where the song is silent", pitch);
            report.Check("duet text: \"Duet: A + B over <root>\"", l.DuetText(l.Segments[3]) == "Duet: Two + Three over One", l.DuetText(l.Segments[3]));

            // Binding.
            PathCatalog paths = PathCatalog.Parse("{\"version\": 2, \"morph_bars\": 2, \"paths\": [ {\"id\": \"fixture\", \"title\": \"F\", \"seconds\": 60, \"steps\": [" +
                                                  "{\"work_id\": \"Q1\", \"title\": \"One\", \"year\": 1960, \"file\": \"x.mp3\", \"seconds\": 30, \"bpm\": 120}," +
                                                  "{\"work_id\": \"Q2\", \"title\": \"Two\", \"year\": 1970, \"file\": \"y.mp3\", \"seconds\": 30, \"bpm\": 110}," +
                                                  "{\"work_id\": \"Q3\", \"title\": \"Three\", \"year\": 1980, \"file\": \"z.mp3\", \"seconds\": 30, \"bpm\": 100} ] } ] }", "");
            int playable = c.Bind(w => w switch { "Q1" => 1, "Q2" => 2, "Q3" => 3, _ => null }, paths);
            report.Check("duet loops bind to their featured path (songs to steps and nodes by work_id)",
                playable == 1 && l.IsPlayable && l.Path == paths.Paths[0] && l.Songs[2].PathStep == 2 && l.Songs[2].NodeId == 3 && c.For(paths.Paths[0]) == l,
                DuetCatalog.WhyNotPlayable(l));
            int unbound = c.Bind(w => w == "Q1" ? 1 : null, paths);
            report.Check("a duet loop whose song is not in the graph is not playable (it says why)", unbound == 0 && !l.IsPlayable && DuetCatalog.WhyNotPlayable(l).Contains("Q2"),
                DuetCatalog.WhyNotPlayable(l));

            DuetCatalog broken = DuetCatalog.Parse("{\"version\": 2, \"paths\": [ {\"id\": \"b\", \"file\": \"b.mp3\", \"seconds\": 10, \"loops\": false, \"root\": \"Q9\", \"key\": \"H major\", \"bpm\": 0, " +
                                                   "\"segments\": [ {\"start\": 0, \"end\": 4, \"kind\": \"solo\", \"instrumental\": \"Q1\", \"vocals\": [\"Q1\"]}, " +
                                                   "{\"start\": 5, \"end\": 9, \"kind\": \"handoff\", \"instrumental\": \"Q1\", \"vocals\": [\"Q1\", \"Q1\"], \"chord_match\": 1.5} ], " +
                                                   "\"beats\": [[1, 0], [0.5, 1]], \"songs\": [ {\"work_id\": \"Q1\", \"melody\": [[0, 60]]} ] } ] }", "");
            string[] wanted = { "version 2", "loops is not true", "root 'Q9'", "key 'H major'", "bpm 0", "kind 'solo'", "vocals is not", "both vocals", "no entering vocal",
                                "chord_match 1.5", "the segment before ends", "not increasing", "a duet needs at least two", "segments end at" };
            report.Check("duets.json: contract violations are reported (version, loops, root, key, tempo, kind, vocals, handoff, chord match, gaps, beats, songs, length)",
                wanted.All(x => broken.Problems.Any(p => p.Contains(x))), string.Join(" | ", wanted.Where(x => !broken.Problems.Any(p => p.Contains(x)))) + " missing; " + string.Join(" | ", broken.Problems));
            DuetCatalog junk = DuetCatalog.Parse("{ \"version\": 1, \"paths\": [ ", "");
            report.Check("duets.json: malformed JSON gives an empty catalog, no exception", !junk.Loaded && junk.Loops.Count == 0 && junk.Problems.Count == 1, junk.Status);
            DuetCatalog none = DuetCatalog.Load(Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", DuetCatalog.FileName));
            report.Check("duets.json: a missing file gives an empty catalog that says so", !none.Loaded && none.Loops.Count == 0 && none.Status.StartsWith("missing"), none.Status);
        }

        static void ValidateScrollMath(Report report)
        {
            const double P = 32;
            bool wrap = Math.Abs(MelodyScroll.WrapNearest(31, P) + 1) < 1e-12 && Math.Abs(MelodyScroll.WrapNearest(-31, P) - 1) < 1e-12 &&
                        Math.Abs(MelodyScroll.WrapNearest(5, P) - 5) < 1e-12 && Math.Abs(MelodyScroll.Fold(-1, P) - 31) < 1e-12 &&
                        Math.Abs(MelodyScroll.X(1, 31.5, P, 10, 620) - 635) < 1e-9 && Math.Abs(MelodyScroll.X(30, 1, P, 10, 620) - 590) < 1e-9;
            report.Check("pan maths: the nearest image of a beat, x across the wrap (beat 1 is 1.5 beats right of a playhead at 31.5)", wrap);
            // A phrase-folded vocal that crosses the phrase end continues as one piece (unwrapped); a loop's last point joins the next cycle's first.
            List<MelodyPoint> sung = new() { new(30, 60), new(30.5, 61), new(31, 62), new(31.5, 63), new(0, 64), new(.5, 65), new(1, float.NaN), new(4, 60), new(4.25, 61) };
            List<MelodyScroll.Piece> pieces = MelodyScroll.Pieces(sung, P, false, MashupSong.MaxGapBeats);
            bool unwrapped = pieces.Count == 2 && pieces[0].Beats.SequenceEqual(new[] { 30, 30.5, 31, 31.5, 32, 32.5 }) && pieces[1].First == 36;
            List<MelodyPoint> loop = new() { new(0, 60), new(.5, 62), new(31.5, 58) };
            List<MelodyScroll.Piece> cyc = MelodyScroll.Pieces(loop, P, true, MashupSong.MaxGapBeats);
            bool joined = cyc.Count == 2 && cyc[1].Beats.SequenceEqual(new[] { 31.5, 32.0 }) && Math.Abs(cyc[1].Pitches[1] - 60) < 1e-6;
            bool line = MelodyScroll.LineAt(pieces, 31.75, P, out float a) && Math.Abs(a - 63.5f) < 1e-4 &&
                        MelodyScroll.LineAt(pieces, .25, P, out float b) && Math.Abs(b - 64.5f) < 1e-4 &&
                        MelodyScroll.LineAt(cyc, 31.75, P, out float c) && Math.Abs(c - 59f) < 1e-4 && !MelodyScroll.LineAt(pieces, 2, P, out _);
            (int from, int to) = MelodyScroll.Images(30, 32.5, -5, 20, P);
            report.Check("pan maths: a vocal crossing the phrase end stays one piece; a loop's end joins its start; the line's pitch between points; the images a piece needs",
                unwrapped && joined && line && from == -1 && to == -1,
                $"{pieces.Count} pieces, {cyc.Count} loop pieces, images {from}..{to}");
        }

        // ------------------------------------------------------------------ synthetic duets from the featured paths

        /// <summary>
        /// A duets.json for up to three playable featured paths (3+ songs first), laid out as the
        /// pipeline's cycle: pairs of whole bars of the root's tempo (about 20 s), 2-bar handoffs
        /// centred on every boundary (the loop point's split over the end and the start), the bed the
        /// root's, a handoff's the entering song's (the leaving song's when the root enters),
        /// invented melodies and the axis progression. The loops do not exist (the clock plays).
        /// </summary>
        static string WriteSyntheticDuets(PathCatalog paths)
        {
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-duet-fixture");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, DuetCatalog.FileName);
            File.WriteAllText(file, SyntheticDuetsJson(paths), new UTF8Encoding(false));
            return file;
        }

        static string SyntheticDuetsJson(PathCatalog paths)
        {
            StringBuilder b = new("{\"version\": 1, \"generated_at\": \"validation\", \"frame\": \"C major / A minor\", \"paths\": [");
            int written = 0;
            foreach (FeaturedPath p in paths.Paths.Where(x => x.IsPlayable && x.Steps.Count >= 2).OrderByDescending(x => x.Steps.Count >= 3).ThenByDescending(x => x.Steps.Count).Take(3))
            {
                int n = p.Steps.Count;
                const int bpb = 4;
                double bpm = p.Steps[0].Bpm > 0 ? p.Steps[0].Bpm : 120;
                double bar = bpb * 60 / bpm;
                int pairBars = Math.Max(4, (int)Math.Round(20 / bar));
                int totalBars = n * pairBars;
                double total = totalBars * bar;
                int beats = totalBars * bpb;
                // Pair k from bar k·pairBars; the handoff into pair k is [k·pairBars − 1, k·pairBars + 1) bars.
                int Entering(int k) => (k + 1) % n;
                int Leaving(int k) => ((k - 1) % n + n) % n;
                int Borrowed(int k) => Entering(k) != 0 ? Entering(k) : Leaving(k);
                List<(double s, double e, string kind, int pair)> segs = new() { (0, bar, "handoff", 0) };
                for (int k = 0; k < n; k++)
                {
                    double start = k * pairBars * bar;
                    segs.Add((start + bar, start + (pairBars - 1) * bar, "duet", k));
                    segs.Add((start + (pairBars - 1) * bar, k + 1 < n ? start + (pairBars + 1) * bar : total, "handoff", (k + 1) % n));
                }
                if (written++ > 0) b.Append(',');
                PathStep root = p.Steps[0];
                b.Append($"{{\"id\": {Q(p.Id)}, \"title\": {Q(p.Title)}, \"file\": {Q(p.Id + "/loop.mp3")}, \"seconds\": {Inv(total)}, \"loops\": true, ")
                    .Append($"\"root\": {Q(root.WorkId)}, \"key\": {Q(root.Key.Length > 0 ? root.Key : "C major")}, \"bpm\": {Inv(bpm)}, \"beats_per_bar\": {bpb}, \"phrase_beats\": 32, \"segments\": [");
                b.Append(string.Join(", ", segs.Select(g =>
                {
                    int k = g.pair;
                    bool hand = g.kind == "handoff";
                    int inst = hand ? Borrowed(k) : 0;
                    return $"{{\"start\": {Inv(g.s)}, \"end\": {Inv(g.e)}, \"kind\": \"{g.kind}\", \"instrumental\": {Q(p.Steps[inst].WorkId)}, " +
                           $"\"vocals\": [{Q(p.Steps[k].WorkId)}, {Q(p.Steps[(k + 1) % n].WorkId)}], " +
                           $"\"entering\": {(hand ? Q(p.Steps[Entering(k)].WorkId) : "null")}, \"leaving\": {(hand ? Q(p.Steps[Leaving(k)].WorkId) : "null")}, " +
                           $"\"chord_match\": {(hand ? "0.75" : "0.875")}}}";
                })));
                b.Append("], \"beats\": [").Append(string.Join(", ", Enumerable.Range(0, beats).Select(i => $"[{Inv(i * 60 / bpm)}, {i}]"))).Append("], \"chords\": [");
                (int r, string q, string roman)[] axis = { (0, "maj", "I"), (7, "maj", "V"), (9, "min", "vi"), (5, "maj", "IV") };
                b.Append(string.Join(", ", Enumerable.Range(0, totalBars).Select(i => $"[{i * bpb}, {i * bpb + bpb}, {axis[i % 4].r}, \"{axis[i % 4].q}\", \"{axis[i % 4].roman}\"]")));
                b.Append("], \"songs\": [");
                for (int j = 0; j < n; j++)
                {
                    // Song j sings pairs j − 1 and j: from its entering handoff to its leaving one (bars, round the loop).
                    double fromBar = (j - 1) * pairBars - 1, toBar = (j + 1) * pairBars + 1;
                    if (n == 2) (fromBar, toBar) = (-1, totalBars + 1);
                    string melody = SyntheticDuetMelody(j, fromBar * bpb, Math.Min(toBar, fromBar + totalBars) * bpb, beats);
                    List<(double, double)> vocal = WrapSpan(fromBar * bar, Math.Min(toBar, fromBar + totalBars) * bar, total);
                    List<(double, double)> instrumental = new();
                    if (j == 0)
                        foreach (var g in segs.Where(g => g.kind == "duet")) instrumental.Add((g.s, g.e));
                    foreach (var g in segs.Where(g => g.kind == "handoff" && Borrowed(g.pair) == j)) instrumental.Add((g.s, g.e));
                    if (j > 0) b.Append(", ");
                    PathStep s = p.Steps[j];
                    b.Append($"{{\"work_id\": {Q(s.WorkId)}, \"title\": {Q(s.Title)}, \"artist\": {Q(s.Artist)}, \"year\": {s.Year}, \"step\": {j}, ")
                        .Append($"\"shift_semitones\": {(j == 0 ? 0 : KeyText.TryParse(root.Key, out int a, out _) && KeyText.TryParse(s.Key, out int c, out _) ? Morph.Wrap(a - c) : 0)}, ")
                        .Append($"\"tempo_ratio\": {Inv(s.Bpm > 0 ? bpm / s.Bpm : 1)}, \"melody\": [{melody}], ")
                        .Append($"\"vocal_audible\": [{string.Join(", ", vocal.Select(v => $"[{Inv(v.Item1)}, {Inv(v.Item2)}]"))}], ")
                        .Append($"\"instrumental_audible\": [{string.Join(", ", instrumental.Select(v => $"[{Inv(v.Item1)}, {Inv(v.Item2)}]"))}]}}");
                }
                b.Append("]}");
            }
            b.Append("]}");
            return b.ToString();
        }

        /// <summary>[a, b) folded into [0, total): one span, or two across the loop point.</summary>
        static List<(double, double)> WrapSpan(double a, double b, double total)
        {
            List<(double, double)> spans = new();
            if (b - a >= total - 1e-9) return new List<(double, double)> { (0, total) };
            double s = ((a % total) + total) % total, e = s + (b - a);
            if (e <= total + 1e-9) spans.Add((s, Math.Min(total, e)));
            else
            {
                spans.Add((s, total));
                spans.Add((0, e - total));
            }
            return spans;
        }

        /// <summary>An invented sung line between unwrapped beats <paramref name="from"/> and <paramref name="to"/> (every 1/8 beat), folded into the loop, in beat order.</summary>
        static string SyntheticDuetMelody(int k, double from, double to, int loopBeats)
        {
            System.Random rng = new(5100 + 41 * k);
            int[] notes = Enumerable.Range(55, 26).Where(p => new[] { 0, 2, 4, 5, 7, 9, 11 }.Contains(p % 12)).ToArray();
            int idx = Array.IndexOf(notes, 64) + (k % 3) - 1;
            List<(double beat, string pitch)> pts = new();
            double u = from;
            while (u < to - 1e-9)
            {
                double ph = ((u % 8) + 8) % 8;
                if (ph >= 6.5)
                {
                    pts.Add((u, "null"));
                    u = Math.Floor(u / 8 + 1e-9) * 8 + 8;
                    continue;
                }
                double[] durations = { .5, .5, 1, 1, 1, 1.5, 2 };
                double dur = Math.Min(durations[rng.Next(durations.Length)], 6.5 - ph);
                int[] moves = { -2, -1, -1, 0, 1, 1, 2, 3, -3 };
                idx = Math.Max(2, Math.Min(notes.Length - 3, idx + moves[rng.Next(moves.Length)]));
                for (double x = u; x < u + dur - 1e-9 && x < to - 1e-9; x += .125)
                {
                    double vib = .14 * Math.Sin(2 * Math.PI * 2.5 * (x - u)) * Math.Min(1, (x - u) / .5);
                    pts.Add((x, (notes[idx] + vib).ToString("0.###", CultureInfo.InvariantCulture)));
                }
                u += dur;
            }
            List<(double beat, string pitch)> folded = pts.Select(p => ((((p.beat % loopBeats) + loopBeats) % loopBeats), p.pitch)).ToList();
            folded.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return string.Join(",", folded.Select(p => $"[{Inv(p.Item1)}, {p.Item2}]"));
        }

        // ------------------------------------------------------------------ scene: one catalog at a time

        static void ValidateDuetScenes(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            string db = loader.ResolvedDbPath;
            PathCatalog paths = loader.Catalog;
            if (paths.PlayableCount == 0)
            {
                paths = SyntheticCatalog(loader.Data!);
                loader.UseCatalog(paths, "validation: synthetic paths for duets");
            }
            report.Text("duets_paths", $"{paths.SourcePath} ({paths.PlayableCount} playable)");
            List<(string label, Func<DuetCatalog> load)> catalogs = new();
            string real = DuetCatalog.DefaultPath();
            if (File.Exists(real)) catalogs.Add(("real", () => DuetCatalog.Load(real)));
            else report.Text("duets_real", $"skipped: {real} not written yet");
            string? fixture = PathArg("-validationDuets");
            if (fixture != null && report.Check("duets: -validationDuets file exists", File.Exists(fixture), fixture))
                catalogs.Add(("fixture", () => DuetCatalog.Load(fixture)));
            string synthetic = WriteSyntheticDuets(paths);
            catalogs.Add(("synthetic", () => DuetCatalog.Load(synthetic)));
            report.Text("duets_catalogs", string.Join(", ", catalogs.Select(x => x.label)));
            for (int i = 0; i < catalogs.Count; i++)
            {
                (string label, Func<DuetCatalog> load) = catalogs[i];
                loader.Build(db);
                if (loader.Catalog.PlayableCount == 0) loader.UseCatalog(paths, "validation: synthetic paths for duets");
                ValidateDuetCatalog(report, loader, cam, load(), label, outDir, width, height, canonicalShots: catalogs.Exists(x => x.label == "real") ? label == "real" : i == catalogs.Count - 1);
            }

            // No duets.json: no duet offered, K says so.
            loader.Build(db);
            if (loader.Catalog.PlayableCount == 0) loader.UseCatalog(paths, "validation: synthetic paths for duets");
            loader.UseDuets(DuetCatalog.Load(Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", DuetCatalog.FileName)), "validation: missing file");
            FeaturedPathsPanel panel = loader.Paths!;
            panel.HandleKey(Key.P);
            bool offered = panel.Rows.Any(r => r.DuetButton != null) || (panel.DuetPlayButton != null && panel.DuetPlayButton.gameObject.activeSelf);
            bool k = panel.HandleKey(Key.K) && !loader.Director.IsTouring && panel.Notice.Contains("no duet loop");
            panel.HandleKey(Key.Escape);
            report.Check("duets: without duets.json no path offers a duet loop; K says so and plays nothing", !offered && k, panel.Notice);
        }

        static void TickDuet(SongGraphLoader loader, float dt)
        {
            WalkthroughDirector d = loader.Director;
            if (d.DuetAudio != null) d.DuetAudio.Advance(dt);
            if (d.MashupAudio != null) d.MashupAudio.Advance(dt);
            if (d.Preview != null) d.Preview.Advance(dt);
            d.Tick(dt);
            if (d.ActivePlayer is SilentSongPlayer s) s.Advance(dt);
            // The captions follow the mix (edit mode runs no Update); the layout follows them.
            if (loader.Narration != null) loader.Narration.Advance(dt);
            if (loader.Layout != null) loader.Layout.ApplyNow();
            if (loader.MelodyGraph != null) loader.MelodyGraph.Refresh();
            if (loader.NarrationOverlay != null) loader.NarrationOverlay.Refresh(dt);
        }

        static void ValidateDuetCatalog(Report report, SongGraphLoader loader, Camera cam, DuetCatalog catalog, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            string L = $"duets[{label}]";
            bool synthetic = label == "synthetic";
            loader.UseDuets(catalog, "validation");
            report.Text($"duets_{label}_file", catalog.SourcePath);
            report.Number($"duets_{label}_count", catalog.Loops.Count, "0");
            report.Check($"{L}: loads (version 1, at least one loop)", catalog.Loaded && catalog.Version == 1 && catalog.Loops.Count > 0, catalog.Status);
            if (catalog.Loops.Count == 0) return;
            List<string> contract = catalog.Problems.Where(x => !synthetic || !x.Contains("loop missing")).ToList();
            report.Check($"{L}: no contract problems (segments, pairs, beats, chords, songs, files)", contract.Count == 0,
                $"{contract.Count}: {string.Join(" | ", contract.Take(5))}");
            List<string> unbound = catalog.Loops.Where(x => !x.IsPlayable).Select(x => $"{x.Id}: {DuetCatalog.WhyNotPlayable(x)}").ToList();
            report.Check($"{L}: every loop maps onto its featured path and its songs onto the graph", unbound.Count == 0, string.Join(" | ", unbound.Take(4)));
            List<string> coverage = catalog.Loops.SelectMany(x => DuetCatalog.CheckCoverage(x, .1)).ToList();
            report.Check($"{L}: two vocals audible at every moment of every loop; the root's bed under every duet segment", coverage.Count == 0,
                string.Join(" | ", coverage.Take(4)));
            List<string> cycle = new();
            foreach (DuetLoop x in catalog.Loops.Where(x => x.IsPlayable))
            {
                bool ok = x.Songs.Count == x.Path!.Steps.Count && x.Songs[x.RootSong].PathStep == 0 &&
                          Enumerable.Range(0, x.Pairs).All(k => x.Segments.Any(g => g.Pair == k)) &&
                          x.Segments.All(g => (g.VocalSongA, g.VocalSongB) == x.PairSongs(g.Pair) || (g.VocalSongB, g.VocalSongA) == x.PairSongs(g.Pair)) &&
                          Math.Abs(x.BeatAt(x.Duration - 1e-4) - x.LoopBeats) < .01;
                if (!ok) cycle.Add(x.Id);
                if (!x.FileExists && !synthetic) cycle.Add(x.Id + " (loop.mp3 missing)");
            }
            report.Check($"{L}: each loop cycles S0+S1 → … → S(n-1)+S0 round the path and its mix beat ends where it starts", cycle.Count == 0, string.Join(", ", cycle));
            DuetLoop l = catalog.Loops.Where(x => x.IsPlayable).OrderByDescending(x => x.Songs.Count >= 3).ThenByDescending(x => x.Songs.Count).ThenBy(x => x.Index).FirstOrDefault()
                         ?? catalog.Loops[0];
            if (!l.IsPlayable) return;
            report.Text($"duets_{label}_tour", $"{l.Id} ({l.Songs.Count} songs, {l.Segments.Count} segments, {l.Duration:0.0} s, {l.LoopBeats:0.##} beats)");
            ValidateDuetTour(report, loader, cam, l, L, label, outDir, width, height, canonicalShots);
        }

        static void ValidateDuetTour(Report report, SongGraphLoader loader, Camera cam, DuetLoop l, string L, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            DuetPlayer player = d.DuetAudio!;
            MelodyGraphPanel graph = loader.MelodyGraph!;
            GraphHud hud = loader.Hud;
            FeaturedPath path = l.Path!;
            string Shot(string name) => Path.Combine(outDir, canonicalShots ? $"{name}.png" : $"{name}_{label}.png");
            int wraps = 0;
            void OnWrap(int c) => wraps++;
            player.Wrapped += OnWrap;
            float fly = d.FlyDuration, grow = d.EdgeGrowDuration;
            try
            {
                ViewerLayout.ScreenOverride = new Vector2Int(width, height);
                loader.Layout?.ApplyNow();
                loader.Highlighter.ApplyFocus(null, force: true);
                loader.FrameOverview(cam);
                panel.HandleKey(Key.P);
                int index = panel.Paths.ToList().IndexOf(path);
                panel.Select(index);
                RowView? row = panel.Rows.FirstOrDefault(r => r.PathIndex == index);
                report.Check($"{L}: the list offers the path's duet loop: 'duet loop' on its row, a Duet button there and in the footer, K in the hints",
                    row != null && row.Subtitle.text.Contains("duet loop") && row.DuetButton != null && panel.DuetPlayButton != null &&
                    panel.DuetPlayButton.gameObject.activeSelf && panel.DuetPlayButton.interactable && panel.ListText.Length > 0,
                    row != null ? row.Subtitle.text : "no row");
                report.Check($"{L}: K plays the duet loop (not the narrated mix): the duet player, no mashup, no narration",
                    panel.HandleKey(Key.K) && d.IsTouring && d.CurrentPath == path && d.DuetMode && d.CurrentDuet == l && d.CurrentMashup == null &&
                    ReferenceEquals(d.ActivePlayer, player) && player.Current == l && panel.State == FeaturedPathsPanel.PanelState.Playing &&
                    !panel.NarrationAvailable && (loader.Narration == null || loader.Narration.CurrentPath == null),
                    $"{d.ActivePlayerName}; duet {d.CurrentDuet?.Id ?? "none"}; mashup {d.CurrentMashup?.Id ?? "none"}");
                graph.Refresh();
                report.Check($"{L}: the melody graph shows the duet: one line and one light per song, a timeline of mix beats that pans (landscape: one phrase per plot width)",
                    graph.Showing && graph.ShownDuet == l && graph.Shown == null && graph.Lines.Count == l.Songs.Count && graph.Dots.Count == l.Songs.Count &&
                    graph.Scrolling && !graph.Portrait && Math.Abs(graph.Period - l.LoopBeats) < 1e-9 &&
                    Mathf.Abs(graph.PixelsPerBeat - (float)(MelodyGraphPanel.PlotArea.width / l.PhraseBeats)) < 1e-3 && graph.KickerText.Contains("DUET"),
                    $"ppb {graph.PixelsPerBeat:0.###}, period {graph.Period:0.##}");

                // A whole cycle and past the wrap on the clock.
                d.FlyDuration = .4f;
                d.EdgeGrowDuration = .3f;
                List<string> wrong = new(), graphWrong = new();
                List<int> visited = new();
                double dt = .05, beatRate = l.Bpm / 60 * dt;
                double worstHead = 0, worstDot = 0, worstLine = 0, worstStep = 0, worstStepAtWrap = 0, worstPan = 0;
                int dotSamples = 0, lineSamples = 0, handoffSamples = 0, wrapSamples = 0;
                List<int> vocals = new(), instrumentals = new();
                double prevBeat = graph.PhraseBeat, prevTotal = player.TotalSeconds;
                float prevZeroX = float.NaN;
                double start = player.TotalSeconds, until = start + l.Duration + 4 * l.BarSeconds;
                int guard = 0;
                while (player.TotalSeconds < until && guard++ < 200000)
                {
                    TickDuet(loader, (float)dt);
                    double t = player.CurrentSeconds;
                    int si = l.SegmentIndexAt(t);
                    DuetSegment g = l.Segments[si];
                    if (visited.Count == 0 || visited[^1] != si) visited.Add(si);
                    (int pa, int pb) = l.PairSongs(g.Pair);
                    int sa = l.StepOf(pa), sb = l.StepOf(pb);
                    if (d.DuetSegmentIndex != si || d.DuetPair != g.Pair || d.DuetSingerA != sa || d.DuetSingerB != sb || d.StepIndex != g.Pair)
                        wrong.Add($"t {t:0.0}: segment {d.DuetSegmentIndex}/{si}, pair {d.DuetPair}/{g.Pair}, singers {d.DuetSingerA},{d.DuetSingerB}/{sa},{sb}");
                    else
                    {
                        SongNode a = loader.NodeById(path.Steps[sa].NodeId), b = loader.NodeById(path.Steps[sb].NodeId);
                        InfluenceEdge? e = GraphRoute.Between(a, b);
                        if (a.State != BubbleState.Focus || b.State != BubbleState.Focus || d.StepEdge != e || (e != null && e.State != EdgeState.Highlight))
                            wrong.Add($"t {t:0.0}: singers {a.State}/{b.State}, edge {(d.StepEdge == e ? "ok" : "other")} {e?.State}");
                        if (g.IsHandoff && g.InstrumentalSong >= 0 && g.InstrumentalSong != l.RootSong)
                        {
                            handoffSamples++;
                            int bs = l.StepOf(g.InstrumentalSong);
                            SongNode borrowed = loader.NodeById(path.Steps[bs].NodeId);
                            if (d.DuetBorrowedStep != bs || (bs != sa && bs != sb && borrowed.State != BubbleState.Related))
                                wrong.Add($"t {t:0.0}: borrowed {d.DuetBorrowedStep}/{bs} {borrowed.State}");
                        }
                    }
                    // The graph: beat continuous (through the wrap), the playhead at the centre, the lights on it and on their lines.
                    double beat = graph.PhraseBeat;
                    double step = MelodyScroll.WrapNearest(beat - prevBeat, l.LoopBeats);
                    double expected = MelodyScroll.WrapNearest(l.BeatAt(t) - l.BeatAt(t - dt), l.LoopBeats);
                    double stepErr = Math.Abs(step - expected);
                    worstStep = Math.Max(worstStep, stepErr);
                    bool atWrap = beat < prevBeat;
                    if (atWrap)
                    {
                        wrapSamples++;
                        worstStepAtWrap = Math.Max(worstStepAtWrap, stepErr);
                    }
                    if (Math.Abs(beat - l.BeatAt(t)) > 1e-6) graphWrong.Add($"t {t:0.00}: beat {beat:0.000} vs {l.BeatAt(t):0.000}");
                    // The loop's start (beat 0) pans right to left, continuously through the wrap.
                    float zeroX = graph.PanelPoint(0, 60).x;
                    if (!float.IsNaN(prevZeroX) && Mathf.Abs(zeroX - MelodyGraphPanel.ScrollHeadX) < MelodyGraphPanel.PlotArea.width * .5f)
                        worstPan = Math.Max(worstPan, Math.Abs(zeroX - prevZeroX + (float)(expected * graph.PixelsPerBeat)));
                    prevZeroX = zeroX;
                    prevBeat = beat;
                    worstHead = Math.Max(worstHead, Math.Abs(graph.PlayheadX - MelodyGraphPanel.ScrollHeadX));
                    worstHead = Math.Max(worstHead, Math.Abs(graph.ScreenPoint(new Vector2(graph.PlayheadX, 0), width, height).x - width * .5f));
                    l.AudibleAt(t, vocals, instrumentals);
                    foreach (MelodyGraphPanel.LineView line in graph.Lines)
                        if (line.Playing != vocals.Contains(line.Song)) graphWrong.Add($"t {t:0.0}: song {line.Song} playing {line.Playing}");
                    foreach (MelodyGraphPanel.DotView dot in graph.Dots)
                    {
                        bool expectedOn = vocals.Contains(dot.Song) && (MelodyScroll.LineAt(graph.Lines[dot.Song].Sung, beat, l.LoopBeats, out _) ||
                                                                         l.Songs[dot.Song].Line.PitchAt(beat, out _, out _));
                        if (dot.Active != expectedOn) graphWrong.Add($"t {t:0.0}: dot {dot.Song} active {dot.Active}");
                        if (!dot.Active) continue;
                        dotSamples++;
                        worstDot = Math.Max(worstDot, Math.Abs(dot.Position.x - MelodyGraphPanel.ScrollHeadX));
                        if (MelodyScroll.LineAt(graph.Lines[dot.Song].Sung, beat, l.LoopBeats, out _))
                        {
                            lineSamples++;
                            worstLine = Math.Max(worstLine, LineGap(graph, graph.Lines[dot.Song], dot.Position));
                        }
                    }
                    if (graph.StripSong != l.RootSong) graphWrong.Add($"t {t:0.0}: strip {graph.StripSong}");
                    if (player.TotalSeconds < prevTotal - 1e-9) graphWrong.Add($"t {t:0.0}: the clock went back {prevTotal:0.000} → {player.TotalSeconds:0.000}");
                    prevTotal = player.TotalSeconds;
                }
                bool all = Enumerable.Range(0, l.Segments.Count).All(visited.Contains);
                report.Check($"{L}: a whole cycle on the clock: every segment in order, round the loop point (once), the cycle counted, never complete",
                    all && wraps == 1 && player.Cycle == 1 && !d.TourComplete && d.IsTouring && visited[0] == l.SegmentIndexAt(l.PairStartSeconds(0)) && visited.Count >= l.Segments.Count,
                    $"visited {string.Join(",", visited)}; wraps {wraps}; cycle {player.Cycle}");
                report.Check($"{L}: the pair follows the loop: both singers glow, the edge between them lights up, a handoff's borrowed instrumental's song is marked",
                    wrong.Count == 0, $"{handoffSamples} borrowed-handoff samples; {string.Join(" | ", wrong.Take(3))}");
                report.Check($"{L}: the melody graph follows: the two singing melodies bright (= the audible vocals), lights on them, the strip the bed's chords",
                    graphWrong.Count == 0 && dotSamples > 0 && graph.ChordText.Length > 0, $"{dotSamples} light samples; {string.Join(" | ", graphWrong.Take(3))}");
                report.Check($"{L}: the playhead stays at the screen's centre (±0.5 px) and every light sits on it, on its drawn melody (±0.5 px)",
                    worstHead <= .5 && worstDot <= .5 && worstLine <= .5 && lineSamples > 0,
                    $"playhead {worstHead:0.000} px, lights {worstDot:0.000} px, line {worstLine:0.000} px over {lineSamples} samples");
                report.Check($"{L}: the timeline pans right to left continuously, through the loop point too (no jump in the beat or the content)",
                    worstStep < 1e-3 && wrapSamples >= 1 && worstStepAtWrap < 1e-3 && worstPan < .5,
                    $"beat step error {worstStep:0.00000} (at the wrap {worstStepAtWrap:0.00000}, {wrapSamples} wraps), pan error {worstPan:0.000} px");

                // Mid-duet and mid-handoff: the strip, the walkthrough text, the camera.
                int duetPair = l.Segments.First(g => !g.IsHandoff).Pair;
                d.GoTo(duetPair);
                bool jumped = Math.Abs(player.CurrentSeconds - l.PairStartSeconds(duetPair)) < 1e-6 && d.DuetPair == duetPair;
                DuetSegment mid = l.Segments.First(g => !g.IsHandoff && g.Pair == duetPair);
                for (int k = 0; k < 4000 && player.CurrentSeconds < (mid.Start + mid.End) / 2; k++) TickDuet(loader, .05f);
                for (int k = 0; k < 40 && d.Flying; k++) TickDuet(loader, .05f);
                panel.RefreshNowPlaying();
                string strip = panel.NowPlayingText;
                (int ma, int mb) = l.PairSongs(mid.Pair);
                bool stripOk = strip.Contains("Duet:") && strip.Contains(GraphHud.Esc(l.Songs[ma].Title)) && strip.Contains(GraphHud.Esc(l.Songs[mb].Title)) &&
                               strip.Contains("over") && strip.Contains(GraphHud.Esc(l.Songs[l.RootSong].Title)) && strip.Contains("Key") && strip.Contains(GraphHud.Esc(l.Key)) &&
                               strip.Contains("BPM") && (mid.ChordMatch == null || strip.Contains(DuetCatalog.Percent(mid.ChordMatch))) && strip.Contains("LOOP") &&
                               strip.Contains("DUET LOOP") && strip.Contains($"PAIR {mid.Pair + 1} / {l.Pairs}") && CountOf(strip, "VOCAL") == 2 && strip.Contains("BED") &&
                               !strip.Contains("NARRATED");
                report.Check($"{L}: the strip reads 'Duet: A + B over <root>', key, BPM, chord match, LOOP with the position; both singers' chips VOCAL, the root's BED",
                    jumped && stripOk, strip.Replace("\n", " / "));
                report.Check($"{L}: the walkthrough text names the pair ('Duet: A + B over <root>', pair, LOOP)",
                    d.HudText.Contains(WalkthroughDirector.DuetDescription(l, mid)) && d.HudText.Contains("LOOP"), FirstLines(d.HudText, 3));
                loader.RefreshView(cam);
                Rect melodyRect = hud.ScreenRect(graph.PanelRect!, width, height);
                bool framed = new[] { ma, mb }.All(s =>
                {
                    Vector3 v = cam.WorldToViewportPoint(loader.NodeById(l.Songs[s].NodeId).transform.position);
                    return v.z > 0 && v.x > .02f && v.x < .98f && v.y * height > melodyRect.yMax && v.y < .98f;
                });
                report.Check($"{L}: the camera frames the pair above the melody graph", framed);
                // The wheel and the bed's strip light the chord sounding; the path's name; both singers 3x.
                CheckLitChords(report, L, loader, cam, width, height, pixels: canonicalShots);
                CheckWheelPlacement(report, L, loader, width, height);
                CheckPathTitle(report, L, loader, path, width, height);
                CheckHighlightedBubbles(report, L, loader, cam, new[] { loader.NodeById(l.Songs[ma].NodeId), loader.NodeById(l.Songs[mb].NodeId) }, width, height);
                IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
                report.Check($"{L}: the melody graph is on screen and never overlaps the strip, legend, info or button",
                    NoOverlap(rects) && rects.Contains(melodyRect) && melodyRect.xMin >= 0 && melodyRect.xMax <= width && melodyRect.yMax <= height);
                string shot = Shot("duet_landscape");
                Capture(cam, loader, shot, width, height, out _);
                CheckLabels(report, loader, $"{label} duet");
                report.Check($"{L}: {Path.GetFileName(shot)} written (mid-duet)", File.Exists(shot));

                DuetSegment? hand = l.Segments.FirstOrDefault(g => g.IsHandoff && g.InstrumentalSong != l.RootSong && g.Length > .5);
                if (hand != null)
                {
                    player.Seek(hand.Start + hand.Length * .5);
                    TickDuet(loader, .05f);
                    panel.RefreshNowPlaying();
                    string hs = panel.NowPlayingText;
                    report.Check($"{L}: mid-handoff the strip names the entering vocal and the borrowed instrumental; its chip reads INSTRUMENTAL",
                        hs.Contains("handoff") && hs.Contains("INSTRUMENTAL") && hs.Contains(GraphHud.Esc(l.Songs[hand.InstrumentalSong].Title)) && d.DuetBorrowedStep == l.StepOf(hand.InstrumentalSong),
                        hs.Replace("\n", " / "));
                }

                // Next / Back round the loop; pause; M; K to the narrated mix and back; Esc.
                int n = l.Pairs;
                d.GoTo(n - 1);
                d.Next();
                bool nextWraps = d.DuetPair == 0 && Math.Abs(player.CurrentSeconds - l.PairStartSeconds(0)) < 1e-6;
                d.Previous();
                bool backWraps = d.DuetPair == n - 1 && Math.Abs(player.CurrentSeconds - l.PairStartSeconds(n - 1)) < 1e-6;
                d.Previous();
                bool back = d.DuetPair == (n - 2 + n) % n;
                panel.HandleKey(Key.RightArrow);
                bool arrow = d.DuetPair == n - 1;
                report.Check($"{L}: Next / → jump to the next pair's start, from the last pair round to the first; Back / ← the other way",
                    nextWraps && backWraps && back && arrow, $"pair {d.DuetPair}, {player.CurrentSeconds:0.00} s");
                panel.HandleKey(Key.Space);
                double held = player.CurrentSeconds;
                for (int k = 0; k < 10; k++) TickDuet(loader, .1f);
                bool frozen = player.Paused && Math.Abs(player.CurrentSeconds - held) < 1e-9;
                panel.HandleKey(Key.Space);
                for (int k = 0; k < 5; k++) TickDuet(loader, .1f);
                report.Check($"{L}: Space pauses the loop (clock frozen) and resumes it", frozen && !player.Paused && player.CurrentSeconds > held);
                bool hidden = panel.HandleKey(Key.M) && !graph.UserVisible && Refresh(graph) && !graph.Showing && d.FramingViewport == d.TourViewport;
                bool shownAgain = panel.HandleKey(Key.M) && graph.UserVisible && Refresh(graph) && graph.Showing && d.FramingViewport == d.MashupTourViewport;
                report.Check($"{L}: M toggles the melody graph (the framing makes room for it)", hidden && shownAgain);
                bool variant = panel.VariantButton != null && panel.VariantButton.gameObject.activeSelf;
                bool toMix = panel.HandleKey(Key.K) && d.IsTouring && !d.DuetMode && d.CurrentDuet == null && !ReferenceEquals(d.ActivePlayer, player) &&
                             player.Current == null && (d.MashupFor(path) == null || d.CurrentMashup == d.MashupFor(path));
                graph.Refresh();
                bool mixGraph = d.CurrentMashup == null ? !graph.Showing : graph.Shown == d.CurrentMashup;
                bool toDuet = panel.HandleKey(Key.K) && d.DuetMode && d.CurrentDuet == l && ReferenceEquals(d.ActivePlayer, player);
                report.Check($"{L}: K (and the strip's Duet / Mix button) switches to the path's narrated mix (or previews) and back to the duet loop",
                    variant && toMix && mixGraph && toDuet, $"{d.ActivePlayerName}");
                panel.HandleKey(Key.C);
                bool compare = d.CurrentDuet == null && !ReferenceEquals(d.ActivePlayer, player) && Refresh(graph) && !graph.Showing;
                panel.HandleKey(Key.C);
                report.Check($"{L}: C (compare) plays the MIDI and hides the graph; C again plays the duet loop", compare && d.CurrentDuet == l && ReferenceEquals(d.ActivePlayer, player));

                // Portrait: three times larger, panning under the centre.
                ViewerLayout.ScreenOverride = new Vector2Int(1080, 1920);
                cam.aspect = 1080f / 1920f;
                loader.Layout?.ApplyNow();
                graph.Refresh();
                for (int k = 0; k < 10; k++) TickDuet(loader, .05f);
                loader.Layout?.ApplyNow();
                for (int k = 0; k < 40 && d.Flying; k++) TickDuet(loader, .05f);
                loader.RefreshView(cam);
                float landscapePpb = (float)(MelodyGraphPanel.PlotArea.width / l.PhraseBeats);
                Vector2 head = graph.ScreenPoint(new Vector2(graph.PlayheadX, 0), 1080, 1920);
                MelodyGraphPanel.DotView? pd = graph.Dots.FirstOrDefault(x => x.Active);
                report.Check($"{L}: portrait 1080x1920: the melody graph is three times larger (px per beat and per semitone), pans, the playhead at the screen's centre (±0.5 px)",
                    graph.Portrait && graph.Scrolling && Mathf.Abs(graph.Height - MelodyGraphPanel.PortraitPanelHeight) < .01f &&
                    Mathf.Abs(graph.Plot.height - MelodyGraphPanel.PortraitScale * MelodyGraphPanel.LandscapePlotHeight) < .01f &&
                    Mathf.Abs(graph.PixelsPerBeat - MelodyGraphPanel.PortraitScale * landscapePpb) < 1e-3 && Mathf.Abs(head.x - 540f) <= .5f &&
                    (pd == null || Mathf.Abs(pd.Position.x - MelodyGraphPanel.ScrollHeadX) <= .5f),
                    $"height {graph.Height:0}, plot {graph.Plot.height:0} (landscape {MelodyGraphPanel.LandscapePlotHeight:0}), ppb {graph.PixelsPerBeat:0.##} vs {landscapePpb:0.##}, playhead at {head.x:0.00} px");
                CheckLitChords(report, L + " portrait", loader, cam, 1080, 1920, pixels: false);
                CheckWheelPlacement(report, L + " portrait", loader, 1080, 1920);
                CheckPathTitle(report, L + " portrait", loader, path, 1080, 1920);
                CheckHighlightedBubbles(report, L + " portrait", loader, cam, loader.Nodes.Where(n => n.State == BubbleState.Focus).ToList(), 1080, 1920);
                IReadOnlyList<Rect> portraitRects = hud.PanelScreenRects(1080, 1920);
                Rect pr = hud.ScreenRect(graph.PanelRect!, 1080, 1920);
                LayoutFrame frame = loader.Layout != null ? loader.Layout.Frame : default;
                report.Check($"{L}: portrait layout: the graph on screen, right above the strip (no narration band), clear of every panel; the pair framed above it",
                    NoOverlap(portraitRects) && portraitRects.Contains(pr) && pr.xMin >= -.5f && pr.xMax <= 1080.5f && pr.yMax <= 1920 &&
                    frame.Vertical && Mathf.Abs(frame.Melody.yMin - (frame.Strip.yMax + ViewerLayout.BandGap)) < .5f &&
                    new[] { ma, mb }.All(s =>
                    {
                        Vector3 v = cam.WorldToViewportPoint(loader.NodeById(l.Songs[s].NodeId).transform.position);
                        return v.z > 0 && v.x > .0f && v.x < 1f && v.y * 1920 > pr.yMax - 1;
                    }),
                    $"melody [{pr.xMin:0},{pr.yMin:0} {pr.width:0}x{pr.height:0}]; {string.Join(" ", portraitRects.Select(x => $"[{x.xMin:0},{x.yMin:0} {x.width:0}x{x.height:0}]"))}");
                string portraitShot = Shot("duet_portrait");
                Capture(cam, loader, portraitShot, 1080, 1920, out _);
                report.Check($"{L}: {Path.GetFileName(portraitShot)} written (1080x1920)", File.Exists(portraitShot));
                MelodyLightRig? rig = graph.Lights;
                report.Check($"{L}: portrait: the light texture covers the taller panel pixel for pixel",
                    rig != null && rig.Texture != null && rig.Texture.height == Mathf.RoundToInt(MelodyGraphPanel.PortraitPanelHeight * hud.ScaleFor(1080, 1920)),
                    rig?.Texture != null ? $"{rig.Texture.width}x{rig.Texture.height}" : "none");

                // Back to landscape.
                ViewerLayout.ScreenOverride = new Vector2Int(width, height);
                cam.aspect = (float)width / height;
                loader.Layout?.ApplyNow();
                graph.Refresh();
                report.Check($"{L}: back in landscape the duet graph keeps panning at its landscape size", !graph.Portrait && graph.Scrolling &&
                    Mathf.Abs(graph.Height - MelodyGraphPanel.PanelHeight) < .01f);
                panel.HandleKey(Key.Escape);
                graph.Refresh();
                report.Check($"{L}: Esc stops the loop, hides the graph and returns to the list", !d.IsTouring && player.Current == null && !graph.Showing && panel.IsOpen);
                CheckBubblesRestored(report, L, loader);
                panel.HandleKey(Key.Escape);
            }
            finally
            {
                player.Wrapped -= OnWrap;
                d.FlyDuration = fly;
                d.EdgeGrowDuration = grow;
                ViewerLayout.ScreenOverride = null;
                cam.aspect = (float)width / height;
                d.Exit();
            }
        }

        static int CountOf(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>Distance (px) from <paramref name="at"/> to the line's drawn pieces at its x (panel px); infinity when none crosses it.</summary>
        static float LineGap(MelodyGraphPanel graph, MelodyGraphPanel.LineView line, Vector2 at)
        {
            float best = float.PositiveInfinity;
            float cx = at.x - graph.ContentShift;
            foreach (List<Vector2> piece in line.Pieces)
                for (int i = 0; i + 1 < piece.Count; i++)
                {
                    Vector2 a = piece[i], b = piece[i + 1];
                    if (cx < Mathf.Min(a.x, b.x) - 1e-3f || cx > Mathf.Max(a.x, b.x) + 1e-3f) continue;
                    float u = Mathf.Abs(b.x - a.x) > 1e-6f ? (cx - a.x) / (b.x - a.x) : 0f;
                    best = Mathf.Min(best, Mathf.Abs(a.y + (b.y - a.y) * u - at.y));
                }
            return best;
        }

        // ------------------------------------------------------------------ a narrated mix in portrait

        static void ValidatePortraitMashup(Report report, SongGraphLoader loader, Camera cam, string outDir)
        {
            loader.Build(loader.ResolvedDbPath);
            PathCatalog paths = loader.Catalog.PlayableCount > 0 ? loader.Catalog : SyntheticCatalog(loader.Data!);
            if (loader.Catalog.PlayableCount == 0) loader.UseCatalog(paths, "validation: synthetic paths");
            // The real mixes when the pipeline wrote them (their melodies, chords and captions), else synthetic ones.
            string realMixes = Path.Combine(SongGraphLoader.RepoRoot(), "data", "audio", "mashups", MashupCatalog.FileName);
            Mashup? m = null;
            if (File.Exists(realMixes))
            {
                loader.UseMashups(MashupCatalog.Load(realMixes), "validation: real mashups (portrait)");
                m = loader.Mashups.Mashups.Where(x => x.IsPlayable).OrderByDescending(x => x.Songs.Count).FirstOrDefault();
            }
            if (m == null)
            {
                loader.UseMashups(MashupCatalog.Load(WriteSyntheticMashups(paths)), "validation: synthetic mashups (portrait)");
                m = loader.Mashups.Mashups.Where(x => x.IsPlayable).OrderByDescending(x => x.Songs.Count).FirstOrDefault();
            }
            if (!report.Check("portrait mix: a narrated mix to play (the real one when written, else synthetic)", m != null, m?.Id ?? "none")) return;
            WalkthroughDirector d = loader.Director;
            MelodyGraphPanel graph = loader.MelodyGraph!;
            try
            {
                // Landscape first: the static phrase graph, unchanged.
                ViewerLayout.ScreenOverride = new Vector2Int(1920, 1080);
                cam.aspect = 1920f / 1080f;
                loader.Layout?.ApplyNow();
                d.StartPathTour(m!.Path!);
                graph.Refresh();
                report.Check("landscape mix: the melody graph is the static phrase graph as before (whole phrase across the plot, moving playhead)",
                    graph.Showing && !graph.Scrolling && !graph.Portrait && Mathf.Abs(graph.Height - MelodyGraphPanel.PanelHeight) < .01f &&
                    Mathf.Abs(graph.PixelsPerBeat - (float)(MelodyGraphPanel.PlotArea.width / m.PhraseBeats)) < 1e-3);
                float landscapePpb = graph.PixelsPerBeat, landscapeSemitone = graph.PixelsPerSemitone;

                ViewerLayout.ScreenOverride = new Vector2Int(1080, 1920);
                cam.aspect = 1080f / 1920f;
                loader.Layout?.ApplyNow();
                graph.Refresh();
                loader.Layout?.ApplyNow();
                report.Check("portrait mix: three times larger (px per beat and per semitone), panning under the screen's centre",
                    graph.Portrait && graph.Scrolling && Mathf.Abs(graph.PixelsPerBeat - 3 * landscapePpb) < 1e-3 && Mathf.Abs(graph.PixelsPerSemitone - 3 * landscapeSemitone) < 1e-3,
                    $"ppb {graph.PixelsPerBeat:0.##} vs {landscapePpb:0.##}, px/semitone {graph.PixelsPerSemitone:0.##} vs {landscapeSemitone:0.##}");
                // Through the phrase's wrap: the beat continuous, the playhead and the lights on the centre and on their lines.
                double prev = graph.PhraseBeat, worstStep = 0, worstLine = 0, worstHead = 0;
                int wraps = 0, lineSamples = 0;
                MashupPlayer player = d.MashupAudio!;
                double end = Math.Min(m.Duration - .5, player.CurrentSeconds + 3 * m.PhraseBeats * 60 / Math.Max(40, m.Segments[0].Bpm));
                for (int k = 0; k < 20000 && player.CurrentSeconds < end; k++)
                {
                    double t0 = player.CurrentSeconds;
                    TickDuet(loader, .05f);
                    double t = player.CurrentSeconds;
                    double beat = graph.PhraseBeat;
                    if (beat < prev) wraps++;
                    worstStep = Math.Max(worstStep, Math.Abs(MelodyScroll.WrapNearest(beat - prev, m.PhraseBeats) - MelodyScroll.WrapNearest(m.PhraseBeatAt(t) - m.PhraseBeatAt(t0), m.PhraseBeats)));
                    prev = beat;
                    worstHead = Math.Max(worstHead, Math.Abs(graph.ScreenPoint(new Vector2(graph.PlayheadX, 0), 1080, 1920).x - 540f));
                    foreach (MelodyGraphPanel.DotView dot in graph.Dots)
                    {
                        if (!dot.Active) continue;
                        worstHead = Math.Max(worstHead, Math.Abs(dot.Position.x - MelodyGraphPanel.ScrollHeadX));
                        if (!MelodyScroll.LineAt(graph.Lines[dot.Song].Sung, beat, m.PhraseBeats, out _)) continue;
                        lineSamples++;
                        worstLine = Math.Max(worstLine, LineGap(graph, graph.Lines[dot.Song], dot.Position));
                    }
                }
                report.Check("portrait mix: the phrase-folded graph wraps round the phrase as it pans (beat continuous), playhead and lights at the centre, lights on their lines",
                    wraps >= 1 && worstStep < 1e-3 && worstHead <= .5 && worstLine <= .5 && lineSamples > 0,
                    $"{wraps} phrase wraps, beat step error {worstStep:0.00000}, centre {worstHead:0.000} px, line {worstLine:0.000} px ({lineSamples} samples)");
                LayoutFrame frame = loader.Layout != null ? loader.Layout.Frame : default;
                // The 3D view sits between the 3x graph and the 2.5x chord wheel (at least a fifth of the screen).
                report.Check("portrait mix: the 3x graph sits above the narration band, the 3D view above it, under the chord wheel", frame.Vertical &&
                    Mathf.Abs(frame.Melody.height - MelodyGraphPanel.PortraitPanelHeight) < .01f && frame.MashupView.height >= .2f &&
                    frame.MashupView.yMax * frame.Canvas.y <= frame.Wheel.yMin + .5f,
                    $"melody {frame.Melody}, view {frame.MashupView}");
                loader.RefreshView(cam);
                string shot = Path.Combine(outDir, "melody_portrait.png");
                Capture(cam, loader, shot, 1080, 1920, out _);
                report.Check("portrait mix: melody_portrait.png written (1080x1920)", File.Exists(shot));

                ViewerLayout.ScreenOverride = new Vector2Int(1920, 1080);
                cam.aspect = 1920f / 1080f;
                loader.Layout?.ApplyNow();
                graph.Refresh();
                report.Check("landscape mix again: back to the static phrase graph", !graph.Scrolling && !graph.Portrait && Mathf.Abs(graph.Height - MelodyGraphPanel.PanelHeight) < .01f);
            }
            finally
            {
                ViewerLayout.ScreenOverride = null;
                cam.aspect = 1920f / 1080f;
                d.Exit();
            }
        }

        // ------------------------------------------------------------------ photos on bubbles

        static void ValidateBubblePhotos(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            loader.Build(loader.ResolvedDbPath);
            NarrationCatalog narration = loader.NarrationCatalog;
            // Synthetic: two photos by work_ids on the most influential songs, one by artist name, one unlicensed (never shown).
            List<SongNode> big = loader.Nodes.OrderByDescending(n => n.Song.Descendants).ThenBy(n => n.NodeId).Take(2).ToList();
            SongNode? named = loader.Nodes.FirstOrDefault(n => !big.Contains(n) && n.Song.Artist.Length > 3 &&
                                                              loader.Nodes.Count(x => BubblePhotos.NameKey(x.Song.Artist) == BubblePhotos.NameKey(n.Song.Artist)) >= 1);
            SongNode? unlicensed = loader.Nodes.FirstOrDefault(n => !big.Contains(n) && n != named);
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-photo-fixture");
            Directory.CreateDirectory(Path.Combine(dir, "artists"));
            // The photo's colour: magenta or green, whichever is further from the first bubble's own key colour.
            Color32 magenta = new(230, 30, 220, 255), green = new(30, 220, 60, 255);
            Color key = big.Count > 0 ? big[0].KeyColor : Color.grey;
            bool useGreen = Vector4.Distance(key, (Color)magenta) < Vector4.Distance(key, (Color)green);
            Color32 photoColor = useGreen ? green : magenta;
            Texture2D jpg = new(96, 128, TextureFormat.RGB24, false);
            Color32[] px = new Color32[96 * 128];
            for (int i = 0; i < px.Length; i++) px[i] = photoColor;
            jpg.SetPixels32(px);
            jpg.Apply();
            byte[] bytes = jpg.EncodeToJPG(95);
            Object.DestroyImmediate(jpg);
            string[] ids = { "artist-Q900001", "artist-Q900002", "artist-Q900003", "artist-Q900004" };
            foreach (string id in ids) File.WriteAllBytes(Path.Combine(dir, "artists", id + ".jpg"), bytes);
            StringBuilder json = new("{\"version\": 1, \"images\": {");
            for (int i = 0; i < big.Count; i++)
                json.Append($"{(i > 0 ? ", " : "")}{Q(ids[i])}: {{\"file\": \"artists/{ids[i]}.jpg\", \"subject\": \"Validation photo {i + 1}\", \"artist_qid\": \"Q90000{i + 1}\", " +
                            $"\"work_ids\": [{Q(big[i].Song.WorkId)}], \"author\": \"Validation\", \"license\": \"CC0\", \"license_url\": null}}");
            if (named != null)
                json.Append($", {Q(ids[2])}: {{\"file\": \"artists/{ids[2]}.jpg\", \"subject\": {Q(named.Song.Artist)}, \"work_ids\": [], \"author\": \"Validation\", \"license\": \"CC0\"}}");
            if (unlicensed != null)
                json.Append($", {Q(ids[3])}: {{\"file\": \"artists/{ids[3]}.jpg\", \"subject\": \"No licence\", \"work_ids\": [{Q(unlicensed.Song.WorkId)}], \"author\": \"Validation\"}}");
            json.Append("}}");
            string file = Path.Combine(dir, ArtistImages.FileName);
            File.WriteAllText(file, json.ToString(), new UTF8Encoding(false));
            try
            {
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                loader.UseNarration(narration, ArtistImages.Load(file), "validation: synthetic photos");
                clock.Stop();
                BubblePhotos photos = loader.BubblePhotos!;
                report.Number("bubble_photos_apply_ms", clock.Elapsed.TotalMilliseconds, "0.0");
                bool mapped = big.All(n => photos.PhotoFor(n.NodeId) is string id && ids.Take(2).Contains(id) && photos.HowMapped(n.NodeId).Contains("work_ids")) &&
                              (named == null || (photos.PhotoFor(named.NodeId) == ids[2] && photos.HowMapped(named.NodeId).Contains("artist name"))) &&
                              (unlicensed == null || photos.PhotoFor(unlicensed.NodeId) == null || photos.PhotoFor(unlicensed.NodeId) != ids[3]);
                report.Check("photos: songs map to catalogued photos by work_ids, else by the artist's name; an unlicensed photo is never used",
                    mapped && photos.Mapped >= big.Count, photos.Status);
                List<SongNode> with = loader.Nodes.Where(n => n.PhotoRenderer != null).ToList();
                bool lazy = with.Count == photos.Mapped && with.All(n => !n.PhotoRenderer!.enabled) && photos.Pending == photos.PhotosUsed;
                report.Check("photos: textures load lazily (nothing decoded before a bubble is in view)", lazy, $"{photos.Pending} pending of {photos.PhotosUsed}");
                bool noCollider = with.All(n => n.PhotoRenderer!.GetComponent<Collider>() == null && n.GetComponent<SphereCollider>() != null);
                bool scaled = with.All(n => Mathf.Abs(n.PhotoRenderer!.transform.localScale.x - n.Radius * 2f * photos.PhotoScale) < 1e-4f);
                photos.LoadAll();
                bool loaded = with.All(n => n.PhotoRenderer!.enabled && n.PhotoRenderer.sharedMaterial != null &&
                                            n.PhotoRenderer.sharedMaterial.shader.name == BubblePhotos.ShaderName && BubblePhotos.IsShared(n.PhotoRenderer.sharedMaterial) &&
                                            n.PhotoRenderer.sharedMaterial.GetTexture("_MainTex") == loader.ArtistImages.Texture(n.PhotoId));
                report.Check("photos: a round billboard on the bubble, scaled with it, no collider; shared materials; the texture shared with the popup card",
                    noCollider && scaled && loaded && photos.Loaded == photos.PhotosUsed && loader.ArtistImages.TexturesLoaded == photos.PhotosUsed,
                    $"{with.Count} bubbles, {photos.Loaded} photos loaded, {BubblePhotos.MaterialCount} materials");
                SongNode first = big[0];
                first.SetState(BubbleState.Dimmed);
                Material dim = first.PhotoRenderer!.sharedMaterial;
                first.SetState(BubbleState.Focus);
                Material focus = first.PhotoRenderer.sharedMaterial;
                first.SetState(BubbleState.Normal);
                report.Check("photos: they follow the bubble's state (a dimmed bubble's photo dims and greys)",
                    dim.GetFloat("_DimFactor") < .5f && dim.GetFloat("_Saturation") < 1f && focus.GetFloat("_DimFactor") >= 1f && dim != focus);

                // Picking and the picture: frame the first photo's bubble, pick at its centre, render with and without the photo.
                List<(Vector3, float)> items = new() { (first.transform.position, first.Radius * 3f) };
                (Vector3 pos, Quaternion rot) = CameraFraming.Frame(cam, items, loader.Frame.ViewForward, 1.2f, 2f, new Rect(.3f, .3f, .4f, .4f));
                cam.transform.SetPositionAndRotation(pos, rot);
                cam.aspect = (float)width / height;
                Vector3 sp = cam.WorldToScreenPoint(first.transform.position);
                Physics.SyncTransforms();
                report.Check("photos: hover / click still pick the bubble through its photo", HoverHighlighter.Pick(cam, sp) == first,
                    $"{HoverHighlighter.Pick(cam, sp)?.Song.Title ?? "nothing"} at ({sp.x:0}, {sp.y:0})");
                Color with1 = Pixel(cam, loader, width, height, first.transform.position);
                first.PhotoRenderer.enabled = false;
                Color without = Pixel(cam, loader, width, height, first.transform.position);
                first.PhotoRenderer.enabled = true;
                bool hue = useGreen ? with1.g > with1.r + .25f && with1.g > with1.b + .25f : with1.r > with1.g + .25f && with1.b > with1.g + .25f;
                report.Check($"photos: the photo is drawn on its bubble, in front of the sphere (a capture with and without it differ at the bubble's centre; {(useGreen ? "green" : "magenta")})",
                    Vector4.Distance(with1, without) > .15f && hue, $"with {with1}, without {without}");
                string shot = Path.Combine(outDir, "bubble_photos.png");
                Capture(cam, loader, shot, width, height, out _);
                report.Check("photos: bubble_photos.png written", File.Exists(shot));
            }
            finally
            {
                loader.FrameOverview(cam);
            }

            // The real catalogue, when the pipeline wrote it.
            string real = ArtistImages.DefaultPath();
            if (!File.Exists(real))
            {
                report.Text("bubble_photos_real", $"skipped: {real} not written yet");
            }
            else
            {
                loader.UseNarration(narration, ArtistImages.Load(real), "validation: real photos");
                BubblePhotos photos = loader.BubblePhotos!;
                photos.LoadAll();
                List<string> bad = photos.Mapping.Values.Distinct().Where(id => loader.ArtistImages.Find(id) is not ArtistImage a || !a.Showable).ToList();
                report.Number("bubble_photos_real_songs", photos.Mapped, "0");
                report.Number("bubble_photos_real_photos", photos.PhotosUsed, "0");
                report.Check("photos (real artists.json): only showable photos are used, every one decodes onto its bubbles",
                    bad.Count == 0 && photos.Loaded == photos.PhotosUsed && loader.Nodes.Where(n => n.PhotoRenderer != null).All(n => n.PhotoRenderer!.enabled),
                    $"{photos.Status}; {bad.Count} not showable; {photos.Loaded}/{photos.PhotosUsed} loaded; problems: {string.Join(" | ", loader.ArtistImages.Problems.Take(3))}");
                HashSet<string> works = new(loader.Nodes.Select(n => n.Song.WorkId));
                bool inGraph = loader.ArtistImages.Images.Values.Any(a => a.Showable && a.WorkIds.Any(works.Contains));
                report.Check("photos (real): every song a showable photo lists (work_ids) in this graph shows it", !inGraph ||
                    loader.ArtistImages.Images.Values.Where(a => a.Showable).All(a => a.WorkIds.Where(works.Contains).All(w =>
                        loader.Nodes.First(n => n.Song.WorkId == w).PhotoRenderer != null)), photos.Status);
            }
        }

        /// <summary>The colour of one screen pixel of a fresh capture.</summary>
        /// <summary>The colour of a capture's pixel under <paramref name="world"/> (in the capture's own pixels, not the Game view's).</summary>
        static Color Pixel(Camera cam, SongGraphLoader loader, int width, int height, Vector3 world)
        {
            Color[] pixels = CapturePixels(cam, loader, width, height);
            cam.aspect = (float)width / height;
            Vector3 v = cam.WorldToViewportPoint(world);
            int x = Mathf.Clamp(Mathf.FloorToInt(v.x * width), 0, width - 1), y = Mathf.Clamp(Mathf.FloorToInt(v.y * height), 0, height - 1);
            return pixels[y * width + x];
        }
    }
}
