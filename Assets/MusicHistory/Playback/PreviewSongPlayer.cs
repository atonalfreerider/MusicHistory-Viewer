#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicHistory.Playback
{
    /// <summary>Seconds-based readout of a player that plays recordings (the path HUD's time bar).</summary>
    public interface IRecordingReadout
    {
        /// <summary>The featured-path step whose render is playing (null when stopped).</summary>
        PathStep? CurrentStep { get; }
        /// <summary>Seconds into the render.</summary>
        double CurrentSeconds { get; }
        /// <summary>Length of the render in seconds (the decoded file once loaded, else paths.json).</summary>
        double DurationSeconds { get; }
        /// <summary>"loading", "audio", "main-thread clock" or "stopped".</summary>
        string ClockSource { get; }
    }

    /// <summary>
    /// Plays the featured paths' prerendered recording previews (data/audio/renders, paths.json v2:
    /// <see cref="PathCatalog"/>). Each render already starts in the previous step's key and tempo
    /// as heard and glides to its own, so this player streams the file and reports the glide from
    /// the render's metadata: <see cref="CurrentSemitones"/> and <see cref="CurrentBpm"/> follow the
    /// smoothstep over <see cref="PathStep.MorphSeconds"/>, progress is in seconds
    /// (<see cref="IRecordingReadout"/>).
    ///
    /// Two AudioSources on a child object (the MIDI synth's OnAudioFilterRead on this object never
    /// touches them): a natural advance crossfades about <see cref="CrossfadeSeconds"/> into the
    /// next clip (Finished is raised that long before the end, once per clip); Next/Back cut with a
    /// short fade; Stop and Pause fade. When no audio device advances AudioSource.time (batchmode,
    /// audio disabled) or a file fails to load, a main-thread clock keeps time so Finished still
    /// fires. The walkthrough uses this player only for path tours' steps listed in paths.json.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]   // raise Finished before the director's Update in the same frame
    public sealed class PreviewSongPlayer : MonoBehaviour, ISongPlayer, IMorphReadout, IRecordingReadout
    {
        [Header("Fades (seconds)")]
        [Tooltip("Equal-power crossfade into the next clip on a natural advance (two AudioSources).")]
        [Min(0f)] public float CrossfadeSeconds = 1f;
        [Tooltip("Fade of a clip cut by Next/Back.")]
        [Min(0f)] public float CutFadeSeconds = .25f;
        [Min(0f)] public float StopFadeSeconds = .35f;
        [Min(0f)] public float PauseFadeSeconds = .12f;

        [Header("Clock")]
        [Tooltip("AudioSource.time standing still this long while it should play hands the clip to the main-thread clock.")]
        [Min(.05f)] public float AudioStallSeconds = .4f;
        [Tooltip("A file still loading after this long starts on the main-thread clock, silently; the recording joins at the clock's position when it arrives.")]
        [Min(.5f)] public float LoadTimeoutSeconds = 6f;
        [Tooltip("The main-thread clock follows Time.timeScale (recordings themselves always play at 1x).")]
        public bool ClockFollowsTimeScale = true;
        [Tooltip("Never load or play audio: only the clock runs (edit mode, tests).")]
        public bool ClockOnly;
        [Tooltip("Play the audio but keep time on the main-thread clock (simulates a device that never advances; tests).")]
        public bool ForceMainClock;
        [Tooltip("Delay every file load by this many real seconds (simulates a slow disk; tests).")]
        [Min(0f)] public float SimulatedLoadDelaySeconds;
        [Range(0f, 1.5f)] public float Volume = 1f;

        /// <summary>Used by <see cref="Play(SongClip, SongClip)"/> to find the render of a (previous, next) pair.</summary>
        public PathCatalog? Catalog { get; set; }
        public float MorphBars { get; set; } = 2f;
        /// <summary>Ignored: a recording has no normalized version (the walkthrough plays MIDI for "compare").</summary>
        public bool ApplesToApples { get; set; }

        public event Action<SongClip>? Started;
        public event Action<SongClip>? Finished;

        sealed class Voice
        {
            public readonly string Name;
            public AudioSource? Source;
            public PathStep? Step;
            public SongClip? Clip;
            public AudioClip? Audio;
            public int Id;
            public double Position;
            public double Duration;
            public bool Active;
            public bool Loading;
            public double LoadWait;
            // Timed out while loading: the recording joins at the clock's position if it still arrives.
            public bool LateLoad;
            public bool AudioPlaying;
            public bool MainClock;
            public bool Started;
            public bool FinishedRaised;
            public bool Paused;
            public double Stall;
            public double LastAudioPosition = -1;
            // Fade envelope (unscaled seconds).
            public float Gain = 1f;
            public float FadeFrom = 1f, FadeTo = 1f, FadeLength, FadeTime;
            public FadeCurve Curve;
            public bool Fading, StopAfterFade, HoldFade;
            public double HoldTime;
            public float PauseGain = 1f;

            public Voice(string name) => Name = name;
        }

        enum FadeCurve { Linear, EqualPowerIn, EqualPowerOut }

        readonly Voice a = new("A"), b = new("B");
        Voice? current, tail;
        bool stopped = true;
        bool paused;
        int nextId;
        string lastWarning = "";

        const int CacheSize = 6;
        readonly Dictionary<string, AudioClip> cache = new(StringComparer.OrdinalIgnoreCase);
        readonly LinkedList<string> lru = new();
        readonly HashSet<string> inFlight = new(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ readouts

        public PathStep? CurrentStep => stopped ? null : current?.Step;
        public SongClip? Clip => stopped ? null : current?.Clip;
        public double CurrentSeconds => stopped || current == null ? 0 : current.Position;
        public double DurationSeconds => stopped || current == null ? 0 : current.Duration;
        public bool IsPlaying => !stopped && current != null && current.Active && !current.FinishedRaised;

        public string ClockSource =>
            stopped || current == null ? "stopped"
            : current.Loading ? "loading"
            : current.MainClock ? "main-thread clock"
            : "audio";

        /// <summary>The main-thread clock keeps the current clip's time (no device, ClockOnly or ForceMainClock).</summary>
        public bool UsingMainClock => current != null && current.MainClock;

        /// <summary>The outgoing clip is still sounding (crossfade or cut fade).</summary>
        public bool TailActive => tail != null && tail.Active;
        public float CurrentGain => current != null && current.Active ? current.Gain * current.PauseGain : 0f;
        /// <summary>Voices still sounding or fading (0, 1 or 2).</summary>
        public int SoundingVoices => (a.Active ? 1 : 0) + (b.Active ? 1 : 0);
        /// <summary>The loudest voice's fade level (0..1).</summary>
        public float MaxGain => Mathf.Max(a.Active ? a.Gain * a.PauseGain : 0f, b.Active ? b.Gain * b.PauseGain : 0f);
        public float TailGain => tail != null && tail.Active ? tail.Gain * tail.PauseGain : 0f;
        /// <summary>The decoded recording of the current clip (null while loading or on the clock).</summary>
        public AudioClip? CurrentAudio => current?.Audio;
        public AudioSource? CurrentSource => current?.Source;
        /// <summary>Last warning (load failure, stalled device), for the HUD and tests.</summary>
        public string LastWarning => lastWarning;
        /// <summary>Recordings that arrived after the load time-out and joined the running clock (tests).</summary>
        public int LateJoins { get; private set; }
        /// <summary>Where the last late recording joined, in seconds into the render.</summary>
        public double LastJoinSeconds { get; private set; }

        public bool Paused
        {
            get => paused;
            set
            {
                paused = value;
                SetPaused(current, value);
                SetPaused(tail, value);
            }
        }

        /// <summary>Beats of the recording heard so far, offset by the clip's excerpt start (keeps the watchdog's progress test).</summary>
        public double CurrentBeat
        {
            get
            {
                Voice? v = stopped ? null : current;
                if (v?.Step == null || v.Clip == null) return 0;
                return v.Clip.ExcerptStartBeat + v.Step.BeatsAt(v.Position);
            }
        }

        public double CurrentSemitones => CurrentStep?.SemitonesAt(CurrentSeconds) ?? 0;
        public double CurrentBpm => CurrentStep?.BpmAt(CurrentSeconds) ?? 0;
        /// <summary>Glide progress 0..1 (1 = the recording's own key and tempo).</summary>
        public double GlideProgress => CurrentStep?.GlideProgress(CurrentSeconds) ?? 1;

        /// <summary>
        /// The render's glide as a <see cref="MorphPlan"/>: start transposition, start tempo over the
        /// recording's own, and the glide length in the clip's bars (<see cref="PathCatalog.MorphBars"/>).
        /// </summary>
        public MorphPlan CurrentPlan
        {
            get
            {
                PathStep? s = CurrentStep;
                if (s == null || !s.Glides) return MorphPlan.None;
                double bpb = current?.Clip != null && current.Clip.BeatsPerBar > 0 ? current.Clip.BeatsPerBar : 4;
                double bars = Catalog != null && Catalog.MorphBars > 0 ? Catalog.MorphBars : MorphBars;
                return new MorphPlan(s.StartSemitones, s.Bpm > 0 ? s.EntryBpm / s.Bpm : 1, bars * bpb);
            }
        }

        public double PlanStartBpm => CurrentStep?.EntryBpm ?? 0;

        // ------------------------------------------------------------------ playback

        /// <summary>
        /// Plays the render of <paramref name="next"/> after <paramref name="previous"/> from
        /// <see cref="Catalog"/>: the exact (previous, next) step, else any step of that song.
        /// </summary>
        public void Play(SongClip next, SongClip? previous)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            PathStep? step = Catalog?.FindStep(previous?.NodeId ?? 0, next.NodeId);
            if (step == null && Catalog != null)
                foreach (FeaturedPath p in Catalog.Paths)
                    foreach (PathStep s in p.Steps)
                        if (step == null && s.NodeId == next.NodeId) step = s;
            if (step == null) throw new FileNotFoundException($"No recording preview for '{next.Title}' in paths.json.");
            PlayStep(step, next);
        }

        /// <summary>Plays <paramref name="step"/>'s render as <paramref name="clip"/> (the clip Started/Finished report).</summary>
        public void PlayStep(PathStep step, SongClip clip)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            if (!ClockOnly && Application.isPlaying && !step.FileExists)
                throw new FileNotFoundException($"Recording preview missing: {step.AbsoluteFile}");

            Voice fresh = current == a ? b : a;
            // The slot's older sound (a tail still fading) makes way at once.
            if (fresh.Active) Release(fresh);
            Voice? old = current != null && current.Active ? current : null;
            if (old != null && old.Paused)
            {
                // A paused clip is silent already: replaced, not resumed.
                Release(old);
                old = null;
            }
            bool naturalAdvance = old != null && !stopped && old.FinishedRaised && !old.Paused;
            if (old != null)
            {
                if (naturalAdvance)
                {
                    // Crossfade: the outgoing clip holds until the new one sounds, then both fade together.
                    BeginFade(old, 0f, CrossfadeSeconds, FadeCurve.EqualPowerOut, stop: true);
                    old.HoldFade = true;
                    old.HoldTime = 0;
                }
                else
                {
                    BeginFade(old, 0f, CutFadeSeconds, FadeCurve.Linear, stop: true);
                }
            }
            tail = old;

            stopped = false;
            paused = false;
            current = fresh;
            Reset(fresh);
            fresh.Id = ++nextId;
            fresh.Step = step;
            fresh.Clip = clip;
            fresh.Duration = step.Seconds > 0 ? step.Seconds : 30;
            fresh.Active = true;
            if (naturalAdvance && CrossfadeSeconds > 0)
            {
                fresh.Gain = 0f;
                BeginFade(fresh, 1f, CrossfadeSeconds, FadeCurve.EqualPowerIn, stop: false);
            }
            else
            {
                fresh.Gain = 1f;
            }

            if (ClockOnly || !Application.isPlaying || !isActiveAndEnabled)
            {
                fresh.MainClock = true;
                return;
            }
            string path = step.AbsoluteFile;
            if (cache.TryGetValue(path, out AudioClip? audio) && audio != null)
            {
                Touch(path);
                StartAudio(fresh, audio);
            }
            else
            {
                fresh.Loading = true;
                Request(path);
            }
        }

        /// <summary>Starts decoding <paramref name="step"/>'s file so the next step starts without a gap.</summary>
        public void Preload(PathStep? step)
        {
            if (step == null || ClockOnly || !Application.isPlaying || !isActiveAndEnabled || !step.FileExists) return;
            if (cache.ContainsKey(step.AbsoluteFile)) Touch(step.AbsoluteFile);
            else Request(step.AbsoluteFile);
        }

        /// <summary>Fades everything out; raises nothing.</summary>
        public void Stop()
        {
            stopped = true;
            paused = false;
            foreach (Voice v in new[] { a, b })
            {
                if (!v.Active) continue;
                v.HoldFade = false;
                // A paused or silent clip has nothing to fade.
                if (v.Paused || v.Loading || !v.AudioPlaying) Release(v);
                else BeginFade(v, 0f, StopFadeSeconds, FadeCurve.Linear, stop: true);
            }
        }

        void Update() => Advance(Time.unscaledDeltaTime);

        /// <summary>Moves fades and clocks on by <paramref name="seconds"/> of real time (public for edit-mode tests).</summary>
        public void Advance(double seconds)
        {
            float dt = (float)Math.Max(0, seconds);
            float clockScale = ClockFollowsTimeScale ? Mathf.Max(0f, Time.timeScale) : 1f;
            Voice? cur = current;
            // The current clip first: its start releases the outgoing clip's held crossfade.
            if (cur != null) Tick(cur, dt, clockScale, isCurrent: !stopped);
            if (tail != null && tail != cur) Tick(tail, dt, clockScale, isCurrent: false);
        }

        void Tick(Voice v, float dt, float clockScale, bool isCurrent)
        {
            if (!v.Active) return;

            // Envelope. A crossfade waits until the incoming clip sounds (it may still be loading).
            bool hold = (v.HoldFade && HoldOn(v, dt)) || (v == current && !v.Started && !stopped);
            if (v.Fading && !hold)
            {
                v.FadeTime += dt;
                float u = v.FadeLength > 0 ? Mathf.Clamp01(v.FadeTime / v.FadeLength) : 1f;
                v.Gain = v.Curve switch
                {
                    FadeCurve.EqualPowerOut => v.FadeFrom * Mathf.Cos(u * Mathf.PI * .5f),
                    FadeCurve.EqualPowerIn => Mathf.Lerp(v.FadeFrom, v.FadeTo, Mathf.Sin(u * Mathf.PI * .5f)),
                    _ => Mathf.Lerp(v.FadeFrom, v.FadeTo, u)
                };
                if (u >= 1f)
                {
                    v.Fading = false;
                    if (v.StopAfterFade)
                    {
                        Release(v);
                        return;
                    }
                }
            }
            float pauseTarget = v.Paused ? 0f : 1f;
            if (!Mathf.Approximately(v.PauseGain, pauseTarget))
            {
                float step = PauseFadeSeconds > 0 ? dt / PauseFadeSeconds : 1f;
                v.PauseGain = Mathf.MoveTowards(v.PauseGain, pauseTarget, step);
                if (v.Paused && v.PauseGain <= 0f && v.Source != null && v.AudioPlaying) v.Source.Pause();
            }
            ApplyVolume(v);

            if (v.Loading)
            {
                v.LoadWait += dt;
                if (v.LoadWait > LoadTimeoutSeconds)
                {
                    Warn($"MusicHistory: recording preview '{Path.GetFileName(v.Step?.AbsoluteFile)}' still loading after {LoadTimeoutSeconds:0.##} s; keeping time until it arrives.");
                    v.Loading = false;
                    v.LateLoad = true;
                    v.MainClock = true;
                }
                return;
            }
            if (v.Paused) return;

            if (!v.Started && isCurrent)
            {
                v.Started = true;
                ReleaseHeldTail();
                if (v.Clip != null) Started?.Invoke(v.Clip);
                if (current != v || !v.Active) return;   // a listener replaced or stopped the clip
            }

            if (v.MainClock)
            {
                v.Position += dt * clockScale;
            }
            else if (v.Source != null && v.Audio != null)
            {
                AudioSource s = v.Source;
                if (s.isPlaying)
                {
                    double pos = v.Audio.frequency > 0 ? s.timeSamples / (double)v.Audio.frequency : s.time;
                    if (pos > v.LastAudioPosition + 1e-6)
                    {
                        v.LastAudioPosition = pos;
                        v.Position = pos;
                        v.Stall = 0;
                    }
                    else if ((v.Stall += dt) > AudioStallSeconds)
                    {
                        HandToClock(v, "AudioSource.time does not advance (no audio device?)");
                    }
                }
                else if (v.LastAudioPosition >= v.Duration - .5)
                {
                    v.Position = v.Duration;   // the source played to its end
                }
                else if ((v.Stall += dt) > AudioStallSeconds)
                {
                    HandToClock(v, "the AudioSource does not play (no audio device?)");
                }
            }
            else
            {
                v.MainClock = true;
            }

            if (isCurrent && !v.FinishedRaised && v.Position >= v.Duration - EarlyFinish(v))
            {
                v.FinishedRaised = true;
                if (v.Clip != null) Finished?.Invoke(v.Clip);
                if (current != v) return;   // the listener moved on: this voice is the tail now
            }
            if (v.Position >= v.Duration)
            {
                v.Position = v.Duration;
                if (isCurrent && !v.FinishedRaised)
                {
                    v.FinishedRaised = true;
                    if (v.Clip != null) Finished?.Invoke(v.Clip);
                }
                if (v.Active && (current != v || !v.Fading)) Release(v, keepPosition: true);
            }
        }

        /// <summary>A natural advance hands over this long before the end, so the crossfade overlaps the tail.</summary>
        double EarlyFinish(Voice v) => Math.Min(CrossfadeSeconds, v.Duration * .25);

        bool HoldOn(Voice v, float dt)
        {
            v.HoldTime += dt;
            // The incoming clip is still loading: keep the outgoing one at full level for a while.
            if (v.HoldTime < 1.5) return true;
            v.HoldFade = false;
            return false;
        }

        void ReleaseHeldTail()
        {
            if (tail != null && tail.Active) tail.HoldFade = false;
        }

        void HandToClock(Voice v, string why)
        {
            v.MainClock = true;
            Warn($"MusicHistory: {why}; the recording preview keeps time on the main-thread clock.");
        }

        void Warn(string message)
        {
            if (message == lastWarning) return;
            lastWarning = message;
            Debug.LogWarning(message);
        }

        static void BeginFade(Voice v, float to, float seconds, FadeCurve curve, bool stop)
        {
            v.FadeFrom = v.Gain;
            v.FadeTo = to;
            v.FadeLength = Mathf.Max(0f, seconds);
            v.FadeTime = 0f;
            v.Curve = curve;
            v.Fading = true;
            v.StopAfterFade = stop;
            v.HoldFade = false;
            if (v.FadeLength <= 0f)
            {
                v.Gain = to;
                v.Fading = stop;   // a zero-length stop releases on the next tick
            }
        }

        void SetPaused(Voice? v, bool value)
        {
            if (v == null || !v.Active || v.Paused == value) return;
            v.Paused = value;
            if (!value && v.Source != null && v.AudioPlaying && !v.Source.isPlaying) v.Source.UnPause();
        }

        void ApplyVolume(Voice v)
        {
            if (v.Source != null) v.Source.volume = Mathf.Clamp01(Volume * v.Gain * v.PauseGain);
        }

        static void Reset(Voice v)
        {
            v.Step = null;
            v.Clip = null;
            v.Audio = null;
            v.Position = 0;
            v.Duration = 0;
            v.Active = false;
            v.Loading = false;
            v.LoadWait = 0;
            v.LateLoad = false;
            v.AudioPlaying = false;
            v.MainClock = false;
            v.Started = false;
            v.FinishedRaised = false;
            v.Paused = false;
            v.Stall = 0;
            v.LastAudioPosition = -1;
            v.Gain = 1f;
            v.Fading = false;
            v.StopAfterFade = false;
            v.HoldFade = false;
            v.PauseGain = 1f;
        }

        /// <summary>Silences and frees a voice at once.</summary>
        void Release(Voice v, bool keepPosition = false)
        {
            if (v.Source != null)
            {
                v.Source.Stop();
                v.Source.clip = null;
            }
            double position = v.Position;
            PathStep? step = v.Step;
            SongClip? clip = v.Clip;
            bool finished = v.FinishedRaised;
            Reset(v);
            if (!keepPosition) return;
            // The current clip that played out keeps its readout (the HUD shows the full bar).
            v.Position = position;
            v.Duration = step?.Seconds ?? position;
            v.Step = step;
            v.Clip = clip;
            v.FinishedRaised = finished;
            v.Started = true;
        }

        void StartAudio(Voice v, AudioClip audio, double startAt = 0)
        {
            AudioSource? s = EnsureSource(v);
            v.Loading = false;
            v.LateLoad = false;
            v.Audio = audio;
            if (audio.length > 0) v.Duration = audio.length;
            if (v.Step != null && v.Step.Seconds > 0 && Math.Abs(audio.length - v.Step.Seconds) > .25)
                Warn($"MusicHistory: '{Path.GetFileName(v.Step.AbsoluteFile)}' is {audio.length:0.00} s long; paths.json says {v.Step.Seconds:0.00} s.");
            if (s == null)
            {
                v.MainClock = true;
                return;
            }
            s.clip = audio;
            s.timeSamples = Mathf.Clamp((int)(startAt * audio.frequency), 0, Math.Max(0, audio.samples - 1));
            ApplyVolume(v);
            s.Play();
            v.AudioPlaying = true;
            v.Stall = 0;
            v.LastAudioPosition = -1;
            v.MainClock = ForceMainClock;
            if (v.Paused) s.Pause();
        }

        AudioSource? EnsureSource(Voice v)
        {
            if (v.Source != null) return v.Source;
            if (this == null) return null;
            // Own child object: the MIDI synth's OnAudioFilterRead on this GameObject would otherwise
            // process these AudioSources too and overwrite the recording with its own output.
            Transform existing = transform.Find("Preview Audio");
            GameObject host = existing != null ? existing.gameObject : new GameObject("Preview Audio");
            host.transform.SetParent(transform, false);
            AudioSource[] sources = host.GetComponents<AudioSource>();
            while (sources.Length < 2)
            {
                host.AddComponent<AudioSource>();
                sources = host.GetComponents<AudioSource>();
            }
            AudioSource source = sources[v == a ? 0 : 1];
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            v.Source = source;
            return source;
        }

        // ------------------------------------------------------------------ loading

        void Request(string path)
        {
            if (inFlight.Contains(path) || !isActiveAndEnabled) return;
            inFlight.Add(path);
            StartCoroutine(Load(path));
        }

        IEnumerator Load(string path)
        {
            AudioClip? clip = null;
            string? error = null;
            if (SimulatedLoadDelaySeconds > 0) yield return new WaitForSecondsRealtime(SimulatedLoadDelaySeconds);
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;
                }
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        clip = DownloadHandlerAudioClip.GetContent(req);
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                }
                else
                {
                    error = req.error;
                }
            }
            inFlight.Remove(path);
            if (clip != null)
            {
                clip.name = Path.GetFileName(path);
                AddToCache(path, clip);
            }
            else
            {
                Warn($"MusicHistory: could not load recording preview '{path}' ({error ?? "no audio"}); keeping time silently.");
            }
            foreach (Voice v in new[] { a, b })
            {
                if (!v.Active || v.Step == null || !string.Equals(v.Step.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase)) continue;
                if (v.Loading)
                {
                    if (clip != null) StartAudio(v, clip);
                    else
                    {
                        v.Loading = false;
                        v.MainClock = true;
                    }
                }
                else if (v.LateLoad && clip != null && v.Position < Math.Min(v.Duration, clip.length) - 1.0)
                {
                    // It arrived after the time-out: the recording joins where the clock is.
                    LateJoins++;
                    LastJoinSeconds = v.Position;
                    StartAudio(v, clip, v.Position);
                }
            }
        }

        void AddToCache(string path, AudioClip clip)
        {
            if (cache.TryGetValue(path, out AudioClip? old) && old != null && old != clip) Destroy(old);
            cache[path] = clip;
            Touch(path);
            while (lru.Count > CacheSize)
            {
                LinkedListNode<string>? node = lru.Last;
                bool evicted = false;
                while (node != null)
                {
                    string key = node.Value;
                    AudioClip c = cache[key];
                    if (c != a.Audio && c != b.Audio)
                    {
                        lru.Remove(node);
                        cache.Remove(key);
                        Destroy(c);
                        evicted = true;
                        break;
                    }
                    node = node.Previous;
                }
                if (!evicted) break;
            }
        }

        void Touch(string path)
        {
            lru.Remove(path);
            lru.AddFirst(path);
        }

        void OnDisable()
        {
            // Scene unload or play-mode exit: silence now, no events.
            foreach (Voice v in new[] { a, b })
            {
                if (v.Source != null) v.Source.Stop();
                Reset(v);
            }
            inFlight.Clear();
            stopped = true;
        }

        void OnDestroy()
        {
            foreach (AudioClip c in cache.Values)
                if (c != null) Destroy(c);
            cache.Clear();
            lru.Clear();
        }
    }
}
