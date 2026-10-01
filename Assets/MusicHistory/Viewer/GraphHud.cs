#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// Screen overlay: legend + controls (top left), the focused song's card (top right: title,
    /// artist · year, key · BPM and its chord progression as a ring, <see cref="ChordRingView"/> —
    /// the song's main loop, or while a mix or a duet loop plays the progression heard, with a hand
    /// at the music's place) or a clicked edge's card, and the walkthrough panel (bottom). For
    /// identity lineages (DESIGN.md §8b) edges read "Shares: &lt;identity&gt; · family of N songs"
    /// (plus "strong match (z …)"), never bits; the lineage and the shared identities are on the
    /// edge card (click an edge), not on the song card.
    /// These panels are not clickable (raycastTarget off); the canvas's GraphicRaycaster serves the
    /// featured-paths panel (<see cref="FeaturedPathsPanel"/>), whose rects join
    /// <see cref="PanelScreenRects"/> through <see cref="ExtraPanels"/>. All strings from the
    /// database are shown inside noparse tags; the database never holds lyrics.
    /// </summary>
    public sealed class GraphHud : MonoBehaviour
    {
        sealed class Panel
        {
            public GameObject Root = null!;
            public RectTransform Rect = null!;
            public TextMeshProUGUI Text = null!;
            public float Width;
        }

        const float Padding = 14f;
        static readonly Color PanelColor = new(.035f, .04f, .05f, .80f);
        public const string Muted = "#9aa3ad";

        Canvas canvas = null!;
        Panel legend = null!, info = null!, tour = null!;
        // The song card inside the info panel: title, artist · year, key · BPM, the chord ring.
        RectTransform card = null!;
        TextMeshProUGUI cardTitle = null!, cardArtist = null!, cardKey = null!;
        ChordRingView ring = null!;
        SongNode? cardSong;
        string cardSummary = "";
        object? ringSource;
        int ringPhrase = -1, ringCurrent = -2;
        /// <summary>Side of the chord ring on the song card (reference px).</summary>
        public const float RingSize = 188f;
        const char Newline = '\n';
        RectTransform progressFill = null!;
        Image progressFillImage = null!;
        SongGraphLoader loader = null!;
        string legendBody = "";

        public bool HelpVisible { get; private set; }
        /// <summary>
        /// What the info panel says: the song card as plain text ("title", "artist · year",
        /// "key · BPM BPM", "loop: I V vi IV") or the edge card's text.
        /// </summary>
        public string InfoText => SongCardVisible ? cardSummary : info.Text.text;
        /// <summary>The song card (title, artist, key · BPM, chord ring) is showing.</summary>
        public bool SongCardVisible => card != null && card.gameObject.activeSelf && info.Root.activeSelf;
        /// <summary>The song the card shows (null: none, or the edge card shows).</summary>
        public SongNode? CardSong => SongCardVisible ? cardSong : null;
        /// <summary>The song card's chord ring.</summary>
        public ChordRingView Ring => ring;
        /// <summary>The song / edge card is showing (InfoText keeps the last text while hidden).</summary>
        public bool InfoVisible => info.Root.activeSelf;
        public string TourText => tour.Text.text;
        public string LegendText => legend.Text.text;
        /// <summary>The legend panel is showing (the featured-paths list replaces it while open).</summary>
        public bool LegendVisible => legend.Root.activeSelf;
        /// <summary>The walkthrough panel at the bottom is showing.</summary>
        public bool TourVisible => tour.Root.activeSelf;
        public Canvas Canvas => canvas;
        /// <summary>The legend panel (top left; <see cref="ViewerLayout"/> moves it in the vertical layout).</summary>
        public RectTransform LegendRect => legend.Rect;
        /// <summary>The song info / edge card panel (top right).</summary>
        public RectTransform InfoRect => info.Rect;
        /// <summary>More panels on this canvas that world labels keep clear of (point-anchored, active ones count).</summary>
        [NonSerialized] public Func<IReadOnlyList<RectTransform>>? ExtraPanels;
        bool legendHidden;

        public void Build(SongGraphLoader owner)
        {
            loader = owner;
            GameObject canvasObject = new("MusicHistory HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 300;
            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            canvasScaler = scaler;

            legend = CreatePanel("Legend", new Vector2(0, 1), new Vector2(0, 1), new Vector2(20, -20), 660, TextAlignmentOptions.TopLeft, 17);
            info = CreatePanel("Song Info", new Vector2(1, 1), new Vector2(1, 1), new Vector2(-20, -20), 540, TextAlignmentOptions.TopLeft, 19);
            BuildCard();
            tour = CreatePanel("Walkthrough", new Vector2(.5f, 0), new Vector2(.5f, 0), new Vector2(0, 16), 1240, TextAlignmentOptions.TopLeft, 19);

            GameObject back = new("Progress", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            back.transform.SetParent(tour.Rect, false);
            Image backImage = back.GetComponent<Image>();
            backImage.color = new Color(1, 1, 1, .12f);
            backImage.raycastTarget = false;
            RectTransform backRect = back.GetComponent<RectTransform>();
            backRect.anchorMin = new Vector2(0, 0);
            backRect.anchorMax = new Vector2(1, 0);
            backRect.pivot = new Vector2(.5f, 0);
            backRect.offsetMin = new Vector2(Padding, 8);
            backRect.offsetMax = new Vector2(-Padding, 13);

            GameObject fill = new("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            fill.transform.SetParent(backRect, false);
            progressFillImage = fill.GetComponent<Image>();
            progressFillImage.color = new Color(.95f, .96f, .98f, .9f);
            progressFillImage.raycastTarget = false;
            progressFill = fill.GetComponent<RectTransform>();
            progressFill.anchorMin = Vector2.zero;
            progressFill.anchorMax = new Vector2(0, 1);
            progressFill.offsetMin = Vector2.zero;
            progressFill.offsetMax = Vector2.zero;

            SetText(info, "");
            SetText(tour, "");
        }

        Panel CreatePanel(string panelName, Vector2 anchor, Vector2 pivot, Vector2 position, float width,
            TextAlignmentOptions alignment, float fontSize)
        {
            GameObject go = new(panelName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(canvas.transform, false);
            Image image = go.GetComponent<Image>();
            image.color = PanelColor;
            image.raycastTarget = false;
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(width, 80);

            GameObject textObject = new("Text", typeof(RectTransform), typeof(CanvasRenderer));
            textObject.transform.SetParent(go.transform, false);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.color = new Color(.93f, .94f, .96f, 1f);
            text.alignment = alignment;
            text.richText = true;
            text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.lineSpacing = 4;
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(Padding, Padding);
            textRect.offsetMax = new Vector2(-Padding, -Padding);
            return new Panel { Root = go, Rect = rect, Text = text, Width = width };
        }

        static void SetText(Panel panel, string text, float extraBottom = 0)
        {
            bool show = !string.IsNullOrEmpty(text);
            if (panel.Root.activeSelf != show) panel.Root.SetActive(show);
            if (!show) return;
            if (panel.Text.text != text) panel.Text.text = text;
            Vector2 preferred = panel.Text.GetPreferredValues(text, panel.Width - 2 * Padding, 0);
            panel.Rect.sizeDelta = new Vector2(panel.Width, Mathf.Ceil(preferred.y) + 2 * Padding + extraBottom);
        }

        public static string Esc(string? s) =>
            string.IsNullOrEmpty(s) ? "" : "<noparse>" + s!.Replace("</noparse>", "") + "</noparse>";

        public void SetLegend(string body)
        {
            legendBody = body;
            RefreshLegend();
        }

        public void ToggleHelp()
        {
            HelpVisible = !HelpVisible;
            RefreshLegend();
        }

        /// <summary>Hides the legend (the featured-paths list takes its place) or shows it again.</summary>
        public void SetLegendVisible(bool visible)
        {
            if (legendHidden == !visible) return;
            legendHidden = !visible;
            RefreshLegend();
        }

        void RefreshLegend()
        {
            bool lineage = loader != null && loader.Data != null && loader.Data.IsIdentityLineage;
            string help = HelpVisible
                ? (lineage
                      ? $"\n<color={Muted}>Mouse</color>  hover shows shared identities · click selects a song or an edge · right-drag looks · wheel dollies"
                      : $"\n<color={Muted}>Mouse</color>  hover shows influences · click selects · right-drag looks · wheel dollies") +
                  $"\n<color={Muted}>Move</color>  W A S D, Q E (Shift = fast) · R reset view · L all labels · V secondary edges · F live layout (time locked) · T lyric themes" +
                  $"\n<color={Muted}>Walkthrough</color>  1 lineage · 2 subtree · 3 chronological{(lineage ? " · 4 family" : "")} · M next mode · Enter start · Space pause · N/→ next · B/← back · Esc exit · C compare in C / 120 BPM" +
                  $"\n<color={Muted}>Featured paths</color>  P opens the list (recording previews) · ↑↓ or 1–9 choose · Enter or click plays · Esc back · M melody graph (mashup mixes) · N captions on/off (narrated paths: on-screen text, no voiceover)" +
                  $"\n<color={Muted}>Duet loops</color>  K plays the selected path's duet loop (two voices at all times, no narration) · while a path plays, K switches between its duet loop and its narrated mix · ←/→ previous / next pair"
                : $"\n<color={Muted}>H</color> controls · <color={Muted}>P</color> featured paths";
            SetText(legend, legendHidden ? "" : legendBody + help);
        }

        /// <summary>The song card for <paramref name="node"/> (null hides the panel).</summary>
        public void ShowSong(SongNode? node)
        {
            if (node == null)
            {
                cardSong = null;
                UiKit.Show(card, false);
                SetText(info, "");
                return;
            }
            info.Text.text = "";
            if (!info.Root.activeSelf) info.Root.SetActive(true);
            UiKit.Show(card, true);
            if (!ReferenceEquals(cardSong, node)) FillCard(node);
            RefreshRing();
        }

        /// <summary>The edge card of a clicked edge.</summary>
        public void ShowEdge(InfluenceEdge edge)
        {
            cardSong = null;
            UiKit.Show(card, false);
            SetText(info, EdgeInfo(edge));
        }

        // ------------------------------------------------------------------ the song card

        void BuildCard()
        {
            card = UiKit.Rect("Song Card", info.Rect);
            UiKit.Place(card, 0, 0, info.Width, RingSize + 2 * Padding);
            cardTitle = UiKit.Text("Title", card, 24, new Color(.96f, .97f, .98f, 1f), TextAlignmentOptions.TopLeft, bold: true, wrap: true);
            cardArtist = UiKit.Text("Artist", card, 17, new Color(.70f, .73f, .77f, 1f), TextAlignmentOptions.TopLeft);
            cardKey = UiKit.Text("Key", card, 17, new Color(.93f, .94f, .96f, 1f), TextAlignmentOptions.TopLeft);
            ring = new ChordRingView(card, RingSize);
            UiKit.Place(ring.Root, info.Width - Padding - RingSize + 6f, Padding - 6f, RingSize, RingSize);
            UiKit.Show(card, false);
        }

        void FillCard(SongNode node)
        {
            cardSong = node;
            SongRecord s = node.Song;
            List<RingChord> loop = MainLoop.Parse(s.MainLoop);
            bool hasRing = loop.Count > 0;
            // The ring's column is always kept: a song without a stored loop still shows the progression heard while it plays.
            float textWidth = info.Width - 2 * Padding - RingSize - 4f;
            cardTitle.text = Esc(s.Title);
            float titleH = Mathf.Ceil(Mathf.Min(cardTitle.GetPreferredValues(cardTitle.text, textWidth, 0).y, 3 * 24 * 1.25f));
            string bpm = SongPalette.Invariant(s.NativeBpm, "0.#");
            cardArtist.text = $"{Esc(s.Artist)} · {s.Year}";
            cardKey.text = $"{Esc(s.KeyName)} · {bpm} BPM";
            float y = Padding + 2f;
            UiKit.Place(cardTitle.rectTransform, Padding + 2f, y, textWidth, titleH);
            y += titleH + 6f;
            UiKit.Place(cardArtist.rectTransform, Padding + 2f, y, textWidth, 22f);
            y += 26f;
            UiKit.Place(cardKey.rectTransform, Padding + 2f, y, textWidth, 22f);
            y += 22f + Padding;
            float height = Mathf.Max(y, RingSize + 2 * Padding - 12f);
            card.sizeDelta = new Vector2(info.Width, height);
            info.Rect.sizeDelta = new Vector2(info.Width, height);
            UiKit.Show(ring.Root, hasRing);
            ringSource = null;
            ringPhrase = -1;
            StringBuilder b = new();
            b.Append(s.Title).Append(Newline).Append(s.Artist).Append(" · ").Append(s.Year).Append(Newline)
                .Append(s.KeyName).Append(" · ").Append(bpm).Append(" BPM");
            if (hasRing)
            {
                b.Append(Newline).Append("loop:");
                foreach (RingChord c in loop) b.Append(' ').Append(c.Roman);
            }
            cardSummary = b.ToString();
        }

        void LateUpdate()
        {
            if (SongCardVisible) RefreshRing();
        }

        /// <summary>
        /// The ring shows the progression heard while a mix or a duet loop plays (the instrumental's
        /// phrase; a duet's bed over its current phrase) with the hand at the music's place, else the
        /// card song's main loop. Refilled only when its source changes; the hand moves every frame.
        /// </summary>
        public void RefreshRing()
        {
            if (cardSong == null || ring == null) return;
            Walkthrough.WalkthroughDirector? d = loader != null ? loader.Director : null;
            if (d != null && d.CurrentDuet is Playback.DuetLoop l)
            {
                double phrase = Math.Max(1, Math.Min(l.PhraseBeats > 0 ? l.PhraseBeats : 32, l.LoopBeats > 0 ? l.LoopBeats : 32));
                double beat = l.BeatAt(d.LoopSeconds);
                int k = (int)Math.Floor(beat / phrase);
                if (!ReferenceEquals(ringSource, l) || ringPhrase != k)
                {
                    ringSource = l;
                    ringPhrase = k;
                    ring.Set(ChordRingView.Window(l.Chords, k * phrase, phrase), phrase, "", "BED", l);
                    ringCurrent = -2;
                }
                Point(beat - k * phrase, "BED");
                return;
            }
            if (d != null && d.CurrentMashup is Playback.Mashup m && d.CurrentSegment is Playback.MashupSegment g && g.InstrumentalSong >= 0)
            {
                List<Playback.MashupChord> chords = m.Songs[g.InstrumentalSong].Chords;
                if (!ReferenceEquals(ringSource, chords))
                {
                    ringSource = chords;
                    ringPhrase = -1;
                    ring.Set(ChordRingView.Window(chords, 0, m.PhraseBeats), m.PhraseBeats, "", "HEARD", chords);
                    ringCurrent = -2;
                }
                Point(m.PhraseBeatAt(d.MixSeconds), "HEARD");
                return;
            }
            if (!ReferenceEquals(ringSource, cardSong))
            {
                ringSource = cardSong;
                ringPhrase = -1;
                List<RingChord> loop = MainLoop.Parse(cardSong.Song.MainLoop);
                ring.Set(loop, 0, loop.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "CHORDS", cardSong);
                UiKit.Show(ring.Root, loop.Count > 0);
                ring.SetPointer(null);
                ringCurrent = -2;
            }
        }

        /// <summary>The hand at <paramref name="position"/>; the centre names the chord under it (text changes only when it does).</summary>
        void Point(double position, string note)
        {
            UiKit.Show(ring.Root, true);
            ring.SetPointer(position);
            if (ring.Current == ringCurrent) return;
            ringCurrent = ring.Current;
            string roman = ringCurrent >= 0 ? ring.Chords[ringCurrent].Roman : "";
            ring.SetCenter(roman.Length > 0 ? Esc(roman) : "·", note);
        }

        /// <summary>Colour of strong-match highlights in the HUD and legend.</summary>
        public const string StrongColor = "#fff27a";

        /// <summary>
        /// Identity lineages (DESIGN.md §8b): "Shares: &lt;identity&gt; · family of N songs", plus
        /// "strong match (z …)" when the edge is flagged. Never bits: the score is a lineage
        /// score, not borrowing evidence.
        /// </summary>
        public static string SharesText(SongGraphData data, EdgeRecord e, string identityColor) =>
            SharesText(data.Family(e.FamilyId), e.Evidence, e, identityColor);

        /// <summary>As above, for a family (with or without the edge that carries it).</summary>
        public static string SharesText(IdentityFamily? family, string? label, EdgeRecord? e, string identityColor)
        {
            StringBuilder b = new();
            b.Append($"<color={Muted}>Shares:</color> <color={identityColor}>{Esc(family?.Label ?? label ?? "")}</color>");
            if (family != null)
                b.Append($" <color={Muted}>· family of {family.Members.Count} song{(family.Members.Count == 1 ? "" : "s")}</color>");
            if (e != null && e.IsStrongMatch && e.Z > 0)
                b.Append($" · <color={StrongColor}><b>strong match</b> (z {SongPalette.Invariant(e.Z, "0.0")})</color>");
            else if ((e != null && e.IsStrongMatch) || (e == null && family != null && family.IsStrong))
                b.Append($" · <color={StrongColor}><b>strong match</b></color>");
            return b.ToString();
        }

        /// <summary>'loop', 'progression schema', 'named schema', 'exact shared passage'.</summary>
        public static string KindText(string kind) => kind switch
        {
            "loop" => "repeating chord loop",
            "schema" => "named chord schema",
            "progression" => "chord progression schema",
            "strong" => "exact shared passage",
            _ => kind
        };

        string EdgeInfo(InfluenceEdge edge)
        {
            SongGraphData data = loader.Data!;
            EdgeRecord e = edge.Record;
            SongRecord a = edge.Source.Song, c = edge.Target.Song;
            string color = SongPalette.ToHex(SongPalette.ChannelColor(edge.Channel));
            StringBuilder b = new();
            b.Append($"<size=125%><b>{Esc(a.Title)}</b> <color={color}>→</color> <b>{Esc(c.Title)}</b></size>\n");
            b.Append($"{Esc(a.Artist)} · {a.Year}  <color={Muted}>→</color>  {Esc(c.Artist)} · {c.Year}\n");
            if (data.IsIdentityLineage)
            {
                b.Append(SharesText(data, e, color)).Append('\n');
                IdentityFamily? family = data.Family(e.FamilyId);
                if (family != null)
                {
                    b.Append($"<color={Muted}>{KindText(family.Kind)}");
                    if (!string.IsNullOrEmpty(family.Roman) && !family.Label.Contains(family.Roman!)) b.Append($" · {Esc(family.Roman)}");
                    SongRecord first = data.Song(family.Members[0].NodeId);
                    if (family.Members.Count > 2) b.Append($" · earliest song {Esc(first.Title)} ({first.Year})");
                    b.Append("</color>\n");
                }
                b.Append($"<color={Muted}>{(e.IsTree ? "Tree edge: the earlier song is the later one's lineage parent" : "Secondary edge: another strong sharer")}" +
                         " · a shared musical identity, not proven copying</color>");
                if (family != null)
                    b.Append($"\n<color={Muted}>Walkthrough 4 (family) + Enter plays " +
                             (family.Members.Count == 2 ? "both songs, earlier first" : $"all {family.Members.Count} songs in time order") + "</color>");
            }
            else
            {
                b.Append($"<color={Muted}>Via</color> <color={color}>{SongPalette.ChannelLabel(edge.Channel)}</color>" +
                         $" · {SongPalette.Invariant(e.ScoreBits, "0")} bits · z {SongPalette.Invariant(e.Z, "0.0")} · {(e.IsTree ? "tree" : "secondary")} edge");
                if (!string.IsNullOrEmpty(e.Evidence)) b.Append($"\n<size=90%><color={Muted}>{Esc(e.Evidence)}</color></size>");
            }
            return b.ToString();
        }

        public void ShowTour(string? text, float progress, Color barColor)
        {
            SetText(tour, text ?? "", 10);
            if (string.IsNullOrEmpty(text)) return;
            progressFill.anchorMax = new Vector2(Mathf.Clamp01(progress), 1);
            progressFillImage.color = barColor;
        }

        /// <summary>"Root (1950) › … › Parent (1962) › <b>This</b>".</summary>
        public static string Lineage(SongNode node)
        {
            List<SongNode> chain = new();
            for (SongNode? n = node; n != null && chain.Count < 512; n = n.TreeParent) chain.Add(n);
            chain.Reverse();
            if (chain.Count == 1) return "root (no earlier influencer)";
            List<string> parts = new();
            for (int i = 0; i < chain.Count; i++)
            {
                if (chain.Count > 6 && i == 1)
                {
                    parts.Add("…");
                    i = chain.Count - 5;
                }
                SongNode n = chain[i];
                parts.Add(n == node ? $"<b>{Esc(n.Song.Title)}</b>" : $"{Esc(n.Song.Title)} ({n.Song.Year})");
            }
            return string.Join(" › ", parts);
        }

        readonly List<Rect> panelRects = new();
        CanvasScaler? canvasScaler;

        /// <summary>
        /// Screen rects (pixels, origin bottom-left) the visible panels cover on a screen the size
        /// of <paramref name="cam"/>'s target, from the panels' anchors and the CanvasScaler's
        /// scale-with-screen-size factor, so world labels can keep clear of them (LabelLayer.KeepClear).
        /// </summary>
        public IReadOnlyList<Rect> PanelScreenRects(Camera cam) => PanelScreenRects(cam.pixelWidth, cam.pixelHeight);

        /// <summary>As above, for a <paramref name="width"/> x <paramref name="height"/> pixel screen.</summary>
        public IReadOnlyList<Rect> PanelScreenRects(float width, float height)
        {
            panelRects.Clear();
            if (canvas == null || canvasScaler == null) return panelRects;
            float w = Mathf.Max(1, width), h = Mathf.Max(1, height);
            Vector2 reference = canvasScaler.referenceResolution;
            float scale = Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(w / reference.x, 2f), Mathf.Log(h / reference.y, 2f), canvasScaler.matchWidthOrHeight));
            AddPanelRect(legend.Rect, w, h, scale);
            AddPanelRect(info.Rect, w, h, scale);
            AddPanelRect(tour.Rect, w, h, scale);
            if (ExtraPanels != null)
            {
                IReadOnlyList<RectTransform> extra = ExtraPanels();
                for (int i = 0; i < extra.Count; i++) AddPanelRect(extra[i], w, h, scale);
            }
            return panelRects;
        }

        /// <summary>
        /// Canvas scale factor for a <paramref name="width"/> x <paramref name="height"/> screen
        /// (CanvasScaler scale-with-screen-size, as the panels are laid out).
        /// </summary>
        public float ScaleFor(float width, float height)
        {
            if (canvasScaler == null) return 1f;
            Vector2 reference = canvasScaler.referenceResolution;
            return Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(Mathf.Max(1, width) / reference.x, 2f), Mathf.Log(Mathf.Max(1, height) / reference.y, 2f), canvasScaler.matchWidthOrHeight));
        }

        /// <summary>Screen rect (pixels, origin bottom-left) of a point-anchored rect on this canvas.</summary>
        public Rect ScreenRect(RectTransform r, float width, float height)
        {
            float scale = ScaleFor(width, height);
            Vector2 size = r.sizeDelta * scale;
            Vector2 anchor = new(r.anchorMin.x * width, r.anchorMin.y * height);
            Vector2 min = anchor + r.anchoredPosition * scale - Vector2.Scale(r.pivot, size);
            return new Rect(min, size);
        }

        void AddPanelRect(RectTransform r, float w, float h, float scale)
        {
            if (r == null || !r.gameObject.activeInHierarchy) return;
            Vector2 size = r.sizeDelta * scale;
            Vector2 anchor = new(r.anchorMin.x * w, r.anchorMin.y * h);
            Vector2 min = anchor + r.anchoredPosition * scale - Vector2.Scale(r.pivot, size);
            panelRects.Add(new Rect(min, size));
        }

        /// <summary>Draws the HUD into <paramref name="cam"/> (screenshots) or back onto the screen (null).</summary>
        public void RenderInto(Camera? cam)
        {
            if (cam == null)
            {
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = null;
            }
            else
            {
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = cam;
                canvas.planeDistance = cam.nearClipPlane + .05f;
            }
            ForceUpdate();
        }

        public void ForceUpdate()
        {
            // Every text on the canvas (the featured-paths panel's too), for edit-mode captures.
            foreach (TMP_Text t in canvas.GetComponentsInChildren<TMP_Text>(false))
                t.ForceMeshUpdate();
            Canvas.ForceUpdateCanvases();
        }
    }
}
