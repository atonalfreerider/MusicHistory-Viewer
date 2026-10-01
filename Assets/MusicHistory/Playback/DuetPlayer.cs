#nullable enable
using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicHistory.Playback
{
    /// <summary>
    /// Plays a featured path's duet loop (data/audio/duets/&lt;id&gt;/loop.mp3, <see cref="DuetCatalog"/>)
    /// round and round, seamlessly: the MP3 is decoded once to PCM (UnityWebRequest, uncompressed),
    /// trimmed to the contract's exact length when the decoder left encoder padding on it, and played
    /// by one looping AudioSource of its own child object: the wrap from the last sample to the first
    /// happens on the audio (DSP) thread, sample-accurately. The loop file is rendered circularly, so
    /// that wrap is the music's own continuation: no fade, no gap, no seek.
    ///
    /// The clock is the source's sample position (timeSamples) unwrapped across the loop point
    /// (<see cref="TotalSeconds"/>, <see cref="Cycle"/>), smoothed for display (the audio thread
    /// reports in buffer steps; a scrolling graph must not stutter) and snapped back when it drifts.
    /// Seeks, Pause and Stop fade (no clicks). When no audio device advances the source (batchmode,
    /// audio disabled), the file fails to load or is still loading after
    /// <see cref="LoadTimeoutSeconds"/>, a main-thread clock keeps time and wraps the same way.
    ///
    /// As an <see cref="ISongPlayer"/> it lets the walkthrough pause and stop it; it never raises
    /// <see cref="Finished"/> (the loop has no end). Progress (<see cref="CurrentBeat"/>) is the
    /// unwrapped time, so the walkthrough's watchdog sees it advance across the wrap.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]
    public sealed class DuetPlayer : MonoBehaviour, ISongPlayer
    {
        [Header("Fades (seconds)")]
        [Min(0f)] public float SeekFadeSeconds = .06f;
        [Min(0f)] public float StopFadeSeconds = .35f;
        [Min(0f)] public float PauseFadeSeconds = .12f;

        [Header("Clock")]
        [Min(.05f)] public float AudioStallSeconds = .4f;
        [Min(.5f)] public float LoadTimeoutSeconds = 8f;
        [Tooltip("The main-thread clock follows Time.timeScale (the audio itself always plays at 1x).")]
        public bool ClockFollowsTimeScale = true;
        [Tooltip("Never load or play audio: only the clock runs (edit mode, tests).")]
        public bool ClockOnly;
        [Tooltip("Play the audio but keep time on the main-thread clock (tests).")]
        public bool ForceMainClock;
        [Tooltip("Display clock: how fast (1/s) it is pulled onto the audio thread's position; beyond SnapSeconds it jumps.")]
        [Min(.5f)] public float ClockPull = 8f;
        [Min(.02f)] public float SnapSeconds = .25f;
        [Range(0f, 1.5f)] public float Volume = 1f;

        [Header("Loop file")]
        [Tooltip("Trim the decoded clip to duets.json's exact length when the decoder left MP3 padding on it (a gap at the loop point otherwise).")]
        public bool TrimToContract = true;
        [Tooltip("Samples of encoder delay to drop from the start when trimming (-1: up to 1105, the LAME encoder + decoder delay).")]
        public int LeadTrimSamples = -1;

        public float MorphBars { get; set; } = 2f;
        /// <summary>Ignored: the loop has no normalized version (the walkthrough plays MIDI for "compare").</summary>
        public bool ApplesToApples { get; set; }

        public event Action<SongClip>? Started;
        /// <summary>Never raised: the loop has no end.</summary>
        public event Action<SongClip>? Finished
        {
            add { }
            remove { }
        }
        /// <summary>Raised when the clock passes the loop point (the new cycle number).</summary>
        public event Action<int>? Wrapped;

        DuetLoop? loop;
        SongClip? clip;
        AudioSource? source;
        AudioClip? audio;
        string audioPath = "";
        // The display clock: unwrapped seconds (cycles x length + position in the loop).
        // The display clock: completed cycles and the position in the loop, kept apart so a seek to a
        // segment's start lands exactly on it (cycles x length + t, folded back, would not).
        int cycles;
        double position, length;
        double Total => cycles * Len + position;

        void SetTotal(double t)
        {
            cycles = (int)Math.Floor(t / Len);
            position = t - cycles * Len;
            if (position >= Len) { position -= Len; cycles++; }
            if (position < 0) { position += Len; cycles--; }
        }

        void AddTime(double dt)
        {
            position += dt;
            while (position >= Len)
            {
                position -= Len;
                cycles++;
            }
        }
        // The audio thread's position, unwrapped (the last sample position and the wraps counted).
        double audioTotal = -1, lastAudioPosition = -1, audioBase, sinceMove;
        int audioCycles;
        bool stopped = true, paused;
        bool loading, lateLoad, mainClock, audioPlaying, startedRaised;
        double loadWait, stall;
        float gain = 1f, gainTarget = 1f, gainRate;
        float pauseGain = 1f;
        double? pendingSeek;
        bool stopAfterFade;
        string lastWarning = "";
        int loads, lastCycle;

        public DuetLoop? Current => stopped ? null : loop;
        public SongClip? Clip => stopped ? null : clip;
        /// <summary>Seconds into the loop, 0 ≤ t &lt; its length (a pending seek reports its target).</summary>
        public double CurrentSeconds => stopped || loop == null ? 0 : pendingSeek ?? position;
        /// <summary>Seconds since the loop started playing, across every wrap (a seek moves it within the current cycle).</summary>
        public double TotalSeconds => stopped || loop == null ? 0 : pendingSeek is double s ? cycles * Len + s : Total;
        /// <summary>Completed passes through the loop point.</summary>
        public int Cycle => stopped || loop == null ? 0 : cycles;
        public double DurationSeconds => stopped || loop == null ? 0 : length;
        public bool IsPlaying => !stopped && loop != null && !paused;
        public string ClockSource => stopped || loop == null ? "stopped" : loading ? "loading" : mainClock ? "main-thread clock" : "audio";
        public bool UsingMainClock => mainClock;
        public AudioClip? CurrentAudio => audio;
        public AudioSource? CurrentSource => source;
        public float CurrentGain => stopped ? 0f : gain * pauseGain;
        public string LastWarning => lastWarning;
        /// <summary>The loop arrived after the load time-out and joined the running clock.</summary>
        public int LateJoins { get; private set; }
        public int Loads => loads;
        /// <summary>Samples dropped from the decoded clip's start / end to match duets.json (0 when it matched).</summary>
        public int TrimmedLead { get; private set; }
        public int TrimmedTail { get; private set; }
        /// <summary>The audio thread's own unwrapped position (seconds; -1 before it plays).</summary>
        public double AudioTotalSeconds => audioTotal;

        double Len => length > 1e-6 ? length : 1;
        public bool Paused
        {
            get => paused;
            set
            {
                if (stopped || paused == value) return;
                paused = value;
                if (!value && source != null && audioPlaying && !source.isPlaying) source.UnPause();
            }
        }

        public double CurrentBeat => TotalSeconds;
        public double CurrentSemitones => 0;
        public double CurrentBpm => Current?.Bpm ?? 0;
        public MorphPlan CurrentPlan => MorphPlan.None;

        public DuetSegment? CurrentSegment => Current?.SegmentAt(CurrentSeconds);
        public double MixBeat => Current?.BeatAt(CurrentSeconds) ?? 0;

        // ------------------------------------------------------------------ playback

        /// <summary>Plays <paramref name="duet"/> from <paramref name="atSeconds"/>; <paramref name="tag"/> is the clip Started reports.</summary>
        public void PlayLoop(DuetLoop duet, double atSeconds, SongClip tag)
        {
            if (duet == null) throw new ArgumentNullException(nameof(duet));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (!stopped && ReferenceEquals(duet, loop))
            {
                clip = tag;
                Seek(atSeconds);
                Paused = false;
                return;
            }
            Silence();
            loop = duet;
            clip = tag;
            length = duet.Duration > 0 ? duet.Duration : 1;
            cycles = 0;
            position = duet.Wrap(atSeconds);
            lastCycle = 0;
            stopped = false;
            paused = false;
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
            gain = 0f;
            Ramp(1f, SeekFadeSeconds);

            if (ClockOnly || !Application.isPlaying || !isActiveAndEnabled || !duet.FileExists)
            {
                if (Application.isPlaying && !ClockOnly && !duet.FileExists) Warn($"MusicHistory: duet loop missing: {duet.AbsoluteFile}; keeping time silently.");
                mainClock = true;
                return;
            }
            if (audio != null && string.Equals(audioPath, duet.AbsoluteFile, StringComparison.OrdinalIgnoreCase))
            {
                StartAudio(position);
            }
            else
            {
                loading = true;
                StartCoroutine(Load(duet.AbsoluteFile, duet.Seconds));
            }
        }

        void ResetAudioClock()
        {
            audioTotal = -1;
            lastAudioPosition = -1;
            audioCycles = 0;
            sinceMove = 0;
            settle = SettleSeconds;
        }

        /// <summary>After a start or a seek, readings this long that disagree with the clock are not anchored on.</summary>
        const double SettleSeconds = .3;
        double settle;

        /// <summary>Jumps to <paramref name="seconds"/> in the loop (wrapped) with a short fade out and in; the cycle count stays.</summary>
        public void Seek(double seconds)
        {
            if (stopped || loop == null) return;
            double target = loop.Wrap(seconds);
            if (mainClock || loading || source == null || !audioPlaying || SeekFadeSeconds <= 0 || paused)
            {
                pendingSeek = null;
                Jump(target);
                return;
            }
            pendingSeek = target;
            Ramp(0f, SeekFadeSeconds);
        }

        /// <summary>Moves the clock (and the source) to <paramref name="target"/> within the current cycle.</summary>
        void Jump(double target)
        {
            position = target;
            lastCycle = cycles;
            ResetAudioClock();
            if (source != null && audio != null && audioPlaying) SetSourceTime(target);
        }

        /// <summary>ISongPlayer: seeks to the pair that <paramref name="next"/>'s song leads (S_k of pair k).</summary>
        public void Play(SongClip next, SongClip? previous)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (loop == null || stopped) throw new InvalidOperationException("No duet loop is loaded (PlayLoop first).");
            int pair = 0;
            foreach (DuetSong s in loop.Songs)
                if (s.NodeId == next.NodeId) pair = Math.Max(0, s.Step);
            clip = next;
            Seek(loop.PairStartSeconds(pair));
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
            if (stopped || loop == null) return;

            if (loading)
            {
                loadWait += dt;
                if (loadWait > LoadTimeoutSeconds)
                {
                    Warn($"MusicHistory: duet loop '{Path.GetFileName(loop.AbsoluteFile)}' still loading after {LoadTimeoutSeconds:0.##} s; keeping time until it arrives.");
                    loading = false;
                    lateLoad = true;
                    mainClock = true;
                }
                return;
            }
            if (paused) return;
            if (!startedRaised)
            {
                startedRaised = true;
                if (clip != null) Started?.Invoke(clip);
                if (stopped) return;
            }

            if (mainClock)
            {
                if (pendingSeek == null) AddTime(dt * (ClockFollowsTimeScale ? Mathf.Max(0f, Time.timeScale) : 1f));
            }
            else if (source != null && audio != null)
            {
                if (source.isPlaying)
                {
                    double pos = audio.frequency > 0 ? source.timeSamples / (double)audio.frequency : source.time;
                    if (lastAudioPosition >= 0 && pos < lastAudioPosition - length * .5) audioCycles++;   // the audio thread wrapped
                    bool moved = lastAudioPosition < 0 || Math.Abs(pos - lastAudioPosition) > 1e-6;
                    lastAudioPosition = pos;
                    if (moved)
                    {
                        stall = 0;
                        sinceMove = 0;
                        if (audioTotal < 0)
                        {
                            // The first reading after a start or a jump: the cycle the display clock is in.
                            double total = Total;
                            double anchorBase = cycles * Len;
                            if (anchorBase + pos < total - length * .5) anchorBase += length;
                            else if (anchorBase + pos > total + length * .5) anchorBase -= length;
                            // Right after a seek the source may still report where it was for a buffer
                            // or two: such a reading is not where the clock is, and is not anchored on.
                            if (settle > 0 && Math.Abs(anchorBase + pos - total) > SnapSeconds)
                            {
                                lastAudioPosition = -1;
                            }
                            else
                            {
                                audioCycles = 0;
                                audioBase = anchorBase;
                                audioTotal = audioBase + pos;
                            }
                        }
                        else audioTotal = audioBase + audioCycles * length + pos;
                    }
                    else
                    {
                        sinceMove += dt;
                        if ((stall += dt) > AudioStallSeconds) HandToClock("AudioSource.timeSamples does not advance (no audio device?)");
                    }
                    settle = Math.Max(0, settle - dt);
                    if (pendingSeek == null && audioTotal < 0) AddTime(dt);   // settling: the clock runs on by itself
                    if (pendingSeek == null && audioTotal >= 0 && !mainClock)
                    {
                        // Smooth display clock: predicted on, pulled onto the audio thread's position
                        // (carried on since its last step), snapped when far.
                        double measured = audioTotal + Math.Min(sinceMove, .1);
                        double predicted = Total + dt;
                        double err = measured - predicted;
                        SetTotal(Math.Abs(err) > SnapSeconds ? measured : predicted + err * Math.Min(1.0, dt * ClockPull));
                    }
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

            int cycle = cycles;
            if (cycle > lastCycle)
            {
                lastCycle = cycle;
                Wrapped?.Invoke(cycle);
            }
        }

        void HandToClock(string why)
        {
            mainClock = true;
            Warn($"MusicHistory: {why}; the duet loop keeps time on the main-thread clock.");
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
            if (audio.length > 0) length = audio.length;
            s.clip = audio;
            s.loop = true;
            SetSourceTime(at);
            ApplyVolume();
            // The audio thread loops the clip sample-accurately (AudioSource.loop on PCM).
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
            Transform existing = transform.Find("Duet Audio");
            GameObject host = existing != null ? existing.gameObject : new GameObject("Duet Audio");
            host.transform.SetParent(transform, false);
            source = host.GetComponent<AudioSource>();
            if (source == null) source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.volume = 0f;
            return source;
        }

        IEnumerator Load(string path, double contractSeconds)
        {
            loads++;
            AudioClip? decoded = null;
            string? error = null;
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;   // PCM: exact timeSamples and a gapless loop
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
                Warn($"MusicHistory: could not load duet loop '{path}' ({error ?? "no audio"}); keeping time silently.");
                if (loop != null && string.Equals(loop.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase))
                {
                    loading = false;
                    mainClock = true;
                }
                yield break;
            }
            decoded.name = Path.GetFileName(path);
            AudioClip trimmed = TrimToContract ? Trim(decoded, contractSeconds) : decoded;
            if (!ReferenceEquals(trimmed, decoded)) Destroy(decoded);
            if (audio != null && audio != trimmed)
            {
                if (source != null && source.clip == audio) source.clip = null;
                Destroy(audio);
            }
            audio = trimmed;
            audioPath = path;
            if (stopped || loop == null || !string.Equals(loop.AbsoluteFile, path, StringComparison.OrdinalIgnoreCase)) yield break;
            if (loading) StartAudio(position);
            else if (lateLoad)
            {
                LateJoins++;
                StartAudio(position);
            }
        }

        /// <summary>
        /// The decoded loop cut to exactly <paramref name="seconds"/>: an MP3 carries encoder delay
        /// and padding, which a decoder that ignores the LAME header leaves on the clip; looped as is,
        /// they would be a gap at the loop point. Drops up to <see cref="LeadTrimSamples"/> from the
        /// start and the rest from the end. Returns <paramref name="clip"/> when it already matches.
        /// </summary>
        AudioClip Trim(AudioClip clip, double seconds)
        {
            TrimmedLead = TrimmedTail = 0;
            if (seconds <= 0 || clip.frequency <= 0) return clip;
            int expected = (int)Math.Round(seconds * clip.frequency);
            int excess = clip.samples - expected;
            if (excess <= 0)
            {
                if (excess < -clip.frequency / 20)
                    Warn($"MusicHistory: '{clip.name}' decodes to {clip.length:0.000} s; duets.json says {seconds:0.000} s (shorter: the loop point moves).");
                return clip;
            }
            if (excess > clip.frequency / 4)
            {
                Warn($"MusicHistory: '{clip.name}' decodes to {clip.length:0.000} s; duets.json says {seconds:0.000} s: not trimmed.");
                return clip;
            }
            int lead = Math.Min(excess, LeadTrimSamples >= 0 ? LeadTrimSamples : 1105);
            int channels = Math.Max(1, clip.channels);
            float[] data = new float[expected * channels];
            if (!clip.GetData(data, lead)) return clip;
            AudioClip cut = AudioClip.Create(clip.name, expected, channels, clip.frequency, false);
            cut.SetData(data, 0);
            TrimmedLead = lead;
            TrimmedTail = excess - lead;
            Debug.Log($"MusicHistory: duet loop '{clip.name}' trimmed to {seconds:0.000} s ({lead} samples of encoder delay, {excess - lead} of padding).");
            return cut;
        }

        void OnDisable()
        {
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
