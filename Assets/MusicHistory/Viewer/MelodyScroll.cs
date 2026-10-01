#nullable enable
using System;
using System.Collections.Generic;
using MusicHistory.Playback;

namespace MusicHistory.Viewer
{
    /// <summary>
    /// The scrolling melody graph's maths (no Unity API, so it is testable outside the editor): a
    /// cyclic beat axis of period P (the phrase of a narrated mix, the loop of a duet) panned under a
    /// playhead fixed at x = head: beat u sits at head + wrapNearest(u − playhead) · pixels per beat,
    /// so the view runs on across the wrap without a jump. Melodies are cut into pieces of unwrapped
    /// beats (monotonic within a piece) and drawn at every image u + k·P that reaches the window.
    /// </summary>
    public static class MelodyScroll
    {
        /// <summary>A sung stretch: beats unwrapped (rising, possibly past P), pitches voiced.</summary>
        public sealed class Piece
        {
            public readonly List<double> Beats = new();
            public readonly List<float> Pitches = new();
            public double First => Beats.Count > 0 ? Beats[0] : 0;
            public double Last => Beats.Count > 0 ? Beats[^1] : 0;
            public int Count => Beats.Count;
        }

        /// <summary><paramref name="v"/> folded into [0, <paramref name="period"/>).</summary>
        public static double Fold(double v, double period)
        {
            if (period <= 0) return v;
            double r = v % period;
            if (r < 0) r += period;
            return r >= period ? 0 : r;
        }

        /// <summary>The image of <paramref name="d"/> nearest 0: in [−P/2, P/2).</summary>
        public static double WrapNearest(double d, double period)
        {
            if (period <= 0) return d;
            double r = Fold(d + period * .5, period) - period * .5;
            return r;
        }

        /// <summary>Panel x of beat <paramref name="beat"/> (any image) with <paramref name="playhead"/> at <paramref name="head"/>.</summary>
        public static double X(double beat, double playhead, double period, double pixelsPerBeat, double head) =>
            head + WrapNearest(beat - playhead, period) * pixelsPerBeat;

        /// <summary>
        /// Pieces of a melody in sung order: a new piece at an unvoiced point, a gap over
        /// <paramref name="maxGap"/> beats, or a jump back that is not the phrase's wrap. A wrap
        /// (the next point's beat lower by about a period) continues the piece unwrapped, when the
        /// cyclic gap is small. <paramref name="cyclic"/> (a loop): the last point joins the first
        /// of the next cycle when they are within <paramref name="maxGap"/> of each other.
        /// </summary>
        public static List<Piece> Pieces(IReadOnlyList<MelodyPoint> sung, double period, bool cyclic, double maxGap)
        {
            List<Piece> pieces = new();
            Piece? piece = null;
            double offset = 0, last = double.NegativeInfinity, lastRaw = double.NegativeInfinity;
            foreach (MelodyPoint p in sung)
            {
                if (!p.Voiced)
                {
                    piece = null;
                    continue;
                }
                double raw = p.Beat;
                // A wrap past the phrase's end: the beat falls by most of a period.
                if (period > 0 && lastRaw > double.NegativeInfinity && raw < lastRaw - period * .5) offset += period;
                lastRaw = raw;
                double u = raw + offset;
                if (piece == null || u - last > maxGap || u < last - 1e-9)
                {
                    piece = new Piece();
                    pieces.Add(piece);
                }
                piece.Beats.Add(u);
                piece.Pitches.Add(p.Pitch);
                last = u;
            }
            if (cyclic && period > 0 && pieces.Count > 0)
            {
                Piece first = pieces[0], end = pieces[^1];
                double gap = first.First + period - end.Last;
                // The loop continues: the last stretch runs on into the next cycle's first point.
                if (gap >= -1e-9 && gap <= maxGap && first.Count > 0 && end.Count > 0)
                {
                    end.Beats.Add(first.First + period);
                    end.Pitches.Add(first.Pitches[0]);
                }
            }
            return pieces;
        }

        /// <summary>
        /// The pitch of the drawn line at beat <paramref name="beat"/> (any image): linear between the
        /// piece's neighbouring points, exactly as the polyline is drawn. False in a breath.
        /// </summary>
        public static bool LineAt(IReadOnlyList<Piece> pieces, double beat, double period, out float pitch)
        {
            pitch = float.NaN;
            foreach (Piece piece in pieces)
            {
                if (piece.Count < 2) continue;
                // The image of the beat that falls inside this piece's span, if any.
                double k = period > 0 ? Math.Floor((piece.First - beat) / period) : 0;
                for (int j = 0; j < 3; j++)
                {
                    double u = beat + (k + j) * period;
                    if (period <= 0 && j > 0) break;
                    if (u < piece.First - 1e-9 || u > piece.Last + 1e-9) continue;
                    int i = Lower(piece.Beats, u);
                    if (i <= 0)
                    {
                        pitch = piece.Pitches[0];
                        return true;
                    }
                    double b0 = piece.Beats[i - 1], b1 = piece.Beats[i];
                    double t = b1 > b0 ? (u - b0) / (b1 - b0) : 0;
                    pitch = (float)(piece.Pitches[i - 1] + (piece.Pitches[i] - piece.Pitches[i - 1]) * Math.Max(0, Math.Min(1, t)));
                    return true;
                }
            }
            return false;
        }

        /// <summary>First index with Beats[i] ≥ <paramref name="u"/>.</summary>
        static int Lower(List<double> beats, double u)
        {
            int lo = 0, hi = beats.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (beats[mid] < u) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// The images k (u + k·P) of a span [<paramref name="first"/>, <paramref name="last"/>] that
        /// reach the window [<paramref name="lo"/>, <paramref name="hi"/>] (unwrapped beats).
        /// </summary>
        public static (int from, int to) Images(double first, double last, double lo, double hi, double period)
        {
            if (period <= 0) return (0, last >= lo && first <= hi ? 0 : -1);
            int from = (int)Math.Ceiling((lo - last) / period - 1e-9);
            int to = (int)Math.Floor((hi - first) / period + 1e-9);
            return (from, to);
        }
    }
}
