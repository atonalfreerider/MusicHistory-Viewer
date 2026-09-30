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
            int before = ErrorCount();
            if (FullPathId.Length > 0)
            {
                yield return FullPath(loader!, FullPathId);
                Check("full path: no exception or error logged", ErrorCount() == before, Errors(before));
                yield break;
            }
            yield return Paths(loader!);
            Check("featured paths in play mode: no exception or error logged", ErrorCount() == before, Errors(before));
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
                bool full = FullPathId.Length > 0;
                if (full) json.Append(",\n  \"steps\": [").Append(string.Join(",", timingJson.Select(t => "\n    " + t))).Append("\n  ]");
                json.Append("\n}\n");
                File.WriteAllText(Path.Combine(dir, full ? "paths_fullplay.json" : "paths_playmode.json"), json.ToString(), new UTF8Encoding(false));
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
