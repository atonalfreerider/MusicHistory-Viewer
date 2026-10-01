# MusicHistory viewer (Unity)

A Unity 6000.6.3f1 URP project that shows the song influence graph built by the
[MusicHistory](../MusicHistory) pipeline (its `docs/DESIGN.md` §10–§13) and walks through it with the
music playing. It is a fork of Unity-FDG and has its own repository; it used to live in
`MusicHistory/unity` (history preserved). In this README, `<MusicHistory>` means that pipeline
repository. The scene and the
reusable pieces (TextBox, CameraControl, the tapered edge mesh and the force-directed simulation)
keep their original script GUIDs.

## Open and run

1. Put this repository next to MusicHistory (`Desktop\MusicHistory` and
   `Desktop\MusicHistory-Viewer`). In Unity Hub, choose **Add project from disk** and pick this
   folder. Open it with **6000.6.3f1**.

   The viewer reads everything the pipeline produced (graphs, MIDI, the SoundFont, recording
   previews) from `<MusicHistory>/data`. `MusicHistory.PipelinePaths` finds `<MusicHistory>` from,
   in order: `-musicHistoryRoot <dir>` on the command line, the `MUSICHISTORY_ROOT` environment
   variable, the parent of `MUSICHISTORY_DATA`, a sibling folder named `MusicHistory`, and the
   folder above this project (the old in-repo layout).
2. Open `Assets/Scenes/SongInfluenceGraph.unity` and press Play.

The loader looks for a graph database in this order:

