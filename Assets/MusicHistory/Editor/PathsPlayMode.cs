#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Play-mode checks for featured paths and teardown, for
    /// <c>Unity.exe -batchmode -projectPath unity -executeMethod MusicHistory.EditorTools.PathsPlayMode.Run [-musicHistoryPaths &lt;paths.json&gt;] -logFile ...</c>
    /// (no -quit, no -nographics). The scene builds itself through SongGraphLoader.Start; then:
    ///
    /// - the EventSystem / InputSystemUIInputModule / GraphicRaycaster set-up, a pointer raycast
    ///   onto a list row, pointer enter (route preview) and click (plays the path);
    /// - the first render decoded by UnityWebRequest (44.1 kHz, length = paths.json seconds) and
    ///   playing, after a forced load time-out (the clock starts, the recording joins at its
    ///   position), Pause and Stop fading;
    /// - a whole path on the main-thread clock (the no-audio-device fallback) at time scale 6:
    ///   steps in order, Started/Finished once each, crossfades at natural advances;
    /// - teardown in varied orders (labels first, graph first, songs first, scene loads with a focused
    ///   song or a playing path, play-mode exit) with no exception or error logged.
    ///
    /// Previews play at volume 0 here (the clocks still run). Writes data/screens/paths_playmode.json;
    /// exit code 0 when every check passes.
    ///
    /// <see cref="RunFullPath"/> instead plays one whole featured path in real time on the audio
    /// clock (listener muted) and reports each step's timings in data/screens/paths_fullplay.json.
    /// <see cref="RunFullMashup"/> plays one whole mashup mix in real time the same way and reports
    /// each segment's timings in data/screens/mashup_fullplay.json.
    /// </summary>
    [InitializeOnLoad]
    public static class PathsPlayMode
    {
        const string ActiveKey = "MusicHistory.PathsPlayMode.Active";
        const string ScenePath = "Assets/Scenes/SongInfluenceGraph.unity";

        static IEnumerator? routine;
        static readonly Stack<IEnumerator> stack = new();
        static readonly List<(string name, bool ok, string detail)> checks = new();
        static readonly List<string> errors = new();
        static int exitCode = -1;
        static int waitTicks;

        static PathsPlayMode()
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            Hook();
        }

        public static void Run()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            SessionState.SetBool(ActiveKey, true);
            Hook();
            EditorApplication.EnterPlaymode();
        }

        /// <summary>
        /// Plays one whole featured path in real time on the audio device, the way a listener hears it:
        /// <c>-executeMethod MusicHistory.EditorTools.PathsPlayMode.RunFullPath [-pathsPlayId &lt;id&gt;]</c>
        /// (default: the first playable path). The listener is muted (AudioListener.volume 0), the        /// recordings play at full level. Logs and writes per-step timings to
        /// data/screens/paths_fullplay.json: load latency, decoded length, the clock, when Finished
        /// fired, the crossfade, the audio clock against the wall clock, and the level of the decoded
        /// recording under the playhead.
        /// </summary>
        public static void RunFullPath()
        {
            string id = "*";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-pathsPlayId", StringComparison.OrdinalIgnoreCase)) id = args[i + 1];
            SessionState.SetString(FullPathKey, id);
            Run();
        }

        const string FullPathKey = "MusicHistory.PathsPlayMode.FullPath";
        static string FullPathId => SessionState.GetString(FullPathKey, "");

        /// <summary>
        /// Plays one whole mashup mix in real time on the audio device:
        /// <c>-executeMethod MusicHistory.EditorTools.PathsPlayMode.RunFullMashup [-mashupPlayId &lt;path id&gt;] [-validationMashups &lt;file&gt;]</c>
        /// (default: the playable mix with the most songs; catalog: -validationMashups, else the real
        /// mashups.json). The listener is muted. Logs and writes per-segment timings to
        /// data/screens/mashup_fullplay.json: when the director entered each segment on the audio
        /// clock against the planned start, the wall clock, the step and vocal step, the melody
        /// graph's bright lines and light points mid-segment, the audio/wall rate and when the mix finished.
        /// </summary>
        public static void RunFullMashup()
        {
            string id = "*";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-mashupPlayId", StringComparison.OrdinalIgnoreCase)) id = args[i + 1];
            SessionState.SetString(FullMashupKey, id);
            Run();
        }

        const string FullMashupKey = "MusicHistory.PathsPlayMode.FullMashup";
        static string FullMashupId => SessionState.GetString(FullMashupKey, "");

        /// <summary>
        /// Plays one duet loop in real time on the audio device, round its loop point (DESIGN.md §16):
        /// <c>-executeMethod MusicHistory.EditorTools.PathsPlayMode.RunFullDuet [-duetPlayId &lt;path id&gt;] [-validationDuets &lt;file&gt;]</c>
        /// (default: the playable loop with the most songs whose loop.mp3 exists; catalog:
        /// -validationDuets, else the real duets.json). The listener is muted. Checks the loop
        /// decodes to its contract length (trimmed of MP3 padding), plays on the audio clock, wraps
        /// once per pass with the display clock continuous through the wrap, runs at real time, and
        /// writes data/screens/duet_fullplay.json.
        /// </summary>
        public static void RunFullDuet()
        {
            string id = "*";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-duetPlayId", StringComparison.OrdinalIgnoreCase)) id = args[i + 1];
            SessionState.SetString(FullDuetKey, id);
            Run();
        }

        const string FullDuetKey = "MusicHistory.PathsPlayMode.FullDuet";
        static string FullDuetId => SessionState.GetString(FullDuetKey, "");

        static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            // The editor's own search indexer can fail at start-up in batchmode; not ours.
            if ((stackTrace ?? "").Contains("UnityEditor.Search.") || condition.Contains("UnityEditor.Search.")) return;
            string first = (stackTrace ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            lock (errors) errors.Add($"{type}: {condition} @ {first.Trim()}");
        }

        static int ErrorCount()
        {
            lock (errors) return errors.Count;
        }

        static void Tick()
        {
            if (exitCode >= 0)
            {
                if (EditorApplication.isPlaying) return;
                SessionState.SetBool(ActiveKey, false);
                SessionState.EraseString(FullPathKey);
                SessionState.EraseString(FullMashupKey);
                SessionState.EraseString(FullDuetKey);
                EditorApplication.update -= Tick;
                Application.logMessageReceivedThreaded -= OnLog;
                EditorApplication.Exit(exitCode);
                return;
            }
            if (routine == null)
            {
                if (!EditorApplication.isPlaying)
                {
                    if (++waitTicks > 200000) Finish("play mode never started");
                    return;
                }
                routine = Main();
                stack.Clear();
                stack.Push(routine);
            }
            try
            {
                // Nested enumerators run to completion before their parent resumes; a null yield waits one tick.
                while (stack.Count > 0)
                {
                    IEnumerator top = stack.Peek();
                    if (!top.MoveNext())
                    {
                        stack.Pop();
                        continue;
                    }
                    if (top.Current is IEnumerator nested)
                    {
                        stack.Push(nested);
                        continue;
                    }
                    break;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Check("no exception in the play-mode routine", false, e.GetType().Name + ": " + e.Message);
                stack.Clear();
            }
            if (stack.Count == 0) Finish(null);
        }

        static IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        /// <summary>Waits <paramref name="seconds"/> of real time (or until <paramref name="until"/> holds).</summary>
        static IEnumerator Seconds(double seconds, Func<bool>? until = null)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds && (until == null || !until())) yield return null;
        }

        static IEnumerator WaitForGraph(Action<SongGraphLoader?> done, int maxFrames = 3000)
        {
            for (int i = 0; i < maxFrames; i++)
            {
                SongGraphLoader? l = Object.FindAnyObjectByType<SongGraphLoader>();
                if (l != null && l.Data != null && l.Nodes.Count > 0 && l.Paths != null)
                {
                    done(l);
                    yield break;
                }
                yield return null;
            }
            done(null);
        }

        static IEnumerator Main()
        {
            SongGraphLoader? loader = null;
            yield return WaitForGraph(l => loader = l);
            if (!Check("scene built itself in play mode", loader != null)) yield break;
            // One frame rendered before counting errors: batchmode renders nothing by itself, so the
            // first camera to render (the melody graph's light camera, later) would otherwise create
            // the render pipeline mid-check, and this editor logs its package-resource reload then.
            Camera? first = loader!.ViewCamera;
            if (first != null) first.Render();
            yield return Frames(2);
            int before = ErrorCount();
            if (FullPathId.Length > 0)
            {
                yield return FullPath(loader!, FullPathId);
                Check("full path: no exception or error logged", ErrorCount() == before, Errors(before));
                yield break;
            }
            if (FullMashupId.Length > 0)
            {
                yield return FullMashup(loader!, FullMashupId);
                Check("full mashup: no exception or error logged", ErrorCount() == before, Errors(before));
                yield break;
            }
            if (FullDuetId.Length > 0)
            {
                yield return FullDuet(loader!, FullDuetId);
                Check("full duet: no exception or error logged", ErrorCount() == before, Errors(before));
                yield break;
            }
            yield return Paths(loader!);
            Check("featured paths in play mode: no exception or error logged", ErrorCount() == before, Errors(before));
            before = ErrorCount();
            yield return Mashups(loader!);
            Check("mashups in play mode: no exception or error logged", ErrorCount() == before, Errors(before));
            yield return Teardown();
        }

        // ------------------------------------------------------------------ featured paths

        static IEnumerator Paths(SongGraphLoader loader)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            PreviewSongPlayer preview = d.Preview!;
            PathCatalog catalog = loader.Catalog;
            preview.Volume = 0f;   // silent validation: the clocks run all the same
            // These checks are about the per-step previews (Mashups below checks the mixes).
            loader.UseMashups(MashupCatalog.Empty("play mode: per-step previews"), "play mode");
            Check("play mode: EventSystem with InputSystemUIInputModule, GraphicRaycaster on the HUD canvas, no UI navigation",
                EventSystem.current != null && EventSystem.current == panel.Events && EventSystem.current.currentInputModule is InputSystemUIInputModule &&
                !EventSystem.current.sendNavigationEvents && loader.Hud.Canvas.GetComponent<UnityEngine.UI.GraphicRaycaster>() != null,
                EventSystem.current != null ? $"{EventSystem.current.name}, module {EventSystem.current.currentInputModule?.GetType().Name}" : "no EventSystem");
            if (catalog.PlayableCount == 0)
            {
                Check("play mode: featured paths skipped (no playable paths.json; pass -musicHistoryPaths)", true, catalog.Status);
                yield break;
            }
            Check("play mode: featured paths load and map onto the graph", catalog.Loaded && catalog.Problems.Count == 0,
                $"{catalog.Status}, {catalog.PlayableCount} playable, {catalog.SourcePath}; {string.Join(" | ", catalog.Problems.Take(3))}");

            // The button opens the list; a pointer over a row hits that row.
            panel.PathsButton!.onClick.Invoke();
            yield return Frames(3);
            Check("play mode: the Paths button opens the list", panel.IsOpen && panel.ListVisible);
            RowView? row = panel.Rows.FirstOrDefault(r => r.gameObject.activeInHierarchy && r.PathIndex == Math.Min(1, catalog.Paths.Count - 1));
            if (!Check("play mode: list rows are showing", row != null)) yield break;
            Vector3[] corners = new Vector3[4];
            ((RectTransform)row!.transform).GetWorldCorners(corners);
            Vector2 center = (corners[0] + corners[2]) * .5f;
            PointerEventData pointer = new(EventSystem.current) { position = center, button = PointerEventData.InputButton.Left };
            List<RaycastResult> hits = new();
            EventSystem.current!.RaycastAll(pointer, hits);
            RowView? hit = hits.Count > 0 ? hits[0].gameObject.GetComponentInParent<RowView>() : null;
            Check("play mode: a pointer over a row hits that row (GraphicRaycaster), and hover picking ignores it",
                hit == row && panel.ContainsScreenPoint(center), $"{hits.Count} hits at {center}; first {(hits.Count > 0 ? hits[0].gameObject.name : "none")}");
            FeaturedPath rowPath = catalog.Paths[row.PathIndex];
            // The first render loads slowly (1 s, simulated) past a 0.3 s time-out: the clock starts first.
            float loadTimeout = preview.LoadTimeoutSeconds;
            preview.LoadTimeoutSeconds = .3f;
            preview.SimulatedLoadDelaySeconds = 1f;
            ExecuteEvents.Execute(row.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
            yield return Frames(2);
            Check("play mode: pointer enter on a row lights that path's route", loader.Highlighter.FocusRoute == loader.RouteFor(rowPath) &&
                loader.RouteLine != null && loader.RouteLine.Visible);
            ExecuteEvents.Execute(row.gameObject, pointer, ExecuteEvents.pointerExitHandler);
            ExecuteEvents.Execute(row.gameObject, pointer, ExecuteEvents.pointerClickHandler);
            yield return Frames(2);
            Check("play mode: clicking a row plays its path; the list becomes the now-playing strip",
                d.IsTouring && d.Mode == TourMode.Path && d.CurrentPath == rowPath && panel.StripVisible && !panel.ListVisible);

            // The real render: decoded by UnityWebRequest, 44.1 kHz, as long as paths.json says.
            PathStep first = rowPath.Steps[0];
            int joins = preview.LateJoins;
            yield return Seconds(10, () => preview.CurrentAudio != null);
            preview.LoadTimeoutSeconds = loadTimeout;
            preview.SimulatedLoadDelaySeconds = 0f;
            AudioClip? audio = preview.CurrentAudio;
            yield return Seconds(.3);
            Check("play mode: a load that outlasts the time-out starts on the clock, then the recording joins at the clock's position",
                preview.LateJoins == joins + 1 && preview.LastJoinSeconds > .4 && preview.LastJoinSeconds < 1.2 && audio != null &&
                preview.ClockSource == "audio" && preview.CurrentSeconds > preview.LastJoinSeconds,
                $"late joins {preview.LateJoins - joins}; joined at {preview.LastJoinSeconds:0.000} s; now {preview.CurrentSeconds:0.00} s on the {preview.ClockSource} clock");
            Check("play mode: the first render decodes to 44.1 kHz audio as long as paths.json says (±0.15 s)",
                audio != null && audio.frequency == 44100 && Math.Abs(audio.length - first.Seconds) < .15,
                audio != null ? $"{audio.frequency} Hz, {audio.channels} ch, {audio.length:0.000} s vs {first.Seconds:0.000} s" : $"clock {preview.ClockSource}; {preview.LastWarning}");
            double t0 = preview.CurrentSeconds;
            yield return Seconds(1.5);
            double played = preview.CurrentSeconds - t0;
            AudioSource? source = preview.CurrentSource;
            Check("play mode: the preview plays and its clock advances in real time", played > 1.0 && played < 2.5,
                $"{played:0.00} s in 1.5 s; clock {preview.ClockSource}; AudioSource playing {(source != null && source.isPlaying)}");
            Debug.Log($"[paths-play] clock source with this audio device: {preview.ClockSource}");

            // Pause and resume fade; the clock stands still while paused.
            panel.HandleKey(Key.Space);
            yield return Seconds(.4);
            double pausedAt = preview.CurrentSeconds;
            float pausedGain = preview.CurrentGain;
            bool sourcePaused = preview.UsingMainClock || (preview.CurrentSource != null && !preview.CurrentSource.isPlaying);
            yield return Seconds(.5);
            bool frozen = Math.Abs(preview.CurrentSeconds - pausedAt) < 1e-6;
            panel.HandleKey(Key.Space);
            yield return Seconds(.4);
            Check("play mode: Space fades the preview out and pauses it (clock frozen), then fades back in",
                pausedGain < .01f && sourcePaused && frozen && preview.CurrentGain > .99f && !preview.Paused,
                $"gain paused {pausedGain:0.00}, resumed {preview.CurrentGain:0.00}; frozen {frozen}");

            // Stop fades out, then the list is back.
            panel.StopButton!.onClick.Invoke();
            int sounding = preview.SoundingVoices;
            yield return Seconds(.12);
            float mid = preview.MaxGain;
            yield return Seconds(.6);
            Check("play mode: Stop fades the recording out (no click) and returns to the list",
                !preview.UsingMainClock ? sounding >= 1 && mid > 0f && mid < 1f && preview.SoundingVoices == 0 : preview.SoundingVoices == 0,
                $"voices {sounding} → {preview.SoundingVoices}, gain after 0.12 s {mid:0.00}");
            Check("play mode: after Stop the list is showing again", panel.IsOpen && panel.ListVisible && !d.IsTouring);

            // A whole path on the main-thread clock (what runs when no device advances AudioSource.time), fast.
            FeaturedPath longest = catalog.Paths.Where(p => p.IsPlayable && p.MissingRenders == 0).OrderByDescending(p => p.Steps.Count).First();
            List<int> started = new(), tails = new(), previous = new();
            List<string> clocks = new();
            Dictionary<int, int> finished = new();
            void OnStarted(SongClip c)
            {
                started.Add(c.NodeId);
                tails.Add(preview.TailActive ? 1 : 0);
                previous.Add(d.PreviousClip?.NodeId ?? 0);
                clocks.Add(preview.ClockSource);
            }
            void OnFinished(SongClip c) => finished[c.NodeId] = finished.TryGetValue(c.NodeId, out int n) ? n + 1 : 1;
            preview.Started += OnStarted;
            preview.Finished += OnFinished;
            // Time scale 6: fast, yet a frame never skips a whole second of clock (the crossfade window).
            const float speed = 6f;
            preview.ForceMainClock = true;
            Time.timeScale = speed;
            try
            {
                panel.Select(catalog.Paths.IndexOf(longest));
                panel.HandleKey(Key.Enter);
                double budget = longest.Steps.Sum(s => s.Seconds) / speed + 15;
                yield return Seconds(budget, () => d.TourComplete);
                yield return Seconds(1.0);
                bool order = started.SequenceEqual(longest.Steps.Select(s => s.NodeId));
                bool previousOk = previous.Skip(1).SequenceEqual(longest.Steps.Take(longest.Steps.Count - 1).Select(s => s.NodeId)) && previous.FirstOrDefault() == 0;
                Check("play mode: on the main-thread clock the whole path plays, every step in order after the one before",
                    d.TourComplete && order && previousOk && clocks.Count > 0 && clocks.All(c => c == "main-thread clock"),
                    $"{longest.Id}: started {string.Join(",", started)}; previous {string.Join(",", previous)}; clocks {string.Join(",", clocks)}");
                Check("play mode: Finished fires once per step (crossfades never double-fire it)",
                    longest.Steps.All(s => finished.TryGetValue(s.NodeId, out int n) && n == 1),
                    string.Join(", ", longest.Steps.Select(s => $"{s.NodeId}:{(finished.TryGetValue(s.NodeId, out int n) ? n : 0)}")));
                Check("play mode: every natural advance crossfades (the outgoing clip still sounds when the next starts)",
                    tails.Count == longest.Steps.Count && tails.Skip(1).All(t => t == 1) && tails[0] == 0, string.Join(",", tails));
            }
            finally
            {
                preview.Started -= OnStarted;
                preview.Finished -= OnFinished;
                preview.ForceMainClock = false;
                Time.timeScale = 1f;
            }
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            Check("play mode: Esc, Esc: back to the list, then closed", !d.IsTouring && panel.State == FeaturedPathsPanel.PanelState.Closed &&
                loader.Hud.LegendVisible);
        }

        // ------------------------------------------------------------------ mashups

        /// <summary>
        /// A path with a mashup mix: the mix decoded by UnityWebRequest and playing in real time,
        /// the melody graph on a camera canvas, pause, Next, M, the whole mix on the main-thread
        /// clock at time scale 8 (segments in order, finished once), and the fallback to per-step
        /// previews without mashups. Catalog: -validationMashups, else the real mashups.json.
        /// </summary>
        static IEnumerator Mashups(SongGraphLoader loader)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            MashupPlayer player = d.MashupAudio!;
            string? arg = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-validationMashups", StringComparison.OrdinalIgnoreCase)) arg = SongGraphLoader.ResolveUserPath(args[i + 1]);
            string real = Path.Combine(SongGraphLoader.RepoRoot(), "data", "audio", "mashups", MashupCatalog.FileName);
            MashupCatalog catalog = arg != null && File.Exists(arg) ? MashupCatalog.Load(arg)
                : File.Exists(real) ? MashupCatalog.Load(real) : MashupCatalog.Empty("no mashups.json");
            loader.UseMashups(catalog, "play mode");
            Mashup? m = catalog.Mashups.Where(x => x.IsPlayable).OrderByDescending(x => x.FileExists).ThenByDescending(x => x.Songs.Count).FirstOrDefault();
            if (m == null)
            {
                Check("play mode: mashups skipped (no playable mashups.json; pass -validationMashups)", true, catalog.Status);
                yield break;
            }
            FeaturedPath path = m.Path!;
            float volume = player.Volume;
            player.Volume = 0f;   // silent validation
            List<int> segments = new();
            int finished = 0;
            void OnSegment()
            {
                if (segments.Count == 0 || segments[^1] != d.SegmentIndex) segments.Add(d.SegmentIndex);
            }
            void OnFinished(Mashup x) => finished++;
            try
            {
                panel.HandleKey(Key.P);
                yield return Frames(2);
                panel.Select(panel.Paths.ToList().IndexOf(path));
                panel.HandleKey(Key.Enter);
                yield return Frames(3);
                MelodyGraphPanel graph = loader.MelodyGraph!;
                Check("play mode: a path with a mashup plays the mix; the melody graph shows on a Screen Space - Camera canvas (bloom reaches it)",
                    d.CurrentMashup == m && ReferenceEquals(d.ActivePlayer, player) && graph.Showing && graph.Canvas != null &&
                    graph.Canvas.renderMode == RenderMode.ScreenSpaceCamera && graph.Canvas.worldCamera == loader.ViewCamera,
                    $"{m.Id}: {d.ActivePlayerName}; canvas {graph.Canvas?.renderMode}");
                if (m.FileExists)
                {
                    yield return Seconds(20, () => player.CurrentAudio != null);
                    AudioClip? audio = player.CurrentAudio;
                    Check("play mode: the mix decodes to 44.1 kHz audio as long as mashups.json says (±0.15 s)",
                        audio != null && audio.frequency == 44100 && Math.Abs(audio.length - m.Seconds) < .15,
                        audio != null ? $"{audio.frequency} Hz, {audio.channels} ch, {audio.length:0.000} s vs {m.Seconds:0.000} s" : $"{player.ClockSource}; {player.LastWarning}");
                    double t0 = player.CurrentSeconds;
                    yield return Seconds(1.5);
                    double played = player.CurrentSeconds - t0;
                    Check("play mode: the mix plays and its clock advances in real time", played > 1.0 && played < 2.5,
                        $"{played:0.00} s in 1.5 s on the {player.ClockSource} clock");
                    Debug.Log($"[paths-play] mashup clock source with this audio device: {player.ClockSource}");
                }
                panel.HandleKey(Key.Space);
                yield return Seconds(.4);
                double pausedAt = player.CurrentSeconds;
                float pausedGain = player.CurrentGain;
                yield return Seconds(.4);
                bool frozen = Math.Abs(player.CurrentSeconds - pausedAt) < 1e-6;
                panel.HandleKey(Key.Space);
                yield return Seconds(.4);
                Check("play mode: Space fades the mix out and pauses it (clock frozen), then fades back in",
                    pausedGain < .01f && frozen && player.CurrentGain > .99f && !player.Paused, $"gain paused {pausedGain:0.00}, resumed {player.CurrentGain:0.00}");
                panel.HandleKey(Key.RightArrow);
                yield return Seconds(.4);
                Check("play mode: Next jumps (fade out, seek, fade in) to where the next song's vocal enters",
                    Math.Abs(player.CurrentSeconds - m.StepStartSeconds(1)) < .6 && d.VocalStepIndex == 1 && d.CurrentSegment?.Kind == MashupSegmentKind.Changeover,
                    $"{player.CurrentSeconds:0.00} s vs {m.StepStartSeconds(1):0.00} s; gain {player.CurrentGain:0.00}");
                panel.HandleKey(Key.M);
                yield return Frames(2);
                bool hidden = !graph.Showing;
                panel.HandleKey(Key.M);
                yield return Frames(2);
                Check("play mode: M hides and shows the melody graph", hidden && graph.Showing);

                // The whole mix on the main-thread clock, fast.
                panel.HandleKey(Key.Escape);
                yield return Frames(2);
                d.SegmentChanged += OnSegment;
                player.MixFinished += OnFinished;
                player.ForceMainClock = true;
                const float speed = 8f;
                Time.timeScale = speed;
                panel.Select(panel.Paths.ToList().IndexOf(path));
                panel.HandleKey(Key.Enter);
                yield return Seconds(m.Duration / speed + 15, () => d.TourComplete);
                yield return Seconds(.5);
                Check("play mode: on the main-thread clock the whole mix plays every segment in order and finishes once",
                    d.TourComplete && segments.SequenceEqual(Enumerable.Range(0, m.Segments.Count)) && finished == 1 && player.UsingMainClock,
                    $"segments {string.Join(",", segments)} of {m.Segments.Count}; finished {finished}; {player.ClockSource}");
            }
            finally
            {
                d.SegmentChanged -= OnSegment;
                player.MixFinished -= OnFinished;
                player.ForceMainClock = false;
                Time.timeScale = 1f;
                player.Volume = volume;
            }
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            // Without mashups the same path plays its per-step previews.
            loader.UseMashups(MashupCatalog.Empty("play mode: fallback"), "play mode");
            if (!panel.IsOpen) panel.HandleKey(Key.P);   // the panel was rebuilt (closed)
            panel.Select(panel.Paths.ToList().IndexOf(path));
            panel.HandleKey(Key.Enter);
            yield return Frames(3);
            Check("play mode: without a mashup the same path plays per step (previews or MIDI), no melody graph",
                d.IsTouring && d.CurrentMashup == null && !ReferenceEquals(d.ActivePlayer, player) && loader.MelodyGraph != null && !loader.MelodyGraph.Showing,
                d.ActivePlayerName);
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            if (panel.IsOpen) panel.HandleKey(Key.Escape);
            yield return Frames(2);
        }

        // ------------------------------------------------------------------ one whole path, real time

        sealed class StepTiming
        {
            public int Index;
            public string Title = "";
            public double Requested = -1, Started = -1, Finished = -1, Left = -1;
            public double FinishedAt = -1, Seconds, AudioLength = -1;
            public int Frequency;
            public string Clock = "";
            public bool TailAtStart;
            public double TailSeconds;
            public double AudioT0 = -1, WallT0, AudioT1 = -1, WallT1;
            public double SumSquares;
            public long Samples;
            public double Peak;
            public int Finishes;
            public bool Preview;

            public double LoadMs => Started >= 0 && Requested >= 0 ? (Started - Requested) * 1000 : -1;
            public double Rate => AudioT1 > AudioT0 && WallT1 > WallT0 ? (AudioT1 - AudioT0) / (WallT1 - WallT0) : 0;
            /// <summary>Level of the decoded recording under the playhead (what the source is playing).</summary>
            public double RmsDb => Samples > 0 ? 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(SumSquares / Samples))) : double.NegativeInfinity;
            public double Wall => Left >= 0 && Requested >= 0 ? Left - Requested : -1;
        }

        static readonly List<string> timingJson = new();

        static IEnumerator FullPath(SongGraphLoader loader, string id)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            PreviewSongPlayer preview = d.Preview!;
            PathCatalog catalog = loader.Catalog;
            FeaturedPath? path = id == "*" ? catalog.Paths.FirstOrDefault(x => x.IsPlayable && x.MissingRenders == 0) : catalog.Find(id);
            if (!Check($"full path: '{id}' is in paths.json, maps onto the graph and has every render",
                    path != null && path.IsPlayable && path.MissingRenders == 0, $"{catalog.Status}: {string.Join(", ", catalog.Paths.Select(x => x.Id))}"))
                yield break;
            FeaturedPath p = path!;
            float listener = AudioListener.volume;
            // Silent on this machine's speakers. The mixer then keeps the voices virtual (AudioSource
            // output reads 0) while their clocks run, so the level is read from the decoded recording
            // under the playhead instead.
            AudioListener.volume = 0f;
            preview.Volume = 1f;
            List<StepTiming> rows = p.Steps.Select((s, i) => new StepTiming { Index = i, Title = s.Title, Seconds = s.Seconds }).ToList();
            Stopwatch wall = Stopwatch.StartNew();
            double Now() => wall.Elapsed.TotalSeconds;
            int completeFrames = 0;
            double completedAt = -1;
            float[] decoded = new float[4096];
            void OnStarted(SongClip c)
            {
                StepTiming? t = rows.FirstOrDefault(r => p.Steps[r.Index].NodeId == c.NodeId && r.Started < 0);
                if (t == null) return;
                t.Started = Now();
                t.TailAtStart = preview.TailActive;
                t.Clock = preview.ClockSource;
            }
            void OnFinished(SongClip c)
            {
                StepTiming? t = rows.FirstOrDefault(r => p.Steps[r.Index].NodeId == c.NodeId);
                if (t == null) return;
                t.Finishes++;
                if (t.Finished < 0)
                {
                    t.Finished = Now();
                    t.FinishedAt = preview.CurrentSeconds;
                }
            }
            preview.Started += OnStarted;
            preview.Finished += OnFinished;
            try
            {
                panel.HandleKey(Key.P);
                panel.Select(catalog.Paths.IndexOf(p));
                yield return Frames(2);
                wall.Restart();
                panel.HandleKey(Key.Enter);
                int last = -1;
                double budget = p.Steps.Sum(s => s.Seconds) + 20;
                while (Now() < budget)
                {
                    int i = d.IsTouring && d.Mode == TourMode.Path ? d.StepIndex : -1;
                    if (i != last && i >= 0 && i < rows.Count)
                    {
                        if (last >= 0 && last < rows.Count && rows[last].Left < 0) rows[last].Left = Now();
                        if (rows[i].Requested < 0) rows[i].Requested = Now();
                        last = i;
                    }
                    if (i >= 0 && i < rows.Count)
                    {
                        StepTiming t = rows[i];
                        t.Preview |= ReferenceEquals(d.ActivePlayer, preview) && preview.CurrentStep == p.Steps[i];
                        if (preview.TailActive && t.Started >= 0) t.TailSeconds = Now() - t.Started;
                        AudioClip? audio = preview.CurrentAudio;
                        AudioSource? source = preview.CurrentSource;
                        if (audio != null && t.AudioLength < 0)
                        {
                            t.AudioLength = audio.length;
                            t.Frequency = audio.frequency;
                        }
                        if (source != null && source.isPlaying && preview.ClockSource == "audio" && !d.TourComplete)
                        {
                            double pos = preview.CurrentSeconds;
                            // Audio clock against the wall clock between 2 s and the Finished hand-over.
                            if (pos >= 2 && t.AudioT0 < 0)
                            {
                                t.AudioT0 = pos;
                                t.WallT0 = Now();
                            }
                            if (t.AudioT0 >= 0 && pos < t.Seconds - 1.5)
                            {
                                t.AudioT1 = pos;
                                t.WallT1 = Now();
                            }
                            // Output level of the playing source, past the fade-in.
                            if (pos > 1 && pos < t.Seconds - 2)
                            {
                                // The decoded samples at the playhead: the recording itself, not silence.
                                int frame = source.timeSamples, channels = audio != null ? Math.Max(1, audio.channels) : 1;
                                if (audio != null && frame + decoded.Length / channels < audio.samples && audio.GetData(decoded, frame))
                                {
                                    foreach (float v in decoded)
                                    {
                                        t.SumSquares += v * v;
                                        t.Peak = Math.Max(t.Peak, Math.Abs(v));
                                    }
                                    t.Samples += decoded.Length;
                                }
                            }
                        }
                    }
                    if (d.TourComplete)
                    {
                        if (completedAt < 0) completedAt = Now();
                        if (++completeFrames > 30 && Now() - completedAt > 1.5) break;
                    }
                    yield return null;
                }
                if (last >= 0 && rows[last].Left < 0) rows[last].Left = completedAt >= 0 ? completedAt : Now();
            }
            finally
            {
                preview.Started -= OnStarted;
                preview.Finished -= OnFinished;
                AudioListener.volume = listener;
            }

            double total = completedAt;
            double expected = p.Steps.Sum(s => s.Seconds) - (p.Steps.Count - 1) * preview.CrossfadeSeconds;
            timingJson.Clear();
            foreach (StepTiming t in rows)
            {
                PathStep s = p.Steps[t.Index];
                string line = $"step {t.Index + 1}/{rows.Count} {t.Title}: requested {t.Requested:0.00} s, started {t.Started:0.00} s (load {t.LoadMs:0} ms), " +
                              $"clock {t.Clock}, {t.Frequency} Hz {t.AudioLength:0.000} s (paths.json {s.Seconds:0.000}), Finished at {t.FinishedAt:0.00} s of the clip " +
                              $"(wall {t.Finished:0.00} s), on screen {t.Wall:0.00} s, crossfade tail {(t.TailAtStart ? $"{t.TailSeconds:0.00} s" : "none")}, " +
                              $"audio/wall rate {t.Rate:0.0000}, recording at the playhead {t.RmsDb:0.0} dBFS rms (peak {t.Peak:0.00})";
                Debug.Log("[paths-full] " + line);
                timingJson.Add($"{{\"step\": {t.Index + 1}, \"work_id\": \"{Esc(s.WorkId)}\", \"title\": \"{Esc(t.Title)}\", \"requested_s\": {F(t.Requested)}, " +
                               $"\"started_s\": {F(t.Started)}, \"load_ms\": {F(t.LoadMs, "0")}, \"clock\": \"{t.Clock}\", \"sample_rate\": {t.Frequency}, " +
                               $"\"decoded_s\": {F(t.AudioLength)}, \"paths_json_s\": {F(s.Seconds)}, \"finished_at_clip_s\": {F(t.FinishedAt)}, \"finished_wall_s\": {F(t.Finished)}, " +
                               $"\"on_screen_s\": {F(t.Wall)}, \"crossfade_tail_s\": {F(t.TailAtStart ? t.TailSeconds : 0)}, \"audio_wall_rate\": {F(t.Rate, "0.0000")}, " +
                               $"\"playhead_rms_dbfs\": {F(double.IsInfinity(t.RmsDb) ? -999 : t.RmsDb, "0.0")}, \"playhead_peak\": {F(t.Peak)}, " +
                               $"\"start_key\": \"{Esc(s.StartKey)}\", \"key\": \"{Esc(s.Key)}\", \"start_bpm\": {F(s.StartBpm)}, \"bpm\": {F(s.Bpm)}, \"morph_s\": {F(s.MorphSeconds)}}}");
            }
            Debug.Log($"[paths-full] {p.Id}: {rows.Count} steps in {total:0.00} s of wall time (renders {p.Steps.Sum(s => s.Seconds):0.00} s, minus {rows.Count - 1} crossfades = {expected:0.00} s)");

            Check($"full path '{p.Id}': the tour completes, every step in order", total > 0 && rows.All(r => r.Requested >= 0 && r.Started >= 0) &&
                rows.Zip(rows.Skip(1), (a, b) => a.Requested < b.Requested).All(x => x), $"{total:0.00} s");
            Check($"full path '{p.Id}': every step plays its recording preview on the audio clock",
                rows.All(r => r.Preview && r.Clock == "audio"), string.Join(", ", rows.Select(r => $"{r.Index + 1}:{r.Clock}")));
            Check($"full path '{p.Id}': each render decodes to 44.1 kHz, as long as paths.json says (±0.15 s)",
                rows.All(r => r.Frequency == 44100 && Math.Abs(r.AudioLength - r.Seconds) < .15),
                string.Join(", ", rows.Select(r => $"{r.AudioLength:0.000}/{r.Seconds:0.000}")));
            Check($"full path '{p.Id}': each step starts within 0.5 s of being reached (the next render is preloaded)",
                rows.All(r => r.LoadMs >= 0 && r.LoadMs < 500), string.Join(", ", rows.Select(r => $"{r.LoadMs:0} ms")));
            Check($"full path '{p.Id}': Finished fires once per step, {preview.CrossfadeSeconds:0.#} s before the clip ends (±0.15 s)",
                rows.All(r => r.Finishes == 1 && Math.Abs(r.FinishedAt - (r.AudioLength - preview.CrossfadeSeconds)) < .15),
                string.Join(", ", rows.Select(r => $"{r.FinishedAt:0.00}/{r.AudioLength:0.00} ×{r.Finishes}")));
            Check($"full path '{p.Id}': every natural advance crossfades (the outgoing recording still sounds when the next starts)",
                rows.Skip(1).All(r => r.TailAtStart) && !rows[0].TailAtStart, string.Join(", ", rows.Select(r => r.TailAtStart ? $"{r.TailSeconds:0.00} s" : "-")));
            Check($"full path '{p.Id}': the audio clock runs at real time (within 1%)",
                rows.All(r => Math.Abs(r.Rate - 1) < .01), string.Join(", ", rows.Select(r => r.Rate.ToString("0.0000", CultureInfo.InvariantCulture))));
            Check($"full path '{p.Id}': the recording under the playhead is not silence (decoded rms above -45 dBFS)",
                rows.All(r => r.RmsDb > -45), string.Join(", ", rows.Select(r => $"{r.RmsDb:0.0} dBFS")));
            Check($"full path '{p.Id}': the whole path takes the render lengths minus the crossfades (±1.5 s)",
                Math.Abs(total - expected) < 1.5, $"{total:0.00} s vs {expected:0.00} s");

            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
        }

        // ------------------------------------------------------------------ one whole mashup mix, real time

        sealed class SegmentTiming
        {
            public int Index;
            public double EnteredWall = -1, EnteredAudio = -1;
            public string Clock = "";
            public int Step = -1, VocalStep = -1;
            public int MidLines = -1, MidDots = -1, MidVoicedDots = -1;
            public bool MidSampled;
            public string MidChord = "";
        }

        static IEnumerator FullMashup(SongGraphLoader loader, string id)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            MashupPlayer player = d.MashupAudio!;
            string? arg = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-validationMashups", StringComparison.OrdinalIgnoreCase)) arg = SongGraphLoader.ResolveUserPath(args[i + 1]);
            string real = Path.Combine(SongGraphLoader.RepoRoot(), "data", "audio", "mashups", MashupCatalog.FileName);
            MashupCatalog catalog = arg != null && File.Exists(arg) ? MashupCatalog.Load(arg)
                : File.Exists(real) ? MashupCatalog.Load(real) : MashupCatalog.Empty("no mashups.json");
            loader.UseMashups(catalog, "full mashup");
            Mashup? found = id == "*"
                ? catalog.Mashups.Where(x => x.IsPlayable && x.FileExists).OrderByDescending(x => x.Songs.Count).ThenByDescending(x => x.Seconds).FirstOrDefault()
                : catalog.Find(id);
            if (!Check($"full mashup: '{id}' is in mashups.json, playable, its mix exists",
                    found != null && found.IsPlayable && found.FileExists && found.Path != null,
                    $"{catalog.Status}; {string.Join(", ", catalog.Mashups.Select(x => $"{x.Id}{(x.IsPlayable ? "" : " (" + MashupCatalog.WhyNotPlayable(x) + ")")}"))}"))
                yield break;
            Mashup m = found!;
            MelodyGraphPanel graph = loader.MelodyGraph!;
            float listener = AudioListener.volume;
            AudioListener.volume = 0f;   // silent on this machine's speakers; the mix plays at full level
            float volume = player.Volume;
            player.Volume = 1f;
            List<SegmentTiming> rows = m.Segments.Select((g, i) => new SegmentTiming { Index = i }).ToList();
            List<int> order = new();
            Stopwatch wall = Stopwatch.StartNew();
            double Now() => wall.Elapsed.TotalSeconds;
            int finished = 0;
            double finishedWall = -1, finishedAudio = -1;
            double audioT0 = -1, wallT0 = 0, audioT1 = -1, wallT1 = 0;
            double sumSq = 0;
            long samples = 0;
            float[] decoded = new float[4096];
            string clockSeen = "";
            void OnFinished(Mashup x)
            {
                finished++;
                if (finishedWall < 0)
                {
                    finishedWall = Now();
                    finishedAudio = player.CurrentSeconds;
                }
            }
            player.MixFinished += OnFinished;
            double completedAt = -1;
            try
            {
                panel.HandleKey(Key.P);
                yield return Frames(2);
                panel.Select(panel.Paths.ToList().IndexOf(m.Path!));
                yield return Frames(2);
                wall.Restart();
                panel.HandleKey(Key.Enter);
                int last = -1;
                double budget = m.Duration + 25;
                while (Now() < budget)
                {
                    if (d.CurrentMashup == m)
                    {
                        int si = d.SegmentIndex;
                        double t = player.CurrentSeconds;
                        if (si != last && si >= 0 && si < rows.Count)
                        {
                            SegmentTiming r = rows[si];
                            if (r.EnteredWall < 0)
                            {
                                r.EnteredWall = Now();
                                r.EnteredAudio = t;
                                r.Clock = player.ClockSource;
                                r.Step = d.StepIndex;
                                r.VocalStep = d.VocalStepIndex;
                            }
                            order.Add(si);
                            last = si;
                        }
                        if (si >= 0 && si < rows.Count && !rows[si].MidSampled)
                        {
                            MashupSegment g = m.Segments[si];
                            if (t >= (g.Start + g.End) / 2)
                            {
                                SegmentTiming r = rows[si];
                                r.MidSampled = true;
                                r.MidLines = graph.Lines.Count(x => x.Playing);
                                r.MidDots = graph.Dots.Count(x => x.Active);
                                r.MidVoicedDots = graph.Dots.Count(x => x.Active && x.Voiced);
                                r.MidChord = graph.ChordText;
                            }
                        }
                        AudioClip? audio = player.CurrentAudio;
                        AudioSource? source = player.CurrentSource;
                        if (player.ClockSource == "audio" && source != null && source.isPlaying && !player.Complete)
                        {
                            clockSeen = "audio";
                            if (t >= 2 && audioT0 < 0)
                            {
                                audioT0 = t;
                                wallT0 = Now();
                            }
                            if (audioT0 >= 0 && t < m.Duration - 2)
                            {
                                audioT1 = t;
                                wallT1 = Now();
                            }
                            int frame = source.timeSamples, channels = audio != null ? Math.Max(1, audio.channels) : 1;
                            if (audio != null && t > 1 && frame + decoded.Length / channels < audio.samples && audio.GetData(decoded, frame))
                            {
                                foreach (float v in decoded) sumSq += v * v;
                                samples += decoded.Length;
                            }
                        }
                        else if (clockSeen.Length == 0) clockSeen = player.ClockSource;
                    }
                    if (d.TourComplete)
                    {
                        if (completedAt < 0) completedAt = Now();
                        if (Now() - completedAt > 1.5) break;
                    }
                    yield return null;
                }
            }
            finally
            {
                player.MixFinished -= OnFinished;
                AudioListener.volume = listener;
                player.Volume = volume;
            }

            double rate = audioT1 > audioT0 && wallT1 > wallT0 ? (audioT1 - audioT0) / (wallT1 - wallT0) : 0;
            double rmsDb = samples > 0 ? 20 * Math.Log10(Math.Max(1e-9, Math.Sqrt(sumSq / samples))) : -999;
            timingJson.Clear();
            foreach (SegmentTiming r in rows)
            {
                MashupSegment g = m.Segments[r.Index];
                string inst = g.InstrumentalSong >= 0 ? m.Songs[g.InstrumentalSong].Title : g.Instrumental;
                string voc = g.VocalSong >= 0 ? m.Songs[g.VocalSong].Title : "-";
                double err = r.EnteredAudio >= 0 ? (r.EnteredAudio - g.Start) * 1000 : double.NaN;
                string line = $"segment {r.Index + 1}/{rows.Count} {MashupSegment.KindName(g.Kind)} {g.Start:0.00}-{g.End:0.00} s ({g.Length:0.00} s) " +
                              $"instrumental {inst}, vocal {voc}, {g.Key} {g.Bpm:0.0} BPM, chord match {MashupCatalog.Percent(g.ChordMatch)}: " +
                              $"entered at {r.EnteredAudio:0.000} s on the {r.Clock} clock ({err:+0;-0} ms), wall {r.EnteredWall:0.00} s, step {r.Step + 1}, vocal step {r.VocalStep + 1}, " +
                              $"mid-segment {r.MidLines} bright melodies, {r.MidDots} light points ({r.MidVoicedDots} voiced), chord {r.MidChord}";
                Debug.Log("[mashup-full] " + line);
                timingJson.Add($"{{\"segment\": {r.Index}, \"kind\": \"{MashupSegment.KindName(g.Kind)}\", \"start_s\": {F(g.Start)}, \"end_s\": {F(g.End)}, " +
                               $"\"instrumental\": \"{Esc(g.Instrumental)}\", \"vocal\": \"{Esc(g.Vocal)}\", \"key\": \"{Esc(g.Key)}\", \"bpm\": {F(g.Bpm, "0.00")}, " +
                               $"\"chord_match\": {(g.ChordMatch is double c ? F(c) : "null")}, \"beat_error_ms\": {(g.BeatErrorMs is double b ? F(b, "0.0") : "null")}, " +
                               $"\"entered_audio_s\": {F(r.EnteredAudio)}, \"entry_error_ms\": {F(err, "0.0")}, \"entered_wall_s\": {F(r.EnteredWall)}, \"clock\": \"{Esc(r.Clock)}\", " +
                               $"\"step\": {r.Step}, \"vocal_step\": {r.VocalStep}, \"mid_bright_melodies\": {r.MidLines}, \"mid_light_points\": {r.MidDots}, " +
                               $"\"mid_voiced_light_points\": {r.MidVoicedDots}, \"mid_chord\": \"{Esc(r.MidChord)}\"}}");
            }
            Debug.Log($"[mashup-full] {m.Id}: {rows.Count} segments; mix {m.Seconds:0.000} s; finished at {finishedAudio:0.000} s of the mix (wall {finishedWall:0.00} s, ×{finished}); " +
                      $"tour complete at wall {completedAt:0.00} s; audio/wall rate {rate:0.0000}; decoded level at the playhead {rmsDb:0.0} dBFS rms");

            Check($"full mashup '{m.Id}': every segment entered once, in order",
                order.SequenceEqual(Enumerable.Range(0, rows.Count)), string.Join(",", order));
            Check($"full mashup '{m.Id}': the whole mix plays on the audio clock",
                clockSeen == "audio" && rows.All(r => r.Clock == "audio"), string.Join(", ", rows.Select(r => r.Clock)));
            Check($"full mashup '{m.Id}': each segment is entered within 100 ms of its planned start (audio clock)",
                rows.All(r => r.EnteredAudio >= 0 && Math.Abs(r.EnteredAudio - m.Segments[r.Index].Start) < .1),
                string.Join(", ", rows.Select(r => $"{(r.EnteredAudio - m.Segments[r.Index].Start) * 1000:0}")) + " ms");
            Check($"full mashup '{m.Id}': the audio clock runs at real time (within 1%)", Math.Abs(rate - 1) < .01, rate.ToString("0.0000", CultureInfo.InvariantCulture));
            Check($"full mashup '{m.Id}': the mix finishes once, at its end (±0.2 s), and the tour completes",
                finished == 1 && Math.Abs(finishedAudio - m.Duration) < .2 && completedAt > 0, $"×{finished} at {finishedAudio:0.000} s of {m.Duration:0.000} s");
            Check($"full mashup '{m.Id}': the wall time of the whole mix matches its length (±1.5 s)",
                completedAt > 0 && Math.Abs(completedAt - m.Duration) < 1.5, $"{completedAt:0.00} s vs {m.Duration:0.00} s");
            Check($"full mashup '{m.Id}': the decoded mix under the playhead is not silence (rms above -45 dBFS)", rmsDb > -45, $"{rmsDb:0.0} dBFS");
            Check($"full mashup '{m.Id}': mid-changeover two melodies are bright with two light points; elsewhere at most one",
                rows.All(r => r.MidSampled && (m.Segments[r.Index].Kind == MashupSegmentKind.Changeover ? r.MidLines == 2 && r.MidDots == 2 : r.MidLines <= 1 && r.MidDots <= 1)),
                string.Join(", ", rows.Select(r => $"{r.Index}:{r.MidLines}/{r.MidDots}")));
            Check($"full mashup '{m.Id}': the step follows the instrumental and the vocal step the vocal",
                rows.All(r => r.Step == m.InstrumentalStep(m.Segments[r.Index]) &&
                              (m.Segments[r.Index].Kind != MashupSegmentKind.Changeover || r.VocalStep == m.VocalStep(m.Segments[r.Index]))),
                string.Join(", ", rows.Select(r => $"{r.Index}:{r.Step}/{r.VocalStep}")));
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            if (panel.IsOpen) panel.HandleKey(Key.Escape);
            yield return Frames(2);
        }

        // ------------------------------------------------------------------ one duet loop, real time, round the loop point

        static IEnumerator FullDuet(SongGraphLoader loader, string id)
        {
            FeaturedPathsPanel panel = loader.Paths!;
            WalkthroughDirector d = loader.Director;
            DuetPlayer player = d.DuetAudio!;
            string? arg = null;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "-validationDuets", StringComparison.OrdinalIgnoreCase)) arg = SongGraphLoader.ResolveUserPath(args[i + 1]);
            string real = DuetCatalog.DefaultPath();
            DuetCatalog catalog = arg != null && File.Exists(arg) ? DuetCatalog.Load(arg)
                : File.Exists(real) ? DuetCatalog.Load(real) : DuetCatalog.Empty("no duets.json");
            loader.UseDuets(catalog, "full duet");
            DuetLoop? found = id == "*"
                ? catalog.Loops.Where(x => x.IsPlayable && x.FileExists).OrderByDescending(x => x.Songs.Count).ThenBy(x => x.Seconds).FirstOrDefault()
                : catalog.Find(id);
            if (!Check($"full duet: '{id}' is in duets.json, playable, its loop.mp3 exists",
                    found != null && found.IsPlayable && found.FileExists && found.Path != null,
                    $"{catalog.Status}; {string.Join(", ", catalog.Loops.Select(x => $"{x.Id}{(x.IsPlayable ? "" : " (" + DuetCatalog.WhyNotPlayable(x) + ")")}"))}"))
                yield break;
            DuetLoop l = found!;
            float listener = AudioListener.volume;
            AudioListener.volume = 0f;   // silent on this machine's speakers; the loop plays at full level
            float volume = player.Volume;
            player.Volume = 1f;
            Stopwatch wall = Stopwatch.StartNew();
            double Now() => wall.Elapsed.TotalSeconds;
            int wraps = 0;
            void OnWrap(int c) => wraps++;
            player.Wrapped += OnWrap;
            double worstBack = 0, worstJump = 0, prevTotal = -1, audioT0 = -1, wallT0 = 0, audioT1 = -1, wallT1 = 0, wrapWall = -1;
            string clockSeen = "";
            int segmentsSeen = 0, lastSegment = -1;
            List<string> wrapLog = new();
            try
            {
                panel.HandleKey(Key.P);
                yield return Frames(2);
                panel.Select(panel.Paths.ToList().IndexOf(l.Path!));
                yield return Frames(2);
                panel.HandleKey(Key.K);
                yield return Seconds(30, () => player.CurrentAudio != null);
                AudioClip? audio = player.CurrentAudio;
                Check("full duet: the loop decodes to PCM as long as duets.json says (any MP3 padding trimmed), on a looping AudioSource",
                    audio != null && Math.Abs(audio.length - l.Seconds) < .02 && player.CurrentSource != null && player.CurrentSource.loop,
                    audio != null ? $"{audio.frequency} Hz, {audio.length:0.0000} s vs {l.Seconds:0.0000} s; trimmed {player.TrimmedLead} + {player.TrimmedTail} samples" : $"{player.ClockSource}; {player.LastWarning}");
                // From near the loop's end, so the wrap comes soon; then once round the whole loop.
                player.Seek(Math.Max(0, l.Duration - 6));
                yield return Seconds(.5);
                wall.Restart();
                double budget = l.Duration + 12;
                while (Now() < budget && wraps < 2)
                {
                    double total = player.TotalSeconds;
                    if (prevTotal >= 0)
                    {
                        double step = total - prevTotal;
                        if (step < 0) worstBack = Math.Max(worstBack, -step);
                        worstJump = Math.Max(worstJump, step - Time.unscaledDeltaTime);
                    }
                    if (wraps >= 1 && wrapWall < 0)
                    {
                        wrapWall = Now();
                        wrapLog.Add($"wrapped at wall {wrapWall:0.000} s, total {total:0.000} s, loop {player.CurrentSeconds:0.000} s, audio {player.AudioTotalSeconds:0.000} s");
                    }
                    prevTotal = total;
                    if (player.ClockSource == "audio")
                    {
                        clockSeen = clockSeen.Length == 0 ? "audio" : clockSeen;
                        if (audioT0 < 0)
                        {
                            audioT0 = total;
                            wallT0 = Now();
                        }
                        audioT1 = total;
                        wallT1 = Now();
                    }
                    else clockSeen = player.ClockSource;
                    if (d.DuetSegmentIndex != lastSegment)
                    {
                        lastSegment = d.DuetSegmentIndex;
                        segmentsSeen++;
                    }
                    yield return null;
                }
            }
            finally
            {
                player.Wrapped -= OnWrap;
                AudioListener.volume = listener;
                player.Volume = volume;
            }
            double rate = audioT1 > audioT0 && wallT1 > wallT0 ? (audioT1 - audioT0) / (wallT1 - wallT0) : 0;
            timingJson.Clear();
            timingJson.Add($"{{\"id\": \"{Esc(l.Id)}\", \"seconds\": {F(l.Seconds)}, \"decoded_s\": {F(player.CurrentAudio != null ? player.CurrentAudio.length : -1)}, " +
                           $"\"trimmed_lead_samples\": {player.TrimmedLead}, \"trimmed_tail_samples\": {player.TrimmedTail}, \"wraps\": {wraps}, \"clock\": \"{Esc(clockSeen)}\", " +
                           $"\"audio_wall_rate\": {F(rate, "0.0000")}, \"worst_backward_s\": {F(worstBack)}, \"worst_jump_s\": {F(worstJump)}, \"segments_seen\": {segmentsSeen}}}");
            foreach (string line in wrapLog) Debug.Log("[duet-full] " + line);
            Debug.Log($"[duet-full] {l.Id}: {l.Seconds:0.000} s loop; {wraps} wraps; clock {clockSeen}; audio/wall rate {rate:0.0000}; worst backward step {worstBack * 1000:0.0} ms, worst jump {worstJump * 1000:0.0} ms");
            Check($"full duet '{l.Id}': the loop plays on the audio clock and wraps (twice: from near its end, then round the whole loop)", clockSeen == "audio" && wraps == 2,
                $"{clockSeen}; {wraps} wraps");
            Check($"full duet '{l.Id}': the display clock is continuous through the loop point (never back, no jump over 50 ms)", worstBack < 1e-6 && worstJump < .05,
                $"back {worstBack * 1000:0.0} ms, jump {worstJump * 1000:0.0} ms");
            Check($"full duet '{l.Id}': the audio clock runs at real time (within 1%)", Math.Abs(rate - 1) < .01, rate.ToString("0.0000", CultureInfo.InvariantCulture));
            Check($"full duet '{l.Id}': the pairs follow the loop (every segment entered on the way round)", segmentsSeen >= l.Segments.Count, $"{segmentsSeen} segment changes for {l.Segments.Count} segments");
            panel.HandleKey(Key.Escape);
            yield return Frames(2);
            if (panel.IsOpen) panel.HandleKey(Key.Escape);
            yield return Frames(2);
        }

        static string F(double v, string format = "0.000") =>
            double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString(format, CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ teardown

        static SongNode Focus(SongGraphLoader loader)
        {
            SongNode node = loader.Nodes.Where(n => n.Incoming.Count > 0 && n.Outgoing.Count > 0)
                .OrderByDescending(n => n.Outgoing.Count).First();
            loader.Highlighter.Select(node);
            loader.Highlighter.ApplyFocus(node, force: true);
            return node;
        }

        static IEnumerator Reload(Action<SongGraphLoader?> done)
        {
            SceneManager.LoadScene("SongInfluenceGraph", LoadSceneMode.Single);
            yield return Frames(2);
            yield return WaitForGraph(done);
        }

        static IEnumerator Teardown()
        {
            SongGraphLoader? loader = Object.FindAnyObjectByType<SongGraphLoader>();
            // a) the label boxes go first, then the highlighter is switched off (its OnDisable runs), then the graph.
            int before = ErrorCount();
            Focus(loader!);
            yield return Frames(2);
            GameObject? labels = GameObject.Find("Labels");
            bool labelsFound = labels != null;
            Object.Destroy(labels);
            yield return Frames(3);
            loader!.Highlighter.enabled = false;
            yield return Frames(1);
            loader.Highlighter.enabled = true;
            yield return Frames(1);
            Object.Destroy(loader.gameObject);
            yield return Frames(3);
            Check("teardown a: labels destroyed first, highlighter disabled, then the graph: no exceptions",
                labelsFound && ErrorCount() == before, Errors(before));

            // b) the graph first, then the labels (same frame).
            yield return Reload(l => loader = l);
            if (!Check("teardown: scene reloads", loader != null)) yield break;
            before = ErrorCount();
            Focus(loader!);
            yield return Frames(2);
            Object.Destroy(loader!.gameObject);
            Object.Destroy(GameObject.Find("Labels"));
            yield return Frames(3);
            Check("teardown b: graph object then labels in one frame: no exceptions", ErrorCount() == before, Errors(before));

            // c) songs and edges first, then the rest.
            yield return Reload(l => loader = l);
            if (!Check("teardown: scene reloads again", loader != null)) yield break;
            before = ErrorCount();
            Focus(loader!);
            yield return Frames(2);
            Transform? graph = loader!.transform.Find("Song Graph");
            bool graphFound = graph != null;
            if (graph != null) Object.Destroy(graph.gameObject);
            yield return Frames(3);
            loader.Highlighter.Select(null);
            loader.Highlighter.enabled = false;
            yield return Frames(2);
            Object.Destroy(loader.gameObject);
            yield return Frames(3);
            Check("teardown c: songs and edges first, highlighter disabled, then the graph object: no exceptions",
                graphFound && ErrorCount() == before, Errors(before));

            // d) a focused scene is replaced by a scene load (Unity's own unload order).
            yield return Reload(l => loader = l);
            if (!Check("teardown: scene reloads a third time", loader != null)) yield break;
            before = ErrorCount();
            Focus(loader!);
            yield return Frames(2);
            yield return Reload(l => loader = l);
            Check("teardown d: scene load with a focused song (the reported MissingReferenceException): no exceptions",
                loader != null && ErrorCount() == before, Errors(before));

            // e) a scene load while a featured path plays (preview voices, strip, route highlight).
            before = ErrorCount();
            if (loader != null && loader.Catalog.PlayableCount > 0 && loader.Paths != null)
            {
                loader.Paths.HandleKey(Key.P);
                loader.Paths.HandleKey(Key.Enter);
                yield return Seconds(1.5);
                bool playing = loader.Director.IsTouring && loader.Director.Mode == TourMode.Path;
                yield return Reload(l => loader = l);
                Check("teardown e: scene load while a featured path plays: no exceptions", playing && loader != null && ErrorCount() == before, Errors(before));
            }

            // f) leaving play mode with a focused song and the paths list open.
            before = ErrorCount();
            Focus(loader!);
            if (loader!.Paths != null && loader.Catalog.PlayableCount > 0)
            {
                loader.Paths.HandleKey(Key.P);
                loader.Paths.HoverRow(0);
            }
            else
            {
                Focus(loader);
            }
            yield return Frames(2);
            EditorApplication.ExitPlaymode();
            for (int i = 0; i < 20000 && EditorApplication.isPlaying; i++) yield return null;
            yield return Frames(5);
            Check("teardown f: exiting play mode with a focused song and the paths list open: no exceptions",
                !EditorApplication.isPlaying && ErrorCount() == before, Errors(before));
        }

        static string Errors(int from)
        {
            lock (errors)
            {
                if (errors.Count <= from) return "";
                return $"{errors.Count - from} errors; first: " + string.Join(" | ", errors.Skip(from).Take(3));
            }
        }

        static bool Check(string name, bool ok, string detail = "")
        {
            checks.Add((name, ok, detail));
            Debug.Log($"[paths-play] {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}");
            return ok;
        }

        static void Finish(string? failure)
        {
            if (failure != null) Check(failure, false);
            int failures = checks.Count(c => !c.ok);
            Debug.Log($"[paths-play] {checks.Count - failures}/{checks.Count} checks passed");
            try
            {
                string dir = Path.Combine(SongGraphLoader.RepoRoot(), "data", "screens");
                Directory.CreateDirectory(dir);
                StringBuilder json = new();
                json.Append("{\n  \"passed\": ").Append(failures == 0 ? "true" : "false").Append(",\n  \"generated_at\": \"")
                    .Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append("\",\n  \"checks\": [");
                json.Append(string.Join(",", checks.Select(c =>
                    $"\n    {{\"name\": \"{Esc(c.name)}\", \"ok\": {(c.ok ? "true" : "false")}, \"detail\": \"{Esc(c.detail)}\"}}")));
                json.Append("\n  ]");
                bool full = FullPathId.Length > 0, fullMix = !full && FullMashupId.Length > 0, fullDuet = !full && !fullMix && FullDuetId.Length > 0;
                if (full) json.Append(",\n  \"steps\": [").Append(string.Join(",", timingJson.Select(t => "\n    " + t))).Append("\n  ]");
                if (fullMix) json.Append(",\n  \"segments\": [").Append(string.Join(",", timingJson.Select(t => "\n    " + t))).Append("\n  ]");
                if (fullDuet) json.Append(",\n  \"loop\": [").Append(string.Join(",", timingJson.Select(t => "\n    " + t))).Append("\n  ]");
                json.Append("\n}\n");
                string name = full ? "paths_fullplay.json" : fullMix ? "mashup_fullplay.json" : fullDuet ? "duet_fullplay.json" : "paths_playmode.json";
                File.WriteAllText(Path.Combine(dir, name), json.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[paths-play] could not write the report: {e.Message}");
            }
            exitCode = failures == 0 ? 0 : 1;
            routine = null;
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        static string Esc(string s) => s.Replace("\\", "/").Replace("\"", "'").Replace("\n", " ").Replace("\r", "");
    }
}
