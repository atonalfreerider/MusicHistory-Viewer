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
    /// Mashup mixes (data/audio/mashups/mashups.json, version 1) in edit mode: the catalog contract
    /// and its mapping onto the graph and the featured paths, phrase-beat interpolation, segment
    /// lookup and audible sets; a path tour that plays its mashup (the mix on the main-thread clock:
    /// edit mode plays no audio) with the steps, highlight, edge and framing following the
    /// segments; the now-playing strip; the melody graph's geometry (every melody inside the plot,
    /// the playing ones highlighted, a light point at the playhead's beat and the melody's pitch,
    /// the chord strip of the instrumental playing), its bloom (a capture with and without the
    /// bloom volume) and its place clear of the HUD at 1920x1080; M, Next / Back / chips, pause,
    /// compare, the fallback to per-step previews without a mashup, and a missing mashups.json.
    ///
    /// Catalogs: the real data/audio/mashups/mashups.json when it exists, -validationMashups
    /// &lt;mashups.json&gt; (e.g. a fixture with generated mixes), and always a synthetic one built
    /// from the featured paths (mixes missing, so the clock plays). Part of
    /// <see cref="Validation.RunPaths"/> and <see cref="Validation.Run"/>; also
    /// <c>-executeMethod MusicHistory.EditorTools.Validation.RunMashups</c> on its own (writes
    /// mashup_validation.json). Screenshots: melody_graph.png (mid-changeover) and
    /// melody_graph_nobloom.png, + _&lt;label&gt; for later catalogs.
    /// </summary>
    public static partial class Validation
    {
        [MenuItem("MusicHistory/Run Mashup Validation (writes data/screens)")]
        public static void RunMashupsFromMenu() => ExecuteMashups(exitWhenDone: false);

        public static void RunMashups() => ExecuteMashups(exitWhenDone: true);

        static void ExecuteMashups(bool exitWhenDone)
        {
            Report report = new();
            string outDir = Arg("-validationOut") ?? Path.Combine(SongGraphLoader.RepoRoot(), "data", "screens");
            Directory.CreateDirectory(outDir);
            try
            {
                int width = int.TryParse(Arg("-validationWidth"), out int w) ? w : 1920;
                int height = int.TryParse(Arg("-validationHeight"), out int h) ? h : 1080;
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                SongGraphLoader? loader = Object.FindAnyObjectByType<SongGraphLoader>();
                Camera? cam = Camera.main;
                if (report.Check("scene has SongGraphLoader and a camera", loader != null && cam != null))
                {
                    cam!.aspect = (float)width / height;
                    string dbPath = PathArg("-validationLineageDb") ?? Path.Combine(SongGraphLoader.RepoRoot(), "data", "graph", "music_graph.db");
                    if (report.Check("mashups: graph database exists", File.Exists(dbPath), dbPath))
                    {
                        loader!.Build(dbPath);
                        ValidateMashups(report, loader, cam, outDir, width, height);
                    }
                    loader!.Clear();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check("no exception", false, e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Replace("\n", " | "));
            }
            WriteJson(report, Path.Combine(outDir, "mashup_validation.json"));
            Debug.Log($"[validation] {report.Checks.Count - report.Failures}/{report.Checks.Count} checks passed; report in {outDir}");
            if (exitWhenDone) EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }

        /// <summary>Everything about mashups; <paramref name="loader"/> holds the graph to check against.</summary>
        static void ValidateMashups(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            ValidateMashupsPure(report);
            string db = loader.ResolvedDbPath;
            loader.Build(db);
            // The featured paths the mashups belong to: the real paths.json, else a catalog of the graph's lineages.
            PathCatalog paths = loader.Catalog;
            if (paths.PlayableCount == 0)
            {
                paths = SyntheticCatalog(loader.Data!);
                loader.UseCatalog(paths, "validation: synthetic paths for mashups");
            }
            report.Text("mashups_paths", $"{paths.SourcePath} ({paths.PlayableCount} playable)");

            List<(string label, Func<MashupCatalog> load)> catalogs = new();
            string real = Path.Combine(SongGraphLoader.RepoRoot(), "data", "audio", "mashups", MashupCatalog.FileName);
            if (File.Exists(real)) catalogs.Add(("real", () => MashupCatalog.Load(real)));
            string? fixture = PathArg("-validationMashups");
            if (fixture != null && report.Check("mashups: -validationMashups file exists", File.Exists(fixture), fixture))
                catalogs.Add(("fixture", () => MashupCatalog.Load(fixture)));
            string synthetic = WriteSyntheticMashups(paths);
            catalogs.Add(("synthetic", () => MashupCatalog.Load(synthetic)));
            report.Text("mashups_catalogs", string.Join(", ", catalogs.Select(c => c.label)));
            for (int i = 0; i < catalogs.Count; i++)
            {
                (string label, Func<MashupCatalog> load) = catalogs[i];
                loader.Build(db);
                if (loader.Catalog.PlayableCount == 0) loader.UseCatalog(paths, "validation: synthetic paths for mashups");
                ValidateMashupCatalog(report, loader, cam, load(), label, outDir, width, height, canonicalShots: i == 0);
            }

            // A missing mashups.json: an empty catalog, and every path plays its per-step previews.
            loader.Build(db);
            if (loader.Catalog.PlayableCount == 0) loader.UseCatalog(paths, "validation: synthetic paths for mashups");
            MashupCatalog none = MashupCatalog.Load(Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", MashupCatalog.FileName));
            loader.UseMashups(none, "validation: missing file");
            FeaturedPath? any = loader.Catalog.Paths.FirstOrDefault(p => p.IsPlayable);
            WalkthroughDirector d = loader.Director;
            bool fallback = any != null && d.StartPathTour(any) && d.CurrentMashup == null && !ReferenceEquals(d.ActivePlayer, d.MashupAudio) &&
                            loader.MelodyGraph != null && Refresh(loader.MelodyGraph) && !loader.MelodyGraph.Showing;
            d.Exit();
            report.Check("mashups: a missing mashups.json gives an empty catalog; paths play their per-step previews, no melody graph",
                !none.Loaded && none.Mashups.Count == 0 && none.Status.StartsWith("missing") && fallback, none.Status);
        }

        // ------------------------------------------------------------------ pure

        const string PureJson =
            "{ \"version\": 1, \"generated_at\": \"2026-09-30T12:00:00Z\", \"frame\": \"C major / A minor\", \"paths\": [" +
            " { \"id\": \"a\", \"title\": \"A\", \"file\": \"a/mix.mp3\", \"seconds\": 10, \"beats_per_bar\": 4, \"phrase_beats\": 8," +
            "   \"segments\": [" +
            "     { \"start\": 0, \"end\": 4, \"kind\": \"full\", \"instrumental\": \"Q1\", \"vocal\": \"Q1\", \"key\": \"C major\", \"bpm\": 120, \"bpm_start\": 120," +
            "       \"vocal_shift_semitones\": null, \"vocal_tempo_ratio\": null, \"chord_match\": null, \"beat_error_ms\": null }," +
            "     { \"start\": 4, \"end\": 8, \"kind\": \"changeover\", \"instrumental\": \"Q1\", \"vocal\": \"Q2\", \"key\": \"C major\", \"bpm\": 120, \"bpm_start\": 120," +
            "       \"vocal_shift_semitones\": -2, \"vocal_tempo_ratio\": 1.2, \"chord_match\": 0.875, \"beat_error_ms\": 11.5 }," +
            "     { \"start\": 8, \"end\": 10, \"kind\": \"morph\", \"instrumental\": \"Q2\", \"vocal\": \"Q2\", \"key\": \"D major\", \"bpm\": 100, \"bpm_start\": 120 } ]," +
            "   \"beats\": [ [0, 0], [0.5, 1], [1, 2], [1.5, 3], [2, 4], [2.5, 5], [3, 6], [3.5, 7], [4, 0], [4.5, 1], [5, 2], [5.5, 3], [6, 6], [6.5, 7], [7, 0], [7.5, 1] ]," +
            "   \"songs\": [" +
            "     { \"work_id\": \"Q1\", \"title\": \"One\", \"artist\": \"X\", \"year\": 1960, \"step\": 0," +
            "       \"melody\": [ [0, 60], [0.5, 62], [1, null], [2, 64], [2.0625, 64.5] ]," +
            "       \"chords\": [ [0, 4, 0, \"maj\", \"I\"], [4, 8, 9, \"min\", \"vi\"] ]," +
            "       \"vocal_audible\": [ [0, 4] ], \"instrumental_audible\": [ [0, 8] ] }," +
            "     { \"work_id\": \"Q2\", \"title\": \"Two\", \"artist\": \"Y\", \"year\": 1970, \"step\": 1," +
            "       \"melody\": [ [4, 69], [6, 60], [6.25, 61], [0, 67] ], \"chords\": [ [0, 8, 7, \"maj\", \"V\"] ]," +
            "       \"vocal_audible\": [ [4, 10] ], \"instrumental_audible\": [ [8, 10] ] } ] } ] }";

        static void ValidateMashupsPure(Report report)
        {
            MashupCatalog c = MashupCatalog.Parse(PureJson, Path.Combine(Path.GetTempPath(), "musichistory-validation-mashups"), "inline");
            Mashup? m = c.Mashups.FirstOrDefault();
            MashupSegment? co = m?.Segments.ElementAtOrDefault(1);
            report.Check("mashups.json v1 parses: segments (kinds, keys, tempi, nullable numbers), beats, songs (melody with breaks, chords, audible spans)",
                c.Loaded && c.Version == 1 && c.Frame == "C major / A minor" && m != null && m.Segments.Count == 3 && m.BeatTimes.Length == 16 &&
                m.Songs.Count == 2 && m.BeatsPerBar == 4 && Math.Abs(m.PhraseBeats - 8) < 1e-9 && m.Bars == 2 &&
                m.Segments[0].Kind == MashupSegmentKind.Full && m.Segments[0].ChordMatch == null && m.Segments[0].VocalShiftSemitones == null &&
                co != null && co.Kind == MashupSegmentKind.Changeover && co.VocalShiftSemitones == -2 && co.VocalTempoRatio == 1.2 &&
                co.ChordMatch == .875 && co.BeatErrorMs == 11.5 && co.InstrumentalSong == 0 && co.VocalSong == 1 &&
                m.Segments[2].Kind == MashupSegmentKind.Morph && m.Segments[2].BpmStart == 120 && m.Segments[2].Bpm == 100 &&
                m.Songs[0].Melody.Count == 5 && !m.Songs[0].Melody[2].Voiced && m.Songs[0].VoicedCount == 4 &&
                m.Songs[0].Chords.Count == 2 && m.Songs[0].Chords[1].Roman == "vi" && m.Songs[0].Chords[1].Minor && m.Songs[0].Chords[1].RootPc == 9 &&
                m.Songs[1].VocalAudible.Count == 1 && !m.FileExists,
                $"{c.Status}; {string.Join(" | ", c.Problems)}");
            report.Check("mashups.json: a missing mix is listed as a problem, not thrown", c.Problems.Count == 1 && c.Problems[0].Contains("mix missing"),
                string.Join(" | ", c.Problems));

            // Segment lookup: boundaries belong to the later segment; before/after clamp.
            bool lookup = m != null && m.SegmentIndexAt(-1) == 0 && m.SegmentIndexAt(0) == 0 && m.SegmentIndexAt(3.999) == 0 &&
                          m.SegmentIndexAt(4) == 1 && m.SegmentIndexAt(7.5) == 1 && m.SegmentIndexAt(8) == 2 && m.SegmentIndexAt(99) == 2;
            report.Check("mashup segment lookup: a boundary belongs to the next segment; before the first and after the last clamp", lookup);

            // Phrase beats: interpolated, wrapped at the phrase end, extrapolated at both ends, a jump not interpolated across.
            double[] probes = { .25, 1.75, 3.75, 4.25, -.5, 7.75, 8.25, 5.75 };
            double[] expect = { .5, 3.5, 7.5, .5, 7, 1.5, 2.5, 3.5 };
            List<string> beatErrors = new();
            for (int i = 0; i < probes.Length && m != null; i++)
            {
                double got = m.PhraseBeatAt(probes[i]);
                if (Math.Abs(got - expect[i]) > 1e-9) beatErrors.Add($"t {probes[i]}: {got} vs {expect[i]}");
            }
            report.Check("mashup phrase beat: interpolated between beats, wraps 7 → 0, extrapolates before/after, a jump in the grid counts one beat",
                m != null && beatErrors.Count == 0, string.Join(" | ", beatErrors));

            List<int> vocals = new(), instrumentals = new();
            string Sets(double t)
            {
                m!.AudibleAt(t, vocals, instrumentals);
                return $"{string.Join(",", vocals)}/{string.Join(",", instrumentals)}";
            }
            report.Check("mashup audible sets: vocal and instrumental per song from vocal_audible / instrumental_audible",
                m != null && Sets(1) == "0/0" && Sets(5) == "1/0" && Sets(9) == "1/1" && Sets(10) == "/",
                m != null ? $"{Sets(1)} {Sets(5)} {Sets(9)} {Sets(10)}" : "");

            bool pitch = m != null &&
                         m.Songs[0].PitchAt(.25, out float p1, out bool v1) && v1 && Math.Abs(p1 - 61) < 1e-4 &&
                         m.Songs[0].PitchAt(1.5, out float p2, out bool v2) && !v2 && Math.Abs(p2 - 64) < 1e-4 &&
                         m.Songs[0].PitchAt(2.03125, out float p3, out bool v3) && v3 && Math.Abs(p3 - 64.25) < 1e-4 &&
                         !m.Songs[0].PitchAt(7.9, out _, out _) &&
                         m.Songs[1].PitchAt(2, out float p4, out bool v4) && !v4 && Math.Abs(p4 - 67) < 1e-4 &&
                         m.Songs[1].PitchAt(6.125, out float p5, out bool v5) && v5 && Math.Abs(p5 - 60.5) < 1e-4 &&
                         m.Songs[1].Melody[0].Beat == 4 && m.Songs[1].ByBeat[0].Beat == 0;
            report.Check("melody pitch: interpolated where sung; in a breath the nearest sung pitch within reach (not voiced); nothing far from a note; " +
                         "a vocal window that wraps past the phrase's end keeps its sung order (looked up by beat)", pitch);
            report.Check("segment glide: a morph's BPM follows a smoothstep from bpm_start to bpm",
                m != null && Math.Abs(m.Segments[2].BpmAt(8) - 120) < 1e-9 && Math.Abs(m.Segments[2].BpmAt(9) - 110) < 1e-9 &&
                Math.Abs(m.Segments[2].BpmAt(10) - 100) < 1e-9 && Math.Abs(m.Segments[1].BpmAt(5) - 120) < 1e-9);

            // Binding to a featured path: songs to steps by work_id; step starts are where each vocal enters.
            PathCatalog paths = PathCatalog.Parse("{\"version\": 2, \"morph_bars\": 2, \"paths\": [ {\"id\": \"a\", \"title\": \"A\", \"seconds\": 60, \"steps\": [" +
                                                  "{\"work_id\": \"Q1\", \"title\": \"One\", \"year\": 1960, \"file\": \"x.mp3\", \"seconds\": 30, \"bpm\": 120}," +
                                                  "{\"work_id\": \"Q2\", \"title\": \"Two\", \"year\": 1970, \"file\": \"y.mp3\", \"seconds\": 30, \"bpm\": 100, \"via\": {\"identity\": \"I-vi\"}} ] } ] }", "");
            int playable = c.Bind(w => w == "Q1" ? 1 : w == "Q2" ? 2 : null, paths);
            report.Check("mashups bind to their featured path (songs to steps by work_id, nodes by work_id); step starts = where each vocal enters",
                playable == 1 && m!.IsPlayable && m.Path == paths.Paths[0] && m.Songs[1].PathStep == 1 && m.Songs[1].NodeId == 2 &&
                c.For(paths.Paths[0]) == m && m.StepStartSeconds(0) == 0 && m.StepStartSeconds(1) == 4 && m.StepReachedAt(3.9, 2) == 0 &&
                m.StepReachedAt(4, 2) == 1 && m.InstrumentalStep(m.Segments[1]) == 0 && m.VocalStep(m.Segments[1]) == 1,
                MashupCatalog.WhyNotPlayable(m!));
            int unbound = c.Bind(w => w == "Q1" ? 1 : null, paths);
            report.Check("a mashup whose song is not in the graph is not playable (it says why)", unbound == 0 && !m!.IsPlayable &&
                MashupCatalog.WhyNotPlayable(m).Contains("Q2"), MashupCatalog.WhyNotPlayable(m!));

            MashupCatalog broken = MashupCatalog.Parse("{\"version\": 2, \"paths\": [ {\"id\": \"b\", \"file\": \"b.mp3\", \"seconds\": 10, \"phrase_beats\": 8, " +
                                                       "\"segments\": [ {\"start\": 0, \"end\": 4, \"kind\": \"swap\", \"instrumental\": \"Q9\", \"bpm\": 100}, " +
                                                       "{\"start\": 5, \"end\": 9, \"kind\": \"changeover\", \"instrumental\": \"Q1\", \"vocal\": \"\", \"bpm\": 100, \"chord_match\": 1.5} ], " +
                                                       "\"beats\": [[1, 0], [0.5, 9]], \"songs\": [ {\"work_id\": \"Q1\", \"melody\": [[0, 60]]} ] } ] }", "");
            string[] wanted = { "version 2", "kind 'swap'", "instrumental 'Q9'", "needs another song's vocal", "chord_match 1.5", "the segment before ends",
                                "not in time order", "outside [0, 8)", "segments end at" };
            report.Check("mashups.json: contract violations are reported (version, kind, unknown song, changeover vocal, chord_match, gaps, beat order and range, length)",
                wanted.All(w => broken.Problems.Any(x => x.Contains(w))), string.Join(" | ", broken.Problems));
            MashupCatalog junk = MashupCatalog.Parse("{ \"version\": 1, \"paths\": [ ", "");
            report.Check("mashups.json: malformed JSON gives an empty catalog, no exception", !junk.Loaded && junk.Mashups.Count == 0 && junk.Problems.Count == 1, junk.Status);
        }

        // ------------------------------------------------------------------ one catalog

        static void ValidateMashupCatalog(Report report, SongGraphLoader loader, Camera cam, MashupCatalog catalog, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            string L = $"mashups[{label}]";
            bool synthetic = label == "synthetic";
            loader.UseMashups(catalog, "validation");
            SongGraphData data = loader.Data!;
            report.Text($"mashups_{label}_file", catalog.SourcePath);
            report.Number($"mashups_{label}_count", catalog.Mashups.Count, "0");
            report.Check($"{L}: loads (version 1, at least one mashup)", catalog.Loaded && catalog.Version == 1 && catalog.Mashups.Count > 0, catalog.Status);
            if (catalog.Mashups.Count == 0) return;
            List<string> contract = catalog.Problems.Where(x => !synthetic || !x.Contains("mix missing")).ToList();
            report.Check($"{L}: no contract problems (segments, beats, songs, files)", contract.Count == 0,
                $"{contract.Count}: {string.Join(" | ", contract.Take(5))}");
            List<string> unbound = catalog.Mashups.Where(x => !x.IsPlayable).Select(x => $"{x.Id}: {MashupCatalog.WhyNotPlayable(x)}").ToList();
            report.Check($"{L}: every mashup maps onto its featured path and its songs onto the graph", unbound.Count == 0, string.Join(" | ", unbound.Take(4)));
            List<string> titles = catalog.Mashups.SelectMany(x => x.Songs).Where(s => s.NodeId > 0 &&
                    (data.Song(s.NodeId).WorkId != s.WorkId || data.Song(s.NodeId).Year != s.Year))
                .Select(s => $"{s.WorkId} {s.Year} vs {data.Song(s.NodeId).Year}").ToList();
            report.Check($"{L}: song work_ids and years agree with the graph", titles.Count == 0, string.Join(" | ", titles.Take(4)));
            // The chain design: the root song's full mix first, the changeovers in path order, the last song's ending.
            List<string> chain = new();
            foreach (Mashup x in catalog.Mashups.Where(x => x.IsPlayable))
            {
                List<MashupSegment> cos = x.Segments.Where(g => g.Kind == MashupSegmentKind.Changeover).ToList();
                bool ok = x.Segments[0].Kind == MashupSegmentKind.Full && x.InstrumentalStep(x.Segments[0]) == 0 &&
                          cos.Count == x.Path!.Steps.Count - 1 &&
                          cos.Select((g, i) => x.InstrumentalStep(g) == i && x.VocalStep(g) == i + 1).All(b => b) &&
                          x.InstrumentalStep(x.Segments[^1]) == x.Path.Steps.Count - 1;
                if (!ok) chain.Add(x.Id);
            }
            report.Check($"{L}: each mix starts with the root song, changes over song by song in path order and ends on the last song", chain.Count == 0,
                string.Join(", ", chain));

            Mashup m = catalog.Mashups.Where(x => x.IsPlayable).OrderByDescending(x => x.Songs.Count).ThenBy(x => x.Index).FirstOrDefault()
                       ?? catalog.Mashups[0];
            if (!m.IsPlayable) return;
            report.Text($"mashups_{label}_tour", $"{m.Id} ({m.Songs.Count} songs, {m.Segments.Count} segments, {m.Duration:0.0} s)");
            ValidateMashupTour(report, loader, cam, m, L, label, outDir, width, height, canonicalShots);
        }

        /// <summary>Edit mode: the mix keeps time on the main-thread clock; this advances it, the walkthrough and the graph.</summary>
        static void TickMashup(SongGraphLoader loader, float dt)
        {
            WalkthroughDirector d = loader.Director;
            if (d.MashupAudio != null) d.MashupAudio.Advance(dt);
            if (d.Preview != null) d.Preview.Advance(dt);
            d.Tick(dt);
            if (d.ActivePlayer is SilentSongPlayer s) s.Advance(dt);
            if (loader.MelodyGraph != null) loader.MelodyGraph.Refresh();
        }

        static void ValidateMashupTour(Report report, SongGraphLoader loader, Camera cam, Mashup m, string L, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            MashupPlayer player = d.MashupAudio!;
            MelodyGraphPanel graph = loader.MelodyGraph!;
            GraphHud hud = loader.Hud;
            FeaturedPath path = m.Path!;
            string Shot(string name) => Path.Combine(outDir, canonicalShots ? $"{name}.png" : $"{name}_{label}.png");
            int mixFinished = 0;
            void OnMix(Mashup x) => mixFinished++;
            player.MixFinished += OnMix;
            float fly = d.FlyDuration, grow = d.EdgeGrowDuration;
            try
            {
                loader.Highlighter.ApplyFocus(null, force: true);
                loader.FrameOverview(cam);
                panel.HandleKey(Key.P);
                panel.Select(panel.Paths.ToList().IndexOf(path));
                RowView? row = panel.Rows.FirstOrDefault(r => r.PathIndex == panel.Paths.ToList().IndexOf(path));
                report.Check($"{L}: the list marks a path with a mashup ('mashup mix', the mix's length) and counts the mixes",
                    row != null && row.Subtitle.text.Contains("mashup mix") && row.Duration.text == PathCatalog.Clock(m.Duration) && panel.ListText.Length > 0,
                    row != null ? $"{row.Subtitle.text} | {row.Duration.text}" : "no row");
                report.Check($"{L}: Enter on a path with a mashup plays the mix (one continuous file), not per-step previews",
                    panel.HandleKey(Key.Enter) && d.IsTouring && d.CurrentPath == path && d.CurrentMashup == m && ReferenceEquals(d.ActivePlayer, player) &&
                    player.Current == m && d.StepIndex == 0 && d.SegmentIndex == 0 && panel.State == FeaturedPathsPanel.PanelState.Playing,
                    $"{d.ActivePlayerName}; mashup {d.CurrentMashup?.Id ?? "none"}");
                graph.Refresh();
                report.Check($"{L}: the melody graph shows while the mashup plays, with one melody line and one light point per song",
                    graph.Showing && graph.Shown == m && graph.Lines.Count == m.Songs.Count && graph.Dots.Count == m.Songs.Count);

                // Play the whole mix on the clock: the steps, highlight, edge and graph follow the segments.
                d.FlyDuration = .4f;
                d.EdgeGrowDuration = .3f;
                List<int> visited = new();
                List<string> wrong = new(), graphWrong = new();
                double dt = .1, guard = 0;
                double worstDot = 0, worstHead = 0;
                int changeoverSamples = 0, dotSamples = 0;
                List<int> vocals = new(), instrumentals = new();
                while (!d.TourComplete && guard < m.Duration + 30)
                {
                    TickMashup(loader, (float)dt);
                    guard += dt;
                    double t = player.CurrentSeconds;
                    int si = m.SegmentIndexAt(t);
                    if (visited.Count == 0 || visited[^1] != d.SegmentIndex) visited.Add(d.SegmentIndex);
                    if (d.TourComplete) break;
                    MashupSegment g = m.Segments[si];
                    int inst = m.InstrumentalStep(g), voc = m.VocalStep(g);
                    if (d.SegmentIndex != si || d.StepIndex != inst || (g.Kind == MashupSegmentKind.Changeover && d.VocalStepIndex != voc))
                        wrong.Add($"t {t:0.0}: segment {d.SegmentIndex}/{si}, step {d.StepIndex}/{inst}, vocal {d.VocalStepIndex}/{voc}");
                    if (g.Kind == MashupSegmentKind.Changeover && wrong.Count == 0)
                    {
                        changeoverSamples++;
                        SongNode a = loader.NodeById(path.Steps[inst].NodeId), b = loader.NodeById(path.Steps[voc].NodeId);
                        InfluenceEdge? e = d.CurrentRoute!.StepEdges[Math.Max(inst, voc)];
                        if (a.State != BubbleState.Focus || b.State != BubbleState.Focus || d.StepEdge != e || (e != null && e.State != EdgeState.Highlight))
                            wrong.Add($"t {t:0.0}: changeover highlight {a.State}/{b.State}, edge {(d.StepEdge == e ? "ok" : "other")}");
                    }
                    // The graph: phrase beat, playhead, playing lines, light points, the chord strip.
                    m.AudibleAt(t, vocals, instrumentals);
                    double beat = m.PhraseBeatAt(t);
                    if (Math.Abs(graph.PhraseBeat - beat) > 1e-6) graphWrong.Add($"t {t:0.0}: beat {graph.PhraseBeat:0.000} vs {beat:0.000}");
                    worstHead = Math.Max(worstHead, Math.Abs(graph.PlayheadX - graph.PanelPoint(beat, 60).x));
                    foreach (MelodyGraphPanel.LineView l in graph.Lines)
                        if (l.Playing != vocals.Contains(l.Song)) graphWrong.Add($"t {t:0.0}: song {l.Song} playing {l.Playing}");
                    foreach (MelodyGraphPanel.DotView dot in graph.Dots)
                    {
                        // The vocals' lights, plus a dimmer one on the backing's own melody during a changeover.
                        int backing = g.Kind == MashupSegmentKind.Changeover && !vocals.Contains(g.InstrumentalSong) ? g.InstrumentalSong : -1;
                        bool expected = (vocals.Contains(dot.Song) || dot.Song == backing) && m.Songs[dot.Song].PitchAt(beat, out _, out _);
                        if (dot.Active != expected || (dot.Active && dot.Backing != (dot.Song == backing)))
                            graphWrong.Add($"t {t:0.0}: dot {dot.Song} active {dot.Active} backing {dot.Backing}");
                        if (!dot.Active) continue;
                        m.Songs[dot.Song].PitchAt(beat, out float p, out _);
                        worstDot = Math.Max(worstDot, (dot.Position - graph.PanelPoint(beat, p)).magnitude);
                        dotSamples++;
                    }
                    if (graph.StripSong != g.InstrumentalSong) graphWrong.Add($"t {t:0.0}: chord strip song {graph.StripSong} vs {g.InstrumentalSong}");
                }
                for (int k = 0; k < 20; k++) TickMashup(loader, (float)dt);
                report.Check($"{L}: the mix plays through every segment in order on the clock and completes once",
                    d.TourComplete && visited.SequenceEqual(Enumerable.Range(0, m.Segments.Count)) && mixFinished == 1,
                    $"visited {string.Join(",", visited)}; finished {mixFinished}; at {player.CurrentSeconds:0.0}/{m.Duration:0.0} s");
                report.Check($"{L}: the current step is the instrumental's song; in a changeover both songs glow and the edge between them lights up",
                    wrong.Count == 0 && changeoverSamples > 0, $"{changeoverSamples} changeover samples; {string.Join(" | ", wrong.Take(3))}");
                report.Check($"{L}: melody graph follows the mix: phrase beat, playing melodies = audible vocals, light points on them, chord strip = instrumental",
                    graphWrong.Count == 0 && dotSamples > 0, $"{dotSamples} light samples; {string.Join(" | ", graphWrong.Take(3))}");
                report.Check($"{L}: each light point sits at the playhead's beat and the melody's pitch there; the playhead at the phrase beat (≤ 0.5 px)",
                    worstDot <= .5 && worstHead <= .5, $"light {worstDot:0.000} px, playhead {worstHead:0.000} px");

                // Geometry: every melody inside the plot, the legend, note names, roman numerals.
                Rect plot = MelodyGraphPanel.PlotArea;
                Rect inside = new(plot.xMin - .5f, plot.yMin - .5f, plot.width + 1f, plot.height + 1f);
                int outside = graph.Lines.Sum(l => l.Points.Count(pt => !inside.Contains(pt)));
                bool counts = graph.Lines.All(l => l.Points.Count == m.Songs[l.Song].VoicedCount);
                report.Check($"{L}: every sung point of every melody is drawn, inside the plot area", outside == 0 && counts,
                    $"{graph.Lines.Sum(l => l.Points.Count)} points, {outside} outside; pitch range {graph.PitchLow:0}–{graph.PitchHigh:0}");
                string legend = graph.LegendText;
                report.Check($"{L}: the legend lists every song's title and year", m.Songs.All(s => legend.Contains(GraphHud.Esc(s.Title)) && legend.Contains(s.Year.ToString(CultureInfo.InvariantCulture))),
                    legend.Replace("\n", " / "));
                report.Check($"{L}: note-name ticks along the pitch axis", graph.PitchLabelText.Contains("C"), graph.PitchLabelText);

                // Mid-changeover: the picture.
                MashupSegment first = m.Segments.First(g => g.Kind == MashupSegmentKind.Changeover);
                d.GoTo(m.VocalStep(first));
                bool jumped = Math.Abs(player.CurrentSeconds - first.Start) < 1e-6 && d.SegmentIndex == first.Index && d.StepIndex == m.InstrumentalStep(first) &&
                              d.VocalStepIndex == m.VocalStep(first);
                double target = first.Start + first.Length * .45;
                for (int k = 0; k < 4000 && player.CurrentSeconds < target; k++) TickMashup(loader, .05f);
                // On a sung note, so the light is at full strength.
                for (int k = 0; k < 120 && !graph.Dots.Any(x => x.Active && x.Voiced && !x.Backing); k++) TickMashup(loader, .05f);
                for (int k = 0; k < 30 && d.Flying; k++) TickMashup(loader, .05f);
                panel.RefreshNowPlaying();
                graph.Refresh();
                report.Check($"{L}: a chip / step jump plays from where that song's vocal enters (its changeover)", jumped,
                    $"at {player.CurrentSeconds:0.00} s, segment {d.SegmentIndex}");
                string strip = panel.NowPlayingText;
                MashupSong vocalSong = m.Songs[first.VocalSong], instSong = m.Songs[first.InstrumentalSong];
                bool stripOk = strip.Contains("Changeover") && strip.Contains("vocal over") && strip.Contains(GraphHud.Esc(vocalSong.Title)) &&
                               strip.Contains(GraphHud.Esc(instSong.Title)) && strip.Contains("Key") && strip.Contains(GraphHud.Esc(first.Key)) && strip.Contains("BPM") &&
                               (first.ChordMatch == null || strip.Contains(MashupCatalog.Percent(first.ChordMatch))) && strip.Contains("CONTINUOUS MIX") &&
                               strip.Contains("VOCAL") && strip.Contains($"STEP {m.InstrumentalStep(first) + 1} / {path.Steps.Count}");
                report.Check($"{L}: the strip reads 'Changeover <vocal song> vocal over <instrumental song>', key, BPM, chord match; the vocal's chip is marked",
                    stripOk, strip.Replace("\n", " / "));
                report.Check($"{L}: the walkthrough text names the segment ('Changeover: X vocal over Y')",
                    d.HudText.Contains(WalkthroughDirector.SegmentDescription(m, first)), FirstLines(d.HudText, 3));
                MelodyGraphPanel.DotView? light = graph.Dots.FirstOrDefault(x => x.Active && x.Voiced && !x.Backing);
                MelodyGraphPanel.LineView? playingLine = graph.Lines.FirstOrDefault(l => l.Song == first.VocalSong);
                report.Check($"{L}: mid-changeover the vocal's melody is the playing one (bright, thicker) with its light point; the backing's own melody a step below; the others are muted",
                    light != null && light.Song == first.VocalSong && playingLine != null && playingLine.Playing &&
                    graph.Lines.Where(l => l.Song != first.VocalSong).All(l => !l.Playing && l.Width < playingLine.Width) &&
                    graph.BackingSong == first.InstrumentalSong && graph.Lines.Where(l => l.Backing).Select(l => l.Song).SequenceEqual(new[] { first.InstrumentalSong }) &&
                    graph.Dots.Where(x => x.Active && x.Song != first.VocalSong).All(x => x.Backing && x.Song == first.InstrumentalSong) &&
                    graph.StripSong == first.InstrumentalSong && graph.VocalStripSong == first.VocalSong && graph.ChordText.Length > 0,
                    $"light on {light?.Song}; backing {graph.BackingSong}; strip {graph.StripSong}, vocal strip {graph.VocalStripSong}; chords {graph.ChordText}");
                MelodyLightRig? rig = graph.Lights;
                UnityEngine.Rendering.Universal.UniversalAdditionalCameraData? rigData = rig != null
                    ? UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(rig.Camera) : null;
                UnityEngine.Rendering.Universal.UniversalAdditionalCameraData mainData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam);
                int lightMask = 1 << MelodyLightRig.Layer;
                report.Check($"{L}: the light points are HDR quads (MusicHistory/MelodyGlow, far above the bloom threshold 1) seen only by their own post-processing camera and bloom volume",
                    rig != null && rig.Active && rig.LightMaterial.shader.name == MelodyGraphPanel.GlowShaderName && rig.CoreIntensity > 2 &&
                    rigData != null && rigData.renderPostProcessing && rig.Camera.cullingMask == lightMask && (cam.cullingMask & lightMask) == 0 &&
                    rig.Volume.gameObject.layer == MelodyLightRig.Layer && (rigData.volumeLayerMask.value & lightMask) != 0 && (mainData.volumeLayerMask.value & lightMask) == 0 &&
                    rig.Bloom.intensity.value > 0 && rig.Image.texture == rig.Texture && rig.Image.gameObject.activeInHierarchy && rig.Texture != null,
                    rig != null ? $"core x{rig.CoreIntensity:0.#}, bloom {rig.Bloom.intensity.value:0.##}, texture {rig.Texture?.width}x{rig.Texture?.height}" : "no light rig");

                // Layout at 1920x1080: clear of the HUD, the songs framed above the graph.
                loader.RefreshView(cam);
                // The wheel and the strip light the chord sounding; the path's name; the two songs 3x.
                CheckLitChords(report, L, loader, cam, width, height, pixels: canonicalShots);
                CheckWheelPlacement(report, L, loader, width, height);
                CheckPathTitle(report, L, loader, path, width, height);
                CheckHighlightedBubbles(report, L, loader, cam, new[] { loader.NodeById(instSong.NodeId), loader.NodeById(vocalSong.NodeId) }, width, height);
                IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
                Rect melodyRect = hud.ScreenRect(graph.PanelRect!, width, height);
                report.Check($"{L}: the melody graph is on screen and never overlaps the strip, legend, info or button",
                    NoOverlap(rects) && rects.Contains(melodyRect) && melodyRect.xMin >= 0 && melodyRect.xMax <= width && melodyRect.yMax <= height,
                    string.Join(" ", rects.Select(x => $"[{x.xMin:0},{x.yMin:0} {x.width:0}x{x.height:0}]")));
                bool framed = new[] { instSong, vocalSong }.All(s =>
                {
                    Vector3 v = cam.WorldToViewportPoint(loader.NodeById(s.NodeId).transform.position);
                    return v.z > 0 && v.x > .02f && v.x < .98f && v.y * height > melodyRect.yMax && v.y < .98f;
                });
                report.Check($"{L}: the camera frames both songs of the changeover above the melody graph", framed);
                string shot = Shot("melody_graph");
                Capture(cam, loader, shot, width, height, out _);
                CheckLabels(report, loader, $"{label} mashup");
                report.Check($"{L}: {Path.GetFileName(shot)} written (mid-changeover)", File.Exists(shot));
                MelodyLightRig? lights = graph.Lights;
                report.Check($"{L}: in a {width}x{height} capture the light texture covers the panel pixel for pixel",
                    lights != null && lights.Texture != null && lights.Texture.width == Mathf.RoundToInt(MelodyGraphPanel.PanelWidth * hud.ScaleFor(width, height)) &&
                    lights.Texture.height == Mathf.RoundToInt(MelodyGraphPanel.PanelHeight * hud.ScaleFor(width, height)),
                    lights?.Texture != null ? $"{lights.Texture.width}x{lights.Texture.height}" : "none");
                if (light != null) CheckBloom(report, loader, cam, graph, light, L, canonicalShots ? Shot("melody_graph_nobloom") : null, width, height);

                // M hides the graph (the songs are framed lower), M shows it again.
                bool hidden = panel.HandleKey(Key.M) && !graph.UserVisible && (Refresh(graph) && !graph.Showing) && d.FramingViewport == d.TourViewport;
                bool back = panel.HandleKey(Key.M) && graph.UserVisible && Refresh(graph) && graph.Showing && d.FramingViewport == d.MashupTourViewport;
                panel.MelodyButton!.onClick.Invoke();
                bool button = !graph.UserVisible;
                panel.MelodyButton.onClick.Invoke();
                report.Check($"{L}: M and the Melody button toggle the graph (the framing makes room for it)", hidden && back && button && graph.UserVisible);

                // Next / Back jump to where songs enter; pause holds the clock.
                d.GoTo(0);
                d.Next();
                bool next = Math.Abs(player.CurrentSeconds - m.StepStartSeconds(1)) < 1e-6 && d.VocalStepIndex == 1 && d.StepIndex == 0;
                d.Next();
                bool next2 = path.Steps.Count < 3 || Math.Abs(player.CurrentSeconds - m.StepStartSeconds(2)) < 1e-6;
                d.Previous();
                bool prev = Math.Abs(player.CurrentSeconds - m.StepStartSeconds(path.Steps.Count < 3 ? 0 : 1)) < 1e-6;
                report.Check($"{L}: Next jumps to the next song's changeover, Back to the previous one", next && next2 && prev,
                    $"{player.CurrentSeconds:0.00} s");
                panel.HandleKey(Key.Space);
                double held = player.CurrentSeconds;
                for (int k = 0; k < 10; k++) TickMashup(loader, .1f);
                bool frozen = player.Paused && Math.Abs(player.CurrentSeconds - held) < 1e-9;
                panel.HandleKey(Key.Space);
                for (int k = 0; k < 5; k++) TickMashup(loader, .1f);
                report.Check($"{L}: Space pauses the mix (clock frozen) and resumes it", frozen && !player.Paused && player.CurrentSeconds > held);

                // Compare (C) plays the MIDI per step and hides the graph; C again returns to the mix.
                panel.HandleKey(Key.C);
                graph.Refresh();
                bool compare = d.CurrentMashup == null && !ReferenceEquals(d.ActivePlayer, player) && !player.IsPlaying && !graph.Showing;
                panel.HandleKey(Key.C);
                graph.Refresh();
                report.Check($"{L}: C (compare) plays the MIDI and hides the graph; C again plays the mix from the step's entry",
                    compare && d.CurrentMashup == m && ReferenceEquals(d.ActivePlayer, player) && graph.Showing &&
                    Math.Abs(player.CurrentSeconds - m.StepStartSeconds(d.MashupStepReached)) < 1e-6);
                panel.HandleKey(Key.Escape);
                graph.Refresh();
                report.Check($"{L}: Esc stops the mix, hides the graph and returns to the list", !d.IsTouring && player.Current == null && !graph.Showing && panel.IsOpen);
                CheckBubblesRestored(report, L, loader);

                // A path without a mashup falls back to its per-step previews (or MIDI).
                FeaturedPath? other = loader.Catalog.Paths.FirstOrDefault(p => p.IsPlayable && loader.Mashups.For(p) == null);
                if (other != null)
                {
                    panel.Select(panel.Paths.ToList().IndexOf(other));
                    panel.HandleKey(Key.Enter);
                    graph.Refresh();
                    report.Check($"{L}: a path without a mashup plays its per-step previews (or MIDI), no melody graph",
                        d.IsTouring && d.CurrentPath == other && d.CurrentMashup == null && !ReferenceEquals(d.ActivePlayer, player) &&
                        (other.Steps[0].FileExists ? ReferenceEquals(d.ActivePlayer, d.Preview) : true) && !graph.Showing, d.ActivePlayerName);
                    panel.HandleKey(Key.Escape);
                }
                // Mashups switched off: the same path plays per step.
                d.PreferMashups = false;
                panel.Select(panel.Paths.ToList().IndexOf(path));
                panel.HandleKey(Key.Enter);
                bool off = d.CurrentMashup == null && !ReferenceEquals(d.ActivePlayer, player);
                panel.HandleKey(Key.Escape);
                d.PreferMashups = true;
                report.Check($"{L}: with PreferMashups off the path plays per step", off);
                panel.HandleKey(Key.Escape);
            }
            finally
            {
                player.MixFinished -= OnMix;
                d.FlyDuration = fly;
                d.EdgeGrowDuration = grow;
                d.PreferMashups = true;
                d.Exit();
            }
        }

        static bool Refresh(MelodyGraphPanel graph)
        {
            graph.Refresh();
            return true;
        }

        /// <summary>
        /// The light point blooms: the same frame rendered with and without the bloom volume; the
        /// ring around the point (outside its own halo's core) is clearly brighter with bloom.
        /// </summary>
        static void CheckBloom(Report report, SongGraphLoader loader, Camera cam, MelodyGraphPanel graph, MelodyGraphPanel.DotView light,
            string L, string? noBloomShot, int width, int height)
        {
            UnityEngine.Rendering.Volume? volume = graph.Lights?.Volume;
            UnityEngine.Rendering.Volume? sceneBloom = SceneLook.BloomVolume;
            if (!report.Check($"{L}: the light points' bloom volume exists", volume != null)) return;
            Vector2 at = graph.ScreenPoint(light.Position, width, height);
            float scale = loader.Hud.ScaleFor(width, height);
            Color[] with = CapturePixels(cam, loader, width, height);
            float weight = volume!.weight;
            Color[] without;
            float sceneWeight = sceneBloom != null ? sceneBloom.weight : 1f;
            Color[] sceneOff;
            try
            {
                volume.weight = 0f;
                without = CapturePixels(cam, loader, width, height);
                if (noBloomShot != null) Capture(cam, loader, noBloomShot, width, height, out _);
                volume.weight = weight;
                // The graph's own bloom switched off instead: the light's glow stays (its bloom is its own).
                if (sceneBloom != null) sceneBloom.weight = 0f;
                sceneOff = CapturePixels(cam, loader, width, height);
            }
            finally
            {
                volume.weight = weight;
                if (sceneBloom != null) sceneBloom.weight = sceneWeight;
            }
            double Ring(Color[] px, float r0, float r1)
            {
                double sum = 0;
                int n = 0;
                for (int y = (int)(at.y - r1); y <= (int)(at.y + r1); y++)
                    for (int x = (int)(at.x - r1); x <= (int)(at.x + r1); x++)
                    {
                        if (x < 0 || y < 0 || x >= width || y >= height) continue;
                        float d = Vector2.Distance(new Vector2(x + .5f, y + .5f), at);
                        if (d < r0 || d > r1) continue;
                        Color c = px[y * width + x];
                        sum += .2126 * c.r + .7152 * c.g + .0722 * c.b;
                        n++;
                    }
                return n > 0 ? sum / n : 0;
            }
            double coreWith = Ring(with, 0, 3 * scale), coreWithout = Ring(without, 0, 3 * scale);
            double ringWith = Ring(with, 24 * scale, 44 * scale), ringWithout = Ring(without, 24 * scale, 44 * scale), ringSceneOff = Ring(sceneOff, 24 * scale, 44 * scale);
            report.Number($"melody_light_core_luma_bloom_{L}", coreWith);
            report.Number($"melody_light_core_luma_nobloom_{L}", coreWithout);
            report.Number($"melody_light_ring_luma_bloom_{L}", ringWith);
            report.Number($"melody_light_ring_luma_nobloom_{L}", ringWithout);
            report.Number($"melody_light_ring_luma_scene_bloom_off_{L}", ringSceneOff);
            report.Check($"{L}: the light point blooms (URP bloom of its own camera): the ring 24–44 px around it is clearly brighter with that bloom than without; the core is white",
                ringWith - ringWithout > .04 && coreWith > .9,
                $"ring {ringWithout:0.000} → {ringWith:0.000}, core {coreWithout:0.000} → {coreWith:0.000} at ({at.x:0}, {at.y:0})");
            report.Check($"{L}: the light's bloom does not depend on the graph's bloom volume (it keeps glowing with that one off)",
                ringSceneOff - ringWithout > .04, $"ring with the graph's bloom off {ringSceneOff:0.000}");
        }

        static Color[] CapturePixels(Camera cam, SongGraphLoader loader, int width, int height, bool hdr = false)
        {
            RenderTexture rt = new(width, height, 24, hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32) { antiAliasing = hdr ? 1 : 4, name = "Bloom Capture" };
            rt.Create();
            RenderTexture? previousTarget = cam.targetTexture, previousActive = RenderTexture.active;
            Texture2D texture = new(width, height, hdr ? TextureFormat.RGBAHalf : TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)width / height;
                loader.RefreshView(cam);
                loader.Hud.RenderInto(cam);
                cam.Render();
                RenderTexture.active = rt;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                return texture.GetPixels();
            }
            finally
            {
                loader.Hud.RenderInto(null);
                cam.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(texture);
            }
        }

        // ------------------------------------------------------------------ synthetic catalog

        /// <summary>
        /// A mashups.json for up to three playable featured paths, laid out like the pipeline's chain
        /// (root phrase, ~20 s changeovers, 2-bar morphs, the last song's ending), with invented
        /// melodies and the axis progression; the mixes do not exist (the clock plays). Returns its path.
        /// </summary>
        static string WriteSyntheticMashups(PathCatalog paths)
        {
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-mashup-fixture");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, MashupCatalog.FileName);
            File.WriteAllText(file, SyntheticMashupsJson(paths), new UTF8Encoding(false));
            return file;
        }

        static string SyntheticMashupsJson(PathCatalog paths)
        {
            static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            StringBuilder b = new("{\"version\": 1, \"generated_at\": \"validation\", \"frame\": \"C major / A minor (relative normalization, as the pipeline)\", \"paths\": [");
            int written = 0;
            foreach (FeaturedPath p in paths.Paths.Where(p => p.IsPlayable && p.Steps.Count >= 2).OrderByDescending(p => p.Steps.Count).Take(3))
            {
                const int bpb = 4, phrase = 32;
                List<(string kind, int inst, int voc, int bars)> plan = new() { ("full", 0, 0, 8) };
                for (int k = 0; k + 1 < p.Steps.Count; k++)
                {
                    double bpm0 = p.Steps[k].Bpm > 0 ? p.Steps[k].Bpm : 120;
                    plan.Add(("changeover", k, k + 1, Math.Max(4, (int)Math.Round(20 / (bpb * 60 / bpm0)))));
                    plan.Add(("morph", k + 1, k + 1, 2));
                }
                plan.Add(("full", p.Steps.Count - 1, p.Steps.Count - 1, 8));
                StringBuilder segs = new(), beats = new();
                double t = 0;
                int count = 0;
                List<(double start, double end, string kind, int inst, int voc)> spans = new();
                foreach ((string kind, int inst, int voc, int bars) in plan)
                {
                    double start = t;
                    PathStep si = p.Steps[inst];
                    double b1 = si.Bpm > 0 ? si.Bpm : 120, b0 = kind == "morph" && p.Steps[inst - 1].Bpm > 0 ? p.Steps[inst - 1].Bpm : b1;
                    int nb = bars * bpb;
                    for (int j = 0; j < nb; j++)
                    {
                        if (beats.Length > 0) beats.Append(',');
                        beats.Append($"[{N(t)}, {count % phrase}]");
                        double u = (j + .5) / nb;
                        t += 60 / (b0 + (b1 - b0) * u * u * (3 - 2 * u));
                        count++;
                    }
                    t = Math.Round(t, 4);
                    spans.Add((start, t, kind, inst, voc));
                    if (segs.Length > 0) segs.Append(',');
                    string key = si.Key.Length > 0 ? si.Key : "C major";
                    string extra = kind == "changeover"
                        ? $"\"vocal_shift_semitones\": {(KeyText.TryParse(si.Key, out int a, out _) && KeyText.TryParse(p.Steps[voc].Key, out int c, out _) ? Morph.Wrap(a - c) : 0)}, " +
                          $"\"vocal_tempo_ratio\": {N(b1 / (p.Steps[voc].Bpm > 0 ? p.Steps[voc].Bpm : b1))}, \"chord_match\": 0.875, \"beat_error_ms\": 12.5"
                        : "\"vocal_shift_semitones\": null, \"vocal_tempo_ratio\": null, \"chord_match\": null, \"beat_error_ms\": null";
                    segs.Append($"{{\"start\": {N(start)}, \"end\": {N(t)}, \"kind\": \"{kind}\", \"instrumental\": {Js(si.WorkId)}, \"vocal\": {Js(p.Steps[voc].WorkId)}, " +
                                $"\"key\": {Js(key)}, \"bpm\": {N(b1)}, \"bpm_start\": {N(b0)}, {extra}}}");
                }
                StringBuilder songs = new();
                for (int k = 0; k < p.Steps.Count; k++)
                {
                    PathStep s = p.Steps[k];
                    var coIn = spans.FirstOrDefault(x => x.kind == "changeover" && x.voc == k);
                    var coOut = spans.FirstOrDefault(x => x.kind == "changeover" && x.inst == k);
                    double v0 = k == 0 ? 0 : coIn.start, v1 = k == p.Steps.Count - 1 ? t : coOut.start;
                    double i0 = spans.First(x => x.inst == k).start, i1 = k == p.Steps.Count - 1 ? t : coOut.end;
                    if (songs.Length > 0) songs.Append(',');
                    songs.Append($"{{\"work_id\": {Js(s.WorkId)}, \"title\": {Js(s.Title)}, \"artist\": {Js(s.Artist)}, \"year\": {s.Year}, \"step\": {k}, ")
                        .Append($"\"melody\": [{SyntheticMelody(k)}], \"chords\": [{SyntheticChords(k == 2 && p.Steps.Count > 3)}], ")
                        .Append($"\"vocal_audible\": [[{N(v0)}, {N(v1)}]], \"instrumental_audible\": [[{N(i0)}, {N(i1)}]]}}");
                }
                if (written++ > 0) b.Append(',');
                b.Append($"{{\"id\": {Js(p.Id)}, \"title\": {Js(p.Title)}, \"file\": {Js(p.Id + "/mix.mp3")}, \"seconds\": {N(t)}, \"beats_per_bar\": {bpb}, \"phrase_beats\": {phrase}, ")
                    .Append($"\"segments\": [{segs}], \"beats\": [{beats}], \"songs\": [{songs}]}}");
            }
            b.Append("]}");
            return b.ToString();

            static string Js(string s) => "\"" + J(s) + "\"";
        }

        /// <summary>An invented sung line (C major scale, two-bar phrases with a breath), every 1/8 beat.</summary>
        static string SyntheticMelody(int k)
        {
            System.Random rng = new(4100 + 37 * k);
            int[] notes = Enumerable.Range(55, 26).Where(p => new[] { 0, 2, 4, 5, 7, 9, 11 }.Contains(p % 12)).ToArray();
            int idx = Array.IndexOf(notes, 64) + (k % 3) - 1;
            StringBuilder b = new();
            double beat = 0;
            void Add(double at, string pitch)
            {
                if (b.Length > 0) b.Append(',');
                b.Append($"[{at.ToString("0.####", CultureInfo.InvariantCulture)}, {pitch}]");
            }
            while (beat < 32 - 1e-9)
            {
                double ph = beat % 8;
                if (ph >= 6.5)
                {
                    Add(beat, "null");
                    beat = (Math.Floor(beat / 8) + 1) * 8;
                    continue;
                }
                double[] durations = { .5, .5, 1, 1, 1, 1.5, 2 };
                double dur = Math.Min(durations[rng.Next(durations.Length)], 6.5 - ph);
                int[] moves = { -2, -1, -1, 0, 1, 1, 2, 3, -3 };
                idx = Math.Max(2, Math.Min(notes.Length - 3, idx + moves[rng.Next(moves.Length)]));
                for (double x = beat; x < beat + dur - 1e-9; x += .125)
                {
                    double vib = .14 * Math.Sin(2 * Math.PI * 2.5 * (x - beat)) * Math.Min(1, (x - beat) / .5);
                    Add(x, (notes[idx] + vib).ToString("0.###", CultureInfo.InvariantCulture));
                }
                beat += dur;
            }
            return b.ToString();
        }

        static string SyntheticChords(bool barPerChord)
        {
            (int root, string q, string roman)[] axis = { (0, "maj", "I"), (7, "maj", "V"), (9, "min", "vi"), (5, "maj", "IV") };
            int cells = barPerChord ? 8 : 4, len = 32 / cells;
            return string.Join(",", Enumerable.Range(0, cells).Select(i => $"[{i * len}, {i * len + len}, {axis[i % 4].root}, \"{axis[i % 4].q}\", \"{axis[i % 4].roman}\"]"));
        }
    }
}
