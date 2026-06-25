namespace ChessKit
{
    /// <summary>
    /// Pure analysis-result / coach scoring / key-building helpers extracted
    /// verbatim from the Program analysis pipeline. None touch shared mutable
    /// state or call other Program members, so they are safe to host in a real
    /// class. Program reaches them unqualified via
    /// <c>using static ChessKit.AnalysisResultUtil;</c>, so every former call site
    /// is byte-for-byte unchanged.
    /// </summary>
    internal static class AnalysisResultUtil
    {
        public static bool AnalysisResultMatchesRequestedFen(BestMoveResult result, string expectedAnalysisFen)
        {
            if (string.IsNullOrWhiteSpace(result.AnalysisFen) || string.IsNullOrWhiteSpace(expectedAnalysisFen))
                return true;

            return string.Equals(result.AnalysisFen.Trim(), expectedAnalysisFen.Trim(), StringComparison.Ordinal);
        }

        public static int GetBestResultDepth(BestMoveResult result)
        {
            int variationDepth = result.Variations.Any(v => v.Depth > 0)
                ? result.Variations.Max(v => v.Depth)
                : 0;

            return Math.Max(result.AnalysisDepth, variationDepth);
        }

        public static int QuantizeCoachComplexityScore(int score)
        {
            int clamped = Math.Clamp(score, 0, 100);
            return Math.Clamp((int)Math.Round(clamped / 5.0, MidpointRounding.AwayFromZero) * 5, 0, 100);
        }

        public static double GetCoachScoreCentipawns(MoveVariation variation)
        {
            if (string.Equals(variation.ScoreType, "mate", StringComparison.OrdinalIgnoreCase))
            {
                int mate = variation.MateIn ?? 0;
                return mate >= 0 ? 100000.0 : -100000.0;
            }

            return variation.Score * 100.0;
        }

        public static string GetArrowPositionKey(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return "";

            var parts = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string board = parts.Length > 0 ? parts[0] : fen;
            string side = parts.Length > 1 ? parts[1] : "";
            return $"{board} {side}";
        }

        public static string NormalizeUciMoveKey(string move)
            => string.IsNullOrWhiteSpace(move) ? "" : move.Trim().ToLowerInvariant();

        public static double GetExternalTopMoveScoreCp(MoveVariation variation, char expectedMovingSide)
        {
            if (string.Equals(variation.ScoreType, "mate", StringComparison.OrdinalIgnoreCase))
            {
                int mate = variation.MateIn ?? 0;
                if (mate == 0)
                    return 100000.0;

                return Math.Sign(mate) * 100000.0;
            }

            _ = expectedMovingSide;
            return variation.Score * 100.0;
        }

        public static string SpeculativePrefetchKeyFromFen(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return "";
            var parts = fen.Split(' ');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                return "";
            // Board layout + side to move. Castling/en-passant/counters are
            // intentionally excluded: the vision detector cannot reproduce them
            // reliably, and board+side determines the analysis for all but rare
            // en-passant/just-lost-castling cases (which the refining wire pass,
            // still run when the prefetch depth is below target, corrects).
            return parts[0] + "|" + parts[1];
        }
    }
}
