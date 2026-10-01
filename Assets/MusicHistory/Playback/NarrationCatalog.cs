#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MusicHistory.Playback
{
    /// <summary>A source a narration cue cites (title and URL; shown nowhere, kept for reports).</summary>
    public sealed class NarrationSource
    {
        public string Title = "";
        public string Url = "";
    }

    /// <summary>
    /// One spoken line of a narrated walkthrough (narration.json, DESIGN.md §15): it starts at
    /// <see cref="At"/> seconds of the path's mashup mix and lasts <see cref="Seconds"/>; while it
    /// speaks the music is ducked to <see cref="DuckDb"/>. The text is our own narration (facts,
    /// paraphrased), never lyrics.
    /// </summary>
    public sealed class NarrationCue
    {
        /// <summary>Position in the path's cue list (sorted by <see cref="At"/>).</summary>
        public int Index;
        public string Id = "";
        /// <summary>Mix seconds the line starts at.</summary>
        public double At;
        /// <summary>Length of the spoken line in seconds.</summary>
        public double Seconds;
        /// <summary>As written in narration.json ("&lt;path id&gt;/&lt;nn&gt;_&lt;cue id&gt;.wav"), relative to its folder.</summary>
        public string File = "";
        public string AbsoluteFile = "";
        public bool FileExists;
        public string Text = "";
        /// <summary>Music gain under the cue in dB (≤ 0).</summary>
        public double DuckDb = NarrationCatalog.DefaultDuckDb;
        /// <summary>Artist image id (data/images/artists.json), "" for none.</summary>
        public string Image = "";
        /// <summary>"intro", "song", "changeover", "outro" when given ("" otherwise).</summary>
        public string Kind = "";
        public readonly List<NarrationSource> Sources = new();
        /// <summary>Sentences whose pitch falls at the end, of how many (the narration stage's check).</summary>
        public int InflectionFalls, InflectionOf;

        public double End => At + Math.Max(0, Seconds);
        public bool Contains(double t) => t >= At && t < End;
        public bool HasImage => Image.Length > 0;
    }

    /// <summary>The narration of one featured path (its id is the path's and the mashup's id).</summary>
    public sealed class NarrationPath
    {
        public string Id = "";
        /// <summary>Sorted by <see cref="NarrationCue.At"/>.</summary>
        public readonly List<NarrationCue> Cues = new();

        /// <summary>The cue speaking at mix time <paramref name="t"/> (null between cues).</summary>
        public NarrationCue? CueAt(double t)
        {
            int i = IndexAt(t);
            return i >= 0 && Cues[i].Contains(t) ? Cues[i] : null;
        }

        /// <summary>Index of the last cue that starts at or before <paramref name="t"/> (-1 before the first).</summary>
        public int IndexAt(double t)
        {
            int lo = 0, hi = Cues.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Cues[mid].At <= t)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return found;
        }

        /// <summary>
        /// The cue whose caption shows at <paramref name="t"/>: its own window plus
        /// <paramref name="hold"/> seconds, never past the next cue's start.
        /// </summary>
        public NarrationCue? CaptionAt(double t, double hold)
        {
            int i = IndexAt(t);
            if (i < 0) return null;
            NarrationCue c = Cues[i];
            double end = c.End + Math.Max(0, hold);
            if (i + 1 < Cues.Count) end = Math.Min(end, Cues[i + 1].At);
            return t < Math.Max(c.End, end) ? c : null;
        }

        /// <summary>The first cue that starts after <paramref name="t"/> (null when none).</summary>
        public NarrationCue? NextAfter(double t)
        {
            int i = IndexAt(t) + 1;
            return i < Cues.Count ? Cues[i] : null;
        }

        public double SpokenSeconds
        {
            get
            {
                double s = 0;
                foreach (NarrationCue c in Cues) s += Math.Max(0, c.Seconds);
                return s;
            }
        }
    }

    /// <summary>
    /// The narrated walkthroughs (data/audio/narration/narration.json, contract version 1, DESIGN.md
    /// §15): per featured path, cues at mix times with their WAV, text, duck depth, image and
    /// sources. A missing file gives an empty catalog that says so (the tours play unnarrated).
    /// </summary>
    public sealed class NarrationCatalog
    {
        public const string FileName = "narration.json";
        public const string CommandLineFlag = "-musicHistoryNarration";
        public const int ContractVersion = 1;
        /// <summary>Duck depth of a cue that gives none.</summary>
        public const double DefaultDuckDb = -12;
        /// <summary>Deepest duck accepted (a lower duck_db is clamped).</summary>
        public const double MinDuckDb = -40;

        public int Version;
        public string Voice = "", VoiceName = "", Model = "";
        public readonly List<NarrationPath> Paths = new();
        public readonly List<string> Problems = new();
        public string SourcePath = "";
        /// <summary>Folder the cue files are relative to (the folder of narration.json).</summary>
        public string Dir = "";
        public bool Loaded;
        public string Status = "";

        public static NarrationCatalog Empty(string status) => new() { Status = status };

        public int CueCount
        {
            get
            {
                int n = 0;
                foreach (NarrationPath p in Paths) n += p.Cues.Count;
                return n;
            }
        }

        /// <summary>The narration of path / mashup <paramref name="id"/> (null when none or it has no cues).</summary>
        public NarrationPath? For(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (NarrationPath p in Paths)
                if (p.Id == id && p.Cues.Count > 0) return p;
            return null;
        }

        /// <summary>&lt;data&gt;/audio/narration/narration.json (see <see cref="PipelinePaths.Data"/>).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(PipelinePaths.Data(), "audio", "narration", FileName);

        /// <summary>-musicHistoryNarration &lt;file&gt;, else <paramref name="configured"/>, else <see cref="DefaultPath"/>.</summary>
        public static string ResolvePath(string? configured, out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (!string.Equals(args[i], CommandLineFlag, StringComparison.OrdinalIgnoreCase)) continue;
                reason = CommandLineFlag;
                return args[i + 1];
            }
            if (!string.IsNullOrWhiteSpace(configured))
            {
                reason = "inspector";
                return configured!;
            }
            reason = "pipeline data";
            return DefaultPath();
        }

        /// <summary>Reads <paramref name="jsonPath"/>; never throws (a missing file gives an empty catalog).</summary>
        public static NarrationCatalog Load(string jsonPath)
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
            if (!System.IO.File.Exists(full)) return new NarrationCatalog { Status = "missing: " + full, SourcePath = full };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new NarrationCatalog { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        public static NarrationCatalog Parse(string json, string dir, string sourcePath = "")
        {
            NarrationCatalog c = new() { SourcePath = sourcePath, Dir = dir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("narration.json is not valid JSON: " + e.Message);
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
            c.Voice = MiniJson.Str(doc, "voice");
            c.VoiceName = MiniJson.Str(doc, "voice_name");
            c.Model = MiniJson.Str(doc, "model");
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(doc, "paths"))
            {
                if (item is not Dictionary<string, object?> p)
                {
                    c.Problems.Add("a paths entry is not an object");
                    continue;
                }
                NarrationPath path = ReadPath(c, p, dir);
                if (path.Id.Length == 0) c.Problems.Add($"narration path #{c.Paths.Count + 1}: no id");
                else if (!ids.Add(path.Id)) c.Problems.Add($"narration path '{path.Id}': duplicate id");
                c.Paths.Add(path);
            }
            c.Status = $"loaded {c.Paths.Count} narrated path{(c.Paths.Count == 1 ? "" : "s")}, {c.CueCount} cue{(c.CueCount == 1 ? "" : "s")}";
            return c;
        }

        static NarrationPath ReadPath(NarrationCatalog c, Dictionary<string, object?> p, string dir)
        {
            NarrationPath path = new() { Id = MiniJson.Str(p, "id") };
            string ctx = $"narration '{(path.Id.Length > 0 ? path.Id : "#" + (c.Paths.Count + 1))}'";
            HashSet<string> cueIds = new(StringComparer.Ordinal);
            foreach (object? item in MiniJson.Arr(p, "cues"))
            {
                if (item is not Dictionary<string, object?> o)
                {
                    c.Problems.Add($"{ctx}: a cue is not an object");
                    continue;
                }
                NarrationCue cue = new()
                {
                    Id = MiniJson.Str(o, "id"),
                    At = MiniJson.Num(o, "at", double.NaN),
                    Seconds = MiniJson.Num(o, "seconds", double.NaN),
                    File = MiniJson.Str(o, "file"),
                    Text = MiniJson.Str(o, "text").Trim(),
                    Kind = MiniJson.Str(o, "kind"),
                    Image = o.TryGetValue("image", out object? img) && img is string s ? s.Trim() : ""
                };
                string at = $"{ctx} cue '{(cue.Id.Length > 0 ? cue.Id : "#" + (path.Cues.Count + 1))}'";
                if (cue.Id.Length == 0) c.Problems.Add($"{at}: no id");
                else if (!cueIds.Add(cue.Id)) c.Problems.Add($"{at}: duplicate id");
                if (double.IsNaN(cue.At) || cue.At < 0)
                {
                    c.Problems.Add($"{at}: at {(double.IsNaN(cue.At) ? "missing" : cue.At.ToString("0.###", CultureInfo.InvariantCulture))}");
                    continue;
                }
                if (double.IsNaN(cue.Seconds) || cue.Seconds <= 0)
                {
                    c.Problems.Add($"{at}: seconds {(double.IsNaN(cue.Seconds) ? "missing" : cue.Seconds.ToString("0.###", CultureInfo.InvariantCulture))}");
                    continue;
                }
                if (cue.Text.Length == 0) c.Problems.Add($"{at}: no text");
                double duck = MiniJson.Num(o, "duck_db", double.NaN);
                if (double.IsNaN(duck))
                {
                    c.Problems.Add($"{at}: no duck_db (using {DefaultDuckDb} dB)");
                    duck = DefaultDuckDb;
                }
                else if (duck > 0)
                {
                    c.Problems.Add($"{at}: duck_db {duck} > 0 (no boost: using 0)");
                    duck = 0;
                }
                else if (duck < MinDuckDb)
                {
                    c.Problems.Add($"{at}: duck_db {duck} below {MinDuckDb} (clamped)");
                    duck = MinDuckDb;
                }
                cue.DuckDb = duck;
                if (cue.File.Length == 0) c.Problems.Add($"{at}: no file");
                else
                {
                    try
                    {
                        cue.AbsoluteFile = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, cue.File.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                        cue.FileExists = System.IO.File.Exists(cue.AbsoluteFile);
                    }
                    catch (Exception e)
                    {
                        c.Problems.Add($"{at}: bad file '{cue.File}' ({e.Message})");
                    }
                }
                foreach (object? src in MiniJson.Arr(o, "sources"))
                    if (src is Dictionary<string, object?> so)
                        cue.Sources.Add(new NarrationSource { Title = MiniJson.Str(so, "title"), Url = MiniJson.Str(so, "url") });
                if (o.TryGetValue("inflection", out object? inf) && inf is Dictionary<string, object?> io)
                {
                    cue.InflectionFalls = (int)MiniJson.Num(io, "falls", 0);
                    cue.InflectionOf = (int)MiniJson.Num(io, "of", 0);
                }
                path.Cues.Add(cue);
            }
            // Mix order (stable for equal times).
            List<NarrationCue> sorted = new(path.Cues);
            int[] order = new int[sorted.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => sorted[a].At != sorted[b].At ? sorted[a].At.CompareTo(sorted[b].At) : a.CompareTo(b));
            path.Cues.Clear();
            for (int i = 0; i < order.Length; i++)
            {
                NarrationCue cue = sorted[order[i]];
                cue.Index = i;
                path.Cues.Add(cue);
            }
            for (int i = 0; i + 1 < path.Cues.Count; i++)
                if (path.Cues[i].End > path.Cues[i + 1].At + 1e-3)
                    c.Problems.Add($"{ctx}: cue '{path.Cues[i].Id}' ({F(path.Cues[i].At)}–{F(path.Cues[i].End)} s) runs into '{path.Cues[i + 1].Id}' at {F(path.Cues[i + 1].At)} s");
            return path;
        }

        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