| Order | Source |
|---|---|
| 1 | `-musicHistoryDb <path>` on the command line (player or editor) |
| 2 | `DbPath` on the **MusicHistory Graph** object (inspector) |
| 3 | `<MusicHistory>/data/graph/music_graph.db` (the pipeline's output) |
| 4 | `Assets/StreamingAssets/music_graph.db` (a DB copied next to a player build) |
| 5 | `<MusicHistory>/data/graph/demo_graph.db` (synthetic graph from `MusicHistory.Layout demo`) |

MIDI paths in the DB are resolved against the DB's own folder, so `../songs/<work_id>/score.mid`
next to `data/graph/` finds `data/songs/`.

## Controls

| Input | Action |
|---|---|
| Hover | Highlights the song, the songs that influenced it and the songs it influenced. Shows its secondary edges and its card at the top right: title, artist · year, key · BPM and its main loop as a chord ring (see "The song card"). The lineage and the shared identity are on the edge card (click an edge). |
| Left click | Selects a song: a sticky focus that becomes the walkthrough's target. A click on no bubble but within 7 px (`EdgePickPixels`) of a drawn edge selects the edge: its card replaces the song info, and a family tour plays its identity. Clicking empty space clears both. |
| Right drag · W A S D · Q E · wheel | Look · move · down/up · dolly. Hold Shift to move faster. |
| R / Home | Reset to the overview. |
| L | Label every song (by default only the 40 most influential songs are labelled, plus whatever is in focus). |
| V | Show all secondary edges. |
| F | Toggle the live force-directed simulation. It is axis-locked, so songs never leave their dates. |
| H | Show or hide the controls. |
| 1 / 2 / 3 / 4 · M | Walkthrough mode: **lineage** (root → selected song), **subtree** (depth-first from the selected song, or from its root when it has no children; children in date order), **chronological** (every song from the selected one onward), **family** (identity lineages only: every song of one shared identity in time order, see below). M steps to the next mode (family only when the graph has families). |
| Enter | Start the walkthrough from the selected song (or the selected edge's later song). With nothing selected, it starts from the deepest lineage, the largest tree, the first song, or the largest family, depending on the mode. |
| Space · N / → · B / ← · Esc | Pause/resume · next step · previous step · leave the tour (the free camera comes back). |
| C | Toggle "compare in C / 120 BPM": plays the normalized MIDI without the key/BPM glide. |
| P · the "Featured paths" button (top centre) | Open the featured paths: curated walks through the graph played from recording previews (see below). |
| M · the "Melody" button (while a path plays its mashup mix or its duet loop) | Show or hide the melody graph above the now-playing strip (see "Mashup mixes and the melody graph"). |
| N (while a narrated path plays) | Captions on or off: the narration's line and its photo card (see "Narrated walkthroughs"; there is no voiceover). On a path without narration N still steps on; → always does. |
| K · the "Duet" buttons | In the featured-paths list: play the selected path's duet loop. While a path plays: switch between its duet loop and its narrated mix (see "Duet loops"). |

Each walkthrough step does four things:

- The camera flies to frame the song and its tree parent.
- The tree edge grows out of the influencer.
- The song's excerpt plays. It starts in the key and BPM of the song heard just before it and glides to its own over `MorphBars` bars (`Morph.Plan` in the Contracts): from the key the previous excerpt ended in to the key this one starts in, and from the tempo the previous excerpt's last bar was actually played at.
- The panel at the bottom shows `Key X → Y · BPM a → b` with the live transposition and tempo, from the plan the player actually plays (`ISongPlayer.CurrentPlan`). In compare mode the target key is the song's home key moved by its `norm_shift` (A minor under relative normalization, C minor under parallel).

"The song heard just before" is the clip the tour was on when the step changed, whichever way it moved: the previous step on a forward advance, the step you left on B/←, the same song on a restart (Enter, C). Only a tour's first song plays natively.

The next step starts when the player raises `Finished`. When the next step plays on the same player, the director does not stop it first, so a song that ended naturally hands off on its bar line. A watchdog advances only when the player makes no progress (its beat stands still, unpaused) for `WatchdogSeconds` (20 s), so an excerpt in a section slower than the song's median tempo is never cut short.

## Featured paths (recording previews)

A featured path is a short, curated walk through the graph: a few songs, each sharing a musical
identity with the one before it, played from 30-second recording previews instead of MIDI. The
paths and their prerendered previews come from `data/audio/renders/paths.json` (version 2,
written by the data pipeline next to the renders; `-musicHistoryPaths <file>` or the loader's
`PathsFile` field picks another). Each step's render already starts in the key and tempo of the
step heard before it and glides to its own over `morph_bars` bars (smoothstep), then plays
natively; the first step plays natively. A missing `paths.json` gives an empty list that says so.

**The panel.** P or the "Featured paths" button opens the list on the left, where the legend was;
the graph is reframed beside it on the years the paths cover. Each row shows the path's title,
subtitle, duration, song count, era and a mini timeline with one dot per song (key colour). Hover
a row, or select one with ↑ ↓ or 1–9, and its route lights up on the graph: its songs glow and
keep their labels, the edges between them light up, a glowing line joins them in play order, and
everything else dims. Below the rows, the hovered (else selected) path's shared identity,
description and songs. Click a row, press Enter or the Play button to play it; Esc closes.

**While a path plays** the list collapses into a now-playing strip at the bottom: the path title,
every step as a chip (click one to jump there), "via <identity>" with the edge kind, family size
and a strong-match badge (z), the live glide (`Key C major → D major (now C#, +1.0 st) · BPM 120 →
96 (now 108.2)`, "gliding" until the song is in its own key and tempo), a recording-preview badge
(or MIDI synth / silent clock when a render is missing), and a time bar in seconds with a tick
where the glide ends. Prev / Pause / Next / Stop, or ← → Space; Enter restarts the step; C
compares in C / 120 BPM (MIDI); Esc stops and returns to the list. The camera flies to each song
framed with the song heard before it, the edge between them grows, and the rest of the route stays
lit. "The song heard before" follows the same rule as the other tours (`PreviousClip`).

**Playback** (`Playback/PreviewSongPlayer.cs`). Only path tours' steps listed in `paths.json`
play recording previews; every other tour plays MIDI as before. The player decodes the MP3 with
UnityWebRequest (the next step is preloaded) and plays it on two AudioSources of its own child
object, so the MeltySynth player's `OnAudioFilterRead` never touches them. `CurrentSemitones` and
`CurrentBpm` follow the render's metadata (`start_semitones`, `start_bpm` → `bpm` over
`morph_seconds`); progress is in seconds. On a natural advance it raises `Finished` about one
second before the end and crossfades (equal power) into the next clip; Next and Back cut with a
short fade; Pause and Stop fade. If no audio device advances `AudioSource.time` (batchmode, audio
disabled) or a file fails to load, a main-thread clock keeps time, so `Finished` still fires. A file
still loading after `LoadTimeoutSeconds` (6 s) starts on that clock, and the recording joins at the
clock's position when it arrives.

## Mashup mixes and the melody graph

When a featured path has a mashup mix in `data/audio/mashups/mashups.json` (version 1, written by
the data pipeline next to one `<path id>/mix.mp3` per path; `-musicHistoryMashups <file>` or the
loader's `MashupsFile` picks another), playing that path plays the mix instead of the per-step
previews: one continuous file in which the root song plays its opening phrase, then each next
song's separated vocal sings over the previous song's instrumental (a changeover, matched in key,
tempo and chords, about 20 s in whole bars), the next song's full mix morphs into its own key and
tempo over two bars, and so on; the last song ends the mix. A path without a mashup, a missing
`mashups.json`, "compare" (C) and the director's `PreferMashups` switched off all play per step as
before. The vocals are audio only: the file holds no words, and the viewer shows none.

**Following the mix** (`Playback/MashupPlayer.cs`, `WalkthroughDirector`). The player decodes the
MP3 with UnityWebRequest and plays it on an AudioSource of its own child object; seeks, Pause and
Stop fade; without an audio device (batchmode) or while the file is still loading after
`LoadTimeoutSeconds`, a main-thread clock keeps time. The segment playing (`segments`), the phrase
beat (`beats`, interpolated, wrapping at the phrase end) and the songs whose vocal and instrumental
are audible (`vocal_audible`, `instrumental_audible`) all come from `mashups.json`. The current step
is the song whose instrumental plays; during a changeover the vocal's song glows as brightly and
the edge between the two lights up and grows, and the camera frames both (above the melody
graph). Next / → and Back / ← jump to where the next / previous song's vocal enters; a chip jumps to
that song's entry; Enter restarts the current one.

**The now-playing strip** names the segment: `Changeover  <vocal song> vocal over <instrumental
song>  via <identity>`, `Morph  <song> glides into its own key and tempo` or `Full mix  <song>`, then
the key and BPM heard, the chord match (`chord_match`), the vocal's transposition and tempo ratio
and the beat alignment, as far as they fit. The instrumental's chip is lit (`BACKING` during a
changeover), the vocal's chip is marked `VOCAL` in its melody colour, and the time bar covers the
whole mix with the changeovers (white) and morphs (accent) marked above it.

**The melody graph** (`Viewer/MelodyGraphPanel.cs`) sits directly above the strip, the same width
(landscape; for the vertical layout see below):

- x is the position in the shared phrase (`phrase_beats`), with bar lines; y the sung pitch in the
  normalized C major / A minor frame, with note-name ticks (C bright).
- Every song's melody (`songs[].melody`, pitch-tracked from its separated vocal stem) is drawn on top
  of the others in its own muted colour; the legend lists title and year. A vocal window that wraps
  past the phrase's end is drawn in two pieces.
- The melodies whose vocal is audible now are bright and thicker, each with a **point of light**
  riding on it at the playhead's beat and the melody's pitch there (dimmer while the voice breathes).
- The chord colour strip along the bottom shows the instrumental's chords in the viewer's key palette
  (circle-of-fifths hue, minor darker) with roman numerals, the current chord underlined; during a
  changeover a thin strip above it shows the vocal's own chords, so the match is visible.
- A playhead with a `bar n · <chord>` tag runs through the graph and the strips.

**Vertical (portrait) layout.** The melody graph is three times larger: three times the pixels per
beat and per semitone (the plot is 498 px tall instead of 166; the panel 604 instead of 272, the
title row and the chord strip keep their size). It no longer fits the width, so it pans: the white
playhead stays at the panel's centre, which is the screen's centre, and the melodies, the chord
strips, the bar lines and the light points move right to left under it. The phrase-folded x of a
narrated mix wraps around the phrase as it pans (a vocal crossing the phrase end runs on as one
line). The panning content is drawn into a masked strip a few beats wider than the plot and only
shifted per frame; it is redrawn when the playhead has moved `RedrawPixels` (360 px) from where it
was drawn. Landscape keeps the static phrase graph.

The light points bloom through URP. uGUI is drawn after URP's post-processing, so they are not
drawn on the panel itself: `Viewer/MelodyLightRig.cs` keeps one HDR quad per song
(`MusicHistory/MelodyGlow`, far above the bloom threshold) on layer 31, far below the graph, seen
only by a dedicated orthographic camera that frames the panel (one unit = one reference pixel) and
renders with its own post-processing and its own bloom volume (on layer 31, so the graph's bloom
and look are untouched) into a texture that a RawImage adds onto the panel. The graph's camera
neither renders layer 31 nor sees its volume.

## Narrated walkthroughs (DESIGN §15)

A featured path whose mashup has narration in `data/audio/narration/narration.json` (version 1,
written by the pipeline's `narration` stage; `-musicHistoryNarration <file>` or the loader's
`NarrationFile` picks another) is narrated while it plays its mix. The artist photos come from
`data/images/artists.json` and `data/images/artists/<image id>.jpg` (`-musicHistoryArtists <file>`
or `ArtistsFile`). Paths in both are under `MusicHistory.PipelinePaths.Data()`. A missing file
leaves the paths unnarrated (the console says which).

- **Captions only, no voiceover** (`Playback/NarrationPlayer.cs`). Each cue's line shows as a
  caption from its `at` time on the mix clock (`MashupPlayer.CurrentSeconds`), held long enough to
  read: at least the cue's spoken length (`seconds`), extended to a reading time (15 characters per
  second, at least 2 s) plus 0.6 s, but never into the next cue. The cue WAVs stay on disk for future
  use and are never loaded or played (no AudioClip, no AudioSource), and the mix is never ducked
  (`MashupPlayer.DuckGain` stays 1; the `DuckEnvelope` is kept for when a voice comes back). The mix
  is the master: a pause, a seek (Next, Back, a chip, Enter) or Stop changes the caption at once;
  N shows or hides the captions.
