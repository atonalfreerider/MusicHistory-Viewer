#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MusicHistory.Walkthrough
{
    /// <summary>
    /// Guided tours through the influence tree (docs/DESIGN.md §11). Each step flies the camera to
    /// frame the song and its tree parent, grows the tree edge from the influencer, and plays the
    /// song's excerpt through the <see cref="ISongPlayer"/>, starting in the key and BPM of the song
    /// heard just before and gliding to its own (<see cref="Morph.Plan"/>). The next step starts
    /// when the player reports the excerpt finished.
    ///
    /// "The song heard just before" is the clip the tour was on when the step changed, whichever
    /// way it moved: the previous tour step on a forward advance, the step the listener left on
    /// B/←, the same song on a restart (Enter, C). Only the tour's first song plays natively.
    ///
    /// Keys: 1/2/3/4 mode (lineage, subtree, chronological, family), M next mode, Enter start (from
    /// the clicked song or edge), Space pause, N or → next, B or ← previous, Esc exit, C toggles
    /// "compare normalized".
    ///
    /// Family mode (identity lineages, DESIGN.md §8b) plays every song of one identity family in
    /// time order — the clicked edge's family, else the clicked song's strongest — each at the
    /// bars where the identity sounds in it (<see cref="TourPlanner.Window"/>), morphing from the
    /// song heard before exactly like the other modes.
    ///
    /// Path mode (<see cref="StartPathTour"/>, from the featured-paths panel) plays exactly the
    /// steps of one featured path (data/audio/renders/paths.json) in order. Each step plays its
    /// prerendered recording preview (<see cref="PreviewSongPlayer"/>), which already starts in the
    /// previous step's key and tempo and glides to its own; a step whose render is missing, and
    /// "compare" (C), play the MIDI instead. Recording previews are used for nothing else.
    /// </summary>
    public sealed class WalkthroughDirector : MonoBehaviour
    {
        public TourMode Mode = TourMode.Lineage;
        [Min(.1f)] public float FlyDuration = 1.8f;
        [Min(.1f)] public float EdgeGrowDuration = 1.6f;
        [Min(0f)] public float MorphBars = 2f;
        [Tooltip("Play the normalized MIDI (graph_meta target_key, target_bpm) without morphing.")]
        public bool ApplesToApples;
        [Min(1f)] public float FramingMargin = 1.35f;
        [Tooltip("Screen area (normalized) a step is framed into, clear of the HUD panels.")]
        public Rect TourViewport = new(.14f, .24f, .72f, .46f);
        [Min(.5f)] public float MinFramingDistance = 7f;
        [Tooltip("Safety net for a player that stops without raising Finished: advance after this many " +
                 "seconds in which its beat did not move (unpaused). Longer than a MIDI parse plus the " +
                 "wait for the previous song's next bar line.")]
        [Min(1f)] public float WatchdogSeconds = 20f;

        public SongGraphLoader Loader = null!;
        public ISongPlayer? Player { get; private set; }
        public SilentSongPlayer Silent { get; private set; } = null!;
        /// <summary>Plays prerendered recording previews (data/audio/renders) when one exists for the step.</summary>
        public PreviewSongPlayer? Preview { get; private set; }
        public string PlayerDescription { get; private set; } = "";
        public bool IsTouring { get; private set; }
        /// <summary>The last step's excerpt has ended.</summary>
        public bool TourComplete { get; private set; }
        public int StepIndex { get; private set; }
        public IReadOnlyList<int> Steps => steps;
        public SongClip? CurrentClip { get; private set; }
        /// <summary>The clip heard just before the current one (null = the current one plays natively).</summary>
        public SongClip? PreviousClip { get; private set; }
        /// <summary>The plan the active player actually plays the current clip with (tempo from what was heard).</summary>
        public MorphPlan CurrentPlan => ActivePlayer?.CurrentPlan ?? MorphPlan.None;
        public ISongPlayer? ActivePlayer { get; private set; }
        public bool Flying => flyT < 1f;
        public string HudText { get; private set; } = "";
        /// <summary>Seconds the active player has made no progress (the watchdog's clock).</summary>
        public double StallSeconds => stallSeconds;
        /// <summary>The identity family a family tour plays (null for the other modes).</summary>
        public int? FamilyId { get; private set; }
        /// <summary>The edge the current step lights up and grows (the tree edge, or the family's edge into the song).</summary>
        public InfluenceEdge? StepEdge { get; private set; }
        /// <summary>The song framed beside the current one: its tree parent, or the previous song of a family tour.</summary>
        public SongNode? StepPartner { get; private set; }
        /// <summary>Family tours: where the current song plays (own excerpt, edge span or first visit).</summary>
        public FamilyWindow? CurrentWindow { get; private set; }
        /// <summary>The featured paths (data/audio/renders/paths.json); empty when the file is missing.</summary>
        public PathCatalog Catalog { get; private set; } = PathCatalog.Empty("not loaded");
        /// <summary>The featured path a path tour plays (null for the other modes).</summary>
        public FeaturedPath? CurrentPath { get; private set; }
        /// <summary>The current path's songs and edges on the graph.</summary>
        public GraphRoute? CurrentRoute { get; private set; }
        /// <summary>The path step playing (path tours only).</summary>
        public PathStep? CurrentPathStep =>
            Mode == TourMode.Path && CurrentPath != null && IsTouring && StepIndex >= 0 && StepIndex < CurrentPath.Steps.Count
                ? CurrentPath.Steps[StepIndex] : null;
        /// <summary>True while another component (the featured-paths panel) owns the keyboard.</summary>
        [NonSerialized] public Func<bool>? KeyboardCaptured;
        /// <summary>Raised when a tour starts or ends.</summary>
        public event Action? TourChanged;

        List<int> steps = new();
        Vector3 flyFromPosition, flyToPosition;
        Quaternion flyFromRotation, flyToRotation;
        float flyT = 1f;
        float edgeT = 1f;
        InfluenceEdge? animatedEdge;
        bool advancePending;
        float hudCountdown;
        // Safety net for a synth that stops without raising Finished: time without beat progress.
        double stallSeconds;
        double lastProgressBeat = double.NegativeInfinity;
        (TourMode, SongNode?, InfluenceEdge?, bool, string, bool) idleKey;
        bool idleKeyValid;
        const float HudInterval = 1f / 12f;
        // The mode to return to after a path tour (path tours are started from the panel).
        TourMode modeBeforePath = TourMode.Lineage;

        public void Initialize(SongGraphLoader loader)
        {
            Loader = loader;
            Silent = SongPlayerDiscovery.Silent(gameObject);
            Silent.MorphBars = MorphBars;
            Player = SongPlayerDiscovery.Discover(gameObject, out string description);
            PlayerDescription = description;
            Silent.Finished -= OnClipFinished;
            Silent.Finished += OnClipFinished;
            Preview = GetComponent<PreviewSongPlayer>();
            if (Preview == null) Preview = gameObject.AddComponent<PreviewSongPlayer>();
            Preview.Catalog = Catalog;
            Preview.Finished -= OnClipFinished;
            Preview.Finished += OnClipFinished;
            if (Player != null && !ReferenceEquals(Player, Silent))
            {
                Player.Finished -= OnClipFinished;
                Player.Finished += OnClipFinished;
            }
            if (double.TryParse(loader.Data?.MetaValue("target_bpm"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double target) && target > 0)
                Silent.TargetBpm = target;
            RefreshIdleHud(true);
        }

        /// <summary>The featured paths path tours play (the loader binds it to the graph first).</summary>
        public void UseCatalog(PathCatalog catalog)
        {
            Catalog = catalog ?? PathCatalog.Empty("none");
            if (Preview != null) Preview.Catalog = Catalog;
            if (IsTouring && Mode == TourMode.Path) Exit();
        }

        /// <summary>
        /// Replaces the discovered player (a custom synth, or a probe in validation). Songs whose
        /// MIDI file is missing still play on the silent clock.
        /// </summary>
        public void UsePlayer(ISongPlayer player, string description)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (Player != null && !ReferenceEquals(Player, Silent)) Player.Finished -= OnClipFinished;
            Player = player;
            PlayerDescription = description;
            if (!ReferenceEquals(player, Silent))
            {
                player.Finished -= OnClipFinished;
                player.Finished += OnClipFinished;
            }
            RefreshIdleHud(true);
        }

        void OnDestroy()
        {
            if (Silent != null) Silent.Finished -= OnClipFinished;
            if (Preview != null) Preview.Finished -= OnClipFinished;
            if (Player != null) Player.Finished -= OnClipFinished;
        }

        void Update()
        {
            HandleInput();
            Tick(Time.unscaledDeltaTime);
        }

        void HandleInput()
        {
            Keyboard? k = Keyboard.current;
            if (k == null || Loader == null || Loader.Data == null) return;
            // The featured-paths panel handles its own keys (list open, or a path tour playing).
            if (KeyboardCaptured != null && KeyboardCaptured()) return;
            if (!IsTouring)
            {
                if (k.digit1Key.wasPressedThisFrame || k.numpad1Key.wasPressedThisFrame) Mode = TourMode.Lineage;
                if (k.digit2Key.wasPressedThisFrame || k.numpad2Key.wasPressedThisFrame) Mode = TourMode.Subtree;
                if (k.digit3Key.wasPressedThisFrame || k.numpad3Key.wasPressedThisFrame) Mode = TourMode.Chronological;
                if ((k.digit4Key.wasPressedThisFrame || k.numpad4Key.wasPressedThisFrame) && Loader.Data.HasFamilies) Mode = TourMode.Family;
                if (k.mKey.wasPressedThisFrame) Mode = TourPlanner.NextMode(Mode, Loader.Data.HasFamilies);
                if (k.cKey.wasPressedThisFrame) ApplesToApples = !ApplesToApples;
                if (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame)
                    StartTour(Mode, Loader.Highlighter.Selected, Loader.Highlighter.SelectedEdge);
                return;
            }
            if (k.escapeKey.wasPressedThisFrame) { Exit(); return; }
            if (k.spaceKey.wasPressedThisFrame) TogglePause();
            if (k.nKey.wasPressedThisFrame || k.rightArrowKey.wasPressedThisFrame) Next();
            if (k.bKey.wasPressedThisFrame || k.leftArrowKey.wasPressedThisFrame) Previous();
            if (k.cKey.wasPressedThisFrame) ToggleApplesToApples();
            if (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame) GoTo(StepIndex);
        }

        /// <summary>Redraws the walkthrough panel now (the paths panel opened or closed).</summary>
        public void RefreshHud()
        {
            if (IsTouring) UpdateTourHud();
            else RefreshIdleHud(true);
        }

        /// <summary>C: toggles "compare in C / 120 BPM" (MIDI, no morph); a running tour restarts its step.</summary>
        public void ToggleApplesToApples()
        {
            ApplesToApples = !ApplesToApples;
            if (IsTouring) GoTo(StepIndex);
        }

        public bool StartTour(TourMode mode, SongNode? from) => StartTour(mode, from, null);

        /// <summary>
        /// Starts a tour from <paramref name="from"/>, or from the clicked <paramref name="edge"/>:
        /// a family tour plays the edge's family; the other modes start from the edge's later song.
        /// </summary>
        public bool StartTour(TourMode mode, SongNode? from, InfluenceEdge? edge)
        {
            if (Loader.Data == null) return false;
            if (from == null && edge != null) from = edge.Target;
            if (mode == TourMode.Path)
            {
                // The first playable path through the song, else the first playable path.
                FeaturedPath? chosen = null;
                foreach (FeaturedPath p in Catalog.Paths)
                    if (p.IsPlayable && chosen == null && (from == null || p.Steps.Exists(st => st.NodeId == from.NodeId))) chosen = p;
                if (chosen == null)
                    foreach (FeaturedPath p in Catalog.Paths)
                        if (p.IsPlayable && chosen == null) chosen = p;
                return chosen != null && StartPathTour(chosen);
            }
            if (mode == TourMode.Family)
                return TourPlanner.FamilyFor(Loader.Data, from != null ? from.NodeId : null, edge?.Record) is int family && StartFamilyTour(family);
            return Begin(mode, TourPlanner.Plan(Loader.Data, mode, from != null ? from.NodeId : null), null);
        }

        /// <summary>Plays every song of identity family <paramref name="familyId"/> in time order.</summary>
        public bool StartFamilyTour(int familyId)
        {
            if (Loader.Data == null) return false;
            return Begin(TourMode.Family, TourPlanner.Family(Loader.Data, familyId), familyId);
        }

        /// <summary>
        /// Plays the steps of featured path <paramref name="path"/> in order (each from its recording
        /// preview when the render exists). False when a step's song is not in the graph.
        /// </summary>
        public bool StartPathTour(FeaturedPath path)
        {
            if (Loader.Data == null || path == null || !path.IsPlayable) return false;
            List<int> planned = new();
            foreach (PathStep step in path.Steps)
            {
                if (step.NodeId < 1 || step.NodeId > Loader.Nodes.Count) return false;
                planned.Add(step.NodeId);
            }
            if (Mode != TourMode.Path) modeBeforePath = Mode;
            CurrentPath = path;
            CurrentRoute = Loader.RouteFor(path);
            return Begin(TourMode.Path, planned, null);
        }

        bool Begin(TourMode mode, List<int> planned, int? familyId)
        {
            if (planned.Count == 0) return false;
            if (mode != TourMode.Path)
            {
                CurrentPath = null;
                CurrentRoute = null;
            }
            Mode = mode;
            FamilyId = familyId;
            steps = planned;
            IsTouring = true;
            // A new tour's first song plays natively, whatever was sounding before.
            CurrentClip = null;
            PreviousClip = null;
            SetCameraInput(false);
            GoTo(0);
            TourChanged?.Invoke();
            return true;
        }

        public void GoTo(int index)
        {
            if (!IsTouring || steps.Count == 0) return;
            // What the listener heard just before this step: the clip being left, whichever way
            // the tour moves (null only at the tour start).
            SongClip? heard = CurrentClip;
            StepIndex = Mathf.Clamp(index, 0, steps.Count - 1);
            advancePending = false;
            TourComplete = false;
            if (animatedEdge != null) animatedEdge.VisibleFraction = 1f;

            SongNode child = Loader.NodeById(steps[StepIndex]);
            SongNode? parent = child.TreeParent;
            InfluenceEdge? stepEdge = child.TreeEdge;
            CurrentWindow = null;
            if (Mode == TourMode.Path && CurrentRoute != null)
            {
                // The song heard before on the path is framed beside this one; the route stays lit.
                parent = StepIndex > 0 ? Loader.NodeById(steps[StepIndex - 1]) : null;
                stepEdge = StepIndex < CurrentRoute.StepEdges.Count ? CurrentRoute.StepEdges[StepIndex] : null;
                Loader.Highlighter.ShowPathStep(child, parent, stepEdge, CurrentRoute);
            }
            else if (Mode == TourMode.Family && FamilyId is int family && Loader.Data != null)
            {
                // The previous song of the family (time order) is framed beside this one; the
                // family's edge into this song lights up (from that song when there is one).
                parent = StepIndex > 0 ? Loader.NodeById(steps[StepIndex - 1]) : null;
                EdgeRecord? record = TourPlanner.FamilyStepEdge(Loader.Data, family, child.NodeId, parent?.NodeId);
                stepEdge = record != null ? child.Incoming.Find(e => ReferenceEquals(e.Record, record)) : null;
                CurrentWindow = TourPlanner.Window(Loader.Data, family, child.NodeId);
                Loader.Highlighter.ShowTourStep(child, parent, stepEdge, family);
            }
            else
            {
                Loader.Highlighter.ShowTourStep(child, parent);
            }
            StepEdge = stepEdge;
            StepPartner = parent;
            Loader.Hud.ShowSong(child);

            animatedEdge = stepEdge;
            edgeT = 0f;
            if (animatedEdge != null) animatedEdge.VisibleFraction = 0f;

            Camera? cam = Loader.ViewCamera;
            if (cam != null)
            {
                List<(Vector3, float)> items = new() { (child.transform.position, child.Radius * 1.4f) };
                if (parent != null) items.Add((parent.transform.position, parent.Radius * 1.4f));
                (flyToPosition, flyToRotation) = CameraFraming.Frame(cam, items, Loader.Frame.ViewForward, FramingMargin, MinFramingDistance, TourViewport);
                flyFromPosition = cam.transform.position;
                flyFromRotation = cam.transform.rotation;
                flyT = 0f;
            }

            CurrentClip = Mode == TourMode.Family && FamilyId is int fam && Loader.Data != null
                ? TourPlanner.FamilyClip(Loader.Data, fam, child.NodeId)
                : child.Song.ToClip();
            PreviousClip = heard;
            ISongPlayer chosen = ChoosePlayer(CurrentClip);
            // Stop only when the step moves to another player object. The same player replaces its
            // own clip: one still playing is cut by the new Play (short fade), and one that ended
            // naturally keeps its bar-line handoff grid, which a Stop would throw away.
            if (ActivePlayer != null && !ReferenceEquals(ActivePlayer, chosen)) ActivePlayer.Stop();
            ActivePlayer = chosen;
            ResetWatchdog();
            try
            {
                chosen.MorphBars = MorphBars;
                chosen.ApplesToApples = ApplesToApples;
                if (ReferenceEquals(chosen, Preview) && CurrentPathStep is PathStep pathStep) Preview!.PlayStep(pathStep, CurrentClip);
                else chosen.Play(CurrentClip, PreviousClip!);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"MusicHistory: player failed on '{CurrentClip.Title}' ({e.Message}); continuing silently.");
                if (!ReferenceEquals(chosen, Silent))
                {
                    try
                    {
                        chosen.Stop();
                    }
                    catch (Exception stopError)
                    {
                        Debug.LogWarning($"MusicHistory: player could not stop ({stopError.Message}).");
                    }
                }
                ActivePlayer = Silent;
                Silent.MorphBars = MorphBars;
                Silent.ApplesToApples = ApplesToApples;
                Silent.Play(CurrentClip, PreviousClip);
            }
            // Decode the next recording now, so the crossfade into it starts on time.
            if (Mode == TourMode.Path && CurrentPath != null && Preview != null && StepIndex + 1 < CurrentPath.Steps.Count)
                Preview.Preload(CurrentPath.Steps[StepIndex + 1]);
            UpdateTourHud();
        }

        /// <summary>Re-applies the current step's highlighting (after V toggles the secondary edges).</summary>
        public void RefreshStepHighlight()
        {
            if (!IsTouring || CurrentClip == null) return;
            SongNode child = Loader.NodeById(CurrentClip.NodeId);
            if (Mode == TourMode.Path && CurrentRoute != null) Loader.Highlighter.ShowPathStep(child, StepPartner, StepEdge, CurrentRoute);
            else if (Mode == TourMode.Family && FamilyId is int family) Loader.Highlighter.ShowTourStep(child, StepPartner, StepEdge, family);
            else Loader.Highlighter.ShowTourStep(child, StepPartner);
        }

        /// <summary>
        /// A path tour's step with its render: the recording preview. Otherwise the real player when
        /// this clip's MIDI file exists, else the silent clock.
        /// </summary>
        ISongPlayer ChoosePlayer(SongClip clip)
        {
            // Recording previews play only the steps listed in paths.json, on a path tour.
            if (Mode == TourMode.Path && !ApplesToApples && Preview != null && CurrentPathStep is PathStep step &&
                step.NodeId == clip.NodeId && step.FileExists) return Preview;
            string? path = ApplesToApples && !string.IsNullOrEmpty(clip.NormalizedMidiPath) ? clip.NormalizedMidiPath : clip.MidiPath;
            bool fileExists = !string.IsNullOrEmpty(path) && File.Exists(path);
            if (Player != null && !ReferenceEquals(Player, Silent) && fileExists) return Player;
            return Silent;
        }

        void OnClipFinished(SongClip clip)
        {
            if (!IsTouring || !ReferenceEquals(clip, CurrentClip)) return;
            // Advance on the next tick: the player may still be inside its own update.
            advancePending = true;
        }

        public void Next()
        {
            if (!IsTouring) return;
            if (StepIndex + 1 < steps.Count) GoTo(StepIndex + 1);
        }

        public void Previous()
        {
            if (!IsTouring) return;
            GoTo(Mathf.Max(0, StepIndex - 1));
        }

        public void TogglePause()
        {
            if (ActivePlayer != null) ActivePlayer.Paused = !ActivePlayer.Paused;
        }

        public void Exit()
        {
            if (!IsTouring) return;
            ActivePlayer?.Stop();
            ActivePlayer = null;
            if (animatedEdge != null) animatedEdge.VisibleFraction = 1f;
            animatedEdge = null;
            IsTouring = false;
            TourComplete = false;
            advancePending = false;
            flyT = 1f;
            CurrentClip = null;
            PreviousClip = null;
            FamilyId = null;
            StepEdge = null;
            StepPartner = null;
            CurrentWindow = null;
            CurrentPath = null;
            CurrentRoute = null;
            if (Mode == TourMode.Path) Mode = modeBeforePath;
            Loader.Highlighter.EndTour();
            if (Loader.Highlighter.FocusEdge != null) Loader.Hud.ShowEdge(Loader.Highlighter.FocusEdge);
            else Loader.Hud.ShowSong(Loader.Highlighter.Focus);
            SetCameraInput(true);
            RefreshIdleHud(true);
            TourChanged?.Invoke();
        }

        void SetCameraInput(bool enabled)
        {
            Camera? cam = Loader.ViewCamera;
            if (cam == null) return;
            CameraControl control = cam.GetComponent<CameraControl>();
            if (control == null) return;
            control.InputEnabled = enabled;
            if (enabled) control.SyncRotationFromTransform();
        }

        void ResetWatchdog()
        {
            stallSeconds = 0;
            lastProgressBeat = double.NegativeInfinity;
        }

        /// <summary>Advances animations by <paramref name="dt"/> seconds (public so edit-mode validation can drive it).</summary>
        public void Tick(float dt)
        {
            if (Loader == null || Loader.Data == null) return;
            if (!IsTouring)
            {
                RefreshIdleHud(false);
                return;
            }
            if (advancePending)
            {
                advancePending = false;
                if (StepIndex + 1 < steps.Count) GoTo(StepIndex + 1);
                else TourComplete = true;
            }

            ISongPlayer? active = ActivePlayer;
            if (active != null && !active.Paused && !TourComplete && !advancePending)
            {
                // Progress, not a precomputed duration: the real player follows the file's tempo map,
                // which can be far slower than its median BPM in places. The clock counts real
                // time, but stands still while Time.timeScale freezes the music.
                double beat = active.CurrentBeat;
                if (beat > lastProgressBeat + 1e-9)
                {
                    lastProgressBeat = beat;
                    stallSeconds = 0;
                }
                else
                {
                    stallSeconds += dt * Math.Min(1f, Math.Max(0f, Time.timeScale));
                }
                bool silentlyStopped = !ReferenceEquals(active, Silent) && !active.IsPlaying && stallSeconds > 3.0;
                if (stallSeconds > WatchdogSeconds || silentlyStopped)
                {
                    Debug.LogWarning($"MusicHistory: '{CurrentClip?.Title}' made no progress for {stallSeconds:0.0} s and did not report Finished; advancing.");
                    advancePending = true;
                }
            }

            Camera? cam = Loader.ViewCamera;
            if (cam != null && flyT < 1f)
            {
                flyT = Mathf.Min(1f, flyT + dt / FlyDuration);
                float u = CameraFraming.SmoothStep01(flyT);
                cam.transform.SetPositionAndRotation(
                    Vector3.Lerp(flyFromPosition, flyToPosition, u),
                    Quaternion.Slerp(flyFromRotation, flyToRotation, u));
            }
            if (animatedEdge != null && edgeT < 1f)
            {
                // The edge starts growing once the flight is half way, so the eye can follow it.
                if (flyT > .5f) edgeT = Mathf.Min(1f, edgeT + dt / EdgeGrowDuration);
                animatedEdge.VisibleFraction = CameraFraming.SmoothStep01(edgeT);
            }
            // The HUD's live key/BPM/beat readout refreshes at 12 Hz (text rebuilds allocate).
            hudCountdown -= dt;
            if (hudCountdown <= 0f)
            {
                hudCountdown = HudInterval;
                UpdateTourHud();
            }
        }

        /// <summary>
        /// The key the normalized file plays <paramref name="clip"/> in: its home key moved by its
        /// norm_shift, so "A minor" under relative normalization and "C minor" under parallel.
        /// </summary>
        public static string NormalizedKeyName(SongClip clip) => SongPalette.KeyName(clip.TonicPc + clip.NormShift, clip.Minor);

        void UpdateTourHud()
        {
            if (CurrentClip == null || steps.Count == 0) return;
            if (Mode == TourMode.Path && CurrentPath != null)
            {
                UpdatePathHud();
                return;
            }
            SongNode child = Loader.NodeById(CurrentClip.NodeId);
            // The framed partner: the tree parent, or the family's previous song on a family tour.
            SongNode? parent = StepPartner;
            InfluenceEdge? edge = StepEdge;
            SongGraphData data = Loader.Data!;
            IdentityFamily? family = Mode == TourMode.Family ? data.Family(FamilyId) : null;
            ISongPlayer? p = ActivePlayer;
            double length = Math.Max(1e-6, CurrentClip.ExcerptEndBeat - CurrentClip.ExcerptStartBeat);
            double into = p != null ? Math.Max(0, p.CurrentBeat - CurrentClip.ExcerptStartBeat) : 0;
            string state = TourComplete ? "tour complete" : p != null && p.Paused ? "paused" : "playing";
            string playerName = ReferenceEquals(p, Silent)
                ? (Player != null && !ReferenceEquals(Player, Silent) ? "silent (MIDI file missing)" : "silent (no synth)")
                : ReferenceEquals(p, Preview) ? "recording preview" : "synth";
            string channelColor = edge != null ? SongPalette.ToHex(SongPalette.ChannelColor(edge.Channel))
                : family != null ? SongPalette.ToHex(SongPalette.ChannelColor(family.Kind == "loop" ? EdgeChannel.Loop : EdgeChannel.Chord))
                : "#f4f6fb";

            string header = $"<b>WALKTHROUGH</b> · {Mode.ToString().ToLowerInvariant()} · step {StepIndex + 1}/{steps.Count} · {state}" +
                            $" · <color={GraphHud.Muted}>{playerName}</color>";
            string pair = parent != null
                ? $"{GraphHud.Esc(parent.Song.Title)} ({parent.Song.Year}) <color={channelColor}>→</color> <b>{GraphHud.Esc(child.Song.Title)}</b> ({child.Song.Year})" +
                  $" · <color={GraphHud.Muted}>{GraphHud.Esc(child.Song.Artist)}</color>"
                : $"<b>{GraphHud.Esc(child.Song.Title)}</b> ({child.Song.Year}) · <color={GraphHud.Muted}>{GraphHud.Esc(child.Song.Artist)} · " +
                  $"{(family != null ? "the family's earliest song" : "root of its tree")}</color>";
            if (family != null)
            {
                // Family tour: the identity every step shares, even a song that joins it without an edge of it.
                pair += "\n<size=85%>" + GraphHud.SharesText(family, family.Label, edge?.Record, channelColor);
                if (CurrentWindow is FamilyWindow w && w.Source != FamilyWindow.OwnExcerpt)
                {
                    double bpb = child.Song.BeatsPerBar > 0 ? child.Song.BeatsPerBar : 4;
                    int firstBar = (int)Math.Round((w.Start - child.Song.FirstDownbeat) / bpb) + 1;
                    int lastBar = (int)Math.Round((w.End - child.Song.FirstDownbeat) / bpb);
                    pair += $" <color={GraphHud.Muted}>· plays bars {firstBar}–{lastBar} ({w.Source}; home key assumed)</color>";
                }
                pair += "</size>";
            }
            else if (edge != null && data.IsIdentityLineage)
            {
                pair += "\n<size=85%>" + GraphHud.SharesText(data, edge.Record, channelColor) + "</size>";
            }
            else if (edge != null && !string.IsNullOrEmpty(edge.Record.Evidence))
            {
                pair += $"\n<size=85%><color={GraphHud.Muted}>{GraphHud.Esc(edge.Record.Evidence)}</color></size>";
            }

            string morph;
            if (ApplesToApples)
            {
                morph = $"Key {GraphHud.Esc(child.Song.KeyName)} → {NormalizedKeyName(CurrentClip)} (normalized) · BPM {Fmt(Silent.TargetBpm, "0")} (normalized)";
            }
            else
            {
                // The player's own plan: its tempo ratio is relative to the file's entry tempo, so
                // the start BPM comes from the player when it knows it.
                MorphPlan plan = CurrentPlan;
                int entryTonic = CurrentClip.EntryTonic;
                bool entryMinor = CurrentClip.EntryIsMinor;
                string startKey = SongPalette.KeyName(entryTonic + (int)Math.Round(plan.StartSemitones), entryMinor);
                string nativeKey = SongPalette.KeyName(entryTonic, entryMinor);
                double startBpm = p is IMorphReadout readout && readout.PlanStartBpm > 0
                    ? readout.PlanStartBpm
                    : CurrentClip.NativeBpm * plan.StartTempoRatio;
                double semis = p?.CurrentSemitones ?? plan.StartSemitones;
                double bpm = p?.CurrentBpm ?? startBpm;
                string nowKey = SongPalette.PitchName(entryTonic + (int)Math.Round(semis), entryMinor);
                morph = $"Key {startKey} → {nativeKey} <color={GraphHud.Muted}>(now {nowKey}, {Fmt(semis, "+0.0;-0.0;0.0")} st)</color>" +
                        $" · BPM {Fmt(startBpm, "0.#")} → {Fmt(CurrentClip.NativeBpm, "0.#")}" +
                        $" <color={GraphHud.Muted}>(now {Fmt(bpm, "0.0")})</color>";
            }
            morph += $" · beat {Fmt(into, "0.0")}/{Fmt(length, "0")}";
            string keys = $"<size=85%><color={GraphHud.Muted}>Space pause · N/→ next · B/← back · Esc exit · C {(ApplesToApples ? "native key and tempo" : "compare in C / 120 BPM")}</color></size>";
            string text = $"{header}\n{pair}\n{morph}\n{keys}";
            HudText = text;
            Loader.Hud.ShowTour(text, (float)(into / length), SongPalette.Hex(channelColor));
        }

        void RefreshIdleHud(bool force)
        {
            if (Loader == null || Loader.Hud == null || Loader.Data == null) return;
            SongNode? selected = Loader.Highlighter != null ? Loader.Highlighter.Selected : null;
            InfluenceEdge? selectedEdge = Loader.Highlighter != null ? Loader.Highlighter.SelectedEdge : null;
            bool pathsOpen = Loader.Paths != null && Loader.Paths.IsOpen;
            (TourMode, SongNode?, InfluenceEdge?, bool, string, bool) key = (Mode, selected, selectedEdge, ApplesToApples, PlayerDescription, pathsOpen);
            if (!force && idleKeyValid && key.Equals(idleKey)) return;
            idleKey = key;
            idleKeyValid = true;
            if (pathsOpen)
            {
                // The featured-paths panel owns Enter and the digits while it is open.
                HudText = "";
                Loader.Hud.ShowTour("", 0f, Color.clear);
                return;
            }
            SongNode? start = selected != null ? selected : selectedEdge != null ? selectedEdge.Target : null;
            string from = Mode == TourMode.Family
                ? FamilyIdleText(Loader.Data, selected, selectedEdge)
                : start != null
                    ? $"from <b>{GraphHud.Esc(start.Song.Title)}</b>"
                    : Mode switch
                    {
                        TourMode.Lineage => "along the deepest lineage (click a song to choose)",
                        TourMode.Subtree => "through the largest tree (click a song to choose)",
                        _ => "from the first song (click a song to choose)"
                    };
            string modes = Loader.Data.HasFamilies ? "1/2/3/4, M" : "1/2/3, M";
            string text = $"<b>Walkthrough</b> <b>{Mode.ToString().ToLowerInvariant()}</b> <color={GraphHud.Muted}>({modes})</color>" +
                          $" · Enter: play {from}" +
                          $" · <color={GraphHud.Muted}>C {(ApplesToApples ? "in C / 120 BPM" : "native keys")} · {PlayerDescription}</color>";
            HudText = text;
            Loader.Hud.ShowTour(text, 0f, Color.clear);
        }

        /// <summary>What Enter plays in family mode: which family, how many songs, and why that one.</summary>
        static string FamilyIdleText(SongGraphData data, SongNode? selected, InfluenceEdge? selectedEdge)
        {
            if (!data.HasFamilies) return "nothing: family mode needs an identity-lineage graph (DESIGN §8b)";
            int? id = TourPlanner.FamilyFor(data, selected != null ? selected.NodeId : null, selectedEdge?.Record);
            if (data.Family(id) is not IdentityFamily f)
                return $"nothing: <b>{GraphHud.Esc(selected?.Song.Title)}</b> belongs to no identity family (click an edge or another song)";
            string why = selectedEdge?.Record.FamilyId == f.FamilyId ? "the selected edge's identity"
                : selected != null ? $"the strongest identity of {GraphHud.Esc(selected.Song.Title)}"
                : "the largest family; click a song or an edge to choose";
            return $"every song of <b>{GraphHud.Esc(f.Label)}</b> in time order ({f.Members.Count} songs; {why})";
        }

        static string Fmt(double v, string format) => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ path tours

        /// <summary>What the step sounds like right now, for the path HUD (seconds for recordings, beats for MIDI).</summary>
        public readonly struct StepReadout
        {
            public readonly bool Valid;
            /// <summary>A recording preview plays (else the MIDI synth or the silent clock).</summary>
            public readonly bool Recording;
            public readonly string Player;
            /// <summary>Key the step starts in (the song heard before, as heard), its own key, and the key sounding now.</summary>
            public readonly string StartKey, Key, NowKey;
            public readonly double StartSemitones, NowSemitones;
            public readonly double StartBpm, Bpm, NowBpm;
            /// <summary>Glide progress 0..1 (1 = the song's own key and tempo).</summary>
            public readonly double Glide;
            /// <summary>Position and length: seconds when <see cref="InSeconds"/>, else beats.</summary>
            public readonly double Position, Length;
            public readonly bool InSeconds;
            /// <summary>"audio" (measured from the recording) or "midi" (paths.json key_source / bpm_source).</summary>
            public readonly string KeySource, BpmSource;

            public StepReadout(bool recording, string player, string startKey, string key, string nowKey,
                double startSemitones, double nowSemitones, double startBpm, double bpm, double nowBpm, double glide,
                double position, double length, bool inSeconds, string keySource, string bpmSource)
            {
                Valid = true;
                Recording = recording;
                Player = player;
                StartKey = startKey;
                Key = key;
                NowKey = nowKey;
                StartSemitones = startSemitones;
                NowSemitones = nowSemitones;
                StartBpm = startBpm;
                Bpm = bpm;
                NowBpm = nowBpm;
                Glide = glide;
                Position = position;
                Length = length;
                InSeconds = inSeconds;
                KeySource = keySource;
                BpmSource = bpmSource;
            }

            public double Progress => Length > 0 ? Math.Max(0, Math.Min(1, Position / Length)) : 0;
        }

        public string ActivePlayerName
        {
            get
            {
                ISongPlayer? p = ActivePlayer;
                if (p == null) return "";
                if (ReferenceEquals(p, Preview)) return "recording preview";
                if (ReferenceEquals(p, Silent))
                    return Player != null && !ReferenceEquals(Player, Silent) ? "silent (MIDI file missing)" : "silent (no synth)";
                return ApplesToApples ? "synth (compare in C / 120 BPM)" : "synth";
            }
        }

        /// <summary>The current step's key/BPM transition and position (the HUD and validation read the same numbers).</summary>
        public StepReadout Readout()
        {
            SongClip? clip = CurrentClip;
            ISongPlayer? p = ActivePlayer;
            if (clip == null || p == null) return default;
            if (p is PreviewSongPlayer preview && preview.CurrentStep is PathStep s)
            {
                double semis = preview.CurrentSemitones;
                string now = KeyText.TryParse(s.Key, out int pc, out bool minor)
                    ? SongPalette.KeyName(pc + (int)Math.Round(semis), minor)
                    : s.Key;
                return new StepReadout(true, ActivePlayerName, s.Glides ? s.StartKey : s.Key, s.Key, now,
                    s.EntrySemitones, semis, s.EntryBpm, s.Bpm, preview.CurrentBpm, preview.GlideProgress,
                    preview.CurrentSeconds, preview.DurationSeconds, true, s.KeySource, s.BpmSource);
            }
            double length = Math.Max(1e-6, clip.ExcerptEndBeat - clip.ExcerptStartBeat);
            double into = Math.Max(0, Math.Min(length, p.CurrentBeat - clip.ExcerptStartBeat));
            if (ApplesToApples)
            {
                string normalized = NormalizedKeyName(clip);
                SongNode node = Loader.NodeById(clip.NodeId);
                return new StepReadout(false, ActivePlayerName, node.Song.KeyName, normalized, normalized, 0, 0,
                    Silent.TargetBpm, Silent.TargetBpm, Silent.TargetBpm, 1, into, length, false, "midi", "midi");
            }
            MorphPlan plan = CurrentPlan;
            int entryTonic = clip.EntryTonic;
            bool entryMinor = clip.EntryIsMinor;
            double startBpm = p is IMorphReadout readout && readout.PlanStartBpm > 0 ? readout.PlanStartBpm : clip.NativeBpm * plan.StartTempoRatio;
            double nowSemis = p.CurrentSemitones;
            return new StepReadout(false, ActivePlayerName,
                SongPalette.KeyName(entryTonic + (int)Math.Round(plan.StartSemitones), entryMinor),
                SongPalette.KeyName(entryTonic, entryMinor),
                SongPalette.KeyName(entryTonic + (int)Math.Round(nowSemis), entryMinor),
                plan.StartSemitones, nowSemis, startBpm, clip.NativeBpm, p.CurrentBpm, plan.Progress(into),
                into, length, false, "midi", "midi");
        }

        /// <summary>Path tours: the panel's now-playing strip replaces the bottom walkthrough panel.</summary>
        void UpdatePathHud()
        {
            FeaturedPath path = CurrentPath!;
            SongClip clip = CurrentClip!;
            StepReadout r = Readout();
            PathStep? step = StepIndex < path.Steps.Count ? path.Steps[StepIndex] : null;
            string state = TourComplete ? "complete" : ActivePlayer != null && ActivePlayer.Paused ? "paused" : "playing";
            string from = StepPartner != null ? $"{StepPartner.Song.Title} ({StepPartner.Song.Year}) → " : "";
            SongNode child = Loader.NodeById(clip.NodeId);
            string via = step?.Via is PathVia v
                ? $"\nvia {v.Identity}{(v.Strong ? $" · strong match (z {Fmt(v.Z, "0.0")})" : "")}"
                : "\nfirst song: plays in its own key and tempo";
            string position = r.InSeconds
                ? $"{PathCatalog.Clock(r.Position)} / {PathCatalog.Clock(r.Length)}"
                : $"beat {Fmt(r.Position, "0.0")}/{Fmt(r.Length, "0")}";
            HudText = $"PATH · {path.Title} · step {StepIndex + 1}/{steps.Count} · {state} · {r.Player}\n" +
                      $"{from}{child.Song.Title} ({child.Song.Year}) · {child.Song.Artist}{via}\n" +
                      $"Key {r.StartKey} → {r.Key} (now {r.NowKey}, {Fmt(r.NowSemitones, "+0.0;-0.0;0.0")} st)" +
                      $" · BPM {Fmt(r.StartBpm, "0.#")} → {Fmt(r.Bpm, "0.#")} (now {Fmt(r.NowBpm, "0.0")}) · {position}";
            Loader.Hud.ShowTour("", 0f, Color.clear);
            if (Loader.Paths != null) Loader.Paths.RefreshNowPlaying();
        }
    }
}
