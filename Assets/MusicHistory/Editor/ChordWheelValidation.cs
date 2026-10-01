#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MusicHistory.Playback;
using MusicHistory.Viewer;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.EditorTools
{
    /// <summary>
    /// Checks shared by the suites for the chord wheel, the chord timeline, the path's name and the
    /// highlighted bubbles: Resonance colour parity and the chord-name vocabulary (pure), the
    /// sounding chord lit with bloom while the others are muted (wheel and strip), the wheel's size,
    /// place and lack of a backdrop in both orientations, the path's name top left, and highlighted
    /// bubbles at three times their size with their collider, photo, label and edges following.
    /// </summary>
    public static partial class Validation
    {
        /// <summary>The song card's old ring: 188 px square, the ring itself 140 px across.</summary>
        const float OldRingSquare = 188f, OldRingDiameter = 140f;

        static bool Near(Color a, Color b, float eps = 2e-3f) =>
            Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps && Mathf.Abs(a.b - b.b) < eps;

        static float MaxChannel(Color c) => Mathf.Max(c.r, c.g, c.b);

        static string Rgb(Color c) => $"({c.r:0.###}, {c.g:0.###}, {c.b:0.###})";

        // ------------------------------------------------------------------ pure

        /// <summary>Colour parity with Resonance-2's TonalColorField, chord names, tonic inference, path names.</summary>
        static void ValidateChordVocabulary(Report r)
        {
            // Expected values worked out by hand from Resonance-2/Assets/TonalColorField.cs:
            // Chord = minor ? Lerp(Pitch, (.7,.06,.85), .55) * .82 : Pitch, Pitch by interval above the key.
            (string what, int root, string quality, int tonic, Color expected)[] cases =
            {
                ("C in C major (Key)", 0, "maj", 0, new Color(.06f, .16f, 1f)),
                ("F in C major (4th)", 5, "maj", 0, new Color(1f, .045f, .09f)),
                ("G in C major (5th)", 7, "maj", 0, new Color(.07f, 1f, .16f)),
                ("D in C major (2nd maj dom)", 2, "maj", 0, new Color(.58f, .16f, .9f)),
                ("Am in C major", 9, "min", 0, new Color(.59614f, .06027f, .5863f)),
                ("Dm in C major", 2, "min", 0, new Color(.52972f, .0861f, .71545f)),
                ("Bdim in C major (dim is not 'minor' to Resonance)", 11, "dim", 0, new Color(.24f, .3f, .42f)),
                ("Am in A minor (Key)", 9, "min", 9, new Color(.33784f, .0861f, .75235f)),
                ("Dm in A minor (4th)", 2, "min", 9, new Color(.6847f, .043665f, .41656f)),
                ("E in A minor (5th)", 4, "maj", 9, new Color(.07f, 1f, .16f)),
                ("G in A minor (min 7th)", 7, "maj", 9, new Color(.35f, .18f, .5f)),
                ("F in A minor (min 6th)", 5, "maj", 9, new Color(.43f, .1f, .38f)),
            };
            List<string> bad = new();
            foreach ((string what, int root, string quality, int tonic, Color expected) in cases)
            {
                Color got = ChordPalette.Of(root, quality, tonic);
                if (!Near(got, expected) || !Mathf.Approximately(got.a, 1f)) bad.Add($"{what}: {Rgb(got)} vs {Rgb(expected)}");
            }
            r.Check("chord colours: Resonance-2 TonalColorField parity (I blue, IV red, V green, the rest by interval; minor pulled to violet ×.82; against the key's tonic, A for A minor)",
                bad.Count == 0 && ChordPalette.MinorColour("min") && !ChordPalette.MinorColour("maj") && !ChordPalette.MinorColour("dim") &&
                Mathf.Approximately(ChordPalette.Chord(9, 0, true).a, .82f), bad.Count == 0 ? $"{cases.Length} chords" : string.Join(" | ", bad));
            MashupChord am = new() { RootPc = 9, Quality = "min", Roman = "vi" };
            r.Check("chord colours: the melody graph's strip and the wheel share one palette",
                MelodyGraphPanel.ChordColor(am, 0) == ChordPalette.Of(9, "min", 0) && new RingChord(0, 1, 9, "min", "vi").ColorIn(0) == ChordPalette.Of(am, 0));
            Color full = ChordPalette.Of(7, "maj", 0), muted = ChordPalette.Muted(full);
            float Sat(Color c) => Mathf.Max(c.r, c.g, c.b) - Mathf.Min(c.r, c.g, c.b);
            r.Check("chord colours: a muted chord is darker and less saturated than its full colour, its hue kept",
                Mathf.Max(muted.r, muted.g, muted.b) < .6f * Mathf.Max(full.r, full.g, full.b) && Sat(muted) < .5f * Sat(full) && muted.g > muted.r && muted.g > muted.b,
                $"{Rgb(full)} → {Rgb(muted)}");

            (int root, string quality, int tonic, string expected)[] names =
            {
                (0, "maj", 0, "Key"), (1, "maj", 0, "Neapolitan"), (2, "maj", 0, "2nd Maj Dom"), (2, "min", 0, "2nd m"), (3, "maj", 0, "Minor 3rd"),
                (4, "min", 0, "Major 3rd m"), (5, "maj", 0, "Fourth"), (6, "maj", 0, "Tritone"), (7, "maj", 0, "Fifth"), (8, "maj", 0, "Minor 6th"),
                (9, "min", 0, "Major 6th m"), (10, "maj", 0, "Minor 7th"), (11, "dim", 0, "Major 7th °"),
                (9, "min", 9, "Key m"), (2, "min", 9, "Fourth m"), (4, "maj", 9, "Fifth"), (0, "maj", 9, "Minor 3rd"), (5, "maj", 9, "Minor 6th"),
                (7, "maj", 9, "Minor 7th"), (11, "dim", 9, "2nd °"), (7, "sus", 0, "Fifth sus"), (0, "aug", 0, "Key +"),
            };
            List<string> wrongNames = new();
            foreach ((int root, string quality, int tonic, string expected) in names)
            {
                string got = ChordNames.Plain(ChordNames.Name(root, tonic, quality), ChordNames.Suffix(quality));
                if (got != expected) wrongNames.Add($"{root}/{quality} in {tonic}: '{got}' vs '{expected}'");
            }
            r.Check("chord names: by the root's interval above the key's tonic ('Key', 'Neapolitan', '2nd Maj Dom', 'Minor 3rd', 'Fifth' ...), the quality as a small suffix (m, °, +, sus)",
                wrongNames.Count == 0, wrongNames.Count == 0 ? $"{names.Length} chords" : string.Join(" | ", wrongNames));
            r.Check("chord names: the short forms for narrow places",
                ChordNames.ShortName(7, 0) == "5th" && ChordNames.ShortName(1, 0) == "Neap" && ChordNames.ShortName(3, 0) == "min3" && ChordNames.ShortName(9, 9) == "Key");

            List<MashupChord> Chords(params (int root, string quality, string roman)[] list) =>
                list.Select(x => new MashupChord { RootPc = x.root, Quality = x.quality, Roman = x.roman }).ToList();
            int layla = ChordKey.TonicOf(Chords((9, "min", "i"), (2, "min", "iv"), (7, "maj", "bVII"), (5, "maj", "bVI")), "D minor");
            int wannabe = ChordKey.TonicOf(Chords((0, "maj", "I"), (9, "min", "vi"), (5, "maj", "IV"), (7, "maj", "V")), "C major");
            int fallback = ChordKey.TonicOf(Array.Empty<MashupChord>(), "E minor");
            r.Check("chord key: the tonic the roman numerals imply (a minor song's 'i' on A → A; 'I' on C → C), else the key name's mode",
                layla == 9 && wannabe == 0 && fallback == 9 && ChordKey.TonicOf(null, "G major") == 0 && ChordKey.ForMode(true) == 9,
                $"Layla {layla}, Wannabe {wannabe}, 'E minor' without chords {fallback}");

            (string id, string expected)[] titles =
            {
                ("aeolian-rock", "Aeolian Rock"), ("rock-and-roll-twelve-bars", "Rock and Roll Twelve Bars"),
                ("summer-place-to-barbie-girl", "Summer Place to Barbie Girl"), ("axis-lauper-to-capaldi", "Axis Lauper to Capaldi")
            };
            List<string> wrongTitles = titles.Where(x => PathCatalog.TitleCase(x.id) != x.expected).Select(x => $"{x.id} → '{PathCatalog.TitleCase(x.id)}'").ToList();
            FeaturedPath named = new() { Id = "aeolian-rock", Name = "Aeolian Rock" }, unnamed = new() { Id = "doo-wop-changes" };
            r.Check("path names: paths.json 'name' when given, else the id in title case (small words lower case inside)",
                wrongTitles.Count == 0 && named.DisplayName == "Aeolian Rock" && unnamed.DisplayName == "Doo Wop Changes", string.Join(" | ", wrongTitles));
        }

        // ------------------------------------------------------------------ in a scene

        /// <summary>
        /// While a mix or a duet loop plays: on the wheel the chord under the hand keeps its full
        /// colour, grows and is lit (HDR glow on the wheel's own bloom camera), every other chord is
        /// muted, and the middle names it; on the strip the sounding chord's cell keeps its colour
        /// with a glow, the others muted. <paramref name="pixels"/>: also compare captures with and
        /// without the wheel's bloom around the lit arc.
        /// </summary>
        static void CheckLitChords(Report r, string L, SongGraphLoader loader, Camera cam, int width, int height, bool pixels)
        {
            GraphHud hud = loader.Hud;
            hud.RefreshRing();
            hud.ForceUpdate();
            ChordRingView ring = hud.Ring;
            int cur = ring.Current, n = ring.Chords.Count, tonic = ring.TonicPc;
            bool colours = cur >= 0 && ring.DrawnColor(cur) == ring.Chords[cur].ColorIn(tonic) &&
                           Enumerable.Range(0, n).Where(i => i != cur).All(i => ring.DrawnColor(i) == ChordPalette.Muted(ring.Chords[i].ColorIn(tonic)));
            (float inner, float outer) = cur >= 0 ? ring.RadiiOf(cur) : (0f, 0f);
            r.Check($"{L}: the wheel lights the chord sounding (full colour, larger arc, named in the middle) and mutes every other chord",
                ring.Visible && n > 0 && colours && outer > ring.OuterRadius && inner < ring.InnerRadius && cur >= 0 && ring.CenterText == ring.Names[cur],
                cur >= 0 ? $"chord {cur + 1}/{n} '{ring.Names[cur]}' {Rgb(ring.DrawnColor(cur))}; centre '{ring.CenterText}'" : $"no current chord of {n}");
            MelodyLightRig? bloom = ring.Bloom;
            int wheelBit = 1 << MelodyLightRig.WheelLayer, melodyBit = 1 << MelodyLightRig.Layer;
            UnityEngine.Rendering.Universal.UniversalAdditionalCameraData? data = bloom != null
                ? UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(bloom.Camera) : null;
            r.Check($"{L}: the wheel's lit chord is an HDR glow (> 1) on its own bloom camera and volume (layer {MelodyLightRig.WheelLayer}), unseen by the view camera and the melody graph's rig",
                bloom != null && bloom.Active && ring.GlowLevel > 1f && bloom.GlowsShown == 1 && bloom.Camera.cullingMask == wheelBit && data != null && data.renderPostProcessing &&
                (data.volumeLayerMask.value & wheelBit) != 0 && (data.volumeLayerMask.value & melodyBit) == 0 && bloom.Volume.gameObject.layer == MelodyLightRig.WheelLayer &&
                (cam.cullingMask & wheelBit) == 0 && bloom.Image.texture == bloom.Texture && bloom.Texture != null && bloom.Image.gameObject.activeInHierarchy &&
                (loader.MelodyGraph?.Lights == null || (loader.MelodyGraph.Lights.Camera.cullingMask & wheelBit) == 0),
                bloom != null ? $"glow {ring.GlowLevel:0.##}, bloom {bloom.Bloom.intensity.value:0.##}, texture {bloom.Texture?.width}x{bloom.Texture?.height}" : "no wheel rig");

            MelodyGraphPanel? graph = loader.MelodyGraph;
            if (graph != null && graph.Showing)
            {
                graph.Refresh();
                MashupChord? lit = graph.LitChord;
                IReadOnlyList<MelodyGraphPanel.StripCell> cells = graph.StripCells;
                List<MelodyGraphPanel.StripCell> on = cells.Where(c => c.Lit).ToList();
                bool cellColours = lit != null && on.Count >= 1 && on.All(c => ReferenceEquals(c.Chord, lit) && c.Color == ChordPalette.Of(lit, graph.StripTonic)) &&
                                   cells.Where(c => !c.Lit).All(c => c.Color == ChordPalette.Muted(ChordPalette.Of(c.Chord, graph.StripTonic)));
                HashSet<string> vocabulary = new();
                foreach (MelodyGraphPanel.StripCell c in cells)
                {
                    string suffix = ChordNames.Suffix(c.Chord.Quality);
                    vocabulary.Add(ChordNames.Plain(ChordNames.Name(c.Chord.RootPc, graph.StripTonic, c.Chord.Quality), suffix));
                    vocabulary.Add(ChordNames.Plain(ChordNames.ShortName(c.Chord.RootPc, graph.StripTonic), suffix));
                }
                string[] labels = graph.ChordText.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
                MelodyLightRig? rig = graph.Lights;
                r.Check($"{L}: the chord timeline lights the chord sounding (full colour, a glow above the bloom threshold) and mutes the others; cells are named 'Key', 'Fifth' …",
                    cellColours && labels.Length > 0 && labels.All(vocabulary.Contains) && rig != null && lit != null && rig.GlowIntensityOf(0) * MaxChannel(ChordPalette.Of(lit, graph.StripTonic)) > rig.BloomThreshold && rig.GlowsShown >= 1,
                    $"lit {(lit != null ? ChordNames.Label(lit, graph.StripTonic) : "none")} ({on.Count} cell(s) of {cells.Count}); glow {rig?.GlowIntensityOf(0):0.##} (bloom threshold {rig?.BloomThreshold:0.##}); names {string.Join(" | ", labels.Take(6))}");
            }

            if (!pixels || bloom == null || cur < 0) return;
            // The lit arc's light: the pixels just outside its middle are brighter with the wheel's bloom than without.
            Rect wr = hud.ScreenRect(hud.WheelRect, width, height);
            float scale = hud.ScaleFor(width, height);
            (float a0, float a1) = ring.Arcs[cur];
            float midDeg = (a0 + a1) * .5f;
            if (a1 - a0 > ChordRingView.MaxGlowDegrees && !float.IsNaN(ring.PointerDegrees))
                midDeg = Mathf.Clamp(ring.PointerDegrees, a0 + ChordRingView.MaxGlowDegrees * .5f, a1 - ChordRingView.MaxGlowDegrees * .5f);
            float mid = midDeg * Mathf.Deg2Rad;
            Vector2 dir = new(Mathf.Sin(mid), -Mathf.Cos(mid));
            Vector2 centre = new(ring.Size * .5f, ring.Size * .5f);
            List<Vector2> samples = new();
            for (float d = outer + 6f; d <= outer + 22f; d += 4f)
                for (float t = -6f; t <= 6f; t += 3f)
                {
                    Vector2 p = centre + dir * d + new Vector2(-dir.y, dir.x) * t;
                    samples.Add(new Vector2(wr.xMin + p.x * scale, wr.yMax - p.y * scale));
                }
            float Mean(Color[] px) => samples.Average(s =>
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(s.x), 0, width - 1), y = Mathf.Clamp(Mathf.RoundToInt(s.y), 0, height - 1);
                Color c = px[y * width + x];
                return .299f * c.r + .587f * c.g + .114f * c.b;
            });
            float with = Mean(CapturePixels(cam, loader, width, height));
            float weight = bloom.Volume.weight, glow = ring.GlowIntensity;
            float without;
            try
            {
                bloom.Volume.weight = 0f;
                ring.GlowIntensity = 0f;
                without = Mean(CapturePixels(cam, loader, width, height));
            }
            finally
            {
                bloom.Volume.weight = weight;
                ring.GlowIntensity = glow;
                hud.ForceUpdate();
            }
            r.Check($"{L}: the lit chord blooms on screen: just outside its arc the capture is clearly brighter with the wheel's light than without",
                with - without > .03f, $"luminance {without:0.000} → {with:0.000}");
        }

        /// <summary>
        /// The wheel: 2.5 times the old ring (square and ring), no backdrop (no panel image behind it,
        /// only small marks), only chord names as text; top right in landscape, top centre in portrait
        /// (right of the legend while it shows), on screen.
        /// </summary>
        static void CheckWheelPlacement(Report r, string L, SongGraphLoader loader, int width, int height)
        {
            GraphHud hud = loader.Hud;
            ChordRingView ring = hud.Ring;
            RectTransform w = hud.WheelRect;
            float scale = hud.ScaleFor(width, height);
            Rect sr = hud.ScreenRect(w, width, height);
            bool vertical = ViewerLayout.FormatFor(width, height) == ScreenFormat.Vertical;
            bool size = Mathf.Abs(w.sizeDelta.x - 2.5f * OldRingSquare) < .01f && Mathf.Abs(w.sizeDelta.y - 2.5f * OldRingSquare) < .01f &&
                        2f * ring.OuterRadius >= 2.5f * OldRingDiameter - .5f;
            float area = w.sizeDelta.x * w.sizeDelta.y;
            List<string> backdrops = w.GetComponentsInChildren<Image>(true)
                .Where(i => i.rectTransform.rect.width * i.rectTransform.rect.height > .03f * area)
                .Select(i => i.name).ToList();
            if (w.GetComponent<Graphic>() != null) backdrops.Add("the wheel itself");
            if (w.parent != hud.Canvas.transform) backdrops.Add("parent " + w.parent.name);
            string title = hud.CardSong != null ? hud.CardSong.Song.Title : "";
            string artist = hud.CardSong != null ? hud.CardSong.Song.Artist : "";
            List<string> texts = w.GetComponentsInChildren<TMP_Text>(false).Select(t => t.text).ToList();
            bool noCardText = texts.All(t => (title.Length == 0 || !t.Contains(title)) && (artist.Length == 0 || !t.Contains(artist)) && !t.Contains("BPM"));
            bool place;
            string where;
            if (vertical)
            {
                bool legend = hud.LegendVisible;
                place = legend
                    ? sr.xMin >= hud.ScreenRect(hud.LegendRect, width, height).xMax - .5f && sr.xMax <= width + .5f
                    : Mathf.Abs(sr.center.x - width * .5f) <= 1f;
                place &= Mathf.Abs(sr.yMax - (height - ViewerLayout.VerticalPanelTop * scale)) <= 1f;
                where = $"portrait{(legend ? " (legend showing)" : "")}: centre x {sr.center.x:0.0} of {width}, top {height - sr.yMax:0.0} px from the top";
            }
            else
            {
                place = Mathf.Abs(sr.xMax - (width - ViewerLayout.Margin * scale)) <= 1f && Mathf.Abs(sr.yMax - (height - ViewerLayout.Margin * scale)) <= 1f;
                where = $"landscape: right {width - sr.xMax:0.0} px, top {height - sr.yMax:0.0} px from the edges";
            }
            r.Check($"{L}: the chord wheel is 2.5x the old ring, floats with no backdrop and no song text, {(vertical ? "top centre" : "top right")}",
                size && backdrops.Count == 0 && noCardText && place && sr.xMin >= -.5f && sr.yMin >= -.5f && sr.xMax <= width + .5f && sr.yMax <= height + .5f,
                $"{w.sizeDelta.x:0}x{w.sizeDelta.y:0} ref px (ring Ø {2 * ring.OuterRadius:0}); {where}; backdrops [{string.Join(", ", backdrops)}]; texts [{string.Join(" | ", texts.Take(8))}]");
            // Names inside their arcs never overlap.
            List<string> clash = new();
            foreach ((int chord, float half, string text) in ring.PlacedLabels)
            {
                (float a0, float a1) = ring.Arcs[chord];
                float midDeg = (a0 + a1) * .5f;
                if (midDeg - half < a0 - .01f || midDeg + half > a1 + .01f) clash.Add($"'{text}' wider than its arc");
            }
            r.Check($"{L}: every chord name sits inside its own arc (no overlaps; narrow arcs shortened or unnamed)",
                clash.Count == 0 && ring.PlacedLabels.Count > 0, $"{ring.PlacedLabels.Count} names of {ring.Chords.Count} chords: {ring.LabelText}; {string.Join(" | ", clash)}");
        }

        /// <summary>The playing path's name, large and outlined, top left (the legend hidden), clear of every panel.</summary>
        static void CheckPathTitle(Report r, string L, SongGraphLoader loader, FeaturedPath path, int width, int height)
        {
            GraphHud hud = loader.Hud;
            hud.ForceUpdate();
            float scale = hud.ScaleFor(width, height);
            Rect sr = hud.ScreenRect(hud.PathTitleRect, width, height);
            IReadOnlyList<Rect> rects = hud.PanelScreenRects(width, height);
            bool clear = rects.Where(x => x != sr).All(x => !x.Overlaps(sr));
            TMP_Text t = hud.PathTitleLabel;
            r.Check($"{L}: the path's name '{path.DisplayName}' shows top left in large outlined type (no box); the legend makes way",
                hud.PathTitleVisible && hud.PathTitleText == path.DisplayName && !hud.LegendVisible && t.fontSize >= ViewerLayout.PathTitleMin &&
                t.fontSharedMaterial == UiKit.OutlinedMaterial && t.GetComponent<Image>() == null &&
                Mathf.Abs(sr.xMin - ViewerLayout.Margin * scale) <= 1f && Mathf.Abs(sr.yMax - (height - ViewerLayout.PathTitleTop * scale)) <= 1f && clear && sr.xMax <= width,
                $"'{hud.PathTitleText}' at {t.fontSize:0} px ref; [{sr.xMin:0},{sr.yMin:0} {sr.width:0}x{sr.height:0}]; clear of {rects.Count - 1} panels: {clear}");
        }

        /// <summary>
        /// The tour's lit songs (<paramref name="lit"/>) at three times their size, smoothly; their
        /// collider, photo, label anchor and edge insets follow; every other bubble its own size; the
        /// camera frames the enlarged bubbles on screen.
        /// </summary>
        static void CheckHighlightedBubbles(Report r, string L, SongGraphLoader loader, Camera cam, IReadOnlyCollection<SongNode> lit, int width, int height)
        {
            SongNode.SettleScales();
            List<string> bad = new();
            foreach (SongNode n in lit)
            {
                float k = SongNode.HighlightScale;
                SphereCollider? sc = n.GetComponent<SphereCollider>();
                // In the graph's frame (the node's own scale times its children's).
                float s = n.transform.localScale.x;
                float collider = sc != null ? sc.radius * s : 0f;
                float bubble = n.BubbleRenderer.transform.localScale.x * s;
                bool photo = n.PhotoRenderer == null || n.PhotoRenderer.transform.parent == n.transform && Mathf.Abs(s - k) < 1e-4f;
                bool label = n.Label == null || Mathf.Abs(n.Label.AnchorRadius - n.Radius * k) < 1e-4f;
                // Edges leave from the enlarged bubble's rim (where the two bubbles are well apart).
                bool edges = n.Outgoing.Where(e => e.Shown && e.Renderer.enabled &&
                        Vector3.Distance(e.Target.transform.position, n.transform.position) > 2f * (n.DisplayRadius + e.Target.DisplayRadius))
                    .All(e => Vector3.Distance(e.transform.position, n.transform.position) >= n.DisplayRadius - 1e-3f);
                if (!n.Highlighted || Mathf.Abs(n.Scale - k) > 1e-4f || Mathf.Abs(n.DisplayRadius - n.Radius * k) > 1e-4f ||
                    Mathf.Abs(collider - n.Radius * k) > 1e-3f || Mathf.Abs(bubble - 2f * n.Radius * k) > 1e-3f || !photo || !label || !edges)
                    bad.Add($"'{n.Song.Title}': scale {n.Scale:0.###}, collider {collider:0.###}/{n.Radius * k:0.###}, bubble {bubble:0.###}, photo {photo}, label {label}, edges {edges}");
            }
            int others = loader.Nodes.Count(x => !lit.Contains(x) && (x.Highlighted || Mathf.Abs(x.Scale - 1f) > 1e-4f));
            r.Check($"{L}: the lit songs' bubbles are 3x (collider, photo, glow, label anchor and edge ends follow); every other bubble its own size",
                lit.Count > 0 && bad.Count == 0 && others == 0, $"{lit.Count} lit; {others} others resized; {string.Join(" | ", bad.Take(3))}");

            // Smoothly, over ScaleSeconds, both ways.
            SongNode first = lit.First();
            first.SetHighlighted(false);
            SongNode.TickScales(SongNode.ScaleSeconds * .5f);
            float halfDown = first.Scale;
            SongNode.TickScales(SongNode.ScaleSeconds * .6f);
            float down = first.Scale;
            first.SetHighlighted(true);
            SongNode.TickScales(SongNode.ScaleSeconds * .25f);
            float quarterUp = first.Scale;
            SongNode.SettleScales();
            r.Check($"{L}: a bubble eases (smoothstep, {SongNode.ScaleSeconds:0.0} s) from 3x back to its size and up again",
                halfDown > 1.5f && halfDown < 2.5f && Mathf.Abs(down - 1f) < 1e-4f && quarterUp > 1f && quarterUp < 1.6f && Mathf.Abs(first.Scale - 3f) < 1e-4f,
                $"half way down {halfDown:0.###}, done {down:0.###}, a quarter up {quarterUp:0.###}, settled {first.Scale:0.###}");

            // The camera frames them at their new size; their labels are drawn on screen.
            loader.RefreshView(cam);
            // Labels are placed in the camera's own pixels (the Game view's size outside a capture).
            float pw = cam.pixelWidth, ph = cam.pixelHeight;
            List<string> off = new();
            foreach (SongNode n in lit)
            {
                Vector3 c = cam.WorldToViewportPoint(n.transform.position);
                Vector3 e = cam.WorldToViewportPoint(n.transform.position + cam.transform.right * n.DisplayRadius);
                float rv = Mathf.Abs(e.x - c.x);
                if (c.z <= 0 || c.x - rv < 0f || c.x + rv > 1f || c.y - rv * width / height < 0f || c.y + rv * width / height > 1f) off.Add($"'{n.Song.Title}' bubble");
                if (n.Label != null && n.Label.Visible && n.Label.Placed &&
                    (n.Label.ScreenRect.xMin < -1f || n.Label.ScreenRect.xMax > pw + 1f || n.Label.ScreenRect.yMin < -1f || n.Label.ScreenRect.yMax > ph + 1f))
                    off.Add($"'{n.Song.Title}' label [{n.Label.ScreenRect.xMin:0},{n.Label.ScreenRect.yMin:0} {n.Label.ScreenRect.width:0}x{n.Label.ScreenRect.height:0}] in {pw:0}x{ph:0}");
            }
            r.Check($"{L}: the camera frames the enlarged bubbles on screen and their labels stay on screen", off.Count == 0, string.Join(", ", off));
        }

        /// <summary>Once no tour lights them, every bubble is back to its own size.</summary>
        static void CheckBubblesRestored(Report r, string L, SongGraphLoader loader)
        {
            SongNode.SettleScales();
            int big = loader.Nodes.Count(x => x.Highlighted || Mathf.Abs(x.Scale - 1f) > 1e-4f);
            r.Check($"{L}: after the tour every bubble is back to its own size", big == 0 && SongNode.ScalingCount == 0, $"{big} still enlarged");
        }
    }
}
