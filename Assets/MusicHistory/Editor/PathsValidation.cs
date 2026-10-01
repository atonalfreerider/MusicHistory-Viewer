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
    /// Featured paths (data/audio/renders/paths.json v2) in edit mode: the catalog contract and its
    /// mapping onto the graph, the recording-preview player's clock, glide readout and crossfade
    /// (clock only: edit mode plays no audio), the path tour, the Featured Paths panel (open,
    /// select, hover highlight, play, now-playing strip, Esc), screenshots, and label null-safety
    /// for teardown. Part of <see cref="Validation.Run"/>; also
    /// <c>-executeMethod MusicHistory.EditorTools.Validation.RunPaths</c> on its own.
    ///
    /// Catalogs checked: -validationPaths &lt;paths.json&gt; (e.g. a fixture), and the real
    /// data/audio/renders/paths.json when it exists. With neither, a small catalog built from the
    /// graph's own lineages (renders missing, so MIDI plays) still exercises the panel and the tour.
    /// Writes paths_button.png, paths_panel_idle.png, paths_panel.png (a hovered row) and
    /// paths_playing.png (+ _&lt;label&gt; per catalog). Then the mashup mixes and the melody graph
    /// (<see cref="ValidateMashups"/>, MashupValidation.cs).
    /// </summary>
    public static partial class Validation
    {
        [MenuItem("MusicHistory/Run Paths Validation (writes data/screens)")]
        public static void RunPathsFromMenu() => ExecutePaths(exitWhenDone: false);

        public static void RunPaths() => ExecutePaths(exitWhenDone: true);

        static void ExecutePaths(bool exitWhenDone)
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
                    ValidatePaths(report, loader!, cam, outDir, width, height);
                    loader!.Clear();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check("no exception", false, e.GetType().Name + ": " + e.Message + " @ " + (e.StackTrace ?? "").Replace("\n", " | "));
            }
            WriteJson(report, Path.Combine(outDir, "paths_validation.json"));
            Debug.Log($"[validation] {report.Checks.Count - report.Failures}/{report.Checks.Count} checks passed; report in {outDir}");
            if (exitWhenDone) EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }

        static void ValidatePaths(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            // TMP logs a warning whenever an Ellipsis-overflow text overflows, since the font has no
            // ellipsis glyph; the panel's texts never ask for it (UiKit truncates, titles shrink to fit).
            int overflowWarnings = 0;
            void OnLog(string message, string stack, LogType type)
            {
                if (message.Contains("character used for Ellipsis")) overflowWarnings++;
            }
            Application.logMessageReceived += OnLog;
            try
            {
                ValidatePathsAll(report, loader, cam, outDir, width, height);
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            report.Check("paths: no TextMesh Pro overflow warnings from the panel (no ellipsis glyph in the font)", overflowWarnings == 0,
                $"{overflowWarnings} warnings");
        }

        static void ValidatePathsAll(Report report, SongGraphLoader loader, Camera cam, string outDir, int width, int height)
        {
            ValidatePathsPure(report);
            string dbPath = PathArg("-validationLineageDb") ?? Path.Combine(SongGraphLoader.RepoRoot(), "data", "graph", "music_graph.db");
            if (!report.Check("paths: graph database exists", File.Exists(dbPath), dbPath)) return;
            loader.Build(dbPath);
            ValidateLabelTeardown(report, loader, cam);

            // A missing paths.json is tolerated: empty catalog, the panel says so, nothing throws.
            string missing = Path.Combine(Path.GetTempPath(), "musichistory-no-such-dir", "paths.json");
            PathCatalog none = PathCatalog.Load(missing);
            loader.UseCatalog(none, "validation: missing file");
            FeaturedPathsPanel? panel = loader.Paths;
            bool emptyOk = panel != null && !none.Loaded && none.Paths.Count == 0 && none.Status.StartsWith("missing");
            if (panel != null)
            {
                panel.HandleKey(Key.P);
                emptyOk &= panel.IsOpen && panel.ListText.Contains("No featured paths yet") && !panel.PlaySelected();
                panel.HandleKey(Key.Escape);
                emptyOk &= !panel.IsOpen;
            }
            report.Check("paths: a missing paths.json gives an empty list and an explanation, no exception", emptyOk, none.Status);

            List<(string label, PathCatalog catalog)> catalogs = new();
            string real = Path.Combine(SongGraphLoader.RepoRoot(), "data", "audio", "renders", PathCatalog.FileName);
            if (File.Exists(real)) catalogs.Add(("real", PathCatalog.Load(real)));
            string? explicitPaths = PathArg("-validationPaths");
            if (explicitPaths != null)
            {
                if (report.Check("paths: -validationPaths file exists", File.Exists(explicitPaths), explicitPaths))
                    catalogs.Add(("fixture", PathCatalog.Load(explicitPaths)));
            }
            if (catalogs.Count == 0)
            {
                report.Text("paths_catalogs", "no paths.json: using a catalog built from the graph (renders missing, MIDI plays)");
                catalogs.Add(("synthetic", SyntheticCatalog(loader.Data!)));
            }
            report.Text("paths_catalogs", string.Join(", ", catalogs.Select(c => $"{c.label}: {c.catalog.SourcePath}")));
            for (int i = 0; i < catalogs.Count; i++)
            {
                (string label, PathCatalog catalog) = catalogs[i];
                ValidateCatalog(report, loader, cam, catalog, label, outDir, width, height, canonicalShots: i == 0);
            }
            ValidateNonPathToursUseMidi(report, loader, catalogs[0].catalog);
            loader.Director.Exit();
            ValidateMashups(report, loader, cam, outDir, width, height);
            loader.Director.Exit();
        }

        // ------------------------------------------------------------------ pure

        static void ValidatePathsPure(Report report)
        {
            const string json = "﻿{ \"version\": 2, \"generated_at\": \"2026-09-30T12:00:00Z\", \"morph_bars\": 2, \"paths\": [" +
                                "{ \"id\": \"a\", \"title\": \"Caf\\u00e9 \\\"test\\\"\", \"subtitle\": \"1960 -> 1970 · 2 songs\", \"description\": \"d\", \"identity\": \"I-V-vi-IV\", \"seconds\": 60," +
                                "  \"steps\": [ { \"work_id\": \"Q1\", \"title\": \"One\", \"artist\": \"X\", \"year\": 1960, \"file\": \"a/01_Q1.mp3\", \"seconds\": 30, \"via\": null," +
                                "     \"start_key\": \"C major\", \"key\": \"C major\", \"start_semitones\": 0, \"start_bpm\": 120, \"bpm\": 120, \"morph_seconds\": 0, \"key_source\": \"audio\", \"bpm_source\": \"audio\" }," +
                                "   { \"work_id\": \"Q2\", \"title\": \"Two\", \"artist\": \"Y\", \"year\": 1970, \"file\": \"a/02_Q2.mp3\", \"seconds\": 30," +
                                "     \"via\": { \"identity\": \"I-V-vi-IV\", \"strong\": true, \"z\": 4.5, \"edge_kind\": \"tree\", \"family_size\": 12 }," +
                                "     \"start_key\": \"C major\", \"key\": \"D major\", \"start_semitones\": -2, \"start_bpm\": 120, \"bpm\": 90, \"morph_seconds\": 4, \"key_source\": \"audio\", \"bpm_source\": \"midi\" } ] } ] }";
            PathCatalog c = PathCatalog.Parse(json, Path.Combine(Path.GetTempPath(), "musichistory-validation-renders"), "inline");
            FeaturedPath? p = c.Paths.FirstOrDefault();
            PathStep? s = p?.Steps.ElementAtOrDefault(1);
            report.Check("paths.json v2 parses: paths, steps, via, keys, tempi, unicode escapes",
                c.Loaded && c.Version == 2 && Math.Abs(c.MorphBars - 2) < 1e-9 && p != null && p.Title == "Café \"test\"" && p.Steps.Count == 2 &&
                p.Steps[0].Via == null && s != null && s.Via != null && s.Via.Identity == "I-V-vi-IV" && s.Via.Strong && Math.Abs(s.Via.Z - 4.5) < 1e-9 &&
                s.Via.EdgeKind == "tree" && s.Via.FamilySize == 12 && s.StartKey == "C major" && s.Key == "D major" && s.StartSemitones == -2 &&
                Math.Abs(s.StartBpm - 120) < 1e-9 && Math.Abs(s.Bpm - 90) < 1e-9 && Math.Abs(s.MorphSeconds - 4) < 1e-9 &&
                s.KeySource == "audio" && s.BpmSource == "midi" && s.File == "a/02_Q2.mp3" && !s.FileExists && p.FirstYear == 1960 && p.LastYear == 1970,
                c.Status);
            report.Check("paths.json: missing render files are listed as problems, not thrown",
                c.Problems.Count(x => x.Contains("render missing")) == 2, string.Join(" | ", c.Problems));

            // The glide as the renders make it: smoothstep over morph_seconds of output time.
            bool glide = s != null && Math.Abs(s.SemitonesAt(0) + 2) < 1e-9 && Math.Abs(s.BpmAt(0) - 120) < 1e-9 &&
                         Math.Abs(s.SemitonesAt(2) + 1) < 1e-9 && Math.Abs(s.BpmAt(2) - 105) < 1e-9 &&
                         Math.Abs(s.SemitonesAt(4)) < 1e-9 && Math.Abs(s.BpmAt(4) - 90) < 1e-9 && Math.Abs(s.BpmAt(20) - 90) < 1e-9 &&
                         Math.Abs(p!.Steps[0].SemitonesAt(0)) < 1e-9 && Math.Abs(p.Steps[0].BpmAt(0) - 120) < 1e-9;
            double numeric = 0, h = 1e-4;
            for (double t = 0; t < 10; t += h) numeric += s!.BpmAt(t + h / 2) / 60 * h;
            report.Check("preview glide: smoothstep from start key/BPM to the recording's own over morph_seconds; beats = ∫BPM/60",
                glide && Math.Abs(numeric - s!.BeatsAt(10)) < 1e-3, $"beats(10 s) {s?.BeatsAt(10):0.0000} vs numeric {numeric:0.0000}");

            PathCatalog broken = PathCatalog.Parse("{\"version\": 1, \"paths\": [ {\"id\": \"x\", \"steps\": [ {\"work_id\": \"Q1\", \"file\": \"x.mp3\", \"seconds\": 3, \"bpm\": 100, " +
                                                  "\"via\": {\"identity\": \"a\"}, \"start_semitones\": 9} ] }, {\"id\": \"x\", \"title\": \"dup\", \"steps\": []} ] }", "");
            string[] expected = { "version 1", "no title", "the first step has a via", "outside [-6, 5]", "duplicate id", "no steps" };
            report.Check("paths.json: contract violations are reported (version, title, via, semitone range, duplicate id, no steps)",
                expected.All(e => broken.Problems.Any(x => x.Contains(e))), string.Join(" | ", broken.Problems));
            PathCatalog junk = PathCatalog.Parse("{ \"version\": 2, \"paths\": [ ", "");
            report.Check("paths.json: malformed JSON gives an empty catalog, no exception", !junk.Loaded && junk.Paths.Count == 0 && junk.Problems.Count == 1, junk.Status);
            report.Check("key names parse (C major, F# minor, Bb major, E♭ minor; junk rejected)",
                KeyText.TryParse("C major", out int pc, out bool minor) && pc == 0 && !minor &&
                KeyText.TryParse("F# minor", out pc, out minor) && pc == 6 && minor &&
                KeyText.TryParse("Bb major", out pc, out minor) && pc == 10 && !minor &&
                KeyText.TryParse("E♭ minor", out pc, out minor) && pc == 3 && minor &&
                !KeyText.TryParse("H dorian", out _, out _) && !KeyText.TryParse("", out _, out _));
            report.Check("durations read m:ss", PathCatalog.Clock(0) == "0:00" && PathCatalog.Clock(29.98) == "0:29" && PathCatalog.Clock(124) == "2:04");
        }

        // ------------------------------------------------------------------ teardown (edit mode)

        /// <summary>
        /// Every label and highlight path skips destroyed text boxes, bubbles and edges (scene unload
        /// and play-mode exit destroy objects in no fixed order). The play-mode half, with real
        /// OnDisable calls, is in <see cref="PathsPlayMode"/>.
        /// </summary>
        static void ValidateLabelTeardown(Report report, SongGraphLoader loader, Camera cam)
        {
            List<string> errors = new();
            void OnLog(string message, string stack, LogType type)
            {
                if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) errors.Add(message);
            }
            Application.logMessageReceived += OnLog;
            string? thrown = null;
            try
            {
                SongNode focus = loader.Nodes.Where(n => n.Incoming.Count > 0 && n.Outgoing.Count > 0).OrderByDescending(n => n.Outgoing.Count).First();
                SongNode other = loader.Nodes.First(n => n != focus && n.Outgoing.Count > 0);
                loader.Highlighter.ApplyFocus(focus, force: true);
                int boxes = 0;
                foreach (WorldLabel l in loader.Labels.Labels)
                {
                    if (l.Box == null) continue;
                    Object.DestroyImmediate(l.Box.gameObject);
                    boxes++;
                }
                loader.Highlighter.ApplyFocus(null, force: true);
                loader.Highlighter.ApplyFocus(other, force: true);
                loader.Labels.Refresh(cam);
                loader.Labels.ForceMeshUpdate();
                loader.SetAllLabels(true);
                loader.SetAllLabels(false);
                report.Number("teardown_label_boxes_destroyed", boxes, "0");
                // Songs and edges gone before the highlighter.
                Transform? graph = loader.transform.Find("Song Graph");
                bool found = graph != null;
                if (graph != null) Object.DestroyImmediate(graph.gameObject);
                loader.Highlighter.ApplyFocus(focus, force: true);
                loader.Highlighter.ApplyFocus(null, force: true);
                report.Check("teardown (edit mode): the graph root was destroyed for the check", found);
            }
            catch (Exception e)
            {
                thrown = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
            }
            report.Check("teardown (edit mode): labels, bubbles and edges destroyed first: focus changes, refresh and L raise nothing",
                thrown == null && errors.Count == 0, thrown ?? string.Join(" | ", errors.Take(3)));
            loader.Build(loader.ResolvedDbPath);
        }

        // ------------------------------------------------------------------ one catalog

        static void ValidateCatalog(Report report, SongGraphLoader loader, Camera cam, PathCatalog catalog, string label,
            string outDir, int width, int height, bool canonicalShots)
        {
            string L = $"paths[{label}]";
            // A fresh graph per catalog: every check starts from the overview.
            loader.Build(loader.ResolvedDbPath);
            loader.UseCatalog(catalog, "validation");
            // These checks are about the per-step previews: no mashup mix takes the paths over here
            // (MashupValidation checks the mixes).
            loader.UseMashups(MashupCatalog.Empty("validation: per-step previews"), "validation");
            SongGraphData data = loader.Data!;
            WalkthroughDirector director = loader.Director;
            report.Text($"paths_{label}_file", catalog.SourcePath);
            report.Text($"paths_{label}_generated_at", catalog.GeneratedAt);
            report.Number($"paths_{label}_count", catalog.Paths.Count, "0");
            report.Number($"paths_{label}_steps", catalog.Paths.Sum(p => p.Steps.Count), "0");
            bool synthetic = label == "synthetic";
            report.Check($"{L}: loads (version 2, at least one path)", catalog.Loaded && catalog.Version == 2 && catalog.Paths.Count > 0, catalog.Status);
            if (catalog.Paths.Count == 0) return;
            List<string> contract = catalog.Problems.Where(x => !synthetic || !x.Contains("render missing")).ToList();
            report.Check($"{L}: no contract problems (ids, titles, via, semitone range, files, durations)", contract.Count == 0,
                $"{contract.Count}: {string.Join(" | ", contract.Take(5))}");

            List<PathStep> steps = catalog.Paths.SelectMany(p => p.Steps).ToList();
            List<string> unmapped = steps.Where(s => s.NodeId <= 0 || data.Song(s.NodeId).WorkId != s.WorkId).Select(s => $"{s.WorkId} '{s.Title}'").ToList();
            report.Check($"{L}: every step's work_id maps to its song in the graph", unmapped.Count == 0 && catalog.PlayableCount == catalog.Paths.Count,
                $"{steps.Count - unmapped.Count}/{steps.Count} mapped; {string.Join(", ", unmapped.Take(5))}");
            List<string> titleMismatch = steps.Where(s => s.NodeId > 0 && (!SameText(data.Song(s.NodeId).Title, s.Title) || data.Song(s.NodeId).Year != s.Year))
                .Select(s => $"{s.WorkId}: '{s.Title}' {s.Year} vs '{data.Song(s.NodeId).Title}' {data.Song(s.NodeId).Year}").ToList();
            report.Check($"{L}: step titles and years agree with the graph", titleMismatch.Count == 0, string.Join(" | ", titleMismatch.Take(4)));

            // Each link is a real graph edge carrying the named identity.
            List<string> badLinks = new(), badKeys = new(), badTempo = new();
            double worstMorph = 0;
            foreach (FeaturedPath p in catalog.Paths)
            {
                GraphRoute route = loader.RouteFor(p);
                for (int i = 1; i < p.Steps.Count; i++)
                {
                    PathStep a = p.Steps[i - 1], b = p.Steps[i];
                    InfluenceEdge? e = route.StepEdges[i];
                    PathVia? via = b.Via;
                    if (e == null) badLinks.Add($"{p.Id} {i}→{i + 1}: no edge");
                    else if (via == null) badLinks.Add($"{p.Id} {i + 1}: no via");
                    else if (!synthetic && (e.Record.Evidence != via.Identity || e.IsTree != (via.EdgeKind == "tree") ||
                                            (data.IsIdentityLineage && e.Record.IsStrongMatch != via.Strong)))
                        badLinks.Add($"{p.Id} {i + 1}: via '{via.Identity}' {via.EdgeKind} strong {via.Strong} vs edge '{e.Record.Evidence}' {(e.IsTree ? "tree" : "secondary")} strong {e.Record.IsStrongMatch}");
                    if (b.Via != null && !string.Equals(b.StartKey, a.Key, StringComparison.OrdinalIgnoreCase)) badKeys.Add($"{p.Id} {i + 1}: start_key {b.StartKey} vs previous key {a.Key}");
                    if (KeyText.TryParse(a.Key, out int pa, out _) && KeyText.TryParse(b.Key, out int pb, out _) && Morph.Wrap(pa - pb) != b.StartSemitones)
                        badKeys.Add($"{p.Id} {i + 1}: start_semitones {b.StartSemitones} vs {a.Key} → {b.Key} = {Morph.Wrap(pa - pb)}");
                    if (a.Bpm > 0 && b.StartBpm > 0)
                    {
                        double octaves = Math.Log(b.StartBpm / a.Bpm, 2);
                        if (Math.Abs(octaves - Math.Round(octaves)) > .015) badTempo.Add($"{p.Id} {i + 1}: start_bpm {b.StartBpm} vs previous bpm {a.Bpm}");
                    }
                    if (b.NodeId > 0 && b.StartBpm > 0 && b.MorphSeconds > 0)
                    {
                        double bpb = data.Song(b.NodeId).BeatsPerBar > 0 ? data.Song(b.NodeId).BeatsPerBar : 4;
                        double expected = catalog.MorphBars * bpb * 60 / b.StartBpm;
                        worstMorph = Math.Max(worstMorph, Math.Abs(b.MorphSeconds - expected) / expected);
                    }
                }
            }
            report.Check($"{L}: consecutive songs share a graph edge that carries the step's 'via' identity (label, tree/secondary, strong)",
                badLinks.Count == 0, $"{badLinks.Count} bad; {string.Join(" | ", badLinks.Take(4))}");
            report.Check($"{L}: each step starts in the previous step's key (start_key, start_semitones)", badKeys.Count == 0,
                string.Join(" | ", badKeys.Take(4)));
            report.Check($"{L}: each step starts at the previous step's tempo (folded by octaves only)", badTempo.Count == 0, string.Join(" | ", badTempo.Take(4)));
            report.Number($"paths_{label}_morph_seconds_vs_bars_max_rel_dev", worstMorph, "0.000");

            if (!synthetic)
            {
                List<string> badFiles = new();
                foreach (PathStep s in steps)
                {
                    int rate = Mp3SampleRate(s.AbsoluteFile, out string why);
                    if (rate != 44100) badFiles.Add($"{s.File}: {why}");
                }
                report.Check($"{L}: every render is an MPEG layer III file at 44.1 kHz", badFiles.Count == 0, string.Join(" | ", badFiles.Take(4)));
                report.Check($"{L}: render files live under the renders folder", steps.All(s =>
                    s.AbsoluteFile.StartsWith(Path.GetFullPath(catalog.RendersDir), StringComparison.OrdinalIgnoreCase)));
            }

            FeaturedPath tourPath = catalog.Paths.Where(p => p.IsPlayable && p.Steps.Count >= 3)
                .OrderByDescending(p => p.Steps.Skip(1).Count(s => s.StartSemitones != 0 && Math.Abs(s.StartBpm - s.Bpm) > 1))
                .ThenByDescending(p => p.Steps.Count).FirstOrDefault() ?? catalog.Paths.First(p => p.IsPlayable);
            report.Text($"paths_{label}_tour", $"{tourPath.Id} ({tourPath.Steps.Count} steps)");
            ValidatePathTour(report, loader, tourPath, L, synthetic);
            ValidatePanel(report, loader, cam, catalog, L, label, outDir, width, height, canonicalShots, synthetic);
        }

        static bool SameText(string a, string b) =>
            string.Equals(string.Join(" ", a.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                string.Join(" ", b.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

        /// <summary>Drives a path tour in edit mode: the preview player keeps time on its main-thread clock.</summary>
        static void TickPath(WalkthroughDirector d, float dt)
        {
            if (d.Preview != null) d.Preview.Advance(dt);
            d.Tick(dt);
            if (d.ActivePlayer is SilentSongPlayer s) s.Advance(dt);
        }

        static void ValidatePathTour(Report report, SongGraphLoader loader, FeaturedPath path, string L, bool synthetic)
        {
            WalkthroughDirector d = loader.Director;
            PreviewSongPlayer preview = d.Preview!;
            List<int> started = new();
            Dictionary<int, int> finished = new();
            Dictionary<int, double> finishedAt = new();
            void OnStarted(SongClip c) => started.Add(c.NodeId);
            void OnFinished(SongClip c)
            {
                finished[c.NodeId] = finished.TryGetValue(c.NodeId, out int n) ? n + 1 : 1;
                finishedAt[c.NodeId] = preview.CurrentSeconds;
            }
            preview.Started += OnStarted;
            preview.Finished += OnFinished;
            try
            {
                bool renders = !synthetic && path.MissingRenders == 0;
                report.Check($"{L}: path tour starts", d.StartPathTour(path) && d.IsTouring && d.Mode == TourMode.Path && d.CurrentPath == path);
                report.Check($"{L}: the tour's steps are exactly the path's songs, in order",
                    d.Steps.SequenceEqual(path.Steps.Select(s => s.NodeId)), string.Join(",", d.Steps));
                if (!renders)
                {
                    report.Check($"{L}: a step without its render plays the MIDI (synth or silent clock), never the preview",
                        !ReferenceEquals(d.ActivePlayer, preview));
                    d.Exit();
                    return;
                }
                PathStep first = path.Steps[0];
                WalkthroughDirector.StepReadout r0 = d.Readout();
                report.Check($"{L}: step 1 plays its recording preview natively (no previous song, no glide)",
                    ReferenceEquals(d.ActivePlayer, preview) && d.PreviousClip == null && d.CurrentPlan.MorphBeats == 0 &&
                    r0.Recording && r0.InSeconds && r0.StartKey == first.Key && Math.Abs(preview.CurrentSemitones) < 1e-9 &&
                    Math.Abs(preview.CurrentBpm - first.Bpm) < 1e-9, $"{d.ActivePlayerName}; {r0.StartKey} → {r0.Key}");

                double dt = .05;
                bool orderOk = true, previewEveryStep = true, crossfades = true, glideOk = true, hudOk = true, secondsOk = true;
                double worstSemis = 0, worstBpm = 0;
                string detail = "";
                string hudSample = "";
                for (int i = 0; i < path.Steps.Count; i++)
                {
                    PathStep s = path.Steps[i];
                    if (d.StepIndex != i) orderOk = false;
                    previewEveryStep &= ReferenceEquals(d.ActivePlayer, preview) && preview.CurrentStep == s;
                    if (i > 0 && (d.PreviousClip == null || d.PreviousClip.NodeId != path.Steps[i - 1].NodeId)) orderOk = false;
                    // Sample the glide at 0, M/2, M and M + 1 s against the render's metadata.
                    double[] probes = s.Glides ? new[] { 0, s.MorphSeconds / 2, s.MorphSeconds, s.MorphSeconds + 1 } : new[] { 0.0, 2.0 };
                    int probe = 0;
                    double guard = 0;
                    while (d.IsTouring && d.StepIndex == i && !d.TourComplete && guard < 400)
                    {
                        double t = preview.CurrentSeconds;
                        if (probe < probes.Length && t >= probes[probe] - 1e-9)
                        {
                            double semis = s.Glides ? s.StartSemitones * (1 - Smooth(t / s.MorphSeconds)) : 0;
                            double bpm = s.Glides ? s.StartBpm + (s.Bpm - s.StartBpm) * Smooth(t / s.MorphSeconds) : s.Bpm;
                            worstSemis = Math.Max(worstSemis, Math.Abs(preview.CurrentSemitones - semis));
                            worstBpm = Math.Max(worstBpm, Math.Abs(preview.CurrentBpm - bpm));
                            WalkthroughDirector.StepReadout r = d.Readout();
                            glideOk &= Math.Abs(r.NowSemitones - semis) < 1e-6 && Math.Abs(r.NowBpm - bpm) < 1e-6 &&
                                       r.Key == s.Key && r.StartKey == (s.Via != null && s.Glides ? s.StartKey : s.Key);
                            secondsOk &= r.InSeconds && Math.Abs(r.Position - t) < 1e-9 && Math.Abs(r.Length - s.Seconds) < 1e-6;
                            if (i == 1 && probe == 1)
                            {
                                string hud = d.HudText;
                                hudSample = FirstLines(hud, 4);
                                hudOk &= hud.Contains($"Key {s.StartKey} → {s.Key}") &&
                                         hud.Contains($"BPM {s.StartBpm.ToString("0.#", CultureInfo.InvariantCulture)} → {s.Bpm.ToString("0.#", CultureInfo.InvariantCulture)}") &&
                                         hud.Contains("recording preview") && hud.Contains($"step 2/{path.Steps.Count}") &&
                                         hud.Contains($"/ {PathCatalog.Clock(s.Seconds)}") && s.Via != null && hud.Contains("via " + s.Via.Identity);
                            }
                            probe++;
                        }
                        TickPath(d, (float)dt);
                        guard += dt;
                    }
                    if (i + 1 < path.Steps.Count)
                    {
                        // A natural advance crossfades: the outgoing clip is still sounding under the new one.
                        bool tail = preview.TailActive;
                        crossfades &= tail;
                        detail += $"{i + 1}→{i + 2}: tail {(tail ? "yes" : "no")}; ";
                    }
                }
                // Let the last clip play out, then some more: nothing fires twice.
                for (int k = 0; k < 80; k++) TickPath(d, (float)dt);
                report.Check($"{L}: the tour plays every step in order, each after the step before (PreviousClip)", orderOk && d.TourComplete,
                    $"complete {d.TourComplete}; started {string.Join(",", started)}");
                report.Check($"{L}: every step plays its recording preview", previewEveryStep);
                report.Check($"{L}: Started and Finished fire exactly once per step (the crossfade does not double-fire)",
                    started.SequenceEqual(path.Steps.Select(s => s.NodeId)) && path.Steps.All(s => finished.TryGetValue(s.NodeId, out int n) && n == 1),
                    string.Join(", ", path.Steps.Select(s => $"{s.NodeId}:{(finished.TryGetValue(s.NodeId, out int n) ? n : 0)}")));
                report.Check($"{L}: Finished fires {preview.CrossfadeSeconds:0.#} s before the end, so the next clip crossfades in",
                    path.Steps.All(s => finishedAt.TryGetValue(s.NodeId, out double at) && Math.Abs(s.Seconds - preview.CrossfadeSeconds - at) <= dt + 1e-6) && crossfades,
                    detail + string.Join(", ", path.Steps.Select(s => finishedAt.TryGetValue(s.NodeId, out double at) ? $"{at:0.00}/{s.Seconds:0.00}" : "-")));
                report.Check($"{L}: key/BPM readout follows the render's metadata (smoothstep over morph_seconds)", glideOk && worstSemis < 1e-6 && worstBpm < 1e-6,
                    $"max error {worstSemis:0.000000} st, {worstBpm:0.000000} BPM");
                report.Check($"{L}: progress and length are the render's seconds, not beats", secondsOk);
                report.Check($"{L}: HUD reads 'Key <start> → <key> · BPM a → b', via <identity>, recording preview, m:ss", hudOk, hudSample);

                // Next mid-clip cuts (short fade, no Finished); Back plays the step before, after the song just heard.
                d.StartPathTour(path);
                for (int k = 0; k < 40; k++) TickPath(d, (float)dt);
                int finishedBefore = finished.Values.Sum();
                int leaving = d.CurrentClip!.NodeId;
                d.Next();
                bool cut = preview.TailActive && d.StepIndex == 1 && finished.Values.Sum() == finishedBefore;
                for (int k = 0; k < 20; k++) TickPath(d, (float)dt);
                cut &= !preview.TailActive && finished.Values.Sum() == finishedBefore;
                report.Check($"{L}: Next mid-clip cuts with a short fade and raises no Finished for the cut clip", cut && d.PreviousClip?.NodeId == leaving);
                leaving = d.CurrentClip!.NodeId;
                d.Previous();
                report.Check($"{L}: Back plays the previous step's preview after the song just heard",
                    d.StepIndex == 0 && ReferenceEquals(d.ActivePlayer, preview) && d.PreviousClip?.NodeId == leaving && preview.CurrentStep == path.Steps[0]);
                d.TogglePause();
                double at0 = preview.CurrentSeconds;
                for (int k = 0; k < 20; k++) TickPath(d, (float)dt);
                bool paused = preview.Paused && Math.Abs(preview.CurrentSeconds - at0) < 1e-9 && d.StallSeconds == 0;
                d.TogglePause();
                for (int k = 0; k < 4; k++) TickPath(d, (float)dt);
                report.Check($"{L}: Space pauses the preview clock and resumes it", paused && !preview.Paused && preview.CurrentSeconds > at0);

                // Compare (C) plays the MIDI; a step whose render is missing plays the MIDI.
                d.ToggleApplesToApples();
                bool compare = !ReferenceEquals(d.ActivePlayer, preview) && !preview.IsPlaying;
                d.ToggleApplesToApples();
                report.Check($"{L}: C (compare in C / 120 BPM) plays the MIDI, then the preview again", compare && ReferenceEquals(d.ActivePlayer, preview));
                PathStep second = path.Steps[1];
                bool saved = second.FileExists;
                second.FileExists = false;
                try
                {
                    d.GoTo(1);
                    report.Check($"{L}: a step whose render is missing plays the MIDI (synth or silent clock)",
                        !ReferenceEquals(d.ActivePlayer, preview) && d.ActivePlayer != null, d.ActivePlayerName);
                }
                finally
                {
                    second.FileExists = saved;
                }
                d.GoTo(1);
                d.Exit();
                report.Check($"{L}: Esc stops the preview and leaves the tour", !d.IsTouring && !preview.IsPlaying && preview.CurrentStep == null &&
                    d.Mode != TourMode.Path);
            }
            finally
            {
                preview.Started -= OnStarted;
                preview.Finished -= OnFinished;
                d.Exit();
            }
        }

        static double Smooth(double u)
        {
            u = Math.Max(0, Math.Min(1, u));
            return u * u * (3 - 2 * u);
        }

        // ------------------------------------------------------------------ panel

        static void ValidatePanel(Report report, SongGraphLoader loader, Camera cam, PathCatalog catalog, string L, string label,
            string outDir, int width, int height, bool canonicalShots, bool synthetic)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            GraphHud hud = loader.Hud;
            string Shot(string name) => Path.Combine(outDir, canonicalShots ? $"{name}.png" : $"{name}_{label}.png");

            loader.Highlighter.ApplyFocus(null, force: true);
            loader.FrameOverview(cam);
            report.Check($"{L}: panel starts closed with the 'Featured paths' button showing", panel.State == FeaturedPathsPanel.PanelState.Closed &&
                panel.ButtonRect != null && panel.ButtonRect.gameObject.activeInHierarchy && !panel.ListVisible && !panel.StripVisible && hud.LegendVisible);
            report.Check($"{L}: the canvas takes clicks (GraphicRaycaster) through an EventSystem with InputSystemUIInputModule, no UI navigation",
                hud.Canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null && panel.Events != null &&
                panel.Events.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() != null &&
                panel.Events.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>() == null && !panel.Events.sendNavigationEvents);
            if (canonicalShots)
            {
                string closed = Path.Combine(outDir, "paths_button.png");
                Capture(cam, loader, closed, width, height, out _);
                report.Check($"{L}: paths_button.png written", File.Exists(closed));
            }

            // P opens the list beside the reframed graph; the legend and the walkthrough bar make way.
            report.Check($"{L}: P opens the list", panel.HandleKey(Key.P) && panel.IsOpen && panel.ListVisible && !panel.StripVisible);
            report.Check($"{L}: the list replaces the legend and the idle walkthrough bar (no overlap)", !hud.LegendVisible && !hud.TourVisible);
            string text = panel.ListText;
            List<string> missingText = new();
            foreach (FeaturedPath p in catalog.Paths.Take(panel.Rows.Count(r => r.gameObject.activeSelf)))
            {
                string duration = PathCatalog.Clock(p.Seconds > 0 ? p.Seconds : p.Steps.Sum(s => s.Seconds));
                foreach (string part in new[] { p.Title, duration, $"{p.Steps.Count} songs", $"{p.FirstYear}–{p.LastYear}" })
                    if (!text.Contains(part)) missingText.Add($"{p.Id}: '{part}'");
            }
            report.Check($"{L}: each row shows title, subtitle, duration, song count, era and a song strip", missingText.Count == 0 &&
                panel.Rows.Where(r => r.gameObject.activeSelf).All(r => r.Dots.Count == catalog.Paths[r.PathIndex].Steps.Count && r.Subtitle.text.Length > 0),
                string.Join(" | ", missingText.Take(4)));
            // Titles show whole: the font has no ellipsis glyph, so a long one shrinks a little instead of being cut.
            List<string> cut = new(), shrunk = new();
            foreach (RowView r in panel.Rows)
            {
                float need = r.Title.GetPreferredValues(r.Title.text, 100000f, 0f).x;
                if (need > r.TitleWidth + .5f || r.Title.fontSize < FeaturedPathsPanel.MinTitleSize - 1e-3f)
                    cut.Add($"'{r.Title.text}' needs {need:0} px of {r.TitleWidth:0} at {r.Title.fontSize:0.##}");
                else if (r.Title.fontSize < FeaturedPathsPanel.TitleSize) shrunk.Add($"'{r.Title.text}' {r.Title.fontSize:0.##} px");
            }
            report.Check($"{L}: every row title shows whole (long ones shrink to fit, not below {FeaturedPathsPanel.MinTitleSize:0} px)", cut.Count == 0,
                cut.Count > 0 ? string.Join(" | ", cut.Take(3)) : shrunk.Count > 0 ? "shrunk: " + string.Join(", ", shrunk) : "none shrunk");
            report.Check($"{L}: a row never repeats its song count (the count column hides when the subtitle says it)",
                panel.Rows.All(r => !(r.Count.gameObject.activeSelf && r.Subtitle.text.Contains(r.Count.text))));
            // Hovering rows never reflows the list (a hovered row that vanished would flicker under the pointer).
            int rowsShown = panel.VisibleRowCount;
            Vector2 playAt = ((RectTransform)panel.PlayButton!.transform).anchoredPosition, listSize = panel.ListRect!.sizeDelta;
            bool steady = true;
            string reflow = "";
            for (int i = 0; i < catalog.Paths.Count; i++)
            {
                if (!panel.Rows[i].gameObject.activeSelf) continue;
                panel.HoverRow(i);
                Vector2 playNow = ((RectTransform)panel.PlayButton.transform).anchoredPosition;
                if (panel.VisibleRowCount != rowsShown || (playNow - playAt).sqrMagnitude > .01f || (panel.ListRect.sizeDelta - listSize).sqrMagnitude > .01f)
                {
                    steady = false;
                    reflow += $"row {i + 1}: {panel.VisibleRowCount} rows, Play at {playNow.y:0}; ";
                }
            }
            panel.HoverRow(-1);
            report.Check($"{L}: hovering any row keeps the rows, the list size and the Play button in place", steady,
                reflow.Length > 0 ? reflow : $"{rowsShown} rows, Play at y {-playAt.y:0}, list {listSize.y:0} px");

            // Selection lights the route: its songs and edges glow, everything else dims.
            bool routes = true;
            string routeDetail = "";
            int count = Math.Min(3, catalog.Paths.Count);
            for (int i = 0; i < count; i++)
            {
                panel.HandleKey(Key.Digit1 + i);
                FeaturedPath p = catalog.Paths[i];
                GraphRoute route = loader.RouteFor(p);
                HashSet<SongNode> on = new(route.Nodes);
                HashSet<InfluenceEdge> lit = new(route.Edges);
                bool ok = panel.Selected == i && loader.Highlighter.FocusRoute == route && route.Nodes.Count == p.Steps.Count &&
                          loader.RouteLine != null && loader.RouteLine.Visible && loader.RouteLine.Route == route &&
                          loader.Nodes.All(n => on.Contains(n) ? n.State == BubbleState.Focus : n.State == BubbleState.Dimmed) &&
                          loader.Edges.All(e => lit.Contains(e) ? e.State == EdgeState.Highlight && e.Shown : e.State == EdgeState.Dimmed) &&
                          route.Nodes.All(n => n.Label != null && n.Label.Visible);
                routes &= ok;
                routeDetail += $"{p.Id}: {route.Nodes.Count} songs, {route.Edges.Count} edges{(ok ? "" : " WRONG")}; ";
            }
            report.Check($"{L}: 1–{count} select a path and light its route (songs, edges, labels, a glowing line); the rest dims", routes, routeDetail);            int before = panel.Selected;
            panel.HandleKey(Key.DownArrow);
            bool down = panel.Selected == (before + 1) % catalog.Paths.Count;
            panel.HandleKey(Key.UpArrow);
            report.Check($"{L}: ↑↓ move the selection", down && panel.Selected == before);
            int hover = catalog.Paths.Count > 1 ? (panel.Selected + 1) % catalog.Paths.Count : 0;
            panel.HoverRow(hover);
            bool hovered = loader.Highlighter.FocusRoute == loader.RouteFor(catalog.Paths[hover]) && panel.Hovered == hover;
            panel.HoverRow(-1);
            report.Check($"{L}: hovering a row previews its route; leaving returns to the selection",
                hovered && loader.Highlighter.FocusRoute == loader.RouteFor(catalog.Paths[panel.Selected]));

            // The graph sits beside the list: the selected route is on screen, right of the panel.
            panel.Select(0);
            loader.RefreshView(cam);
            IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
            Rect listRect = hud.ScreenRect(panel.ListRect!, width, height);
            GraphRoute selectedRoute = loader.RouteFor(catalog.Paths[0]);
            bool beside = selectedRoute.Nodes.All(n =>
            {
                Vector3 v = cam.WorldToViewportPoint(n.transform.position);
                return v.z > 0 && v.x * width > listRect.xMax && v.x < 1 && v.y > 0 && v.y < 1;
            });
            report.Check($"{L}: the open list frames the graph beside it (the selected route is on screen, clear of the list)", beside,
                $"list right edge {listRect.xMax:0} px");
            report.Check($"{L}: the list fits the screen", listRect.yMin >= -1 && listRect.yMax <= height + 1 && listRect.xMin >= 0,
                $"{listRect}");
            report.Check($"{L}: HUD panels never overlap (list, button, info)", NoOverlap(rects), string.Join(" ", rects.Select(x => $"[{x.xMin:0},{x.yMin:0} {x.width:0}x{x.height:0}]")));
            // The list at rest: nothing hovered, the selected (first) path's route rests on the graph.
            string idle = Shot("paths_panel_idle");
            Capture(cam, loader, idle, width, height, out _);
            report.Check($"{L}: {Path.GetFileName(idle)} written (list at rest, the selected route lit)",
                File.Exists(idle) && panel.Hovered < 0 && loader.Highlighter.FocusRoute == selectedRoute);
            panel.HoverRow(catalog.Paths.Count > 1 ? 1 : 0);
            string shot = Shot("paths_panel");
            Capture(cam, loader, shot, width, height, out _);
            report.Check($"{L}: {Path.GetFileName(shot)} written", File.Exists(shot));
            CheckLabels(report, loader, $"{label} paths list");
            panel.HoverRow(-1);

            // Enter plays the selected path: the list collapses into the now-playing strip.
            FeaturedPath play = catalog.Paths.FirstOrDefault(p => p.IsPlayable && p.Steps.Count >= 2 && p.Steps[1].StartSemitones != 0)
                                ?? catalog.Paths.FirstOrDefault(p => p.IsPlayable && p.Steps.Count >= 2) ?? catalog.Paths[0];
            panel.Select(catalog.Paths.IndexOf(play));
            report.Check($"{L}: Enter plays the selected path; the list collapses into the now-playing strip",
                panel.HandleKey(Key.Enter) && d.IsTouring && d.Mode == TourMode.Path && d.CurrentPath == play &&
                panel.State == FeaturedPathsPanel.PanelState.Playing && panel.StripVisible && !panel.ListVisible && HudRefreshed(hud) && !hud.LegendVisible &&
                hud.PathTitleVisible && hud.PathTitleText == play.DisplayName && !hud.TourVisible);
            // Into step 2, mid-glide (a short flight, so the camera has arrived for the picture).
            PathStep step2 = play.Steps[1];
            float fly = d.FlyDuration, grow = d.EdgeGrowDuration;
            d.FlyDuration = .4f;
            d.EdgeGrowDuration = .3f;
            for (int guard = 0; guard < 2000 && d.StepIndex < 1; guard++) TickPath(d, .05f);
            double target = step2.Glides ? Math.Max(.6, step2.MorphSeconds * .45) : 1.5;
            for (int guard = 0; guard < 400 && d.StepIndex == 1 && (d.Preview!.CurrentSeconds < target || d.Flying); guard++) TickPath(d, .05f);
            d.FlyDuration = fly;
            d.EdgeGrowDuration = grow;
            panel.RefreshNowPlaying();
            string strip = panel.NowPlayingText;
            bool recording = !synthetic && play.MissingRenders == 0;
            bool stripOk = strip.Contains(GraphHud.Esc(play.Title)) && strip.Contains($"STEP 2 / {play.Steps.Count}") &&
                           play.Steps.All(s => strip.Contains(GraphHud.Esc(s.Title))) &&
                           (step2.Via == null || strip.Contains(GraphHud.Esc(step2.Via.Identity))) &&
                           (step2.Via == null || !step2.Via.Strong || strip.Contains("STRONG MATCH")) &&
                           (!recording || strip.Contains("RECORDING PREVIEW")) &&
                           strip.Contains("Key") && strip.Contains("BPM") && strip.Contains(" / ");
            report.Check($"{L}: the strip shows the path, step i/n with every song, 'via <identity>' (+ strong match), the key/BPM glide, the source badge and the time",
                stripOk, strip.Replace("\n", " / "));
            if (recording && step2.Glides && (step2.StartSemitones != 0 || Math.Abs(step2.StartBpm - step2.Bpm) > .05))
                report.Check($"{L}: mid-glide the strip reads the start → target key and BPM with the live values",
                    strip.Contains("gliding") && strip.Contains("(now ") &&
                    (step2.StartKey == step2.Key || strip.Contains($"{GraphHud.Esc(step2.StartKey)} <color={GraphHud.Muted}>→</color> <b>{GraphHud.Esc(step2.Key)}</b>")) &&
                    (Math.Abs(step2.StartBpm - step2.Bpm) < .05 || strip.Contains($"{step2.StartBpm.ToString("0.#", CultureInfo.InvariantCulture)} <color={GraphHud.Muted}>→</color> <b>{step2.Bpm.ToString("0.#", CultureInfo.InvariantCulture)}</b>")),
                    FirstLines(strip, 5));
            loader.RefreshView(cam);
            CheckPathTitle(report, L, loader, play, width, height);
            CheckHighlightedBubbles(report, L, loader, cam, loader.Nodes.Where(n => n.State == BubbleState.Focus).ToList(), width, height);
            rects = hud.PanelScreenRects(width, height);
            report.Check($"{L}: while playing, strip, path name, wheel, info and button never overlap", NoOverlap(rects),
                string.Join(" ", rects.Select(x => $"[{x.xMin:0},{x.yMin:0} {x.width:0}x{x.height:0}]")));
            shot = Shot("paths_playing");
            Capture(cam, loader, shot, width, height, out _);
            report.Check($"{L}: {Path.GetFileName(shot)} written", File.Exists(shot));
            CheckLabels(report, loader, $"{label} path tour");

            // The strip's buttons drive the tour; Esc goes back to the list, Esc again closes it.
            int at = d.StepIndex;
            panel.NextButton!.onClick.Invoke();
            bool next = d.StepIndex == Math.Min(at + 1, play.Steps.Count - 1);
            panel.PrevButton!.onClick.Invoke();
            bool prev = d.StepIndex == at;
            panel.PauseButton!.onClick.Invoke();
            bool paused = d.ActivePlayer != null && d.ActivePlayer.Paused;
            panel.PauseButton.onClick.Invoke();
            report.Check($"{L}: strip buttons Next, Prev and Pause drive the tour", next && prev && paused && d.ActivePlayer != null && !d.ActivePlayer.Paused);
            report.Check($"{L}: Esc during a path stops it and returns to the list", panel.HandleKey(Key.Escape) && !d.IsTouring && panel.IsOpen && panel.ListVisible &&
                !panel.StripVisible && !(d.Preview?.IsPlaying ?? false) && HudRefreshed(hud) && !hud.PathTitleVisible);
            CheckBubblesRestored(report, L, loader);
            panel.StopButton!.onClick.Invoke();   // no tour: harmless
            report.Check($"{L}: Esc on the list closes it; legend and walkthrough bar come back",
                panel.HandleKey(Key.Escape) && panel.State == FeaturedPathsPanel.PanelState.Closed && hud.LegendVisible && hud.TourVisible &&
                loader.Highlighter.RoutePreview == null && loader.Highlighter.FocusRoute == null);
            // Clicking a row plays it too; the Paths button toggles the list.
            panel.PathsButton!.onClick.Invoke();
            bool opened = panel.IsOpen;
            panel.ClickRow(0);
            bool clicked = d.IsTouring && d.CurrentPath == catalog.Paths[0];
            panel.PathsButton.onClick.Invoke();   // while playing: stop, back to the list
            bool back = !d.IsTouring && panel.IsOpen;
            panel.PathsButton.onClick.Invoke();
            report.Check($"{L}: the Paths button toggles the list; a row click plays its path", opened && clicked && back && panel.State == FeaturedPathsPanel.PanelState.Closed);
        }

        /// <summary>The HUD brought up to date (edit mode has no LateUpdate); always true, for use inside a check.</summary>
        static bool HudRefreshed(GraphHud hud)
        {
            hud.ForceUpdate();
            return true;
        }

        static bool NoOverlap(IReadOnlyList<Rect> rects)
        {
            for (int i = 0; i < rects.Count; i++)
                for (int j = i + 1; j < rects.Count; j++)
                    if (rects[i].Overlaps(rects[j])) return false;
            return true;
        }

        /// <summary>Lineage and family tours never use recording previews, even for songs that have renders.</summary>
        static void ValidateNonPathToursUseMidi(Report report, SongGraphLoader loader, PathCatalog catalog)
        {
            loader.UseCatalog(catalog, "validation");
            WalkthroughDirector d = loader.Director;
            FeaturedPath? p = catalog.Paths.FirstOrDefault(x => x.IsPlayable && x.MissingRenders == 0);
            if (p == null) return;
            bool ok = true;
            int stepsSeen = 0;
            foreach (TourMode mode in new[] { TourMode.Lineage, TourMode.Chronological })
            {
                if (!d.StartTour(mode, loader.NodeById(p.Steps[^1].NodeId))) continue;
                for (int i = 0; i < Math.Min(4, d.Steps.Count); i++, stepsSeen++)
                {
                    ok &= !ReferenceEquals(d.ActivePlayer, d.Preview) && d.Mode == mode;
                    d.Next();
                }
                d.Exit();
            }
            report.Check("paths: lineage and chronological tours through path songs play MIDI, never the recording previews", ok && stepsSeen > 0,
                $"{stepsSeen} steps checked");
        }

        /// <summary>A catalog from the graph's own lineages (renders missing), when no paths.json exists.</summary>
        static PathCatalog SyntheticCatalog(SongGraphData data)
        {
            StringBuilder b = new("{\"version\": 2, \"generated_at\": \"validation\", \"morph_bars\": 2, \"paths\": [");
            List<SongRecord> ends = data.Songs.Where(s => s.TreeDepth >= 2).OrderByDescending(s => s.Descendants).ThenBy(s => s.NodeId).Take(3).ToList();
            for (int k = 0; k < ends.Count; k++)
            {
                List<SongRecord> chain = new();
                for (SongRecord? s = ends[k]; s != null && chain.Count < 4; s = s.TreeParent is int t ? data.Song(t) : null) chain.Add(s);
                chain.Reverse();
                if (k > 0) b.Append(',');
                b.Append($"{{\"id\": \"lineage-{k + 1}\", \"title\": {Js(chain[^1].Title)}, \"subtitle\": \"{chain[0].Year} -> {chain[^1].Year} · {chain.Count} songs\", ")
                    .Append($"\"description\": \"A lineage of the graph (validation).\", \"identity\": \"lineage\", \"seconds\": {chain.Count * 30}, \"steps\": [");
                for (int i = 0; i < chain.Count; i++)
                {
                    SongRecord s = chain[i];
                    if (i > 0) b.Append(',');
                    string via = i == 0 ? "null" : $"{{\"identity\": {Js(data.Edges.FirstOrDefault(e => e.Target == s.NodeId && e.IsTree)?.Evidence ?? "")}, \"strong\": false, \"z\": 0, \"edge_kind\": \"tree\", \"family_size\": 2}}";
                    string prevKey = i == 0 ? s.KeyName : chain[i - 1].KeyName;
                    b.Append($"{{\"work_id\": {Js(s.WorkId)}, \"title\": {Js(s.Title)}, \"artist\": {Js(s.Artist)}, \"year\": {s.Year}, \"file\": \"missing/{i + 1:00}_{s.WorkId}.mp3\", ")
                        .Append($"\"seconds\": 30, \"via\": {via}, \"start_key\": {Js(prevKey)}, \"key\": {Js(s.KeyName)}, \"start_semitones\": {(i == 0 ? 0 : Morph.Wrap(chain[i - 1].TonicPc - s.TonicPc))}, ")
                        .Append($"\"start_bpm\": {Inv(i == 0 ? s.NativeBpm : chain[i - 1].NativeBpm)}, \"bpm\": {Inv(s.NativeBpm)}, \"morph_seconds\": {(i == 0 ? "0" : "3")}, \"key_source\": \"midi\", \"bpm_source\": \"midi\"}}");
                }
                b.Append("]}");
            }
            b.Append("]}");
            return PathCatalog.Parse(b.ToString(), Path.Combine(Path.GetTempPath(), "musichistory-synthetic-renders"), "synthetic (graph lineages)");

            static string Js(string s) => "\"" + J(s) + "\"";
            static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Sample rate of the first MPEG audio frame (after an ID3v2 tag); 0 when none is found.</summary>
        static int Mp3SampleRate(string path, out string why)
        {
            why = "";
            try
            {
                using FileStream f = File.OpenRead(path);
                byte[] head = new byte[10];
                long start = 0;
                if (f.Read(head, 0, 10) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                {
                    int size = (head[6] & 0x7f) << 21 | (head[7] & 0x7f) << 14 | (head[8] & 0x7f) << 7 | (head[9] & 0x7f);
                    start = 10 + size + ((head[5] & 0x10) != 0 ? 10 : 0);
                }
                f.Seek(start, SeekOrigin.Begin);
                byte[] buf = new byte[16384];
                int n = f.Read(buf, 0, buf.Length);
                for (int i = 0; i + 3 < n; i++)
                {
                    if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                    int version = (buf[i + 1] >> 3) & 3, layer = (buf[i + 1] >> 1) & 3;
                    int bitrate = buf[i + 2] >> 4, rate = (buf[i + 2] >> 2) & 3;
                    if (version == 1 || layer != 1 || bitrate == 0 || bitrate == 15 || rate == 3) continue;
                    int[] table = version == 3 ? new[] { 44100, 48000, 32000 } : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
                    why = $"MPEG{(version == 3 ? "1" : version == 2 ? "2" : "2.5")} layer III {table[rate]} Hz";
                    return table[rate];
                }
                why = "no MPEG layer III frame";
                return 0;
            }
            catch (Exception e)
            {
                why = e.Message;
                return 0;
            }
        }
    }
}
