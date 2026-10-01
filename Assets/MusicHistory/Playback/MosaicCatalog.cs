#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHistory.Playback
{
    /// <summary>What a stretch of a melody mosaic's mix plays (mosaics.json section "kind").</summary>
    public enum MosaicSectionKind
    {
        /// <summary>The target song: its instrumental and its own vocal.</summary>
        Original,
        /// <summary>The target's instrumental under the pieces' vocals, each shifted and warped onto the target's melody.</summary>
        Mosaic,
        /// <summary>The target's instrumental and vocal with the harmony voices.</summary>
        Harmony
    }

    /// <summary>One sung note: start and end in loop beats, MIDI pitch (normalized frame, as heard). Never lyrics.</summary>
    public readonly struct MosaicNote
    {
        public readonly double Start, End;
        public readonly float Pitch;

        public MosaicNote(double start, double end, float pitch)
        {
            Start = start;
            End = end;
            Pitch = pitch;
        }

        public bool Contains(double beat) => beat >= Start && beat < End;
    }

    /// <summary>One section of a mosaic's mix: whole loops of the target, in seconds of the mix.</summary>
    public sealed class MosaicSection
    {
        public int Index;
        public double Start, End;
        public MosaicSectionKind Kind;
        /// <summary>Whole loops of the target the section lasts.</summary>
        public int Loops;
        /// <summary>The mix's loop number (from 0) the section starts on.</summary>
        public int FirstLoop;

        public double Length => Math.Max(0, End - Start);
        public bool Contains(double t) => t >= Start && t < End;
        public bool HasLoop(int loop) => loop >= FirstLoop && loop < FirstLoop + Loops;

        public static string KindName(MosaicSectionKind kind) => kind switch
        {
            MosaicSectionKind.Mosaic => "mosaic",
            MosaicSectionKind.Harmony => "harmony",
            _ => "original"
        };

        /// <summary>"Original", "Mosaic", "Harmony".</summary>
        public static string Title(MosaicSectionKind kind) => kind switch
        {
            MosaicSectionKind.Mosaic => "Mosaic",
            MosaicSectionKind.Harmony => "Harmony",
            _ => "Original"
        };
    }

    /// <summary>A song of a mosaic: the target, a piece's source or a harmony voice.</summary>
    public sealed class MosaicSong
    {
        public int Index;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        /// <summary>Graph node (0 = not in the graph).</summary>
        public int NodeId;
        public bool IsTarget, IsSource, IsHarmony;
        /// <summary>
        /// The song's colour slot: 0 for the target, then 1, 2, … for the pieces' songs and the harmony
        /// voices in order of first appearance (a distinct colour per song, see MosaicTimeline.Color).
        /// </summary>
        public int ColorSlot => Index;

        /// <summary>"Title, 1996" (the year left out when unknown).</summary>
        public string TitleYear => Year > 0 ? $"{Title}, {Year}" : Title;
    }

    /// <summary>One piece: a run of another song's sung notes covering a span of the target's loop.</summary>
    public sealed class MosaicPiece
    {
        public int Index;
        /// <summary>Index into <see cref="Mosaic.Songs"/>.</summary>
        public int Song = -1;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        /// <summary>The loop beats it covers.</summary>
        public double Start, End;
        /// <summary>Where it was sung in the source's preview (seconds).</summary>
        public double SourceStart, SourceEnd;
        public int ShiftSemitones;
        public double TempoRatio = 1;
        /// <summary>Its note-for-note match rate (matched / the larger of the target's and its own notes), 0..1.</summary>
        public double Match;
        /// <summary>As heard (shifted and warped), in loop beats.</summary>
        public readonly List<MosaicNote> Notes = new();
        /// <summary>The target's notes whose onset lies in the piece's span.</summary>
        public int TargetNotes;

        public double Length => Math.Max(0, End - Start);
        public bool Contains(double beat) => beat >= Start && beat < End;
        /// <summary>The notes the rate is counted over: the larger of the target's (in the span) and the piece's own.</summary>
        public int NotesCompared => Math.Max(TargetNotes, Notes.Count);
        /// <summary>Notes sung note for note (the rate times <see cref="NotesCompared"/>, rounded).</summary>
        public int MatchedNotes => Math.Max(0, Math.Min(NotesCompared, (int)Math.Round(Match * NotesCompared)));
    }

    /// <summary>A harmony voice: another song's melody aligned to sound against the target's.</summary>
    public sealed class MosaicHarmony
    {
        public int Index;
        /// <summary>Index into <see cref="Mosaic.Songs"/>.</summary>
        public int Song = -1;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        public int ShiftSemitones;
        public double TempoRatio = 1;
        /// <summary>Share of its notes consonant with the target's, 0..1.</summary>
        public double Consonance;
        public readonly List<MosaicNote> Notes = new();
    }

    /// <summary>
    /// A melody mosaic (data/audio/mosaics/&lt;id&gt;/mix.mp3, DESIGN.md §17): one loop of a target song's
    /// melody rebuilt piece by piece from other songs' sung melodies (each transposed and time-scaled
    /// onto it), then harmonized by other melodies. The mix plays once: the original loops, the
    /// mosaic loops, the harmony loops, as many whole loops as fit in 90 s.
    ///
    /// Two clocks: seconds of the mix (sections) and the mix beat (the beats counted from the first,
    /// <see cref="BeatAt"/>), which runs over whole loops of <see cref="LoopBeats"/>: loop
    /// k = ⌊beat / LoopBeats⌋, the loop beat the rest. Beats, chords and notes are in loop beats.
    /// </summary>
    public sealed class Mosaic
    {
        public int Index;
        public string Id = "";
        /// <summary>Short Title Case name ("That's All Right, Reassembled"), shown top left while it plays.</summary>
        public string Name = "";
        /// <summary>"&lt;target title&gt; rebuilt from &lt;n&gt; songs" as written.</summary>
        public string Title = "";
        /// <summary>The target song (also <see cref="Songs"/>[0]).</summary>
        public MosaicSong Target = new() { IsTarget = true };
        /// <summary>As written ("&lt;id&gt;/mix.mp3", relative to the mosaics folder).</summary>
        public string File = "";
        public string AbsoluteFile = "";
        public bool FileExists;
        public double Seconds;
        /// <summary>The target's key ("A major").</summary>
        public string Key = "";
        public double Bpm;
        public int BeatsPerBar = 4;
        public double LoopBeats = 16;
        public readonly List<MosaicSection> Sections = new();
        /// <summary>Every beat of the mix: its time, and its loop beat as written (0 ≤ beat &lt; LoopBeats).</summary>
        public double[] BeatTimes = Array.Empty<double>();
        public double[] BeatValues = Array.Empty<double>();
        /// <summary>The target loop's chords (loop beats, normalized frame, roman numerals).</summary>
        public readonly List<MashupChord> Chords = new();
        /// <summary>The target loop's melody.</summary>
        public readonly List<MosaicNote> Notes = new();
        public readonly List<MosaicPiece> Pieces = new();
        public readonly List<MosaicHarmony> Harmonies = new();
        /// <summary>The target, then the pieces' songs and the harmony voices in order of first appearance.</summary>
        public readonly List<MosaicSong> Songs = new();
        /// <summary>Share of the loop's notes inside the pieces' spans / sung note for note by them, 0..1.</summary>
        public double Coverage, Match;

        public double Duration => Seconds > 0 ? Seconds : Sections.Count > 0 ? Sections[^1].End : 0;
        /// <summary>Whole loops in the mix (every section's).</summary>
        public int Loops
        {
            get
            {
                int n = 0;
                foreach (MosaicSection s in Sections) n += Math.Max(0, s.Loops);
                return n;
            }
        }
        public int LoopBars => BeatsPerBar > 0 ? (int)Math.Round(LoopBeats / BeatsPerBar) : 0;
        /// <summary>Beats in the whole mix.</summary>
        public double TotalBeats => Loops * LoopBeats;
        /// <summary>The different songs the pieces come from.</summary>
        public int SourceCount
        {
            get
            {
                int n = 0;
                foreach (MosaicSong s in Songs) if (s.IsSource) n++;
                return n;
            }
        }

        /// <summary>The target is in the graph and the mix has its three sections.</summary>
        public bool IsPlayable => Target.NodeId > 0 && Sections.Count == 3 && Pieces.Count > 0 && LoopBeats > 0;

        /// <summary>"Mosaic: &lt;target title&gt; rebuilt from N songs" (plain text).</summary>
        public string StripTitle => $"Mosaic: {Target.Title} rebuilt from {SourceCount} song{(SourceCount == 1 ? "" : "s")}";

        // ------------------------------------------------------------------ clock

        /// <summary>
        /// The mix beat at mix time <paramref name="t"/>: beats counted from the first (beat i at
        /// <see cref="BeatTimes"/>[i]), interpolated between them, carried on at the first / last
        /// spacing before the first and after the last (so it reaches <see cref="TotalBeats"/> at the end).
        /// </summary>
        public double BeatAt(double t)
        {
            int n = BeatTimes.Length;
            double spb = 60.0 / (Bpm > 0 ? Bpm : 120);
            if (n == 0) return t / spb;
            if (n == 1) return (t - BeatTimes[0]) / spb;
            int i = LastBeatAtOrBefore(t);
            if (i < 0)
            {
                double dt0 = BeatTimes[1] - BeatTimes[0];
                return (t - BeatTimes[0]) / (dt0 > 1e-9 ? dt0 : spb);
            }
            if (i >= n - 1)
            {
                double dt = BeatTimes[n - 1] - BeatTimes[n - 2];
                return n - 1 + (t - BeatTimes[n - 1]) / (dt > 1e-9 ? dt : spb);
            }
            double span = BeatTimes[i + 1] - BeatTimes[i];
            return i + (span > 1e-9 ? (t - BeatTimes[i]) / span : 0);
        }

        /// <summary>The mix time of mix beat <paramref name="beat"/> (the inverse of <see cref="BeatAt"/>), clamped to the mix.</summary>
        public double TimeAtBeat(double beat)
        {
            int n = BeatTimes.Length;
            double spb = 60.0 / (Bpm > 0 ? Bpm : 120), t;
            if (n < 2) t = (n == 1 ? BeatTimes[0] : 0) + beat * spb;
            else if (beat <= 0) t = BeatTimes[0] + beat * (BeatTimes[1] - BeatTimes[0]);
            else if (beat >= n - 1) t = BeatTimes[n - 1] + (beat - (n - 1)) * (BeatTimes[n - 1] - BeatTimes[n - 2]);
            else
            {
                int i = (int)Math.Floor(beat);
                t = BeatTimes[i] + (beat - i) * (BeatTimes[i + 1] - BeatTimes[i]);
            }
            return Math.Max(0, Math.Min(Duration, t));
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

        /// <summary>The loop (0 .. <see cref="Loops"/> − 1) the mix beat <paramref name="beat"/> falls in (clamped).</summary>
        public int LoopOfBeat(double beat)
        {
            int loops = Math.Max(1, Loops);
            double lb = LoopBeats > 0 ? LoopBeats : 16;
            int k = (int)Math.Floor(beat / lb + 1e-9);
            return Math.Max(0, Math.Min(loops - 1, k));
        }

        /// <summary>The loop playing at mix time <paramref name="t"/>.</summary>
        public int LoopAt(double t) => LoopOfBeat(BeatAt(t));

        /// <summary>The loop beat (0 .. <see cref="LoopBeats"/>) at mix time <paramref name="t"/>.</summary>
        public double LoopBeatAt(double t)
        {
            double beat = BeatAt(t);
            return Math.Max(0, Math.Min(LoopBeats, beat - LoopOfBeat(beat) * LoopBeats));
        }

        /// <summary>Where loop <paramref name="loop"/> starts (seconds; clamped to 0 .. <see cref="Loops"/>, the last giving the mix's end).</summary>
        public double LoopStartSeconds(int loop)
        {
            int loops = Loops;
            loop = Math.Max(0, Math.Min(loops, loop));
            if (loop >= loops) return Duration;
            int beat = (int)Math.Round(loop * LoopBeats);
            if (beat < BeatTimes.Length) return BeatTimes[beat];
            return loops > 0 ? Duration * loop / loops : 0;
        }

        /// <summary>The section that holds loop <paramref name="loop"/> (-1 without sections).</summary>
        public int SectionOfLoop(int loop)
        {
            if (Sections.Count == 0) return -1;
            foreach (MosaicSection s in Sections)
                if (s.HasLoop(loop)) return s.Index;
            return loop < 0 ? 0 : Sections.Count - 1;
        }

        /// <summary>The section playing at mix time <paramref name="t"/> (a boundary belongs to the next; -1 without sections).</summary>
        public int SectionIndexAt(double t)
        {
            int n = Sections.Count;
            if (n == 0) return -1;
            int found = 0;
            for (int i = 0; i < n; i++)
                if (Sections[i].Start <= t) found = i;
            return found;
        }

        public MosaicSection? SectionAt(double t)
        {
            int i = SectionIndexAt(t);
            return i >= 0 ? Sections[i] : null;
        }

        /// <summary>The section of the given kind (null when missing).</summary>
        public MosaicSection? SectionOf(MosaicSectionKind kind)
        {
            foreach (MosaicSection s in Sections) if (s.Kind == kind) return s;
            return null;
        }

        /// <summary>The kind of loop <paramref name="loop"/>'s section (Original without sections).</summary>
        public MosaicSectionKind KindOfLoop(int loop)
        {
            int s = SectionOfLoop(loop);
            return s >= 0 ? Sections[s].Kind : MosaicSectionKind.Original;
        }

        /// <summary>The piece sounding at loop beat <paramref name="beat"/> (-1 between pieces).</summary>
        public int PieceIndexAt(double beat)
        {
            for (int i = 0; i < Pieces.Count; i++)
                if (Pieces[i].Contains(beat)) return i;
            return -1;
        }

        /// <summary>The piece sounding at loop beat <paramref name="beat"/>, else the last one before it, else the first (-1 without pieces).</summary>
        public int PieceAtOrBefore(double beat)
        {
            int found = Pieces.Count > 0 ? 0 : -1;
            for (int i = 0; i < Pieces.Count; i++)
                if (Pieces[i].Start <= beat + 1e-9) found = i;
            return found;
        }

        /// <summary>The target loop's chord at loop beat <paramref name="beat"/> (null in a gap).</summary>
        public MashupChord? ChordAt(double beat)
        {
            foreach (MashupChord c in Chords)
                if (beat >= c.Start && beat < c.End) return c;
            return null;
        }

        /// <summary>
        /// The pitch of <paramref name="notes"/> at loop beat <paramref name="beat"/>: <paramref name="voiced"/>
        /// inside a note, else (a rest) the nearest note's pitch within <paramref name="reach"/> beats,
        /// the one before first. False when nothing is sung within reach.
        /// </summary>
        public static bool NoteAt(IReadOnlyList<MosaicNote> notes, double beat, out float pitch, out bool voiced, double reach = 2.0)
        {
            pitch = float.NaN;
            voiced = false;
            double best = double.MaxValue;
            foreach (MosaicNote n in notes)
            {
                if (n.Contains(beat))
                {
                    pitch = n.Pitch;
                    voiced = true;
                    return true;
                }
                double d = beat >= n.End ? beat - n.End : n.Start - beat;
                // Ties go to the note before (it is still ringing in the listener's ear).
                if (d <= reach && (d < best - 1e-9 || (Math.Abs(d - best) <= 1e-9 && beat >= n.End)))
                {
                    best = d;
                    pitch = n.Pitch;
                }
            }
            return !float.IsNaN(pitch);
        }

        /// <summary>Lowest and highest pitch of every note (the target's, the pieces', the harmonies'); false when there are none.</summary>
        public bool PitchRange(out float lo, out float hi)
        {
            lo = float.MaxValue;
            hi = float.MinValue;
            void Add(List<MosaicNote> list, ref float l, ref float h)
            {
                foreach (MosaicNote n in list)
                {
                    l = Math.Min(l, n.Pitch);
                    h = Math.Max(h, n.Pitch);
                }
            }
            Add(Notes, ref lo, ref hi);
            foreach (MosaicPiece p in Pieces) Add(p.Notes, ref lo, ref hi);
            foreach (MosaicHarmony h in Harmonies) Add(h.Notes, ref lo, ref hi);
            return lo <= hi;
        }

        // ------------------------------------------------------------------ words

        /// <summary>"3 semitones down", "1 semitone up", "same key".</summary>
        public static string ShiftText(int semitones)
        {
            if (semitones == 0) return "same key";
            int a = Math.Abs(semitones);
            return $"{a} semitone{(a == 1 ? "" : "s")} {(semitones > 0 ? "up" : "down")}";
        }

        /// <summary>"84% speed" (a tempo ratio of 0.84), "original speed" at 100%.</summary>
        public static string SpeedText(double ratio)
        {
            int pct = (int)Math.Round(ratio * 100);
            return pct == 100 ? "original speed" : pct.ToString(CultureInfo.InvariantCulture) + "% speed";
        }

        /// <summary>"Fireflies, 2009 · 3 semitones down · 84% speed · 83% notes match" (plain text).</summary>
        public static string PieceCaption(MosaicPiece p) =>
            $"{(p.Year > 0 ? $"{p.Title}, {p.Year}" : p.Title)} · {ShiftText(p.ShiftSemitones)} · {SpeedText(p.TempoRatio)} · " +
            $"{MosaicCatalog.Percent(p.Match)} notes match";

        /// <summary>"Diana, 1957 · 3 semitones down · 75% speed · 86% consonant" (plain text).</summary>
        public static string HarmonyCaption(MosaicHarmony h) =>
            $"{(h.Year > 0 ? $"{h.Title}, {h.Year}" : h.Title)} · {ShiftText(h.ShiftSemitones)} · {SpeedText(h.TempoRatio)} · " +
            $"{MosaicCatalog.Percent(h.Consonance)} consonant";
    }

    /// <summary>
    /// The melody mosaics: data/audio/mosaics/mosaics.json (UTF-8, version 1, DESIGN.md §17;
    /// musichistory/mosaic/contract.py is the pipeline's validator): <c>{ version, generated_at, frame,
    /// mosaics: [ { id, name, title, target: { work_id, title, artist, year }, file, seconds, key, bpm,
    /// beats_per_bar, loop_beats, sections: [ { start, end, kind: original|mosaic|harmony, loops } ],
    /// beats: [ [mix seconds, loop beat] ], chords: [ [start, end, root_pc, quality, roman] ],
    /// notes: [ [start, end, pitch] ], pieces: [ { work_id, title, artist, year, start, end,
    /// source_start, source_end, shift_semitones, tempo_ratio, match, notes } ], harmonies: [ {
    /// work_id, title, artist, year, shift_semitones, tempo_ratio, consonance, notes } ], coverage,
    /// match } ] }</c>. Each mix (relative to the mosaics folder) is an MP3 of whole loops; its vocals
    /// are audio only: the file holds no words and neither does the JSON. A missing or unreadable file
    /// gives an empty catalog (<see cref="Status"/> says why); contract violations go to
    /// <see cref="Problems"/> and never throw. Pure C#: no Unity API.
    /// </summary>
    public sealed class MosaicCatalog
    {
        public const string FileName = "mosaics.json";
        public const string CommandLineFlag = "-musicHistoryMosaics";
        public const int ContractVersion = 1;
        /// <summary>The longest mix (seconds): as many whole loops as fit in it.</summary>
        public const double MaxSeconds = 90;
        public const int NameMax = 32;
        public const double MinTempo = .66, MaxTempo = 1.5;
        const double Eps = 1e-3;

        static readonly string[] Kinds = { "original", "mosaic", "harmony" };
        static readonly HashSet<string> Qualities = new(StringComparer.Ordinal) { "maj", "min", "dim", "aug", "sus", "other" };
        static readonly Regex SlugRe = new(@"^[a-z0-9]+(-[a-z0-9]+)*$");
        static readonly Regex KeyRe = new(@"^[A-G][b#]? (major|minor)$");
        static readonly Regex RomanRe = new(@"^[b#]?(I|II|III|IV|V|VI|VII|i|ii|iii|iv|v|vi|vii)(o|\+|sus)?$");
        static readonly string[] DocKeys = { "version", "generated_at", "frame", "mosaics" };
        static readonly string[] MosaicKeys = { "id", "name", "title", "target", "file", "seconds", "key", "bpm", "beats_per_bar", "loop_beats",
                                                "sections", "beats", "chords", "notes", "pieces", "harmonies", "coverage", "match" };
        static readonly string[] TargetKeys = { "work_id", "title", "artist", "year" };
        static readonly string[] SectionKeys = { "start", "end", "kind", "loops" };
        static readonly string[] PieceKeys = { "work_id", "title", "artist", "year", "start", "end", "source_start", "source_end",
                                               "shift_semitones", "tempo_ratio", "match", "notes" };
        static readonly string[] HarmonyKeys = { "work_id", "title", "artist", "year", "shift_semitones", "tempo_ratio", "consonance", "notes" };

        public int Version;
        public string GeneratedAt = "";
        public string Frame = "";
        public readonly List<Mosaic> Mosaics = new();
        public readonly List<string> Problems = new();
        public string SourcePath = "";
        /// <summary>Folder the mix files are relative to (the folder of mosaics.json).</summary>
        public string Dir = "";
        public bool Loaded;
        public string Status = "";

        public static MosaicCatalog Empty(string status) => new() { Status = status };

        public int PlayableCount
        {
            get
            {
                int n = 0;
                foreach (Mosaic m in Mosaics) if (m.IsPlayable) n++;
                return n;
            }
        }

        public Mosaic? Find(string id)
        {
            foreach (Mosaic m in Mosaics) if (m.Id == id) return m;
            return null;
        }

        /// <summary>&lt;data&gt;/audio/mosaics/mosaics.json (see <see cref="PipelinePaths.Data"/>).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(PipelinePaths.Data(), "audio", "mosaics", FileName);

        /// <summary>Reads <paramref name="jsonPath"/>; never throws (a missing file gives an empty catalog).</summary>
        public static MosaicCatalog Load(string jsonPath)
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
            if (!System.IO.File.Exists(full)) return new MosaicCatalog { Status = "missing: " + full, SourcePath = full };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new MosaicCatalog { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        public static MosaicCatalog Parse(string json, string dir, string sourcePath = "")
        {
            MosaicCatalog c = new() { SourcePath = sourcePath, Dir = dir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("mosaics.json is not valid JSON: " + e.Message);
                return c;
            }
            if (root is not Dictionary<string, object?> doc)
            {
                c.Status = "unreadable: the top level is not an object";
                c.Problems.Add(c.Status);
                return c;
            }
            c.Loaded = true;
            Keys(c, doc, DocKeys, "mosaics.json");
            c.Version = (int)MiniJson.Num(doc, "version", 0);
            if (c.Version != ContractVersion) c.Problems.Add($"version {c.Version}, expected {ContractVersion}");
            c.GeneratedAt = MiniJson.Str(doc, "generated_at");
            if (!DateTimeOffset.TryParse(c.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
                c.Problems.Add($"generated_at '{c.GeneratedAt}' is not ISO-8601");
            c.Frame = MiniJson.Str(doc, "frame");
            if (!c.Frame.StartsWith("C major / A minor", StringComparison.Ordinal)) c.Problems.Add($"frame '{c.Frame}' is not the normalized C major / A minor frame");
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(doc, "mosaics"))
            {
                if (item is not Dictionary<string, object?> o)
                {
                    c.Problems.Add("a mosaics entry is not an object");
                    continue;
                }
                Mosaic m = ReadMosaic(c, o, dir);
                if (m.Id.Length > 0 && !ids.Add(m.Id)) c.Problems.Add($"mosaic '{m.Id}': duplicate id");
                c.Mosaics.Add(m);
            }
            c.Status = $"loaded {c.Mosaics.Count} mosaic{(c.Mosaics.Count == 1 ? "" : "s")}";
            return c;
        }

        /// <summary>Exactly <paramref name="want"/>: an extra key is reported too (the contract carries no free text).</summary>
        static void Keys(MosaicCatalog c, Dictionary<string, object?> o, string[] want, string where)
        {
            List<string> missing = new(), extra = new();
            foreach (string k in want) if (!o.ContainsKey(k)) missing.Add(k);
            foreach (string k in o.Keys) if (Array.IndexOf(want, k) < 0) extra.Add(k);
            if (missing.Count > 0 || extra.Count > 0)
                c.Problems.Add($"{where}: keys differ (extra [{string.Join(", ", extra)}], missing [{string.Join(", ", missing)}])");
        }

        static bool IsInt(object? v) => v is double d && !double.IsNaN(d) && Math.Abs(d - Math.Round(d)) < 1e-9;

        static Mosaic ReadMosaic(MosaicCatalog c, Dictionary<string, object?> o, string dir)
        {
            Mosaic m = new()
            {
                Index = c.Mosaics.Count,
                Id = MiniJson.Str(o, "id"),
                Name = MiniJson.Str(o, "name"),
                Title = MiniJson.Str(o, "title"),
                File = MiniJson.Str(o, "file"),
                Seconds = MiniJson.Num(o, "seconds", 0),
                Key = MiniJson.Str(o, "key"),
                Bpm = MiniJson.Num(o, "bpm", 0),
                BeatsPerBar = (int)Math.Round(MiniJson.Num(o, "beats_per_bar", 4)),
                LoopBeats = MiniJson.Num(o, "loop_beats", 0),
                Coverage = MiniJson.Num(o, "coverage", 0),
                Match = MiniJson.Num(o, "match", 0)
            };
            string ctx = $"mosaic '{(m.Id.Length > 0 ? m.Id : "#" + m.Index)}'";
            Keys(c, o, MosaicKeys, ctx);
            if (!SlugRe.IsMatch(m.Id)) c.Problems.Add($"{ctx}: id is not a slug");
            if (m.Name.Length == 0 || m.Name.Length > NameMax || !char.IsUpper(m.Name[0]))
                c.Problems.Add($"{ctx}: name '{m.Name}' must be Title Case, at most {NameMax} characters");
            if (m.Title.Length == 0) c.Problems.Add($"{ctx}: no title");

            // The target (song 0).
            if (o.TryGetValue("target", out object? tv) && tv is Dictionary<string, object?> t)
            {
                Keys(c, t, TargetKeys, ctx + " target");
                m.Target = new MosaicSong
                {
                    Index = 0,
                    IsTarget = true,
                    WorkId = MiniJson.Str(t, "work_id"),
                    Title = MiniJson.Str(t, "title"),
                    Artist = MiniJson.Str(t, "artist"),
                    Year = (int)MiniJson.Num(t, "year", 0)
                };
                if (m.Target.WorkId.Length == 0 || m.Target.Title.Length == 0 || m.Target.Artist.Length == 0 || !IsInt(t.GetValueOrDefault("year")))
                    c.Problems.Add($"{ctx}: the target needs work_id, title, artist and year");
            }
            else c.Problems.Add($"{ctx}: no target");
            m.Songs.Add(m.Target);

            // The file.
            string expected = m.Id + "/mix.mp3";
            if (m.File != expected) c.Problems.Add($"{ctx}: file '{m.File}' must be '{expected}'");
            if (m.File.Length > 0)
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

            // Scalars.
            if (!(m.Seconds > 0 && m.Seconds <= MaxSeconds + Eps)) c.Problems.Add($"{ctx}: seconds {m.Seconds} must be in (0, {MaxSeconds:0}]");
            if (!KeyRe.IsMatch(m.Key) || !KeyText.TryParse(m.Key, out _, out _)) c.Problems.Add($"{ctx}: key '{m.Key}'");
            if (!(m.Bpm >= 20 && m.Bpm <= 400)) c.Problems.Add($"{ctx}: bpm {m.Bpm}");
            if (!IsInt(o.GetValueOrDefault("beats_per_bar")) || m.BeatsPerBar < 2 || m.BeatsPerBar > 12)
            {
                c.Problems.Add($"{ctx}: beats_per_bar {m.BeatsPerBar}");
                m.BeatsPerBar = 4;
            }
            if (!(m.LoopBeats > 0) || Math.Abs(m.LoopBeats / m.BeatsPerBar - Math.Round(m.LoopBeats / m.BeatsPerBar)) > Eps)
            {
                c.Problems.Add($"{ctx}: loop_beats {m.LoopBeats} is not a positive whole number of bars");
                if (!(m.LoopBeats > 0)) m.LoopBeats = 4 * m.BeatsPerBar;
            }
            double lb = m.LoopBeats;

            ReadSections(c, m, o, ctx);
            ReadBeats(c, m, o, ctx);
            ReadChords(c, m, o, ctx);
            ReadNotes(c, MiniJson.Arr(o, "notes"), m.Notes, lb, $"{ctx} notes");
            if (m.Notes.Count == 0) c.Problems.Add($"{ctx}: the target loop has no notes");

            // Pieces (each from another song, sorted, not overlapping).
            Dictionary<string, int> byWork = new(StringComparer.Ordinal);
            if (m.Target.WorkId.Length > 0) byWork[m.Target.WorkId] = 0;
            int SongOf(string work, string title, string artist, int year, bool source, bool harmony)
            {
                if (!byWork.TryGetValue(work, out int i))
                {
                    i = m.Songs.Count;
                    m.Songs.Add(new MosaicSong { Index = i, WorkId = work, Title = title, Artist = artist, Year = year });
                    byWork[work] = i;
                }
                MosaicSong s = m.Songs[i];
                s.IsSource |= source;
                s.IsHarmony |= harmony;
                return i;
            }
            double prevEnd = 0;
            foreach (object? item in MiniJson.Arr(o, "pieces"))
            {
                string at = $"{ctx} piece {m.Pieces.Count + 1}";
                if (item is not Dictionary<string, object?> p)
                {
                    c.Problems.Add($"{at}: not an object");
                    continue;
                }
                Keys(c, p, PieceKeys, at);
                MosaicPiece piece = new()
                {
                    Index = m.Pieces.Count,
                    WorkId = MiniJson.Str(p, "work_id"),
                    Title = MiniJson.Str(p, "title"),
                    Artist = MiniJson.Str(p, "artist"),
                    Year = (int)MiniJson.Num(p, "year", 0),
                    Start = MiniJson.Num(p, "start", 0),
                    End = MiniJson.Num(p, "end", 0),
                    SourceStart = MiniJson.Num(p, "source_start", 0),
                    SourceEnd = MiniJson.Num(p, "source_end", 0),
                    ShiftSemitones = (int)Math.Round(MiniJson.Num(p, "shift_semitones", 0)),
                    TempoRatio = MiniJson.Num(p, "tempo_ratio", 1),
                    Match = MiniJson.Num(p, "match", 0)
                };
                SongFields(c, piece.WorkId, piece.Title, piece.Artist, p, at);
                if (piece.WorkId.Length > 0 && piece.WorkId == m.Target.WorkId) c.Problems.Add($"{at}: a piece must come from another song than the target");
                if (!(piece.Start >= 0 && piece.Start < piece.End && piece.End <= lb + Eps)) c.Problems.Add($"{at}: start/end [{piece.Start}, {piece.End}] outside the loop's {lb} beats");
                else if (piece.Start < prevEnd - Eps) c.Problems.Add($"{at}: overlaps the piece before or is out of order");
                else prevEnd = piece.End;
                if (!(piece.SourceStart >= 0 && piece.SourceStart < piece.SourceEnd)) c.Problems.Add($"{at}: source_start/source_end");
                ShiftTempo(c, p, piece.ShiftSemitones, piece.TempoRatio, at);
                if (!(piece.Match >= 0 && piece.Match <= 1)) c.Problems.Add($"{at}: match {piece.Match}");
                ReadNotes(c, MiniJson.Arr(p, "notes"), piece.Notes, lb, at);
                piece.Song = piece.WorkId.Length > 0 ? SongOf(piece.WorkId, piece.Title, piece.Artist, piece.Year, true, false) : -1;
                foreach (MosaicNote n in m.Notes)
                    if (n.Start >= piece.Start - Eps && n.Start < piece.End - Eps) piece.TargetNotes++;
                m.Pieces.Add(piece);
            }
            if (m.Pieces.Count == 0) c.Problems.Add($"{ctx}: no pieces");

            // Harmonies: one or two other songs.
            HashSet<string> voices = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(o, "harmonies"))
            {
                string at = $"{ctx} harmony {m.Harmonies.Count + 1}";
                if (item is not Dictionary<string, object?> h)
                {
                    c.Problems.Add($"{at}: not an object");
                    continue;
                }
                Keys(c, h, HarmonyKeys, at);
                MosaicHarmony voice = new()
                {
                    Index = m.Harmonies.Count,
                    WorkId = MiniJson.Str(h, "work_id"),
                    Title = MiniJson.Str(h, "title"),
                    Artist = MiniJson.Str(h, "artist"),
                    Year = (int)MiniJson.Num(h, "year", 0),
                    ShiftSemitones = (int)Math.Round(MiniJson.Num(h, "shift_semitones", 0)),
                    TempoRatio = MiniJson.Num(h, "tempo_ratio", 1),
                    Consonance = MiniJson.Num(h, "consonance", 0)
                };
                SongFields(c, voice.WorkId, voice.Title, voice.Artist, h, at);
                if (voice.WorkId == m.Target.WorkId || !voices.Add(voice.WorkId))
                    c.Problems.Add($"{at}: must be another song than the target and the other voice");
                ShiftTempo(c, h, voice.ShiftSemitones, voice.TempoRatio, at);
                if (!(voice.Consonance >= 0 && voice.Consonance <= 1)) c.Problems.Add($"{at}: consonance {voice.Consonance}");
                ReadNotes(c, MiniJson.Arr(h, "notes"), voice.Notes, lb, at);
                voice.Song = voice.WorkId.Length > 0 ? SongOf(voice.WorkId, voice.Title, voice.Artist, voice.Year, false, true) : -1;
                m.Harmonies.Add(voice);
            }
            if (m.Harmonies.Count < 1 || m.Harmonies.Count > 2) c.Problems.Add($"{ctx}: {m.Harmonies.Count} harmonies (one or two)");

            if (!(m.Coverage >= 0 && m.Coverage <= 1 && m.Match >= 0 && m.Match <= 1)) c.Problems.Add($"{ctx}: coverage/match must be in 0..1");
            else if (m.Match > m.Coverage + Eps) c.Problems.Add($"{ctx}: match {m.Match} exceeds coverage {m.Coverage}");
            return m;
        }

        static void SongFields(MosaicCatalog c, string work, string title, string artist, Dictionary<string, object?> o, string at)
        {
            if (work.Length == 0) c.Problems.Add($"{at}: work_id");
            if (title.Length == 0) c.Problems.Add($"{at}: title");
            if (artist.Length == 0) c.Problems.Add($"{at}: artist");
            if (!IsInt(o.GetValueOrDefault("year"))) c.Problems.Add($"{at}: year");
        }

        static void ShiftTempo(MosaicCatalog c, Dictionary<string, object?> o, int shift, double ratio, string at)
        {
            if (!IsInt(o.GetValueOrDefault("shift_semitones")) || shift < -6 || shift > 6) c.Problems.Add($"{at}: shift_semitones {shift} outside -6..6");
            if (!(ratio >= MinTempo - .02 && ratio <= MaxTempo + .02)) c.Problems.Add($"{at}: tempo_ratio {ratio} outside {MinTempo}..{MaxTempo}");
        }

        static void ReadSections(MosaicCatalog c, Mosaic m, Dictionary<string, object?> o, string ctx)
        {
            List<string> kinds = new();
            double prev = 0;
            int loop = 0;
            foreach (object? item in MiniJson.Arr(o, "sections"))
            {
                string at = $"{ctx} section {m.Sections.Count + 1}";
                if (item is not Dictionary<string, object?> s)
                {
                    c.Problems.Add($"{at}: not an object");
                    continue;
                }
                Keys(c, s, SectionKeys, at);
                string kind = MiniJson.Str(s, "kind").Trim().ToLowerInvariant();
                kinds.Add(kind);
                MosaicSection sec = new()
                {
                    Index = m.Sections.Count,
                    Start = MiniJson.Num(s, "start", 0),
                    End = MiniJson.Num(s, "end", 0),
                    Loops = (int)Math.Round(MiniJson.Num(s, "loops", 0)),
                    FirstLoop = loop,
                    Kind = kind switch
                    {
                        "mosaic" => MosaicSectionKind.Mosaic,
                        "harmony" => MosaicSectionKind.Harmony,
                        _ => MosaicSectionKind.Original
                    }
                };
                if (!(sec.Start < sec.End)) c.Problems.Add($"{at}: end {sec.End} not after start {sec.Start}");
                if (Math.Abs(sec.Start - prev) > Eps) c.Problems.Add($"{at}: starts at {sec.Start}, the section before ended at {prev}");
                prev = sec.End;
                if (!IsInt(s.GetValueOrDefault("loops")) || sec.Loops < 1)
                {
                    c.Problems.Add($"{at}: loops must be a positive int");
                    sec.Loops = Math.Max(1, sec.Loops);
                }
                loop += sec.Loops;
                m.Sections.Add(sec);
            }
            if (string.Join(",", kinds) != string.Join(",", Kinds)) c.Problems.Add($"{ctx}: sections are [{string.Join(", ", kinds)}], must be original, mosaic, harmony");
            if (m.Sections.Count == 0) return;
            if (Math.Abs(prev - m.Seconds) > .05) c.Problems.Add($"{ctx}: sections end at {prev}, the mix lasts {m.Seconds}");
            int loops = m.Loops;
            if (loops <= 0 || m.Seconds <= 0) return;
            double loopSeconds = m.Seconds / loops;
            foreach (MosaicSection s in m.Sections)
                if (Math.Abs(s.Length - s.Loops * loopSeconds) > .05) c.Problems.Add($"{ctx} section {s.Index + 1}: not {s.Loops} whole loops");
            if (m.Seconds + loopSeconds <= MaxSeconds - Eps) c.Problems.Add($"{ctx}: another whole loop would fit in {MaxSeconds:0} s");
        }

        static void ReadBeats(MosaicCatalog c, Mosaic m, Dictionary<string, object?> o, string ctx)
        {
            List<double> times = new(), values = new();
            bool ok = true;
            double lb = m.LoopBeats;
            foreach (object? b in MiniJson.Arr(o, "beats"))
            {
                if (b is not List<object?> pair || pair.Count != 2 || pair[0] is not double t || pair[1] is not double beat)
                {
                    if (ok) c.Problems.Add($"{ctx}: beats[{times.Count}] is not [seconds, loop beat]");
                    ok = false;
                    continue;
                }
                int i = times.Count;
                if (ok && ((i > 0 && t <= times[^1]) || t < 0 || (m.Seconds > 0 && t >= m.Seconds) || beat < 0 || beat >= lb || Math.Abs(beat - i % lb) > Eps))
                {
                    c.Problems.Add($"{ctx}: beats[{i}] = [{t}, {beat}] out of order or range");
                    ok = false;
                }
                times.Add(t);
                values.Add(beat);
            }
            if (times.Count < 2) c.Problems.Add($"{ctx}: {times.Count} beats");
            int loops = m.Loops;
            if (loops > 0 && times.Count != (int)Math.Round(loops * lb)) c.Problems.Add($"{ctx}: {times.Count} beats, expected {(int)Math.Round(loops * lb)} ({loops} loops of {lb})");
            m.BeatTimes = times.ToArray();
            m.BeatValues = values.ToArray();
        }

        static void ReadChords(MosaicCatalog c, Mosaic m, Dictionary<string, object?> o, string ctx)
        {
            double prev = 0, lb = m.LoopBeats;
            foreach (object? item in MiniJson.Arr(o, "chords"))
            {
                int ci = m.Chords.Count;
                if (item is not List<object?> row || row.Count != 5 || row[0] is not double a || row[1] is not double b || !IsInt(row[2]) ||
                    row[3] is not string quality || !Qualities.Contains(quality) || row[4] is not string roman || !RomanRe.IsMatch(roman))
                {
                    c.Problems.Add($"{ctx}: chords[{ci}] is not [start, end, root_pc, quality, roman]");
                    continue;
                }
                int root = (int)Math.Round((double)row[2]!);
                if (!(a >= 0 && a < b && b <= lb + Eps && root >= 0 && root <= 11) || a < prev - Eps)
                    c.Problems.Add($"{ctx}: chords[{ci}] [{a}, {b}] {roman} out of range or order");
                prev = b;
                m.Chords.Add(new MashupChord { Start = a, End = b, RootPc = ((root % 12) + 12) % 12, Quality = quality, Roman = roman });
            }
            if (m.Chords.Count == 0) c.Problems.Add($"{ctx}: no chords");
        }

        static void ReadNotes(MosaicCatalog c, List<object?> rows, List<MosaicNote> into, double lb, string at)
        {
            double last = -1;
            foreach (object? item in rows)
            {
                int k = into.Count;
                if (item is not List<object?> n || n.Count != 3 || n[0] is not double a || n[1] is not double b || n[2] is not double p)
                {
                    c.Problems.Add($"{at}: notes[{k}] is not [start, end, pitch]");
                    return;
                }
                if (!(a >= 0 && a < b && b <= lb + Eps && p >= 20 && p <= 110) || a < last - Eps)
                {
                    c.Problems.Add($"{at}: notes[{k}] = [{a}, {b}, {p}] out of range or order");
                    return;
                }
                last = a;
                into.Add(new MosaicNote(a, b, (float)p));
            }
        }

        /// <summary>Maps every song to its graph node (<paramref name="nodeOfWork"/>). Returns the number of mosaics that are playable.</summary>
        public int Bind(Func<string, int?> nodeOfWork)
        {
            int playable = 0;
            foreach (Mosaic m in Mosaics)
            {
                foreach (MosaicSong s in m.Songs) s.NodeId = s.WorkId.Length > 0 ? nodeOfWork(s.WorkId) ?? 0 : 0;
                if (m.IsPlayable) playable++;
            }
            return playable;
        }

        /// <summary>Why <paramref name="m"/> cannot play (empty when it can).</summary>
        public static string WhyNotPlayable(Mosaic m)
        {
            if (m.Target.NodeId <= 0) return $"the target {m.Target.WorkId} is not in the graph";
            if (m.Sections.Count != 3) return "it does not have the original, mosaic and harmony sections";
            if (m.Pieces.Count == 0) return "no pieces";
            if (m.LoopBeats <= 0) return "no loop length";
            return "";
        }

        public static string Percent(double? v) => v is double d ? (d * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "–";
    }
}
