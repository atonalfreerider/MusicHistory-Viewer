#nullable enable
using System;
using System.Collections.Generic;
using MusicHistory.Playback;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The melody graph's maths for a melody mosaic (no Unity API, so it is testable outside the
    /// editor). The graph is a timeline of mix beats (every loop one after the other, not folded)
    /// panning under the fixed centre playhead; the loop's beats, chords and notes repeat each loop.
    /// Every loop gets its own lines: the target's melody in every loop (bright while it sounds, a
    /// faint guide in the mosaic loops), each piece in every mosaic loop, each harmony voice in every
    /// harmony loop. Notes become step lines: (start, pitch) → (end, pitch), joined to the next note
    /// when it follows within <see cref="JoinGap"/> beats, else broken.
    /// </summary>
    public static class MosaicTimeline
    {
        /// <summary>Which voice a line draws.</summary>
        public enum Voice { Target, Piece, Harmony }

        /// <summary>Notes closer than this (beats) are joined by the line; further apart the line breaks.</summary>
        public const double JoinGap = .12;
        /// <summary>A light rests on the nearest note this many beats around a rest.</summary>
        public const double RestReach = 1.0;

        /// <summary>One line of the graph: a voice in one loop, in mix beats.</summary>
        public sealed class LineSpec
        {
            public Voice Kind;
            /// <summary>The mix's loop (0 ..) it is drawn in.</summary>
            public int Loop;
            /// <summary>Index into <see cref="Mosaic.Pieces"/> / <see cref="Mosaic.Harmonies"/> (-1 for the other voices).</summary>
            public int Piece = -1, Harmony = -1;
            /// <summary>Index into <see cref="Mosaic.Songs"/> (its colour slot).</summary>
            public int Song;
            /// <summary>The kind of the loop's section.</summary>
            public MosaicSectionKind Section;
            /// <summary>The voice's notes (loop beats).</summary>
            public IReadOnlyList<MosaicNote> Notes = Array.Empty<MosaicNote>();
            /// <summary>The line in mix beats (pieces of it, see <see cref="MelodyScroll.Piece"/>).</summary>
            public List<MelodyScroll.Piece> Sung = new();
            /// <summary>The target in a mosaic loop: a faint guide under the pieces.</summary>
            public bool Guide => Kind == Voice.Target && Section == MosaicSectionKind.Mosaic;
            public double First, Last;
        }

        /// <summary>Where the mix is: mix beat, loop, loop beat, section and the piece sounding.</summary>
        public readonly struct State
        {
            /// <summary>Mix beat (beats from the first, across the loops).</summary>
            public readonly double Beat;
            public readonly int Loop;
            /// <summary>Beat within the loop (0 .. loop beats).</summary>
            public readonly double LoopBeat;
            /// <summary>Index into <see cref="Mosaic.Sections"/> (-1 without sections).</summary>
            public readonly int Section;
            public readonly MosaicSectionKind Kind;
            /// <summary>The piece sounding (mosaic loops; -1 between pieces and in the other sections).</summary>
            public readonly int Piece;
            /// <summary>The piece shown as playing on the graph and the bubbles: the one sounding, else the last before the playhead in the loop, else the first (-1 outside the mosaic loops).</summary>
            public readonly int FocusPiece;

            public State(double beat, int loop, double loopBeat, int section, MosaicSectionKind kind, int piece, int focusPiece)
            {
                Beat = beat;
                Loop = loop;
                LoopBeat = loopBeat;
                Section = section;
                Kind = kind;
                Piece = piece;
                FocusPiece = focusPiece;
            }
        }

        /// <summary>Where mosaic <paramref name="m"/> is at mix time <paramref name="t"/>.</summary>
        public static State At(Mosaic m, double t) => AtBeat(m, m.BeatAt(t));

        /// <summary>Where mosaic <paramref name="m"/> is at mix beat <paramref name="beat"/>.</summary>
        public static State AtBeat(Mosaic m, double beat)
        {
            int loop = m.LoopOfBeat(beat);
            double lb = m.LoopBeats > 0 ? m.LoopBeats : 16;
            double within = Math.Max(0, Math.Min(lb, beat - loop * lb));
            int section = m.SectionOfLoop(loop);
            MosaicSectionKind kind = section >= 0 ? m.Sections[section].Kind : MosaicSectionKind.Original;
            int piece = kind == MosaicSectionKind.Mosaic ? m.PieceIndexAt(within) : -1;
            int focus = kind == MosaicSectionKind.Mosaic ? (piece >= 0 ? piece : m.PieceAtOrBefore(within)) : -1;
            return new State(beat, loop, within, section, kind, piece, focus);
        }

        /// <summary>
        /// Notes as a sung line shifted by <paramref name="offset"/> beats: each note a level step,
        /// joined to the next when it follows within <see cref="JoinGap"/> (an unvoiced point breaks it).
        /// </summary>
        public static List<MelodyPoint> Points(IReadOnlyList<MosaicNote> notes, double offset)
        {
            List<MelodyPoint> points = new(notes.Count * 2 + 4);
            double lastEnd = double.NegativeInfinity;
            foreach (MosaicNote n in notes)
            {
                if (points.Count > 0 && n.Start - lastEnd > JoinGap) points.Add(new MelodyPoint(lastEnd + offset, float.NaN));
                points.Add(new MelodyPoint(n.Start + offset, n.Pitch));
                points.Add(new MelodyPoint(n.End + offset, n.Pitch));
                lastEnd = Math.Max(lastEnd, n.End);
            }
            return points;
        }

        /// <summary>The drawn line of <paramref name="notes"/> shifted by <paramref name="offset"/> beats (mix beats).</summary>
        public static List<MelodyScroll.Piece> Line(IReadOnlyList<MosaicNote> notes, double offset) =>
            MelodyScroll.Pieces(Points(notes, offset), 0, false, double.MaxValue);

        /// <summary>Every line of the graph: the target in every loop, each piece in every mosaic loop, each harmony voice in every harmony loop.</summary>
        public static List<LineSpec> Lines(Mosaic m)
        {
            List<LineSpec> lines = new();
            int loops = m.Loops;
            double lb = m.LoopBeats;
            for (int k = 0; k < loops; k++)
            {
                MosaicSectionKind kind = m.KindOfLoop(k);
                double offset = k * lb;
                lines.Add(Spec(Voice.Target, k, -1, -1, 0, kind, m.Notes, offset));
                if (kind == MosaicSectionKind.Mosaic)
                    foreach (MosaicPiece p in m.Pieces)
                        lines.Add(Spec(Voice.Piece, k, p.Index, -1, Math.Max(0, p.Song), kind, p.Notes, offset));
                if (kind == MosaicSectionKind.Harmony)
                    foreach (MosaicHarmony h in m.Harmonies)
                        lines.Add(Spec(Voice.Harmony, k, -1, h.Index, Math.Max(0, h.Song), kind, h.Notes, offset));
            }
            return lines;
        }

        static LineSpec Spec(Voice voice, int loop, int piece, int harmony, int song, MosaicSectionKind section, IReadOnlyList<MosaicNote> notes, double offset)
        {
            LineSpec s = new()
            {
                Kind = voice,
                Loop = loop,
                Piece = piece,
                Harmony = harmony,
                Song = song,
                Section = section,
                Notes = notes,
                Sung = Line(notes, offset)
            };
            s.First = s.Sung.Count > 0 ? s.Sung[0].First : offset;
            s.Last = s.Sung.Count > 0 ? s.Sung[^1].Last : offset;
            return s;
        }

        /// <summary>
        /// A line is bright (playing) when its voice sounds in the loop now: the target in an original
        /// or harmony loop, the piece shown as playing in a mosaic loop, every harmony voice in a
        /// harmony loop. The target's guide line in a mosaic loop never is.
        /// </summary>
        public static bool Playing(LineSpec line, State now)
        {
            if (line.Loop != now.Loop) return false;
            return line.Kind switch
            {
                Voice.Target => now.Kind != MosaicSectionKind.Mosaic,
                Voice.Piece => now.Kind == MosaicSectionKind.Mosaic && line.Piece == now.FocusPiece && now.Piece >= 0,
                _ => now.Kind == MosaicSectionKind.Harmony
            };
        }

        /// <summary>
        /// Where a light on <paramref name="line"/> rides at mix beat <paramref name="beat"/>: on the drawn
        /// line (<paramref name="voiced"/>), else in a rest on the nearest note within
        /// <see cref="RestReach"/>. False when nothing is sung near.
        /// </summary>
        public static bool LightPitch(LineSpec line, double beat, double loopBeat, out float pitch, out bool voiced)
        {
            if (MelodyScroll.LineAt(line.Sung, beat, 0, out pitch))
            {
                voiced = true;
                return true;
            }
            bool near = Mosaic.NoteAt(line.Notes, loopBeat, out pitch, out _, RestReach);
            voiced = false;
            return near;
        }

        /// <summary>The colour slot's place in the graph's palette: 0 the target, the others cycling through the song colours.</summary>
        public static int PaletteIndex(int slot, int paletteSize) => slot <= 0 ? 0 : 1 + (slot - 1) % Math.Max(1, paletteSize - 1);
    }
}