- **Caption and photo card** (`Viewer/NarrationOverlay.cs`). The line shows as a subtitle (our own
  narration, never lyrics), and the cue's image as a card with the photo, whom it shows and, always,
  `Photo: <author>, <license> (Wikimedia Commons)` under it. Both fade and slide in and out. A photo
  without a licence, or whose file is missing, is never shown. The strip's kicker says `CAPTIONS  N`
  (`CAPTIONS OFF  N` after N).
- **Layout** (`Viewer/ViewerLayout.cs`) follows `Screen.width` / `Screen.height` at runtime (a resized
  Game view, the Recorder's output size). Horizontal keeps the layout above: the caption above the
  melody graph (above the strip when the graph is hidden), the card in the column right of the strip
  (beside the caption on 4:3), and the songs framed above the caption. Vertical (height more than 1.2
  × width, e.g. 1080x1920) scales the canvases to a 1280-wide reference and stacks, from the top: the
  graph view, the melody graph (three times larger, panning), the narration band (330 px: photo card
  left, caption right; only while a narrated mix plays, otherwise the graph sits right on the strip)
  and the now-playing strip; the Paths button moves to the top right and the legend and song info
  start under it. The 3D view gets about 28% of a 9:16 frame above the larger graph (it had a third).

### Recording a narrated path

**MusicHistory → Record narrated path → Horizontal 1920x1080** or **Vertical 1080x1920** records the
path playing or selected in the featured-paths panel (in play mode), else the path recorded last,
else the first narrated path with a mashup. It opens `Assets/Scenes/SongInfluenceGraph.unity` if
needed, enters play mode, starts the tour paused while the mix loads, starts the
Unity Recorder (`com.unity.recorder` 5.1.7: Game view at the output size, constant 30 fps, H.264 MP4
with the AudioListener's sound), lets the layout adapt (legend and Paths button hidden), plays the
tour from the top and stops 1.5 s after it ends. **Stop recording** in the same menu stops early.
The file is `<MusicHistory>/data/recordings/<path id>_<horizontal|vertical>.mp4`. The narration is in
it as captions only (no voiceover).

From the command line (not `-batchmode`: the Recorder needs the Game view; the project must not be
open in another editor), the editor quits when the file is written (exit code 0):

```bash
"$UNITY" -projectPath "$PWD" -executeMethod MusicHistory.EditorTools.PathRecorder.Record -path <path id> -format vertical -logFile "$PWD/record.log"
```

## Duet loops (DESIGN §16)

A featured path with a duet loop in `data/audio/duets/duets.json` (version 1, written by the
pipeline's `duets` stage next to one `<path id>/loop.mp3` per path; `-musicHistoryDuets <file>` or
the loader's `DuetsFile` picks another; a missing file offers no duets) can play it instead of its
narrated mix: two vocals at all times, all in the root song's key and tempo over the root's
instrumental bed, pair k = S_k + S_(k+1), handing off around the path and looping back to the
start, with no narration.

- **Choosing it.** In the featured-paths list a path with a loop says `duet loop` on its row and has
  a **DUET** button there; the footer has **Duet loop  K** next to **Play path**; K plays the selected
  path's loop. While a path plays, K (or the strip's **Duet / Mix** button) switches between its
  duet loop and its narrated mix, from the top.
- **Playback** (`Playback/DuetPlayer.cs`). The MP3 is decoded once to PCM, cut to the contract's exact
  length when the decoder left MP3 padding on it, and played by a looping AudioSource of its own child
  object: the wrap from the last sample to the first happens on the audio thread, sample-accurately,
  and the file is rendered circularly, so it is seamless. The clock follows the source's sample
  position, unwrapped across the wrap (`TotalSeconds`, `Cycle`) and smoothed for display; seeks,
  Pause and Stop fade. Next / → and Back / ← jump to the next / previous pair's start, round the
  loop; a chip jumps to the pair it leads; the tour never completes.
- **On the graph.** Both singers glow and the edge between them lights up (and grows the first time);
  during a handoff the borrowed instrumental's song is marked (it glows faintly); the camera frames
  the pair. The strip reads `Duet: A + B over <root>` (and, in a handoff, which vocal enters over
  whose instrumental), the key, the BPM, the chord match and `LOOP n  m:ss / m:ss`; the singers'
  chips say VOCAL, the root's BED, a handoff's borrowed song INSTRUMENTAL; the time bar marks the
  handoffs.
- **The melody graph** is a timeline of mix beats in both layouts, panning right to left under the
  centre playhead and wrapping seamlessly at the loop point (one phrase per plot width in landscape,
  three times larger in portrait); the two singing melodies are bright and thicker with their bloom
  lights, the others muted; the chord strip is the bed's chords.

### Recording a duet loop

**MusicHistory → Record duet loop → Horizontal 1920x1080** or **Vertical 1080x1920**, or
`-variant duet` on the command line (default `narrated`), records one full cycle from the loop's
start plus the first 4 bars after the wrap (so the seamless loop point is in the video) to
`<MusicHistory>/data/recordings/<path id>_duet_<horizontal|vertical>.mp4`:

```bash
"$UNITY" -projectPath "$PWD" -executeMethod MusicHistory.EditorTools.PathRecorder.Record -path <path id> -format vertical -variant duet -logFile "$PWD/record.log"
```

## Photos on the bubbles

Every song whose artist has a catalogued photo (`data/images/artists.json`, DESIGN §15) shows it as a
round, camera-facing picture on its bubble (`Viewer/BubblePhotos.cs`, `Resources/BubblePhoto.shader`):
a child quad just in front of the sphere, 72% of the bubble's diameter so the key colour and the
decade ring stay around it, cropped to a square (a portrait keeps its upper part), scaling with the
bubble and following its state (a dimmed bubble's photo dims and greys). Songs map to photos by the
catalogue's `work_ids`, else by the artist's name (or its lead artist before "feat." / "&"); only
photos with a file and a licence are used. Photos are decoded lazily (one per frame, the first time
one of their bubbles is in view) and shared with the narration's popup card, with mipmaps; the
quads have no collider, so hover and click pick the bubble as before; smaller than 12 px on screen
they are not drawn. The credit stays on the popup card.

## The song card

The info panel at the top right is a compact card: the title, `artist · year`, `key · BPM`, and the
song's main loop (`song_node.main_loop`, its first reading: roman numerals relative to the major, so
in the normalized C major frame) as a ring (`Viewer/ChordRing.cs`): one arc per chord, clockwise
from 12 o'clock (marked), in the same chord colours as the melody graph's chord strip
(`ChordPalette`: the key palette at the chord's root, minor and diminished darker), its roman
numeral beside it. While a mashup mix plays, the ring shows the progression heard (the
instrumental's chords over the phrase, sized by their length) with a hand at the music's place and
the chord under it in the middle; during a duet loop, the bed's chords over the current phrase. The
lineage and the shared identity moved to the edge card (click an edge).

## Identity lineages (DESIGN §8b)

The influence stage can write two kinds of graph. The viewer reads `graph_meta.edge_semantics` to tell them apart:

| `edge_semantics` | Meaning | How the viewer describes an edge |
|---|---|---|
| `identity_lineage` | The two songs share a musical identity: a loop, a chord schema or progression, or an exact passage. It is shared DNA, not proven copying. | `Shares: <identity> · family of N songs`, plus `strong match (z …)` on a strong match. Bits are never shown. |
| `strict_evidence`, or the key is missing (older databases and `--mode evidence`) | Strict v2 borrowing evidence. | `Via <channel> · N bits · z …`, plus the evidence text. Unchanged from before. |

**Families.** For identity lineages the viewer also reads `identity_family` and `song_family` when both tables exist. Each edge's family is the one family both songs belong to whose `label` equals the edge's `evidence`. It must be exactly one, otherwise the edge is reported as a contract problem. On the real graph all 1,713 edges resolve. An edge is a **strong match** when its family has kind `strong`. Without the tables, the viewer falls back to `z > 0`. In the real graph, 66 edges are strong matches, and those are exactly the edges with `z > 0`.

**HUD.** The info panel shows `Shares: <identity>` for the song's tree edge, and says `Shares identities with N earlier · M later` instead of "influenced by". Click an edge to see its **edge card**, which shows:

- the two songs;
- the identity, the family size and the strong-match z;
- the kind of identity (repeating chord loop, named chord schema, chord progression schema, or exact shared passage);
- whether it is a tree edge or a secondary edge;
- the note "a shared musical identity, not proven copying".

The edge lights up, its two songs glow, the rest of its family stays undimmed and everything else dims.

**Legend.** It explains edges as shared musical identities and says they are not proven copying. It lists the identity colours (chord progression orange, loop gold, bass line green, melody blue, melody + harmony white) and highlights strong matches. Strong-match tree edges are drawn in the brightest tier (`EdgeTier.Strong`).

**Family walkthrough (4 or M, then Enter).** The tour plays every member of one family in time order: `song_family` members by node id, which is `(time_value, work_id)` order. The viewer picks the family as follows:

| Selection | Family played |
|---|---|
| An edge | The edge's family |
| A song | The song's strongest family: the highest `song_family.strength` among the families it shares with another song, ties to the smaller family, then the lower id |
| Nothing | The largest family |

A song with no family has nothing to play. The idle panel says which family Enter will play, and why that one.

Each step morphs from the song heard just before, exactly as in the other modes (`WalkthroughDirector.GoTo`). The camera frames the song together with the previous song of the family. The family's edge into the song grows: the one from that previous song if there is one, otherwise the tree edge, otherwise the family's highest-scoring edge. The panel shows `Shares: <identity> · family of N songs`.

Each song plays where the identity sounds in it (`TourPlanner.Window`):

| Window | Used when | Count on the real graph |
|---|---|---|
| Its own excerpt | The excerpt starts on the bar of the identity's first visit (`song_family.first_beat`). The excerpt's entry and exit keys apply. | 727 |
| An edge span | Otherwise, if the influence stage exported a span for an edge of this family at this song. | 250 |
| 8 bars from the bar of the first visit | Neither of the above. | 171 |

The counts are over the 1,148 memberships in families that more than one song shares.

**Limitations.**

- The graph stores no key region for a window outside the song's own excerpt, so for those windows the key handoff uses the home key (the entry and exit columns read as NULL). The panel marks such windows with "home key assumed".
- The graph stores no song length, so an 8-bar first-visit window can run past the end of a short file. The tail is then silent.
- Mono.Data.Sqlite reads `REAL` columns as single precision, so window comparisons allow `TourPlanner.BeatTolerance` (0.001 beat).

## Visual encoding

| Element | Meaning |
|---|---|
| Time | Runs left → right (`TimeAxis` on the loader: `LeftToRight`, `BottomToTop`, or `AsLaidOut`). Decade ticks and year labels sit under the graph, and a faint ring marks each decade. Positions come from the layout stage. When they are NULL (layout has not run), a deterministic fallback pins each song to its date. |
| Bubble | Area ∝ descendants + 1: the layout's `display_radius`, else 0.2·√(descendants+1), never smaller than 4 px on screen. |
| Bubble fill | Key colour. The hue is the key signature on the circle of fifths: C major and A minor share a hue. Minor keys are darker. |
| Bubble ring | Decade colour. |
| Tree edge | Always visible. Tapered, wide at the influencer. Coloured by the channel that carried the influence: chords orange, melody blue, melody + harmony white, bass green, loop gold. Edges leading into big subtrees (trunks) are wider and brighter. Resting edges are drawn with additive light, so bundles of edges read as flows rather than a tangle. |
| Strong match (identity lineages) | A tree edge whose shared identity is an exact passage above the calibrated v2 threshold. Drawn brightest (`EdgeTier.Strong`), in its channel colour. |
| Secondary edge | Hidden until hover (or V). A selected edge's family shows all of its edges. |
| Labels | Keep a constant size on screen, independent of bubble size. Always drawn on top. When two labels would overlap, the lower-priority one is hidden, in this order: focus, then related songs, then the most influential songs. A label that would sit under a HUD panel or be cut by the screen edge is hidden too, unless it is the focus label (`LabelLayer.KeepClear`, fed by `GraphHud.PanelScreenRects`). |

## Data contract (read-only)

The viewer reads `graph_meta`, `nodes`, `song_node` and `influence_edges` exactly as specified in
DESIGN §10. `node_layout_metadata` (`mass`, `display_radius`) and the latest `layout_run`
(`time_axis`, `time_direction`, `year_scale`, `min_time`, `params_json`) are optional. The
`song_node` columns `entry_tonic_pc`, `entry_mode`, `exit_tonic_pc` and `exit_mode` are read
when present (`PRAGMA table_info`); older databases without them read as NULL, which means the
home key. `graph_meta.edge_semantics` and the DESIGN §8b tables `identity_family` and
`song_family` are optional too. A missing key reads as `strict_evidence`, and missing tables as a
graph with no families.

The viewer also relies on these invariants, and reports any that fail as warnings (validation
treats them as failures):

- Node ids run 1..N with no gaps, in order of (time_value, work_id).
- Every non-root node has exactly one tree edge, and it comes from `tree_parent_node`.
- Every edge's source is earlier than its target.
- `descendants`, `tree_depth`, `tree_root_node`, `in_degree` and `out_degree` agree with the edges.
- For identity lineages with family tables: every `song_family` row names an existing song and family, each family's `size` equals its number of rows, `family_count` matches, and each edge's evidence names exactly one family that both of its songs share.

The database must be readable by Unity's bundled SQLite 3.15. That rules out STRICT tables,
generated columns, and window functions or UPSERT inside views or triggers. Use
`journal_mode=DELETE`.

The viewer never reads or shows lyrics. Every string shown from the database is escaped
(`<noparse>`).

## Code map

| Path | Role |
|---|---|
| `Assets/MusicHistory/Viewer/SongGraphLoader.cs` | Scene entry point (GUID of the old `LoadFromSqliteDb`). Resolves the DB path, builds nodes, edges, timeline, HUD, hover and walkthrough, and frames the overview. Handles the F/L/V/H/R keys. |
| `Viewer/SongGraphData.cs` | `SongGraphReader`: Mono.Data.Sqlite reader for §10, MIDI path resolution, invariant checks. It also reads `edge_semantics`, `identity_family`, `song_family` and each edge's family (DESIGN §8b). |
| `Viewer/GraphFrame.cs` | Maps layout positions to the display time axis, fits time_value → coordinate, fallback layout. |
| `Viewer/SongNode.cs`, `InfluenceEdge.cs` | Song bubble (sphere impostor) and tapered edge, with an animatable `VisibleFraction`. |
| `Viewer/GraphMaterials.cs` | Shared material cache: 201 bubble and 10–30 edge materials for the 1000-song demo graph. No renderer owns a material. |
| `Viewer/LabelLayer.cs`, `TextBox.cs` | Constant-screen-size world labels with priority decluttering. |
| `Viewer/TimelineAxis.cs` | Decade axis, ticks, year labels and rings. |
| `Viewer/HoverHighlighter.cs`, `GraphHud.cs` | Hover, song and edge selection (screen-space edge pick) and highlighting, featured-path routes. Screen HUD: legend, song info or edge card (`Shares: …` for identity lineages), walkthrough panel. |
| `Viewer/FeaturedPathsPanel.cs`, `RouteLine.cs`, `UiKit.cs` | The featured-paths button, list and now-playing strip (uGUI on the HUD canvas, EventSystem with InputSystemUIInputModule); the glowing route line; procedural rounded sprites and layout helpers. |
| `Playback/PathCatalog.cs` | `paths.json` v2 reader (no Unity API): paths, steps, via, the glide maths, contract problems; binds work ids to songs. |
| `Playback/PreviewSongPlayer.cs` | Recording-preview `ISongPlayer` for path tours: decode, crossfade, fades, main-thread fallback clock. |
| `Playback/MashupCatalog.cs`, `MashupPlayer.cs` | `mashups.json` v1 reader (no Unity API): segments, beats, songs (melody, chords, audible spans), phrase-beat interpolation, segment lookup, binding to the paths and the graph; the mix player (decode, seek, fades, fallback clock). |
| `Viewer/MelodyGraphPanel.cs`, `MelodyLightRig.cs`, `UiShapes.cs` | The melody graph (own Screen Space - Camera canvas); the bloomed light points (dedicated camera, bloom volume and texture); anti-aliased polylines and rectangles for uGUI. |
| `Viewer/CameraControl.cs`, `SceneLook.cs` | Free-fly camera (`InputEnabled`, `SyncRotationFromTransform`, null-safe input); gradient sky and bloom. |
| `Walkthrough/TourPlanner.cs`, `WalkthroughDirector.cs`, `CameraFraming.cs` | Tour sequences (including the family tour's family choice, order and per-song windows), the step state machine, and perspective-correct framing that keeps the HUD clear. |
| `Playback/SilentSongPlayer.cs` | Timer-based `ISongPlayer`: the same morph tempo maths, raises `Finished`. |
| `Playback/SongPlayerDiscovery.cs` | Chooses the player: an `ISongPlayer` component on the loader object; else `MusicHistory.Audio.SongPlayer`, added by reflection; else the silent player. The silent player also covers songs whose MIDI file is missing. |
| `Assets/FDG/ForceDirectedGraph.cs` | Unity-FDG simulation with an axis lock, a Burst job, persistent buffers and a `Stepped` event. |
| `Assets/Resources/SongBubble.shader`, `GlowingEdge.shader` | Unlit URP shaders with properties in the `UnityPerMaterial` CBUFFER (SRP Batcher compatible). |
| `Assets/Resources/MelodyGlow.shader` | HDR sprite shader (intensity, configurable blend) for the melody graph's light points and their additive composite. |
| `Assets/MusicHistory/Editor/Validation.cs`, `LineageValidation.cs`, `PlayModeBench.cs` | Headless checks (identity lineages in `LineageValidation.cs`), screenshots and the play-mode benchmark. |
| `Assets/MusicHistory/Editor/PathsValidation.cs`, `MashupValidation.cs`, `PathsPlayMode.cs` | Featured paths and mashup mixes: edit-mode checks and screenshots, play-mode checks. |
| `Playback/NarrationCatalog.cs`, `NarrationPlayer.cs` | `narration.json` v1 reader (cues, lookups on the mix clock, contract problems); the narration player (cue scheduling on the mix clock, voice AudioSource, duck envelope). |
| `Viewer/ArtistImages.cs`, `NarrationOverlay.cs`, `ViewerLayout.cs` | `artists.json` reader and on-demand JPG textures, the credit line; the caption and photo card; the horizontal / vertical layout. |
| `Assets/MusicHistory/Editor/NarrationValidation.cs`, `PathRecorder.cs` | Narration (captions) checks (no scene needed); recording a narrated path or a duet loop with the Unity Recorder. |
| `Playback/DuetCatalog.cs`, `DuetPlayer.cs` | `duets.json` v1 reader (no Unity API): segments, pairs, beats, the bed's chords, songs, the loop clock, contract checks, binding; the seamless loop player. |
| `Viewer/MelodyScroll.cs` | The panning melody graph's maths (no Unity API): wrapping, pieces in unwrapped beats, the line's pitch, images. |
| `Viewer/BubblePhotos.cs`, `Assets/Resources/BubblePhoto.shader` | Artist photos on the bubbles. |
| `Viewer/ChordRing.cs` | The shared chord palette, main-loop parsing and the chord ring of the song card. |
| `Assets/MusicHistory/Editor/DuetValidation.cs` | Duet loops, the vertical melody graph and the bubble photos in edit mode. |
| `Assets/MusicHistory/Contracts/` | Shared with the audio module (foundation, do not edit). |

## Performance (measured)

Measured on 2026-09-29 with the 1000-song demo graph (`data/graph/demo_graph.db`: 2114 edges,
959 of them tree edges). Hardware: RTX 2080 Ti, D3D11, editor play mode run headlessly by
`PlayModeBench`.

| Phase | Player loop, mean (p95) | Full 1920×1080 MSAA×4 render + GPU sync, mean (p95) |
|---|---|---|
| Idle overview | 0.5 ms (0.9) | 5.4 ms (8.6) |
| Hover churn, a new focus every 10 frames | 0.8 ms (2.3) | 5.3 ms (7.9) |
| Running walkthrough | 0.9 ms (2.0) | 5.6 ms (7.9) |
| Live simulation (F) | 10.0 ms (11.8) | 5.4 ms (7.6) |

Other measurements:

- Building the graph from the DB takes 0.7–1.3 s.
- A focus change takes 2.7 ms at the median. It creates labels lazily the first time a song is hovered.
- Garbage collection when idle is about 140 B per frame.
- Materials are shared: 201 for bubbles and 20 for edges, all SRP Batcher compatible.

Measured again on 2026-09-30, after the identity-lineage changes, on the same hardware. Two other
Unity editors were open during these runs. The family tour played the 74-song "I-IV-V-I cadence"
family at time scale 30.

| Phase | Demo graph: player loop, mean (p95) | Demo graph: render + sync | `music_graph.db` (identity lineages): player loop | `music_graph.db`: render + sync |
|---|---|---|---|---|
| Idle overview | 0.58 ms (0.69) | 5.2 ms (6.4) | 0.56 ms (0.65) | 5.6 ms (6.6) |
| Hover churn | 0.81 ms (2.49) | 6.9 ms (9.7) | 0.79 ms (2.22) | 5.7 ms (8.0) |
| Running walkthrough (lineage) | 0.89 ms (2.03) | 5.3 ms (6.9) | 0.80 ms (1.92) | 5.1 ms (6.5) |
| Running family tour | not run (no families) | not run | 0.85 ms (2.07) | 5.2 ms (6.3) |
| Live simulation (F) | 9.46 ms (11.48) | 8.5 ms (10.5) | 8.05 ms (9.42) | 8.5 ms (11.5) |

A focus change took 2.6 ms at the median on the demo graph and 1.9 ms on `music_graph.db`, where
the graph built in 647 ms. An earlier run on `music_graph.db` in the same session was slower
throughout (family tour 2.10 ms (6.42) loop and 11.3 ms (39.9) render; idle render 8.3 ms (20.1)),
so these numbers vary with machine load.

## Headless validation

Run from the repo root. Keep `-batchmode` but leave out `-nographics`, so the camera can render.
Close any editor that has this project open first.

```bash
# Build the fixture graph: 13 real MIDI files, NULL positions.
.venv/Scripts/python.exe tests/unity/graph_fixture.py build
.venv/Scripts/python.exe -m pytest tests/unity -q

UNITY="C:/Program Files/Unity/Hub/Editor/6000.6.3f1/Editor/Unity.exe"
# Edit-mode validation: counts, invariants, hover, tours, the silent player's clock,
# axis lock, screenshots. Writes data/screens/*.png and validation.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.Validation.Run   -musicHistoryDb "$PWD/data/graph/demo_graph.db" -logFile "$PWD/validate.log"
# Options: -validationDb <db> -validationFixtureDb <db> -validationLineageDb <db> -validationOut <dir> -validationWidth/-validationHeight

# Play-mode benchmark (the scene builds itself through Start). Writes data/screens/playmode_bench.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.PlayModeBench.Run   -musicHistoryDb "$PWD/data/graph/demo_graph.db" -logFile "$PWD/bench.log"
```

Relative `-musicHistoryDb` paths are tried against Unity's working directory (the project
folder), then against the repository root.

The identity-lineage checks (`Editor/LineageValidation.cs`) run on `data/graph/music_graph.db`
when its `edge_semantics` is `identity_lineage`; `-validationLineageDb <db>` picks another database.
They check the following:

- The semantics key and the family tables are read in full.
- Every edge's family is resolved, and matches an independent SQL join.
- Strong matches are exactly the edges with `z > 0`.
- The HUD, the edge card and the legend wording hold for both kinds of graph: `Shares:` and never
  bits for identity lineages, bits and z for strict evidence. The strict-evidence case is also
  checked on a copy stripped of `edge_semantics` and the family tables, which stands in for an
  older database.
- The family tour order equals the time order of `song_family` for every family (SQL).
- The strongest family agrees with SQL for every song.
- Every family window is bar aligned and contains the identity's first visit.
- The family tour morphs from the song heard before, on Next, B/← and a natural advance.
- No drawn label lies under a HUD panel.

They write `lineage_overview.png`, `lineage_walkthrough.png`, `lineage_edge_card.png` and
`family_tour.png`. When the graph has families, `PlayModeBench` adds a family-tour phase with the
real synth and writes `playmode_family_tour.png`.

Both commands exit with code 0 only when every check passes. In the editor, the same checks
run from **MusicHistory → Run Validation**.

Featured paths are checked by `Validation.Run` too (on `music_graph.db`), and on their own:

```bash
# Edit mode: the paths.json contract against the graph (work ids, via = the edge's identity,
# start key/tempo = the step before, 44.1 kHz MPEG files), the glide readout and crossfade on the
# preview clock, the path tour, the panel (P, 1-9, arrows, hover, Enter, strip, Esc), label
# teardown safety, and paths_button.png, paths_panel_idle.png, paths_panel.png, paths_playing.png.
# -validationPaths <paths.json> adds another catalog (a fixture); the real one is used when present.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.Validation.RunPaths -logFile "$PWD/paths.log"
# Play mode: EventSystem clicks, the first render decoded (44.1 kHz, length = paths.json) after a
# simulated slow load (clock first, then the recording joins), Pause and Stop fades, a whole path on
# the main-thread clock (order, Finished once, crossfades), and scene teardown in varied orders
# (the reported MissingReferenceException case included) with no exception logged.
# -musicHistoryPaths <paths.json> picks another catalog. Writes data/screens/paths_playmode.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.PathsPlayMode.Run -logFile "$PWD/paths_play.log"
# One whole path in real time on the audio device, as a listener hears it (the listener is muted):
# per step the load latency, decoded length, clock, when Finished fired, the crossfade, the audio
# clock against the wall clock, and the level of the decoded recording under the playhead.
# Writes data/screens/paths_fullplay.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.PathsPlayMode.RunFullPath -pathsPlayId orbison-to-flowers -logFile "$PWD/paths_full.log"
```

Mashup mixes are checked by `Validation.Run` and `Validation.RunPaths` too, and on their own:

```bash
# Edit mode: the mashups.json contract (segments, beats, songs) and its binding to the paths and the
# graph, phrase-beat interpolation, segment lookup, audible sets, melody pitch lookup; the chain
# (root song first, changeovers in path order, last song last); a whole mix on the clock (steps,
# both songs lit in a changeover, the edge, the strip); the melody graph's geometry (every sung
# point inside the plot, the playing melodies bright, a light point at the playhead's beat and the
# melody's pitch, the chord strip of the instrumental); the bloom (the same frame with and without
# the light's bloom volume, and with the graph's bloom off); no overlap with the HUD at 1920x1080;
# M, Next/Back, chips, Space, C, Esc, and the fallback to per-step previews. Catalogs: the real
# mashups.json when present, -validationMashups <file>, and a synthetic one built from the paths.
# Writes melody_graph.png (mid-changeover), melody_graph_nobloom.png and mashup_validation.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.Validation.RunMashups -logFile "$PWD/mashups.log"
```

Narrated walkthroughs are checked on their own, without opening a scene (**MusicHistory → Run
Narration Validation**, or):

```bash
# narration.json and artists.json parsing (fixtures; the real files too once the pipeline wrote
# them: WAVs kept on disk, photos with author and licence, cues inside their mix), the captions on
# the mix clock (from 'at', held for their reading time, pause, seeks into, before and inside a
# line, N off/on, Stop) with no voiceover (no narration AudioSource, the music at full gain through
# every cue), the (unused, kept) duck envelope (0.15 s attack, 0.6 s release), the credit
# line and on-demand JPG decoding, and the horizontal / vertical layout rects (no overlaps at
# 1920x1080, 1080x1920 and six other sizes). Writes narration_validation.json.
"$UNITY" -batchmode -projectPath "$PWD" -executeMethod MusicHistory.EditorTools.NarrationValidation.Run -logFile "$PWD/narration.log"
```

`PathsPlayMode.Run` also plays a mashup in play mode (-validationMashups <file>, else the real
catalog): the mix decoded (44.1 kHz, length = mashups.json) and playing in real time, the melody
graph on its camera canvas, Space, Next, M, the whole mix on the main-thread clock at time scale 8
(every segment in order, finished once), and the same path per step without mashups.

Duet loops, the vertical melody graph and the bubble photos are checked on their own (**MusicHistory
→ Run Duet Validation**, or):

```bash
# Pure: the duets.json contract on an inline loop (segments, pairs, beats, chords, melodies, audible
# spans), the loop clock (segments, pairs, the mix beat continuous through the loop point), two vocals
# audible everywhere, contract violations, binding, the panning maths. Scene, for a synthetic catalog
# built from the paths, the real duets.json (when written) and -validationDuets <file>: the list offers
# the duet, K plays it (no narration), a whole cycle and past the wrap on the clock (pairs, singers
# lit, the edge between them, the borrowed instrumental marked, the camera on the pair, the graph
# panning under a centre playhead ±0.5 px with every light on it and on its line, no jump at the
# wrap), the strip text, Next/Back round the loop, Space, M, K, C, Esc; portrait 1080x1920 (the graph
# three times larger, panning, centred, clear of the HUD); a narrated mix in portrait (wrapping round
# its phrase) and back in landscape (the static graph); photos on bubbles (synthetic and real
# artists.json: mapping, lazy loading, shared textures, dimming, picking, drawn in front of the
# bubble). Writes duet_landscape.png, duet_portrait.png, melody_portrait.png, bubble_photos.png and
# duet_validation.json.
"$UNITY" -batchmode -projectPath "$PWD" -executeMethod MusicHistory.EditorTools.DuetValidation.Run -logFile "$PWD/duets.log"
# One duet loop in real time on the audio device round its loop point (decoded length = duets.json
# after trimming, two wraps, the clock continuous through them, real-time rate). -duetPlayId <path id>,
# -validationDuets <file>. Writes data/screens/duet_fullplay.json.
"$UNITY" -batchmode -projectPath "$PWD" -executeMethod MusicHistory.EditorTools.PathsPlayMode.RunFullDuet -logFile "$PWD/duet_full.log"
```

## Lyric themes

`Assets/Scenes/LyricThemes.unity` shows the second graph from DESIGN §12: where each song sits
among ten lyrical themes. Open the scene and press Play. It is in the build settings after
`SongInfluenceGraph`.

The viewer (`Assets/MusicHistory/Themes/ThemesViewer.cs`) looks for a themes database in this order:
`-themesDb <path>` on the command line, then `DbPath` on the **Lyric Themes** object, then
`<MusicHistory>/data/graph/themes_graph.db`, then `StreamingAssets/themes_graph.db`, then
`<MusicHistory>/data/graph/themes_demo.db`. If the layout stage has not written positions yet
(`position_x` is NULL), the viewer places the songs itself and says so in the legend. It puts each
song at the barycentre of the anchors weighted by score², then pushes overlapping songs apart.
The result is deterministic.

| Element | Meaning |
|---|---|
| Gold pads on the ring | The ten themes, 36° apart. Each label shows the theme text, its filter key and how many songs have it as their top theme. |
| Bubble position | The layout position: where the pulls of the song's theme scores balance. A song that is all one theme sits on that theme. |
| Bubble colour | Blue for a male singer, pink for a female singer, grey for mixed, nonbinary, unknown or instrumental. |
| Filled / hollow bubble | Classified from the lyrics / from the title only. |
| Faint ring, spokes, half-radius circle | Guides on the ring plane. |

| Input | Action |
|---|---|
| Hover | Shows a card with the title, artist, year, singer, "lyrics" or "title only", and the top three themes with bars and scores. Gold tethers link the song to those themes. |
| Left click | Plays the song's excerpt in its own key and BPM, with no morph. Click it again, or press Space, to stop. |
| Left or middle drag · right drag · wheel | Pan · orbit · zoom toward the pointer. |
| W A S D / arrows · Q E · Z X | Pan · rotate · zoom. Hold Shift to go faster. |
| 1–9, 0 | Show only the songs whose top theme is that theme. Press the key again, or Esc, to show all. |
| L | Label every song (labels that would overlap are hidden). |
| R / Home · H | Reset the view · show the controls. |
| G / Backspace / the top-right button | Back to `SongInfluenceGraph`. |

The themes database holds no lyrics, and the viewer never shows any. Validation checks every
song's hover card: it may contain only the title, artist, year, singer, text source, theme labels
and scores.

```bash
# Edit-mode checks against themes_demo.db and themes_graph.db (-themesValidationDb <db> for one).
# Writes data/screens/themes_overview.png (the DB the scene opens by default), per-DB
# themes_{overview,hover,filter,all_labels}_<db>.png and themes_validation.json.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.ThemesValidation.Run -logFile "$PWD/themes_validate.log"
# Play mode: frame times, a click through the real SongPlayer, and the switch back to the influence graph.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.ThemesPlayModeBench.Run -logFile "$PWD/themes_bench.log"
# Rebuild the scene file (and its build-settings entry) from code.
"$UNITY" -batchmode -projectPath "$PWD/unity" -executeMethod MusicHistory.EditorTools.ThemesValidation.CreateScene -logFile "$PWD/themes_scene.log"
```

In the editor, the same checks run from **MusicHistory → Lyric Themes**.

Measured on 2026-09-30 with the RTX 2080 Ti on D3D11, at 1920×1080 with MSAA ×4. The scene
had 1,012 songs from `themes_graph.db` (fallback positions). With the scene idle, the player loop
took 0.54 ms on average (0.76 ms at p95), and a render plus GPU sync took 3.8 ms (5.8 ms at p95).
Moving the hover to a new song took 0.79 ms at the median; that includes picking and all bubble
states, with 24 or fewer shared bubble materials. Building the scene took about 0.9 s.
