namespace ChessKit
{
    internal static class BuildLimits
    {
        // Runtime edition gate. There is no compile-time Free Edition build anymore:
        // one binary ships and at runtime it is either the limited Free Edition (no
        // valid license verified this session) or fully Licensed. Program wires
        // this up at startup to its sticky "license verified" flag
        // (HasVerifiedFullVersionLicense). Until wired, it defaults to Free so the
        // safe (limited) behavior is the default and a missing wire can never
        // accidentally unlock licensed features.
        private static Func<bool> _isLicensedAccessor = static () => false;

        /// <summary>
        /// Installs the accessor that reports whether an active full-version
        /// license has been verified this session. Set once by Program at startup.
        /// </summary>
        public static void SetLicensedAccessor(Func<bool> isLicensed)
            => _isLicensedAccessor = isLicensed ?? (static () => false);

#if DEBUG
        // Debug-only edition override for testing without a real license server.
        // null = follow the real licensed flag; true = force Free; false = force
        // Licensed. Set via SetDebugFreeEditionOverride. Compiled out of Release
        // entirely.
        private static bool? _debugForceFreeEdition;

        public static void SetDebugFreeEditionOverride(bool forceFree) => _debugForceFreeEdition = forceFree;
        public static bool IsDebugFreeEditionOverride => _debugForceFreeEdition == true;

        // Free Edition == not licensed. In Debug, a forced override wins so a build
        // without a license server can still exercise both editions.
        public static bool IsFreeEdition => _debugForceFreeEdition ?? !_isLicensedAccessor();
#else
        public static bool IsDebugFreeEditionOverride => false;

        // Free Edition == not licensed.
        public static bool IsFreeEdition => !_isLicensedAccessor();
#endif

        public static string ProductSuffix => "";
        // Depth, infinite analysis, and the number of lines are NOT meaningfully
        // enforceable in an open-source client (any client-side cap can be patched
        // out), so they are no longer Free-gated — the real Free limit is the
        // server-side vision move-count window. Depth runs to 32 (also the Game
        // Analysis max), the board can run infinite analysis, and up to 6 lines.
        public static int MaxDepth => 32;
        public static bool AllowInfiniteAnalysis => true;
        public static int MaxLines => 6;
        // Engine-side MultiPV ceiling, intentionally WIDER than MaxLines (the
        // display slider cap): the Bullet profile requests extra lines purely
        // to feed the PV-continuation cache with plausible opponent replies -
        // nothing above MaxLines is ever displayed. Every client-side MultiPV
        // clamp (remote request, setoption capture, info-line parsers) must
        // use this, not MaxLines, or the extra lines silently vanish.
        public const int MaxEnginePvLines = 10;
        public static int MaxThreads => IsFreeEdition ? 4 : 16;
        public static int MaxHashMb => IsFreeEdition ? 128 : 1024;
        // The Free move cap is now governed by the SERVER (a per-HWID move-count
        // window + cooldown reported on each analysis response and surfaced via
        // FreeTierServerState). The old client-side cut-off is retired: leave
        // these effectively unlimited for Free so the client never stops analysis
        // on its own count - the server's window/cooldown is the only limit.
        public static int ExternalAnalysisMoveLimit => int.MaxValue;
        public static int AnalysisBoardLiveMoveLimit => int.MaxValue;
        public static int ExternalAnalysisPlyLimit => ExternalAnalysisMoveLimit == int.MaxValue ? int.MaxValue : ExternalAnalysisMoveLimit * 2;
        public static int AnalysisBoardLivePlyLimit => AnalysisBoardLiveMoveLimit == int.MaxValue ? int.MaxValue : AnalysisBoardLiveMoveLimit * 2;
        public static int GameAnalysisPlyLimit => IsFreeEdition ? 20 : int.MaxValue;
        public static int MatchGameLimit => IsFreeEdition ? 1 : int.MaxValue;
        public static int MatchMaxSeconds => IsFreeEdition ? 60 : int.MaxValue;
        public static int OpeningBookMoveLimit => IsFreeEdition ? 3 : int.MaxValue;
        public static bool AllowAnnotatedPgnExport => !IsFreeEdition;
        public static bool AllowMatchPgnExport => !IsFreeEdition;
        public static bool AllowCoach => !IsFreeEdition;
        public static bool AllowGameAnalysisCoach => true;
        public static int GameAnalysisRunLimitPerLaunch => IsFreeEdition ? 1 : int.MaxValue;
        public static int GameAnalysisCoachLimitPerLaunch => IsFreeEdition ? 1 : int.MaxValue;

        public static int ClampDepth(int depth) => Math.Clamp(depth, 0, MaxDepth);
        public static int ClampLines(int lines) => Math.Clamp(lines, 1, MaxLines);
        public static int ClampThreads(int threads) => Math.Clamp(threads, 1, MaxThreads);
        public static int ClampHashMb(int hashMb) => Math.Clamp(hashMb, 16, MaxHashMb);
        public static int ClampMatchSeconds(int seconds) => Math.Clamp(seconds, 1, MatchMaxSeconds);
        public static int ClampMatchGameLimit(int games) => MatchGameLimit == int.MaxValue ? Math.Clamp(games, 0, 1000) : Math.Clamp(games <= 0 ? MatchGameLimit : games, 1, MatchGameLimit);
    }
}
