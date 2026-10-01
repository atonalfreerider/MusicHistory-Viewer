#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicHistory.Playback
{
    /// <summary>
    /// Plays a featured path's mashup mix (data/audio/mashups/&lt;id&gt;/mix.mp3, <see cref="MashupCatalog"/>):
    /// one continuous file, so this player only streams it, seeks in it and reports where it is —
    /// the mix time (<see cref="CurrentSeconds"/>), the segment, the phrase beat and which songs'
    /// vocals and instrumentals are audible, all from mashups.json.
    ///
    /// The file is decoded with UnityWebRequest and played on an AudioSource of its own child
    /// object (the MIDI synth's OnAudioFilterRead on this GameObject never touches it). Seeks,
    /// Pause and Stop fade (no clicks). When no audio device advances AudioSource.time (batchmode,
    /// audio disabled), the file fails to load or is still loading after
    /// <see cref="LoadTimeoutSeconds"/>, a main-thread clock keeps time (the recording joins at the
    /// clock's position if it arrives late), so <see cref="Finished"/> still fires at the end.
    ///
    /// As an <see cref="ISongPlayer"/> it lets the walkthrough pause and stop it; <see cref="Play"/>
    /// seeks to the step of the clip's song. Progress (<see cref="CurrentBeat"/>) is the mix time.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]   // raise Finished before the director's Update in the same frame
    public sealed class MashupPlayer : MonoBehaviour, ISongPlayer
    {
        [Header("Fades (seconds)")]
        [Min(0f)] public float SeekFadeSeconds = .06f;
        [Min(0f)] public float StopFadeSeconds = .35f;
        [Min(0f)] public float PauseFadeSeconds = .12f;

        [Header("Clock")]
        [Min(.05f)] public float AudioStallSeconds = .4f;
        [Min(.5f)] public float LoadTimeoutSeconds = 8f;
        [Tooltip("The main-thread clock follows Time.timeScale (the recording itself always plays at 1x).")]
        public bool ClockFollowsTimeScale = true;
        [Tooltip("Never load or play audio: only the clock runs (edit mode, tests).")]
        public bool ClockOnly;
        [Tooltip("Play the audio but keep time on the main-thread clock (tests).")]
        public bool ForceMainClock;
        [Range(0f, 1.5f)] public float Volume = 1f;

        public float MorphBars { get; set; } = 2f;
        /// <summary>Ignored: the mix has no normalized version (the walkthrough plays MIDI for "compare").</summary>
        public bool ApplesToApples { get; set; }

        public event Action<SongClip>? Started;
        public event Action<SongClip>? Finished;
        /// <summary>Raised once when the mix reaches its end.</summary>
        public event Action<Mashup>? MixFinished;

        Mashup? mashup;
        SongClip? clip;
        AudioSource? source;
        AudioClip? audio;
        string audioPath = "";
        double position, duration;
        bool playing, stopped = true, paused;
        bool loading, lateLoad, mainClock, audioPlaying, startedRaised, finishedRaised;
        double loadWait, stall, lastAudioPosition = -1;
        // Envelope: gain moves to target at rate per second; a pending seek waits for the fade-out.
        float gain = 1f, gainTarget = 1f, gainRate;
        float pauseGain = 1f;
        double? pendingSeek;
        bool stopAfterFade;
        string lastWarning = "";
        int loads;

        public Mashup? Current => stopped ? null : mashup;
        public SongClip? Clip => stopped ? null : clip;
        /// <summary>Seconds into the mix (a pending seek reports its target).</summary>
        public double CurrentSeconds => stopped || mashup == null ? 0 : pendingSeek ?? position;
        public double DurationSeconds => stopped || mashup == null ? 0 : duration;
        public bool IsPlaying => !stopped && mashup != null && playing && !finishedRaised;
        public bool Complete => !stopped && finishedRaised;
        public string ClockSource => stopped || mashup == null ? "stopped" : loading ? "loading" : mainClock ? "main-thread clock" : "audio";
        public bool UsingMainClock => mainClock;
        public AudioClip? CurrentAudio => audio;
        public AudioSource? CurrentSource => source;
        public float CurrentGain => stopped ? 0f : gain * pauseGain;
        public string LastWarning => lastWarning;
        /// <summary>Recordings that arrived after the load time-out and joined the running clock.</summary>
        public int LateJoins { get; private set; }
        /// <summary>Decode requests made (a cached mix is not decoded again).</summary>
        public int Loads => loads;

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
        public double CurrentBpm => CurrentSegment?.BpmAt(CurrentSeconds) ?? 0;
        public MorphPlan CurrentPlan => MorphPlan.None;

        public MashupSegment? CurrentSegment => Current?.SegmentAt(CurrentSeconds);
        public double PhraseBeat => Current?.PhraseBeatAt(CurrentSeconds) ?? 0;

        // ------------------------------------------------------------------ playback

        /// <summary>Plays <paramref name="mix"/> from <paramref name="atSeconds"/>; <paramref name="tag"/> is the clip Started/Finished report.</summary>
        public void PlayMix(Mashup mix, double atSeconds, SongClip tag)
        {
            if (mix == null) throw new ArgumentNullException(nameof(mix));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (!stopped && ReferenceEquals(mix, mashup))
            {
                clip = tag;
                Seek(atSeconds);
                Paused = false;
                return;
            }
            Silence();
            mashup = mix;
            clip = tag;
            duration = mix.Duration > 0 ? mix.Duration : 1;
            position = Clamp(atSeconds);
            stopped = false;
            paused = false;
            playing = true;
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
            lastAudioPosition = -1;
            pauseGain = 1f;
            // Fade in from silence (the mix may start mid-phrase after a jump).
            gain = 0f;
            Ramp(1f, SeekFadeSeconds);

            if (ClockOnly || !Application.isPlaying || !isActiveAndEnabled || !mix.FileExists)
            {
                if (Application.isPlaying && !ClockOnly && !mix.FileExists) Warn($"MusicHistory: mashup mix missing: {mix.AbsoluteFile}; keeping time silently.");
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

        /// <summary>Jumps to <paramref name="seconds"/> with a short fade out and in (immediately on the clock).</summary>
        public void Seek(double seconds)
        {
            if (stopped || mashup == null) return;
            double target = Clamp(seconds);
            finishedRaised = false;
            playing = true;
            if (mainClock || loading || source == null || !audioPlaying || SeekFadeSeconds <= 0 || paused)
            {
                pendingSeek = null;
                position = target;
                lastAudioPosition = -1;
                if (source != null && audio != null && audioPlaying) SetSourceTime(target);
                return;
            }
            pendingSeek = target;
            Ramp(0f, SeekFadeSeconds);
        }

        /// <summary>ISongPlayer: seeks to where <paramref name="next"/>'s step starts in the current mix.</summary>
        public void Play(SongClip next, SongClip? previous)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (mashup == null || stopped) throw new InvalidOperationException("No mashup mix is loaded (PlayMix first).");
            int step = -1;
            foreach (MashupSong s in mashup.Songs)
                if (s.NodeId == next.NodeId) step = s.PathStep;
            clip = next;
            Seek(mashup.StepStartSeconds(Math.Max(0, step)));
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
            // Envelope.
            if (!Mathf.Approximately(gain, gainTarget))
            {
                gain = float.IsInfinity(gainRate) ? gainTarget : Mathf.MoveTowards(gain, gainTarget, gainRate * dt);
            }
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
                    position = target;
                    lastAudioPosition = -1;
                    if (source != null && audio != null && audioPlaying) SetSourceTime(target);
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
            if (stopped || mashup == null) return;

            if (loading)
            {
                loadWait += dt;
                if (loadWait > LoadTimeoutSeconds)
                {
                    Warn($"MusicHistory: mashup mix '{Path.GetFileName(mashup.AbsoluteFile)}' still loading after {LoadTimeoutSeconds:0.##} s; keeping time until it arrives.");
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
                    if (pos > lastAudioPosition + 1e-6)
                    {
                        lastAudioPosition = pos;
                        if (pendingSeek == null) position = pos;
                        stall = 0;
                    }
                    else if ((stall += dt) > AudioStallSeconds)
                    {
                        HandToClock("AudioSource.time does not advance (no audio device?)");
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
                playing = false;
                if (clip != null) Finished?.Invoke(clip);
                if (mashup != null) MixFinished?.Invoke(mashup);
            }
        }

        void HandToClock(string why)
        {
            mainClock = true;
            Warn($"MusicHistory: {why}; the mashup mix keeps time on the main-thread clock.");
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
                if (mashup != null && mashup.Seconds > 0 && Math.Abs(audio.length - mashup.Seconds) > .25)
                    Warn($"MusicHistory: '{Path.GetFileName(audioPath)}' is {audio.length:0.00} s long; mashups.json says {mashup.Seconds:0.00} s.");
                duration = Math.Max(duration, audio.length);
            }
            s.clip = audio;
            SetSourceTime(at);
            ApplyVolume();
            s.Play();
            audioPlaying = true;
            stall = 0;
            lastAudioPosition = -1;
            mainClock = ForceMainClock;
            if (paused) s.Pause();
        }

        AudioSource? EnsureSource()
        {
            if (source != null) return source;
            if (this == null) return null;
            Transform existing = transform.Find("Mashup Audio");
            GameObject host = existing != null ? existing.gameObject : new GameObject("Mashup Audio");
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
                    handler.compressed = false;   // exact timeSamples for seeking
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
                Warn($"MusicHistory: could not load mashup mix '{path}' ({error ?? "no audio"}); keeping time silently.");
                if (mashup != null && string.Equals(mashup.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase))
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
            if (stopped || mashup == null || !string.Equals(mashup.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase)) yield break;
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
