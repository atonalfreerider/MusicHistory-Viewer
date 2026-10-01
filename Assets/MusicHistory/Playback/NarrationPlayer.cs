#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicHistory.Playback
{
    /// <summary>
    /// The music's duck under narration: a smooth envelope (smoothstep of a linear ramp) that
    /// reaches the cue's depth <see cref="AttackSeconds"/> after the voice starts and is back at
    /// 0 dB <see cref="ReleaseSeconds"/> after it stops. It never ducks while not engaged.
    /// Engine-agnostic (edit-mode validation drives it directly).
    /// </summary>
    public sealed class DuckEnvelope
    {
        public float AttackSeconds = .15f;
        public float ReleaseSeconds = .6f;
        /// <summary>How far a change of depth between two engaged cues moves per second (dB).</summary>
        public float DepthSlewDbPerSecond = 40f;

        /// <summary>Linear progress 0 (open) .. 1 (fully ducked).</summary>
        public float Amount { get; private set; }
        /// <summary>The depth in use (dB ≤ 0): the speaking cue's, kept while releasing.</summary>
        public float DepthDb { get; private set; }
        public bool Engaged { get; private set; }

        /// <summary>Music gain now in dB (0 = not ducked).</summary>
        public float GainDb => DepthDb * Smooth(Amount);
        /// <summary>Music gain now, linear (1 = not ducked).</summary>
        public float Gain => GainDb >= -1e-4f ? 1f : Mathf.Pow(10f, GainDb / 20f);

        public static float Smooth(float u)
        {
            u = Mathf.Clamp01(u);
            return u * u * (3f - 2f * u);
        }

        /// <summary>Moves on by <paramref name="dt"/> seconds; <paramref name="engaged"/> while narration is heard, at <paramref name="depthDb"/>.</summary>
        public void Step(double dt, bool engaged, float depthDb)
        {
            float d = (float)Math.Max(0, dt);
            Engaged = engaged;
            depthDb = Mathf.Min(0f, depthDb);
            if (engaged)
            {
                // A fresh duck takes the cue's depth; a change between engaged cues slews.
                if (Amount <= 0f) DepthDb = depthDb;
                else DepthDb = Mathf.MoveTowards(DepthDb, depthDb, DepthSlewDbPerSecond * d);
                Amount = AttackSeconds > 0 ? Mathf.MoveTowards(Amount, 1f, d / AttackSeconds) : 1f;
                if (Amount > 1f - 1e-4f) Amount = 1f;   // float steps land exactly on the depth
            }
            else
            {
                Amount = ReleaseSeconds > 0 ? Mathf.MoveTowards(Amount, 0f, d / ReleaseSeconds) : 0f;
                if (Amount < 1e-4f) Amount = 0f;
                if (Amount <= 0f) DepthDb = 0f;
            }
        }

        public void Reset()
        {
            Amount = 0f;
            DepthDb = 0f;
            Engaged = false;
        }
    }

    /// <summary>
    /// Narrated walkthroughs (DESIGN.md §15): while a featured path plays its mashup mix, plays each
    /// narration cue's WAV (data/audio/narration, <see cref="NarrationCatalog"/>) at its time on
    /// the mix clock (<see cref="MashupPlayer.CurrentSeconds"/>) on an AudioSource of its own child
    /// object, and ducks the mix under it (<see cref="DuckEnvelope"/>, attack 0.15 s, release
    /// 0.6 s, to the cue's duck_db) through <see cref="MashupPlayer.DuckGain"/>.
    ///
    /// The mix clock is the master: a pause pauses the voice, a seek (Next / Back / a chip / Enter)
    /// stops a cue the new time is not in and starts the one it lands in when it lands within
    /// <see cref="MaxJoinLateSeconds"/> of its start (a jump into the middle of a sentence waits for
    /// the next cue; the caption still shows the line), a seek inside the speaking cue re-syncs it,
    /// and drift beyond <see cref="ResyncSeconds"/> is corrected. Stop (the tour ends) silences it
    /// at once and opens the duck. Without a narrated mashup, with narration off (N) or between
    /// cues the music is never ducked (after the release of the last line).
    ///
    /// <see cref="ClockOnly"/> (edit-mode validation, no audio device) runs the same schedule with
    /// no audio: a cue then "speaks" for its whole window while the mix clock runs.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]   // after MashupPlayer (-90) has moved its clock, before the director
    public sealed class NarrationPlayer : MonoBehaviour
    {
        [Tooltip("N toggles it while a narrated path plays.")]
        public bool NarrationOn = true;
        [Min(0f)] public float AttackSeconds = .15f;
        [Min(0f)] public float ReleaseSeconds = .6f;
        [Range(0f, 1.5f)] public float Volume = 1f;
        [Tooltip("A cue starts only when the mix reaches it within this many seconds of its start (a jump into a sentence waits for the next).")]
        [Min(0f)] public float MaxJoinLateSeconds = 1f;
        [Tooltip("Voice and mix further apart than this are re-synced.")]
        [Min(.02f)] public float ResyncSeconds = .15f;
        [Tooltip("The caption stays this long after the line ends (never past the next cue).")]
        [Min(0f)] public float CaptionHoldSeconds = .6f;
        [Tooltip("Fade of a cue cut by a seek or Stop.")]
        [Min(0f)] public float CutFadeSeconds = .05f;
        [Tooltip("Never load or play audio: the schedule and the duck run on the mix clock only (edit mode, tests).")]
        public bool ClockOnly;

        public NarrationCatalog Catalog { get; private set; } = NarrationCatalog.Empty("not loaded");
        public MashupPlayer? Mix { get; private set; }
        public DuckEnvelope Envelope { get; } = new();
        /// <summary>The narration of the mashup playing (null: none, or no narrated mashup plays).</summary>
        public NarrationPath? CurrentPath { get; private set; }
        /// <summary>The cue whose voice is heard now (null between cues, paused, off).</summary>
        public NarrationCue? SpeakingCue { get; private set; }
        /// <summary>The cue whose line the caption shows (its window plus <see cref="CaptionHoldSeconds"/>; null when off).</summary>
        public NarrationCue? CaptionCue { get; private set; }
        /// <summary>Narration is heard now (the duck is engaged).</summary>
        public bool Speaking { get; private set; }
        /// <summary>Mix seconds last seen.</summary>
        public double MixSeconds { get; private set; }
        /// <summary>Seconds into the speaking cue's WAV (the mix clock's view in <see cref="ClockOnly"/>).</summary>
        public double VoiceSeconds { get; private set; }
        public float DuckGainDb => Envelope.GainDb;
        public float DuckGain => Envelope.Gain;
        /// <summary>Cues started (a re-sync is not a start).</summary>
        public int Starts { get; private set; }
        public int Resyncs { get; private set; }
        /// <summary>Cues the mix jumped into too late to start (caption only).</summary>
        public int LateSkips { get; private set; }
        public string LastWarning { get; private set; } = "";
        public AudioSource? Source => source;
        /// <summary>Raised when N switches narration on or off.</summary>
        public event Action<bool>? Toggled;

        Func<Mashup?>? currentMashup;
        AudioSource? source;
        NarrationCue? voiceCue;
        NarrationCue? lastDue;
        bool lateCounted, voicePaused;
        float voiceGain;
        bool stopAfterFade;
        readonly Dictionary<string, AudioClip?> clips = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> loading = new(StringComparer.OrdinalIgnoreCase);
        NarrationPath? preloaded;

        /// <summary>
        /// Follows <paramref name="mix"/>'s clock for whatever mashup <paramref name="current"/> says
        /// plays now (the walkthrough's path mashup), narrated from <paramref name="catalog"/>.
        /// </summary>
        public void Bind(MashupPlayer mix, Func<Mashup?> current, NarrationCatalog catalog)
        {
            if (Mix != null && Mix != mix) Mix.DuckGain = 1f;
            Mix = mix;
            currentMashup = current;
            UseCatalog(catalog);
        }

        public void UseCatalog(NarrationCatalog catalog)
        {
            Catalog = catalog ?? NarrationCatalog.Empty("none");
            StopVoice(immediate: true);
            ReleaseClips();
            preloaded = null;
            CurrentPath = null;
            CaptionCue = null;
            Envelope.Reset();
            if (Mix != null) Mix.DuckGain = 1f;
        }

        /// <summary>The narration a mashup / featured path id has (null when none).</summary>
        public NarrationPath? For(string? id) => Catalog.For(id);

        /// <summary>N: narration on or off (off silences the voice, releases the duck and hides the caption).</summary>
        public void Toggle() => SetOn(!NarrationOn);

        public void SetOn(bool on)
        {
            if (NarrationOn == on) return;
            NarrationOn = on;
            if (!on) StopVoice(immediate: false);
            lastDue = null;
            Toggled?.Invoke(on);
        }

        /// <summary>Every cue WAV of <paramref name="path"/> is decoded (or failed, or has no file).</summary>
        public bool AllLoaded(NarrationPath? path)
        {
            if (path == null || ClockOnly) return true;
            foreach (NarrationCue c in path.Cues)
                if (c.FileExists && !clips.ContainsKey(c.AbsoluteFile)) return false;
            return true;
        }

        /// <summary>Starts decoding every cue of <paramref name="path"/> (play mode).</summary>
        public void Preload(NarrationPath? path)
        {
            if (path == null || ClockOnly || !Application.isPlaying || !isActiveAndEnabled) return;
            foreach (NarrationCue c in path.Cues) Request(c);
        }

        void Update() => Advance(Time.unscaledDeltaTime);

        /// <summary>Follows the mix clock by <paramref name="dt"/> seconds of real time (public for edit-mode tests).</summary>
        public void Advance(double dt)
        {
            MashupPlayer? mix = Mix;
            Mashup? m = currentMashup != null ? currentMashup() : null;
            NarrationPath? path = m != null && mix != null && mix.Current == m ? Catalog.For(m.Id) : null;
            if (!ReferenceEquals(path, CurrentPath))
            {
                CurrentPath = path;
                lastDue = null;
                if (path != null && !ReferenceEquals(path, preloaded))
                {
                    preloaded = path;
                    Preload(path);
                }
            }
            if (path == null || mix == null)
            {
                // No narrated mix (the tour stopped): silence now, no duck.
                StopVoice(immediate: true);
                SpeakingCue = null;
                CaptionCue = null;
                Speaking = false;
                Envelope.Reset();
                if (mix != null && !Mathf.Approximately(mix.DuckGain, 1f))
                {
                    mix.DuckGain = 1f;
                    mix.RefreshVolume();
                }
                StepVoiceFade(dt);
                return;
            }

            double t = mix.CurrentSeconds;
            MixSeconds = t;
            string clock = mix.ClockSource;
            bool running = mix.IsPlaying && !mix.Paused && clock != "loading" && clock != "stopped";
            NarrationCue? due = NarrationOn ? path.CueAt(t) : null;
            CaptionCue = NarrationOn ? path.CaptionAt(t, CaptionHoldSeconds) : null;

            if (voiceCue != null && !ReferenceEquals(voiceCue, due)) StopVoice(immediate: false);
            if (!ReferenceEquals(due, lastDue))
            {
                lastDue = due;
                lateCounted = false;
            }
            if (voiceCue == null && due != null && running)
            {
                double offset = t - due.At;
                if (offset <= MaxJoinLateSeconds + 1e-6) StartVoice(due, offset);
                else if (!lateCounted)
                {
                    lateCounted = true;
                    LateSkips++;
                }
            }

            bool heard = false;
            if (voiceCue != null)
            {
                double expected = t - voiceCue.At;
                if (ClockOnly)
                {
                    VoiceSeconds = expected;
                    heard = running && voiceCue.Contains(t);
                }
                else if (source != null && source.clip != null)
                {
                    if (!running)
                    {
                        if (source.isPlaying)
                        {
                            source.Pause();
                            voicePaused = true;
                        }
                    }
                    else
                    {
                        if (voicePaused)
                        {
                            source.UnPause();
                            voicePaused = false;
                        }
                        else if (!source.isPlaying && expected >= 0 && expected < source.clip.length - .05)
                        {
                            // Ended or cut, and the mix is back inside the line (a seek within the cue).
                            SetVoiceTime(expected);
                            source.Play();
                        }
                        if (source.isPlaying)
                        {
                            double actual = source.clip.frequency > 0 ? source.timeSamples / (double)source.clip.frequency : source.time;
                            if (Math.Abs(actual - expected) > ResyncSeconds && expected >= 0 && expected < source.clip.length - .05)
                            {
                                SetVoiceTime(expected);
                                Resyncs++;
                                actual = expected;
                            }
                            VoiceSeconds = actual;
                        }
                    }
                    heard = running && source.isPlaying && !stopAfterFade;
                }
            }
            SpeakingCue = heard ? voiceCue : null;
            Speaking = heard;

            Envelope.AttackSeconds = AttackSeconds;
            Envelope.ReleaseSeconds = ReleaseSeconds;
            Envelope.Step(dt, heard, (float)(voiceCue?.DuckDb ?? Envelope.DepthDb));
            float gain = Envelope.Gain;
            if (!Mathf.Approximately(mix.DuckGain, gain))
            {
                mix.DuckGain = gain;
                mix.RefreshVolume();
            }
            StepVoiceFade(dt);
        }

        // ------------------------------------------------------------------ the voice

        bool StartVoice(NarrationCue cue, double offset)
        {
            if (ClockOnly)
            {
                voiceCue = cue;
                VoiceSeconds = Math.Max(0, offset);
                Starts++;
                return true;
            }
            if (!cue.FileExists) return false;
            if (!clips.TryGetValue(cue.AbsoluteFile, out AudioClip? clip))
            {
                Request(cue);
                return false;
            }
            if (clip == null) return false;   // failed to load: caption only
            AudioSource? s = EnsureSource();
            if (s == null) return false;
            s.Stop();
            s.clip = clip;
            voiceCue = cue;
            voicePaused = false;
            stopAfterFade = false;
            SetVoiceTime(Math.Max(0, offset));
            voiceGain = 1f;
            ApplyVoiceVolume();
            s.Play();
            VoiceSeconds = Math.Max(0, offset);
            Starts++;
            return true;
        }

        void StopVoice(bool immediate)
        {
            voiceCue = null;
            voicePaused = false;
            if (source == null) return;
            if (immediate || CutFadeSeconds <= 0 || !source.isPlaying)
            {
                source.Stop();
                stopAfterFade = false;
                voiceGain = 0f;
                ApplyVoiceVolume();
                return;
            }
            stopAfterFade = true;
        }

        void StepVoiceFade(double dt)
        {
            if (source == null) return;
            if (stopAfterFade)
            {
                voiceGain = Mathf.MoveTowards(voiceGain, 0f, CutFadeSeconds > 0 ? (float)dt / CutFadeSeconds : 1f);
                if (voiceGain <= 0f)
                {
                    source.Stop();
                    stopAfterFade = false;
                }
            }
            ApplyVoiceVolume();
        }

        void ApplyVoiceVolume()
        {
            if (source != null) source.volume = Mathf.Clamp01(Volume * voiceGain);
        }

        void SetVoiceTime(double seconds)
        {
            if (source == null || source.clip == null) return;
            AudioClip c = source.clip;
            source.timeSamples = Mathf.Clamp((int)(seconds * c.frequency), 0, Math.Max(0, c.samples - 1));
        }

        AudioSource? EnsureSource()
        {
            if (source != null) return source;
            if (this == null) return null;
            // Own child object: the MIDI synth's OnAudioFilterRead on the walkthrough's GameObject
            // would otherwise process this AudioSource too.
            Transform existing = transform.Find("Narration Audio");
            GameObject host = existing != null ? existing.gameObject : new GameObject("Narration Audio");
            host.transform.SetParent(transform, false);
            source = host.GetComponent<AudioSource>();
            if (source == null) source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.priority = 0;   // never culled
            source.bypassReverbZones = true;
            source.volume = 0f;
            return source;
        }

        // ------------------------------------------------------------------ loading

        void Request(NarrationCue cue)
        {
            if (!cue.FileExists || clips.ContainsKey(cue.AbsoluteFile) || loading.Contains(cue.AbsoluteFile)) return;
            if (!Application.isPlaying || !isActiveAndEnabled) return;
            loading.Add(cue.AbsoluteFile);
            StartCoroutine(Load(cue.AbsoluteFile));
        }

        IEnumerator Load(string path)
        {
            AudioClip? decoded = null;
            string? error = null;
            using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.WAV))
            {
                if (req.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                    handler.compressed = false;   // exact timeSamples
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
            loading.Remove(path);
            if (this == null)
            {
                if (decoded != null) Destroy(decoded);
                yield break;
            }
            if (decoded == null) Warn($"MusicHistory: could not load narration '{path}' ({error ?? "no audio"}); its caption shows without the voice.");
            else decoded.name = Path.GetFileName(path);
            clips[path] = decoded;
        }

        void Warn(string message)
        {
            if (message == LastWarning) return;
            LastWarning = message;
            Debug.LogWarning(message);
        }

        void ReleaseClips()
        {
            if (source != null)
            {
                source.Stop();
                source.clip = null;
            }
            foreach (AudioClip? c in clips.Values)
                if (c != null)
                {
                    if (Application.isPlaying) Destroy(c);
                    else DestroyImmediate(c);
                }
            clips.Clear();
        }

        void OnDisable()
        {
            if (source != null) source.Stop();
            voiceCue = null;
            stopAfterFade = false;
            loading.Clear();
            if (Mix != null) Mix.DuckGain = 1f;
            Envelope.Reset();
        }

        void OnDestroy() => ReleaseClips();
    }
}
