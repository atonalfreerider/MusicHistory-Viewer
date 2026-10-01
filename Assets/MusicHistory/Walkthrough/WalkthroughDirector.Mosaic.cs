#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using UnityEngine;

namespace MusicHistory.Walkthrough
{
    /// <summary>
    /// Melody mosaics (DESIGN.md §17): one mix played once on <see cref="MosaicPlayer"/> — the target's
    /// loop (original), the loop rebuilt from other songs' melodies (mosaic), the loop harmonized
    /// (harmony) — with no narration. The step is the loop playing (<see cref="MosaicLoop"/>); the
    /// songs sounding glow at three times their size: the target in the original and harmony loops
    /// (with the harmony voices' songs), the playing piece's song in the mosaic loops (the target
    /// faintly beside it). The camera holds one zoom for the whole mosaic (the median distance that
    /// frames each of its songs beside the target) and only pans, slowly, to the songs singing now,
    /// or to the singing song alone when the pair is too far apart: it never zooms per piece; the 3x
    /// bubble and its light show who sings. Next / Back jump to the next / previous loop, a
    /// section chip (<see cref="GoToMosaicSection"/>) to its section's first loop.
    /// </summary>
    public sealed partial class WalkthroughDirector
    {
        /// <summary>Plays a melody mosaic's mix (data/audio/mosaics).</summary>
        public MosaicPlayer? MosaicAudio { get; private set; }
        /// <summary>The melody mosaics (data/audio/mosaics/mosaics.json), bound to the graph.</summary>
        public MosaicCatalog Mosaics { get; private set; } = MosaicCatalog.Empty("not loaded");
        /// <summary>The mosaic a mosaic tour plays (null for the other modes).</summary>
        public Mosaic? PlayingMosaic { get; private set; }
        /// <summary>The mosaic playing now (null when no mosaic tour runs).</summary>
        public Mosaic? CurrentMosaic => IsTouring && Mode == TourMode.Mosaic ? PlayingMosaic : null;
        /// <summary>Seconds into the mosaic's mix (0 when none plays).</summary>
        public double MosaicSeconds => CurrentMosaic != null && MosaicAudio != null ? MosaicAudio.CurrentSeconds : 0;
        /// <summary>Where the mosaic is (mix beat, loop, section, the piece sounding), as of the last tick.</summary>
        public MosaicTimeline.State MosaicState { get; private set; }
        /// <summary>The loop playing (0 .. loops − 1; -1 when no mosaic plays).</summary>
        public int MosaicLoop { get; private set; } = -1;
        /// <summary>Index of the section playing (-1 when no mosaic plays).</summary>
        public int MosaicSectionIndex { get; private set; } = -1;
        public MosaicSection? CurrentMosaicSection =>
            CurrentMosaic is Mosaic m && MosaicSectionIndex >= 0 && MosaicSectionIndex < m.Sections.Count ? m.Sections[MosaicSectionIndex] : null;
        /// <summary>The piece sounding (-1 between pieces and outside the mosaic loops).</summary>
        public int MosaicPiece { get; private set; } = -1;
        /// <summary>The piece shown as playing (the one sounding, else the last before the playhead in the loop; -1 outside the mosaic loops).</summary>
        public int MosaicFocusPiece { get; private set; } = -1;
        /// <summary>The songs glowing at three times their size now.</summary>
        public IReadOnlyList<SongNode> MosaicLit => mosaicLit;
        /// <summary>The songs the camera frames: every song of the mosaic in the graph, held for the whole mix.</summary>
        public IReadOnlyList<SongNode> MosaicFramed => mosaicFramed;

        readonly List<SongNode> mosaicLit = new(), mosaicFramed = new(), mosaicMembers = new();
        (int loop, int section, int piece) mosaicKey = (-1, -1, -1);
        string mosaicFrameKey = "";
        float mosaicDistance;
        (List<SongNode> nodes, SongNode focus, SongNode target)? mosaicShot;
        float? mosaicDepth;
        readonly List<SongNode> mosaicShotFramed = new();
        /// <summary>The songs the camera frames now (the singing songs, or the singing song alone when they don't fit).</summary>
        public IReadOnlyList<SongNode> MosaicShotFramed => mosaicShotFramed;
        /// <summary>The camera's held distance for this mosaic (0 before it is chosen).</summary>
        public float MosaicDistance => mosaicDistance;

