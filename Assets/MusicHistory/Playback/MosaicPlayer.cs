#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicHistory.Playback
{
    /// <summary>
    /// Plays a melody mosaic's mix (data/audio/mosaics/&lt;id&gt;/mix.mp3, <see cref="MosaicCatalog"/>)
    /// once, from its start to its end: the original loops, the mosaic loops, the harmony loops.
    /// The MP3 is decoded once to PCM (UnityWebRequest, uncompressed, so seeks land on the sample)
    /// and played by an AudioSource of its own child object (the MIDI synth's OnAudioFilterRead on
    /// this GameObject never touches it).
    ///
    /// The clock is the audio thread's position (timeSamples), smoothed for display (the audio
    /// thread reports in buffer steps; the panning melody graph must not stutter) and snapped back
    /// when it drifts. Seeks, Pause and Stop fade (no clicks). When no audio device advances the
    /// source (batchmode, audio disabled), the file fails to load or is still loading after
    /// <see cref="LoadTimeoutSeconds"/>, a main-thread clock keeps time (the recording joins at the
    /// clock's position when it arrives late), so <see cref="Finished"/> still fires at the end.
    ///
    /// As an <see cref="ISongPlayer"/> it lets the walkthrough pause and stop it; progress
    /// (<see cref="CurrentBeat"/>) is the mix time in seconds.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]   // raise Finished before the director's Update in the same frame
    public sealed class MosaicPlayer : MonoBehaviour, ISongPlayer
    {
        [Header("Fades (seconds)")]
        [Min(0f)] public float SeekFadeSeconds = .06f;
        [Min(0f)] public float StopFadeSeconds = .35f;
        [Min(0f)] public float PauseFadeSeconds = .12f;

        [Header("Clock")]
        [Min(.05f)] public float AudioStallSeconds = .4f;
        [Min(.5f)] public float LoadTimeoutSeconds = 8f;
        [Tooltip("The main-thread clock follows Time.timeScale (the mix itself always plays at 1x).")]
        public bool ClockFollowsTimeScale = true;
        [Tooltip("Never load or play audio: only the clock runs (edit mode, tests).")]
        public bool ClockOnly;
        [Tooltip("Play the audio but keep time on the main-thread clock (tests).")]
        public bool ForceMainClock;
        [Tooltip("Display clock: how fast (1/s) it is pulled onto the audio thread's position; beyond SnapSeconds it jumps.")]
        [Min(.5f)] public float ClockPull = 8f;
        [Min(.02f)] public float SnapSeconds = .25f;
        [Range(0f, 1.5f)] public float Volume = 1f;

        public float MorphBars { get; set; } = 2f;
        /// <summary>Ignored: the mix has no normalized version.</summary>
        public bool ApplesToApples { get; set; }

        public event Action<SongClip>? Started;
        public event Action<SongClip>? Finished;
        /// <summary>Raised once when the mix reaches its end.</summary>
        public event Action<Mosaic>? MixFinished;

        Mosaic? mosaic;
        SongClip? clip;
        AudioSource? source;
        AudioClip? audio;
        string audioPath = "";
        double position, duration;
        bool stopped = true, paused;
        bool loading, lateLoad, mainClock, audioPlaying, startedRaised, finishedRaised;
        double loadWait, stall, sinceMove, settle;
        // The audio thread's last reading (-1: none since the last start or jump).
        double lastAudioPosition = -1;
        bool anchored;
        float gain = 1f, gainTarget = 1f, gainRate;
        float pauseGain = 1f;
        double? pendingSeek;
        bool stopAfterFade;
        string lastWarning = "";
        int loads;

        /// <summary>After a start or a seek, readings this long that disagree with the clock are not anchored on.</summary>
        const double SettleSeconds = .3;

        public Mosaic? Current => stopped ? null : mosaic;
        public SongClip? Clip => stopped ? null : clip;
        /// <summary>Seconds into the mix (a pending seek reports its target).</summary>
        public double CurrentSeconds => stopped || mosaic == null ? 0 : pendingSeek ?? position;
        public double DurationSeconds => stopped || mosaic == null ? 0 : duration;
        public bool IsPlaying => !stopped && mosaic != null && !finishedRaised;
        /// <summary>The mix played to its end (until a seek or Stop).</summary>
        public bool Complete => !stopped && finishedRaised;
        public string ClockSource => stopped || mosaic == null ? "stopped" : loading ? "loading" : mainClock ? "main-thread clock" : "audio";
        public bool UsingMainClock => mainClock;
        public AudioClip? CurrentAudio => audio;
        public AudioSource? CurrentSource => source;
        public float CurrentGain => stopped ? 0f : gain * pauseGain;
        public string LastWarning => lastWarning;
        /// <summary>Recordings that arrived after the load time-out and joined the running clock.</summary>
        public int LateJoins { get; private set; }
        /// <summary>Decode requests made (a cached mix is not decoded again).</summary>
        public int Loads => loads;
        /// <summary>The audio thread's own position (seconds; -1 before it plays).</summary>
        public double AudioSeconds => lastAudioPosition;

        public bool Paused
        {
            get => paused;
            set
            {
                if (stopped || paused == value) return;
                paused = value;
                if (!value && source != null && audioPlaying && !source.isPlaying && !finishedRaised) source.UnPause();
            }
        }

        /// <summary>Progress for the walkthrough's watchdog: the mix time in seconds.</summary>
        public double CurrentBeat => CurrentSeconds;
        public double CurrentSemitones => 0;
        public double CurrentBpm => Current?.Bpm ?? 0;
        public MorphPlan CurrentPlan => MorphPlan.None;

        /// <summary>The mix beat now (beats from the first, across every loop).</summary>
        public double MixBeat => Current?.BeatAt(CurrentSeconds) ?? 0;

        // ------------------------------------------------------------------ playback

        /// <summary>Plays <paramref name="mix"/> from <paramref name="atSeconds"/>; <paramref name="tag"/> is the clip Started/Finished report.</summary>
        public void PlayMosaic(Mosaic mix, double atSeconds, SongClip tag)
        {
            if (mix == null) throw new ArgumentNullException(nameof(mix));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (!stopped && ReferenceEquals(mix, mosaic))
            {
                clip = tag;
                Seek(atSeconds);
                Paused = false;
                return;
            }
            Silence();
            mosaic = mix;
            clip = tag;
            duration = mix.Duration > 0 ? mix.Duration : 1;
            position = Clamp(atSeconds);
            stopped = false;
            paused = false;
            finishedRaised = false;
            startedRaised = false;
            pendingSeek = null;
            stopAfterFade = false;
            loading = false;
            lateLoad = false;
            mainClock = false;
            audioPlaying = false;
            loadWait = 0;
            stall = 0;
            ResetAudioClock();
            pauseGain = 1f;
            // Fade in from silence (the mix may start mid-loop after a jump).
            gain = 0f;
            Ramp(1f, SeekFadeSeconds);

            if (ClockOnly || !Application.isPlaying || !isActiveAndEnabled || !mix.FileExists)
            {
                if (Application.isPlaying && !ClockOnly && !mix.FileExists) Warn($"MusicHistory: mosaic mix missing: {mix.AbsoluteFile}; keeping time silently.");
                mainClock = true;
                return;
            }
            if (audio != null && string.Equals(audioPath, mix.AbsoluteFile, StringComparison.OrdinalIgnoreCase))
            {
                StartAudio(position);
            }
            else
            {
                loading = true;
                StartCoroutine(Load(mix.AbsoluteFile));
            }
        }

        void ResetAudioClock()
        {
            lastAudioPosition = -1;
            anchored = false;
            sinceMove = 0;
            settle = SettleSeconds;
        }

        /// <summary>Jumps to <paramref name="seconds"/> with a short fade out and in (immediately on the clock).</summary>
        public void Seek(double seconds)
        {
            if (stopped || mosaic == null) return;
            double target = Clamp(seconds);
            finishedRaised = false;
            if (mainClock || loading || source == null || !audioPlaying || SeekFadeSeconds <= 0 || paused)
            {
                pendingSeek = null;
                Jump(target);
                return;
            }
            pendingSeek = target;
            Ramp(0f, SeekFadeSeconds);
        }

        void Jump(double target)
        {
            position = target;
            ResetAudioClock();
            if (source != null && audio != null && audioPlaying)
            {
                SetSourceTime(target);
                // A source that played to its end stopped: it plays again from here.
                if (!source.isPlaying && !paused) source.Play();
            }
        }

        /// <summary>ISongPlayer: plays the clip's song where it first sounds (the target from the start; a piece's song at its first piece, a harmony voice at the harmony section).</summary>
        public void Play(SongClip next, SongClip? previous)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (mosaic == null || stopped) throw new InvalidOperationException("No mosaic mix is loaded (PlayMosaic first).");
            clip = next;
            double at = 0;
            foreach (MosaicSong s in mosaic.Songs)
            {
                if (s.NodeId != next.NodeId) continue;
                if (s.IsSource && mosaic.SectionOf(MosaicSectionKind.Mosaic) is MosaicSection ms)
                {
                    foreach (MosaicPiece p in mosaic.Pieces)
                        if (p.Song == s.Index)
                        {
                            at = mosaic.TimeAtBeat(ms.FirstLoop * mosaic.LoopBeats + p.Start);
                            break;
                        }
                }
                else if (s.IsHarmony && mosaic.SectionOf(MosaicSectionKind.Harmony) is MosaicSection hs) at = hs.Start;
                break;
            }
            Seek(at);
        }

        /// <summary>Fades out and stops; raises nothing.</summary>
        public void Stop()
        {
            if (stopped) return;
            stopped = true;
            paused = false;
            pendingSeek = null;
            if (source != null && audioPlaying && source.isPlaying && StopFadeSeconds > 0)
            {
                stopAfterFade = true;
                Ramp(0f, StopFadeSeconds);
            }
            else Silence();
        }

        void Silence()
        {
            if (source != null)
            {
                source.Stop();
                source.volume = 0f;
            }
            audioPlaying = false;
            stopAfterFade = false;
        }

        double Clamp(double t) => Math.Max(0, Math.Min(Math.Max(0, duration - 1e-3), t));

        void Ramp(float target, float seconds)
        {
            gainTarget = target;
            gainRate = seconds > 0 ? 1f / seconds : float.PositiveInfinity;
        }

        void Update() => Advance(Time.unscaledDeltaTime);

        /// <summary>Moves the envelope and the clock on by <paramref name="seconds"/> of real time (public for edit-mode tests).</summary>
        public void Advance(double seconds)
        {
            float dt = (float)Math.Max(0, seconds);
            if (!Mathf.Approximately(gain, gainTarget))
                gain = float.IsInfinity(gainRate) ? gainTarget : Mathf.MoveTowards(gain, gainTarget, gainRate * dt);
            if (gain <= 0f && gainTarget <= 0f)
            {
                if (stopAfterFade)
                {
                    Silence();
                    ApplyVolume();
                    return;
                }
                if (pendingSeek is double target)
                {
                    pendingSeek = null;
                    Jump(target);
                    Ramp(1f, SeekFadeSeconds);
                }
            }
            float pauseTarget = paused ? 0f : 1f;
            if (!Mathf.Approximately(pauseGain, pauseTarget))
            {
                pauseGain = Mathf.MoveTowards(pauseGain, pauseTarget, PauseFadeSeconds > 0 ? dt / PauseFadeSeconds : 1f);
                if (paused && pauseGain <= 0f && source != null && audioPlaying && source.isPlaying) source.Pause();
            }
            ApplyVolume();
            if (stopped || mosaic == null) return;

            if (loading)
            {
                loadWait += dt;
                if (loadWait > LoadTimeoutSeconds)
                {
                    Warn($"MusicHistory: mosaic mix '{Path.GetFileName(mosaic.AbsoluteFile)}' still loading after {LoadTimeoutSeconds:0.##} s; keeping time until it arrives.");
                    loading = false;
                    lateLoad = true;
                    mainClock = true;
                }
                return;
            }
            if (paused || finishedRaised) return;
            if (!startedRaised)
            {
                startedRaised = true;
                if (clip != null) Started?.Invoke(clip);
                if (stopped) return;
            }

            if (mainClock)
            {
                if (pendingSeek == null) position += dt * (ClockFollowsTimeScale ? Mathf.Max(0f, Time.timeScale) : 1f);
            }
            else if (source != null && audio != null)
            {
                if (source.isPlaying)
                {
                    double pos = audio.frequency > 0 ? source.timeSamples / (double)audio.frequency : source.time;
                    bool moved = lastAudioPosition < 0 || Math.Abs(pos - lastAudioPosition) > 1e-6;
                    if (moved)
                    {
                        // Right after a seek the source may still report where it was for a buffer or
                        // two: such a reading is not where the clock is, and is not anchored on.
                        if (!anchored && settle > 0 && Math.Abs(pos - position) > SnapSeconds) lastAudioPosition = -1;
                        else
                        {
                            lastAudioPosition = pos;
                            anchored = true;
                        }
                        stall = 0;
                        sinceMove = 0;
                    }
                    else
                    {
                        sinceMove += dt;
                        if ((stall += dt) > AudioStallSeconds) HandToClock("AudioSource.timeSamples does not advance (no audio device?)");
                    }
                    settle = Math.Max(0, settle - dt);
                    if (pendingSeek == null)
                    {
                        if (!anchored) position += dt;   // settling: the clock runs on by itself
                        else if (!mainClock)
                        {
                            // Smooth display clock: predicted on, pulled onto the audio thread's position
                            // (carried on since its last step), snapped when far.
                            double measured = lastAudioPosition + Math.Min(sinceMove, .1);
                            double predicted = position + dt;
                            double err = measured - predicted;
                            position = Math.Abs(err) > SnapSeconds ? measured : predicted + err * Math.Min(1.0, dt * ClockPull);
                        }
                    }
                }
                else if (lastAudioPosition >= duration - .5)
                {
                    position = duration;   // played to its end
                }
                else if ((stall += dt) > AudioStallSeconds)
                {
                    HandToClock("the AudioSource does not play (no audio device?)");
                }
            }
            else
            {
                mainClock = true;
            }

            if (position >= duration - 1e-3 && pendingSeek == null)
            {
                position = duration;
                finishedRaised = true;
                if (clip != null) Finished?.Invoke(clip);
                if (mosaic != null) MixFinished?.Invoke(mosaic);
            }
        }

        void HandToClock(string why)
        {
            mainClock = true;
            Warn($"MusicHistory: {why}; the mosaic mix keeps time on the main-thread clock.");
        }

        void Warn(string message)
        {
            if (message == lastWarning) return;
            lastWarning = message;
            Debug.LogWarning(message);
        }

        void ApplyVolume()
        {
            if (source != null) source.volume = Mathf.Clamp01(Volume * gain * pauseGain);
        }

        /// <summary>Applies <see cref="Volume"/> and the fades to the AudioSource now.</summary>
        public void RefreshVolume() => ApplyVolume();

        void SetSourceTime(double seconds)
        {
            if (source == null || audio == null) return;
            source.timeSamples = Mathf.Clamp((int)(seconds * audio.frequency), 0, Math.Max(0, audio.samples - 1));
        }

        void StartAudio(double at)
        {
            AudioSource? s = EnsureSource();
            loading = false;
            lateLoad = false;
            if (audio == null || s == null)
            {
                mainClock = true;
                return;
            }
            if (audio.length > 0)
            {
                if (mosaic != null && mosaic.Seconds > 0 && Math.Abs(audio.length - mosaic.Seconds) > .25)
                    Warn($"MusicHistory: '{Path.GetFileName(audioPath)}' is {audio.length:0.00} s long; mosaics.json says {mosaic.Seconds:0.00} s.");
                duration = Math.Max(duration, audio.length);
            }
            s.clip = audio;
            s.loop = false;
            SetSourceTime(at);
            ApplyVolume();
            s.Play();
            audioPlaying = true;
            stall = 0;
            ResetAudioClock();
            mainClock = ForceMainClock;
            if (paused) s.Pause();
        }

        AudioSource? EnsureSource()
        {
            if (source != null) return source;
            if (this == null) return null;
            Transform existing = transform.Find("Mosaic Audio");
            GameObject host = existing != null ? existing.gameObject : new GameObject("Mosaic Audio");
            host.transform.SetParent(transform, false);
            source = host.GetComponent<AudioSource>();
            if (source == null) source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            return source;
        }

        IEnumerator Load(string path)
        {
            loads++;
            AudioClip? decoded = null;
            string? error = null;
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;   // PCM: exact timeSamples for the clock and the seeks
                }
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        decoded = DownloadHandlerAudioClip.GetContent(req);
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                }
                else error = req.error;
            }
            if (this == null) yield break;
            if (decoded == null)
            {
                Warn($"MusicHistory: could not load mosaic mix '{path}' ({error ?? "no audio"}); keeping time silently.");
                if (mosaic != null && string.Equals(mosaic.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase))
                {
                    loading = false;
                    mainClock = true;
                }
                yield break;
            }
            decoded.name = Path.GetFileName(path);
            if (audio != null && audio != decoded)
            {
                if (source != null && source.clip == audio) source.clip = null;
                Destroy(audio);
            }
            audio = decoded;
            audioPath = path;
            if (stopped || mosaic == null || !string.Equals(mosaic.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase)) yield break;
            if (loading) StartAudio(position);
            else if (lateLoad && position < Math.Min(duration, decoded.length) - 1.0)
            {
                LateJoins++;
                StartAudio(position);
            }
        }

        void OnDisable()
        {
            // Scene unload or play-mode exit: silence now, no events.
            if (source != null) source.Stop();
            audioPlaying = false;
            stopped = true;
            loading = false;
            pendingSeek = null;
        }

        void OnDestroy()
        {
            if (audio != null) Destroy(audio);
            audio = null;
        }
    }
}
