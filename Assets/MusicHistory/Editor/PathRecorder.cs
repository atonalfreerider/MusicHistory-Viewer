#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using MusicHistory.Walkthrough;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Records a narrated featured-path tour to MP4 with the Unity Recorder (DESIGN.md §15):
    /// MusicHistory › Record narrated path › Horizontal 1920x1080 / Vertical 1080x1920, or
    /// <c>Unity -projectPath &lt;viewer&gt; -executeMethod MusicHistory.EditorTools.PathRecorder.Record
    /// -path &lt;id&gt; -format horizontal|vertical</c> (not -batchmode: the Recorder captures the Game
    /// view; the editor quits when the file is written, exit code 0 on success).
    ///
    /// Enters play mode in Assets/Scenes/SongInfluenceGraph.unity, waits for the viewer, starts the
    /// path tour (its mashup mix, with narration on) paused while the mix and the narration WAVs
    /// load, starts the Recorder (Game view at the output size, constant 30 fps, H.264 MP4 with the
    /// AudioListener's sound), lets the layout adapt to that size (<see cref="ViewerLayout"/>; the
    /// legend and the Paths button are hidden), plays the tour from the top and stops 1.5 s after it
    /// ends. Output: &lt;MusicHistory&gt;/data/recordings/&lt;path id&gt;_&lt;format&gt;.mp4.
    ///
    /// The menu records the path playing or selected in the featured-paths panel (play mode), else
    /// the path recorded last, else the first narrated path with a mashup.
    /// </summary>
    public static class PathRecorder
    {
        const string RequestKey = "MusicHistory.PathRecorder.Request";
        const string ExitKey = "MusicHistory.PathRecorder.ExitCode";
        const string LastPathPref = "MusicHistory.PathRecorder.LastPath";
        const string ScenePath = "Assets/Scenes/SongInfluenceGraph.unity";
        const string Menu = "MusicHistory/Record narrated path/";
        public const int Fps = 30;
        /// <summary>Seconds of picture after the tour ends.</summary>
        public const float TailSeconds = 1.5f;

        enum Phase { Idle, WaitApp, Loading, Resize, Recording, Tail }

        static Phase phase = Phase.Idle;
        static RecorderController? controller;
        static SongGraphLoader? loader;
        static FeaturedPath? path;
        static bool vertical, exitWhenDone;
        static int width, height, tailEndFrame, resizeFrames;
        static double deadline, recordDeadline;
        static string output = "";
        static string? requestedId;

        [MenuItem(Menu + "Horizontal 1920x1080", priority = 1)]
        static void RecordHorizontal() => Begin(MenuPathId(), false, false);

        [MenuItem(Menu + "Vertical 1080x1920", priority = 2)]
        static void RecordVertical() => Begin(MenuPathId(), true, false);

        [MenuItem(Menu + "Horizontal 1920x1080", true)]
        [MenuItem(Menu + "Vertical 1080x1920", true)]
        static bool CanRecord() => phase == Phase.Idle;

        [MenuItem(Menu + "Stop recording", priority = 20)]
        static void StopFromMenu() => Finish("stopped from the menu", 4);

        [MenuItem(Menu + "Stop recording", true)]
        static bool CanStop() => phase != Phase.Idle || SessionState.GetString(RequestKey, "").Length > 0;

        /// <summary>-executeMethod entry: -path &lt;id&gt; -format horizontal|vertical.</summary>
        public static void Record()
        {
            string? id = Arg("-path");
            string format = (Arg("-format") ?? "horizontal").Trim().ToLowerInvariant();
            if (format != "horizontal" && format != "vertical")
            {
                Debug.LogError($"[record] -format must be horizontal or vertical, not '{format}'.");
                EditorApplication.Exit(2);
                return;
            }
            if (Application.isBatchMode)
                Debug.LogWarning("[record] -batchmode has no Game view to record: run Unity without -batchmode (the editor quits when the video is written).");
            Begin(string.IsNullOrWhiteSpace(id) ? null : id, format == "vertical", exitWhenDone: true);
        }

        /// <summary>Records path <paramref name="pathId"/> (null: the menu's choice) in the given format.</summary>
        public static void Begin(string? pathId, bool portrait, bool exitWhenDone)
        {
            if (phase != Phase.Idle)
            {
                Debug.LogWarning("[record] a recording is already running (MusicHistory › Record narrated path › Stop recording).");
                return;
            }
            SessionState.SetString(RequestKey, $"{(portrait ? "v" : "h")}|{(exitWhenDone ? "1" : "0")}|{pathId ?? ""}");
            if (EditorApplication.isPlaying)
            {
                Wait();
                return;
            }
            if (SceneManager.GetActiveScene().path != ScenePath)
            {
                if (!exitWhenDone && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    SessionState.EraseString(RequestKey);
                    return;
                }
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            EditorApplication.isPlaying = true;
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    if (SessionState.GetString(RequestKey, "").Length > 0) Wait();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    if (phase != Phase.Idle) Finish("play mode ended before the tour did", 5, leavePlayMode: false);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    int code = SessionState.GetInt(ExitKey, -1);
                    if (code >= 0)
                    {
                        SessionState.EraseInt(ExitKey);
                        EditorApplication.Exit(code);
                    }
                    break;
            }
        }

        static void Wait()
        {
            string[] request = SessionState.GetString(RequestKey, "h|0|").Split(new[] { '|' }, 3);
            SessionState.EraseString(RequestKey);
            vertical = request[0] == "v";
            exitWhenDone = request.Length > 1 && request[1] == "1";
            requestedId = request.Length > 2 && request[2].Length > 0 ? request[2] : null;
            width = vertical ? 1080 : 1920;
            height = vertical ? 1920 : 1080;
            output = "";
            phase = Phase.WaitApp;
            deadline = EditorApplication.timeSinceStartup + 120;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        static void Tick()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Finish("error: " + e.Message, 6);
            }
        }

        static void Step()
        {
            double now = EditorApplication.timeSinceStartup;
            switch (phase)
            {
                case Phase.WaitApp:
                {
                    SongGraphLoader? l = Object.FindAnyObjectByType<SongGraphLoader>();
                    if (l == null || l.Data == null || l.Paths == null || l.Narration == null || l.Layout == null || l.Director == null)
                    {
                        if (now > deadline) Finish("the viewer did not build (is the graph database there?)", 3);
                        return;
                    }
                    loader = l;
                    path = Choose(l, requestedId, out string why);
                    if (path == null)
                    {
                        Finish(why, 3);
                        return;
                    }
                    EditorPrefs.SetString(LastPathPref, path.Id);
                    WalkthroughDirector d = l.Director;
                    l.Layout.RecordingMode = true;
                    d.PreferMashups = true;
                    d.ApplesToApples = false;
                    l.Narration.SetOn(true);
                    if (l.MelodyGraph != null) l.MelodyGraph.SetUserVisible(true);
                    if (d.IsTouring) d.Exit();
                    l.Paths.Select(l.Paths.Paths.ToList().IndexOf(path));
                    if (!l.Paths.PlaySelected())
                    {
                        Finish($"path '{path.Id}' could not start ({l.Paths.Notice})", 3);
                        return;
                    }
                    // Hold the start while the mix and the narration load.
                    if (d.ActivePlayer != null) d.ActivePlayer.Paused = true;
                    if (d.CurrentMashup == null) Debug.LogWarning($"[record] '{path.Id}' has no playable mashup mix: it records the per-step previews, without narration.");
                    else if (l.Narration.For(d.CurrentMashup.Id) == null) Debug.LogWarning($"[record] '{path.Id}' has no narration in {l.NarrationCatalog.SourcePath} ({l.NarrationCatalog.Status}): recording it unnarrated.");
                    phase = Phase.Loading;
                    deadline = now + 60;
                    return;
                }
                case Phase.Loading:
                {
                    SongGraphLoader l = loader!;
                    if (l == null) { Finish("the viewer went away", 5); return; }
                    WalkthroughDirector d = l.Director;
                    MashupPlayer? mix = d.MashupAudio;
                    bool mixReady = d.CurrentMashup == null || mix == null || mix.CurrentAudio != null || !d.CurrentMashup.FileExists;
                    bool narrationReady = l.Narration == null || l.Narration.AllLoaded(l.Narration.CurrentPath);
                    if (!(mixReady && narrationReady) && now < deadline) return;
                    if (!mixReady) Debug.LogWarning("[record] the mix is still loading after 60 s; recording anyway.");
                    if (!narrationReady) Debug.LogWarning("[record] some narration WAVs are still loading after 60 s; recording anyway.");
                    output = OutputBase(path!.Id, vertical);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    StartRecorder();
                    resizeFrames = 0;
                    phase = Phase.Resize;
                    return;
                }
                case Phase.Resize:
                {
                    // The Recorder sizes the Game view; the layout follows Screen.width / height.
                    if ((Screen.width != width || Screen.height != height) && ++resizeFrames < 90) return;
                    if (Screen.width != width || Screen.height != height)
                        Debug.LogWarning($"[record] the Game view is {Screen.width}x{Screen.height}, not {width}x{height}; the layout follows the Game view.");
                    SongGraphLoader l = loader!;
                    WalkthroughDirector d = l.Director;
                    l.Layout!.ApplyNow();
                    d.GoTo(0);   // from the top: the mix seeks to 0 and plays
                    if (d.ActivePlayer != null) d.ActivePlayer.Paused = false;
                    d.Reframe(immediate: true);
                    double seconds = d.CurrentMashup != null ? d.CurrentMashup.Duration : path!.Seconds > 0 ? path.Seconds : 600;
                    recordDeadline = now + seconds * 3 + 120;
                    Debug.Log($"[record] recording '{path!.Id}' ({(vertical ? "vertical" : "horizontal")} {width}x{height}, {Fps} fps) to {output}.mp4");
                    phase = Phase.Recording;
                    return;
                }
                case Phase.Recording:
                {
                    SongGraphLoader l = loader!;
                    if (l == null) { Finish("the viewer went away", 5); return; }
                    if (controller == null || !controller.IsRecording())
                    {
                        Finish("the Recorder stopped by itself", 5);
                        return;
                    }
                    WalkthroughDirector d = l.Director;
                    if (d.TourComplete || !d.IsTouring)
                    {
                        tailEndFrame = Time.frameCount + Mathf.CeilToInt(TailSeconds * Fps);
                        phase = Phase.Tail;
                        return;
                    }
                    if (now > recordDeadline) Finish("the tour did not end in time; stopped", 5);
                    return;
                }
                case Phase.Tail:
                    if (Time.frameCount >= tailEndFrame) Finish("", 0);
                    return;
            }
        }

        static void StartRecorder()
        {
            RecorderControllerSettings settings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            MovieRecorderSettings movie = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            movie.name = "MusicHistory narrated path";
            movie.Enabled = true;
            movie.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,   // H.264
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High
            };
            movie.ImageInputSettings = new GameViewInputSettings { OutputWidth = width, OutputHeight = height };
            movie.AudioInputSettings.PreserveAudio = true;   // the AudioListener: mix, narration
            movie.OutputFile = output;                        // the Recorder adds .mp4
            settings.AddRecorderSettings(movie);
            settings.SetRecordModeToManual();
            settings.FrameRatePlayback = FrameRatePlayback.Constant;
            settings.FrameRate = Fps;
            settings.CapFrameRate = true;
            controller = new RecorderController(settings);
            controller.PrepareRecording();
            if (!controller.StartRecording()) throw new InvalidOperationException("the Recorder did not start");
        }

        static void Finish(string problem, int code, bool leavePlayMode = true)
        {
            bool wasRecording = controller != null && controller.IsRecording();
            if (wasRecording) controller!.StopRecording();
            controller = null;
            EditorApplication.update -= Tick;
            SessionState.EraseString(RequestKey);
            if (loader != null && loader.Layout != null) loader.Layout.RecordingMode = false;
            string file = output.Length > 0 ? output + ".mp4" : "";
            if (code == 0) Debug.Log($"[record] saved {file}");
            else Debug.LogError($"[record] {problem}{(wasRecording ? $" (partial video: {file})" : "")}");
            phase = Phase.Idle;
            loader = null;
            path = null;
            if (!exitWhenDone) return;
            exitWhenDone = false;
            if (EditorApplication.isPlaying)
            {
                // Quit once play mode has ended (the Recorder has closed the file by then).
                SessionState.SetInt(ExitKey, code);
                if (leavePlayMode) EditorApplication.isPlaying = false;
            }
            else EditorApplication.Exit(code);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>&lt;data&gt;/recordings/&lt;path id&gt;_&lt;format&gt; (no extension: the Recorder adds .mp4).</summary>
        public static string OutputBase(string pathId, bool portrait)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            string safe = new(pathId.Select(c => bad.Contains(c) || c == '<' || c == '>' ? '_' : c).ToArray());
            return Path.Combine(PipelinePaths.Data(), "recordings", $"{safe}_{(portrait ? "vertical" : "horizontal")}");
        }

        static string? MenuPathId()
        {
            if (!EditorApplication.isPlaying) return null;
            SongGraphLoader? l = Object.FindAnyObjectByType<SongGraphLoader>();
            if (l == null || l.Director == null) return null;
            if (l.Director.CurrentPath != null) return l.Director.CurrentPath.Id;
            if (l.Paths != null && l.Paths.Selected >= 0 && l.Paths.Selected < l.Paths.Paths.Count) return l.Paths.Paths[l.Paths.Selected].Id;
            return null;
        }

        /// <summary>
        /// <paramref name="id"/> exactly; else the path recorded last, else the first narrated path
        /// with a mashup, else the first path with a mashup, else the first playable path.
        /// </summary>
        static FeaturedPath? Choose(SongGraphLoader l, string? id, out string why)
        {
            IReadOnlyList<FeaturedPath> paths = l.Catalog.Paths;
            string ids = string.Join(", ", paths.Select(p => p.Id));
            if (id != null)
            {
                FeaturedPath? exact = paths.FirstOrDefault(p => p.Id == id);
                why = exact == null ? $"no featured path '{id}' in {l.Catalog.SourcePath} (paths: {ids})"
                    : !exact.IsPlayable ? $"path '{id}' is not playable in this graph" : "";
                return exact != null && exact.IsPlayable ? exact : null;
            }
            string last = EditorPrefs.GetString(LastPathPref, "");
            FeaturedPath? chosen = paths.FirstOrDefault(p => p.Id == last && p.IsPlayable)
                                   ?? paths.FirstOrDefault(p => p.IsPlayable && l.Director.MashupFor(p) is Mashup m && l.NarrationCatalog.For(m.Id) != null)
                                   ?? paths.FirstOrDefault(p => p.IsPlayable && l.Director.MashupFor(p) != null)
                                   ?? paths.FirstOrDefault(p => p.IsPlayable);
            why = chosen == null ? $"no playable featured path ({l.Catalog.Status}; paths: {ids})" : "";
            return chosen;
        }

        static string? Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