        /// <summary>The melody mosaics mosaic tours play (the loader binds them first).</summary>
        public void UseMosaics(MosaicCatalog catalog)
        {
            Mosaics = catalog ?? MosaicCatalog.Empty("none");
            if (IsTouring && Mode == TourMode.Mosaic) Exit();
        }

        /// <summary>Plays <paramref name="mosaic"/>'s mix from its start. False when its target is not in the graph.</summary>
        public bool StartMosaic(Mosaic mosaic)
        {
            if (Loader == null || Loader.Data == null || mosaic == null || !mosaic.IsPlayable) return false;
            List<int> planned = new();
            foreach (MosaicSong s in mosaic.Songs)
                if (s.NodeId >= 1 && s.NodeId <= Loader.Nodes.Count && !planned.Contains(s.NodeId)) planned.Add(s.NodeId);
            if (planned.Count == 0 || planned[0] != mosaic.Target.NodeId) return false;
            if (Mode != TourMode.Path && Mode != TourMode.Mosaic) modeBeforePath = Mode;
            // A path or another mosaic playing stops first.
            if (IsTouring) ActivePlayer?.Stop();
            CurrentPath = null;
            CurrentRoute = null;
            PathMashup = null;
            PathDuet = null;
            DuetMode = false;
            PlayingMosaic = mosaic;
            mosaicMembers.Clear();
            foreach (int id in planned) mosaicMembers.Add(Loader.NodeById(id));
            return Begin(TourMode.Mosaic, planned, null);
        }

        void ResetMosaicState()
        {
            MosaicState = default;
            MosaicLoop = -1;
            MosaicSectionIndex = -1;
            MosaicPiece = -1;
            MosaicFocusPiece = -1;
            mosaicKey = (-1, -1, -1);
            mosaicFrameKey = "";
            mosaicDistance = 0f;
            mosaicShot = null;
            mosaicDepth = null;
            mosaicShotFramed.Clear();
            mosaicLit.Clear();
            mosaicFramed.Clear();
        }

        /// <summary>Mosaic: plays the mix from the start of loop <paramref name="index"/>.</summary>
        void GoToMosaic(int index)
        {
            Mosaic m = CurrentMosaic!;
            MosaicPlayer player = MosaicAudio!;
            int loop = Mathf.Clamp(index, 0, Math.Max(0, m.Loops - 1));
            advancePending = false;
            TourComplete = false;
            if (ActivePlayer != null && !ReferenceEquals(ActivePlayer, player)) ActivePlayer.Stop();
            ActivePlayer = player;
            ResetWatchdog();
            player.MorphBars = MorphBars;
            player.PlayMosaic(m, m.LoopStartSeconds(loop), Loader.NodeById(m.Target.NodeId).Song.ToClip());
            SyncMosaic(player.CurrentSeconds, force: true);
            UpdateTourHud();
        }

        /// <summary>A section chip: plays the mix from section <paramref name="section"/>'s first loop. False when no mosaic plays.</summary>
        public bool GoToMosaicSection(int section)
        {
            if (CurrentMosaic is not Mosaic m || m.Sections.Count == 0) return false;
            GoTo(m.Sections[Mathf.Clamp(section, 0, m.Sections.Count - 1)].FirstLoop);
            return true;
        }

