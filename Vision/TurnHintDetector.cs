using System;
using System.Collections.Generic;
using System.Linq;

namespace ChessKit.Vision
{
    /// <summary>
    /// Composes the vision model's advisory "highlighted squares" output into a
    /// side-to-move hint via a strict pair-gate. The model reports EVERY tinted
    /// square (last-move tint, selection, check flash, user analysis paint);
    /// only one pattern constitutes evidence of a last move:
    ///
    ///   exactly TWO highlighted squares, one EMPTY (the from-square) and one
    ///   OCCUPIED (the to-square), where the piece on the to-square could
    ///   legally have arrived from the empty one.
    ///
    /// Anything else - a single square (selection), three-plus squares (user
    /// analysis coloring; castling also highlights only two on the major
    /// sites), paint on two empty squares, or geometry the piece cannot have
    /// played - yields NO hint rather than a wrong one. The mover is the color
    /// of the piece on the to-square; the side to move is the opposite.
    ///
    /// ADVISORY ONLY: callers feed this as one vote into the existing
    /// side-to-move inference; it must never flip a FEN on its own.
    /// </summary>
    internal static class TurnHintDetector
    {
        internal readonly record struct TurnHint(char MoverColor, string FromSquare, string ToSquare)
        {
            /// <summary>'w' when white is to move next (black just moved).</summary>
            public char SideToMove => MoverColor == 'w' ? 'b' : 'w';
        }

        /// <summary>
        /// highlights: algebraic squares in the SAME orientation as the FEN
        /// (the server emits them post-flip, matching the FEN it returns).
        /// placement: the FEN's piece-placement field for the same frame.
        /// </summary>
        public static TurnHint? TryInferLastMove(IReadOnlyList<string>? highlights, string? placement)
        {
            if (highlights == null || string.IsNullOrWhiteSpace(placement))
                return null;

            // Dedup + validate square names; the strict pair-gate needs an
            // exact count of DISTINCT squares.
            var squares = highlights
                .Where(IsValidSquare)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(s => s.ToLowerInvariant())
                .ToList();
            if (squares.Count != 2)
                return null;

            char[,]? board = ParsePlacement(placement);
            if (board == null)
                return null;

            char a = PieceAt(board, squares[0]);
            char b = PieceAt(board, squares[1]);
            bool aEmpty = a == '\0';
            bool bEmpty = b == '\0';
            if (aEmpty == bEmpty)
                return null;                    // both empty (paint) or both occupied (paint/ambiguous)

            string from = aEmpty ? squares[0] : squares[1];
            string to = aEmpty ? squares[1] : squares[0];
            char piece = aEmpty ? b : a;

            if (!CouldPieceHaveMoved(board, piece, from, to))
                return null;

            return new TurnHint(char.IsUpper(piece) ? 'w' : 'b', from, to);
        }

        private static bool IsValidSquare(string? s) =>
            !string.IsNullOrEmpty(s) && s.Length == 2 &&
            s[0] >= 'a' && s[0] <= 'h' && s[1] >= '1' && s[1] <= '8';

        /// <summary>rank 8 = row 0 (FEN order); file a = col 0.</summary>
        private static char[,]? ParsePlacement(string placement)
        {
            string[] rows = placement.Trim().Split(' ')[0].Split('/');
            if (rows.Length != 8)
                return null;
            var board = new char[8, 8];
            for (int r = 0; r < 8; r++)
            {
                int c = 0;
                foreach (char ch in rows[r])
                {
                    if (ch >= '1' && ch <= '8')
                    {
                        c += ch - '0';
                    }
                    else
                    {
                        if (c > 7 || !"prnbqkPRNBQK".Contains(ch))
                            return null;
                        board[r, c++] = ch;
                    }
                }
                if (c != 8)
                    return null;
            }
            return board;
        }

        private static char PieceAt(char[,] board, string sq)
        {
            int col = sq[0] - 'a';
            int row = 8 - (sq[1] - '0');
            return board[row, col];
        }

        /// <summary>
        /// Piece-movement geometry with path blocking, evaluated on the POST-
        /// move board (the from-square is empty now, which is exactly why the
        /// intermediate path can be checked on the current position). Pawn
        /// moves accept both pushes (same file) and diagonal captures - the
        /// captured piece is gone from the board, so a diagonal with an
        /// occupied to-square is consistent either way. Castling shows as a
        /// two-square king move on the highlight pair.
        /// </summary>
        private static bool CouldPieceHaveMoved(char[,] board, char piece, string from, string to)
        {
            int fc = from[0] - 'a', fr = 8 - (from[1] - '0');
            int tc = to[0] - 'a', tr = 8 - (to[1] - '0');
            int dc = tc - fc, dr = tr - fr;
            int adc = Math.Abs(dc), adr = Math.Abs(dr);
            if (adc == 0 && adr == 0)
                return false;

            switch (char.ToLowerInvariant(piece))
            {
                case 'n':
                    return (adc == 1 && adr == 2) || (adc == 2 && adr == 1);
                case 'k':
                    // one step any direction, or the two-file castling slide
                    return (adc <= 1 && adr <= 1) || (adr == 0 && adc == 2);
                case 'b':
                    return adc == adr && PathClear(board, fr, fc, tr, tc);
                case 'r':
                    return (adc == 0 || adr == 0) && PathClear(board, fr, fc, tr, tc);
                case 'q':
                    return (adc == adr || adc == 0 || adr == 0) && PathClear(board, fr, fc, tr, tc);
                case 'p':
                {
                    // White pawns move toward rank 8 (row decreasing).
                    int forward = char.IsUpper(piece) ? -1 : 1;
                    if (dc == 0 && dr == forward)
                        return true;                                    // single push
                    if (dc == 0 && dr == 2 * forward)
                    {
                        // double push only from the pawn's home rank, path clear
                        int homeRow = char.IsUpper(piece) ? 6 : 1;
                        return fr == homeRow && board[fr + forward, fc] == '\0';
                    }
                    return adc == 1 && dr == forward;                   // capture (incl. en passant)
                }
                default:
                    return false;
            }
        }

        private static bool PathClear(char[,] board, int fr, int fc, int tr, int tc)
        {
            int sr = Math.Sign(tr - fr), sc = Math.Sign(tc - fc);
            int r = fr + sr, c = fc + sc;
            while (r != tr || c != tc)
            {
                if (board[r, c] != '\0')
                    return false;
                r += sr; c += sc;
            }
            return true;
        }
    }
}
