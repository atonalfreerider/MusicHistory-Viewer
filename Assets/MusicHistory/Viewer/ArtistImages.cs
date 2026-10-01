#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MusicHistory.Playback;
using UnityEngine;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// One freely licensed photograph of a singer or group (data/images/artists.json, DESIGN.md §15):
    /// the JPG, whom it shows, and its Wikimedia Commons attribution, shown under every popup.
    /// </summary>
    public sealed class ArtistImage
    {
        public string Id = "";
        public string File = "";
        public string AbsoluteFile = "";
        public bool FileExists;
        /// <summary>Whom the photo shows ("The Beatles in 1964").</summary>
        public string Subject = "";
        /// <summary>The file's Commons page.</summary>
        public string Page = "";
        /// <summary>Plain text (Commons' HTML artist field is stripped).</summary>
        public string Author = "";
        public string License = "";
        public string LicenseUrl = "";

        /// <summary>"Photo: &lt;author&gt;, &lt;license&gt; (Wikimedia Commons)".</summary>
        public string Credit => ArtistImages.CreditLine(Author, License);
        /// <summary>The photo may be shown: its file exists and it carries a licence to attribute.</summary>
        public bool Showable => FileExists && License.Length > 0;
    }

    /// <summary>
    /// The artist photo catalog (data/images/artists.json; JPGs in data/images/artists/), loaded
    /// on demand: a photo's texture is decoded the first time a cue shows it and kept until
    /// <see cref="Release"/>. Tolerant of the catalog's shape: a list of entries, or entries keyed
    /// by image id, at the top level or under "images" / "artists" / "photos" / "entries"; field
    /// names subject|name, author|creator|credit, license|licence (or {"name","url"}),
    /// license_url|licence_url, commons_page|page|url, file|path (default artists/&lt;id&gt;.jpg).
    /// A missing catalog is empty and says so (no popups).
    /// </summary>
    public sealed class ArtistImages
    {
        public const string FileName = "artists.json";
        public const string CommandLineFlag = "-musicHistoryArtists";

        public readonly Dictionary<string, ArtistImage> Images = new(StringComparer.Ordinal);
        public readonly List<string> Problems = new();
        public string SourcePath = "";
        /// <summary>Folder the files are relative to (the folder of artists.json).</summary>
        public string Dir = "";
        public bool Loaded;
        public string Status = "";
        readonly Dictionary<string, Texture2D?> textures = new(StringComparer.Ordinal);

        public static ArtistImages Empty(string status) => new() { Status = status };

        /// <summary>The photo credit: "Photo: &lt;author&gt;, &lt;license&gt; (Wikimedia Commons)".</summary>
        public static string CreditLine(string? author, string? license)
        {
            string a = Clean(author);
            string l = Clean(license);
            return $"Photo: {(a.Length > 0 ? a : "unknown author")}, {(l.Length > 0 ? l : "licence unknown")} (Wikimedia Commons)";
        }

        public ArtistImage? Find(string? id) => !string.IsNullOrEmpty(id) && Images.TryGetValue(id!, out ArtistImage? i) ? i : null;

        /// <summary>&lt;data&gt;/images/artists.json (see <see cref="PipelinePaths.Data"/>).</summary>
        public static string DefaultPath() => System.IO.Path.Combine(PipelinePaths.Data(), "images", FileName);

        /// <summary>-musicHistoryArtists &lt;file&gt;, else <paramref name="configured"/>, else <see cref="DefaultPath"/>.</summary>
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
        public static ArtistImages Load(string jsonPath)
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
            if (!System.IO.File.Exists(full)) return new ArtistImages { Status = "missing: " + full, SourcePath = full, Dir = System.IO.Path.GetDirectoryName(full) ?? "" };
            string text;
            try
            {
                text = System.IO.File.ReadAllText(full, Encoding.UTF8);
            }
            catch (Exception e)
            {
                return new ArtistImages { Status = $"unreadable: {e.Message}", SourcePath = full };
            }
            return Parse(text, System.IO.Path.GetDirectoryName(full) ?? "", full);
        }

        static readonly string[] ListKeys = { "images", "artists", "photos", "entries", "items" };

        public static ArtistImages Parse(string json, string dir, string sourcePath = "")
        {
            ArtistImages c = new() { SourcePath = sourcePath, Dir = dir };
            object? root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                c.Status = "unreadable: " + e.Message;
                c.Problems.Add("artists.json is not valid JSON: " + e.Message);
                return c;
            }
            c.Loaded = true;
            object? body = root;
            if (root is Dictionary<string, object?> doc)
                foreach (string key in ListKeys)
                    if (doc.TryGetValue(key, out object? v) && (v is List<object?> || v is Dictionary<string, object?>))
                    {
                        body = v;
                        break;
                    }
            if (body is List<object?> list)
            {
                foreach (object? item in list)
                    if (item is Dictionary<string, object?> o) c.Add(o, null);
                    else c.Problems.Add("an image entry is not an object");
            }
            else if (body is Dictionary<string, object?> keyed)
            {
                foreach (KeyValuePair<string, object?> kv in keyed)
                    if (kv.Value is Dictionary<string, object?> o) c.Add(o, kv.Key);
            }
            else
            {
                c.Problems.Add("artists.json holds no image entries");
            }
            c.Status = $"loaded {c.Images.Count} photo{(c.Images.Count == 1 ? "" : "s")}";
            return c;
        }

        void Add(Dictionary<string, object?> o, string? key)
        {
            ArtistImage image = new()
            {
                Id = First(o, "id", "image_id", "image", "key") is { Length: > 0 } id ? id : key ?? "",
                Subject = Clean(First(o, "subject", "name", "label", "caption")),
                Page = First(o, "commons_page", "page", "commons_url", "page_url", "descriptionurl", "source_url", "url"),
                Author = Clean(First(o, "author", "artist_credit", "creator", "photographer", "credit", "attribution")),
                License = Clean(First(o, "license", "licence", "license_short", "licence_short", "license_name", "licence_name", "license_short_name")),
                LicenseUrl = First(o, "license_url", "licence_url"),
                File = First(o, "file", "path", "local_file", "image_file", "filename")
            };
            // {"license": {"name": ..., "url": ...}}
            foreach (string k in new[] { "license", "licence" })
                if (o.TryGetValue(k, out object? lv) && lv is Dictionary<string, object?> lo)
                {
                    if (image.License.Length == 0) image.License = Clean(First(lo, "name", "short", "short_name", "title"));
                    if (image.LicenseUrl.Length == 0) image.LicenseUrl = First(lo, "url", "link");
                }
            string ctx = $"image '{(image.Id.Length > 0 ? image.Id : "#" + (Images.Count + 1))}'";
            if (image.Id.Length == 0)
            {
                Problems.Add($"{ctx}: no id");
                return;
            }
            if (image.License.Length == 0) Problems.Add($"{ctx}: no licence (never shown)");
            if (image.Author.Length == 0) Problems.Add($"{ctx}: no author");
            if (image.File.Length == 0) image.File = "artists/" + image.Id + ".jpg";
            try
            {
                string rel = image.File.Replace('/', System.IO.Path.DirectorySeparatorChar);
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, rel));
                if (!System.IO.File.Exists(full) && !rel.Contains(System.IO.Path.DirectorySeparatorChar.ToString()))
                {
                    string inFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, "artists", rel));
                    if (System.IO.File.Exists(inFolder)) full = inFolder;
                }
                image.AbsoluteFile = full;
                image.FileExists = System.IO.File.Exists(full);
            }
            catch (Exception e)
            {
                Problems.Add($"{ctx}: bad file '{image.File}' ({e.Message})");
            }
            if (Images.ContainsKey(image.Id)) Problems.Add($"{ctx}: duplicate id");
            Images[image.Id] = image;
        }

        /// <summary>The first of <paramref name="keys"/> that holds a string or a number.</summary>
        static string First(Dictionary<string, object?> o, params string[] keys)
        {
            foreach (string k in keys)
            {
                if (!o.TryGetValue(k, out object? v) || v == null) continue;
                if (v is string s)
                {
                    if (s.Trim().Length > 0) return s.Trim();
                }
                else if (v is double d) return d.ToString(CultureInfo.InvariantCulture);
            }
            return "";
        }

        static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
        static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

        /// <summary>Plain text: HTML tags removed (Commons' artist field), entities decoded, whitespace collapsed.</summary>
        public static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = Tags.Replace(s!, " ");
            t = t.Replace("&amp;", "&").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
                 .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&nbsp;", " ");
            return Spaces.Replace(t, " ").Trim();
        }

        // ------------------------------------------------------------------ textures (on demand)

        /// <summary>The photo's texture, decoded on first use (null when the file is missing or unreadable).</summary>
        public Texture2D? Texture(string? id)
        {
            ArtistImage? image = Find(id);
            if (image == null || !image.FileExists) return null;
            if (textures.TryGetValue(image.Id, out Texture2D? cached)) return cached;
            Texture2D? tex = null;
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(image.AbsoluteFile);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "Artist " + image.Id,
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 1
                };
                if (!tex.LoadImage(bytes, true))
                {
                    Kill(tex);
                    tex = null;
                    Problems.Add($"image '{image.Id}': {image.AbsoluteFile} could not be decoded");
                }
            }
            catch (Exception e)
            {
                if (tex != null) Kill(tex);
                tex = null;
                Problems.Add($"image '{image.Id}': {e.Message}");
            }
            textures[image.Id] = tex;
            return tex;
        }

        /// <summary>Photos decoded so far.</summary>
        public int TexturesLoaded
        {
            get
            {
                int n = 0;
                foreach (Texture2D? t in textures.Values) if (t != null) n++;
                return n;
            }
        }

        /// <summary>Destroys every decoded texture.</summary>
        public void Release()
        {
            foreach (Texture2D? t in textures.Values) if (t != null) Kill(t);
            textures.Clear();
        }

        static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
