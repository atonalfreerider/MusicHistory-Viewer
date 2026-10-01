#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MusicHistory.Playback
{
    /// <summary>What a stretch of a duet loop plays (duets.json segment "kind").</summary>
    public enum DuetSegmentKind
    {
        /// <summary>Two vocals over the root song's instrumental bed.</summary>
        Duet,
        /// <summary>
        /// A pair boundary: the leaving vocal fades out, the entering one fades in, and the bed is
        /// briefly another song's instrumental (in the root's key and tempo).
        /// </summary>
        Handoff
    }

    /// <summary>One segment of a duet loop (seconds of the loop).</summary>
    public sealed class DuetSegment
    {
        public int Index;
        public double Start, End;
        public DuetSegmentKind Kind;
        /// <summary>work_id of the instrumental heard (the root's bed, or the borrowed one in a handoff).</summary>
        public string Instrumental = "";
        /// <summary>work_ids of the two vocals heard at the segment's end.</summary>
        public string VocalA = "", VocalB = "";
        /// <summary>Handoffs: the vocal fading in / out ("" for a duet segment).</summary>
        public string Entering = "", Leaving = "";
        /// <summary>Bar-level chord agreement of the vocals against the bed, 0..1.</summary>
        public double? ChordMatch;
        /// <summary>Indices into <see cref="DuetLoop.Songs"/> (-1 = unknown or none).</summary>
        public int InstrumentalSong = -1, VocalSongA = -1, VocalSongB = -1, EnteringSong = -1, LeavingSong = -1;
        /// <summary>The pair (0..n-1) this segment belongs to: a handoff belongs to the pair it enters.</summary>
        public int Pair;

        public double Length => Math.Max(0, End - Start);
        public bool Contains(double t) => t >= Start && t < End;
        public bool IsHandoff => Kind == DuetSegmentKind.Handoff;

        public static string KindName(DuetSegmentKind kind) => kind == DuetSegmentKind.Handoff ? "handoff" : "duet";
    }

    /// <summary>One song of a duet loop: its melody (mix beats, as heard) and when its stems sound.</summary>
    public sealed class DuetSong
    {
        public int Index;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        /// <summary>Order in the path as written in duets.json (0 = the root song).</summary>
        public int Step;
        /// <summary>The vocal's transposition into the root's key (semitones) and tempo ratio onto its grid.</summary>
        public double ShiftSemitones;
        public double TempoRatio = 1;
        /// <summary>The featured path's step that plays this song (-1 until bound).</summary>
        public int PathStep = -1;
        /// <summary>Graph node (0 = not in the graph).</summary>
        public int NodeId;
        /// <summary>As heard, in time order: mix beats and MIDI pitch (NaN = unvoiced). Never lyrics.</summary>
        public readonly List<MelodyPoint> Melody = new();
        public readonly List<(double start, double end)> VocalAudible = new();
        public readonly List<(double start, double end)> InstrumentalAudible = new();

        /// <summary>
        /// The melody as a <see cref="MashupSong"/> for the melody graph and pitch lookups: the same
        /// points, plus a copy of the first and last beat around the loop point (so a lookup at the
        /// wrap sees the melody continue). <see cref="MashupSong.Chords"/> stays empty.
        /// </summary>
        public MashupSong Line { get; } = new();

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
    }

    /// <summary>
    /// A featured path's duet loop (data/audio/duets/&lt;id&gt;/loop.mp3, DESIGN.md §16): two sung
    /// melodies at all times over the root song's instrumental, in the root's key and tempo,
    /// handing off pair by pair around the path and looping back to the start. The file is
    /// rendered circularly: its last sample continues into its first.
    /// </summary>
    public sealed class DuetLoop
    {
        public int Index;
        /// <summary>The featured path id (paths.json).</summary>
        public string Id = "";
        public string Title = "";
        /// <summary>As written ("&lt;id&gt;/loop.mp3", relative to the duets folder).</summary>
        public string File = "";
        public string AbsoluteFile = "";
        public bool FileExists;
        public double Seconds;
        public bool Loops = true;
        /// <summary>work_id of the root song S0 (its bed plays throughout).</summary>
        public string Root = "";
        /// <summary>Index into <see cref="Songs"/> of the root song (-1 = unknown).</summary>
        public int RootSong = -1;
        public string Key = "";
        public double Bpm;
        public int BeatsPerBar = 4;
        public double PhraseBeats = 32;
        public readonly List<DuetSegment> Segments = new();
        /// <summary>Every beat of the loop: its time and its mix beat (from 0, increasing).</summary>
        public double[] BeatTimes = Array.Empty<double>();
        public double[] BeatValues = Array.Empty<double>();
        /// <summary>What the bed plays, in mix beats (normalized frame, roman numerals).</summary>
        public readonly List<MashupChord> Chords = new();
        public readonly List<DuetSong> Songs = new();
        /// <summary>The featured path it plays (after <see cref="DuetCatalog.Bind"/>).</summary>
        public FeaturedPath? Path;
        /// <summary>Beats in one cycle (the mix beat at the loop point; see <see cref="ComputeLoopBeats"/>).</summary>
        public double LoopBeats;
        /// <summary>Number of pairs (= songs, one pair per song: S_k + S_(k+1 mod n)).</summary>
        public int Pairs => Songs.Count;

        public double Duration => Seconds > 0 ? Seconds : Segments.Count > 0 ? Segments[^1].End : 0;
        public int LoopBars => BeatsPerBar > 0 ? (int)Math.Round(LoopBeats / BeatsPerBar) : 0;
        /// <summary>Seconds per bar at the loop's tempo.</summary>
        public double BarSeconds => (Bpm > 0 ? 60.0 / Bpm : .5) * Math.Max(1, BeatsPerBar);

        /// <summary>Bound to its featured path, every song in the graph and on the path, with segments.</summary>
        public bool IsPlayable
        {
            get
            {
                if (Path == null || Segments.Count == 0 || Songs.Count < 2 || RootSong < 0) return false;
                foreach (DuetSong s in Songs) if (s.NodeId <= 0 || s.PathStep < 0) return false;
                foreach (DuetSegment g in Segments) if (g.InstrumentalSong < 0 || g.VocalSongA < 0 || g.VocalSongB < 0) return false;
                return true;
            }
        }

        /// <summary><paramref name="t"/> folded into [0, <see cref="Duration"/>).</summary>
        public double Wrap(double t)
        {
            double d = Duration;
            if (d <= 0) return Math.Max(0, t);
            double r = t % d;
            if (r < 0) r += d;
            return r >= d ? 0 : r;
        }

        /// <summary>The segment playing at <paramref name="t"/> (wrapped into the loop; -1 without segments).</summary>
        public int SegmentIndexAt(double t)
        {
            int n = Segments.Count;
            if (n == 0) return -1;
            t = Wrap(t);
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

        public DuetSegment? SegmentAt(double t)
        {
            int i = SegmentIndexAt(t);
            return i >= 0 ? Segments[i] : null;
        }

        /// <summary>The pair playing at <paramref name="t"/> (0 without segments).</summary>
        public int PairAt(double t) => SegmentAt(t)?.Pair ?? 0;

        /// <summary>The songs (indices into <see cref="Songs"/>) singing pair <paramref name="k"/>: S_k and S_(k+1 mod n).</summary>
        public (int a, int b) PairSongs(int k)
        {
            int n = Songs.Count;
            if (n == 0) return (-1, -1);
            k = ((k % n) + n) % n;
            return (SongForStep(k), SongForStep((k + 1) % n));
        }

        int SongForStep(int step)
        {
            foreach (DuetSong s in Songs) if (s.Step == step) return s.Index;
            return step < Songs.Count ? step : -1;
        }

        /// <summary>Where pair <paramref name="k"/> starts: its first segment in the file (its entering handoff when it has one).</summary>
        public double PairStartSeconds(int k)
        {
            int n = Math.Max(1, Pairs);
            k = ((k % n) + n) % n;
            foreach (DuetSegment g in Segments)
                if (g.Pair == k) return g.Start;
            return 0;
        }

        /// <summary>
        /// Mix beat at loop time <paramref name="t"/> (wrapped into the loop), interpolated between
        /// the beats; across the loop point it interpolates from the last beat to the next cycle's
        /// first, so it rises continuously to <see cref="LoopBeats"/> and starts again at 0.
        /// </summary>
        public double BeatAt(double t)
        {
            double period = LoopBeats > 0 ? LoopBeats : 1;
            int n = BeatTimes.Length;
            double bpm = Bpm > 0 ? Bpm : 120;
            if (n == 0) return WrapBeat(t * bpm / 60.0, period);
            t = Wrap(t);
            double d = Duration;
            int i = LastBeatAtOrBefore(t);
            double t0, b0, t1, b1;
            if (i < 0)
            {
                // Before the first beat: from the previous cycle's last beat.
                t0 = BeatTimes[n - 1] - d;
                b0 = BeatValues[n - 1] - period;
                t1 = BeatTimes[0];
                b1 = BeatValues[0];
            }
            else if (i >= n - 1)
            {
                t0 = BeatTimes[n - 1];
                b0 = BeatValues[n - 1];
                t1 = BeatTimes[0] + d;
                b1 = BeatValues[0] + period;
            }
            else
            {
                t0 = BeatTimes[i];
                b0 = BeatValues[i];
                t1 = BeatTimes[i + 1];
                b1 = BeatValues[i + 1];
            }
            if (t1 - t0 <= 1e-9) return WrapBeat(b0, period);
            double u = Math.Max(0, Math.Min(1, (t - t0) / (t1 - t0)));
            return WrapBeat(b0 + u * (b1 - b0), period);
        }

        static double WrapBeat(double v, double p)
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

        /// <summary>
        /// Beats in one cycle: from the first beat round to the first beat of the next cycle (the
        /// last beat interval carried over the loop point), rounded to a whole beat when within 0.1.
        /// </summary>
        public static double ComputeLoopBeats(double[] times, double[] beats, double seconds, double bpm)
        {
            int n = times.Length;
            if (n == 0 || seconds <= 0) return Math.Max(1, seconds * (bpm > 0 ? bpm : 120) / 60.0);
            double dt = n >= 2 ? times[n - 1] - times[n - 2] : 60.0 / (bpm > 0 ? bpm : 120);
            double db = n >= 2 ? beats[n - 1] - beats[n - 2] : 1;
            if (dt <= 1e-6) dt = 60.0 / (bpm > 0 ? bpm : 120);
            if (db <= 1e-6) db = 1;
            double gap = seconds - times[n - 1] + times[0];
            double p = beats[n - 1] - beats[0] + gap / dt * db;
            double r = Math.Round(p);
            return Math.Abs(p - r) < .1 && r > 0 ? r : Math.Max(1e-3, p);
        }

        /// <summary>Songs whose vocal / instrumental is audible at <paramref name="t"/> (indices into <see cref="Songs"/>).</summary>
        public void AudibleAt(double t, List<int> vocals, List<int> instrumentals)
        {
            vocals.Clear();
            instrumentals.Clear();
            t = Wrap(t);
            for (int i = 0; i < Songs.Count; i++)
            {
                if (Songs[i].VocalAt(t)) vocals.Add(i);
                if (Songs[i].InstrumentalAt(t)) instrumentals.Add(i);
            }
        }

        /// <summary>The bed's chord at mix beat <paramref name="beat"/> (null in a gap).</summary>
        public MashupChord? ChordAt(double beat)
        {
            foreach (MashupChord c in Chords)
                if (beat >= c.Start && beat < c.End) return c;
            return null;
        }

        /// <summary>The song the path's step <paramref name="pathStep"/> plays (null when unbound).</summary>
        public DuetSong? SongForPathStep(int pathStep)
        {
            foreach (DuetSong s in Songs) if (s.PathStep == pathStep) return s;
            return null;
        }

        /// <summary>The path step of a song index (-1 when unbound).</summary>
        public int StepOf(int song) => song >= 0 && song < Songs.Count ? Songs[song].PathStep : -1;

        /// <summary>"Duet: A + B over Root" for the segment at <paramref name="t"/> (plain text).</summary>
        public string DuetText(DuetSegment g)
        {
            string Title(int i) => i >= 0 && i < Songs.Count ? Songs[i].Title : "?";
            return $"Duet: {Title(g.VocalSongA)} + {Title(g.VocalSongB)} over {Title(RootSong)}";
        }

        /// <summary>Lowest and highest sung pitch of every song (false when nothing is voiced).</summary>
        public bool PitchRange(out float lo, out float hi)
        {
            lo = float.MaxValue;
            hi = float.MinValue;
            foreach (DuetSong s in Songs)
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
    /// The featured paths' duet loops: data/audio/duets/duets.json (UTF-8, version 1, DESIGN.md §16):
    /// <c>{ version, generated_at, frame, paths: [ { id, title, file, seconds, loops, root, key, bpm,
    /// beats_per_bar, phrase_beats, segments: [ { start, end, kind: duet|handoff, instrumental,
    /// vocals: [w, w], entering, leaving, chord_match } ], beats: [ [t, mix_beat] ],
    /// chords: [ [start_beat, end_beat, root_pc, quality, roman] ], songs: [ { work_id, title, artist,
    /// year, step, shift_semitones, tempo_ratio, melody: [ [mix_beat, pitch|null] ],
    /// vocal_audible: [ [s, e] ], instrumental_audible: [ [s, e] ] } ] } ] }</c>. Each loop file
    /// (relative to the duets folder) is an MP3 rendered circularly. Vocals are audio only: the file
    /// holds no words. A missing or unreadable file gives an empty catalog (<see cref="Status"/> says
    /// why); contract violations go to <see cref="Problems"/> and never throw. Pure C#: no Unity API.
    /// </summary>
    public sealed class DuetCatalog
    {
        public const string FileName = "duets.json";
        public const string CommandLineFlag = "-musicHistoryDuets";
        public const int ContractVersion = 1;

        public int Version;
        public string GeneratedAt = "";
        public string Frame = "";
        public readonly List<DuetLoop> Loops = new();
        public readonly List<string> Problems = new();
        public string SourcePath = "";
        /// <summary>Folder the loop files are relative to (the folder of duets.json).</summary>
        public string Dir = "";
        public bool Loaded;
        public string Status = "";

        public static DuetCatalog Empty(string status) => new() { Status = status };

        public int PlayableCount
        {
            get
            {
                int n = 0;
                foreach (DuetLoop l in Loops) if (l.IsPlayable) n++;
                return n;
            }
        }

        public DuetLoop? Find(string id)
        {
            foreach (DuetLoop l in Loops) if (l.Id == id) return l;
            return null;
        }

        /// <summary>The playable duet loop of <paramref name="path"/> (bound to that very path object), else null.</summary>
        public DuetLoop? For(FeaturedPath? path)
        {
            if (path == null) return null;
            foreach (DuetLoop l in Loops)
                if (ReferenceEquals(l.Path, path) && l.IsPlayable) return l;
            return null;
        }

        /// <summary>&lt;data&gt;/audio/duets/duets.json (see <see cref="PipelinePaths.Data"/>).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(PipelinePaths.Data(), "audio", "duets", FileName);

        /// <summary>Reads <paramref name="jsonPath"/>; never throws (a missing file gives an empty catalog).</summary>
        public static DuetCatalog Load(string jsonPath)
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
            if (!System.IO.File.Exists(full)) return new DuetCatalog { Status = "missing: " + full, SourcePath = full };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new DuetCatalog { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        public static DuetCatalog Parse(string json, string dir, string sourcePath = "")
        {
            DuetCatalog c = new() { SourcePath = sourcePath, Dir = dir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("duets.json is not valid JSON: " + e.Message);
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
                DuetLoop l = ReadLoop(c, p, dir);
                if (l.Id.Length > 0 && !ids.Add(l.Id)) c.Problems.Add($"duet '{l.Id}': duplicate id");
                c.Loops.Add(l);
            }
            c.Status = $"loaded {c.Loops.Count} duet loop{(c.Loops.Count == 1 ? "" : "s")}";
            return c;
        }

        static DuetLoop ReadLoop(DuetCatalog c, Dictionary<string, object?> p, string dir)
        {
            DuetLoop l = new()
            {
                Index = c.Loops.Count,
                Id = MiniJson.Str(p, "id"),
                Title = MiniJson.Str(p, "title"),
                File = MiniJson.Str(p, "file"),
                Seconds = MiniJson.Num(p, "seconds", 0),
                Loops = !p.TryGetValue("loops", out object? lv) || lv is not bool loopsFlag || loopsFlag,
                Root = MiniJson.Str(p, "root"),
                Key = MiniJson.Str(p, "key"),
                Bpm = MiniJson.Num(p, "bpm", 0),
                BeatsPerBar = (int)Math.Round(MiniJson.Num(p, "beats_per_bar", 4)),
                PhraseBeats = MiniJson.Num(p, "phrase_beats", 0)
            };
            string ctx = $"duet '{(l.Id.Length > 0 ? l.Id : "#" + l.Index)}'";
            if (l.Id.Length == 0) c.Problems.Add($"{ctx}: no id");
            if (!p.ContainsKey("loops") || !l.Loops) c.Problems.Add($"{ctx}: loops is not true");
            if (l.BeatsPerBar <= 0)
            {
                c.Problems.Add($"{ctx}: beats_per_bar {l.BeatsPerBar}");
                l.BeatsPerBar = 4;
            }
            if (l.PhraseBeats <= 0)
            {
                c.Problems.Add($"{ctx}: phrase_beats {l.PhraseBeats}");
                l.PhraseBeats = 8 * l.BeatsPerBar;
            }
            if (l.Seconds <= 0) c.Problems.Add($"{ctx}: seconds {l.Seconds}");
            if (l.Bpm <= 0) c.Problems.Add($"{ctx}: bpm {l.Bpm}");
            if (!KeyText.TryParse(l.Key, out _, out _)) c.Problems.Add($"{ctx}: key '{l.Key}'");
            if (l.File.Length == 0) c.Problems.Add($"{ctx}: no file");
            else
            {
                try
                {
                    l.AbsoluteFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, l.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    l.FileExists = System.IO.File.Exists(l.AbsoluteFile);
                }
                catch (Exception e)
                {
                    c.Problems.Add($"{ctx}: bad file '{l.File}' ({e.Message})");
                }
                if (!l.FileExists) c.Problems.Add($"{ctx}: loop missing: {l.File}");
            }

            // Beats first: the loop's length in beats bounds the melodies and chords.
            List<double> times = new(), values = new();
            bool order = true, range = true;
            foreach (object? b in MiniJson.Arr(p, "beats"))
            {
                if (b is not List<object?> pair || pair.Count < 2 || pair[0] is not double t || pair[1] is not double mb)
                {
                    c.Problems.Add($"{ctx}: a beat is not [seconds, mix_beat]");
                    continue;
                }
                if (times.Count > 0 && (t <= times[^1] || mb <= values[^1])) order = false;
                if (t < -1e-6 || (l.Seconds > 0 && t > l.Seconds + 1e-6)) range = false;
                times.Add(t);
                values.Add(mb);
            }
            if (!order) c.Problems.Add($"{ctx}: beats are not increasing in time and beat");
            if (!range) c.Problems.Add($"{ctx}: a beat is outside [0, seconds]");
            if (times.Count < 2) c.Problems.Add($"{ctx}: {times.Count} beats");
            else if (Math.Abs(values[0]) > 1e-6) c.Problems.Add($"{ctx}: mix beats start at {values[0]:0.###}, not 0");
            l.BeatTimes = times.ToArray();
            l.BeatValues = values.ToArray();
            l.LoopBeats = DuetLoop.ComputeLoopBeats(l.BeatTimes, l.BeatValues, l.Seconds, l.Bpm);

            // Songs: segments refer to them by work_id.
            Dictionary<string, int> byWork = new(StringComparer.Ordinal);
            foreach (object? s in MiniJson.Arr(p, "songs"))
            {
                if (s is not Dictionary<string, object?> song)
                {
                    c.Problems.Add($"{ctx}: a song is not an object");
                    continue;
                }
                DuetSong ds = ReadSong(c, l, song, ctx);
                if (ds.WorkId.Length == 0) c.Problems.Add($"{ctx}: a song has no work_id");
                else if (byWork.ContainsKey(ds.WorkId)) c.Problems.Add($"{ctx}: song {ds.WorkId} listed twice");
                else byWork[ds.WorkId] = ds.Index;
                l.Songs.Add(ds);
            }
            if (l.Songs.Count < 2) c.Problems.Add($"{ctx}: {l.Songs.Count} songs (a duet needs at least two)");
            HashSet<int> steps = new();
            foreach (DuetSong s in l.Songs)
                if (!steps.Add(s.Step) || s.Step < 0 || s.Step >= l.Songs.Count)
                    c.Problems.Add($"{ctx}: song {s.WorkId} has step {s.Step} (steps must be 0..{l.Songs.Count - 1}, each once)");
            if (l.Root.Length == 0 || !byWork.TryGetValue(l.Root, out l.RootSong))
            {
                c.Problems.Add($"{ctx}: root '{l.Root}' is not one of the songs");
                l.RootSong = -1;
            }
            else if (l.Songs[l.RootSong].Step != 0) c.Problems.Add($"{ctx}: the root song {l.Root} is not step 0");

            int Song(string work, string at, string what, bool required)
            {
                if (work.Length == 0)
                {
                    if (required) c.Problems.Add($"{at}: no {what}");
                    return -1;
                }
                if (byWork.TryGetValue(work, out int i)) return i;
                c.Problems.Add($"{at}: {what} '{work}' is not one of the songs");
                return -1;
            }

            double previousEnd = double.NaN;
            foreach (object? s in MiniJson.Arr(p, "segments"))
            {
                if (s is not Dictionary<string, object?> g)
                {
                    c.Problems.Add($"{ctx}: a segment is not an object");
                    continue;
                }
                DuetSegment seg = new()
                {
                    Index = l.Segments.Count,
                    Start = MiniJson.Num(g, "start", 0),
                    End = MiniJson.Num(g, "end", 0),
                    Instrumental = MiniJson.Str(g, "instrumental"),
                    Entering = MiniJson.Str(g, "entering"),
                    Leaving = MiniJson.Str(g, "leaving"),
                    ChordMatch = g.TryGetValue("chord_match", out object? cm) && cm is double d && !double.IsNaN(d) ? d : null
                };
                string at = $"{ctx} segment {seg.Index + 1}";
                string kind = MiniJson.Str(g, "kind").Trim().ToLowerInvariant();
                switch (kind)
                {
                    case "duet": seg.Kind = DuetSegmentKind.Duet; break;
                    case "handoff": seg.Kind = DuetSegmentKind.Handoff; break;
                    default:
                        c.Problems.Add($"{at}: kind '{kind}'");
                        seg.Kind = DuetSegmentKind.Duet;
                        break;
                }
                List<object?> vocals = MiniJson.Arr(g, "vocals");
                if (vocals.Count != 2 || vocals[0] is not string va || vocals[1] is not string vb)
                {
                    c.Problems.Add($"{at}: vocals is not [work_id, work_id]");
                }
                else
                {
                    seg.VocalA = va;
                    seg.VocalB = vb;
                    if (va == vb) c.Problems.Add($"{at}: both vocals are {va}");
                }
                seg.InstrumentalSong = Song(seg.Instrumental, at, "instrumental", true);
                seg.VocalSongA = Song(seg.VocalA, at, "vocal", true);
                seg.VocalSongB = Song(seg.VocalB, at, "vocal", true);
                seg.EnteringSong = Song(seg.Entering, at, "entering vocal", seg.Kind == DuetSegmentKind.Handoff);
                seg.LeavingSong = Song(seg.Leaving, at, "leaving vocal", seg.Kind == DuetSegmentKind.Handoff);
                if (seg.Kind == DuetSegmentKind.Duet && (seg.Entering.Length > 0 || seg.Leaving.Length > 0))
                    c.Problems.Add($"{at}: a duet segment has an entering or leaving vocal");
                if (seg.Kind == DuetSegmentKind.Handoff && seg.EnteringSong >= 0 && seg.EnteringSong != seg.VocalSongA && seg.EnteringSong != seg.VocalSongB)
                    c.Problems.Add($"{at}: the entering vocal {seg.Entering} is not one of the vocals at the segment's end");
                if (seg.End <= seg.Start) c.Problems.Add($"{at}: end {seg.End} not after start {seg.Start}");
                if (!double.IsNaN(previousEnd) && Math.Abs(seg.Start - previousEnd) > .05)
                    c.Problems.Add($"{at}: starts at {seg.Start:0.###} s, the segment before ends at {previousEnd:0.###} s");
                previousEnd = seg.End;
                if (seg.ChordMatch is double m && (m < 0 || m > 1)) c.Problems.Add($"{at}: chord_match {m}");
                l.Segments.Add(seg);
            }
            if (l.Segments.Count == 0) c.Problems.Add($"{ctx}: no segments");
            else
            {
                if (Math.Abs(l.Segments[0].Start) > .05) c.Problems.Add($"{ctx}: the first segment starts at {l.Segments[0].Start:0.###} s");
                if (l.Seconds > 0 && Math.Abs(l.Segments[^1].End - l.Seconds) > .5)
                    c.Problems.Add($"{ctx}: segments end at {l.Segments[^1].End:0.###} s, the loop is {l.Seconds:0.###} s");
            }
            AssignPairs(c, l, ctx);

            foreach (object? item in MiniJson.Arr(p, "chords"))
            {
                if (item is not List<object?> row || row.Count < 3 || row[0] is not double a || row[1] is not double b2 || row[2] is not double rootPc)
                {
                    c.Problems.Add($"{ctx}: a chord is not [start, end, root_pc, quality, roman]");
                    continue;
                }
                MashupChord chord = new()
                {
                    Start = a,
                    End = b2,
                    RootPc = (((int)Math.Round(rootPc) % 12) + 12) % 12,
                    Quality = row.Count > 3 ? row[3] as string ?? "" : "",
                    Roman = row.Count > 4 ? row[4] as string ?? "" : ""
                };
                if (b2 <= a) c.Problems.Add($"{ctx}: chord {chord.Roman} ends before it starts");
                if (a < -1e-6 || b2 > l.LoopBeats + .5) c.Problems.Add($"{ctx}: chord {chord.Roman} [{a:0.##}, {b2:0.##}] is outside the loop's {l.LoopBeats:0.##} beats");
                l.Chords.Add(chord);
            }
            l.Chords.Sort((x, y) => x.Start.CompareTo(y.Start));
            foreach (DuetSong s in l.Songs) BuildLine(l, s);
            return l;
        }

        /// <summary>
        /// Pair k = S_k + S_(k+1 mod n). A handoff whose entering vocal is step e opens pair (e - 1)
        /// mod n; a duet segment belongs to the pair the last handoff opened (before the file's
        /// first handoff: the pair before the one it opens). Without handoffs, from the vocals.
        /// </summary>
        static void AssignPairs(DuetCatalog c, DuetLoop l, string ctx)
        {
            int n = l.Songs.Count;
            if (n == 0 || l.Segments.Count == 0) return;
            int Step(int song) => song >= 0 && song < n ? l.Songs[song].Step : -1;
            int Mod(int v) => ((v % n) + n) % n;
            int firstHandoff = l.Segments.FindIndex(g => g.IsHandoff && g.EnteringSong >= 0);
            int current;
            if (firstHandoff >= 0) current = Mod(Step(l.Segments[firstHandoff].EnteringSong) - 2);
            else
            {
                int a = Step(l.Segments[0].VocalSongA), b = Step(l.Segments[0].VocalSongB);
                current = a >= 0 && b >= 0 ? (Mod(b - a) == 1 ? a : Mod(a - b) == 1 ? b : 0) : 0;
            }
            foreach (DuetSegment g in l.Segments)
            {
                if (g.IsHandoff && g.EnteringSong >= 0) current = Mod(Step(g.EnteringSong) - 1);
                g.Pair = current;
                // The vocals at the segment's end must be exactly the pair's two songs.
                (int pa, int pb) = l.PairSongs(current);
                bool match = n == 2
                    ? (g.VocalSongA == pa || g.VocalSongA == pb) && (g.VocalSongB == pa || g.VocalSongB == pb)
                    : (g.VocalSongA == pa && g.VocalSongB == pb) || (g.VocalSongA == pb && g.VocalSongB == pa);
                if (!match && g.VocalSongA >= 0 && g.VocalSongB >= 0)
                    c.Problems.Add($"{ctx} segment {g.Index + 1}: vocals {g.VocalA} + {g.VocalB} are not pair {current + 1} (steps {current} and {(current + 1) % n})");
            }
        }

        static DuetSong ReadSong(DuetCatalog c, DuetLoop l, Dictionary<string, object?> s, string ctx)
        {
            DuetSong song = new()
            {
                Index = l.Songs.Count,
                WorkId = MiniJson.Str(s, "work_id"),
                Title = MiniJson.Str(s, "title"),
                Artist = MiniJson.Str(s, "artist"),
                Year = (int)MiniJson.Num(s, "year", 0),
                Step = (int)MiniJson.Num(s, "step", l.Songs.Count),
                ShiftSemitones = MiniJson.Num(s, "shift_semitones", 0),
                TempoRatio = MiniJson.Num(s, "tempo_ratio", 1)
            };
            string at = $"{ctx} song {song.WorkId}";
            bool range = true;
            double last = double.NegativeInfinity;
            bool ordered = true;
            foreach (object? item in MiniJson.Arr(s, "melody"))
            {
                if (item is not List<object?> pair || pair.Count < 2 || pair[0] is not double beat)
                {
                    c.Problems.Add($"{at}: a melody point is not [mix_beat, pitch]");
                    continue;
                }
                float pitch = pair[1] is double d && !double.IsNaN(d) ? (float)d : float.NaN;
                if (beat < -1e-6 || beat > l.LoopBeats + 1e-3) range = false;
                if (!float.IsNaN(pitch) && (pitch < 12 || pitch > 120)) range = false;
                if (beat < last - 1e-9) ordered = false;
                last = beat;
                song.Melody.Add(new MelodyPoint(beat, pitch));
            }
            if (!range) c.Problems.Add($"{at}: a melody point is outside the loop's beats or the MIDI range");
            if (!ordered) c.Problems.Add($"{at}: melody beats are not in time order");
            if (song.TempoRatio <= 0) c.Problems.Add($"{at}: tempo_ratio {song.TempoRatio}");
            ReadSpans(c, l, s, "vocal_audible", song.VocalAudible, at);
            ReadSpans(c, l, s, "instrumental_audible", song.InstrumentalAudible, at);
            return song;
        }

        static void ReadSpans(DuetCatalog c, DuetLoop l, Dictionary<string, object?> s, string key, List<(double, double)> into, string at)
        {
            foreach (object? item in MiniJson.Arr(s, key))
            {
                if (item is not List<object?> pair || pair.Count < 2 || pair[0] is not double a || pair[1] is not double b || b < a)
                {
                    c.Problems.Add($"{at}: a {key} entry is not [start, end]");
                    continue;
                }
                if (a < -.05 || (l.Seconds > 0 && b > l.Seconds + .05)) c.Problems.Add($"{at}: {key} [{a:0.##}, {b:0.##}] is outside the loop");
                into.Add((a, b));
            }
        }

        /// <summary>The melody graph's view of a song: its points, plus the loop point bridged both ways.</summary>
        static void BuildLine(DuetLoop l, DuetSong s)
        {
            MashupSong line = s.Line;
            line.Index = s.Index;
            line.WorkId = s.WorkId;
            line.Title = s.Title;
            line.Artist = s.Artist;
            line.Year = s.Year;
            line.Step = s.Step;
            line.Melody.Clear();
            double p = l.LoopBeats;
            const double bridge = 2.0;
            // The end of the loop before its start, and its start after its end (a lookup at the
            // wrap sees the melody continue).
            foreach (MelodyPoint m in s.Melody)
                if (m.Beat > p - bridge) line.Melody.Add(new MelodyPoint(m.Beat - p, m.Pitch));
            line.Melody.AddRange(s.Melody);
            foreach (MelodyPoint m in s.Melody)
                if (m.Beat < bridge) line.Melody.Add(new MelodyPoint(m.Beat + p, m.Pitch));
            line.VocalAudible.AddRange(s.VocalAudible);
            line.InstrumentalAudible.AddRange(s.InstrumentalAudible);
        }

        /// <summary>
        /// Maps every song to its graph node (<paramref name="nodeOfWork"/>) and every loop to the
        /// featured path with its id in <paramref name="paths"/> (each song to that path's step with
        /// the same work_id). Returns the number of loops that are playable.
        /// </summary>
        public int Bind(Func<string, int?> nodeOfWork, PathCatalog? paths)
        {
            int playable = 0;
            foreach (DuetLoop l in Loops)
            {
                FeaturedPath? path = paths?.Find(l.Id);
                l.Path = path;
                bool[] used = new bool[path?.Steps.Count ?? 0];
                foreach (DuetSong s in l.Songs)
                {
                    s.NodeId = nodeOfWork(s.WorkId) ?? 0;
                    s.Line.NodeId = s.NodeId;
                    s.PathStep = -1;
                    if (path != null)
                        for (int i = 0; i < path.Steps.Count; i++)
                        {
                            if (used[i] || path.Steps[i].WorkId != s.WorkId) continue;
                            used[i] = true;
                            s.PathStep = i;
                            break;
                        }
                    s.Line.PathStep = s.PathStep;
                }
                if (l.IsPlayable) playable++;
            }
            return playable;
        }

        /// <summary>Why <paramref name="l"/> cannot play (empty when it can).</summary>
        public static string WhyNotPlayable(DuetLoop l)
        {
            if (l.Path == null) return "no featured path with this id";
            if (l.Segments.Count == 0) return "no segments";
            if (l.Songs.Count < 2) return "fewer than two songs";
            if (l.RootSong < 0) return "the root song is not one of the songs";
            foreach (DuetSong s in l.Songs)
            {
                if (s.NodeId <= 0) return $"{s.WorkId} is not in the graph";
                if (s.PathStep < 0) return $"{s.WorkId} is not a step of the path";
            }
            foreach (DuetSegment g in l.Segments)
                if (g.InstrumentalSong < 0 || g.VocalSongA < 0 || g.VocalSongB < 0) return $"segment {g.Index + 1} names an unknown song";
            return "";
        }

        /// <summary>
        /// Contract checks that need the whole loop: two vocals audible at every sampled moment
        /// (every <paramref name="step"/> seconds), the root's instrumental audible outside handoffs.
        /// Returns the problems found (empty when it holds).
        /// </summary>
        public static List<string> CheckCoverage(DuetLoop l, double step = .25)
        {
            List<string> problems = new();
            if (l.Duration <= 0) return problems;
            List<int> vocals = new(), instrumentals = new();
            for (double t = step * .5; t < l.Duration; t += step)
            {
                l.AudibleAt(t, vocals, instrumentals);
                if (vocals.Count < 2)
                {
                    problems.Add($"{l.Id}: {vocals.Count} vocal{(vocals.Count == 1 ? "" : "s")} audible at {t:0.00} s");
                    if (problems.Count >= 8) break;
                }
                DuetSegment? g = l.SegmentAt(t);
                if (g != null && !g.IsHandoff && l.RootSong >= 0 && !instrumentals.Contains(l.RootSong))
                {
                    problems.Add($"{l.Id}: the root's instrumental is not audible at {t:0.00} s (a duet segment)");
                    if (problems.Count >= 8) break;
                }
            }
            return problems;
        }

        public static string Percent(double? v) => v is double d ? (d * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "–";
    }
}
