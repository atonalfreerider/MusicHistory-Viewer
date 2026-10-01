#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Narrated walkthroughs (DESIGN.md §15) in edit mode, without opening a scene: narration.json
    /// and artists.json parsing (synthetic fixtures, plus the real files when the pipeline has
    /// written them), the captions against the mashup mix clock (play, pause, seek, Next / Back
    /// jumps, Stop, captions off; each held for its reading time) with a <see cref="MashupPlayer"/>
    /// and a <see cref="NarrationPlayer"/> on the main-thread clock — and no voiceover: no narration
    /// audio, the music never ducked — the (unused, kept) duck envelope, the photo credit line and
    /// on-demand JPG decoding, and the horizontal / vertical layout rects (no overlaps at 1920x1080
    /// and 1080x1920 and other sizes).
    ///
    /// MusicHistory › Run Narration Validation, or
    /// <c>-executeMethod MusicHistory.EditorTools.NarrationValidation.Run</c> (exit code 0 when every
    /// check passes). Writes narration_validation.json to -validationOut, else
    /// &lt;MusicHistory&gt;/data/screens.
    /// </summary>
    public static class NarrationValidation
    {
        sealed class Report
        {
            public readonly List<(string name, bool ok, string detail)> Checks = new();
            public int Failures => Checks.Count(c => !c.ok);

            public bool Check(string name, bool ok, string detail = "")
            {
                Checks.Add((name, ok, detail));
                string line = $"[narration-validation] {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " — " + detail : "")}";
                if (ok) Debug.Log(line);
                else Debug.LogWarning(line);
                return ok;
            }

            public void Note(string text) => Debug.Log("[narration-validation] " + text);
        }

        [MenuItem("MusicHistory/Run Narration Validation")]
        public static void RunFromMenu() => Execute(exitWhenDone: false);

        /// <summary>-executeMethod entry (batchmode is fine: nothing here needs a Game view).</summary>
        public static void Run() => Execute(exitWhenDone: true);

        static void Execute(bool exitWhenDone)
        {
            Report report = new();
            try
            {
                CatalogChecks(report);
                ImageChecks(report);
                EnvelopeChecks(report);
                SchedulingChecks(report);
                LayoutChecks(report);
                RealDataChecks(report);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                report.Check("no exception", false, e.GetType().Name + ": " + e.Message);
            }
            string summary = $"{report.Checks.Count - report.Failures}/{report.Checks.Count} narration checks passed";
            try
            {
                string outDir = Arg("-validationOut") ?? Path.Combine(PipelinePaths.Data(), "screens");
                Directory.CreateDirectory(outDir);
                string file = Path.Combine(outDir, "narration_validation.json");
                File.WriteAllText(file, Json(report), new UTF8Encoding(false));
                summary += "; report " + file;
            }
            catch (Exception e)
            {
                summary += $"; report not written ({e.Message})";
            }
            if (report.Failures == 0) Debug.Log("[narration-validation] " + summary);
            else Debug.LogError("[narration-validation] " + summary + "; failures: " + string.Join(" | ", report.Checks.Where(c => !c.ok).Select(c => c.name)));
            if (exitWhenDone) EditorApplication.Exit(report.Failures == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ fixtures

        const string PathId = "fixture-path";

        /// <summary>A narration.json in the contract's shape (cues deliberately out of order).</summary>
        static string NarrationFixture(string extraPath = "") => @"{
  ""version"": 1, ""voice"": ""7NoxJCAEPTXbnfIvyaF6"", ""voice_name"": ""JohnV4"", ""model"": ""eleven_v4"",
  ""paths"": [
    {""id"": """ + PathId + @""", ""cues"": [
      {""id"": ""song1"", ""at"": 12.0, ""seconds"": 6.0, ""file"": """ + PathId + @"/02_song1.wav"", ""text"": ""The first song builds its verse on four chords."",
       ""duck_db"": -10.5, ""image"": ""artist-a"", ""sources"": [{""title"": ""Interview"", ""url"": ""https://example.org/a""}], ""inflection"": {""falls"": 1, ""of"": 1}},
      {""id"": ""intro"", ""at"": 1.0, ""seconds"": 4.0, ""file"": """ + PathId + @"/01_intro.wav"", ""text"": ""Three songs share one progression."",
       ""duck_db"": -12, ""image"": null, ""sources"": []},
      {""id"": ""changeover"", ""at"": 30.0, ""seconds"": 5.0, ""file"": """ + PathId + @"/03_changeover.wav"", ""text"": ""Here the second vocal enters over the first band."",
       ""duck_db"": -14, ""image"": ""artist-b"", ""sources"": []},
      {""id"": ""outro"", ""at"": 50.0, ""seconds"": 4.0, ""file"": """ + PathId + @"/04_outro.wav"", ""text"": ""The progression outlived all three."",
       ""image"": ""artist-missing""}
    ]}" + extraPath + @"
  ]
}";

        static NarrationCatalog FixtureCatalog() => NarrationCatalog.Parse(NarrationFixture(), Path.Combine(Path.GetTempPath(), "narration-fixture"), "fixture");

        static void CatalogChecks(Report r)
        {
            NarrationCatalog c = FixtureCatalog();
            NarrationPath? p = c.For(PathId);
            r.Check("catalog: fixture parses (version 1, voice, one path, four cues)",
                c.Loaded && c.Version == 1 && c.VoiceName == "JohnV4" && c.Model == "eleven_v4" && p != null && p.Cues.Count == 4, c.Status);
            if (p == null) return;
            r.Check("catalog: cues sorted by mix time, indexed", p.Cues.Select(q => q.Id).SequenceEqual(new[] { "intro", "song1", "changeover", "outro" }) &&
                                                               p.Cues.Select(q => q.Index).SequenceEqual(new[] { 0, 1, 2, 3 }),
                string.Join(", ", p.Cues.Select(q => $"{q.Id}@{q.At}")));
            NarrationCue s1 = p.Cues[1];
            r.Check("catalog: cue fields (at, seconds, end, text, duck_db, image, sources, inflection)",
                s1.At == 12 && s1.Seconds == 6 && s1.End == 18 && s1.Text.StartsWith("The first song") && Math.Abs(s1.DuckDb + 10.5) < 1e-9 &&
                s1.Image == "artist-a" && s1.Sources.Count == 1 && s1.Sources[0].Url == "https://example.org/a" && s1.InflectionFalls == 1 && s1.InflectionOf == 1);
            r.Check("catalog: image null is no image; the file resolves against the narration folder",
                !p.Cues[0].HasImage && p.Cues[0].AbsoluteFile.EndsWith(Path.Combine(PathId, "01_intro.wav")) && !p.Cues[0].FileExists);
            r.Check("catalog: a cue without duck_db gets the default and is reported",
                p.Cues[3].DuckDb == NarrationCatalog.DefaultDuckDb && c.Problems.Any(x => x.Contains("outro") && x.Contains("duck_db")), string.Join(" | ", c.Problems));
            r.Check("catalog: lookups on the mix clock (before, inside, between, edge, after)",
                p.CueAt(.5) == null && p.CueAt(1.0) == p.Cues[0] && p.CueAt(4.99) == p.Cues[0] && p.CueAt(5.0) == null && p.CueAt(13) == s1 &&
                p.CueAt(29.99) == null && p.CueAt(30) == p.Cues[2] && p.CueAt(60) == null && p.IndexAt(25) == 1 && p.NextAfter(25) == p.Cues[2]);
            r.Check("catalog: the caption holds after the line, never into the next cue",
                p.CaptionAt(5.3, .6) == p.Cues[0] && p.CaptionAt(5.7, .6) == null && p.CaptionAt(18.5, .6) == s1 && p.CaptionAt(11.99, 20) == p.Cues[0]);
            r.Check("catalog: For() finds the path by id; unknown ids and empty ids give null",
                c.For("nope") == null && c.For("") == null && c.For(null) == null);

            NarrationCatalog overlap = NarrationCatalog.Parse(@"{""version"":1,""paths"":[{""id"":""x"",""cues"":[
                {""id"":""a"",""at"":0,""seconds"":5,""file"":""x/a.wav"",""text"":""A."",""duck_db"":-12},
                {""id"":""b"",""at"":3,""seconds"":2,""file"":""x/b.wav"",""text"":""B."",""duck_db"":3},
                {""id"":""c"",""at"":-1,""seconds"":2,""file"":""x/c.wav"",""text"":""C."",""duck_db"":-12},
                {""id"":""a"",""at"":9,""seconds"":1,""file"":""x/d.wav"",""text"":""D."",""duck_db"":-99}]}]}", "", "overlap");
            NarrationPath? op = overlap.For("x");
            r.Check("catalog: overlapping cues, a positive duck_db, a negative time, a duplicate id and an extreme duck are reported",
                op != null && op.Cues.Count == 3 && overlap.Problems.Any(x => x.Contains("runs into")) && overlap.Problems.Any(x => x.Contains("> 0")) &&
                overlap.Problems.Any(x => x.Contains("at -1")) && overlap.Problems.Any(x => x.Contains("duplicate")) &&
                op.Cues.All(q => q.DuckDb <= 0 && q.DuckDb >= NarrationCatalog.MinDuckDb), string.Join(" | ", overlap.Problems));

            NarrationCatalog bad = NarrationCatalog.Parse("{not json", "", "bad");
            NarrationCatalog missing = NarrationCatalog.Load(Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "narration.json"));
            r.Check("catalog: invalid JSON and a missing file give empty catalogs that say so",
                !bad.Loaded && bad.Paths.Count == 0 && bad.Status.StartsWith("unreadable") && !missing.Loaded && missing.Paths.Count == 0 && missing.Status.StartsWith("missing"),
                $"{bad.Status} / {missing.Status}");
            r.Check("catalog: the default location is <data>/audio/narration/narration.json",
                NarrationCatalog.DefaultPath().Replace('\\', '/').EndsWith("/audio/narration/narration.json"), NarrationCatalog.DefaultPath());
        }

        // ------------------------------------------------------------------ photos and attribution

        static void ImageChecks(Report r)
        {
            string dir = Path.Combine(Path.GetTempPath(), "musichistory-narration-validation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "artists"));
            try
            {
                // A real JPG to decode on demand.
                Texture2D src = new(6, 4, TextureFormat.RGB24, false);
                Color32[] px = new Color32[24];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32((byte)(i * 10), 80, 160, 255);
                src.SetPixels32(px);
                src.Apply();
                File.WriteAllBytes(Path.Combine(dir, "artists", "artist-a.jpg"), src.EncodeToJPG(90));
                Object.DestroyImmediate(src);

                string listForm = @"{""version"": 1, ""images"": [
                  {""id"": ""artist-a"", ""file"": ""artists/artist-a.jpg"", ""subject"": ""Artist A on stage, 1971"", ""commons_page"": ""https://commons.wikimedia.org/wiki/File:A.jpg"",
                   ""author"": ""<a href='https://commons.wikimedia.org/wiki/User:Jane'>Jane &amp; Co.</a>"", ""license"": ""CC BY-SA 4.0"", ""license_url"": ""https://creativecommons.org/licenses/by-sa/4.0""},
                  {""id"": ""artist-b"", ""subject"": ""Artist B"", ""author"": ""John Smith"", ""licence"": {""name"": ""CC BY 2.0"", ""url"": ""https://creativecommons.org/licenses/by/2.0""}},
                  {""id"": ""artist-c"", ""file"": ""artists/artist-c.jpg"", ""subject"": ""Unlicensed"", ""author"": ""Nobody""}]}";
                ArtistImages images = ArtistImages.Parse(listForm, dir, "fixture");
                ArtistImage? a = images.Find("artist-a"), b = images.Find("artist-b"), c = images.Find("artist-c");
                r.Check("photos: list form parses (subject, page, author without markup, licence, licence URL, file)",
                    a != null && a.Subject == "Artist A on stage, 1971" && a.Page.Contains("File:A.jpg") && a.Author == "Jane & Co." &&
                    a.License == "CC BY-SA 4.0" && a.LicenseUrl.Contains("by-sa") && a.FileExists, images.Status + " " + string.Join(" | ", images.Problems));
                r.Check("photos: the credit line reads 'Photo: <author>, <license> (Wikimedia Commons)'",
                    a != null && a.Credit == "Photo: Jane & Co., CC BY-SA 4.0 (Wikimedia Commons)", a?.Credit ?? "");
                r.Check("photos: a nested licence object and the default file artists/<id>.jpg",
                    b != null && b.License == "CC BY 2.0" && b.LicenseUrl.Contains("by/2.0") && b.File == "artists/artist-b.jpg" && !b.FileExists &&
                    b.Credit == "Photo: John Smith, CC BY 2.0 (Wikimedia Commons)", b?.Credit ?? "");
                r.Check("photos: an unlicensed or missing photo is never shown (and is reported)",
                    c != null && !c.Showable && b != null && !b.Showable && a != null && a.Showable && images.Problems.Any(x => x.Contains("artist-c") && x.Contains("licence")),
                    string.Join(" | ", images.Problems));
                r.Check("photos: the credit is always complete, even without an author or licence",
                    ArtistImages.CreditLine("", "") == "Photo: unknown author, licence unknown (Wikimedia Commons)" &&
                    ArtistImages.CreditLine(" Ann  Lee ", "Public domain") == "Photo: Ann Lee, Public domain (Wikimedia Commons)");
                r.Check("photos: the card's credit markup keeps the text literal (no rich-text injection)",
                    a != null && NarrationOverlay.CreditMarkup(a) == "<noparse>Photo: Jane & Co., CC BY-SA 4.0 (Wikimedia Commons)</noparse>", a != null ? NarrationOverlay.CreditMarkup(a) : "");

                Texture2D? tex = images.Texture("artist-a");
                Texture2D? again = images.Texture("artist-a");
                r.Check("photos: the JPG decodes on demand, once (cached)", tex != null && tex.width == 6 && tex.height == 4 && ReferenceEquals(tex, again) && images.TexturesLoaded == 1,
                    tex != null ? $"{tex.width}x{tex.height}" : "null");
                r.Check("photos: a missing file gives no texture", images.Texture("artist-b") == null && images.Texture("nope") == null);
                images.Release();
                r.Check("photos: Release destroys the textures", tex == null && images.TexturesLoaded == 0);

                string keyed = @"{""artist-x"": {""subject"": ""X"", ""author"": ""Y"", ""license"": ""CC0""}, ""artist-y"": {""name"": ""Z"", ""creator"": ""W"", ""licence_short"": ""CC BY 3.0""}}";
                ArtistImages k = ArtistImages.Parse(keyed, dir, "keyed");
                ArtistImages nested = ArtistImages.Parse(@"{""version"": 1, ""artists"": {""artist-z"": {""subject"": ""Z"", ""author"": ""Q"", ""license"": ""CC BY 4.0""}}}", dir, "nested");
                r.Check("photos: entries keyed by image id (top level or under \"artists\")",
                    k.Find("artist-x")?.License == "CC0" && k.Find("artist-y")?.Subject == "Z" && k.Find("artist-y")?.Author == "W" &&
                    k.Find("artist-y")?.License == "CC BY 3.0" && nested.Find("artist-z")?.Author == "Q");
                ArtistImages none = ArtistImages.Load(Path.Combine(dir, "nothing.json"));
                r.Check("photos: a missing artists.json is empty and says so", none.Images.Count == 0 && none.Status.StartsWith("missing"), none.Status);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception)
                {
                    // A temp folder left behind is harmless.
                }
            }
        }

        // ------------------------------------------------------------------ the duck envelope (kept for a voice to come back; unused)

        static void EnvelopeChecks(Report r)
        {
            DuckEnvelope e = new() { AttackSeconds = .15f, ReleaseSeconds = .6f };
            for (int i = 0; i < 200; i++) e.Step(.01, false, -12f);
            r.Check("duck: never ducks while no narration is heard", e.GainDb == 0f && e.Gain == 1f);

            List<float> attack = new();
            for (int i = 0; i < 15; i++)
            {
                e.Step(.01, true, -12f);
                attack.Add(e.GainDb);
            }
            bool monotonic = attack.Zip(attack.Skip(1), (x, y) => y <= x + 1e-5f).All(ok => ok);
            r.Check("duck: attack reaches duck_db after 0.15 s, smoothly and monotonically",
                Mathf.Abs(e.GainDb + 12f) < 1e-3f && monotonic && attack[0] > -1f && Mathf.Abs(attack[7] + 6f) < 2f,
                string.Join(" ", attack.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture))));
            r.Check("duck: linear gain matches the dB value", Mathf.Abs(e.Gain - Mathf.Pow(10f, -12f / 20f)) < 1e-4f, e.Gain.ToString("0.0000", CultureInfo.InvariantCulture));
            e.Step(.5, true, -12f);
            r.Check("duck: holds while the line plays", Mathf.Abs(e.GainDb + 12f) < 1e-3f);

            List<float> release = new();
            for (int i = 0; i < 60; i++)
            {
                e.Step(.01, false, -12f);
                release.Add(e.GainDb);
            }
            bool rising = release.Zip(release.Skip(1), (x, y) => y >= x - 1e-5f).All(ok => ok);
            r.Check("duck: release returns to 0 dB after 0.6 s (not before), smoothly",
                e.GainDb == 0f && e.Gain == 1f && rising && release[29] < -3f && release[29] > -9f && release[58] > -.05f,
                $"at 0.3 s {release[29]:0.00} dB");

            DuckEnvelope two = new() { AttackSeconds = .15f, ReleaseSeconds = .6f };
            for (int i = 0; i < 30; i++) two.Step(.01, true, -10f);
            float before = two.GainDb;
            two.Step(.01, true, -16f);
            r.Check("duck: a new cue's deeper duck_db slews (no jump)", before < -9.99f && two.GainDb < before && two.GainDb > -10.5f, $"{before:0.00} → {two.GainDb:0.00}");
            DuckEnvelope fresh = new() { AttackSeconds = .15f, ReleaseSeconds = .6f };
            fresh.Step(.01, true, -20f);
            r.Check("duck: a fresh duck takes the cue's own depth", Mathf.Abs(fresh.DepthDb + 20f) < 1e-4f && fresh.GainDb < 0f);
        }

        // ------------------------------------------------------------------ captions on the mix clock (no voiceover)

        static void SchedulingChecks(Report r)
        {
            NarrationCatalog catalog = FixtureCatalog();
            NarrationPath path = catalog.For(PathId)!;
            // Reading windows: at least the spoken length, extended to the reading time, never into the next cue.
            NarrationCatalog reading = NarrationCatalog.Parse(@"{""version"":1,""paths"":[{""id"":""r"",""cues"":[
                {""id"":""a"",""at"":0,""seconds"":1,""file"":""r/a.wav"",""text"":""" + new string('a', 60) + @""",""duck_db"":-12},
                {""id"":""b"",""at"":5,""seconds"":1,""file"":""r/b.wav"",""text"":""" + new string('b', 90) + @""",""duck_db"":-12},
                {""id"":""c"",""at"":8,""seconds"":6,""file"":""r/c.wav"",""text"":""Short."",""duck_db"":-12}]}]}", "", "reading");
            NarrationPath rp = reading.For("r")!;
            r.Check("captions: held for the reading time (60 chars at 15/s = 4 s) plus the hold, at least the spoken length, never into the next cue",
                Math.Abs(rp.CaptionEnd(0, 15, 2, .6) - 4.6) < 1e-9 && Math.Abs(rp.CaptionEnd(1, 15, 2, .6) - 8) < 1e-9 && Math.Abs(rp.CaptionEnd(2, 15, 2, .6) - 14.6) < 1e-9 &&
                rp.ReadingCaptionAt(4.5, 15, 2, .6) == rp.Cues[0] && rp.ReadingCaptionAt(4.7, 15, 2, .6) == null && rp.ReadingCaptionAt(7.99, 15, 2, .6) == rp.Cues[1],
                $"{rp.CaptionEnd(0, 15, 2, .6):0.00} / {rp.CaptionEnd(1, 15, 2, .6):0.00} / {rp.CaptionEnd(2, 15, 2, .6):0.00}");

            GameObject host = new("Narration Validation") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                MashupPlayer mix = host.AddComponent<MashupPlayer>();
                mix.ClockOnly = true;
                NarrationPlayer narration = host.AddComponent<NarrationPlayer>();
                Mashup m = SyntheticMashup(PathId, 60);
                Mashup? current = m;
                narration.Bind(mix, () => current, catalog);
                SongClip tag = new() { NodeId = 1, Title = "fixture" };
                mix.PlayMix(m, 0, tag);
                const double dt = .02;
                float lowest = 1f;
                void Step()
                {
                    mix.Advance(dt);
                    narration.Advance(dt);
                    lowest = Mathf.Min(lowest, mix.DuckGain);
                }
                void Run(double seconds)
                {
                    for (double s = 0; s < seconds - 1e-9; s += dt) Step();
                }
                void RunTo(double t)
                {
                    for (int k = 0; k < 100000 && mix.CurrentSeconds < t - 1e-9; k++) Step();
                }
                double End(int i) => narration.CaptionEnd(path, i);

                RunTo(.8);
                r.Check("captions: before the first cue no caption shows", narration.CurrentPath == path && narration.CaptionCue == null, $"t {mix.CurrentSeconds:0.00}");
                RunTo(1.05);
                r.Check("captions: the intro's caption appears at its 'at' on the mix clock", narration.CaptionCue == path.Cues[0] && narration.CaptionsShown == 1,
                    $"t {mix.CurrentSeconds:0.000}");
                // Pause: the clock stands, the caption stays.
                mix.Paused = true;
                double pausedAt = mix.CurrentSeconds;
                Run(1.0);
                bool pausedOk = Math.Abs(mix.CurrentSeconds - pausedAt) < 1e-9 && narration.CaptionCue == path.Cues[0];
                mix.Paused = false;
                Run(.2);
                r.Check("captions: pause holds the clock and the caption; resume goes on", pausedOk && narration.CaptionCue == path.Cues[0] && narration.CaptionsShown == 1);
                RunTo(End(0) - .05);
                bool held = narration.CaptionCue == path.Cues[0];
                RunTo(End(0) + .05);
                r.Check("captions: the caption is held through its reading window (at least the spoken length), then goes",
                    held && narration.CaptionCue == null && End(0) >= path.Cues[0].End, $"window ends {End(0):0.00} s (spoken to {path.Cues[0].End:0.00} s)");
                // A jump into the middle of a line shows that line (there is no voice to wait for).
                mix.Seek(14.0);
                Run(.1);
                r.Check("captions: a seek into the middle of a line shows its caption at once", narration.CaptionCue == path.Cues[1], $"t {mix.CurrentSeconds:0.00}");
                mix.Seek(11.7);
                Run(.1);
                bool waiting = narration.CaptionCue == null;
                RunTo(12.1);
                r.Check("captions: a seek back before a cue shows nothing until its time", waiting && narration.CaptionCue == path.Cues[1]);
                // Captions off (N): nothing shows; on again, the line of the moment.
                RunTo(31.5);
                narration.SetOn(false);
                Run(.3);
                bool offOk = narration.CaptionCue == null;
                narration.SetOn(true);
                Run(.1);
                r.Check("captions: N hides the captions and shows them again", offOk && narration.CaptionCue == path.Cues[2], $"t {mix.CurrentSeconds:0.00}");
                // Stop: the tour ends (no mashup plays) → no caption at once.
                RunTo(50.2);
                bool outro = narration.CaptionCue == path.Cues[3];
                mix.Stop();
                current = null;
                narration.Advance(dt);
                r.Check("captions: Stop clears the caption at once", outro && narration.CurrentPath == null && narration.CaptionCue == null);

                // Over the whole mix: every cue's caption appears once, at its time; no voiceover at all.
                current = m;
                mix.PlayMix(m, 0, tag);
                narration.Advance(0);
                int before = narration.CaptionsShown;
                List<string> late = new();
                NarrationCue? last = null;
                for (int k = 0; k < 10000 && !mix.Complete; k++)
                {
                    Step();
                    NarrationCue? c = narration.CaptionCue;
                    if (c != null && !ReferenceEquals(c, last) && mix.CurrentSeconds - c.At > dt + 1e-6) late.Add($"{c.Id} at {mix.CurrentSeconds:0.00}");
                    last = c;
                }
                AudioSource[] sources = host.GetComponentsInChildren<AudioSource>(true);
                r.Check("captions: over the whole mix every cue's caption appears once, on time",
                    narration.CaptionsShown - before == path.Cues.Count && late.Count == 0, $"{narration.CaptionsShown - before} of {path.Cues.Count}; late {string.Join(", ", late)}");
                r.Check("no voiceover: no narration AudioSource or clip, nothing speaks, the music stays at full gain through every cue",
                    sources.Length == 0 && narration.Source == null && !narration.Speaking && narration.SpeakingCue == null && lowest == 1f && narration.DuckGain == 1f &&
                    narration.AllLoaded(path), $"{sources.Length} audio sources; lowest mix gain {lowest:0.000}");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        static Mashup SyntheticMashup(string id, double seconds)
        {
            Mashup m = new() { Id = id, Title = id, Seconds = seconds, BeatsPerBar = 4, PhraseBeats = 32 };
            m.Segments.Add(new MashupSegment { Index = 0, Start = 0, End = seconds, Kind = MashupSegmentKind.Full, Key = "C major", Bpm = 120, BpmStart = 120, InstrumentalSong = 0 });
            m.Songs.Add(new MashupSong { Index = 0, WorkId = "w1", Title = "Song", Year = 1970, PathStep = 0, NodeId = 1 });
            return m;
        }

        // ------------------------------------------------------------------ layout

        static readonly (int w, int h, string label)[] Screens =
        {
            (1920, 1080, "horizontal 1920x1080"), (1080, 1920, "vertical 1080x1920"),
            (2560, 1440, "horizontal 2560x1440"), (1280, 720, "horizontal 1280x720"), (1440, 1080, "horizontal 4:3 1440x1080"),
            (2560, 1080, "horizontal 21:9 2560x1080"), (1080, 2340, "vertical 1080x2340"), (720, 1280, "vertical 720x1280")
        };

        const float NominalLegend = 300f, NominalInfo = 330f;

        static void LayoutChecks(Report r)
        {
            r.Check("layout: 1920x1080 is horizontal, 1080x1920 vertical, a square-ish window stays horizontal",
                ViewerLayout.FormatFor(1920, 1080) == ScreenFormat.Horizontal && ViewerLayout.FormatFor(1080, 1920) == ScreenFormat.Vertical &&
                ViewerLayout.FormatFor(1000, 1100) == ScreenFormat.Horizontal);
            LayoutFrame h = ViewerLayout.Compute(1920, 1080, true, true, true, NominalLegend, NominalInfo, ViewerLayout.DefaultTourView, ViewerLayout.DefaultMashupView);
            r.Check("layout: horizontal keeps today's layout (1920x1080 canvas, strip and melody graph where they were)",
                Near(h.Canvas, new Vector2(1920, 1080)) && Near(h.Strip, new Rect(340, 16, 1240, 206)) && Near(h.Melody, new Rect(340, MelodyGraphPanel.PanelBottom, 1240, 272)) &&
                Mathf.Approximately(h.Melody.yMin, MelodyGraphPanel.PanelBottom) && Near(h.Button, new Rect(834, 1020, 252, 40)),
                $"canvas {h.Canvas}, melody {h.Melody}");
            LayoutFrame v = ViewerLayout.Compute(1080, 1920, true, true, true, NominalLegend, NominalInfo, ViewerLayout.DefaultTourView, ViewerLayout.DefaultMashupView);
            r.Check("layout: vertical stacks graph view > melody graph > caption + photo > strip",
                v.Vertical && v.Strip.yMin >= 0 && v.Band.yMin >= v.Strip.yMax && v.Melody.yMin >= v.Band.yMax && v.MashupView.yMin * v.Canvas.y >= v.Melody.yMax &&
                v.CardSlot.yMin >= v.Band.yMin && v.CardSlot.yMax <= v.Band.yMax + .01f && v.CaptionSlot.yMin >= v.Band.yMin && v.CaptionSlot.yMax <= v.Band.yMax + .01f,
                $"strip {v.Strip}, band {v.Band}, melody {v.Melody}, view y {v.MashupView.yMin:0.000}–{v.MashupView.yMax:0.000}");
            float vs = v.Scale;
            r.Check("layout: vertical text is readable at 1080x1920 (caption ≥ 30 px, photo subject ≥ 20 px, credit ≥ 13 px on screen)",
                v.CaptionFont * vs >= 30f && v.SubjectFont * vs >= 20f && v.CreditFont * vs >= 13f && v.CaptionMinFont * vs >= 22f,
                $"scale {vs:0.000}: caption {v.CaptionFont * vs:0.#} px, subject {v.SubjectFont * vs:0.#} px, credit {v.CreditFont * vs:0.#} px");
            // The portrait melody graph is three times larger (DESIGN.md §16), so the 3D view above it
            // gets less of the frame than before (it was at least a third).
            r.Check("layout: the vertical graph view is at least a quarter of the screen, above the 3x melody graph",
                v.MashupView.height >= .25f && Mathf.Approximately(v.Melody.height, MelodyGraphPanel.PortraitPanelHeight),
                $"{v.MashupView.height:0.000}; melody graph {v.Melody.height:0} px tall");
            LayoutFrame duet = ViewerLayout.Compute(1080, 1920, true, false, false, NominalLegend, NominalInfo, ViewerLayout.DefaultTourView, ViewerLayout.DefaultMashupView);
            r.Check("layout: vertical without narration (a duet loop): the melody graph sits right above the strip, the view gets the band's room",
                duet.Melody.yMin <= duet.Strip.yMax + ViewerLayout.BandGap + .01f && duet.MashupView.yMin * duet.Canvas.y >= duet.Melody.yMax &&
                duet.MashupView.height > v.MashupView.height,
                $"melody {duet.Melody}, view y {duet.MashupView.yMin:0.000}–{duet.MashupView.yMax:0.000}");

            foreach ((int w, int hh, string label) in Screens)
                foreach (bool melody in new[] { true, false })
                    foreach (bool photo in new[] { true, false })
                    {
                        LayoutFrame f = ViewerLayout.Compute(w, hh, melody, photo, true, NominalLegend, NominalInfo, ViewerLayout.DefaultTourView, ViewerLayout.DefaultMashupView);
                        // The biggest caption and card the overlay can place.
                        Rect caption = f.PlaceCaption(new Vector2(f.CaptionSlot.width, f.CaptionSlot.height));
                        Rect card = f.PlaceCard(f.CardSlot.height);
                        List<(string, Rect)> fixedRects = new() { ("strip", f.Strip), ("button", f.Button), ("legend", f.Legend), ("info", f.Info) };
                        if (melody) fixedRects.Add(("melody graph", f.Melody));
                        List<string> overlaps = new();
                        foreach ((string name, Rect rect) in fixedRects)
                        {
                            if (caption.Overlaps(rect)) overlaps.Add("caption×" + name);
                            if (photo && card.Overlaps(rect)) overlaps.Add("card×" + name);
                        }
                        if (photo && caption.Overlaps(card)) overlaps.Add("caption×card");
                        if (f.Strip.Overlaps(f.Melody)) overlaps.Add("strip×melody");
                        Rect screen = new(0, 0, f.Canvas.x, f.Canvas.y);
                        bool inside = Inside(screen, caption) && Inside(screen, f.Strip) && Inside(screen, f.Melody) && (!photo || Inside(screen, card));
                        bool cardRoom = !photo || card.width >= 200f && card.height >= 280f;
                        r.Check($"layout {label}{(melody ? "" : ", melody hidden")}{(photo ? ", photo" : "")}: no overlaps, all on screen",
                            overlaps.Count == 0 && inside && cardRoom,
                            $"{f.Format} canvas {f.Canvas.x:0}x{f.Canvas.y:0}; caption {R(caption)}; card {R(card)}{(overlaps.Count > 0 ? "; " + string.Join(", ", overlaps) : "")}");
                    }
        }

        static bool Inside(Rect outer, Rect inner) =>
            inner.xMin >= outer.xMin - .01f && inner.yMin >= outer.yMin - .01f && inner.xMax <= outer.xMax + .01f && inner.yMax <= outer.yMax + .01f;

        static bool Near(Vector2 a, Vector2 b) => Vector2.Distance(a, b) < .5f;
        static bool Near(Rect a, Rect b) => Near(a.position, b.position) && Near(a.size, b.size);
        static string R(Rect x) => $"({x.x:0},{x.y:0} {x.width:0}x{x.height:0})";

        // ------------------------------------------------------------------ the real data (when the pipeline wrote it)

        static void RealDataChecks(Report r)
        {
            string narrationFile = NarrationCatalog.DefaultPath();
            string artistsFile = ArtistImages.DefaultPath();
            NarrationCatalog n = NarrationCatalog.Load(narrationFile);
            ArtistImages images = ArtistImages.Load(artistsFile);
            r.Note($"real narration: {n.Status}; photos: {images.Status}");
            if (!n.Loaded)
            {
                r.Note("narration.json not produced yet: real-data checks skipped.");
                return;
            }
            r.Check("real: narration.json is contract version 1 with no problems", n.Version == NarrationCatalog.ContractVersion && n.Problems.Count == 0,
                n.Problems.Count > 0 ? $"{n.Problems.Count} problems, first: {string.Join(" | ", n.Problems.Take(5))}" : n.Status);
            List<NarrationCue> cues = n.Paths.SelectMany(p => p.Cues).ToList();
            List<NarrationCue> noFile = cues.Where(c => !c.FileExists).ToList();
            // The WAVs are kept for future reference (never played): they should still be there.
            r.Check("real: every cue's WAV exists (kept on disk, never played)", noFile.Count == 0, noFile.Count > 0 ? $"{noFile.Count} missing, first {noFile[0].AbsoluteFile}" : $"{cues.Count} cues");
            List<NarrationCue> withImage = cues.Where(c => c.HasImage).ToList();
            List<string> badImages = withImage.Where(c => images.Find(c.Image) is not ArtistImage a || !a.Showable || a.Author.Length == 0)
                .Select(c => c.Image).Distinct().ToList();
            r.Check("real: every cue image is in artists.json with its file, author and licence", badImages.Count == 0,
                badImages.Count > 0 ? $"{badImages.Count} unusable: {string.Join(", ", badImages.Take(8))}" : $"{withImage.Count} cues with photos");
            MashupCatalog mashups = MashupCatalog.Load(Path.Combine(PipelinePaths.Data(), "audio", "mashups", MashupCatalog.FileName));
            if (mashups.Loaded)
            {
                List<string> orphan = new(), late = new();
                foreach (NarrationPath p in n.Paths)
                {
                    Mashup? m = mashups.Find(p.Id);
                    if (m == null)
                    {
                        orphan.Add(p.Id);
                        continue;
                    }
                    foreach (NarrationCue c in p.Cues)
                        if (c.End > m.Duration + .05) late.Add($"{p.Id}/{c.Id} ends {c.End:0.0} s > mix {m.Duration:0.0} s");
                }
                r.Check("real: every narrated path has a mashup mix, and every cue ends inside it", orphan.Count == 0 && late.Count == 0,
                    string.Join("; ", orphan.Select(o => "no mashup " + o).Concat(late).Take(6)));
            }
            List<NarrationCue> empty = cues.Where(c => c.FileExists && new FileInfo(c.AbsoluteFile).Length <= 44).ToList();
            r.Check("real: no cue WAV is empty", empty.Count == 0, empty.Count > 0 ? $"first {empty[0].AbsoluteFile}" : "");
            r.Note($"real: {n.Paths.Count} narrated paths, {cues.Count} cues, {cues.Sum(c => c.Seconds):0} s spoken");
        }

        // ------------------------------------------------------------------ helpers

        static string Json(Report report)
        {
            StringBuilder b = new();
            b.Append("{\n  \"passed\": ").Append(report.Checks.Count - report.Failures).Append(",\n  \"failed\": ").Append(report.Failures).Append(",\n  \"checks\": [\n");
            for (int i = 0; i < report.Checks.Count; i++)
            {
                (string name, bool ok, string detail) = report.Checks[i];
                b.Append("    {\"name\": ").Append(Q(name)).Append(", \"ok\": ").Append(ok ? "true" : "false").Append(", \"detail\": ").Append(Q(detail)).Append('}');
                b.Append(i + 1 < report.Checks.Count ? ",\n" : "\n");
            }
            b.Append("  ]\n}\n");
            return b.ToString();
        }

        static string Q(string s)
        {
            StringBuilder b = new("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (ch < 0x20) b.Append("\\u").Append(((int)ch).ToString("x4"));
                        else b.Append(ch);
                        break;
                }
            }
            return b.Append('"').ToString();
        }

        static string? Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