        /// <summary>Follows the mix: a new loop, section or piece changes the lit songs, the wheel's song and the framing.</summary>
        void SyncMosaic(double t, bool force = false)
        {
            Mosaic? m = CurrentMosaic;
            if (m == null) return;
            MosaicTimeline.State st = MosaicTimeline.At(m, t);
            MosaicState = st;
            MosaicPiece = st.Piece;
            (int, int, int) key = (st.Loop, st.Section, st.FocusPiece);
            if (!force && key == mosaicKey) return;
            mosaicKey = key;
            MosaicLoop = st.Loop;
            MosaicSectionIndex = st.Section;
            MosaicFocusPiece = st.FocusPiece;
            StepIndex = Math.Max(0, st.Loop);
            VocalStepIndex = -1;

            SongNode target = Loader.NodeById(m.Target.NodeId);
            SongNode? Node(int song) =>
                song >= 0 && song < m.Songs.Count && m.Songs[song].NodeId >= 1 && m.Songs[song].NodeId <= Loader.Nodes.Count ? Loader.NodeById(m.Songs[song].NodeId) : null;
            mosaicLit.Clear();
            SongNode focus = target;
            SongNode? related = null;
            switch (st.Kind)
            {
                case MosaicSectionKind.Mosaic:
                    SongNode? source = st.FocusPiece >= 0 ? Node(m.Pieces[st.FocusPiece].Song) : null;
                    if (source != null && source != target)
                    {
                        // The piece's song sings over the target's band: it glows, the target beside it.
                        focus = source;
                        related = target;
                        mosaicLit.Add(source);
                    }
                    else mosaicLit.Add(target);
                    break;
                case MosaicSectionKind.Harmony:
                    mosaicLit.Add(target);
                    foreach (MosaicHarmony h in m.Harmonies)
                        if (Node(h.Song) is SongNode voice && !mosaicLit.Contains(voice)) mosaicLit.Add(voice);
                    break;
                default:
                    mosaicLit.Add(target);
                    break;
            }
            if (CurrentClip == null || CurrentClip.NodeId != focus.NodeId) CurrentClip = focus.Song.ToClip();
            PreviousClip = null;
            CurrentWindow = null;
            StepEdge = null;
            StepPartner = related;
            if (animatedEdge != null) animatedEdge.VisibleFraction = 1f;
            animatedEdge = null;
            Loader.Highlighter.ShowMosaicStep(mosaicLit, related, mosaicMembers, focus);
            Loader.Hud.ShowSong(focus);
            // One steady framing for the whole mosaic (it flies there once; seeks and pieces never move it).
            if (mosaicFramed.Count == 0)
            {
                mosaicFramed.AddRange(mosaicMembers);
                if (!mosaicFramed.Contains(target)) mosaicFramed.Insert(0, target);
            }
            // One zoom for the whole mosaic; the camera only pans, slowly, to the songs singing now.
            List<SongNode> shot = new(mosaicLit);
            if (related != null && !shot.Contains(related)) shot.Add(related);
            StringBuilder frame = new();
            foreach (SongNode n in shot) frame.Append(n.NodeId).Append(',');
            string frameKey = frame.ToString();
            if (frameKey != mosaicFrameKey)
            {
                mosaicFrameKey = frameKey;
                mosaicShot = (shot, focus, target);
                PanToNodes(shot, focus, target);
            }
            SegmentChanged?.Invoke();
        }


        (Vector3, float) MosaicItem(SongNode n) => (n.transform.position, n.Radius * SongNode.HighlightScale * 1.4f);

        /// <summary>
        /// The held distance: the median of what each song of the mosaic needs to be framed beside the
        /// target (so most pieces show both), never closer than the target alone needs.
        /// </summary>
        float ChooseMosaicDistance(Camera cam, SongNode target)
        {
            float Need(List<(Vector3, float)> items)
            {
                CameraFraming.FrameAt(cam, items, Loader.Frame.ViewForward, 0f, FramingMargin, FramingViewport, out _, out float needed);
                return needed;
            }
            float alone = Mathf.Max(MinFramingDistance, Need(new() { MosaicItem(target) }));
            List<float> pairs = new();
            foreach (SongNode n in mosaicFramed)
                if (n != target) pairs.Add(Need(new() { MosaicItem(target), MosaicItem(n) }));
            pairs.Sort();
            return Mathf.Max(alone, pairs.Count > 0 ? pairs[pairs.Count / 2] : alone);
        }

