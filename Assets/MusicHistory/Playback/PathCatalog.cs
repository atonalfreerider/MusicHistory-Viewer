#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MusicHistory.Playback
{
    /// <summary>How a path step is reached from the step before it (paths.json "via").</summary>
    public sealed class PathVia
    {
        /// <summary>The shared identity: the evidence label of the graph edge between the two songs.</summary>
        public string Identity = "";
        public bool Strong;
        public double Z;
        /// <summary>"tree" or "secondary".</summary>
        public string EdgeKind = "";
        public int FamilySize;
    }

    /// <summary>
    /// One song of a featured path and its prerendered recording preview (paths.json v2, see
    /// <see cref="PathCatalog"/>). The file starts in <see cref="StartKey"/> / <see cref="StartBpm"/>
    /// (the previous step as heard) and glides to this recording's own key and tempo over
    /// <see cref="MorphSeconds"/> of output time (smoothstep), then plays natively.
    /// </summary>
    public sealed class PathStep
    {
        /// <summary>0-based position in the path.</summary>
        public int Index;
        public string WorkId = "";
        public string Title = "";
        public string Artist = "";
        public int Year;
        /// <summary>As written in paths.json, relative to the renders folder.</summary>
        public string File = "";
        /// <summary>Resolved against the renders folder.</summary>
        public string AbsoluteFile = "";
        public bool FileExists;
        public double Seconds;
        /// <summary>Null for the first step (it plays natively).</summary>
        public PathVia? Via;
        public string StartKey = "";
        public string Key = "";
        public int StartSemitones;
        public double StartBpm;
        public double Bpm;
        public double MorphSeconds;
        /// <summary>"audio" (measured from the recording) or "midi".</summary>
        public string KeySource = "";
        public string BpmSource = "";
        /// <summary>Graph node of <see cref="WorkId"/> after <see cref="PathCatalog.Bind"/> (0 = not in the graph).</summary>
        public int NodeId;
        public FeaturedPath Path = null!;

        /// <summary>The render glides (it has a previous step and a glide length).</summary>
        public bool Glides => Via != null && MorphSeconds > 0;
        /// <summary>BPM the file starts at (the previous step's tempo as heard; this recording's own for the first step).</summary>
        public double EntryBpm => Glides && StartBpm > 0 ? StartBpm : Bpm;
        public int EntrySemitones => Glides ? StartSemitones : 0;

        /// <summary>Smoothstep glide progress 0..1 at <paramref name="seconds"/> into the file (1 = native).</summary>
        public double GlideProgress(double seconds)
        {
            if (!Glides) return 1;
            double u = Math.Max(0, Math.Min(1, seconds / MorphSeconds));
            return u * u * (3 - 2 * u);
        }

        /// <summary>Transposition against the recording's own key, in semitones, at <paramref name="seconds"/>.</summary>
        public double SemitonesAt(double seconds) => EntrySemitones * (1 - GlideProgress(seconds));

        /// <summary>Tempo heard at <paramref name="seconds"/>: from <see cref="EntryBpm"/> to <see cref="Bpm"/>.</summary>
        public double BpmAt(double seconds)
        {
            double s = GlideProgress(seconds);
            return EntryBpm + (Bpm - EntryBpm) * s;
        }

        /// <summary>Beats of the recording heard by <paramref name="seconds"/>: ∫ BpmAt/60 dt (closed form of the smoothstep glide).</summary>
        public double BeatsAt(double seconds)
        {
            double t = Math.Max(0, seconds);
            if (!Glides) return t * Bpm / 60.0;
            double m = MorphSeconds, b0 = EntryBpm, b1 = Bpm;
            double inGlide = Math.Min(t, m), u = inGlide / m;
            // ∫0^u (3v² - 2v³) dv = u³ - u⁴/2
            double beats = (b0 * inGlide + (b1 - b0) * m * (u * u * u - u * u * u * u / 2)) / 60.0;
            if (t > m) beats += b1 * (t - m) / 60.0;
            return beats;
        }
    }

    /// <summary>A curated walk through the graph with recording previews (one paths.json entry).</summary>
    public sealed class FeaturedPath
    {
        public int Index;
        public string Id = "";
        public string Title = "";
        public string Subtitle = "";
        /// <summary>What connects the songs (musical identity only).</summary>
        public string Description = "";
        /// <summary>The main shared identity.</summary>
        public string Identity = "";
        public double Seconds;
        public readonly List<PathStep> Steps = new();

        public int FirstYear
        {
            get
            {
                int y = int.MaxValue;
                foreach (PathStep s in Steps) if (s.Year > 0) y = Math.Min(y, s.Year);
                return y == int.MaxValue ? 0 : y;
            }
        }

        public int LastYear
        {
            get
            {
                int y = 0;
                foreach (PathStep s in Steps) y = Math.Max(y, s.Year);
                return y;
            }
        }

        /// <summary>Every step maps to a song of the graph (after <see cref="PathCatalog.Bind"/>).</summary>
        public bool IsPlayable
        {
            get
            {
                if (Steps.Count == 0) return false;
                foreach (PathStep s in Steps) if (s.NodeId <= 0) return false;
                return true;
            }
        }

        public int MissingRenders
        {
            get
            {
                int n = 0;
                foreach (PathStep s in Steps) if (!s.FileExists) n++;
                return n;
            }
        }

        public int StrongLinks
        {
            get
            {
                int n = 0;
                foreach (PathStep s in Steps) if (s.Via != null && s.Via.Strong) n++;
                return n;
            }
        }
    }

    /// <summary>
    /// The featured paths: data/audio/renders/paths.json (UTF-8, version 2):
    /// <c>{ version, generated_at, morph_bars, paths: [ { id, title, subtitle, description, identity,
    /// seconds, steps: [ { work_id, title, artist, year, file, seconds, via, start_key, key,
    /// start_semitones, start_bpm, bpm, morph_seconds, key_source, bpm_source } ] } ] }</c>.
    /// Each step's file (relative to the renders folder) is a 44.1 kHz MP3 that starts in the
    /// previous step's key and tempo as heard and glides to its own. A missing or unreadable file
    /// gives an empty catalog (<see cref="Status"/> says why); contract violations are listed in
    /// <see cref="Problems"/> and never throw. Pure C#: no Unity API.
    /// </summary>
    public sealed class PathCatalog
    {
        public const string FileName = "paths.json";
        public const string CommandLineFlag = "-musicHistoryPaths";
        public const int ContractVersion = 2;

        public int Version;
        public string GeneratedAt = "";
        public double MorphBars = 2;
        public readonly List<FeaturedPath> Paths = new();
        public readonly List<string> Problems = new();
        /// <summary>The paths.json read ("" for an in-memory catalog).</summary>
        public string SourcePath = "";
        /// <summary>Folder the step files are relative to (the folder of paths.json).</summary>
        public string RendersDir = "";
        /// <summary>The file was found and parsed.</summary>
        public bool Loaded;
        /// <summary>"loaded 5 paths", "missing: …", "unreadable: …".</summary>
        public string Status = "";

        public int PlayableCount
        {
            get
            {
                int n = 0;
                foreach (FeaturedPath p in Paths) if (p.IsPlayable) n++;
                return n;
            }
        }

        public static PathCatalog Empty(string status) => new() { Status = status };

        /// <summary>Reads <paramref name="jsonPath"/>; never throws (a missing file gives an empty catalog).</summary>
        public static PathCatalog Load(string jsonPath)
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
            if (!System.IO.File.Exists(full)) return new PathCatalog { Status = "missing: " + full, SourcePath = full };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new PathCatalog { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        /// <summary>Parses paths.json text; step files resolve against <paramref name="rendersDir"/>.</summary>
        public static PathCatalog Parse(string json, string rendersDir, string sourcePath = "")
        {
            PathCatalog c = new() { SourcePath = sourcePath, RendersDir = rendersDir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("paths.json is not valid JSON: " + e.Message);
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
            c.MorphBars = MiniJson.Num(doc, "morph_bars", 2);
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(doc, "paths"))
            {
                if (item is not Dictionary<string, object?> p)
                {
                    c.Problems.Add("a paths entry is not an object");
                    continue;
                }
                FeaturedPath path = new()
                {
                    Index = c.Paths.Count,
                    Id = MiniJson.Str(p, "id"),
                    Title = MiniJson.Str(p, "title"),
                    Subtitle = MiniJson.Str(p, "subtitle"),
                    Description = MiniJson.Str(p, "description"),
                    Identity = MiniJson.Str(p, "identity"),
                    Seconds = MiniJson.Num(p, "seconds", 0)
                };
                string ctx = $"path '{(path.Id.Length > 0 ? path.Id : "#" + path.Index)}'";
                if (path.Id.Length == 0) c.Problems.Add($"{ctx}: no id");
                else if (!ids.Add(path.Id)) c.Problems.Add($"{ctx}: duplicate id");
                if (path.Title.Length == 0) c.Problems.Add($"{ctx}: no title");
                foreach (object? s in MiniJson.Arr(p, "steps"))
                {
                    if (s is not Dictionary<string, object?> step)
                    {
                        c.Problems.Add($"{ctx}: a step is not an object");
                        continue;
                    }
                    path.Steps.Add(ReadStep(c, path, step, rendersDir, ctx));
                }
                if (path.Steps.Count == 0) c.Problems.Add($"{ctx}: no steps");
                double sum = 0;
                foreach (PathStep s in path.Steps) sum += s.Seconds;
                if (path.Steps.Count > 0 && Math.Abs(sum - path.Seconds) > 1.0)
                    c.Problems.Add($"{ctx}: seconds {path.Seconds:0.##} but the steps add up to {sum:0.##}");
                c.Paths.Add(path);
            }
            c.Status = $"loaded {c.Paths.Count} path{(c.Paths.Count == 1 ? "" : "s")}";
            return c;
        }

        static PathStep ReadStep(PathCatalog c, FeaturedPath path, Dictionary<string, object?> s, string rendersDir, string ctx)
        {
            PathStep step = new()
            {
                Index = path.Steps.Count,
                Path = path,
                WorkId = MiniJson.Str(s, "work_id"),
                Title = MiniJson.Str(s, "title"),
                Artist = MiniJson.Str(s, "artist"),
                Year = (int)MiniJson.Num(s, "year", 0),
                File = MiniJson.Str(s, "file"),
                Seconds = MiniJson.Num(s, "seconds", 0),
                StartKey = MiniJson.Str(s, "start_key"),
                Key = MiniJson.Str(s, "key"),
                StartSemitones = (int)Math.Round(MiniJson.Num(s, "start_semitones", 0)),
                StartBpm = MiniJson.Num(s, "start_bpm", 0),
                Bpm = MiniJson.Num(s, "bpm", 0),
                MorphSeconds = MiniJson.Num(s, "morph_seconds", 0),
                KeySource = MiniJson.Str(s, "key_source"),
                BpmSource = MiniJson.Str(s, "bpm_source")
            };
            string at = $"{ctx} step {step.Index + 1}";
            if (s.TryGetValue("via", out object? v) && v is Dictionary<string, object?> via)
            {
                step.Via = new PathVia
                {
                    Identity = MiniJson.Str(via, "identity"),
                    Strong = MiniJson.Bool(via, "strong"),
                    Z = MiniJson.Num(via, "z", 0),
                    EdgeKind = MiniJson.Str(via, "edge_kind"),
                    FamilySize = (int)MiniJson.Num(via, "family_size", 0)
                };
            }
            if (step.WorkId.Length == 0) c.Problems.Add($"{at}: no work_id");
            if (step.Index == 0 && step.Via != null) c.Problems.Add($"{at}: the first step has a via");
            if (step.Index > 0 && step.Via == null) c.Problems.Add($"{at}: no via");
            if (step.StartSemitones < -6 || step.StartSemitones > 5) c.Problems.Add($"{at}: start_semitones {step.StartSemitones} outside [-6, 5]");
            if (step.Seconds <= 0) c.Problems.Add($"{at}: seconds {step.Seconds}");
            if (step.Bpm <= 0) c.Problems.Add($"{at}: bpm {step.Bpm}");
            if (step.MorphSeconds < 0) c.Problems.Add($"{at}: morph_seconds {step.MorphSeconds}");
            if (step.File.Length == 0)
            {
                c.Problems.Add($"{at}: no file");
            }
            else
            {
                try
                {
                    step.AbsoluteFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(rendersDir, step.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    step.FileExists = System.IO.File.Exists(step.AbsoluteFile);
                }
                catch (Exception e)
                {
                    c.Problems.Add($"{at}: bad file '{step.File}' ({e.Message})");
                }
                if (!step.FileExists) c.Problems.Add($"{at}: render missing: {step.File}");
            }
            return step;
        }

        /// <summary>
        /// Maps every step's work_id to a graph node (<paramref name="nodeOfWork"/> returns null when
        /// the song is not in the graph). A path with an unmapped step is not playable.
        /// </summary>
        public int Bind(Func<string, int?> nodeOfWork)
        {
            int unmapped = 0;
            foreach (FeaturedPath p in Paths)
                foreach (PathStep s in p.Steps)
                {
                    s.NodeId = nodeOfWork(s.WorkId) ?? 0;
                    if (s.NodeId > 0) continue;
                    unmapped++;
                    Problems.Add($"path '{p.Id}' step {s.Index + 1}: {s.WorkId} ('{s.Title}') is not in the graph");
                }
            return unmapped;
        }

        public FeaturedPath? Find(string id)
        {
            foreach (FeaturedPath p in Paths) if (p.Id == id) return p;
            return null;
        }

        /// <summary>A step that plays <paramref name="nodeId"/> right after <paramref name="previousNodeId"/> (0 = the path's first step).</summary>
        public PathStep? FindStep(int previousNodeId, int nodeId)
        {
            foreach (FeaturedPath p in Paths)
                for (int i = 0; i < p.Steps.Count; i++)
                {
                    if (p.Steps[i].NodeId != nodeId) continue;
                    int prev = i > 0 ? p.Steps[i - 1].NodeId : 0;
                    if (prev == previousNodeId) return p.Steps[i];
                }
            return null;
        }

        /// <summary>"2:04".</summary>
        public static string Clock(double seconds)
        {
            int s = (int)Math.Floor(Math.Max(0, seconds) + 1e-6);
            return $"{s / 60}:{s % 60:00}";
        }
    }

    /// <summary>Parses key names such as "C major", "F# minor", "Bb major", "E♭ minor".</summary>
    public static class KeyText
    {
        public static bool TryParse(string? text, out int tonicPc, out bool minor)
        {
            tonicPc = 0;
            minor = false;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text!.Trim();
            int pc;
            switch (char.ToUpperInvariant(t[0]))
            {
                case 'C': pc = 0; break;
                case 'D': pc = 2; break;
                case 'E': pc = 4; break;
                case 'F': pc = 5; break;
                case 'G': pc = 7; break;
                case 'A': pc = 9; break;
                case 'B': pc = 11; break;
                default: return false;
            }
            int i = 1;
            while (i < t.Length)
            {
                char ch = t[i];
                if (ch == '#' || ch == '♯') pc++;
                else if (ch == 'b' || ch == '♭') pc--;
                else break;
                i++;
            }
            string rest = t.Substring(i).Trim().ToLowerInvariant();
            if (rest.StartsWith("min") || rest == "m") minor = true;
            else if (rest.Length == 0 || rest.StartsWith("maj")) minor = false;
            else return false;
            tonicPc = ((pc % 12) + 12) % 12;
            return true;
        }
    }

    /// <summary>
    /// A small JSON reader: objects become Dictionary&lt;string, object?&gt;, arrays List&lt;object?&gt;,
    /// numbers double, plus string, bool and null. Throws FormatException on malformed input.
    /// </summary>
    public static class MiniJson
    {
        public static object? Parse(string json)
        {
            int i = 0;
            if (json.Length > 0 && json[0] == '﻿') i = 1;
            object? value = Value(json, ref i);
            Skip(json, ref i);
            if (i != json.Length) throw new FormatException($"unexpected '{json[i]}' at {i}");
            return value;
        }

        public static string Str(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out object? v) && v != null ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

        public static double Num(Dictionary<string, object?> o, string key, double fallback) =>
            o.TryGetValue(key, out object? v) && v is double d && !double.IsNaN(d) ? d : fallback;

        public static bool Bool(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out object? v) && v is bool b && b;

        public static List<object?> Arr(Dictionary<string, object?> o, string key) =>
            o.TryGetValue(key, out object? v) && v is List<object?> list ? list : new List<object?>();

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object? Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{': return Obj(s, ref i);
                case '[': return List(s, ref i);
                case '"': return String(s, ref i);
                case 't': Expect(s, ref i, "true"); return true;
                case 'f': Expect(s, ref i, "false"); return false;
                case 'n': Expect(s, ref i, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return Number(s, ref i);
                    throw new FormatException($"unexpected '{c}' at {i}");
            }
        }

        static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException($"expected {word} at {i}");
            i += word.Length;
        }

        static Dictionary<string, object?> Obj(string s, ref int i)
        {
            Dictionary<string, object?> o = new(StringComparer.Ordinal);
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == '}')
            {
                i++;
                return o;
            }
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"expected a key at {i}");
                string key = String(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at {i}");
                i++;
                o[key] = Value(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException($"expected ',' or '}}' at {i}");
            }
        }

        static List<object?> List(string s, ref int i)
        {
            List<object?> list = new();
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']')
            {
                i++;
                return list;
            }
            while (true)
            {
                list.Add(Value(s, ref i));
                Skip(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException($"expected ',' or ']' at {i}");
            }
        }

        static string String(string s, ref int i)
        {
            StringBuilder b = new();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return b.ToString();
                if (c != '\\')
                {
                    b.Append(c);
                    continue;
                }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': b.Append('"'); break;
                    case '\\': b.Append('\\'); break;
                    case '/': b.Append('/'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"bad escape \\{e}");
                }
            }
            throw new FormatException("unterminated string");
        }

        static double Number(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            string token = s.Substring(start, i - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new FormatException($"bad number '{token}' at {start}");
            return d;
        }
    }
}
