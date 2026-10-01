#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MusicHistory.Playback
{
    /// <summary>What a stretch of a mashup mix plays (mashups.json segment "kind").</summary>
    public enum MashupSegmentKind
    {
        /// <summary>One song's full mix (the root song's opening phrase, the last song's ending).</summary>
        Full,
        /// <summary>The previous song's instrumental with the next song's vocal, matched bar for bar.</summary>
        Changeover,
        /// <summary>The next song's full mix gliding from the previous key/tempo to its own.</summary>
        Morph
    }

    /// <summary>One segment of a mashup mix (seconds of the mix).</summary>
    public sealed class MashupSegment
    {
        public int Index;
        public double Start, End;
        public MashupSegmentKind Kind;
        /// <summary>work_id of the instrumental heard (the full mix for "full" and "morph").</summary>
        public string Instrumental = "";
        /// <summary>work_id of the vocal heard ("" when none).</summary>
        public string Vocal = "";
        /// <summary>Key heard ("C major"), the normalized-frame key never: what plays.</summary>
        public string Key = "";
        /// <summary>Tempo heard (for a morph, the value it ends on).</summary>
        public double Bpm;
        public double BpmStart;
        public int? VocalShiftSemitones;
        public double? VocalTempoRatio;
        /// <summary>Bar-level chord agreement of the vocal's source against the instrumental, 0..1.</summary>
        public double? ChordMatch;
        public double? BeatErrorMs;
        /// <summary>Index into <see cref="Mashup.Songs"/> of the instrumental's song (-1 = unknown work_id).</summary>
        public int InstrumentalSong = -1;
        /// <summary>Index into <see cref="Mashup.Songs"/> of the vocal's song (-1 = none).</summary>
        public int VocalSong = -1;

        public double Length => Math.Max(0, End - Start);
        public bool HasVocal => Vocal.Length > 0;
        public bool Contains(double t) => t >= Start && t < End;

        /// <summary>Smoothstep progress 0..1 of a morph at mix time <paramref name="t"/> (1 for the other kinds).</summary>
        public double Glide(double t)
        {
            if (Kind != MashupSegmentKind.Morph || Length <= 0) return 1;
            double u = Math.Max(0, Math.Min(1, (t - Start) / Length));
            return u * u * (3 - 2 * u);
        }

        /// <summary>Tempo heard at mix time <paramref name="t"/> (a morph glides from <see cref="BpmStart"/>).</summary>
        public double BpmAt(double t)
        {
            double from = BpmStart > 0 ? BpmStart : Bpm;
            return from + (Bpm - from) * Glide(t);
        }

        public static string KindName(MashupSegmentKind kind) => kind switch
        {
            MashupSegmentKind.Changeover => "changeover",
            MashupSegmentKind.Morph => "morph",
            _ => "full"
        };
    }

    /// <summary>A point of a sung melody: phrase beat and MIDI pitch (NaN = unvoiced break).</summary>
    public readonly struct MelodyPoint
    {
        public readonly double Beat;
        public readonly float Pitch;

        public MelodyPoint(double beat, float pitch)
        {
            Beat = beat;
            Pitch = pitch;
        }

        public bool Voiced => !float.IsNaN(Pitch);
    }

    /// <summary>One chord of a song's instrumental phrase, in phrase beats and the normalized frame.</summary>
    public sealed class MashupChord
    {
        public double Start, End;
        /// <summary>Root pitch class 0..11 in the normalized (C major / A minor) frame.</summary>
        public int RootPc;
        /// <summary>"maj", "min", "dim", "aug", "sus" or "other".</summary>
        public string Quality = "";
        /// <summary>Roman numeral ("vi").</summary>
        public string Roman = "";

        public bool Minor => Quality == "min" || Quality == "dim";
    }

    /// <summary>One song of a mashup: its melody, its chords and when its stems sound.</summary>
    public sealed class MashupSong
    {
        public int Index;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        /// <summary>Order in the path as written in mashups.json.</summary>
        public int Step;
        /// <summary>The featured path's step that plays this song (-1 until bound).</summary>
        public int PathStep = -1;
        /// <summary>Graph node (0 = not in the graph).</summary>
        public int NodeId;
        /// <summary>
        /// As sung, in time order: phrase beats rise and wrap back to 0 where the vocal window
        /// crosses the phrase's end. NaN pitch = unvoiced.
        /// </summary>
        public readonly List<MelodyPoint> Melody = new();
        List<MelodyPoint>? byBeat;

        /// <summary>The melody sorted by phrase beat (for lookups at a beat).</summary>
        public IReadOnlyList<MelodyPoint> ByBeat
        {
            get
            {
                if (byBeat != null && byBeat.Count == Melody.Count) return byBeat;
                byBeat = new List<MelodyPoint>(Melody);
                // Stable: points at the same beat keep their sung order.
                int[] order = new int[byBeat.Count];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                Array.Sort(order, (a, b) => Melody[a].Beat != Melody[b].Beat ? Melody[a].Beat.CompareTo(Melody[b].Beat) : a.CompareTo(b));
                for (int i = 0; i < order.Length; i++) byBeat[i] = Melody[order[i]];
                return byBeat;
            }
        }
        public readonly List<MashupChord> Chords = new();
        public readonly List<(double start, double end)> VocalAudible = new();
        public readonly List<(double start, double end)> InstrumentalAudible = new();

        public bool VocalAt(double t) => In(VocalAudible, t);
        public bool InstrumentalAt(double t) => In(InstrumentalAudible, t);

        static bool In(List<(double start, double end)> spans, double t)
        {
            foreach ((double a, double b) in spans)
                if (t >= a && t < b) return true;
            return false;
        }

        public int VoicedCount
        {
            get
            {
                int n = 0;
                foreach (MelodyPoint p in Melody) if (p.Voiced) n++;
                return n;
            }
        }

        /// <summary>The chord sounding at phrase beat <paramref name="beat"/> (null in a gap).</summary>
        public MashupChord? ChordAt(double beat)
        {
            foreach (MashupChord c in Chords)
                if (beat >= c.Start && beat < c.End) return c;
            return null;
        }

        /// <summary>
        /// The melody at phrase beat <paramref name="beat"/>: <paramref name="voiced"/> when the voice
        /// sings there (pitch interpolated between neighbouring points), else the pitch it last sang
        /// (or next sings) within <paramref name="reach"/> beats, so a marker can rest during a breath.
        /// False when nothing is sung within reach.
        /// </summary>
        public bool PitchAt(double beat, out float pitch, out bool voiced, double reach = 2.0)
        {
            pitch = float.NaN;
            voiced = false;
            IReadOnlyList<MelodyPoint> pts = ByBeat;
            int n = pts.Count;
            if (n == 0) return false;
            int i = LastAtOrBefore(pts, beat);
            if (i >= 0)
            {
                MelodyPoint a = pts[i];
                MelodyPoint? b = i + 1 < n ? pts[i + 1] : null;
                if (a.Voiced)
                {
                    if (b is MelodyPoint nb && nb.Voiced && nb.Beat - a.Beat <= MaxGapBeats)
                    {
                        double u = nb.Beat > a.Beat ? (beat - a.Beat) / (nb.Beat - a.Beat) : 0;
                        pitch = (float)(a.Pitch + (nb.Pitch - a.Pitch) * Math.Max(0, Math.Min(1, u)));
                        voiced = true;
                        return true;
                    }
                    if (beat - a.Beat <= HoldBeats)
                    {
                        pitch = a.Pitch;
                        voiced = true;
                        return true;
                    }
                }
            }
            // A breath: the nearest sung pitch within reach (the one before first).
            double best = double.MaxValue;
            for (int k = Math.Max(0, i); k >= 0; k--)
            {
                MelodyPoint p = pts[k];
                if (beat - p.Beat > reach) break;
                if (!p.Voiced) continue;
                best = beat - p.Beat;
                pitch = p.Pitch;
                break;
            }
            for (int k = i + 1; k < n; k++)
            {
                MelodyPoint p = pts[k];
                if (p.Beat - beat > reach || p.Beat - beat >= best) break;
                if (!p.Voiced) continue;
                pitch = p.Pitch;
                break;
            }
            return !float.IsNaN(pitch);
        }

        /// <summary>Points further apart than this (beats) are not joined by a line.</summary>
        public const double MaxGapBeats = .5;
        /// <summary>A last voiced point counts as still sounding this long (beats) after it.</summary>
        public const double HoldBeats = .25;

        static int LastAtOrBefore(IReadOnlyList<MelodyPoint> pts, double beat)
        {
            int lo = 0, hi = pts.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (pts[mid].Beat <= beat)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }
    }

    /// <summary>
    /// A featured path played as one continuous mashup mix (data/audio/mashups/&lt;id&gt;/mix.mp3):
    /// the root song's opening, then changeovers (the next song's vocal over the previous song's
    /// instrumental, matched in key, tempo and chords) and short morphs, to the last song's ending.
    /// </summary>
    public sealed class Mashup
    {
        public int Index;
        /// <summary>The featured path id (paths.json).</summary>
        public string Id = "";
        public string Title = "";
        /// <summary>As written ("&lt;id&gt;/mix.mp3", relative to the mashups folder).</summary>
        public string File = "";
        public string AbsoluteFile = "";
        public bool FileExists;
        public double Seconds;
        public int BeatsPerBar = 4;
        public double PhraseBeats = 32;
        public readonly List<MashupSegment> Segments = new();
        /// <summary>Every beat of the mix: its time and its position in the phrase.</summary>
        public double[] BeatTimes = Array.Empty<double>();
        public double[] BeatPhrase = Array.Empty<double>();
        public readonly List<MashupSong> Songs = new();
        /// <summary>The featured path it plays (after <see cref="MashupCatalog.Bind"/>).</summary>
        public FeaturedPath? Path;

        /// <summary>Bound to its featured path, every song in the graph and on the path, with segments.</summary>
        public bool IsPlayable
        {
            get
            {
                if (Path == null || Segments.Count == 0 || Songs.Count == 0) return false;
                foreach (MashupSong s in Songs) if (s.NodeId <= 0 || s.PathStep < 0) return false;
                foreach (MashupSegment g in Segments) if (g.InstrumentalSong < 0) return false;
                return true;
            }
        }

        public double Duration => Seconds > 0 ? Seconds : Segments.Count > 0 ? Segments[^1].End : 0;
        public int Bars => BeatsPerBar > 0 ? (int)Math.Round(PhraseBeats / BeatsPerBar) : 0;

        /// <summary>The segment playing at <paramref name="t"/> (clamped to the first and last; -1 without segments).</summary>
        public int SegmentIndexAt(double t)
        {
            int n = Segments.Count;
            if (n == 0) return -1;
            if (t < Segments[0].Start) return 0;
            int lo = 0, hi = n - 1, found = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Segments[mid].Start <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        public MashupSegment? SegmentAt(double t)
        {
            int i = SegmentIndexAt(t);
            return i >= 0 ? Segments[i] : null;
        }

        /// <summary>
        /// Position in the phrase at mix time <paramref name="t"/>, interpolated between the beats
        /// (0 ≤ result &lt; <see cref="PhraseBeats"/>). Across the phrase's end it wraps; a jump in
        /// the beat grid (a new phrase frame) is not interpolated across.
        /// </summary>
        public double PhraseBeatAt(double t)
        {
            double p = PhraseBeats > 0 ? PhraseBeats : 1;
            int n = BeatTimes.Length;
            if (n == 0)
            {
                double bpm = Segments.Count > 0 && Segments[0].Bpm > 0 ? Segments[0].Bpm : 120;
                return Wrap(t * bpm / 60.0, p);
            }
            if (n == 1)
            {
                double bpm = Segments.Count > 0 && Segments[0].Bpm > 0 ? Segments[0].Bpm : 120;
                return Wrap(BeatPhrase[0] + (t - BeatTimes[0]) * bpm / 60.0, p);
            }
            int i = LastBeatAtOrBefore(t);
            if (i < 0)
            {
                double dt = Math.Max(1e-6, BeatTimes[1] - BeatTimes[0]);
                return Wrap(BeatPhrase[0] - (BeatTimes[0] - t) / dt, p);
            }
            if (i >= n - 1)
            {
                double dt = Math.Max(1e-6, BeatTimes[n - 1] - BeatTimes[n - 2]);
                return Wrap(BeatPhrase[n - 1] + (t - BeatTimes[n - 1]) / dt, p);
            }
            double t0 = BeatTimes[i], t1 = BeatTimes[i + 1];
            double d = BeatPhrase[i + 1] - BeatPhrase[i];
            if (d < 0) d += p;   // wrapped at the phrase end
            if (d <= 1e-9 || d > 2.5) d = 1;   // a jump: keep counting one beat from where it was
            double u = t1 > t0 ? Math.Max(0, Math.Min(1, (t - t0) / (t1 - t0))) : 0;
            return Wrap(BeatPhrase[i] + u * d, p);
        }

        static double Wrap(double v, double p)
        {
            double r = v % p;
            if (r < 0) r += p;
            return r >= p ? 0 : r;
        }

        int LastBeatAtOrBefore(double t)
        {
            int lo = 0, hi = BeatTimes.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (BeatTimes[mid] <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>Songs whose vocal / instrumental is audible at <paramref name="t"/> (indices into <see cref="Songs"/>).</summary>
        public void AudibleAt(double t, List<int> vocals, List<int> instrumentals)
        {
            vocals.Clear();
            instrumentals.Clear();
            for (int i = 0; i < Songs.Count; i++)
            {
                if (Songs[i].VocalAt(t)) vocals.Add(i);
                if (Songs[i].InstrumentalAt(t)) instrumentals.Add(i);
            }
        }

        /// <summary>The song the path's step <paramref name="pathStep"/> plays (null when unbound).</summary>
        public MashupSong? SongForStep(int pathStep)
        {
            foreach (MashupSong s in Songs) if (s.PathStep == pathStep) return s;
            return null;
        }

        /// <summary>
        /// Where the path's step <paramref name="pathStep"/> starts in the mix: 0 for the first song;
        /// otherwise where its vocal first enters (its changeover), else its first segment.
        /// </summary>
        public double StepStartSeconds(int pathStep)
        {
            if (pathStep <= 0 || Segments.Count == 0) return 0;
            MashupSong? song = SongForStep(pathStep);
            if (song == null) return 0;
            foreach (MashupSegment g in Segments)
                if (g.VocalSong == song.Index) return g.Start;
            foreach (MashupSegment g in Segments)
                if (g.InstrumentalSong == song.Index) return g.Start;
            return 0;
        }

        /// <summary>The last step whose start (<see cref="StepStartSeconds"/>) is at or before <paramref name="t"/>.</summary>
        public int StepReachedAt(double t, int steps)
        {
            int reached = 0;
            for (int i = 1; i < steps; i++)
                if (StepStartSeconds(i) <= t + 1e-6) reached = i;
            return reached;
        }

        /// <summary>The path step of a segment's instrumental (-1 when unbound).</summary>
        public int InstrumentalStep(MashupSegment g) => g.InstrumentalSong >= 0 ? Songs[g.InstrumentalSong].PathStep : -1;

        /// <summary>The path step of a segment's vocal (-1 when none).</summary>
        public int VocalStep(MashupSegment g) => g.VocalSong >= 0 ? Songs[g.VocalSong].PathStep : -1;

        /// <summary>Lowest and highest sung pitch of every song (false when nothing is voiced).</summary>
        public bool PitchRange(out float lo, out float hi)
        {
            lo = float.MaxValue;
            hi = float.MinValue;
            foreach (MashupSong s in Songs)
                foreach (MelodyPoint p in s.Melody)
                {
                    if (!p.Voiced) continue;
                    lo = Math.Min(lo, p.Pitch);
                    hi = Math.Max(hi, p.Pitch);
                }
            return lo <= hi;
        }
    }

    /// <summary>
    /// The featured paths' mashup mixes: data/audio/mashups/mashups.json (UTF-8, version 1):
    /// <c>{ version, generated_at, frame, paths: [ { id, title, file, seconds, beats_per_bar,
    /// phrase_beats, segments: [ { start, end, kind, instrumental, vocal, key, bpm, bpm_start,
    /// vocal_shift_semitones, vocal_tempo_ratio, chord_match, beat_error_ms } ], beats: [ [t, phrase_beat] ],
    /// songs: [ { work_id, title, artist, year, step, melody: [ [phrase_beat, pitch|null] ],
    /// chords: [ [start, end, root_pc, quality, roman] ], vocal_audible: [ [s, e] ],
    /// instrumental_audible: [ [s, e] ] } ] } ] }</c>. Each mix file (relative to the mashups
    /// folder) is a 44.1 kHz stereo MP3. Vocals are audio only: the file holds no words. A missing
    /// or unreadable file gives an empty catalog (<see cref="Status"/> says why); contract
    /// violations go to <see cref="Problems"/> and never throw. Pure C#: no Unity API.
    /// </summary>
    public sealed class MashupCatalog
    {
        public const string FileName = "mashups.json";
        public const string CommandLineFlag = "-musicHistoryMashups";
        public const int ContractVersion = 1;

        public int Version;
        public string GeneratedAt = "";
        public string Frame = "";
        public readonly List<Mashup> Mashups = new();
        public readonly List<string> Problems = new();
        public string SourcePath = "";
        /// <summary>Folder the mix files are relative to (the folder of mashups.json).</summary>
        public string Dir = "";
        public bool Loaded;
        public string Status = "";

        public static MashupCatalog Empty(string status) => new() { Status = status };

        public int PlayableCount
        {
            get
            {
                int n = 0;
                foreach (Mashup m in Mashups) if (m.IsPlayable) n++;
                return n;
            }
        }

        public Mashup? Find(string id)
        {
            foreach (Mashup m in Mashups) if (m.Id == id) return m;
            return null;
        }

        /// <summary>The playable mashup of <paramref name="path"/> (bound to that very path object), else null.</summary>
        public Mashup? For(FeaturedPath? path)
        {
            if (path == null) return null;
            foreach (Mashup m in Mashups)
                if (ReferenceEquals(m.Path, path) && m.IsPlayable) return m;
            return null;
        }

        /// <summary>Reads <paramref name="jsonPath"/>; never throws (a missing file gives an empty catalog).</summary>
        public static MashupCatalog Load(string jsonPath)
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(jsonPath);
            }
            catch (Exception e)
            {
                return Empty($"bad path '{jsonPath}': {e.Message}");
            }
            if (!System.IO.File.Exists(full)) return new MashupCatalog { Status = "missing: " + full, SourcePath = full };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new MashupCatalog { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        public static MashupCatalog Parse(string json, string dir, string sourcePath = "")
        {
            MashupCatalog c = new() { SourcePath = sourcePath, Dir = dir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("mashups.json is not valid JSON: " + e.Message);
                return c;
            }
            if (root is not Dictionary<string, object?> doc)
            {
                c.Status = "unreadable: the top level is not an object";
                c.Problems.Add(c.Status);
                return c;
            }
            c.Loaded = true;
            c.Version = (int)MiniJson.Num(doc, "version", 0);
            if (c.Version != ContractVersion) c.Problems.Add($"version {c.Version}, expected {ContractVersion}");
            c.GeneratedAt = MiniJson.Str(doc, "generated_at");
            c.Frame = MiniJson.Str(doc, "frame");
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(doc, "paths"))
            {
                if (item is not Dictionary<string, object?> p)
                {
                    c.Problems.Add("a paths entry is not an object");
                    continue;
                }
                Mashup m = ReadMashup(c, p, dir);
                if (m.Id.Length > 0 && !ids.Add(m.Id)) c.Problems.Add($"mashup '{m.Id}': duplicate id");
                c.Mashups.Add(m);
            }
            c.Status = $"loaded {c.Mashups.Count} mashup{(c.Mashups.Count == 1 ? "" : "s")}";
            return c;
        }

        static Mashup ReadMashup(MashupCatalog c, Dictionary<string, object?> p, string dir)
        {
            Mashup m = new()
            {
                Index = c.Mashups.Count,
                Id = MiniJson.Str(p, "id"),
                Title = MiniJson.Str(p, "title"),
                File = MiniJson.Str(p, "file"),
                Seconds = MiniJson.Num(p, "seconds", 0),
                BeatsPerBar = (int)Math.Round(MiniJson.Num(p, "beats_per_bar", 4)),
                PhraseBeats = MiniJson.Num(p, "phrase_beats", 0)
            };
            string ctx = $"mashup '{(m.Id.Length > 0 ? m.Id : "#" + m.Index)}'";
            if (m.Id.Length == 0) c.Problems.Add($"{ctx}: no id");
            if (m.BeatsPerBar <= 0)
            {
                c.Problems.Add($"{ctx}: beats_per_bar {m.BeatsPerBar}");
                m.BeatsPerBar = 4;
            }
            if (m.PhraseBeats <= 0)
            {
                c.Problems.Add($"{ctx}: phrase_beats {m.PhraseBeats}");
                m.PhraseBeats = 8 * m.BeatsPerBar;
            }
            if (m.Seconds <= 0) c.Problems.Add($"{ctx}: seconds {m.Seconds}");
            if (m.File.Length == 0) c.Problems.Add($"{ctx}: no file");
            else
            {
                try
                {
                    m.AbsoluteFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, m.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    m.FileExists = System.IO.File.Exists(m.AbsoluteFile);
                }
                catch (Exception e)
                {
                    c.Problems.Add($"{ctx}: bad file '{m.File}' ({e.Message})");
                }
                if (!m.FileExists) c.Problems.Add($"{ctx}: mix missing: {m.File}");
            }

            // Songs first: segments refer to them by work_id.
            Dictionary<string, int> byWork = new(StringComparer.Ordinal);
            foreach (object? s in MiniJson.Arr(p, "songs"))
            {
                if (s is not Dictionary<string, object?> song)
                {
                    c.Problems.Add($"{ctx}: a song is not an object");
                    continue;
                }
                MashupSong ms = ReadSong(c, m, song, ctx);
                if (ms.WorkId.Length == 0) c.Problems.Add($"{ctx}: a song has no work_id");
                else if (byWork.ContainsKey(ms.WorkId)) c.Problems.Add($"{ctx}: song {ms.WorkId} listed twice");
                else byWork[ms.WorkId] = ms.Index;
                m.Songs.Add(ms);
            }
            if (m.Songs.Count == 0) c.Problems.Add($"{ctx}: no songs");

            double previousEnd = double.NaN;
            foreach (object? s in MiniJson.Arr(p, "segments"))
            {
                if (s is not Dictionary<string, object?> g)
                {
                    c.Problems.Add($"{ctx}: a segment is not an object");
                    continue;
                }
                MashupSegment seg = new()
                {
                    Index = m.Segments.Count,
                    Start = MiniJson.Num(g, "start", 0),
                    End = MiniJson.Num(g, "end", 0),
                    Instrumental = MiniJson.Str(g, "instrumental"),
                    Vocal = MiniJson.Str(g, "vocal"),
                    Key = MiniJson.Str(g, "key"),
                    Bpm = MiniJson.Num(g, "bpm", 0),
                    BpmStart = MiniJson.Num(g, "bpm_start", 0),
                    VocalShiftSemitones = OptNum(g, "vocal_shift_semitones") is double v ? (int)Math.Round(v) : null,
                    VocalTempoRatio = OptNum(g, "vocal_tempo_ratio"),
                    ChordMatch = OptNum(g, "chord_match"),
                    BeatErrorMs = OptNum(g, "beat_error_ms")
                };
                string at = $"{ctx} segment {seg.Index + 1}";
                string kind = MiniJson.Str(g, "kind").Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "full": seg.Kind = MashupSegmentKind.Full; break;
                    case "changeover": seg.Kind = MashupSegmentKind.Changeover; break;
                    case "morph": seg.Kind = MashupSegmentKind.Morph; break;
                    default:
                        c.Problems.Add($"{at}: kind '{kind}'");
                        seg.Kind = MashupSegmentKind.Full;
                        break;
                }
                if (seg.BpmStart <= 0) seg.BpmStart = seg.Bpm;
                if (seg.End <= seg.Start) c.Problems.Add($"{at}: end {seg.End} not after start {seg.Start}");
                if (!double.IsNaN(previousEnd) && Math.Abs(seg.Start - previousEnd) > .05)
                    c.Problems.Add($"{at}: starts at {seg.Start:0.###} s, the segment before ends at {previousEnd:0.###} s");
                previousEnd = seg.End;
                if (seg.Bpm <= 0) c.Problems.Add($"{at}: bpm {seg.Bpm}");
                if (seg.Instrumental.Length == 0 || !byWork.TryGetValue(seg.Instrumental, out seg.InstrumentalSong))
                {
                    c.Problems.Add($"{at}: instrumental '{seg.Instrumental}' is not one of the songs");
                    seg.InstrumentalSong = -1;
                }
                if (seg.Vocal.Length > 0 && !byWork.TryGetValue(seg.Vocal, out seg.VocalSong))
                {
                    c.Problems.Add($"{at}: vocal '{seg.Vocal}' is not one of the songs");
                    seg.VocalSong = -1;
                }
                if (seg.Vocal.Length == 0) seg.VocalSong = -1;
                if (seg.Kind == MashupSegmentKind.Changeover && (seg.VocalSong < 0 || seg.Vocal == seg.Instrumental))
                    c.Problems.Add($"{at}: a changeover needs another song's vocal");
                if (seg.ChordMatch is double cm && (cm < 0 || cm > 1)) c.Problems.Add($"{at}: chord_match {cm}");
                m.Segments.Add(seg);
            }
            if (m.Segments.Count == 0) c.Problems.Add($"{ctx}: no segments");
            else
            {
                if (Math.Abs(m.Segments[0].Start) > .05) c.Problems.Add($"{ctx}: the first segment starts at {m.Segments[0].Start:0.###} s");
                if (m.Seconds > 0 && Math.Abs(m.Segments[^1].End - m.Seconds) > .5)
                    c.Problems.Add($"{ctx}: segments end at {m.Segments[^1].End:0.###} s, the mix is {m.Seconds:0.###} s");
            }

            List<double> times = new(), phrase = new();
            bool order = true, range = true;
            foreach (object? b in MiniJson.Arr(p, "beats"))
            {
                if (b is not List<object?> pair || pair.Count < 2 || pair[0] is not double t || pair[1] is not double pb)
                {
                    c.Problems.Add($"{ctx}: a beat is not [seconds, phrase_beat]");
                    continue;
                }
                if (times.Count > 0 && t <= times[^1]) order = false;
                if (pb < 0 || pb >= m.PhraseBeats + 1e-6) range = false;
                times.Add(t);
                phrase.Add(pb);
            }
            if (!order) c.Problems.Add($"{ctx}: beats are not in time order");
            if (!range) c.Problems.Add($"{ctx}: a phrase_beat is outside [0, {m.PhraseBeats})");
            if (times.Count < 2) c.Problems.Add($"{ctx}: {times.Count} beats");
            m.BeatTimes = times.ToArray();
            m.BeatPhrase = phrase.ToArray();
            return m;
        }

        static MashupSong ReadSong(MashupCatalog c, Mashup m, Dictionary<string, object?> s, string ctx)
        {
            MashupSong song = new()
            {
                Index = m.Songs.Count,
                WorkId = MiniJson.Str(s, "work_id"),
                Title = MiniJson.Str(s, "title"),
                Artist = MiniJson.Str(s, "artist"),
                Year = (int)MiniJson.Num(s, "year", 0),
                Step = (int)MiniJson.Num(s, "step", m.Songs.Count)
            };
            string at = $"{ctx} song {song.WorkId}";
            bool range = true;
            foreach (object? item in MiniJson.Arr(s, "melody"))
            {
                if (item is not List<object?> pair || pair.Count < 2 || pair[0] is not double beat)
                {
                    c.Problems.Add($"{at}: a melody point is not [phrase_beat, pitch]");
                    continue;
                }
                float pitch = pair[1] is double d && !double.IsNaN(d) ? (float)d : float.NaN;
                if (beat < -1e-6 || beat > m.PhraseBeats + 1e-6) range = false;
                if (!float.IsNaN(pitch) && (pitch < 12 || pitch > 120)) range = false;
                song.Melody.Add(new MelodyPoint(beat, pitch));
            }
            if (!range) c.Problems.Add($"{at}: a melody point is outside the phrase or the MIDI range");
            foreach (object? item in MiniJson.Arr(s, "chords"))
            {
                if (item is not List<object?> row || row.Count < 3 || row[0] is not double a || row[1] is not double b || row[2] is not double root)
                {
                    c.Problems.Add($"{at}: a chord is not [start, end, root_pc, quality, roman]");
                    continue;
                }
                MashupChord chord = new()
                {
                    Start = a,
                    End = b,
                    RootPc = (((int)Math.Round(root) % 12) + 12) % 12,
                    Quality = row.Count > 3 ? row[3] as string ?? "" : "",
                    Roman = row.Count > 4 ? row[4] as string ?? "" : ""
                };
                if (b <= a) c.Problems.Add($"{at}: chord {chord.Roman} ends before it starts");
                song.Chords.Add(chord);
            }
            song.Chords.Sort((x, y) => x.Start.CompareTo(y.Start));
            ReadSpans(c, s, "vocal_audible", song.VocalAudible, at);
            ReadSpans(c, s, "instrumental_audible", song.InstrumentalAudible, at);
            return song;
        }

        static void ReadSpans(MashupCatalog c, Dictionary<string, object?> s, string key, List<(double, double)> into, string at)
        {
            foreach (object? item in MiniJson.Arr(s, key))
            {
                if (item is not List<object?> pair || pair.Count < 2 || pair[0] is not double a || pair[1] is not double b || b < a)
                {
                    c.Problems.Add($"{at}: a {key} entry is not [start, end]");
                    continue;
                }
                into.Add((a, b));
            }
        }

        static double? OptNum(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out object? v) && v is double d && !double.IsNaN(d) ? d : null;

        /// <summary>
        /// Maps every song to its graph node (<paramref name="nodeOfWork"/>) and every mashup to the
        /// featured path with its id in <paramref name="paths"/> (each song to that path's step with
        /// the same work_id). Returns the number of mashups that are playable.
        /// </summary>
        public int Bind(Func<string, int?> nodeOfWork, PathCatalog? paths)
        {
            int playable = 0;
            foreach (Mashup m in Mashups)
            {
                FeaturedPath? path = paths?.Find(m.Id);
                m.Path = path;
                bool[] used = new bool[path?.Steps.Count ?? 0];
                foreach (MashupSong s in m.Songs)
                {
                    s.NodeId = nodeOfWork(s.WorkId) ?? 0;
                    s.PathStep = -1;
                    if (path != null)
                        for (int i = 0; i < path.Steps.Count; i++)
                        {
                            if (used[i] || path.Steps[i].WorkId != s.WorkId) continue;
                            used[i] = true;
                            s.PathStep = i;
                            break;
                        }
                }
                if (m.IsPlayable) playable++;
            }
            return playable;
        }

        /// <summary>Why <paramref name="m"/> cannot play (empty when it can).</summary>
        public static string WhyNotPlayable(Mashup m)
        {
            if (m.Path == null) return "no featured path with this id";
            if (m.Segments.Count == 0) return "no segments";
            foreach (MashupSong s in m.Songs)
            {
                if (s.NodeId <= 0) return $"{s.WorkId} is not in the graph";
                if (s.PathStep < 0) return $"{s.WorkId} is not a step of the path";
            }
            foreach (MashupSegment g in m.Segments)
                if (g.InstrumentalSong < 0) return $"segment {g.Index + 1} has an unknown instrumental";
            return "";
        }

        public static string Percent(double? v) => v is double d ? (d * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "–";
    }
}