        /// <summary>
        /// Pans (zoom held) to frame <paramref name="nodes"/>; when they don't fit at the held distance,
        /// to <paramref name="focus"/> alone.
        /// </summary>
        void PanToNodes(IReadOnlyList<SongNode> nodes, SongNode focus, SongNode target)
        {
            Camera? cam = Loader.ViewCamera;
            if (cam == null || nodes.Count == 0) return;
            if (mosaicDistance <= 0f) mosaicDistance = ChooseMosaicDistance(cam, target);
            List<(Vector3, float)> items = new();
            foreach (SongNode n in nodes) items.Add(MosaicItem(n));
            (Vector3 p, Quaternion r) = CameraFraming.FrameAt(cam, items, Loader.Frame.ViewForward, mosaicDistance, FramingMargin, FramingViewport, out bool fits, out _);
            mosaicShotFramed.Clear();
            if (fits) mosaicShotFramed.AddRange(nodes);
            else
            {
                (p, r) = CameraFraming.FrameAt(cam, new List<(Vector3, float)> { MosaicItem(focus) }, Loader.Frame.ViewForward, mosaicDistance,
                    FramingMargin, FramingViewport, out _, out _);
                mosaicShotFramed.Add(focus);
            }
            // One depth plane for the whole mosaic (songs sit at different depths in the graph): the
            // camera moves only across the view, so the zoom never changes.
            Vector3 fwd = r * Vector3.forward;
            float depth = Vector3.Dot(p, fwd);
            if (mosaicDepth is float held) p += fwd * (held - depth);
            else mosaicDepth = depth;
            (flyToPosition, flyToRotation) = (p, r);
            flyFromPosition = cam.transform.position;
            flyFromRotation = cam.transform.rotation;
            flyT = 0f;
            flySlow = true;
        }

        /// <summary>The framing area changed (layout, orientation): the zoom is chosen again for it.</summary>
        void RepanMosaic()
        {
            if (mosaicShot is not { } s) return;
            mosaicDistance = 0f;
            mosaicDepth = null;
            PanToNodes(s.nodes, s.focus, s.target);
        }

        void OnMosaicFinished(Mosaic m)
        {
            if (!IsTouring || !ReferenceEquals(CurrentMosaic, m)) return;
            TourComplete = true;
            UpdateTourHud();
        }

        /// <summary>
        /// What is heard now, as one line (plain text): the original's song, the piece sounding
        /// ("Fireflies, 2009 · 3 semitones down · 84% speed · 4/4 notes match"), or the harmony voices.
        /// </summary>
        public static string MosaicCaption(Mosaic m, MosaicTimeline.State st)
        {
            switch (st.Kind)
            {
                case MosaicSectionKind.Mosaic:
                    return st.Piece >= 0 && st.Piece < m.Pieces.Count
                        ? Mosaic.PieceCaption(m.Pieces[st.Piece])
                        : "between pieces: no song covers this beat";
                case MosaicSectionKind.Harmony:
                    List<string> voices = new();
                    foreach (MosaicHarmony h in m.Harmonies) voices.Add(Mosaic.HarmonyCaption(h));
                    return string.Join("  +  ", voices);
                default:
                    return $"{m.Target.TitleYear} · {m.Target.Artist} · the melody to rebuild";
            }
        }

        /// <summary>Mosaics: the panel's now-playing strip replaces the bottom walkthrough panel.</summary>
        void UpdateMosaicHud()
        {
            Mosaic m = CurrentMosaic!;
            MosaicTimeline.State st = MosaicState;
            string state = TourComplete ? "complete" : ActivePlayer != null && ActivePlayer.Paused ? "paused" : "playing";
            double t = MosaicSeconds, length = MosaicAudio != null && MosaicAudio.DurationSeconds > 0 ? MosaicAudio.DurationSeconds : m.Duration;
            string section = st.Section >= 0 ? $"{MosaicSection.Title(st.Kind)} section {st.Section + 1}/{m.Sections.Count}" : "";
            HudText = $"MOSAIC · {m.Name} · loop {Math.Max(0, st.Loop) + 1}/{m.Loops} · {state} · {ActivePlayerName}\n" +
                      $"{m.StripTitle} · {section}\n" +
                      $"{MosaicCaption(m, st)} · Key {m.Key} · BPM {Fmt(m.Bpm, "0.#")} · coverage {MosaicCatalog.Percent(m.Coverage)} · match {MosaicCatalog.Percent(m.Match)} · " +
                      $"{PathCatalog.Clock(t)} / {PathCatalog.Clock(length)}";
            Loader.Hud.ShowTour("", 0f, Color.clear);
            if (Loader.Paths != null) Loader.Paths.RefreshNowPlaying();
        }
    }
}
