#nullable enable
using System;
using UnityEngine;

namespace MusicHistory.Playback
{
    /// <summary>
    /// The music's duck under a narration voice (unused while the narration is captions only, kept
    /// for when a voice comes back): a smooth envelope (smoothstep of a linear ramp) that
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
    /// Narrated walkthroughs (DESIGN.md §15), captions only: while a featured path plays its mashup
    /// mix, each narration cue's line (data/audio/narration, <see cref="NarrationCatalog"/>) shows as
    /// a caption (and its photo popup, <see cref="Viewer.NarrationOverlay"/>) from its time on the mix
    /// clock (<see cref="MashupPlayer.CurrentSeconds"/>), held long enough to read: at least the
    /// cue's spoken length, extended to <see cref="ReadingCharsPerSecond"/> (never shorter than
    /// <see cref="MinCaptionSeconds"/>) plus <see cref="CaptionHoldSeconds"/>, never into the next cue.
    ///
    /// There is no voiceover: the cue WAVs stay on disk for future use but are never loaded or
    /// played (no AudioClip, no AudioSource), and the mix is never ducked
    /// (<see cref="MashupPlayer.DuckGain"/> stays 1). <see cref="DuckEnvelope"/> is kept for when a
    /// voice comes back. The mix clock is the master: a pause, a seek (Next / Back / a chip / Enter)
    /// or Stop simply changes which caption shows; N switches the captions on and off.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-80)]   // after MashupPlayer (-90) has moved its clock, before the director
    public sealed class NarrationPlayer : MonoBehaviour
    {
        [Tooltip("N toggles the captions while a narrated path plays.")]
        public bool NarrationOn = true;
        [Tooltip("Reading speed a caption is held for (characters per second), when that is longer than its spoken length.")]
        [Min(1f)] public float ReadingCharsPerSecond = 15f;
        [Tooltip("A caption shows at least this long (never past the next cue).")]
        [Min(0f)] public float MinCaptionSeconds = 2f;
        [Tooltip("The caption stays this long after its reading time (never past the next cue).")]
        [Min(0f)] public float CaptionHoldSeconds = .6f;
        [Tooltip("Unused: there is no voiceover, so nothing touches audio. Kept for the validation's setup.")]
        public bool ClockOnly;

        public NarrationCatalog Catalog { get; private set; } = NarrationCatalog.Empty("not loaded");
        public MashupPlayer? Mix { get; private set; }
        /// <summary>Unused while there is no voiceover (it never engages); kept for a voice to come back.</summary>
        public DuckEnvelope Envelope { get; } = new();
        /// <summary>The narration of the mashup playing (null: none, or no narrated mashup plays).</summary>
        public NarrationPath? CurrentPath { get; private set; }
        /// <summary>The cue whose line the caption shows (its reading window; null when off or between captions).</summary>
        public NarrationCue? CaptionCue { get; private set; }
        /// <summary>Never a voice: always null.</summary>
        public NarrationCue? SpeakingCue => null;
        /// <summary>Never a voice: always false.</summary>
        public bool Speaking => false;
        /// <summary>Mix seconds last seen.</summary>
        public double MixSeconds { get; private set; }
        /// <summary>The mix is never ducked: 0 dB.</summary>
        public float DuckGainDb => 0f;
        public float DuckGain => 1f;
        /// <summary>Captions shown (a new cue's caption appearing counts once).</summary>
        public int CaptionsShown { get; private set; }
        /// <summary>No AudioSource is ever created (no voiceover).</summary>
        public AudioSource? Source => null;
        /// <summary>Raised when N switches the captions on or off.</summary>
        public event Action<bool>? Toggled;

        Func<Mashup?>? currentMashup;
        NarrationCue? lastCaption;

        /// <summary>
        /// Follows <paramref name="mix"/>'s clock for whatever mashup <paramref name="current"/> says
        /// plays now (the walkthrough's path mashup), captioned from <paramref name="catalog"/>.
        /// </summary>
        public void Bind(MashupPlayer mix, Func<Mashup?> current, NarrationCatalog catalog)
        {
            Mix = mix;
            mix.DuckGain = 1f;
            currentMashup = current;
            UseCatalog(catalog);
        }

        public void UseCatalog(NarrationCatalog catalog)
        {
            Catalog = catalog ?? NarrationCatalog.Empty("none");
            CurrentPath = null;
            CaptionCue = null;
            lastCaption = null;
            Envelope.Reset();
            if (Mix != null) Mix.DuckGain = 1f;
        }

        /// <summary>The narration a mashup / featured path id has (null when none).</summary>
        public NarrationPath? For(string? id) => Catalog.For(id);

        /// <summary>N: captions on or off.</summary>
        public void Toggle() => SetOn(!NarrationOn);

        public void SetOn(bool on)
        {
            if (NarrationOn == on) return;
            NarrationOn = on;
            lastCaption = null;
            Toggled?.Invoke(on);
        }

        /// <summary>Nothing to load: the captions are text (the WAVs are never decoded).</summary>
        public bool AllLoaded(NarrationPath? path) => true;

        /// <summary>No-op: the cue WAVs are never loaded (no voiceover).</summary>
        public void Preload(NarrationPath? path)
        {
        }

        /// <summary>When the caption of cue <paramref name="index"/> of <paramref name="path"/> goes (mix seconds).</summary>
        public double CaptionEnd(NarrationPath path, int index) =>
            path.CaptionEnd(index, ReadingCharsPerSecond, MinCaptionSeconds, CaptionHoldSeconds);

        void Update() => Advance(Time.unscaledDeltaTime);

        /// <summary>Follows the mix clock (public for edit-mode tests; the mix clock is the master, <paramref name="dt"/> is unused).</summary>
        public void Advance(double dt)
        {
            MashupPlayer? mix = Mix;
            Mashup? m = currentMashup != null ? currentMashup() : null;
            NarrationPath? path = m != null && mix != null && mix.Current == m ? Catalog.For(m.Id) : null;
            if (!ReferenceEquals(path, CurrentPath))
            {
                CurrentPath = path;
                lastCaption = null;
            }
            // Never ducked: the music always plays at its own level.
            if (mix != null && !Mathf.Approximately(mix.DuckGain, 1f))
            {
                mix.DuckGain = 1f;
                mix.RefreshVolume();
            }
            if (path == null || mix == null)
            {
                CaptionCue = null;
                return;
            }
            double t = mix.CurrentSeconds;
            MixSeconds = t;
            CaptionCue = NarrationOn ? path.ReadingCaptionAt(t, ReadingCharsPerSecond, MinCaptionSeconds, CaptionHoldSeconds) : null;
            if (CaptionCue != null && !ReferenceEquals(CaptionCue, lastCaption)) CaptionsShown++;
            lastCaption = CaptionCue;
        }

        void OnDisable()
        {
            if (Mix != null) Mix.DuckGain = 1f;
            CaptionCue = null;
        }
    }
}
