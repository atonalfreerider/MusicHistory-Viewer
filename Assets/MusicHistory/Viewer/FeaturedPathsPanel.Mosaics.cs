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
using UnityEngine.UI;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The featured-paths panel's melody mosaics (data/audio/mosaics/mosaics.json, DESIGN.md §17): a
    /// list of their own in the same sheet (O opens it, or switches the open list between the paths
    /// and the mosaics; so does the header's Mosaics / Paths button). Each row shows the mosaic's
    /// name and length, its target song (title, artist, year), how many songs rebuild it, a strip of
    /// its pieces across the loop in their songs' colours and its note match; below the rows, the
    /// hovered (else selected) mosaic's target, pieces and harmonies. ↑↓ / 1–9 select (the songs
    /// light up on the graph), Enter, a click or Play plays it, Esc closes.
    ///
    /// While a mosaic plays the strip reads "Mosaic: &lt;target&gt; rebuilt from N songs", LOOP k / n,
    /// the section as three chips (original, mosaic, harmony; a click jumps there), what is heard
    /// ("Fireflies, 2009 · 3 semitones down · 84% speed · 4/4 notes match"), the key, BPM,
    /// coverage and match, and the whole mix on the time bar with its sections marked.
    /// </summary>
    public sealed partial class FeaturedPathsPanel
    {
        /// <summary>Which list the open sheet shows.</summary>
        public enum ListTab { Paths, Mosaics }

        public ListTab Tab { get; private set; } = ListTab.Paths;
        readonly List<Mosaic> mosaics = new();
        readonly List<MosaicRowView> mosaicRows = new();
        /// <summary>The mosaics listed (the loader's catalog).</summary>
        public IReadOnlyList<Mosaic> Mosaics => mosaics;
        /// <summary>The mosaic rows (index = row slot = mosaic index).</summary>
        public IReadOnlyList<MosaicRowView> MosaicRows => mosaicRows;
        /// <summary>The selected mosaic row, -1 when there are none.</summary>
        public int SelectedMosaic { get; private set; } = -1;
        /// <summary>The mosaic row under the pointer, -1 when none.</summary>
        public int HoveredMosaic { get; private set; } = -1;
        /// <summary>Mosaic rows the list shows at once (the rest scroll).</summary>
        public int VisibleMosaicRowCount => visibleMosaicRows;
        /// <summary>The header's Mosaics / Paths switch (O).</summary>
        public Button? TabButton => tabButton;

        int firstMosaicRow, visibleMosaicRows;
        float maxMosaicDetail = -1f;
        Button tabButton = null!;
        Image tabBack = null!;
        TextMeshProUGUI tabLabel = null!, playLabel = null!;
        RectTransform mosaicRowsArea = null!;
        TextMeshProUGUI mosaicTarget = null!, mosaicSongs = null!, mosaicEmpty = null!;

        /// <summary>Text of the mosaics list (rows and the detail), for validation.</summary>
        public string MosaicListText
        {
            get
            {
                StringBuilder b = new();
                foreach (MosaicRowView r in mosaicRows)
                {
                    if (!r.gameObject.activeSelf) continue;
                    b.Append(r.Title.text).Append(" | ").Append(r.Subtitle.text).Append(" | ").Append(r.Duration.text)
                        .Append(" | ").Append(r.Count.text).Append(" | ").Append(r.Era.text).Append('\n');
                }
                if (mosaicTarget != null && mosaicTarget.gameObject.activeSelf) b.Append(mosaicTarget.text).Append('\n');
                if (mosaicSongs != null && mosaicSongs.gameObject.activeSelf) b.Append(mosaicSongs.text).Append('\n');
                if (mosaicEmpty != null && mosaicEmpty.gameObject.activeSelf) b.Append(mosaicEmpty.text);
                return b.ToString();
            }
        }

        void ResetMosaics(SongGraphLoader owner)
        {
            mosaicRows.Clear();
            mosaics.Clear();
            mosaics.AddRange(owner.Mosaics.Mosaics);
            Tab = ListTab.Paths;
            SelectedMosaic = mosaics.Count > 0 ? Math.Max(0, mosaics.FindIndex(m => m.IsPlayable)) : -1;
            HoveredMosaic = -1;
            firstMosaicRow = 0;
            maxMosaicDetail = -1f;
        }

        // ------------------------------------------------------------------ build

        void BuildMosaicList()
        {
            playLabel = playButton.GetComponentInChildren<TextMeshProUGUI>();
            // The header's switch between the two lists, left of the close button.
            tabButton = MakeButton(list, "Mosaics Tab", "MOSAICS  O", 12, new Color(1, 1, 1, .08f), TextColor, out tabBack);
            UiKit.Place((RectTransform)tabButton.transform, ListWidth - Pad - 34 - 10 - 136, 21, 136, 26);
            tabLabel = tabButton.GetComponentInChildren<TextMeshProUGUI>();
            tabLabel.characterSpacing = 3;
            tabButton.onClick.AddListener(() =>
            {
                Deselect();
                SetTab(Tab == ListTab.Mosaics ? ListTab.Paths : ListTab.Mosaics);
            });
            UiKit.Show(tabButton, mosaics.Count > 0);

            mosaicRowsArea = UiKit.Rect("Mosaic Rows", list);
            for (int i = 0; i < mosaics.Count; i++) mosaicRows.Add(MakeMosaicRow(i));
            mosaicTarget = UiKit.Text("Mosaic Target", list, 16, TextColor);
            mosaicSongs = UiKit.Text("Mosaic Songs", list, 15, new Color(.84f, .86f, .89f, 1f), TextAlignmentOptions.TopLeft, wrap: true);
            mosaicSongs.lineSpacing = 2;
            mosaicEmpty = UiKit.Text("Mosaics Empty", list, 16, MutedColor, TextAlignmentOptions.TopLeft, wrap: true);
            ShowMosaicList(false);
        }

        MosaicRowView MakeMosaicRow(int index)
        {
            Mosaic m = mosaics[index];
            float w = ListWidth - 2 * 12f - 10f;
            RectTransform r = UiKit.Rect($"Mosaic {index + 1}", mosaicRowsArea);
            r.gameObject.AddComponent<CanvasRenderer>();
            Image bg = r.gameObject.AddComponent<Image>();
            bg.sprite = UiKit.Rounded(10);
            bg.type = Image.Type.Sliced;
            bg.color = new Color(1, 1, 1, 0f);
            bg.raycastTarget = true;
            MosaicRowView row = r.gameObject.AddComponent<MosaicRowView>();
            row.Panel = this;
            row.MosaicIndex = index;
            row.Background = bg;
            row.Width = w;

            row.Accent = UiKit.Image("Accent", r, DefaultAccent, 2);
            UiKit.Place(row.Accent.rectTransform, 0, 14, 4, RowHeight - 28);
            row.Badge = UiKit.Image("Badge", r, new Color(1, 1, 1, .1f), 6);
            UiKit.Place(row.Badge.rectTransform, 14, 14, 26, 26);
            row.BadgeText = UiKit.Text("Number", row.Badge.transform, 14, TextColor, TextAlignmentOptions.Center, bold: true);
            UiKit.Fill(row.BadgeText.rectTransform);
            row.BadgeText.text = index < 9 ? (index + 1).ToString(CultureInfo.InvariantCulture) : "·";

            float left = 54f, right = 104f, duration = 64f;
            row.TitleWidth = w - left - 14 - duration - 10;
            row.Title = UiKit.Text("Title", r, TitleSize, TextColor, TextAlignmentOptions.MidlineLeft, bold: true);
            UiKit.Place(row.Title.rectTransform, left, 11, row.TitleWidth, 28);
            row.Duration = UiKit.Text("Duration", r, 19, TextColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Duration.rectTransform, w - 14 - duration, 11, duration, 28);
            row.Subtitle = UiKit.Text("Subtitle", r, 15, MutedColor);
            UiKit.Place(row.Subtitle.rectTransform, left, 39, w - left - right - 8, 20);
            row.Count = UiKit.Text("Count", r, 14, MutedColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Count.rectTransform, w - right, 39, right - 14, 20);
            row.Era = UiKit.Text("Match", r, 14, MutedColor, TextAlignmentOptions.MidlineRight);
            UiKit.Place(row.Era.rectTransform, w - right - 10, 60, right - 4, 20);

            // The loop as a strip: one block per piece in its song's colour (the gaps the target's own).
            row.Strip = UiKit.Rect("Pieces", r);
            float stripWidth = w - left - right - 8;
            UiKit.Place(row.Strip, left, 64, stripWidth, 14);
            row.StripWidth = stripWidth;
            Image track = UiKit.Image("Track", row.Strip, new Color(1, 1, 1, .12f));
            UiKit.Place(track.rectTransform, 0, 6, stripWidth, 2);
            double lb = m.LoopBeats > 0 ? m.LoopBeats : 16;
            foreach (MosaicPiece p in m.Pieces)
            {
                Image block = UiKit.Image($"Piece {p.Index + 1}", row.Strip, MelodyGraphPanel.MosaicColor(p.Song), 2);
                float x0 = (float)(p.Start / lb) * stripWidth, x1 = (float)(p.End / lb) * stripWidth;
                UiKit.Place(block.rectTransform, x0 + .5f, 3f, Mathf.Max(2f, x1 - x0 - 1f), 8f);
                row.Blocks.Add(block);
            }

            row.Title.text = GraphHud.Esc(m.Name.Length > 0 ? m.Name : PathCatalog.TitleCase(m.Id));
            UiKit.FitWidth(row.Title, row.TitleWidth, TitleSize, MinTitleSize);
            row.Duration.text = PathCatalog.Clock(m.Duration);
            MosaicSong t = m.Target;
            string subtitle = $"{t.Title} · {t.Artist}{(t.Year > 0 ? $", {t.Year}" : "")}";
            if (!m.IsPlayable) subtitle = "not in this graph · " + subtitle;
            row.Subtitle.text = GraphHud.Esc(subtitle);
            UiKit.FitWidth(row.Subtitle, w - left - right - 8, 15f, 12f);
            int n = m.SourceCount;
            row.Count.text = $"{n} song{(n == 1 ? "" : "s")}";
            row.Era.text = $"match {MosaicCatalog.Percent(m.Match)}";
            return row;
        }

        // ------------------------------------------------------------------ state

        /// <summary>Opens the sheet on <paramref name="tab"/> (or switches to it when open).</summary>
        public void OpenTab(ListTab tab)
        {
            if (State == PanelState.List)
            {
                SetTab(tab);
                return;
            }
            Tab = tab;
            Open();
        }

        /// <summary>Shows the paths' or the mosaics' list in the open sheet (O, the header's switch).</summary>
        public void SetTab(ListTab tab)
        {
            Tab = tab;
            Notice = "";
            Hovered = -1;
            HoveredMosaic = -1;
            if (State != PanelState.List) return;
            if (tab == ListTab.Mosaics && SelectedMosaic < 0 && mosaics.Count > 0) SelectedMosaic = 0;
            RefreshList();
            ApplyPreview();
        }

        /// <summary>Selects mosaic row <paramref name="index"/> (clamped); its songs light up.</summary>
        public void SelectMosaic(int index)
        {
            if (mosaics.Count == 0) return;
            SelectedMosaic = Mathf.Clamp(index, 0, mosaics.Count - 1);
            Notice = "";
            if (visibleMosaicRows > 0)
            {
                if (SelectedMosaic < firstMosaicRow) firstMosaicRow = SelectedMosaic;
                else if (SelectedMosaic >= firstMosaicRow + visibleMosaicRows) firstMosaicRow = SelectedMosaic - visibleMosaicRows + 1;
            }
            RefreshList();
            ApplyPreview();
        }

        public void MoveMosaicSelection(int delta)
        {
            if (mosaics.Count == 0) return;
            SelectMosaic(SelectedMosaic < 0 ? 0 : (SelectedMosaic + delta + mosaics.Count) % mosaics.Count);
        }

        /// <summary>The pointer entered mosaic row <paramref name="index"/> (-1: left the rows).</summary>
        public void HoverMosaicRow(int index)
        {
            int next = index >= 0 && index < mosaics.Count ? index : -1;
            if (next == HoveredMosaic) return;
            HoveredMosaic = next;
            RefreshList();
            ApplyPreview();
        }

        public void ClickMosaicRow(int index)
        {
            if (index < 0 || index >= mosaics.Count) return;
            SelectMosaic(index);
            PlaySelectedMosaic();
        }

        /// <summary>Plays the selected mosaic; false when it cannot play.</summary>
        public bool PlaySelectedMosaic()
        {
            if (SelectedMosaic < 0 || SelectedMosaic >= mosaics.Count)
            {
                Notice = "No melody mosaics to play (data/audio/mosaics).";
                RefreshList();
                return false;
            }
            Mosaic m = mosaics[SelectedMosaic];
            if (!m.IsPlayable)
            {
                Notice = $"This mosaic cannot play here: {MosaicCatalog.WhyNotPlayable(m)}.";
                RefreshList();
                return false;
            }
            loader.Highlighter.SetRoutePreview(null);
            bool ok = Director.StartMosaic(m);
            if (!ok)
            {
                Notice = "The mosaic could not start.";
                RefreshList();
                ApplyPreview();
            }
            return ok;
        }

        /// <summary>Plays mosaic <paramref name="id"/> as if chosen from the list (the recorder, validation). False when it cannot.</summary>
        public bool PlayMosaic(string id)
        {
            int index = mosaics.FindIndex(m => m.Id == id);
            if (index < 0) return false;
            SelectedMosaic = index;
            Tab = ListTab.Mosaics;
            return PlaySelectedMosaic();
        }

        bool HandleMosaicListKey(Key key)
        {
            switch (key)
            {
                case Key.P:
                case Key.Escape:
                    Close();
                    return true;
                case Key.UpArrow:
                    MoveMosaicSelection(-1);
                    return true;
                case Key.DownArrow:
                    MoveMosaicSelection(1);
                    return true;
                case Key.PageUp:
                    SelectMosaic(SelectedMosaic - Math.Max(1, visibleMosaicRows));
                    return true;
                case Key.PageDown:
                    SelectMosaic(SelectedMosaic + Math.Max(1, visibleMosaicRows));
                    return true;
                case Key.Enter:
                case Key.NumpadEnter:
                    PlaySelectedMosaic();
                    return true;
            }
            int digit = Digit(key);
            if (digit >= 1 && digit <= mosaics.Count)
            {
                SelectMosaic(digit - 1);
                return true;
            }
            return false;
        }

        void ApplyMosaicPreview()
        {
            int index = HoveredMosaic >= 0 ? HoveredMosaic : SelectedMosaic;
            GraphRoute? route = index >= 0 && index < mosaics.Count && mosaics[index].IsPlayable ? loader.RouteFor(mosaics[index]) : null;
            loader.Highlighter.SetRoutePreview(route);
        }

        void ScrollMosaics(int delta)
        {
            int max = Math.Max(0, mosaics.Count - visibleMosaicRows);
            int next = Mathf.Clamp(firstMosaicRow + delta, 0, max);
            if (next == firstMosaicRow) return;
            firstMosaicRow = next;
            RefreshList();
        }

        // ------------------------------------------------------------------ refresh: the list

        void ShowMosaicList(bool on)
        {
            UiKit.Show(mosaicRowsArea, on);
            UiKit.Show(mosaicTarget, on);
            UiKit.Show(mosaicSongs, on);
            UiKit.Show(mosaicEmpty, on && mosaics.Count == 0);
        }

        /// <summary>The paths' list (RefreshList): the mosaics' rows and detail go, the title and Play button are the paths'.</summary>
        void ShowPathsTab()
        {
            ShowMosaicList(false);
            UiKit.SetText(listTitle, "Featured paths");
            UiKit.Show(tabButton, mosaics.Count > 0);
            UiKit.SetText(tabLabel, "MOSAICS  O");
            tabBack.color = new Color(1, 1, 1, .08f);
            if (playLabel != null) UiKit.SetText(playLabel, "►  Play path");
        }

        void RefreshMosaicList()
        {
            // The paths' parts make way.
            foreach (RowView r in rows) UiKit.Show(r, false);
            UiKit.Show(detailIdentity, false);
            UiKit.Show(detailDescription, false);
            foreach (TextMeshProUGUI t in detailSteps) UiKit.Show(t, false);
            UiKit.Show(emptyText, false);
            UiKit.Show(duetPlayButton, false);
            UiKit.Show(mosaicRowsArea, true);
            UiKit.Show(tabButton, true);
            UiKit.SetText(tabLabel, "PATHS  O");
            tabBack.color = UiKit.WithAlpha(DefaultAccent, .22f);
            UiKit.SetText(listTitle, "Melody mosaics");

            int playable = 0;
            double total = 0;
            foreach (Mosaic m in mosaics)
            {
                if (m.IsPlayable) playable++;
                total += m.Duration;
            }
            UiKit.SetText(listSummary, mosaics.Count == 0
                ? "none yet"
                : $"{mosaics.Count} mosaic{(mosaics.Count == 1 ? "" : "s")} · {PathCatalog.Clock(total)} · melodies rebuilt from other songs" +
                  (playable < mosaics.Count ? $" · {mosaics.Count - playable} not in this graph" : ""));

            int shown = HoveredMosaic >= 0 ? HoveredMosaic : SelectedMosaic;
            Mosaic? detail = shown >= 0 && shown < mosaics.Count ? mosaics[shown] : null;
            float inner = ListWidth - 2 * Pad;
            float detailHeight = MaxMosaicDetail(inner);
            float maxHeight = 1080f - 2 * Margin;
            float fixedHeight = HeaderHeight + 14 + detailHeight + FooterHeight;
            int fit = Mathf.Max(1, Mathf.FloorToInt((maxHeight - fixedHeight + RowGap) / (RowHeight + RowGap)));
            visibleMosaicRows = Math.Min(mosaics.Count, fit);
            firstMosaicRow = Mathf.Clamp(firstMosaicRow, 0, Math.Max(0, mosaics.Count - visibleMosaicRows));
            if (SelectedMosaic >= 0 && visibleMosaicRows > 0)
            {
                if (SelectedMosaic < firstMosaicRow) firstMosaicRow = SelectedMosaic;
                else if (SelectedMosaic >= firstMosaicRow + visibleMosaicRows) firstMosaicRow = SelectedMosaic - visibleMosaicRows + 1;
            }

            float y = HeaderHeight;
            UiKit.Place(mosaicRowsArea, 12, y, ListWidth - 24, visibleMosaicRows * (RowHeight + RowGap));
            for (int i = 0; i < mosaicRows.Count; i++)
            {
                MosaicRowView row = mosaicRows[i];
                bool visible = i >= firstMosaicRow && i < firstMosaicRow + visibleMosaicRows;
                UiKit.Show(row, visible);
                if (!visible) continue;
                UiKit.Place((RectTransform)row.transform, 0, (i - firstMosaicRow) * (RowHeight + RowGap), row.Width, RowHeight);
                StyleMosaicRow(row, i == SelectedMosaic, i == HoveredMosaic);
            }
            bool scrolls = mosaics.Count > visibleMosaicRows;
            UiKit.Show(scrollTrack, scrolls);
            UiKit.Show(scrollThumb, scrolls);
            float rowsHeight = visibleMosaicRows * (RowHeight + RowGap) - (visibleMosaicRows > 0 ? RowGap : 0);
            if (scrolls)
            {
                UiKit.Place(scrollTrack.rectTransform, ListWidth - 10, y, 4, rowsHeight);
                float thumb = Mathf.Max(24f, rowsHeight * visibleMosaicRows / mosaics.Count);
                float at = (rowsHeight - thumb) * firstMosaicRow / Math.Max(1, mosaics.Count - visibleMosaicRows);
                UiKit.Place(scrollThumb.rectTransform, ListWidth - 10, y + at, 4, thumb);
            }
            y += Math.Max(0, rowsHeight) + 14;

            bool empty = mosaics.Count == 0;
            UiKit.Show(mosaicEmpty, empty);
            UiKit.Show(detailDivider, !empty);
            if (empty)
            {
                MosaicCatalog c = loader.Mosaics;
                string source = c.SourcePath.Length > 0 ? c.SourcePath : MosaicCatalog.FileName;
                mosaicEmpty.text = "No melody mosaics yet.\n" +
                                   $"<size=88%><color={Muted}>{GraphHud.Esc(c.Status)}. The mosaics come from {GraphHud.Esc(System.IO.Path.GetFileName(source))} " +
                                   "(the pipeline's mosaic stage, data/audio/mosaics).</color></size>";
                float h = mosaicEmpty.GetPreferredValues(mosaicEmpty.text, inner, 0).y;
                UiKit.Place(mosaicEmpty.rectTransform, Pad, y - 4, inner, h + 4);
                UiKit.Show(mosaicTarget, false);
                UiKit.Show(mosaicSongs, false);
                y += h + 10;
            }
            else
            {
                UiKit.Place(detailDivider.rectTransform, Pad, y - 8, inner, 1);
                float top = y + 6;
                y = Mathf.Max(FillMosaicDetail(detail, top, inner), top + detailHeight);
            }

            UiKit.Show(notice, Notice.Length > 0);
            if (Notice.Length > 0)
            {
                notice.text = GraphHud.Esc(Notice);
                float h = notice.GetPreferredValues(notice.text, inner, 0).y;
                UiKit.Place(notice.rectTransform, Pad, y, inner, h);
                y += h + 8;
            }

            y += 6;
            footerHint.text = empty
                ? $"<b><color=#e6e9ee>O</color></b> paths   <b><color=#e6e9ee>Esc</color></b> close"
                : $"<b><color=#e6e9ee>Enter</color></b> play   <b><color=#e6e9ee>↑↓</color></b> <b><color=#e6e9ee>1–{Math.Min(9, mosaics.Count)}</color></b> select   " +
                  $"<b><color=#e6e9ee>O</color></b> paths   <b><color=#e6e9ee>Esc</color></b> close";
            UiKit.Place(footerHint.rectTransform, Pad, y, inner - 160, 36);
            UiKit.Show(playButton, !empty);
            if (playLabel != null) UiKit.SetText(playLabel, "►  Play mosaic");
            bool canPlay = detail != null && SelectedMosaic >= 0 && mosaics[SelectedMosaic].IsPlayable;
            playButton.interactable = canPlay;
            playBack.color = canPlay ? DefaultAccent : new Color(1, 1, 1, .12f);
            UiKit.Place((RectTransform)playButton.transform, ListWidth - Pad - 150, y, 150, 36);
            y += 36 + Pad;
            list.sizeDelta = new Vector2(ListWidth, Mathf.Min(maxHeight, y));
        }

        void StyleMosaicRow(MosaicRowView row, bool selected, bool hovered)
        {
            Mosaic m = mosaics[row.MosaicIndex];
            row.Background.color = selected ? RowSelectedColor : hovered ? RowHoverColor : new Color(1, 1, 1, 0f);
            UiKit.Show(row.Accent, selected);
            row.Badge.color = selected ? UiKit.WithAlpha(DefaultAccent, .9f) : new Color(1, 1, 1, .1f);
            row.BadgeText.color = selected ? new Color(.06f, .06f, .07f, 1f) : TextColor;
            row.Title.color = m.IsPlayable ? TextColor : DimTextColor;
            row.Duration.color = m.IsPlayable ? TextColor : DimTextColor;
        }

        float MaxMosaicDetail(float width)
        {
            if (maxMosaicDetail >= 0) return maxMosaicDetail;
            float h = 0;
            foreach (Mosaic m in mosaics)
            {
                mosaicSongs.text = MosaicSongsText(m);
                h = Mathf.Max(h, 26 + mosaicSongs.GetPreferredValues(mosaicSongs.text, width, 0).y + 6);
            }
            return maxMosaicDetail = h;
        }

        float FillMosaicDetail(Mosaic? m, float y, float width)
        {
            UiKit.Show(mosaicTarget, m != null);
            UiKit.Show(mosaicSongs, m != null);
            if (m == null) return y;
            MosaicSong t = m.Target;
            mosaicTarget.text = $"<color={Muted}>Rebuilds</color> <b><color={UiKit.Hex(MelodyGraphPanel.TargetColor)}>{GraphHud.Esc(t.Title)}</color></b>" +
                                $" <color={Muted}>{GraphHud.Esc(t.Artist)}{(t.Year > 0 ? $", {t.Year}" : "")} · from {m.SourceCount} song{(m.SourceCount == 1 ? "" : "s")}</color>";
            UiKit.FitWidth(mosaicTarget, width, 16f, 12f);
            UiKit.Place(mosaicTarget.rectTransform, Pad, y, width, 22);
            y += 26;
            mosaicSongs.text = MosaicSongsText(m);
            float h = mosaicSongs.GetPreferredValues(mosaicSongs.text, width, 0).y;
            UiKit.Place(mosaicSongs.rectTransform, Pad, y, width, h + 2);
            return y + h + 6;
        }

        /// <summary>"Pieces: ■ Title (1936) …  Harmonies: ■ Title (1986) … · coverage 95% · match 90% · 9 loops of 4 bars, A major".</summary>
        static string MosaicSongsText(Mosaic m)
        {
            StringBuilder b = new();
            b.Append($"<color={Muted}>Pieces</color>  ");
            for (int i = 0; i < m.Pieces.Count; i++)
            {
                MosaicPiece p = m.Pieces[i];
                if (i > 0) b.Append($"<color={Muted}>,</color> ");
                b.Append($"<color={UiKit.Hex(MelodyGraphPanel.MosaicColor(p.Song))}>■</color> {GraphHud.Esc(p.Title)}");
                if (p.Year > 0) b.Append($" <color={Muted}>({p.Year})</color>");
            }
            b.Append($"\n<color={Muted}>Harmonies</color>  ");
            for (int i = 0; i < m.Harmonies.Count; i++)
            {
                MosaicHarmony h = m.Harmonies[i];
                if (i > 0) b.Append($"<color={Muted}>,</color> ");
                b.Append($"<color={UiKit.Hex(MelodyGraphPanel.MosaicColor(h.Song))}>■</color> {GraphHud.Esc(h.Title)}");
                if (h.Year > 0) b.Append($" <color={Muted}>({h.Year})</color>");
            }
            b.Append($"\n<color={Muted}>coverage {MosaicCatalog.Percent(m.Coverage)} · match {MosaicCatalog.Percent(m.Match)} · " +
                     $"{m.Loops} loops of {m.LoopBars} bars · {GraphHud.Esc(m.Key)} · {m.Bpm.ToString("0.#", CultureInfo.InvariantCulture)} BPM</color>");
            return b.ToString();
        }

        // ------------------------------------------------------------------ refresh: now playing

        /// <summary>The via row as the paths lay it out (a mosaic's caption widens and shrinks it).</summary>
        void RestoreStripVia()
        {
            if (!Mathf.Approximately(stripVia.fontSize, 17f)) stripVia.fontSize = 17f;
            if (!Mathf.Approximately(stripVia.rectTransform.sizeDelta.x, 860f)) UiKit.Place(stripVia.rectTransform, Pad, 114, 860, 26);
        }

        /// <summary>
        /// The now-playing strip for a melody mosaic: "Mosaic: &lt;target&gt; rebuilt from N songs", LOOP k / n,
        /// the sections as chips, what is heard, key, BPM, coverage and match, the mix on the time bar.
        /// </summary>
        void RefreshMosaicStrip(WalkthroughDirector d, Mosaic m)
        {
            Color accent = DefaultAccent;
            float leftmost = LayoutButtons(true, false, accent);
            MosaicTimeline.State st = d.MosaicState;
            MosaicPlayer? player = d.MosaicAudio;
            double t = d.MosaicSeconds, length = player != null && player.DurationSeconds > 0 ? player.DurationSeconds : m.Duration;
            stripAccent.color = accent;
            stripKicker.color = accent;
            string state = d.TourComplete ? "MOSAIC COMPLETE" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "PAUSED" : "NOW PLAYING";
            string section = st.Section >= 0 ? $" · {MosaicSection.KindName(st.Kind).ToUpperInvariant()} SECTION" : "";
            UiKit.SetText(stripKicker, $"{state} · MELODY MOSAIC{section}");
            UiKit.SetText(stripTitle, GraphHud.Esc(m.StripTitle));

            float right = StripWidth - Pad;
            stepPill.Set($"LOOP {Math.Max(0, st.Loop) + 1} / {m.Loops}");
            right = stepPill.PlaceRight(right, 22) - 8;
            if (player != null && player.CurrentAudio != null)
            {
                recordingPill.SetColors(UiKit.WithAlpha(RecordingColor, .2f), RecordingColor);
                recordingPill.Set("● MOSAIC MIX");
            }
            else
            {
                recordingPill.SetColors(new Color(1, 1, 1, .08f), MutedColor);
                recordingPill.Set(player != null && player.ClockSource == "loading" ? "LOADING MIX" : "MOSAIC · SILENT CLOCK");
            }
            recordingPill.PlaceRight(right, 22);

            // Chips: the three sections; the one playing lit, with the loop within it.
            float x = Pad, width = StripWidth - 2 * Pad, arrow = 22f;
            int n = m.Sections.Count;
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
                MosaicSection s = m.Sections[i];
                bool current = i == st.Section, past = i < st.Section;
                c.Background.color = current ? UiKit.WithAlpha(accent, .26f) : past ? new Color(1, 1, 1, .07f) : new Color(1, 1, 1, .035f);
                c.Label.color = current ? TextColor : past ? new Color(.8f, .82f, .85f, 1f) : MutedColor;
                string loops = $"{s.Loops} loop{(s.Loops == 1 ? "" : "s")}";
                string here = current ? $"  <size=78%><color={UiKit.Hex(accent)}><b>LOOP {st.Loop - s.FirstLoop + 1}/{s.Loops}</b></color></size>" : "";
                UiKit.SetText(c.Label, $"{(current ? "<b>" : "")}{MosaicSection.Title(s.Kind)}{(current ? "</b>" : "")}  <size=85%><color={(current ? UiKit.Hex(accent) : Muted)}>{loops}</color></size>{here}");
                x += chipWidth + arrow;
            }

            // What is heard now (the piece sounding, the harmony voices, the original).
            string dot = $" <color={Muted}>·</color> ";
            string what;
            switch (st.Kind)
            {
                case MosaicSectionKind.Mosaic:
                    if (st.Piece >= 0 && st.Piece < m.Pieces.Count)
                    {
                        MosaicPiece p = m.Pieces[st.Piece];
                        what = $"<color={Muted}>Piece {p.Index + 1}/{m.Pieces.Count}</color>  " +
                               $"<b><color={UiKit.Hex(MelodyGraphPanel.MosaicColor(p.Song))}>{GraphHud.Esc(p.Title)}</color></b>{(p.Year > 0 ? $", {p.Year}" : "")}" +
                               $"{dot}{Mosaic.ShiftText(p.ShiftSemitones)}{dot}{Mosaic.SpeedText(p.TempoRatio)}{dot}{p.MatchedNotes}/{p.NotesCompared} notes match";
                    }
                    else what = $"<color={Muted}>between pieces: no song covers this beat · the faint line is the melody of</color> {GraphHud.Esc(m.Target.Title)}";
                    break;
                case MosaicSectionKind.Harmony:
                    List<string> voices = new();
                    foreach (MosaicHarmony h in m.Harmonies)
                        voices.Add($"<b><color={UiKit.Hex(MelodyGraphPanel.MosaicColor(h.Song))}>{GraphHud.Esc(h.Title)}</color></b>{(h.Year > 0 ? $", {h.Year}" : "")}" +
                                   $"{dot}{Mosaic.ShiftText(h.ShiftSemitones)}{dot}{Mosaic.SpeedText(h.TempoRatio)}{dot}{MosaicCatalog.Percent(h.Consonance)} consonant");
                    what = $"<color={Muted}>Harmony</color>  " + string.Join($"  <color={Muted}>+</color>  ", voices);
                    break;
                default:
                    what = $"<color={Muted}>Original</color>  <b><color={UiKit.Hex(MelodyGraphPanel.TargetColor)}>{GraphHud.Esc(m.Target.Title)}</color></b>{(m.Target.Year > 0 ? $", {m.Target.Year}" : "")}" +
                           $"{dot}{GraphHud.Esc(m.Target.Artist)}{dot}<color={Muted}>the melody to rebuild</color>";
                    break;
            }
            UiKit.SetText(stripVia, what);
            float viaWidth = StripWidth - 2 * Pad - 250f;
            UiKit.Place(stripVia.rectTransform, Pad, 114, viaWidth, 26);
            UiKit.FitWidth(stripVia, viaWidth, 17f, 11f);
            UiKit.Show(strongPill.Root, false);

            List<string> parts = new()
            {
                $"<color={Muted}>Key</color> <b>{GraphHud.Esc(m.Key)}</b>",
                $"<color={Muted}>BPM</color> <b>{F(m.Bpm, "0.#")}</b>",
                $"<color={Muted}>coverage</color> <b>{MosaicCatalog.Percent(m.Coverage)}</b>",
                $"<color={Muted}>match</color> <b>{MosaicCatalog.Percent(m.Match)}</b>"
            };
            float readoutWidth = leftmost - Pad - 12;
            string readout = "";
            for (int k = parts.Count; k >= 1; k--)
            {
                readout = string.Join($"   <color={Muted}>·</color>   ", parts.GetRange(0, k));
                if (stripReadout.GetPreferredValues(readout, 4000, 0).x <= readoutWidth) break;
            }
            UiKit.SetText(stripReadout, readout);
            UiKit.Place(stripReadout.rectTransform, Pad, 164, readoutWidth, 30);
            UiKit.SetText(stripTime, $"{PathCatalog.Clock(t)} / {PathCatalog.Clock(length)}");

            // The whole mix on the time bar; the mosaic (white) and harmony (accent) sections marked above it.
            float barWidth = progressTrack.rectTransform.sizeDelta.x;
            UiKit.Place(progressFill.rectTransform, 0, 0, Mathf.Max(5f, barWidth * (float)(length > 0 ? Math.Min(1, t / length) : 0)), 5);
            progressFill.color = accent;
            UiKit.Show(glideMark, false);
            int marks = 0;
            foreach (MosaicSection s in m.Sections)
            {
                if (s.Kind == MosaicSectionKind.Original || length <= 0) continue;
                while (segmentMarks.Count <= marks) segmentMarks.Add(UiKit.Image($"Segment {segmentMarks.Count + 1}", progressTrack.transform, Color.white, 1));
                Image mark = segmentMarks[marks++];
                UiKit.Show(mark, true);
                float x0 = barWidth * (float)(s.Start / length), x1 = barWidth * (float)(Math.Min(length, s.End) / length);
                bool now = s.Index == st.Section;
                mark.color = s.Kind == MosaicSectionKind.Mosaic ? new Color(1, 1, 1, now ? .75f : .32f) : UiKit.WithAlpha(accent, now ? .95f : .55f);
                UiKit.Place(mark.rectTransform, x0 + 1, -6, Mathf.Max(2f, x1 - x0 - 2), 3);
            }
            for (int k = marks; k < segmentMarks.Count; k++) UiKit.Show(segmentMarks[k], false);

            bool graph = loader.MelodyGraph != null && loader.MelodyGraph.UserVisible;
            melodyBack.color = graph ? UiKit.WithAlpha(accent, .3f) : new Color(1, 1, 1, .08f);
            UiKit.SetText(melodyLabel, graph ? "Melody  M" : $"<color={Muted}>Melody  M</color>");
            UiKit.SetText(pauseLabel, d.TourComplete ? "►  Replay" : d.ActivePlayer != null && d.ActivePlayer.Paused ? "►  Resume" : "II  Pause");
            prevButton.interactable = st.Loop > 0 || t > .5 || d.TourComplete;
            nextButton.interactable = st.Loop + 1 < m.Loops;
        }
    }

    /// <summary>One row of the melody mosaics' list (pointer hover and click).</summary>
    public sealed class MosaicRowView : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        public FeaturedPathsPanel Panel = null!;
        public int MosaicIndex;
        public float Width;
        public float StripWidth;
        public float TitleWidth;
        public Image Background = null!, Accent = null!, Badge = null!;
        public TextMeshProUGUI BadgeText = null!, Title = null!, Subtitle = null!, Duration = null!, Count = null!, Era = null!;
        public RectTransform Strip = null!;
        /// <summary>The pieces across the loop, in their songs' colours.</summary>
        public readonly List<Image> Blocks = new();

        public void OnPointerEnter(PointerEventData eventData) => Panel.HoverMosaicRow(MosaicIndex);

        public void OnPointerExit(PointerEventData eventData)
        {
            if (Panel.HoveredMosaic == MosaicIndex) Panel.HoverMosaicRow(-1);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left) Panel.ClickMosaicRow(MosaicIndex);
        }
    }
}
