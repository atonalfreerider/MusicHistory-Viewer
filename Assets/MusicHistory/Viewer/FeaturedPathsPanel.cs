#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MusicHistory.Playback;
using MusicHistory.Walkthrough;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The featured paths (data/audio/renders/paths.json, <see cref="PathCatalog"/>) on the HUD canvas.
    ///
    /// Closed: a small "Featured paths" button at the top centre (P). Open: a list docked on the
    /// left in place of the legend, with the graph reframed into the space beside it. Each row shows
    /// the path's title, subtitle, duration, song count, era and a mini timeline of its songs;
    /// hovering a row (or selecting it with ↑↓ / 1–9) lights that path's songs and edges on the
    /// graph and dims the rest; a click, Enter or the Play button plays it. While a path plays, the
    /// list collapses into a now-playing strip at the bottom: path title, the steps as chips, "via
    /// &lt;identity&gt;" with a strong-match badge, the live key/BPM glide, a recording-preview badge
    /// and a time bar, with Prev / Pause / Next / Stop. Esc goes back one level (tour → list →
    /// closed). Uses an EventSystem with InputSystemUIInputModule (the project runs the new Input
    /// System only); navigation events are off so W A S D and Enter never drive the UI.
    ///
    /// A path with a mashup mix plays it as one continuous mix: the strip then names the segment
    /// ("Changeover: &lt;vocal song&gt; vocal over &lt;instrumental song&gt;", morph, full mix), the key
    /// and BPM heard and the chord match; the instrumental's chip is lit, the vocal's chip marked;
    /// the time bar covers the whole mix with its changeovers marked; the melody graph
    /// (<see cref="MelodyGraphPanel"/>) sits above the strip, toggled by M or the Melody button.
    /// </summary>
    public sealed class FeaturedPathsPanel : MonoBehaviour
    {
        public enum PanelState { Closed, List, Playing }

        // Layout, in reference pixels (1920x1080 canvas).
        public const float ListWidth = 620f;
        public const float Margin = 20f;
        const float HeaderHeight = 78f;
        const float RowHeight = 86f;
        const float RowGap = 4f;
        const float FooterHeight = 60f;
        public const float StripWidth = 1240f;
        public const float StripHeight = 206f;
        const float Pad = 20f;
        /// <summary>Row title size, and the smallest a long title may shrink to so it fits whole.</summary>
        public const float TitleSize = 20f, MinTitleSize = 15f;

        static readonly Color PanelColor = new(.03f, .035f, .045f, .9f);
        // The list and the strip are large: opaque, so the timeline's year labels (always drawn) never show through.
        static readonly Color SheetColor = new(.03f, .035f, .045f, 1f);
        static readonly Color TextColor = new(.94f, .95f, .97f, 1f);
        static readonly Color MutedColor = new(.60f, .64f, .68f, 1f);
        static readonly Color DimTextColor = new(.42f, .45f, .49f, 1f);
        static readonly Color RowHoverColor = new(1f, 1f, 1f, .045f);
        static readonly Color RowSelectedColor = new(1f, 1f, 1f, .085f);
        static readonly Color DividerColor = new(1f, 1f, 1f, .08f);
        static readonly Color RecordingColor = SongPalette.Hex("#43d17a");
        static readonly Color StrongColor = SongPalette.Hex(GraphHud.StrongColor);
        static readonly Color DefaultAccent = SongPalette.Hex("#ffcf4a");
        const string Muted = GraphHud.Muted;

        SongGraphLoader loader = null!;
        EventSystem? eventSystem;
        readonly List<FeaturedPath> paths = new();

        public PanelState State { get; private set; } = PanelState.Closed;
        public bool IsOpen => State == PanelState.List;
        /// <summary>The selected row (path index), -1 when there are no paths.</summary>
        public int Selected { get; private set; } = -1;
        /// <summary>The row under the pointer, -1 when none.</summary>
        public int Hovered { get; private set; } = -1;
        public IReadOnlyList<FeaturedPath> Paths => paths;
        public EventSystem? Events => eventSystem;
        /// <summary>Normalized screen area the graph is framed into while the list is open.</summary>
        /// <remarks>The right margin leaves room for the labels, which sit to the right of their songs.</remarks>
        public Rect FreeViewport => new((Margin + ListWidth + 24f) / 1920f, .07f, 1f - (Margin + ListWidth + 24f) / 1920f - .065f, .83f);
        /// <summary>A message shown under the list (why a path cannot play).</summary>
        public string Notice { get; private set; } = "";

        // ------------------------------------------------------------------ view objects

        RectTransform? root;
        // Toggle button (top centre).
        RectTransform button = null!;
        Image buttonBack = null!;
        TextMeshProUGUI buttonLabel = null!;
        Image buttonKeycap = null!;
        readonly List<Image> buttonIcon = new();
        // List.
        RectTransform list = null!;
        TextMeshProUGUI listTitle = null!, listSummary = null!;
        Button closeButton = null!;
        readonly List<RowView> rows = new();
        RectTransform rowsArea = null!;
        Image scrollTrack = null!, scrollThumb = null!;
        Image detailDivider = null!;
        TextMeshProUGUI detailIdentity = null!, detailDescription = null!, notice = null!;
        readonly List<TextMeshProUGUI> detailSteps = new();
        TextMeshProUGUI footerHint = null!;
        Button playButton = null!;
        Image playBack = null!;
        TextMeshProUGUI emptyText = null!;
        int firstRow;
        int visibleRows;
        // Tallest detail block of any path (-1 = not measured): the list keeps this much room, so
        // hovering rows never changes how many rows show or moves the Play button.
        float maxDetailHeight = -1f;
        int minYear = 1950, maxYear = 2020;
        // Now-playing strip.
        RectTransform strip = null!;
        TextMeshProUGUI stripKicker = null!, stripTitle = null!, stripVia = null!, stripReadout = null!, stripTime = null!;
        Image stripAccent = null!;
        Pill recordingPill = null!, stepPill = null!, strongPill = null!;
        readonly List<ChipView> chips = new();
        readonly List<TextMeshProUGUI> chipArrows = new();
        Image progressTrack = null!, progressFill = null!, glideMark = null!;
        Button prevButton = null!, pauseButton = null!, nextButton = null!, stopButton = null!, melodyButton = null!;
        TextMeshProUGUI pauseLabel = null!, melodyLabel = null!;
        Image melodyBack = null!;
        readonly List<Image> segmentMarks = new();

        // Camera pose the list set when it opened (restored to the full overview on close if untouched).
        Vector3 framedPosition;
        Quaternion framedRotation;
        bool framedByPanel;
        // A path started from the list returns to it when it ends; one started elsewhere closes.
        bool returnToList;

        /// <summary>Text of the list (titles, subtitles, durations, counts, eras), for validation.</summary>
        public string ListText
        {
            get
            {
                StringBuilder b = new();
                foreach (RowView r in rows)
                {
                    if (!r.gameObject.activeSelf) continue;
                    // Only what shows: the count column hides when the subtitle already says it.
                    b.Append(r.Title.text).Append(" | ").Append(r.Subtitle.text).Append(" | ").Append(r.Duration.text)
                        .Append(" | ").Append(r.Count.gameObject.activeSelf ? r.Count.text : "").Append(" | ").Append(r.Era.text).Append('\n');
                }
                b.Append(detailIdentity != null ? detailIdentity.text : "").Append('\n');
                b.Append(detailDescription != null ? detailDescription.text : "").Append('\n');
                foreach (TextMeshProUGUI t in detailSteps)
                    if (t.gameObject.activeSelf) b.Append(t.text).Append('\n');
                if (emptyText != null && emptyText.gameObject.activeSelf) b.Append(emptyText.text);
                return b.ToString();
            }
        }

        /// <summary>Text of the now-playing strip, for validation.</summary>
        public string NowPlayingText
        {
            get
            {
                if (strip == null) return "";
                StringBuilder b = new();
                b.Append(stripKicker.text).Append(" | ").Append(stripTitle.text).Append(" | ").Append(recordingPill.Label.text)
                    .Append(" | ").Append(stepPill.Label.text).Append('\n');
                foreach (ChipView c in chips)
                    if (c.gameObject.activeSelf) b.Append('[').Append(c.Label.text).Append("] ");
                b.Append('\n').Append(stripVia.text);
                if (strongPill.Root.gameObject.activeSelf) b.Append(" | ").Append(strongPill.Label.text);
                b.Append('\n').Append(stripReadout.text).Append('\n').Append(stripTime.text).Append(" | ").Append(pauseLabel.text);
                return b.ToString();
            }
        }

        public RectTransform? ListRect => list;
        public RectTransform? StripRect => strip;
        public RectTransform? ButtonRect => button;
        public bool ListVisible => list != null && list.gameObject.activeInHierarchy;
        public bool StripVisible => strip != null && strip.gameObject.activeInHierarchy;
        public Button? PathsButton => button != null ? button.GetComponent<Button>() : null;
        public Button? PlayButton => playButton;
        public Button? CloseButton => closeButton;
        public Button? StopButton => stopButton;
        public Button? NextButton => nextButton;
        public Button? PrevButton => prevButton;
        public Button? PauseButton => pauseButton;
        /// <summary>Shows or hides the melody graph while a mashup plays (M).</summary>
        public Button? MelodyButton => melodyButton;
        /// <summary>The row objects (index = row slot; <see cref="RowView.PathIndex"/> is the path).</summary>
        public IReadOnlyList<RowView> Rows => rows;
        public IReadOnlyList<ChipView> Chips => chips;
        /// <summary>Rows the list shows at once (the rest scroll).</summary>
        public int VisibleRowCount => visibleRows;

        WalkthroughDirector Director => loader.Director;

        // ------------------------------------------------------------------ build

        /// <summary>(Re)builds the panel for <paramref name="owner"/>'s catalog on its HUD canvas.</summary>
        public void Build(SongGraphLoader owner)
        {
            loader = owner;
            if (root != null) Discard(root.gameObject);
            rows.Clear();
            chips.Clear();
            chipArrows.Clear();
            segmentMarks.Clear();
            detailSteps.Clear();
            buttonIcon.Clear();
            paths.Clear();
            paths.AddRange(owner.Catalog.Paths);
            State = PanelState.Closed;
            Hovered = -1;
            Selected = paths.Count > 0 ? Math.Max(0, paths.FindIndex(p => p.IsPlayable)) : -1;
            firstRow = 0;
            maxDetailHeight = -1f;
            Notice = "";
            framedByPanel = false;
            ComputeYearRange();

            Canvas canvas = owner.Hud.Canvas;
            root = UiKit.Rect("Featured Paths", canvas.transform);
            UiKit.Fill(root);
            EnsureEventSystem();
            BuildButton();
            BuildList();
            BuildStrip();
            owner.Hud.ExtraPanels = VisibleRects;

            WalkthroughDirector d = owner.Director;
            d.KeyboardCaptured = () => State != PanelState.Closed;
            d.TourChanged -= OnTourChanged;
            d.TourChanged += OnTourChanged;
            ApplyState();
        }

        void OnDestroy()
        {
            if (loader != null && loader.Director != null) loader.Director.TourChanged -= OnTourChanged;
        }

        static void Discard(GameObject go)
        {
            if (Application.isPlaying) Destroy(go);
            else DestroyImmediate(go);
        }

        void ComputeYearRange()
        {
            int lo = int.MaxValue, hi = 0;
            foreach (FeaturedPath p in paths)
            {
                if (p.FirstYear > 0) lo = Math.Min(lo, p.FirstYear);
                hi = Math.Max(hi, p.LastYear);
            }
            if (lo == int.MaxValue) (lo, hi) = (1950, 2020);
            minYear = lo / 10 * 10;
            maxYear = Math.Max(minYear + 10, (hi + 9) / 10 * 10);
        }

        void EnsureEventSystem()
        {
            eventSystem = EventSystem.current != null ? EventSystem.current : FindAnyObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                GameObject go = new("EventSystem", typeof(EventSystem));
                go.transform.SetParent(transform, false);
                eventSystem = go.GetComponent<EventSystem>();
            }
            // The project runs the new Input System only: StandaloneInputModule would throw.
            StandaloneInputModule legacy = eventSystem.GetComponent<StandaloneInputModule>();
            if (legacy != null)
            {
                if (Application.isPlaying) Destroy(legacy);
                else DestroyImmediate(legacy);
            }
            if (eventSystem.GetComponent<InputSystemUIInputModule>() == null) eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            // Keys are handled here and by the walkthrough, never as UI navigation (W A S D, Enter).
            eventSystem.sendNavigationEvents = false;
        }

        void BuildButton()
        {
            button = UiKit.Rect("Paths Button", root!);
            button.anchorMin = button.anchorMax = new Vector2(.5f, 1f);
            button.pivot = new Vector2(.5f, 1f);
            button.anchoredPosition = new Vector2(0, -Margin);
            button.sizeDelta = new Vector2(252, 40);
            button.gameObject.AddComponent<CanvasRenderer>();
            buttonBack = button.gameObject.AddComponent<Image>();
            buttonBack.sprite = UiKit.Rounded(12);
            buttonBack.type = Image.Type.Sliced;
            buttonBack.color = PanelColor;
            Button b = button.gameObject.AddComponent<Button>();
            b.targetGraphic = buttonBack;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = b.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
            colors.pressedColor = new Color(.8f, .8f, .8f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, .5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = .08f;
            b.colors = colors;
            b.onClick.AddListener(() =>
            {
                Deselect();
                if (State == PanelState.Closed) Open();
                else if (State == PanelState.List) Close();
                else StopTour();
            });

            // Icon: three songs on one line, the last one lit (a path with its stops).
            RectTransform icon = UiKit.Rect("Icon", button);
            UiKit.Place(icon, 18, 14, 32, 12);
            Image link = UiKit.Image("Link", icon, UiKit.WithAlpha(TextColor, .45f), 1);
            UiKit.Place(link.rectTransform, 4, 5, 24, 2);
            buttonIcon.Add(link);
            for (int i = 0; i < 3; i++)
            {
                Image dot = UiKit.Dot("Song", icon, i == 2 ? DefaultAccent : TextColor);
                UiKit.Place(dot.rectTransform, i * 12f, 2f, 8, 8);
                buttonIcon.Add(dot);
            }

            buttonLabel = UiKit.Text("Label", button, 17, TextColor, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(buttonLabel.rectTransform, 58, 0, 150, 40);
            buttonLabel.text = "Featured paths";
            buttonKeycap = UiKit.Image("Keycap", button, new Color(1, 1, 1, .12f), 5);
            UiKit.Place(buttonKeycap.rectTransform, 216, 9, 22, 22);
            TextMeshProUGUI cap = UiKit.Text("Key", buttonKeycap.transform, 13, TextColor, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(cap.rectTransform);
            cap.text = "P";
        }

        void BuildList()
        {
            list = UiKit.Rect("Featured Paths List", root!);
            UiKit.Place(list, Margin, Margin, ListWidth, 600);
            list.gameObject.AddComponent<CanvasRenderer>();
            Image back = list.gameObject.AddComponent<Image>();
            back.sprite = UiKit.Rounded(14);
            back.type = Image.Type.Sliced;
            back.color = SheetColor;
            back.raycastTarget = true;   // blocks picking through the panel
            list.gameObject.AddComponent<ListScroll>().Panel = this;

            listTitle = UiKit.Text("Title", list, 25, TextColor, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(listTitle.rectTransform, Pad, 16, ListWidth - 2 * Pad - 50, 32);
            listTitle.text = "Featured paths";
            listSummary = UiKit.Text("Summary", list, 15, MutedColor);
            UiKit.Place(listSummary.rectTransform, Pad, 48, ListWidth - 2 * Pad - 50, 20);

            closeButton = MakeButton(list, "Close", "×", 22, new Color(1, 1, 1, .06f), TextColor, out _);
            UiKit.Place((RectTransform)closeButton.transform, ListWidth - Pad - 34, 18, 34, 34);
            closeButton.onClick.AddListener(() =>
            {
                Deselect();
                Close();
            });

            rowsArea = UiKit.Rect("Rows", list);
            scrollTrack = UiKit.Image("Scroll Track", list, new Color(1, 1, 1, .05f), 2);
            scrollThumb = UiKit.Image("Scroll Thumb", list, new Color(1, 1, 1, .25f), 2);
            for (int i = 0; i < paths.Count; i++) rows.Add(MakeRow(i));

            detailDivider = UiKit.Image("Divider", list, DividerColor);
            detailIdentity = UiKit.Text("Identity", list, 16, TextColor);
            detailDescription = UiKit.Text("Description", list, 16, new Color(.84f, .86f, .89f, 1f), TextAlignmentOptions.TopLeft, wrap: true);
            detailDescription.lineSpacing = 2;
            int maxSteps = 0;
            foreach (FeaturedPath p in paths) maxSteps = Math.Max(maxSteps, p.Steps.Count);
            for (int i = 0; i < Math.Min(12, maxSteps); i++)
                detailSteps.Add(UiKit.Text($"Step {i + 1}", list, 15, TextColor));
            notice = UiKit.Text("Notice", list, 14, StrongColor, TextAlignmentOptions.TopLeft, wrap: true);
            emptyText = UiKit.Text("Empty", list, 16, MutedColor, TextAlignmentOptions.TopLeft, wrap: true);

            footerHint = UiKit.Text("Hint", list, 14, MutedColor);
            playButton = MakeButton(list, "Play", "►  Play path", 16, DefaultAccent, new Color(.07f, .07f, .08f, 1f), out playBack);
            playButton.onClick.AddListener(() =>
            {
                Deselect();
                PlaySelected();
            });
        }

        RowView MakeRow(int pathIndex)
        {
            FeaturedPath p = paths[pathIndex];
            float w = ListWidth - 2 * 12f - 10f;   // leaves room for the scroll track
            RectTransform r = UiKit.Rect($"Row {pathIndex + 1}", rowsArea);
            r.gameObject.AddComponent<CanvasRenderer>();
            Image bg = r.gameObject.AddComponent<Image>();
            bg.sprite = UiKit.Rounded(10);
            bg.type = Image.Type.Sliced;
            bg.color = new Color(1, 1, 1, 0f);
            bg.raycastTarget = true;
            RowView row = r.gameObject.AddComponent<RowView>();
            row.Panel = this;
            row.PathIndex = pathIndex;
            row.Background = bg;
            row.Width = w;

            row.Accent = UiKit.Image("Accent", r, DefaultAccent, 2);
            UiKit.Place(row.Accent.rectTransform, 0, 14, 4, RowHeight - 28);
            row.Badge = UiKit.Image("Badge", r, new Color(1, 1, 1, .1f), 6);
            UiKit.Place(row.Badge.rectTransform, 14, 14, 26, 26);
            row.BadgeText = UiKit.Text("Number", row.Badge.transform, 14, TextColor, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(row.BadgeText.rectTransform);
            row.BadgeText.text = pathIndex < 9 ? (pathIndex + 1).ToString(CultureInfo.InvariantCulture) : "·";

            float left = 54f, right = 104f, duration = 64f;
            // The title line only shares its width with the duration ("2:29").
            row.TitleWidth = w - left - 14 - duration - 10;
            row.Title = UiKit.Text("Title", r, TitleSize, TextColor, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(row.Title.rectTransform, left, 11, row.TitleWidth, 28);
            row.Duration = UiKit.Text("Duration", r, 19, TextColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Duration.rectTransform, w - 14 - duration, 11, duration, 28);
            row.Subtitle = UiKit.Text("Subtitle", r, 15, MutedColor);
            UiKit.Place(row.Subtitle.rectTransform, left, 39, w - left - right - 8, 20);
            row.Count = UiKit.Text("Count", r, 14, MutedColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Count.rectTransform, w - right, 39, right - 14, 20);
            row.Era = UiKit.Text("Era", r, 14, MutedColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Era.rectTransform, w - right - 10, 60, right - 4, 20);

            // Mini timeline: decade ticks, the path's span and one dot per song at its year.
            row.Strip = UiKit.Rect("Strip", r);
            float stripWidth = w - left - right - 8;
            UiKit.Place(row.Strip, left, 64, stripWidth, 14);
            row.StripWidth = stripWidth;
            row.Track = UiKit.Image("Track", row.Strip, new Color(1, 1, 1, .12f));
            UiKit.Place(row.Track.rectTransform, 0, 6, stripWidth, 2);
            for (int y = minYear; y <= maxYear; y += 10)
            {
                Image tick = UiKit.Image($"Tick {y}", row.Strip, new Color(1, 1, 1, y % 50 == 0 ? .28f : .14f));
                UiKit.Place(tick.rectTransform, YearX(y, stripWidth) - .5f, y % 50 == 0 ? 2 : 4, 1, y % 50 == 0 ? 10 : 6);
            }
            row.Span = UiKit.Image("Span", row.Strip, DefaultAccent, 2);
            for (int i = 0; i < p.Steps.Count; i++)
            {
                Image dot = UiKit.Dot($"Song {i + 1}", row.Strip, TextColor);
                row.Dots.Add(dot);
            }

            row.Title.text = GraphHud.Esc(p.Title.Length > 0 ? p.Title : p.Id);
            // The font has no ellipsis glyph: a long title shrinks a little instead of losing letters.
            UiKit.FitWidth(row.Title, row.TitleWidth, TitleSize, MinTitleSize);
            // A path with a mashup mix plays it: its length is the mix's.
            Mashup? mix = loader.Director != null ? loader.Director.MashupFor(p) : null;
            row.Duration.text = PathCatalog.Clock(mix != null ? mix.Duration : p.Seconds > 0 ? p.Seconds : SumSeconds(p));
            row.Count.text = $"{p.Steps.Count} song{(p.Steps.Count == 1 ? "" : "s")}";
            row.Era.text = p.FirstYear > 0 ? (p.FirstYear == p.LastYear ? $"{p.FirstYear}" : $"{p.FirstYear}–{p.LastYear}") : "";
            string subtitle = p.Subtitle.Length > 0 ? p.Subtitle.Replace("->", "→") : $"{p.FirstYear} → {p.LastYear} · {p.Steps.Count} songs";
            if (!p.IsPlayable) subtitle = "not in this graph · " + subtitle;
            else if (mix != null) subtitle = "mashup mix · " + subtitle;
            else if (p.MissingRenders > 0) subtitle = $"{p.MissingRenders} render{(p.MissingRenders == 1 ? "" : "s")} missing (MIDI instead) · " + subtitle;
            row.Subtitle.text = GraphHud.Esc(subtitle);
            // paths.json subtitles read "1997 -> 2019 · 3 songs · …": the count column would repeat
            // it on the same line, so it hides and the subtitle takes the full width.
            bool countInSubtitle = System.Text.RegularExpressions.Regex.IsMatch(subtitle, $@"(^|[^0-9]){p.Steps.Count} songs?\b");
            UiKit.Show(row.Count, !countInSubtitle);
            if (countInSubtitle) UiKit.Place(row.Subtitle.rectTransform, left, 39, w - left - 14, 20);
            LayoutRowStrip(row, p);
            return row;
        }

        static double SumSeconds(FeaturedPath p)
        {
            double s = 0;
            foreach (PathStep step in p.Steps) s += step.Seconds;
            return s;
        }

        float YearX(float year, float width) => Mathf.Clamp01((year - minYear) / Mathf.Max(1f, maxYear - minYear)) * width;

        void LayoutRowStrip(RowView row, FeaturedPath p)
        {
            Color accent = AccentOf(p);
            float x0 = YearX(p.FirstYear, row.StripWidth), x1 = YearX(p.LastYear, row.StripWidth);
            UiKit.Place(row.Span.rectTransform, x0, 5, Mathf.Max(3f, x1 - x0), 4);
            row.Span.color = UiKit.WithAlpha(accent, .85f);
            // Songs of the same year sit side by side.
            Dictionary<int, int> sameYear = new();
            for (int i = 0; i < p.Steps.Count; i++)
            {
                PathStep s = p.Steps[i];
                int k = sameYear.TryGetValue(s.Year, out int n) ? n : 0;
                sameYear[s.Year] = k + 1;
                float x = YearX(s.Year, row.StripWidth) + k * 7f;
                Image dot = row.Dots[i];
                UiKit.Place(dot.rectTransform, x - 5f, 2f, 10f, 10f);
                dot.color = SongColor(s);
            }
        }

        Color SongColor(PathStep s)
        {
            if (loader.Data == null || s.NodeId < 1 || s.NodeId > loader.Data.Songs.Count) return MutedColor;
            SongRecord song = loader.Data.Song(s.NodeId);
            return SongPalette.KeyColor(song.TonicPc, song.Minor);
        }

        Color AccentOf(FeaturedPath p) => p.IsPlayable ? loader.RouteFor(p).Color : MutedColor;

        void BuildStrip()
        {
            strip = UiKit.Rect("Now Playing", root!);
            strip.anchorMin = strip.anchorMax = new Vector2(.5f, 0f);
            strip.pivot = new Vector2(.5f, 0f);
            strip.anchoredPosition = new Vector2(0, 16);
            strip.sizeDelta = new Vector2(StripWidth, StripHeight);
            strip.gameObject.AddComponent<CanvasRenderer>();
            Image back = strip.gameObject.AddComponent<Image>();
            back.sprite = UiKit.Rounded(14);
            back.type = Image.Type.Sliced;
            back.color = SheetColor;
            back.raycastTarget = true;

            // Grid: kicker + title with pills (top), step chips, via + time, time bar, key/BPM glide + controls.
            stripAccent = UiKit.Image("Accent", strip, DefaultAccent, 2);
            UiKit.Place(stripAccent.rectTransform, 0, 16, 4, 44);
            stripKicker = UiKit.Text("Kicker", strip, 12, DefaultAccent, TextAlignmentOptions.MidlineLeft, bold: true);
            stripKicker.characterSpacing = 6;
            UiKit.Place(stripKicker.rectTransform, Pad, 12, 600, 18);
            stripTitle = UiKit.Text("Title", strip, 24, TextColor, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(stripTitle.rectTransform, Pad, 30, 820, 32);

            stepPill = new Pill(strip, "Step", new Color(1, 1, 1, .1f), TextColor);
            recordingPill = new Pill(strip, "Source", UiKit.WithAlpha(RecordingColor, .2f), RecordingColor);

            int maxSteps = 0;
            foreach (FeaturedPath p in paths) maxSteps = Math.Max(maxSteps, p.Steps.Count);
            for (int i = 0; i < maxSteps; i++)
            {
                if (i > 0)
                {
                    TextMeshProUGUI arrow = UiKit.Text($"Arrow {i}", strip, 18, MutedColor, TextAlignmentOptions.Center);
                    arrow.text = "›";
                    chipArrows.Add(arrow);
                }
                RectTransform c = UiKit.Rect($"Chip {i + 1}", strip);
                c.gameObject.AddComponent<CanvasRenderer>();
                Image bg = c.gameObject.AddComponent<Image>();
                bg.sprite = UiKit.Rounded(9);
                bg.type = Image.Type.Sliced;
                bg.raycastTarget = true;
                ChipView chip = c.gameObject.AddComponent<ChipView>();
                chip.Panel = this;
                chip.Step = i;
                chip.Background = bg;
                chip.Label = UiKit.Text("Label", c, 14, TextColor, TextAlignmentOptions.MidlineLeft);
                UiKit.Fill(chip.Label.rectTransform);
                chip.Label.margin = new Vector4(12, 0, 10, 0);
                chips.Add(chip);
            }

            stripVia = UiKit.Text("Via", strip, 17, TextColor);
            UiKit.Place(stripVia.rectTransform, Pad, 114, 860, 26);
            strongPill = new Pill(strip, "Strong", UiKit.WithAlpha(StrongColor, .18f), StrongColor);
            stripReadout = UiKit.Text("Readout", strip, 16, TextColor, TextAlignmentOptions.MidlineLeft);
            UiKit.Place(stripReadout.rectTransform, Pad, 164, 740, 30);

            float buttonsWidth = 4 * 100 + 3 * 8;
            float bx = StripWidth - Pad - buttonsWidth;
            prevButton = MakeButton(strip, "Prev", "◄  Prev", 15, new Color(1, 1, 1, .08f), TextColor, out _);
            UiKit.Place((RectTransform)prevButton.transform, bx, 162, 100, 32);
            pauseButton = MakeButton(strip, "Pause", "II  Pause", 15, new Color(1, 1, 1, .08f), TextColor, out _);
            UiKit.Place((RectTransform)pauseButton.transform, bx + 108, 162, 100, 32);
            pauseLabel = pauseButton.GetComponentInChildren<TextMeshProUGUI>();
            nextButton = MakeButton(strip, "Next", "Next  ►", 15, new Color(1, 1, 1, .08f), TextColor, out _);
            UiKit.Place((RectTransform)nextButton.transform, bx + 216, 162, 100, 32);
            stopButton = MakeButton(strip, "Stop", "■  Stop", 15, new Color(1, 1, 1, .08f), TextColor, out _);
            UiKit.Place((RectTransform)stopButton.transform, bx + 324, 162, 100, 32);
            // Mashups only: the melody graph toggle, left of Prev.
            melodyButton = MakeButton(strip, "Melody", "Melody  M", 15, new Color(1, 1, 1, .08f), TextColor, out melodyBack);
            UiKit.Place((RectTransform)melodyButton.transform, bx - 128, 162, 120, 32);
            melodyLabel = melodyButton.GetComponentInChildren<TextMeshProUGUI>();
            melodyButton.onClick.AddListener(() =>
            {
                Deselect();
                ToggleMelody();
            });
            UiKit.Show(melodyButton, false);
            prevButton.onClick.AddListener(() => { Deselect(); if (Director.IsTouring) Director.Previous(); });
            nextButton.onClick.AddListener(() => { Deselect(); if (Director.IsTouring) Director.Next(); });
            pauseButton.onClick.AddListener(() => { Deselect(); PauseOrReplay(); });
            stopButton.onClick.AddListener(() => { Deselect(); StopTour(); });

            stripTime = UiKit.Text("Time", strip, 15, MutedColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(stripTime.rectTransform, StripWidth - Pad - 160, 114, 160, 26);
            // The time bar spans the strip under the via row; the tick marks where the glide ends.
            progressTrack = UiKit.Image("Progress", strip, new Color(1, 1, 1, .1f), 2);
            UiKit.Place(progressTrack.rectTransform, Pad, 147, StripWidth - 2 * Pad, 5);
            progressFill = UiKit.Image("Fill", progressTrack.transform, DefaultAccent, 2);
            UiKit.Place(progressFill.rectTransform, 0, 0, 0, 5);
            glideMark = UiKit.Image("Glide", progressTrack.transform, new Color(1, 1, 1, .45f));
            UiKit.Place(glideMark.rectTransform, 0, -3, 2, 11);
        }

        Button MakeButton(Transform parent, string name, string label, float size, Color back, Color text, out Image background)
        {
            RectTransform r = UiKit.Rect(name, parent);
            r.gameObject.AddComponent<CanvasRenderer>();
            background = r.gameObject.AddComponent<Image>();
            background.sprite = UiKit.Rounded(8);
            background.type = Image.Type.Sliced;
            background.color = back;
            Button b = r.gameObject.AddComponent<Button>();
            b.targetGraphic = background;
            b.navigation = new Navigation { mode = Navigation.Mode.None };
            ColorBlock colors = b.colors;
            // Hover brightens (translucent buttons get more opaque), press darkens; no multiplier at rest.
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.18f, 1.18f, 1.18f, 1.9f);
            colors.pressedColor = new Color(.85f, .85f, .85f, 1.4f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(1f, 1f, 1f, .45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = .08f;
            b.colors = colors;
            TextMeshProUGUI t = UiKit.Text("Label", r, size, text, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(t.rectTransform);
            t.text = label;
            return b;
        }

        void Deselect()
        {
            // A clicked button stays "selected" otherwise, and Submit (Enter) would click it again.
            if (eventSystem != null && eventSystem.currentSelectedGameObject != null) eventSystem.SetSelectedGameObject(null);
        }

        readonly List<RectTransform> visibleRects = new();

        /// <summary>The button, list and strip that are showing (labels keep clear of them; no per-frame allocation).</summary>
        IReadOnlyList<RectTransform> VisibleRects()
        {
            visibleRects.Clear();
            if (button != null && button.gameObject.activeInHierarchy) visibleRects.Add(button);
            if (list != null && list.gameObject.activeInHierarchy) visibleRects.Add(list);
            if (strip != null && strip.gameObject.activeInHierarchy) visibleRects.Add(strip);
            if (loader != null && loader.MelodyGraph != null && loader.MelodyGraph.Showing && loader.MelodyGraph.PanelRect is RectTransform melody)
                visibleRects.Add(melody);
            return visibleRects;
        }

        /// <summary>The pointer is over the button, the list or the strip (hover picking skips it).</summary>
        public bool ContainsScreenPoint(Vector2 screen)
        {
            if (root == null) return false;
            IReadOnlyList<RectTransform> rects = VisibleRects();
            for (int i = 0; i < rects.Count; i++)
            {
                // The melody graph has a canvas of its own (on the view camera).
                Canvas? canvas = rects[i].GetComponentInParent<Canvas>();
                Camera? cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                if (RectTransformUtility.RectangleContainsScreenPoint(rects[i], screen, cam)) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ state

        public void Toggle()
        {
            if (State == PanelState.Closed) Open();
            else if (State == PanelState.List) Close();
            else StopTour();
        }

        /// <summary>Shows the list (a running tour of another mode ends first) and frames the graph beside it.</summary>
        public void Open()
        {
            if (State == PanelState.List) return;
            if (Director.IsTouring)
            {
                bool wasPath = Director.Mode == TourMode.Path;
                Director.Exit();   // TourChanged: a path tour returns to the list by itself
                if (wasPath && State == PanelState.List) return;
            }
            State = PanelState.List;
            if (Selected < 0 && paths.Count > 0) Selected = 0;
            ApplyState();
            FrameGraph();
        }

        public void Close()
        {
            if (State == PanelState.Closed) return;
            if (Director.IsTouring && Director.Mode == TourMode.Path) Director.Exit();
            State = PanelState.Closed;
            Hovered = -1;
            ApplyState();
            RestoreCamera();
        }

        /// <summary>Selects row <paramref name="index"/> (clamped); its route lights up.</summary>
        public void Select(int index)
        {
            if (paths.Count == 0) return;
            Selected = Mathf.Clamp(index, 0, paths.Count - 1);
            Notice = "";
            ScrollToSelected();
            RefreshList();
            ApplyPreview();
        }

        public void MoveSelection(int delta)
        {
            if (paths.Count == 0) return;
            Select(Selected < 0 ? 0 : (Selected + delta + paths.Count) % paths.Count);
        }

        /// <summary>The pointer entered row <paramref name="index"/> (-1: left the rows).</summary>
        public void HoverRow(int index)
        {
            int next = index >= 0 && index < paths.Count ? index : -1;
            if (next == Hovered) return;
            Hovered = next;
            RefreshList();
            ApplyPreview();
        }

        public void ClickRow(int index)
        {
            if (index < 0 || index >= paths.Count) return;
            Select(index);
            PlaySelected();
        }

        /// <summary>Plays the selected path (a path tour); false when it cannot play.</summary>
        public bool PlaySelected()
        {
            if (Selected < 0 || Selected >= paths.Count) return false;
            FeaturedPath p = paths[Selected];
            if (!p.IsPlayable)
            {
                Notice = "This path's songs are not all in the loaded graph, so it cannot play here.";
                RefreshList();
                return false;
            }
            loader.Highlighter.SetRoutePreview(null);
            bool ok = Director.StartPathTour(p);
            if (!ok)
            {
                Notice = "The path could not start.";
                RefreshList();
                ApplyPreview();
            }
            return ok;
        }

        /// <summary>Ends the path tour and returns to the list.</summary>
        public void StopTour()
        {
            returnToList = true;
            if (Director.IsTouring) Director.Exit();
            else if (State == PanelState.Playing) OnTourChanged();
        }

        void PauseOrReplay()
        {
            if (!Director.IsTouring) return;
            if (Director.TourComplete) Director.GoTo(0);
            else Director.TogglePause();
            RefreshNowPlaying();
        }

        /// <summary>M or the Melody button: shows or hides the melody graph (only while a mashup plays).</summary>
        public bool ToggleMelody()
        {
            if (State != PanelState.Playing || Director.CurrentMashup == null || loader.MelodyGraph == null) return false;
            loader.MelodyGraph.Toggle();
            RefreshNowPlaying();
            return true;
        }

        /// <summary>A chip was clicked: jump to that step (it morphs from the song heard before).</summary>
        public void JumpTo(int step)
        {
            if (State != PanelState.Playing || !Director.IsTouring) return;
            Director.GoTo(step);
        }

        void OnTourChanged()
        {
            if (loader == null || root == null) return;
            WalkthroughDirector d = Director;
            if (d.IsTouring && d.Mode == TourMode.Path)
            {
                if (State != PanelState.Playing) returnToList = State == PanelState.List;
                State = PanelState.Playing;
                Hovered = -1;
                if (d.CurrentPath != null) Selected = Math.Max(0, paths.IndexOf(d.CurrentPath));
                framedByPanel = false;
                ApplyState();
                RefreshNowPlaying();
            }
            else if (State == PanelState.Playing)
            {
                // The path tour ended (Esc, Stop): back to the list, graph framed beside it.
                State = returnToList ? PanelState.List : PanelState.Closed;
                ApplyState();
                if (State == PanelState.List) FrameGraph();
            }
            else if (d.IsTouring && State == PanelState.List)
            {
                State = PanelState.Closed;
                ApplyState();
            }
        }

        void ApplyState()
        {
            if (root == null) return;
            bool listOpen = State == PanelState.List, playing = State == PanelState.Playing;
            UiKit.Show(list, listOpen);
            UiKit.Show(strip, playing);
            loader.Hud.SetLegendVisible(!listOpen);
            buttonBack.color = listOpen || playing ? new Color(.16f, .14f, .07f, .95f) : PanelColor;
            buttonKeycap.color = listOpen || playing ? UiKit.WithAlpha(DefaultAccent, .35f) : new Color(1, 1, 1, .12f);
            if (listOpen)
            {
                RefreshList();
                ApplyPreview();
            }
            else
            {
                if (!Director.IsTouring) loader.Highlighter.SetRoutePreview(null);
            }
            Director.RefreshHud();
        }

        void ApplyPreview()
        {
            if (State != PanelState.List) return;
            int index = Hovered >= 0 ? Hovered : Selected;
            GraphRoute? route = index >= 0 && index < paths.Count && paths[index].IsPlayable ? loader.RouteFor(paths[index]) : null;
            loader.Highlighter.SetRoutePreview(route);
        }

        void FrameGraph()
        {
            Camera? cam = loader.ViewCamera;
            if (cam == null) return;
            // The years the featured paths cover, beside the list (routes read larger than in the full overview).
            int first = int.MaxValue, last = 0;
            foreach (FeaturedPath p in paths)
            {
                if (!p.IsPlayable) continue;
                first = Math.Min(first, p.FirstYear);
                last = Math.Max(last, p.LastYear);
            }
            if (first <= last) loader.FrameYears(cam, first - 3, last + 3, FreeViewport);
            else loader.FrameOverview(cam);
            framedPosition = cam.transform.position;
            framedRotation = cam.transform.rotation;
            framedByPanel = true;
        }

        void RestoreCamera()
        {
            Camera? cam = loader.ViewCamera;
            if (cam == null || !framedByPanel) return;
            framedByPanel = false;
            // Untouched since the list framed it: frame the whole graph again for the full screen.
            if ((cam.transform.position - framedPosition).sqrMagnitude < 1e-6f && Quaternion.Angle(cam.transform.rotation, framedRotation) < .01f)
                loader.FrameOverview(cam);
        }

        void ScrollToSelected()
        {
            if (visibleRows <= 0 || Selected < 0) return;
            if (Selected < firstRow) firstRow = Selected;
            else if (Selected >= firstRow + visibleRows) firstRow = Selected - visibleRows + 1;
        }

        /// <summary>Scrolls the rows by <paramref name="delta"/> (mouse wheel).</summary>
        public void Scroll(int delta)
        {
            int max = Math.Max(0, paths.Count - visibleRows);
            int next = Mathf.Clamp(firstRow + delta, 0, max);
            if (next == firstRow) return;
            firstRow = next;
            RefreshList();
        }

        // ------------------------------------------------------------------ refresh: list

        /// <summary>Lays out and fills the list for the current selection, hover and scroll.</summary>
        public void RefreshList()
        {
            if (list == null) return;
            int playable = 0, mixes = 0;
            double total = 0;
            foreach (FeaturedPath p in paths)
            {
                if (p.IsPlayable) playable++;
                Mashup? mix = Director.MashupFor(p);
                if (mix != null) mixes++;
                total += mix != null ? mix.Duration : p.Seconds > 0 ? p.Seconds : SumSeconds(p);
            }
            UiKit.SetText(listSummary, paths.Count == 0
                ? "none yet"
                : $"{paths.Count} path{(paths.Count == 1 ? "" : "s")} · {PathCatalog.Clock(total)} of recording previews" +
                  (mixes > 0 ? $" · {mixes} mashup mix{(mixes == 1 ? "" : "es")}" : "") +
                  (playable < paths.Count ? $" · {paths.Count - playable} not in this graph" : ""));

            // Detail of the hovered row, else the selected one.
            int shown = Hovered >= 0 ? Hovered : Selected;
            FeaturedPath? detail = shown >= 0 && shown < paths.Count ? paths[shown] : null;
            float inner = ListWidth - 2 * Pad;
            // Room for the tallest detail, whichever row is shown: the rows and the footer stay put.
            float detailHeight = MaxDetailHeight(inner);
            float maxHeight = 1080f - 2 * Margin;
            float fixedHeight = HeaderHeight + 14 + detailHeight + FooterHeight;
            int fit = Mathf.Max(1, Mathf.FloorToInt((maxHeight - fixedHeight + RowGap) / (RowHeight + RowGap)));
            visibleRows = Math.Min(paths.Count, fit);
            firstRow = Mathf.Clamp(firstRow, 0, Math.Max(0, paths.Count - visibleRows));
            ScrollClamp();

            float y = HeaderHeight;
            UiKit.Place(rowsArea, 12, y, ListWidth - 24, visibleRows * (RowHeight + RowGap));
            for (int i = 0; i < rows.Count; i++)
            {
                RowView row = rows[i];
                bool visible = i >= firstRow && i < firstRow + visibleRows;
                UiKit.Show(row, visible);
                if (!visible) continue;
                UiKit.Place((RectTransform)row.transform, 0, (i - firstRow) * (RowHeight + RowGap), row.Width, RowHeight);
                StyleRow(row, i == Selected, i == Hovered);
            }
            bool scrolls = paths.Count > visibleRows;
            UiKit.Show(scrollTrack, scrolls);
            UiKit.Show(scrollThumb, scrolls);
            float rowsHeight = visibleRows * (RowHeight + RowGap) - (visibleRows > 0 ? RowGap : 0);
            if (scrolls)
            {
                UiKit.Place(scrollTrack.rectTransform, ListWidth - 10, y, 4, rowsHeight);
                float thumb = Mathf.Max(24f, rowsHeight * visibleRows / paths.Count);
                float at = (rowsHeight - thumb) * firstRow / Math.Max(1, paths.Count - visibleRows);
                UiKit.Place(scrollThumb.rectTransform, ListWidth - 10, y + at, 4, thumb);
            }
            y += Math.Max(0, rowsHeight) + 14;

            bool empty = paths.Count == 0;
            UiKit.Show(emptyText, empty);
            UiKit.Show(detailDivider, !empty);
            if (empty)
            {
                string source = loader.Catalog.SourcePath.Length > 0 ? loader.Catalog.SourcePath : "data/audio/renders/paths.json";
                emptyText.text = "No featured paths yet.\n" +
                                 $"<size=88%><color={Muted}>{GraphHud.Esc(loader.Catalog.Status)}. The path list comes from " +
                                 $"{GraphHud.Esc(System.IO.Path.GetFileName(source))} next to the recording renders " +
                                 "(tools/render_paths.py). The walkthrough modes 1–4 still work.</color></size>";
                float h = emptyText.GetPreferredValues(emptyText.text, inner, 0).y;
                UiKit.Place(emptyText.rectTransform, Pad, y - 4, inner, h + 4);
                y += h + 10;
            }
            else
            {
                UiKit.Place(detailDivider.rectTransform, Pad, y - 8, inner, 1);
                float detailTop = y + 6;
                y = Mathf.Max(FillDetail(detail, detailTop, inner), detailTop + detailHeight);
            }

            UiKit.Show(notice, Notice.Length > 0);
            if (Notice.Length > 0)
            {
                notice.text = GraphHud.Esc(Notice);
                float h = notice.GetPreferredValues(notice.text, inner, 0).y;
                UiKit.Place(notice.rectTransform, Pad, y, inner, h);
                y += h + 8;
            }

            // Footer: key hints and the Play button.
            y += 6;
            footerHint.text = empty
                ? $"<b><color=#e6e9ee>Esc</color></b> close"
                : $"<b><color=#e6e9ee>Enter</color></b> play   <b><color=#e6e9ee>↑↓</color></b> <b><color=#e6e9ee>1–{Math.Min(9, paths.Count)}</color></b> select   <b><color=#e6e9ee>Esc</color></b> close";
            UiKit.Place(footerHint.rectTransform, Pad, y, inner - 160, 36);
            UiKit.Show(playButton, !empty);
            bool canPlay = detail != null && Selected >= 0 && paths[Selected].IsPlayable;
            playButton.interactable = canPlay;
            playBack.color = canPlay ? AccentOf(paths[Selected]) : new Color(1, 1, 1, .12f);
            UiKit.Place((RectTransform)playButton.transform, ListWidth - Pad - 150, y, 150, 36);
            y += 36 + Pad;
            list.sizeDelta = new Vector2(ListWidth, Mathf.Min(maxHeight, y));
        }

        void ScrollClamp()
        {
            if (Selected >= 0 && visibleRows > 0)
            {
                if (Selected < firstRow) firstRow = Selected;
                else if (Selected >= firstRow + visibleRows) firstRow = Selected - visibleRows + 1;
            }
        }

        void StyleRow(RowView row, bool selected, bool hovered)
        {
            FeaturedPath p = paths[row.PathIndex];
            Color accent = AccentOf(p);
            row.Background.color = selected ? RowSelectedColor : hovered ? RowHoverColor : new Color(1, 1, 1, 0f);
            UiKit.Show(row.Accent, selected);
            row.Accent.color = accent;
            row.Badge.color = selected ? UiKit.WithAlpha(accent, .9f) : new Color(1, 1, 1, .1f);
            row.BadgeText.color = selected ? new Color(.06f, .06f, .07f, 1f) : TextColor;
            Color title = p.IsPlayable ? TextColor : DimTextColor;
            row.Title.color = title;
            row.Duration.color = p.IsPlayable ? TextColor : DimTextColor;
        }

        float MaxDetailHeight(float width)
        {
            if (maxDetailHeight >= 0) return maxDetailHeight;
            float h = 0;
            foreach (FeaturedPath p in paths) h = Mathf.Max(h, DetailHeight(p, width));
            return maxDetailHeight = h;
        }

        float DetailHeight(FeaturedPath? p, float width)
        {
            if (p == null) return 0;
            float h = 26;
            if (p.Description.Length > 0) h += detailDescription.GetPreferredValues(GraphHud.Esc(p.Description), width, 0).y + 10;
            h += Math.Min(detailSteps.Count, p.Steps.Count) * 22 + 4;
            return h;
        }

        float FillDetail(FeaturedPath? p, float y, float width)
        {
            UiKit.Show(detailIdentity, p != null);
            UiKit.Show(detailDescription, p != null && p.Description.Length > 0);
            if (p == null)
            {
                foreach (TextMeshProUGUI t in detailSteps) UiKit.Show(t, false);
                return y;
            }
            Color accent = AccentOf(p);
            int strong = p.StrongLinks;
            string identity = p.Identity.Length > 0 ? p.Identity : "shared musical identities";
            detailIdentity.text = $"<color={Muted}>Shares</color> <b><color={UiKit.Hex(accent)}>{GraphHud.Esc(identity)}</color></b>" +
                                  (strong > 0 ? $"   <color={GraphHud.StrongColor}>{strong} strong match{(strong == 1 ? "" : "es")}</color>" : "");
            UiKit.Place(detailIdentity.rectTransform, Pad, y, width, 22);
            y += 26;
            if (p.Description.Length > 0)
            {
                detailDescription.text = GraphHud.Esc(p.Description);
                float h = detailDescription.GetPreferredValues(detailDescription.text, width, 0).y;
                UiKit.Place(detailDescription.rectTransform, Pad, y, width, h + 2);
                y += h + 10;
            }
            for (int i = 0; i < detailSteps.Count; i++)
            {
                TextMeshProUGUI t = detailSteps[i];
                bool show = i < p.Steps.Count;
                UiKit.Show(t, show);
                if (!show) continue;
                PathStep s = p.Steps[i];
                string mark = s.Via != null && s.Via.Strong ? $" <color={GraphHud.StrongColor}>●</color>" : "";
                t.text = $"<color={Muted}>{s.Year}</color>   <b>{GraphHud.Esc(s.Title)}</b>{mark}  <color={Muted}>{GraphHud.Esc(s.Artist)}</color>";
                t.color = s.NodeId > 0 ? TextColor : DimTextColor;
                UiKit.Place(t.rectTransform, Pad, y, width, 22);
                y += 22;
            }
            return y + 4;
        }

        // ------------------------------------------------------------------ refresh: now playing

        /// <summary>Updates the now-playing strip from the walkthrough (called at 12 Hz while a path plays).</summary>
        public void RefreshNowPlaying()
        {
            if (strip == null || loader == null) return;
            WalkthroughDirector d = Director;
            FeaturedPath? p = d.CurrentPath;
            if (!d.IsTouring || p == null) return;
            if (State != PanelState.Playing) OnTourChanged();
            Color accent = AccentOf(p);
            if (d.CurrentMashup is Mashup mashup && d.CurrentSegment is MashupSegment segment)
            {
                RefreshMashup(d, p, mashup, segment, accent);
                return;
            }
            LayoutButtons(false);
            foreach (Image mark in segmentMarks) UiKit.Show(mark, false);
            UiKit.Place(stripReadout.rectTransform, Pad, 164, 740, 30);
            int step = d.StepIndex;
            WalkthroughDirector.StepReadout r = d.Readout();
            PathStep? s = step < p.Steps.Count ? p.Steps[step] : null;

            stripAccent.color = accent;
            stripKicker.color = accent;
            string state = d.TourComplete ? "PATH COMPLETE" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "PAUSED" : "NOW PLAYING";
            UiKit.SetText(stripKicker, $"{state} · FEATURED PATH");
            UiKit.SetText(stripTitle, GraphHud.Esc(p.Title));

            // Pills, right-aligned on the title row.
            float right = StripWidth - Pad;
            stepPill.Set($"STEP {step + 1} / {p.Steps.Count}");
            right = stepPill.PlaceRight(right, 22) - 8;
            if (r.Recording)
            {
                recordingPill.SetColors(UiKit.WithAlpha(RecordingColor, .2f), RecordingColor);
                recordingPill.Set("● RECORDING PREVIEW");
            }
            else
            {
                recordingPill.SetColors(new Color(1, 1, 1, .08f), MutedColor);
                recordingPill.Set(r.Player.StartsWith("silent") ? "SILENT CLOCK" : "MIDI SYNTH");
            }
            recordingPill.PlaceRight(right, 22);

            // Chips: every step; the playing one lit in the path's colour.
            float x = Pad, width = StripWidth - 2 * Pad, arrow = 22f;
            int n = p.Steps.Count;
            float chipWidth = (width - (n - 1) * arrow) / Math.Max(1, n);
            for (int i = 0; i < chips.Count; i++)
            {
                ChipView c = chips[i];
                bool on = i < n;
                UiKit.Show(c, on);
                if (i > 0 && i - 1 < chipArrows.Count) UiKit.Show(chipArrows[i - 1], on);
                if (!on) continue;
                if (i > 0)
                {
                    UiKit.Place(chipArrows[i - 1].rectTransform, x - arrow, 70, arrow, 34);
                }
                UiKit.Place((RectTransform)c.transform, x, 70, chipWidth, 34);
                PathStep ps = p.Steps[i];
                bool current = i == step, past = i < step;
                c.Background.color = current ? UiKit.WithAlpha(accent, .26f) : past ? new Color(1, 1, 1, .07f) : new Color(1, 1, 1, .035f);
                c.Label.color = current ? TextColor : past ? new Color(.8f, .82f, .85f, 1f) : MutedColor;
                UiKit.SetText(c.Label, $"<color={(current ? UiKit.Hex(accent) : Muted)}>{ps.Year}</color>  {(current ? "<b>" : "")}{GraphHud.Esc(ps.Title)}{(current ? "</b>" : "")}");
                x += chipWidth + arrow;
            }

            // Via row.
            if (s?.Via is PathVia via)
            {
                List<string> facts = new();
                if (via.EdgeKind.Length > 0) facts.Add($"{via.EdgeKind} edge");
                if (via.FamilySize > 0) facts.Add($"family of {via.FamilySize}");
                string extra = string.Join(" · ", facts);
                UiKit.SetText(stripVia, $"<color={Muted}>via</color>  <b><color={UiKit.Hex(accent)}>{GraphHud.Esc(via.Identity)}</color></b>" +
                                        (extra.Length > 0 ? $"  <size=85%><color={Muted}>{extra}</color></size>" : ""));
                UiKit.Show(strongPill.Root, via.Strong);
                if (via.Strong)
                {
                    strongPill.Set(via.Z > 0 ? $"STRONG MATCH · z {via.Z.ToString("0.0", CultureInfo.InvariantCulture)}" : "STRONG MATCH");
                    float viaWidth = stripVia.GetPreferredValues(stripVia.text, 2000, 0).x;
                    strongPill.PlaceLeft(Pad + Mathf.Min(viaWidth, 820) + 14, 116, 22);
                }
            }
            else
            {
                UiKit.SetText(stripVia, $"<color={Muted}>first song</color>  <b>plays in its own key and tempo</b>");
                UiKit.Show(strongPill.Root, false);
            }

            // Live key/BPM glide.
            if (r.Valid)
            {
                bool glides = r.Glide < .999 && (Math.Abs(r.StartSemitones) > 1e-6 || Math.Abs(r.StartBpm - r.Bpm) > .05);
                string key = r.StartKey == r.Key
                    ? $"<color={Muted}>Key</color> <b>{GraphHud.Esc(r.Key)}</b>"
                    : $"<color={Muted}>Key</color> {GraphHud.Esc(r.StartKey)} <color={Muted}>→</color> <b>{GraphHud.Esc(r.Key)}</b>" +
                      (glides ? $" <color={Muted}>(now {GraphHud.Esc(r.NowKey.Split(' ')[0])}, {Signed(r.NowSemitones)} st)</color>" : "");
                string bpm = Math.Abs(r.StartBpm - r.Bpm) < .05
                    ? $"<color={Muted}>BPM</color> <b>{F(r.Bpm, "0.#")}</b>"
                    : $"<color={Muted}>BPM</color> {F(r.StartBpm, "0.#")} <color={Muted}>→</color> <b>{F(r.Bpm, "0.#")}</b>" +
                      (glides ? $" <color={Muted}>(now {F(r.NowBpm, "0.0")})</color>" : "");
                string tag = glides ? $"<color={UiKit.Hex(accent)}>gliding</color>   " : "";
                UiKit.SetText(stripReadout, $"{tag}{key}   <color={Muted}>·</color>   {bpm}");
                UiKit.SetText(stripTime, r.InSeconds
                    ? $"{PathCatalog.Clock(r.Position)} / {PathCatalog.Clock(r.Length)}"
                    : $"beat {F(r.Position, "0")} / {F(r.Length, "0")}");
                float barWidth = progressTrack.rectTransform.sizeDelta.x;
                UiKit.Place(progressFill.rectTransform, 0, 0, Mathf.Max(5f, barWidth * (float)r.Progress), 5);
                progressFill.color = accent;
                // Where the glide ends on the time bar (recordings: morph_seconds).
                bool mark = r.InSeconds && s != null && s.Glides && r.Length > 0;
                UiKit.Show(glideMark, mark);
                if (mark) UiKit.Place(glideMark.rectTransform, barWidth * (float)Math.Min(1, s!.MorphSeconds / r.Length) - 1, -3, 2, 11);
            }
            UiKit.SetText(pauseLabel, d.TourComplete ? "►  Replay" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "►  Resume" : "II  Pause");
            prevButton.interactable = step > 0 || d.TourComplete;
            nextButton.interactable = step + 1 < p.Steps.Count;
        }

        /// <summary>The bottom-right buttons; a mashup adds the melody graph toggle left of Prev.</summary>
        void LayoutButtons(bool mashup)
        {
            UiKit.Show(melodyButton, mashup);
        }

        /// <summary>The now-playing strip for a mashup mix: the segment, what is heard, the chord match, the whole mix's time bar.</summary>
        void RefreshMashup(WalkthroughDirector d, FeaturedPath p, Mashup m, MashupSegment g, Color accent)
        {
            LayoutButtons(true);
            int step = d.StepIndex, vocal = d.VocalStepIndex, n = p.Steps.Count;
            double t = d.MixSeconds, length = d.MashupAudio != null ? d.MashupAudio.DurationSeconds : m.Duration;
            stripAccent.color = accent;
            stripKicker.color = accent;
            string state = d.TourComplete ? "PATH COMPLETE" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "PAUSED" : "NOW PLAYING";
            UiKit.SetText(stripKicker, $"{state} · FEATURED PATH · CONTINUOUS MIX");
            UiKit.SetText(stripTitle, GraphHud.Esc(p.Title));

            float right = StripWidth - Pad;
            stepPill.Set($"STEP {step + 1} / {n}");
            right = stepPill.PlaceRight(right, 22) - 8;
            MashupPlayer? player = d.MashupAudio;
            if (player != null && player.CurrentAudio != null)
            {
                recordingPill.SetColors(UiKit.WithAlpha(RecordingColor, .2f), RecordingColor);
                recordingPill.Set("● MASHUP MIX");
            }
            else
            {
                recordingPill.SetColors(new Color(1, 1, 1, .08f), MutedColor);
                recordingPill.Set(player != null && player.ClockSource == "loading" ? "LOADING MIX" : "MASHUP · SILENT CLOCK");
            }
            recordingPill.PlaceRight(right, 22);

            // Chips: the instrumental's song lit; during a changeover the vocal's song marked in its melody colour.
            float x = Pad, width = StripWidth - 2 * Pad, arrow = 22f;
            float chipWidth = (width - (n - 1) * arrow) / Math.Max(1, n);
            for (int i = 0; i < chips.Count; i++)
            {
                ChipView c = chips[i];
                bool on = i < n;
                UiKit.Show(c, on);
                if (i > 0 && i - 1 < chipArrows.Count) UiKit.Show(chipArrows[i - 1], on);
                if (!on) continue;
                if (i > 0) UiKit.Place(chipArrows[i - 1].rectTransform, x - arrow, 70, arrow, 34);
                UiKit.Place((RectTransform)c.transform, x, 70, chipWidth, 34);
                PathStep ps = p.Steps[i];
                bool current = i == step, singing = i == vocal, past = i < step;
                MashupSong? song = m.SongForStep(i);
                Color voice = song != null ? MelodyGraphPanel.BrightColor(song.Index) : accent;
                c.Background.color = current ? UiKit.WithAlpha(accent, .26f) : singing ? UiKit.WithAlpha(voice, .16f) : past ? new Color(1, 1, 1, .07f) : new Color(1, 1, 1, .035f);
                c.Label.color = current || singing ? TextColor : past ? new Color(.8f, .82f, .85f, 1f) : MutedColor;
                string tag = singing ? $"  <size=78%><color={UiKit.Hex(voice)}><b>VOCAL</b></color></size>"
                    : current && vocal >= 0 ? $"  <size=78%><color={UiKit.Hex(accent)}><b>BACKING</b></color></size>" : "";
                UiKit.SetText(c.Label, $"<color={(current ? UiKit.Hex(accent) : singing ? UiKit.Hex(voice) : Muted)}>{ps.Year}</color>  " +
                                       $"{(current || singing ? "<b>" : "")}{GraphHud.Esc(ps.Title)}{(current || singing ? "</b>" : "")}{tag}");
                x += chipWidth + arrow;
            }

            // What is heard now.
            string Song(int index) => index >= 0 && index < m.Songs.Count ? m.Songs[index].Title : "";
            string Colored(int index) => $"<b><color={UiKit.Hex(MelodyGraphPanel.BrightColor(index))}>{GraphHud.Esc(Song(index))}</color></b>";
            PathVia? via = vocal >= 0 && vocal < n ? p.Steps[vocal].Via : step < n ? p.Steps[step].Via : null;
            string identity = via != null && via.Identity.Length > 0
                ? $"   <size=85%><color={Muted}>via</color> <color={UiKit.Hex(accent)}>{GraphHud.Esc(via.Identity)}</color></size>" : "";
            string what = g.Kind switch
            {
                MashupSegmentKind.Changeover => $"<color={Muted}>Changeover</color>  {Colored(g.VocalSong)} <color={Muted}>vocal over</color> <b>{GraphHud.Esc(Song(g.InstrumentalSong))}</b>{identity}",
                MashupSegmentKind.Morph => $"<color={Muted}>Morph</color>  <b>{GraphHud.Esc(Song(g.InstrumentalSong))}</b> <color={Muted}>glides into its own key and tempo</color>{identity}",
                _ => $"<color={Muted}>Full mix</color>  {Colored(g.InstrumentalSong)}" +
                     $"  <color={Muted}>{(g.Index == 0 ? "the root song" : g.Index == m.Segments.Count - 1 ? "the last song, to the end" : "")}</color>"
            };
            UiKit.SetText(stripVia, what);
            bool strong = via != null && via.Strong && g.Kind != MashupSegmentKind.Full;
            UiKit.Show(strongPill.Root, strong);
            if (strong)
            {
                strongPill.Set(via!.Z > 0 ? $"STRONG MATCH · z {via.Z.ToString("0.0", CultureInfo.InvariantCulture)}" : "STRONG MATCH");
                float viaWidth = stripVia.GetPreferredValues(stripVia.text, 2000, 0).x;
                strongPill.PlaceLeft(Pad + Mathf.Min(viaWidth, 820) + 14, 116, 22);
            }

            // Key, tempo, chord match, the vocal's transposition, the beat alignment.
            List<string> parts = new();
            bool morphing = g.Kind == MashupSegmentKind.Morph && Math.Abs(g.BpmStart - g.Bpm) > .05;
            parts.Add($"<color={Muted}>Key</color> <b>{GraphHud.Esc(g.Key)}</b>");
            parts.Add(morphing
                ? $"<color={Muted}>BPM</color> {F(g.BpmStart, "0.#")} <color={Muted}>→</color> <b>{F(g.Bpm, "0.#")}</b> <color={Muted}>(now {F(g.BpmAt(t), "0.0")})</color>"
                : $"<color={Muted}>BPM</color> <b>{F(g.Bpm, "0.#")}</b>");
            if (g.ChordMatch is double cm) parts.Add($"<color={Muted}>chords match</color> <b>{MashupCatalog.Percent(cm)}</b>");
            if (g.Kind == MashupSegmentKind.Changeover && g.VocalShiftSemitones is int shift)
                parts.Add($"<color={Muted}>vocal</color> <b>{(shift > 0 ? "+" : "")}{shift} st</b>" +
                          (g.VocalTempoRatio is double ratio && Math.Abs(ratio - 1) > .005 ? $" <color={Muted}>×{F(ratio, "0.00")}</color>" : ""));
            if (g.BeatErrorMs is double ms) parts.Add($"<color={Muted}>beats ±{F(ms, "0")} ms</color>");
            float readoutWidth = StripWidth - Pad - 4 * 100 - 3 * 8 - 128 - Pad - 12;
            string readout = "";
            for (int k = parts.Count; k >= 1; k--)
            {
                readout = (morphing ? $"<color={UiKit.Hex(accent)}>gliding</color>   " : "") + string.Join($"   <color={Muted}>·</color>   ", parts.GetRange(0, k));
                if (stripReadout.GetPreferredValues(readout, 4000, 0).x <= readoutWidth) break;
            }
            UiKit.SetText(stripReadout, readout);
            UiKit.Place(stripReadout.rectTransform, Pad, 164, readoutWidth, 30);
            UiKit.SetText(stripTime, $"{PathCatalog.Clock(t)} / {PathCatalog.Clock(length)}");

            // The whole mix on the time bar; changeovers (white) and morphs (accent) marked above it.
            float barWidth = progressTrack.rectTransform.sizeDelta.x;
            UiKit.Place(progressFill.rectTransform, 0, 0, Mathf.Max(5f, barWidth * (float)(length > 0 ? Math.Min(1, t / length) : 0)), 5);
            progressFill.color = accent;
            UiKit.Show(glideMark, false);
            int marks = 0;
            foreach (MashupSegment s in m.Segments)
            {
                if (s.Kind == MashupSegmentKind.Full || length <= 0) continue;
                while (segmentMarks.Count <= marks) segmentMarks.Add(UiKit.Image($"Segment {segmentMarks.Count + 1}", progressTrack.transform, Color.white, 1));
                Image mark = segmentMarks[marks++];
                UiKit.Show(mark, true);
                float x0 = barWidth * (float)(s.Start / length), x1 = barWidth * (float)(Math.Min(length, s.End) / length);
                bool now = s.Index == g.Index;
                mark.color = s.Kind == MashupSegmentKind.Changeover ? new Color(1, 1, 1, now ? .75f : .32f) : UiKit.WithAlpha(accent, now ? .95f : .55f);
                UiKit.Place(mark.rectTransform, x0 + 1, -6, Mathf.Max(2f, x1 - x0 - 2), 3);
            }
            for (int k = marks; k < segmentMarks.Count; k++) UiKit.Show(segmentMarks[k], false);

            bool graph = loader.MelodyGraph != null && loader.MelodyGraph.UserVisible;
            melodyBack.color = graph ? UiKit.WithAlpha(accent, .3f) : new Color(1, 1, 1, .08f);
            UiKit.SetText(melodyLabel, graph ? "Melody  M" : $"<color={Muted}>Melody  M</color>");
            UiKit.SetText(pauseLabel, d.TourComplete ? "►  Replay" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "►  Resume" : "II  Pause");
            prevButton.interactable = t > .5 || d.TourComplete;
            nextButton.interactable = d.MashupStepReached + 1 < n;
        }

        static string F(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);
        static string Signed(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ input

        static readonly Key[] WatchedKeys =
        {
            Key.P, Key.Escape, Key.Enter, Key.NumpadEnter, Key.UpArrow, Key.DownArrow, Key.PageUp, Key.PageDown,
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9,
            Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9,
            Key.Space, Key.N, Key.B, Key.LeftArrow, Key.RightArrow, Key.C, Key.M
        };

        void Update()
        {
            if (loader == null || loader.Data == null || root == null) return;
            Keyboard? k = Keyboard.current;
            if (k == null) return;
            foreach (Key key in WatchedKeys)
                if (k[key].wasPressedThisFrame && HandleKey(key)) break;
        }

        /// <summary>One key press (public so validation can drive the panel without a keyboard). True when used.</summary>
        public bool HandleKey(Key key)
        {
            switch (State)
            {
                case PanelState.Closed:
                    if (key != Key.P) return false;
                    Open();
                    return true;
                case PanelState.List:
                    switch (key)
                    {
                        case Key.P:
                        case Key.Escape:
                            Close();
                            return true;
                        case Key.UpArrow:
                            MoveSelection(-1);
                            return true;
                        case Key.DownArrow:
                            MoveSelection(1);
                            return true;
                        case Key.PageUp:
                            Select(Selected - Math.Max(1, visibleRows));
                            return true;
                        case Key.PageDown:
                            Select(Selected + Math.Max(1, visibleRows));
                            return true;
                        case Key.Enter:
                        case Key.NumpadEnter:
                            PlaySelected();
                            return true;
                    }
                    int digit = Digit(key);
                    if (digit >= 1 && digit <= paths.Count)
                    {
                        Select(digit - 1);
                        return true;
                    }
                    return false;
                case PanelState.Playing:
                    WalkthroughDirector d = Director;
                    switch (key)
                    {
                        case Key.Escape:
                        case Key.P:
                            StopTour();
                            return true;
                        case Key.Space:
                            PauseOrReplay();
                            return true;
                        case Key.N:
                        case Key.RightArrow:
                            d.Next();
                            return true;
                        case Key.B:
                        case Key.LeftArrow:
                            d.Previous();
                            return true;
                        case Key.Enter:
                        case Key.NumpadEnter:
                            d.GoTo(d.TourComplete ? 0 : d.StepIndex);
                            return true;
                        case Key.C:
                            d.ToggleApplesToApples();
                            return true;
                        case Key.M:
                            return ToggleMelody();
                    }
                    return false;
            }
            return false;
        }

        static int Digit(Key key)
        {
            if (key >= Key.Digit1 && key <= Key.Digit9) return key - Key.Digit1 + 1;
            if (key >= Key.Numpad1 && key <= Key.Numpad9) return key - Key.Numpad1 + 1;
            return 0;
        }

        // ------------------------------------------------------------------ views

        /// <summary>A small rounded label ("RECORDING PREVIEW", "STEP 2 / 4", "STRONG MATCH").</summary>
        sealed class Pill
        {
            public readonly Image Root;
            public readonly TextMeshProUGUI Label;

            public Pill(Transform parent, string name, Color back, Color text)
            {
                Root = UiKit.Image(name + " Pill", parent, back, 11);
                Label = UiKit.Text("Label", Root.transform, 12, text, TextAlignmentOptions.Center, bold: true);
                Label.characterSpacing = 4;
                UiKit.Fill(Label.rectTransform);
            }

            public void Set(string text) => UiKit.SetText(Label, text);

            public void SetColors(Color back, Color text)
            {
                Root.color = back;
                Label.color = text;
            }

            float Width => Label.GetPreferredValues(Label.text, 1000, 0).x + 22f;

            /// <summary>Right edge at <paramref name="right"/> (from the strip's left); returns its left edge.</summary>
            public float PlaceRight(float right, float y)
            {
                float w = Width;
                UiKit.Place(Root.rectTransform, right - w, y, w, 22);
                return right - w;
            }

            public void PlaceLeft(float left, float y, float h)
            {
                UiKit.Place(Root.rectTransform, left, y, Width, h);
            }
        }
    }

    /// <summary>One row of the featured-paths list (pointer hover and click).</summary>
    public sealed class RowView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public FeaturedPathsPanel Panel = null!;
        public int PathIndex;
        public float Width;
        public float StripWidth;
        /// <summary>Width the title may use (it shrinks to fit, see <see cref="UiKit.FitWidth"/>).</summary>
        public float TitleWidth;
        public Image Background = null!, Accent = null!, Badge = null!, Track = null!, Span = null!;
        public TextMeshProUGUI BadgeText = null!, Title = null!, Subtitle = null!, Duration = null!, Count = null!, Era = null!;
        public RectTransform Strip = null!;
        public readonly List<Image> Dots = new();

        public void OnPointerEnter(PointerEventData eventData) => Panel.HoverRow(PathIndex);

        public void OnPointerExit(PointerEventData eventData)
        {
            if (Panel.Hovered == PathIndex) Panel.HoverRow(-1);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Panel.ClickRow(PathIndex);
        }
    }

    /// <summary>A step chip of the now-playing strip: click to jump to that step.</summary>
    public sealed class ChipView : MonoBehaviour, IPointerClickHandler
    {
        public FeaturedPathsPanel Panel = null!;
        public int Step;
        public Image Background = null!;
        public TextMeshProUGUI Label = null!;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Panel.JumpTo(Step);
        }
    }

    /// <summary>Mouse-wheel scrolling of the featured-paths rows.</summary>
    public sealed class ListScroll : MonoBehaviour, IScrollHandler
    {
        public FeaturedPathsPanel Panel = null!;

        public void OnScroll(PointerEventData eventData)
        {
            float y = eventData.scrollDelta.y;
            if (Mathf.Abs(y) > .01f) Panel.Scroll(y > 0 ? -1 : 1);
        }
    }

}
