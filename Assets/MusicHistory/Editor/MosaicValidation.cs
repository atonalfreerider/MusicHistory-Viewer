#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using MusicHistory.Walkthrough;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Melody mosaics (DESIGN.md §17) in edit mode (no audio: the mix keeps time on its main-thread
    /// clock). MusicHistory › Run Mosaic Validation, or <c>-executeMethod
    /// MusicHistory.EditorTools.MosaicValidation.Run</c> (exits with 0 when every check passes). Writes
    /// data/screens/mosaic_validation.json and the screenshots mosaic_landscape.png and
    /// mosaic_portrait.png (both in the mosaic section).
    ///
    /// <list type="bullet">
    /// <item>Pure: the mosaics.json v1 contract on an inline mosaic (sections, loops, beats, chords,
    /// notes, pieces, harmonies, songs and their colours), the clock (mix beat, loops, sections, piece
    /// lookup), the notes as lines, the words ("3 semitones down · 84% speed · 83% notes match"),
    /// contract violations, malformed and missing files; the real data/audio/mosaics/mosaics.json
    /// (every mix there, every piece's match a rate with its notes heard).</item>
    /// <item>Scene, for a synthetic catalog built from the graph's songs (always), the real
    /// mosaics.json (when written) and -validationMosaics &lt;file&gt;: O opens the mosaics list (target,
    /// number of songs), the selection lights its songs; Enter plays it (no narration); the whole
    /// mix on the clock: sections and loops in order, finished once; the songs sounding glow at 3x
    /// (the target, the playing piece's song, the harmony voices); the melody graph panning under the
    /// centre playhead (±0.5 px), one bright line per voice sounding with its light on it, the pieces'
    /// spans in their songs' colours with the playing one lit around the playhead, the harmony lines;
    /// the wheel on the target loop's chords with the chord sounding lit; the name top left; the
    /// strip ("Mosaic: X rebuilt from N songs", LOOP k / n, the section, what is heard, coverage,
    /// match); Next / Back, the section chips, Space, M, C and K ignored, Esc; portrait 1080x1920.</item>
    /// </list>
    /// </summary>
    public static class MosaicValidation
    {
        [MenuItem("MusicHistory/Run Mosaic Validation (writes data/screens)")]
        public static void RunFromMenu() => Validation.ExecuteMosaics(exitWhenDone: false);

        public static void Run() => Validation.ExecuteMosaics(exitWhenDone: true);
    }

    public static partial class Validation
    {
        internal static void ExecuteMosaics(bool exitWhenDone)
        {
            Report report = new();
            string outDir = Arg("-validationOut") ?? Path.Combine(SongGraphLoader.RepoRoot(), "data", "screens");
            Directory.CreateDirectory(outDir);
            Vector2Int? screenBefore = ViewerLayout.ScreenOverride;
            try
            {
                ValidateMosaicsPure(report);
                ValidateRealMosaicsFile(report);
                int width = int.TryParse(Arg("-validationWidth"), out int w) ? w : 1920;
                int height = int.TryParse(Arg("-validationHeight"), out int h) ? h : 1080;
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                SongGraphLoader? loader = Object.FindAnyObjectByType<SongGraphLoader>();
                Camera? cam = Camera.main;
                if (report.Check("mosaics: scene has SongGraphLoader and a camera", loader != null && cam != null))
                {
                    cam!.aspect = (float)width / height;
                    string graphDir = Path.Combine(SongGraphLoader.RepoRoot(), "data", "graph");
                    string dbPath = PathArg("-validationLineageDb") ?? FirstExisting(Path.Combine(graphDir, "music_graph.db"), Path.Combine(graphDir, "demo_graph.db")) ?? "";
                    if (report.Check("mosaics: graph database exists", File.Exists(dbPath), dbPath))
                    {
                        loader!.Build(dbPath);
                        ValidateMosaicScenes(report, loader, cam, outDir, width, height);
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
            WriteJson(report, Path.Combine(outDir, "mosaic_validation.json"));
            Debug.Log($"[validation] {report.Checks.Count - report.Failures}/{report.Checks.Count} checks passed; report in {outDir}");
            if (exitWhenDone) EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }

        /// <summary>Rich text without its tags (noparse, colour, size, bold).</summary>
        static string Plain(string rich) => Regex.Replace(rich, "<[^>]*>", "");

        // ------------------------------------------------------------------ pure: the contract, the clock, the words

        /// <summary>
        /// A mosaic at 80 BPM (a beat = 0.75 s), 4-bar loops (16 beats, 12 s): 1 original (an intro), 4 mosaic
        /// and 2 harmony loops (84 s; another loop would not fit in 90 s). The target sings 13 notes with two
        /// rests; three pieces cover [0, 4), [4, 9) and [10, 16) (the beat [9, 10) uncovered): the first
        /// all 4 notes (3 semitones up, 84% speed), the second 3 of 4 (2 down, 112%), the third 4 of 5
        /// (its own extra note; same key, original speed). Two harmony voices: a third above, a fourth below.
        /// <paramref name="works"/>: target, three sources, two harmony voices.
        /// </summary>
        public static string MosaicFixtureJson(string id = "fixture-mosaic", string[]? works = null, string[]? titles = null, int[]? years = null)
        {
            works ??= new[] { "Q10", "Q11", "Q12", "Q13", "Q14", "Q15" };
            titles ??= new[] { "Target Song", "Fireflies", "Second Source", "Third Source", "Upper Voice", "Lower Voice" };
            years ??= new[] { 1990, 2009, 1965, 1978, 1983, 2001 };
            string Song(int i) => $"\"work_id\": {Q(works[i])}, \"title\": {Q(titles[i])}, \"artist\": \"Artist {i + 1}\", \"year\": {years[i]}";
            string Notes(IEnumerable<(double a, double b, double p)> notes) => "[" + string.Join(", ", notes.Select(n => $"[{Inv(n.a)}, {Inv(n.b)}, {Inv(n.p)}]")) + "]";
            (double a, double b, double p)[] target =
            {
                (0, 1, 64), (1, 2, 65), (2, 3, 67), (3, 4, 65), (4, 5, 64), (5, 6, 62),
                (7, 8, 60), (8, 9, 62), (9, 10, 64), (10, 11, 65), (11, 12, 67), (12, 13, 69), (13, 14, 67)
            };
            StringBuilder b = new();
            b.Append("{\"version\": 1, \"generated_at\": \"2026-10-01T12:00:00Z\", \"frame\": \"C major / A minor (relative normalization, as the pipeline)\", \"mosaics\": [{");
            b.Append($"\"id\": {Q(id)}, \"name\": \"Fixture, Reassembled\", \"title\": {Q(titles[0] + " rebuilt from 3 songs")}, \"target\": {{{Song(0)}}}, ");
            b.Append($"\"file\": {Q(id + "/mix.mp3")}, \"seconds\": 84, \"key\": \"D major\", \"bpm\": 80, \"beats_per_bar\": 4, \"loop_beats\": 16, ");
            b.Append("\"sections\": [{\"start\": 0, \"end\": 12, \"kind\": \"original\", \"loops\": 1}, {\"start\": 12, \"end\": 60, \"kind\": \"mosaic\", \"loops\": 4}, " +
                     "{\"start\": 60, \"end\": 84, \"kind\": \"harmony\", \"loops\": 2}], ");
            b.Append("\"beats\": [").Append(string.Join(", ", Enumerable.Range(0, 112).Select(i => $"[{Inv(i * .75)}, {i % 16}]"))).Append("], ");
            b.Append("\"chords\": [[0, 4, 0, \"maj\", \"I\"], [4, 8, 9, \"min\", \"vi\"], [8, 12, 5, \"maj\", \"IV\"], [12, 16, 7, \"maj\", \"V\"]], ");
            b.Append("\"notes\": ").Append(Notes(target)).Append(", \"pieces\": [");
            b.Append($"{{{Song(1)}, \"start\": 0, \"end\": 4, \"source_start\": 12.5, \"source_end\": 15.1, \"shift_semitones\": 3, \"tempo_ratio\": 0.84, \"match\": 1, " +
                     $"\"notes\": {Notes(target.Take(4))}}}, ");
            b.Append($"{{{Song(2)}, \"start\": 4, \"end\": 9, \"source_start\": 3, \"source_end\": 7.2, \"shift_semitones\": -2, \"tempo_ratio\": 1.12, \"match\": 0.75, " +
                     $"\"notes\": {Notes(new[] { (4.0, 5.0, 64.0), (5.0, 6.0, 62.0), (7.0, 8.0, 61.0), (8.0, 9.0, 62.0) })}}}, ");
            b.Append($"{{{Song(3)}, \"start\": 10, \"end\": 16, \"source_start\": 20, \"source_end\": 24, \"shift_semitones\": 0, \"tempo_ratio\": 1.0, \"match\": 0.8, " +
                     $"\"notes\": {Notes(new[] { (10.0, 11.0, 65.0), (11.0, 12.0, 67.0), (12.0, 12.5, 69.0), (12.5, 13.0, 70.0), (13.0, 14.0, 67.0) })}}}], ");
            b.Append("\"harmonies\": [");
            b.Append($"{{{Song(4)}, \"shift_semitones\": 5, \"tempo_ratio\": 0.75, \"consonance\": 0.9, \"notes\": {Notes(target.Select(n => (n.a, n.b, n.p + 4)))}}}, ");
            b.Append($"{{{Song(5)}, \"shift_semitones\": -3, \"tempo_ratio\": 1.25, \"consonance\": 0.8, \"notes\": {Notes(target.Select(n => (n.a, n.b, n.p - 5)))}}}], ");
            b.Append("\"coverage\": 0.9231, \"match\": 0.8462}]}");
            return b.ToString();
        }

        static void ValidateMosaicsPure(Report report)
        {
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-validation-mosaics");
            MosaicCatalog c = MosaicCatalog.Parse(MosaicFixtureJson(), dir, "inline");
            Mosaic? m = c.Mosaics.FirstOrDefault();
            report.Check("mosaics.json v1 parses: name, target, key, tempo, loop, sections (original, mosaic, harmony), beats, chords, notes, pieces, harmonies",
                c.Loaded && c.Version == 1 && m != null && m.Id == "fixture-mosaic" && m.Name == "Fixture, Reassembled" && m.Target.WorkId == "Q10" &&
                m.Target.Title == "Target Song" && m.Target.Year == 1990 && m.Seconds == 84 && m.Key == "D major" && m.Bpm == 80 && m.BeatsPerBar == 4 &&
                m.LoopBeats == 16 && m.Sections.Count == 3 && m.Sections[1].Kind == MosaicSectionKind.Mosaic && m.Sections[2].FirstLoop == 5 && m.Loops == 7 &&
                m.BeatTimes.Length == 112 && m.Chords.Count == 4 && m.Chords[1].Roman == "vi" && m.Notes.Count == 13 && m.Pieces.Count == 3 &&
                m.Pieces[1].ShiftSemitones == -2 && m.Pieces[1].TempoRatio == 1.12 && m.Harmonies.Count == 2 && m.Harmonies[0].Consonance == .9 && !m.FileExists,
                $"{c.Status}; {string.Join(" | ", c.Problems)}");
            report.Check("mosaics.json: a missing mix is listed as a problem, not thrown (and it is the only one)",
                c.Problems.Count == 1 && c.Problems[0].Contains("mix missing"), string.Join(" | ", c.Problems));
            if (m == null) return;
            report.Check("mosaic songs: the target first, then the pieces' songs, then the harmony voices; one colour slot each (distinct colours)",
                m.Songs.Count == 6 && m.Songs[0].IsTarget && m.Songs.Skip(1).Take(3).All(s => s.IsSource) && m.Songs.Skip(4).All(s => s.IsHarmony) &&
                m.Pieces.Select(p => p.Song).SequenceEqual(new[] { 1, 2, 3 }) && m.Harmonies.Select(h => h.Song).SequenceEqual(new[] { 4, 5 }) && m.SourceCount == 3 &&
                m.Songs.Select(s => MelodyGraphPanel.MosaicColor(s.ColorSlot)).Distinct().Count() == 6,
                string.Join(", ", m.Songs.Select(s => $"{s.Index}:{s.Title}")));

            // The clock.
            bool beats = Math.Abs(m.BeatAt(3.75) - 5) < 1e-9 && Math.Abs(m.BeatAt(4.125) - 5.5) < 1e-9 && Math.Abs(m.BeatAt(84) - 112) < 1e-9 &&
                         Math.Abs(m.TimeAtBeat(32) - 24) < 1e-9 && Math.Abs(m.TimeAtBeat(5.5) - 4.125) < 1e-9;
            bool loops = m.LoopAt(23.99) == 1 && m.LoopAt(24) == 2 && m.LoopAt(84) == 6 && Math.Abs(m.LoopBeatAt(24 + 2.25) - 3) < 1e-9 &&
                         Math.Abs(m.LoopStartSeconds(2) - 24) < 1e-9 && Math.Abs(m.LoopStartSeconds(7) - 84) < 1e-9 && m.LoopStartSeconds(0) == 0;
            bool sections = m.SectionIndexAt(11.99) == 0 && m.SectionIndexAt(12) == 1 && m.SectionIndexAt(83.9) == 2 && m.SectionOfLoop(0) == 0 && m.SectionOfLoop(1) == 1 &&
                            m.SectionOfLoop(4) == 1 && m.SectionOfLoop(5) == 2 && m.KindOfLoop(3) == MosaicSectionKind.Mosaic && m.KindOfLoop(6) == MosaicSectionKind.Harmony;
            report.Check("mosaic clock: the mix beat (exact on the beats, between them, to the end), loops (a boundary belongs to the next), loop starts, sections by time and by loop",
                beats && loops && sections, $"beat {m.BeatAt(84):0.###} at the end, loop {m.LoopAt(24)} at 24 s");
            bool pieces = m.PieceIndexAt(3.99) == 0 && m.PieceIndexAt(4) == 1 && m.PieceIndexAt(9.5) == -1 && m.PieceIndexAt(10) == 2 && m.PieceAtOrBefore(9.5) == 1 &&
                          m.Pieces[0].TargetNotes == 4 && m.Pieces[1].TargetNotes == 4 && m.Pieces[2].TargetNotes == 4 && m.Pieces[2].NotesCompared == 5 &&
                          m.Pieces[0].MatchedNotes == 4 && m.Pieces[1].MatchedNotes == 3 && m.Pieces[2].MatchedNotes == 4;
            report.Check("mosaic pieces: the piece at a loop beat (a boundary to the next, -1 in a gap), the target's notes in each span, notes matched of those compared",
                pieces, string.Join(", ", m.Pieces.Select(p => $"{p.MatchedNotes}/{p.NotesCompared}")));
            bool notes = Mosaic.NoteAt(m.Notes, 1.5, out float n1, out bool v1) && v1 && n1 == 65 &&
                         Mosaic.NoteAt(m.Notes, 6.5, out float n2, out bool v2) && !v2 && n2 == 62 &&
                         Mosaic.NoteAt(m.Notes, 15.5, out float n3, out _, 2.0) && n3 == 67 && !Mosaic.NoteAt(m.Notes, 15.5, out _, out _, 1.0);
            report.Check("mosaic notes: the pitch sung at a beat, in a rest the nearest note (the one before on a tie), nothing beyond reach", notes);

            // The notes as lines.
            List<MelodyScroll.Piece> line = MosaicTimeline.Line(m.Notes, 32);
            bool steps = line.Count == 2 && line[0].Count == 12 && Math.Abs(line[0].First - 32) < 1e-9 && Math.Abs(line[1].First - 39) < 1e-9 &&
                         MelodyScroll.LineAt(line, 33.5, 0, out float l1) && l1 == 65 && MelodyScroll.LineAt(line, 32.5, 0, out float l2) && l2 == 64 &&
                         !MelodyScroll.LineAt(line, 38.5, 0, out _);
            List<MosaicTimeline.LineSpec> specs = MosaicTimeline.Lines(m);
            MosaicTimeline.State inPiece = MosaicTimeline.At(m, m.TimeAtBeat(3 * 16 + 5)), inGap = MosaicTimeline.At(m, m.TimeAtBeat(3 * 16 + 9.5)),
                                 inHarmony = MosaicTimeline.At(m, m.TimeAtBeat(5 * 16 + 2)), inOriginal = MosaicTimeline.At(m, m.TimeAtBeat(0 * 16 + 2));
            string Lit(MosaicTimeline.State s) => string.Join(",", specs.Where(x => MosaicTimeline.Playing(x, s)).Select(x => $"{x.Kind}{x.Loop}{(x.Piece >= 0 ? "p" + x.Piece : x.Harmony >= 0 ? "h" + x.Harmony : "")}"));
            bool playing = Lit(inPiece) == "Piece3p1" && Lit(inGap) == "" && inGap.FocusPiece == 1 && inGap.Piece == -1 && Lit(inHarmony) == "Target5,Harmony5h0,Harmony5h1" &&
                           Lit(inOriginal) == "Target0" && specs.Count(x => x.Guide) == 4;
            report.Check("mosaic lines: notes as steps (joined when they follow, broken at a rest); a line per voice per loop; only the voices sounding in the loop playing are bright, the target a guide in the mosaic loops",
                steps && specs.Count == 7 + 12 + 4 && playing, $"{line.Count} pieces; {specs.Count} lines; lit: piece [{Lit(inPiece)}] gap [{Lit(inGap)}] harmony [{Lit(inHarmony)}] original [{Lit(inOriginal)}]");

            // The words.
            bool words = Mosaic.ShiftText(-3) == "3 semitones down" && Mosaic.ShiftText(1) == "1 semitone up" && Mosaic.ShiftText(0) == "same key" &&
                         Mosaic.SpeedText(.84) == "84% speed" && Mosaic.SpeedText(1.004) == "original speed" &&
                         Mosaic.PieceCaption(m.Pieces[0]) == $"Fireflies, 2009 · 3 semitones up · 84% speed · {MosaicCatalog.Percent(m.Pieces[0].Match)} notes match" &&
                         Mosaic.PieceCaption(m.Pieces[2]) == $"Third Source, 1978 · same key · original speed · {MosaicCatalog.Percent(m.Pieces[2].Match)} notes match" &&
                         Mosaic.HarmonyCaption(m.Harmonies[1]) == "Lower Voice, 2001 · 3 semitones down · 125% speed · 80% consonant" &&
                         m.StripTitle == "Mosaic: Target Song rebuilt from 3 songs";
            report.Check("mosaic words: 'Fireflies, 2009 · 3 semitones up · 84% speed · 100% notes match', the harmony voices, 'Mosaic: X rebuilt from N songs'",
                words, $"{Mosaic.PieceCaption(m.Pieces[0])} | {Mosaic.HarmonyCaption(m.Harmonies[1])} | {m.StripTitle}");
            report.Check("mosaic binding: a target in the graph makes it playable; one that is not says why",
                c.Bind(w => w == "Q10" ? 7 : (int?)null) == 1 && m.IsPlayable && m.Target.NodeId == 7 && m.Songs[1].NodeId == 0 &&
                c.Bind(w => null) == 0 && !m.IsPlayable && MosaicCatalog.WhyNotPlayable(m).Contains("Q10"), MosaicCatalog.WhyNotPlayable(m));

            MosaicCatalog broken = MosaicCatalog.Parse("{\"version\": 2, \"generated_at\": \"yesterday\", \"frame\": \"D major\", \"mosaics\": [ {\"id\": \"Bad Id\", " +
                "\"name\": \"lower case name that is far too long to be a name\", \"title\": \"\", \"target\": {\"work_id\": \"Q1\", \"title\": \"T\", \"artist\": \"A\", \"year\": 1990}, " +
                "\"file\": \"x/mix.mp3\", \"seconds\": 95, \"key\": \"H major\", \"bpm\": 10, \"beats_per_bar\": 4, \"loop_beats\": 10, " +
                "\"sections\": [{\"start\": 0, \"end\": 10, \"kind\": \"mosaic\", \"loops\": 1}, {\"start\": 11, \"end\": 20, \"kind\": \"original\", \"loops\": 0}], " +
                "\"beats\": [[0, 0], [0.5, 2]], \"chords\": [[0, 4, 0, \"major\", \"I\"]], \"notes\": [[0, 1, 140]], " +
                "\"pieces\": [{\"work_id\": \"Q1\", \"title\": \"T\", \"artist\": \"A\", \"year\": 1990, \"start\": 2, \"end\": 1, \"source_start\": 3, \"source_end\": 2, " +
                "\"shift_semitones\": 9, \"tempo_ratio\": 2, \"match\": 1.5, \"notes\": []}], \"harmonies\": [], \"coverage\": 0.5, \"match\": 0.9, \"lyrics\": \"la\"} ] }", "");
            string[] wanted = { "version 2", "generated_at", "frame", "extra [lyrics]", "not a slug", "Title Case", "no title", "must be 'Bad Id/mix.mp3'", "seconds 95",
                                "key 'H major'", "bpm 10", "loop_beats 10", "must be original, mosaic, harmony", "starts at 11", "loops must be a positive int",
                                "out of order or range", "chords[0] is not", "notes[0]", "another song than the target", "start/end", "source_start",
                                "shift_semitones 9", "tempo_ratio 2", "match 1.5", "0 harmonies", "exceeds coverage" };
            List<string> missing = wanted.Where(x => !broken.Problems.Any(p => p.Contains(x))).ToList();
            report.Check("mosaics.json: contract violations are reported (keys, version, frame, id, name, file, length, key, tempo, loop, sections, beats, chords, notes, pieces, harmonies, coverage)",
                missing.Count == 0, $"{string.Join(" | ", missing)} missing; {string.Join(" | ", broken.Problems)}");
            MosaicCatalog junk = MosaicCatalog.Parse("{ \"version\": 1, \"mosaics\": [ ", "");
            report.Check("mosaics.json: malformed JSON gives an empty catalog, no exception", !junk.Loaded && junk.Mosaics.Count == 0 && junk.Problems.Count == 1, junk.Status);
            MosaicCatalog none = MosaicCatalog.Load(Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", MosaicCatalog.FileName));
            report.Check("mosaics.json: a missing file gives an empty catalog that says so", !none.Loaded && none.Mosaics.Count == 0 && none.Status.StartsWith("missing"), none.Status);
        }

        /// <summary>The pipeline's data/audio/mosaics/mosaics.json, when written: no problems, every mix there, the clock over every beat.</summary>
        static void ValidateRealMosaicsFile(Report report)
        {
            string real = MosaicCatalog.DefaultPath();
            if (!File.Exists(real))
            {
                report.Text("mosaics_real_file", $"skipped: {real} not written yet");
                return;
            }
            MosaicCatalog c = MosaicCatalog.Load(real);
            report.Number("mosaics_real_count", c.Mosaics.Count, "0");
            report.Check("mosaics.json (real): loads with no contract problems, every mix.mp3 there", c.Loaded && c.Mosaics.Count > 0 && c.Problems.Count == 0 && c.Mosaics.All(m => m.FileExists),
                $"{c.Status}; {c.Problems.Count} problems: {string.Join(" | ", c.Problems.Take(5))}");
            List<string> bad = new();
            foreach (Mosaic m in c.Mosaics)
            {
                if (Math.Abs(m.BeatAt(m.Seconds) - m.TotalBeats) > .05) bad.Add($"{m.Id}: the mix beat ends at {m.BeatAt(m.Seconds):0.##}, not {m.TotalBeats}");
                for (int k = 0; k < m.Loops; k++)
                    if (m.SectionOfLoop(k) != m.SectionIndexAt(m.LoopStartSeconds(k) + 1e-3)) bad.Add($"{m.Id}: loop {k + 1} starts in section {m.SectionIndexAt(m.LoopStartSeconds(k))}");
                foreach (MosaicPiece p in m.Pieces)
                    if (!(p.Match > 0 && p.Match <= 1) || p.Notes.Count == 0) bad.Add($"{m.Id} '{p.Title}': match {p.Match}, {p.Notes.Count} notes");
                if (!(m.Coverage > 0 && m.Coverage <= 1)) bad.Add($"{m.Id}: coverage {m.Coverage}");
                if (m.Songs.Count > MelodyGraphPanel.MosaicColorCount + 1) bad.Add($"{m.Id}: {m.Songs.Count} songs, more than the colours");
            }
            report.Check("mosaics.json (real): the mix beat ends with the last loop, every loop starts in its section, every piece's match a rate with notes heard, the coverage a share, a colour per song",
                bad.Count == 0, string.Join(" | ", bad.Take(5)));
        }

        // ------------------------------------------------------------------ scene

        /// <summary>A mosaics.json from the graph's songs (the target and five others; no mix file: the clock plays). Returns its path.</summary>
        static string WriteSyntheticMosaics(SongGraphData data)
        {
            List<SongRecord> songs = data.Songs.Where(s => !string.IsNullOrEmpty(s.WorkId) && s.Year > 0)
                .OrderByDescending(s => s.Descendants).ThenBy(s => s.NodeId).Take(6).ToList();
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-mosaic-fixture");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, MosaicCatalog.FileName);
            string json = songs.Count == 6
                ? MosaicFixtureJson("synthetic-mosaic", songs.Select(s => s.WorkId).ToArray(), songs.Select(s => s.Title).ToArray(), songs.Select(s => s.Year).ToArray())
                : "{\"version\": 1, \"generated_at\": \"2026-10-01T12:00:00Z\", \"frame\": \"C major / A minor\", \"mosaics\": []}";
            File.WriteAllText(file, json, new UTF8Encoding(false));
            return file;
        }

        static void ValidateMosaicScenes(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            string db = loader.ResolvedDbPath;
            List<(string label, Func<MosaicCatalog> load)> catalogs = new();
            string real = MosaicCatalog.DefaultPath();
            if (File.Exists(real)) catalogs.Add(("real", () => MosaicCatalog.Load(real)));
            else report.Text("mosaics_real", $"skipped: {real} not written yet");
            string? fixture = PathArg("-validationMosaics");
            if (fixture != null && report.Check("mosaics: -validationMosaics file exists", File.Exists(fixture), fixture))
                catalogs.Add(("fixture", () => MosaicCatalog.Load(fixture)));
            string synthetic = WriteSyntheticMosaics(loader.Data!);
            catalogs.Add(("synthetic", () => MosaicCatalog.Load(synthetic)));
            report.Text("mosaics_catalogs", string.Join(", ", catalogs.Select(x => x.label)));
            for (int i = 0; i < catalogs.Count; i++)
            {
                (string label, Func<MosaicCatalog> load) = catalogs[i];
                loader.Build(db);
                ValidateMosaicCatalog(report, loader, cam, load(), label, outDir, width, height,
                    canonicalShots: catalogs.Exists(x => x.label == "real") ? label == "real" : i == catalogs.Count - 1);
            }

            // No mosaics.json: O opens a list that says so; Enter plays nothing; P still opens the paths.
            loader.Build(db);
            loader.UseMosaics(MosaicCatalog.Load(Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", MosaicCatalog.FileName)), "validation: missing file");
            FeaturedPathsPanel panel = loader.Paths!;
            bool opened = panel.HandleKey(Key.O) && panel.IsOpen && panel.Tab == FeaturedPathsPanel.ListTab.Mosaics && panel.ListText.Contains("No melody mosaics yet");
            bool nothing = panel.HandleKey(Key.Enter) && !loader.Director.IsTouring;
            bool back = panel.HandleKey(Key.O) && panel.Tab == FeaturedPathsPanel.ListTab.Paths && panel.IsOpen;
            panel.HandleKey(Key.Escape);
            bool paths = panel.HandleKey(Key.P) && panel.IsOpen && panel.Tab == FeaturedPathsPanel.ListTab.Paths;
            panel.HandleKey(Key.Escape);
            report.Check("mosaics: without mosaics.json O opens a mosaics list that says so, Enter plays nothing, O switches back to the paths, P opens the paths",
                opened && nothing && back && paths && panel.State == FeaturedPathsPanel.PanelState.Closed, panel.ListText.Replace("\n", " / "));
        }

        static void TickMosaic(SongGraphLoader loader, float dt)
        {
            WalkthroughDirector d = loader.Director;
            if (d.MosaicAudio != null) d.MosaicAudio.Advance(dt);
            d.Tick(dt);
            if (loader.Layout != null) loader.Layout.ApplyNow();
            if (loader.MelodyGraph != null) loader.MelodyGraph.Refresh();
            // Edit mode runs no LateUpdate: the wheel and the name follow the mix here.
            loader.Hud.RefreshRing();
            loader.Hud.RefreshPathTitle();
        }

        static void ValidateMosaicCatalog(Report report, SongGraphLoader loader, Camera cam, MosaicCatalog catalog, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            string L = $"mosaics[{label}]";
            bool synthetic = label == "synthetic";
            loader.UseMosaics(catalog, "validation");
            report.Text($"mosaics_{label}_file", catalog.SourcePath);
            report.Number($"mosaics_{label}_count", catalog.Mosaics.Count, "0");
            report.Check($"{L}: loads (version 1, at least one mosaic)", catalog.Loaded && catalog.Version == 1 && catalog.Mosaics.Count > 0, catalog.Status);
            if (catalog.Mosaics.Count == 0) return;
            List<string> contract = catalog.Problems.Where(x => !synthetic || !x.Contains("mix missing")).ToList();
            report.Check($"{L}: no contract problems (sections, loops, beats, chords, notes, pieces, harmonies, files)", contract.Count == 0,
                $"{contract.Count}: {string.Join(" | ", contract.Take(5))}");
            List<string> unbound = catalog.Mosaics.Where(x => !x.IsPlayable).Select(x => $"{x.Id}: {MosaicCatalog.WhyNotPlayable(x)}").ToList();
            report.Check($"{L}: every mosaic's target is in the graph", unbound.Count == 0, string.Join(" | ", unbound.Take(4)));
            List<string> outside = catalog.Mosaics.SelectMany(x => x.Songs.Where(s => s.NodeId <= 0).Select(s => $"{x.Id}: {s.WorkId}")).ToList();
            report.Text($"mosaics_{label}_songs_not_in_graph", outside.Count == 0 ? "none" : string.Join(", ", outside.Take(8)));
            Mosaic? m = catalog.Mosaics.Where(x => x.IsPlayable).OrderByDescending(x => x.Pieces.Count).ThenBy(x => x.Index).FirstOrDefault();
            if (m == null) return;
            report.Text($"mosaics_{label}_tour", $"{m.Id} ({m.Pieces.Count} pieces from {m.SourceCount} songs, {m.Harmonies.Count} harmonies, {m.Loops} loops, {m.Duration:0.0} s)");
            ValidateMosaicTour(report, loader, cam, m, L, label, outDir, width, height, canonicalShots);
        }

        static void ValidateMosaicTour(Report report, SongGraphLoader loader, Camera cam, Mosaic m, string L, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            MosaicPlayer player = d.MosaicAudio!;
            MelodyGraphPanel graph = loader.MelodyGraph!;
            GraphHud hud = loader.Hud;
            string Shot(string name) => Path.Combine(outDir, canonicalShots ? $"{name}.png" : $"{name}_{label}.png");
            int finished = 0;
            void OnFinished(Mosaic x) => finished++;
            player.MixFinished += OnFinished;
            float fly = d.FlyDuration, grow = d.EdgeGrowDuration;
            SongNode? NodeOf(int song) => song >= 0 && song < m.Songs.Count && m.Songs[song].NodeId > 0 ? loader.NodeById(m.Songs[song].NodeId) : null;
            SongNode target = loader.NodeById(m.Target.NodeId);
            double lb = m.LoopBeats;
            try
            {
                ViewerLayout.ScreenOverride = new Vector2Int(width, height);
                loader.Layout?.ApplyNow();
                loader.Highlighter.ApplyFocus(null, force: true);
                loader.FrameOverview(cam);

                // The list.
                bool opened = panel.HandleKey(Key.O) && panel.IsOpen && panel.Tab == FeaturedPathsPanel.ListTab.Mosaics && HudRefreshed(hud) && !hud.LegendVisible;
                int index = panel.Mosaics.ToList().IndexOf(m);
                panel.SelectMosaic(index);
                MosaicRowView? row = panel.MosaicRows.FirstOrDefault(r => r.MosaicIndex == index);
                string text = panel.ListText;
                bool rowOk = row != null && row.gameObject.activeSelf && Plain(row.Subtitle.text).Contains(m.Target.Title) &&
                             row.Count.text == $"{m.SourceCount} song{(m.SourceCount == 1 ? "" : "s")}" && row.Blocks.Count == m.Pieces.Count &&
                             text.Contains(GraphHud.Esc(m.Name)) && text.Contains(PathCatalog.Clock(m.Duration));
                report.Check($"{L}: O opens the mosaics list: each row the name, length, target song and number of source songs, its pieces as coloured blocks",
                    opened && rowOk, row != null ? $"{Plain(row.Title.text)} | {Plain(row.Subtitle.text)} | {row.Count.text} | {PathCatalog.Clock(m.Duration)}; {row.Blocks.Count} blocks" : "no row");
                GraphRoute route = loader.RouteFor(m);
                HashSet<SongNode> members = new(route.Nodes);
                report.Check($"{L}: the selected mosaic's songs light up on the graph, the rest dims",
                    loader.Highlighter.FocusRoute == route && route.Nodes.Contains(target) && loader.Nodes.All(n => members.Contains(n) ? n.State == BubbleState.Focus : n.State == BubbleState.Dimmed));
                bool tabs = panel.HandleKey(Key.O) && panel.Tab == FeaturedPathsPanel.ListTab.Paths && panel.ListText.Length >= 0 && panel.HandleKey(Key.O) &&
                            panel.Tab == FeaturedPathsPanel.ListTab.Mosaics && panel.TabButton != null && panel.TabButton.gameObject.activeInHierarchy;
                report.Check($"{L}: O (and the header's switch) toggles the open list between the mosaics and the paths", tabs);

                // Enter plays it.
                panel.SelectMosaic(index);
                bool started = panel.HandleKey(Key.Enter) && d.IsTouring && d.Mode == TourMode.Mosaic && d.CurrentMosaic == m && ReferenceEquals(d.ActivePlayer, player) &&
                               player.Current == m && panel.State == FeaturedPathsPanel.PanelState.Playing && panel.StripVisible && !panel.ListVisible &&
                               d.CurrentMashup == null && d.CurrentDuet == null && !panel.NarrationAvailable && (loader.Narration == null || loader.Narration.CurrentPath == null);
                report.Check($"{L}: Enter plays the mosaic's mix on the mosaic player, no narration; the list becomes the now-playing strip",
                    started, $"{d.ActivePlayerName}; mode {d.Mode}; mosaic {d.CurrentMosaic?.Id ?? "none"}");
                TickMosaic(loader, 0f);
                List<MosaicTimeline.LineSpec> specs = MosaicTimeline.Lines(m);
                report.Check($"{L}: the melody graph shows the mosaic: a line per voice and loop, a light per voice, a timeline of the mix's beats that pans (one loop per plot width)",
                    graph.Showing && graph.ShownMosaic == m && graph.Shown == null && graph.ShownDuet == null && graph.Lines.Count == specs.Count &&
                    graph.Dots.Count == 2 + m.Harmonies.Count && graph.Scrolling && !graph.Portrait && Math.Abs(graph.Period - lb) < 1e-9 &&
                    Mathf.Abs(graph.PixelsPerBeat - (float)(MelodyGraphPanel.PlotArea.width / lb)) < 1e-3 && graph.KickerText.Contains("MOSAIC"),
                    $"{graph.Lines.Count} lines of {specs.Count}, {graph.Dots.Count} lights, ppb {graph.PixelsPerBeat:0.###}");

                // The whole mix on the clock.
                d.FlyDuration = .4f;
                d.EdgeGrowDuration = .3f;
                List<string> wrong = new(), bubbles = new(), graphWrong = new(), spanWrong = new(), stripWrong = new(), wheelWrong = new();
                List<int> sectionsVisited = new(), loopsVisited = new();
                double dt = .05, worstHead = 0, worstDot = 0, worstLine = 0, worstStep = 0;
                int dotSamples = 0, lineSamples = 0, litSpanSamples = 0, harmonySamples = 0, gapSamples = 0, ticks = 0;
                HashSet<int> piecesLit = new();
                // The camera: one zoom and one rotation for the whole mix once it has flown in; it only
                // pans, gently, to the songs singing now.
                double? heldDepth = null;
                Vector3 heldFwd = default, prevPos = cam.transform.position;
                double worstZoom = 0, worstTurn = 0, worstSpeed = 0;
                int sectionSamples = 0, pans = 0;
                bool wasFlying = d.Flying, sawFlight = d.Flying;
                double prevBeat = graph.MosaicBeat, prevT = player.CurrentSeconds;
                ChordRingView ring = hud.Ring;
                int guard = 0;
                while (!d.TourComplete && guard++ < 200000)
                {
                    TickMosaic(loader, (float)dt);
                    ticks++;
                    double t = player.CurrentSeconds;
                    MosaicTimeline.State st = MosaicTimeline.At(m, t);
                    if (st.Beat > m.TotalBeats) st = MosaicTimeline.AtBeat(m, m.TotalBeats);
                    {
                        Vector3 pos = cam.transform.position, fwd = cam.transform.forward;
                        // Depth of the graph plane (the target's) along the view: constant while only panning.
                        double depth = Math.Max(1e-3, Vector3.Dot(target.transform.position - pos, fwd));
                        if (heldDepth is double hd)
                        {
                            worstZoom = Math.Max(worstZoom, Math.Abs(depth - hd) / hd);
                            worstTurn = Math.Max(worstTurn, Vector3.Angle(heldFwd, fwd));
                            worstSpeed = Math.Max(worstSpeed, Vector3.Distance(pos, prevPos) / dt / hd);
                        }
                        else if (sawFlight && !d.Flying)
                        {
                            heldDepth = depth;
                            heldFwd = fwd;
                        }
                        sawFlight |= d.Flying;
                        if (d.Flying && !wasFlying && heldDepth != null) pans++;
                        wasFlying = d.Flying;
                        prevPos = pos;
                        if (st.Kind == MosaicSectionKind.Mosaic) sectionSamples++;
                    }
                    if (sectionsVisited.Count == 0 || sectionsVisited[^1] != st.Section) sectionsVisited.Add(st.Section);
                    if (loopsVisited.Count == 0 || loopsVisited[^1] != st.Loop) loopsVisited.Add(st.Loop);
                    // The director follows the mix.
                    if (d.MosaicLoop != st.Loop || d.MosaicSectionIndex != st.Section || d.MosaicPiece != st.Piece || d.StepIndex != st.Loop)
                        wrong.Add($"t {t:0.00}: loop {d.MosaicLoop}/{st.Loop}, section {d.MosaicSectionIndex}/{st.Section}, piece {d.MosaicPiece}/{st.Piece}");
                    double boundary = m.Sections.Min(s => Math.Abs(t - s.Start));
                    if (boundary > .05 && t < m.Duration - .05 && m.SectionIndexAt(t) != st.Section) wrong.Add($"t {t:0.00}: section by time {m.SectionIndexAt(t)}, by beat {st.Section}");
                    // The songs sounding glow at 3x.
                    List<SongNode> lit = new();
                    SongNode? related = null;
                    if (st.Kind == MosaicSectionKind.Mosaic && st.FocusPiece >= 0 && NodeOf(m.Pieces[st.FocusPiece].Song) is SongNode src && src != target)
                    {
                        lit.Add(src);
                        related = target;
                    }
                    else lit.Add(target);
                    if (st.Kind == MosaicSectionKind.Harmony)
                        foreach (MosaicHarmony h in m.Harmonies)
                            if (NodeOf(h.Song) is SongNode voice && !lit.Contains(voice)) lit.Add(voice);
                    int focused = loader.Nodes.Count(n => n.State == BubbleState.Focus);
                    if (!lit.All(n => n.State == BubbleState.Focus && n.Highlighted) || focused != lit.Count || (related != null && related.State != BubbleState.Related))
                        bubbles.Add($"t {t:0.00} {st.Kind}: lit {string.Join(",", lit.Select(n => $"{n.Song.Title}:{n.State}"))}; {focused} focused; target {target.State}");
                    // The graph: beat, playhead, bright lines, lights on their lines.
                    MosaicTimeline.State gs = graph.MosaicNow;
                    if (Math.Abs(gs.Beat - st.Beat) > 1e-6 || gs.Loop != st.Loop) graphWrong.Add($"t {t:0.00}: graph beat {gs.Beat:0.000} vs {st.Beat:0.000}");
                    if (!d.TourComplete)
                    {
                        double expected = m.BeatAt(t) - m.BeatAt(prevT);
                        worstStep = Math.Max(worstStep, Math.Abs(graph.MosaicBeat - prevBeat - expected));
                    }
                    prevBeat = graph.MosaicBeat;
                    prevT = t;
                    worstHead = Math.Max(worstHead, Math.Abs(graph.PlayheadX - MelodyGraphPanel.ScrollHeadX));
                    worstHead = Math.Max(worstHead, Math.Abs(graph.ScreenPoint(new Vector2(graph.PlayheadX, 0), width, height).x - width * .5f));
                    for (int i = 0; i < graph.Lines.Count; i++)
                    {
                        MelodyGraphPanel.LineView line = graph.Lines[i];
                        if (line.MosaicLine == null) graphWrong.Add($"line {i} is not a mosaic line");
                        else if (line.Playing != MosaicTimeline.Playing(line.MosaicLine, st))
                            graphWrong.Add($"t {t:0.00}: {line.MosaicLine.Kind} loop {line.MosaicLine.Loop} piece {line.MosaicLine.Piece} harmony {line.MosaicLine.Harmony} playing {line.Playing}");
                    }
                    for (int i = 0; i < graph.Dots.Count; i++)
                    {
                        MelodyGraphPanel.DotView dot = graph.Dots[i];
                        int expectedLine = graph.Lines.ToList().FindIndex(x => x.MosaicLine != null && x.Playing && x.MosaicLine.Loop == st.Loop &&
                            (i == 0 ? x.MosaicLine.Kind == MosaicTimeline.Voice.Target : i == 1 ? x.MosaicLine.Kind == MosaicTimeline.Voice.Piece
                                : x.MosaicLine.Kind == MosaicTimeline.Voice.Harmony && x.MosaicLine.Harmony == i - 2));
                        bool expectedOn = expectedLine >= 0 && MosaicTimeline.LightPitch(graph.Lines[expectedLine].MosaicLine!, st.Beat, st.LoopBeat, out _, out _);
                        if (dot.Active != expectedOn || (dot.Active && dot.Line != expectedLine))
                        {
                            graphWrong.Add($"t {t:0.00}: light {i} active {dot.Active} (line {dot.Line}), expected {expectedOn} (line {expectedLine})");
                            continue;
                        }
                        if (!dot.Active) continue;
                        dotSamples++;
                        if (i >= 2) harmonySamples++;
                        worstDot = Math.Max(worstDot, Math.Abs(dot.Position.x - MelodyGraphPanel.ScrollHeadX));
                        if (dot.Voiced)
                        {
                            lineSamples++;
                            worstLine = Math.Max(worstLine, LineGap(graph, graph.Lines[dot.Line], dot.Position));
                        }
                    }
                    // The spans: the song's own colour; the playing piece lit around the playhead, no other.
                    foreach (MelodyGraphPanel.MosaicSpan s in graph.Spans)
                    {
                        if (s.Color != MelodyGraphPanel.MosaicColor(m.Pieces[s.Piece].Song)) spanWrong.Add($"piece {s.Piece} colour");
                        if (m.KindOfLoop(s.Loop) != MosaicSectionKind.Mosaic) spanWrong.Add($"a span in loop {s.Loop + 1}, not a mosaic loop");
                        bool shouldLight = st.Kind == MosaicSectionKind.Mosaic && st.Piece >= 0 && s.Loop == st.Loop && s.Piece == st.Piece;
                        if (s.Lit != shouldLight) spanWrong.Add($"t {t:0.00}: span loop {s.Loop + 1} piece {s.Piece + 1} lit {s.Lit}");
                    }
                    if (st.Kind == MosaicSectionKind.Mosaic && st.Piece >= 0)
                    {
                        List<MelodyGraphPanel.MosaicSpan> on = graph.Spans.Where(s => s.Lit).ToList();
                        float shift = graph.ContentShift;
                        if (on.Count != 1 || on[0].X0 + shift > MelodyGraphPanel.ScrollHeadX + .5f || on[0].X1 + shift < MelodyGraphPanel.ScrollHeadX - .5f)
                            spanWrong.Add($"t {t:0.00}: {on.Count} lit spans{(on.Count == 1 ? $" [{on[0].X0 + shift:0.0}, {on[0].X1 + shift:0.0}] around {MelodyGraphPanel.ScrollHeadX}" : "")}");
                        else litSpanSamples++;
                        piecesLit.Add(st.Piece);
                    }
                    else if (st.Kind == MosaicSectionKind.Mosaic) gapSamples++;
                    // The wheel: the target loop's chords, the one sounding lit.
                    if (ring.SourceKey != m || !ring.Visible) wheelWrong.Add($"t {t:0.00}: wheel shows {ring.SourceKey?.GetType().Name ?? "nothing"}");
                    else
                    {
                        int expectedChord = -1;
                        for (int k = 0; k < ring.Chords.Count; k++)
                            if (st.LoopBeat >= ring.Chords[k].Start && st.LoopBeat < ring.Chords[k].Start + ring.Chords[k].Length) expectedChord = k;
                        if (expectedChord >= 0 && ring.Current != expectedChord && Math.Abs(st.LoopBeat - ring.Chords[expectedChord].Start) > 1e-3)
                            wheelWrong.Add($"t {t:0.00}: wheel chord {ring.Current}, expected {expectedChord}");
                    }
                    // The strip, now and then.
                    if (ticks % 7 == 0 && !d.TourComplete)
                    {
                        panel.RefreshNowPlaying();
                        string strip = Plain(panel.NowPlayingText);
                        List<string> want = new() { m.StripTitle, $"LOOP {st.Loop + 1} / {m.Loops}", $"coverage {MosaicCatalog.Percent(m.Coverage)}", $"match {MosaicCatalog.Percent(m.Match)}",
                                                     "Original", "Mosaic", "Harmony", $"{MosaicSection.KindName(st.Kind).ToUpperInvariant()} SECTION" };
                        if (st.Kind == MosaicSectionKind.Mosaic && st.Piece >= 0) want.Add(Mosaic.PieceCaption(m.Pieces[st.Piece]));
                        if (st.Kind == MosaicSectionKind.Harmony) want.AddRange(m.Harmonies.Select(Mosaic.HarmonyCaption));
                        if (st.Kind == MosaicSectionKind.Original) want.Add(m.Target.Title);
                        List<string> gone = want.Where(x => !strip.Contains(x)).ToList();
                        if (gone.Count > 0) stripWrong.Add($"t {t:0.00}: missing '{string.Join("', '", gone)}' in: {strip.Replace("\n", " / ")}");
                        if (hud.PathTitleText != m.Name || hud.LegendVisible) stripWrong.Add($"t {t:0.00}: title '{hud.PathTitleText}', legend {hud.LegendVisible}");
                    }
                }
                report.Number($"mosaics_{label}_ticks", ticks, "0");
                report.Number($"mosaics_{label}_camera_zoom_change", worstZoom, "0.00000");
                report.Number($"mosaics_{label}_camera_turn_deg", worstTurn, "0.000");
                report.Number($"mosaics_{label}_camera_pan_speed_per_s", worstSpeed, "0.000");
                report.Number($"mosaics_{label}_camera_pans", pans, "0");
                report.Check($"{L}: the camera holds one zoom and one rotation for the whole mix once it has flown in (zoom within 1%, turn under 0.5 degrees): it only pans",
                    heldDepth != null && sectionSamples > 0 && worstZoom < .01 && worstTurn < .5,
                    $"zoom change {worstZoom * 100:0.000}%, turn {worstTurn:0.000} deg, {pans} pans");
                report.Check($"{L}: the pans are gentle (under 0.8 of the held distance per second) and at most one per piece or section change",
                    worstSpeed < .8 && pans <= m.Pieces.Count * m.Loops + m.Sections.Count + m.Harmonies.Count + 2,
                    $"fastest {worstSpeed:0.000} of the distance per second; {pans} pans");
                bool order = sectionsVisited.SequenceEqual(Enumerable.Range(0, m.Sections.Count)) && loopsVisited.SequenceEqual(Enumerable.Range(0, m.Loops));
                report.Check($"{L}: the whole mix on the clock: original → mosaic → harmony, every loop in order, finished once (the tour complete)",
                    order && finished == 1 && d.TourComplete && d.IsTouring && player.Complete,
                    $"sections {string.Join(",", sectionsVisited)}; loops {string.Join(",", loopsVisited)}; finished {finished}");
                report.Check($"{L}: the walkthrough follows the mix (loop, section, piece; the section by time agrees away from the boundaries)",
                    wrong.Count == 0, string.Join(" | ", wrong.Take(3)));
                report.Check($"{L}: the songs sounding glow at 3x: the target (original, harmony), the playing piece's song with the target beside it (mosaic), the harmony voices",
                    bubbles.Count == 0, string.Join(" | ", bubbles.Take(3)));
                report.Check($"{L}: the melody graph follows: the voices sounding in the loop playing bright (the target a faint guide under the pieces), a light on each while it sings",
                    graphWrong.Count == 0 && dotSamples > 0 && harmonySamples > 0, $"{dotSamples} light samples ({harmonySamples} harmony); {string.Join(" | ", graphWrong.Take(3))}");
                report.Check($"{L}: the playhead stays at the screen's centre (±0.5 px), every light on it and on its drawn line (±0.5 px); the timeline runs on continuously",
                    worstHead <= .5 && worstDot <= .5 && worstLine <= .5 && lineSamples > 0 && worstStep < 1e-3,
                    $"playhead {worstHead:0.000} px, lights {worstDot:0.000} px, line {worstLine:0.000} px over {lineSamples} samples, beat step error {worstStep:0.00000}");
                report.Check($"{L}: each piece is a span in its song's colour; the playing one is lit around the playhead, no other (every piece lit in turn)",
                    spanWrong.Count == 0 && litSpanSamples > 0 && piecesLit.Count == m.Pieces.Count, $"{litSpanSamples} lit samples, {gapSamples} gap samples, pieces lit {piecesLit.Count}/{m.Pieces.Count}; {string.Join(" | ", spanWrong.Take(3))}");
                report.Check($"{L}: the chord wheel shows the target loop's chords, the chord sounding under the hand", wheelWrong.Count == 0 && ring.Chords.Count > 0,
                    $"{ring.Chords.Count} chords; {string.Join(" | ", wheelWrong.Take(3))}");
                report.Check($"{L}: the strip reads 'Mosaic: <target> rebuilt from N songs', LOOP k / n, the section, what is heard (the piece's words, the harmony voices), coverage and match; the name top left",
                    stripWrong.Count == 0, string.Join(" | ", stripWrong.Take(2)));
                panel.RefreshNowPlaying();
                report.Check($"{L}: at the end the strip says the mosaic is complete; Space replays it from the top",
                    panel.NowPlayingText.Contains("MOSAIC COMPLETE") && panel.HandleKey(Key.Space) && !d.TourComplete && d.MosaicLoop == 0 && player.CurrentSeconds < .01 && player.IsPlaying);

                // Mid-piece in the mosaic section: the strip, the wheel, the name, the source at 3x, the camera, the picture.
                MosaicSection mosaicSection = m.SectionOf(MosaicSectionKind.Mosaic)!;
                MosaicPiece longest = m.Pieces.OrderByDescending(p => p.Length).First();
                int midLoop = mosaicSection.FirstLoop + Math.Min(1, mosaicSection.Loops - 1);
                d.GoTo(midLoop);
                player.Seek(m.TimeAtBeat(midLoop * lb + (longest.Start + longest.End) * .5));
                player.Paused = true;
                TickMosaic(loader, .05f);
                for (int k = 0; k < 60 && d.Flying; k++) TickMosaic(loader, .05f);
                panel.RefreshNowPlaying();
                string mid = Plain(panel.NowPlayingText);
                SongNode? source = NodeOf(longest.Song);
                report.Check($"{L}: mid-piece the strip reads the piece's words ('{Mosaic.PieceCaption(longest)}'), the walkthrough text the mosaic",
                    d.MosaicPiece == longest.Index && mid.Contains(Mosaic.PieceCaption(longest)) && mid.Contains("MOSAIC SECTION") && d.HudText.Contains(m.StripTitle) && d.HudText.Contains("MOSAIC"),
                    mid.Replace("\n", " / "));
                loader.RefreshView(cam);
                Rect melodyRect = hud.ScreenRect(graph.PanelRect!, width, height);
                List<SongNode> mosaicSongs = m.Songs.Where(s => s.NodeId > 0).Select(s => loader.NodeById(s.NodeId)).Distinct().ToList();
                List<string> offFrame = FramingProblems(cam, d.MosaicShotFramed, melodyRect, hud.ScreenRect(hud.WheelRect, width, height), width, height);
                report.Check($"{L}: the camera frames the singing song (with the target when both fit at the held zoom) on screen, above the melody graph and clear of the wheel",
                    offFrame.Count == 0 && source != null && d.MosaicShotFramed.Contains(source) && mosaicSongs.All(d.MosaicFramed.Contains) && source.Highlighted,
                    $"{d.MosaicShotFramed.Count} framed; {string.Join(" | ", offFrame.Take(4))}");
                CheckLitChords(report, L, loader, cam, width, height, pixels: canonicalShots);
                CheckWheelPlacement(report, L, loader, width, height);
                CheckMosaicTitle(report, L, loader, m, width, height);
                if (source != null) CheckHighlightedBubbles(report, L, loader, cam, new[] { source }, width, height);
                IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
                report.Check($"{L}: the melody graph is on screen and never overlaps the strip, the name, the wheel or the button",
                    NoOverlap(rects) && rects.Contains(melodyRect) && melodyRect.xMin >= 0 && melodyRect.xMax <= width && melodyRect.yMax <= height,
                    string.Join(" ", rects.Select(x => $"[{x.xMin:0},{x.yMin:0} {x.width:0}x{x.height:0}]")));
                string shot = Shot("mosaic_landscape");
                Capture(cam, loader, shot, width, height, out _);
                CheckLabels(report, loader, $"{label} mosaic");
                report.Check($"{L}: {Path.GetFileName(shot)} written (mid-piece, mosaic section)", File.Exists(shot));

                // The harmony section: the target and the voices lit, their lines bright with their lights.
                MosaicSection harmony = m.SectionOf(MosaicSectionKind.Harmony)!;
                panel.JumpTo(harmony.Index);
                bool chip = Math.Abs(player.CurrentSeconds - m.LoopStartSeconds(harmony.FirstLoop)) < 1e-6 && d.MosaicSectionIndex == harmony.Index;
                double firstNote = m.Notes.Count > 0 ? m.Notes[0].Start + .1 : 1;
                player.Seek(m.TimeAtBeat(harmony.FirstLoop * lb + firstNote));
                TickMosaic(loader, .05f);
                List<SongNode> voices = m.Harmonies.Select(h => NodeOf(h.Song)).Where(n => n != null).Select(n => n!).ToList();
                bool harmonyLit = target.State == BubbleState.Focus && target.Highlighted && voices.All(n => n.State == BubbleState.Focus && n.Highlighted) &&
                                  graph.Lines.Count(l => l.Playing) == 1 + m.Harmonies.Count && graph.Dots[0].Active;
                report.Check($"{L}: a section chip jumps to its section; in the harmony section the target and the harmony voices glow at 3x, their lines bright with their lights",
                    chip && harmonyLit, $"{graph.Lines.Count(l => l.Playing)} bright lines; lights {string.Join(",", graph.Dots.Select(x => x.Active ? "on" : "off"))}");

                // Next / Back: loop by loop; Space; M; C, K and O ignored while a mosaic plays.
                player.Paused = false;
                d.GoTo(0);
                d.Next();
                bool next = d.MosaicLoop == 1 && Math.Abs(player.CurrentSeconds - m.LoopStartSeconds(1)) < 1e-6;
                panel.HandleKey(Key.RightArrow);
                bool arrow = d.MosaicLoop == 2;
                d.Previous();
                bool back = d.MosaicLoop == 1 && Math.Abs(player.CurrentSeconds - m.LoopStartSeconds(1)) < 1e-6;
                d.GoTo(m.Loops - 1);
                d.Next();
                bool last = d.MosaicLoop == m.Loops - 1;
                report.Check($"{L}: Next / → jump to the next loop's start (into the next section), Back / ← to the previous; nothing past the last loop", next && arrow && back && last,
                    $"loop {d.MosaicLoop}, {player.CurrentSeconds:0.00} s");
                panel.HandleKey(Key.Space);
                double held = player.CurrentSeconds;
                for (int k = 0; k < 10; k++) TickMosaic(loader, .1f);
                bool frozen = player.Paused && Math.Abs(player.CurrentSeconds - held) < 1e-9;
                panel.HandleKey(Key.Space);
                for (int k = 0; k < 5; k++) TickMosaic(loader, .1f);
                report.Check($"{L}: Space pauses the mix (clock frozen) and resumes it", frozen && !player.Paused && player.CurrentSeconds > held);
                bool hidden = panel.HandleKey(Key.M) && !graph.UserVisible && Refresh(graph) && !graph.Showing && d.FramingViewport == d.TourViewport;
                bool shownAgain = panel.HandleKey(Key.M) && graph.UserVisible && Refresh(graph) && graph.Showing && d.FramingViewport == d.MashupTourViewport;
                bool ignored = !panel.HandleKey(Key.C) && !panel.HandleKey(Key.K) && !panel.HandleKey(Key.O) && d.CurrentMosaic == m && !d.ApplesToApples &&
                               ReferenceEquals(d.ActivePlayer, player);
                report.Check($"{L}: M toggles the melody graph (the framing makes room for it); C, K and O do nothing while a mosaic plays", hidden && shownAgain && ignored);

                // Portrait: three times larger, panning under the centre; the picture mid-piece.
                ViewerLayout.ScreenOverride = new Vector2Int(1080, 1920);
                cam.aspect = 1080f / 1920f;
                loader.Layout?.ApplyNow();
                d.GoTo(midLoop);
                player.Seek(m.TimeAtBeat(midLoop * lb + (longest.Start + longest.End) * .5));
                player.Paused = true;
                TickMosaic(loader, .05f);
                loader.Layout?.ApplyNow();
                for (int k = 0; k < 60 && d.Flying; k++) TickMosaic(loader, .05f);
                loader.RefreshView(cam);
                float landscapePpb = (float)(MelodyGraphPanel.PlotArea.width / lb);
                Vector2 head = graph.ScreenPoint(new Vector2(graph.PlayheadX, 0), 1080, 1920);
                MelodyGraphPanel.DotView? pd = graph.Dots.FirstOrDefault(x => x.Active);
                report.Check($"{L}: portrait 1080x1920: the melody graph three times larger (px per beat and per semitone), panning, the playhead at the screen's centre (±0.5 px), the piece lit",
                    graph.Portrait && graph.Scrolling && Mathf.Abs(graph.Height - MelodyGraphPanel.PortraitPanelHeight) < .01f &&
                    Mathf.Abs(graph.Plot.height - MelodyGraphPanel.PortraitScale * MelodyGraphPanel.LandscapePlotHeight) < .01f &&
                    Mathf.Abs(graph.PixelsPerBeat - MelodyGraphPanel.PortraitScale * landscapePpb) < 1e-3 && Mathf.Abs(head.x - 540f) <= .5f &&
                    (pd == null || Mathf.Abs(pd.Position.x - MelodyGraphPanel.ScrollHeadX) <= .5f) && graph.Spans.Count(s => s.Lit) == 1,
                    $"height {graph.Height:0}, ppb {graph.PixelsPerBeat:0.##} vs {landscapePpb:0.##}, playhead at {head.x:0.00} px, {graph.Spans.Count(s => s.Lit)} lit spans");
                CheckLitChords(report, L + " portrait", loader, cam, 1080, 1920, pixels: false);
                CheckWheelPlacement(report, L + " portrait", loader, 1080, 1920);
                CheckMosaicTitle(report, L + " portrait", loader, m, 1080, 1920);
                IReadOnlyList<Rect> portraitRects = hud.PanelScreenRects(1080, 1920);
                Rect pr = hud.ScreenRect(graph.PanelRect!, 1080, 1920);
                LayoutFrame frame = loader.Layout != null ? loader.Layout.Frame : default;
                for (int k = 0; k < 80 && d.Flying; k++) TickMosaic(loader, .05f);
                loader.RefreshView(cam);
                List<string> portraitOff = FramingProblems(cam, d.MosaicShotFramed, pr, hud.ScreenRect(hud.WheelRect, 1080, 1920), 1080, 1920);
                report.Check($"{L}: portrait layout: the graph right above the strip (no narration band), clear of every panel; the singing song framed on screen above it, clear of the wheel",
                    NoOverlap(portraitRects) && portraitRects.Contains(pr) && pr.xMin >= -.5f && pr.xMax <= 1080.5f && pr.yMax <= 1920 &&
                    frame.Vertical && Mathf.Abs(frame.Melody.yMin - (frame.Strip.yMax + ViewerLayout.BandGap)) < .5f && portraitOff.Count == 0,
                    $"melody [{pr.xMin:0},{pr.yMin:0} {pr.width:0}x{pr.height:0}]; {string.Join(" | ", portraitOff.Take(4))}");
                string portraitShot = Shot("mosaic_portrait");
                Capture(cam, loader, portraitShot, 1080, 1920, out _);
                CheckLabels(report, loader, $"{label} mosaic portrait");
                report.Check($"{L}: {Path.GetFileName(portraitShot)} written (1080x1920, mosaic section)", File.Exists(portraitShot));
                player.Paused = false;

                // Back to landscape; Esc back to the mosaics list, Esc again closes.
                ViewerLayout.ScreenOverride = new Vector2Int(width, height);
                cam.aspect = (float)width / height;
                loader.Layout?.ApplyNow();
                graph.Refresh();
                report.Check($"{L}: back in landscape the mosaic graph keeps panning at its landscape size", !graph.Portrait && graph.Scrolling &&
                    Mathf.Abs(graph.Height - MelodyGraphPanel.PanelHeight) < .01f && graph.ShownMosaic == m);
                panel.HandleKey(Key.Escape);
                graph.Refresh();
                report.Check($"{L}: Esc stops the mix, hides the graph and the name, returns to the mosaics list",
                    !d.IsTouring && player.Current == null && !graph.Showing && panel.IsOpen && panel.Tab == FeaturedPathsPanel.ListTab.Mosaics && HudRefreshed(hud) && !hud.PathTitleVisible);
                CheckBubblesRestored(report, L, loader);
                report.Check($"{L}: Esc on the mosaics list closes it; the legend comes back", panel.HandleKey(Key.Escape) && panel.State == FeaturedPathsPanel.PanelState.Closed && HudRefreshed(hud) && hud.LegendVisible);
            }
            finally
            {
                player.MixFinished -= OnFinished;
                d.FlyDuration = fly;
                d.EdgeGrowDuration = grow;
                ViewerLayout.ScreenOverride = null;
                cam.aspect = (float)width / height;
                d.Exit();
            }
        }

        /// <summary>
        /// Songs whose bubble (at its drawn size) is off a <paramref name="width"/> x <paramref name="height"/>
        /// screen, under the top of <paramref name="melody"/> or over <paramref name="wheel"/> (screen px).
        /// </summary>
        static List<string> FramingProblems(Camera cam, IEnumerable<SongNode> nodes, Rect melody, Rect wheel, int width, int height)
        {
            List<string> bad = new();
            cam.aspect = (float)width / height;
            foreach (SongNode n in nodes)
            {
                Vector3 c = cam.WorldToViewportPoint(n.transform.position);
                Vector3 e = cam.WorldToViewportPoint(n.transform.position + cam.transform.right * n.DisplayRadius);
                float r = Mathf.Abs(e.x - c.x) * width;
                Rect disc = new(c.x * width - r, c.y * height - r, 2 * r, 2 * r);
                if (c.z <= 0 || disc.xMin < 0 || disc.xMax > width || disc.yMax > height) bad.Add($"'{n.Song.Title}' off screen [{disc.xMin:0},{disc.yMin:0} {disc.width:0}]");
                else if (disc.yMin < melody.yMax) bad.Add($"'{n.Song.Title}' under the melody graph's top ({disc.yMin:0} < {melody.yMax:0})");
                else if (disc.Overlaps(wheel)) bad.Add($"'{n.Song.Title}' under the wheel");
            }
            return bad;
        }

        /// <summary>The playing mosaic's name, large and outlined, top left (the legend hidden), clear of every panel.</summary>
        static void CheckMosaicTitle(Report r, string L, SongGraphLoader loader, Mosaic m, int width, int height)
        {
            GraphHud hud = loader.Hud;
            hud.ForceUpdate();
            float scale = hud.ScaleFor(width, height);
            Rect sr = hud.ScreenRect(hud.PathTitleRect, width, height);
            IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
            bool clear = rects.Where(x => x != sr).All(x => !x.Overlaps(sr));
            TMP_Text t = hud.PathTitleLabel;
            r.Check($"{L}: the mosaic's name '{m.Name}' shows top left in large outlined type (no box); the legend makes way",
                hud.PathTitleVisible && hud.PathTitleText == m.Name && !hud.LegendVisible && t.fontSize >= ViewerLayout.PathTitleMin &&
                t.fontSharedMaterial == UiKit.OutlinedMaterial && t.GetComponent<Image>() == null &&
                Mathf.Abs(sr.xMin - ViewerLayout.Margin * scale) <= 1f && Mathf.Abs(sr.yMax - (height - ViewerLayout.PathTitleTop * scale)) <= 1f && clear && sr.xMax <= width,
                $"'{hud.PathTitleText}' at {t.fontSize:0} px ref; [{sr.xMin:0},{sr.yMin:0} {sr.width:0}x{sr.height:0}]; clear of {rects.Count - 1} panels: {clear}");
        }
    }
}
