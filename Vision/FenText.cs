namespace ChessKit
{
    /// <summary>
    /// Pure FEN / board-position string helpers extracted verbatim from the
    /// Program live pipeline. None touch shared mutable state or call other
    /// Program members, so they are safe to host in a real class. Program reaches
    /// them unqualified via <c>using static ChessKit.FenText;</c>, so every former
    /// call site is byte-for-byte unchanged.
    /// </summary>
    internal static class FenText
    {
        public static string ExpandBoardPosition(string boardPosition)
        {
            if (string.IsNullOrWhiteSpace(boardPosition))
                return "";

            var sb = new System.Text.StringBuilder(64);
            foreach (char c in boardPosition)
            {
                if (c == '/')
                    continue;

                if (char.IsDigit(c))
                {
                    int count = c - '0';
                    if (count < 1 || count > 8)
                        return "";
                    sb.Append('.', count);
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        public static string IntersectCastlingRights(string existingRights, string detectedRights)
        {
            if (existingRights == "-" || string.IsNullOrEmpty(existingRights))
                return "-";

            if (detectedRights == "-" || string.IsNullOrEmpty(detectedRights))
                return "-";

            var orderedRights = new[] { 'K', 'Q', 'k', 'q' };
            var survivingRights = new System.Text.StringBuilder();

            foreach (char right in orderedRights)
            {
                if (existingRights.Contains(right) && detectedRights.Contains(right))
                {
                    survivingRights.Append(right);
                }
            }

            return survivingRights.Length > 0 ? survivingRights.ToString() : "-";
        }

        public static string ValidateCastlingRights(string boardFEN, string castlingRights)
        {
            if (castlingRights == "-") return "-";

            var ranks = boardFEN.Split('/');
            var validRights = new System.Text.StringBuilder();

            // Check white castling
            if (castlingRights.Contains('K') || castlingRights.Contains('Q'))
            {
                string rank1 = ranks[7];

                bool whiteKingOnE1 = false;
                int fileCount = 0;
                foreach (char c in rank1)
                {
                    if (char.IsDigit(c))
                    {
                        fileCount += (c - '0');
                    }
                    else
                    {
                        if (c == 'K' && fileCount == 4)
                        {
                            whiteKingOnE1 = true;
                        }
                        fileCount++;
                    }
                }

                if (whiteKingOnE1)
                {
                    fileCount = 0;
                    foreach (char c in rank1)
                    {
                        if (char.IsDigit(c))
                        {
                            fileCount += (c - '0');
                        }
                        else
                        {
                            if (c == 'R')
                            {
                                if (fileCount == 7 && castlingRights.Contains('K'))
                                    validRights.Append('K');
                                if (fileCount == 0 && castlingRights.Contains('Q'))
                                    validRights.Append('Q');
                            }
                            fileCount++;
                        }
                    }
                }
            }

            // Check black castling
            if (castlingRights.Contains('k') || castlingRights.Contains('q'))
            {
                string rank8 = ranks[0];

                bool blackKingOnE8 = false;
                int fileCount = 0;
                foreach (char c in rank8)
                {
                    if (char.IsDigit(c))
                    {
                        fileCount += (c - '0');
                    }
                    else
                    {
                        if (c == 'k' && fileCount == 4)
                        {
                            blackKingOnE8 = true;
                        }
                        fileCount++;
                    }
                }

                if (blackKingOnE8)
                {
                    fileCount = 0;
                    foreach (char c in rank8)
                    {
                        if (char.IsDigit(c))
                        {
                            fileCount += (c - '0');
                        }
                        else
                        {
                            if (c == 'r')
                            {
                                if (fileCount == 7 && castlingRights.Contains('k'))
                                    validRights.Append('k');
                                if (fileCount == 0 && castlingRights.Contains('q'))
                                    validRights.Append('q');
                            }
                            fileCount++;
                        }
                    }
                }
            }

            return validRights.Length > 0 ? validRights.ToString() : "-";
        }

        public static bool IsSparseExternalBoardPosition(string boardPosition)
        {
            if (string.IsNullOrWhiteSpace(boardPosition))
                return false;

            int totalPieces = 0;
            int totalPawns = 0;
            int nonKingPieces = 0;

            foreach (char c in boardPosition)
            {
                if (c == '/' || char.IsDigit(c))
                    continue;

                totalPieces++;

                char lower = char.ToLowerInvariant(c);
                if (lower == 'p')
                    totalPawns++;

                if (lower != 'k')
                    nonKingPieces++;
            }

            return totalPieces <= 10 || nonKingPieces <= 8 || totalPawns <= 4;
        }

        public static bool BoardPositionHasReferencePawn(string boardPosition, char referenceColor)
        {
            if (string.IsNullOrWhiteSpace(boardPosition))
                return false;

            char pawn = referenceColor == 'b' ? 'p' : 'P';
            return boardPosition.IndexOf(pawn) >= 0;
        }

        public static bool BoardPositionHasAnyPawn(string boardPosition)
        {
            if (string.IsNullOrWhiteSpace(boardPosition))
                return false;

            return boardPosition.IndexOf('P') >= 0 || boardPosition.IndexOf('p') >= 0;
        }

        public static bool IsEmptyBoardPosition(string boardPosition)
        {
            return string.Equals(boardPosition, "8/8/8/8/8/8/8/8", StringComparison.Ordinal);
        }

        public static char? GetSideToMove(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return null;

            var parts = fen.Split(' ');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                return null;

            char side = char.ToLowerInvariant(parts[1][0]);
            return side is 'w' or 'b' ? side : null;
        }

        public static char GetFenPieceAt(string fen, int file, int rank)
        {
            if (file < 0 || file > 7 || rank < 0 || rank > 7 || string.IsNullOrWhiteSpace(fen))
                return '\0';

            string board = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            int currentRank = 7;
            int currentFile = 0;
            foreach (char c in board)
            {
                if (c == '/')
                {
                    currentRank--;
                    currentFile = 0;
                    continue;
                }

                if (char.IsDigit(c))
                {
                    currentFile += c - '0';
                    continue;
                }

                if (currentFile == file && currentRank == rank)
                    return c;

                currentFile++;
            }

            return '\0';
        }
    }
}
