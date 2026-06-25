using ChessKit;
using OpenCvSharp;
using System.Diagnostics;
using System.Text;

// Static fields, constants, nested types and P/Invoke declarations for Program.
partial class Program
{
    private const string SingleInstanceMutexName = "Local\\ChessKit.SingleInstance";
    private const string StartupTermsVersion = "2026-05-05";

    // Shared live-pipeline state holder. The property-safe shared fields of the
    // real-time tracking/FEN/analysis/arrow loop live on this single instance;
    // each is exposed to the hot-path code through the `internal static` property
    // shims below. Constructed during Program static-init (before Main runs and
    // long before the hot loop), so it is always present wherever the fields are
    // used. See Core/LiveState.cs for why the remaining shared fields stay on
    // Program as `internal static`.
    internal static readonly LiveState _state = new();
    private static Mutex? _singleInstanceMutex = null;
    private static readonly AppSettingsManager _appSettingsManager =
        new(Path.Combine(AppContext.BaseDirectory, "settings.ini"));
    private static HotkeyController? _hotkeyController = null;
    private static SystemTrayController? _systemTray = null;
    // Owns the standalone Analysis Board feature (engine analysis loop, engine-vs-engine
    // match, game review, FEN mirroring). Constructed once the
    // analysis-board / game-analysis forms exist (see CreateAnalysisBoardController).
    private static AnalysisBoardController? _analysisBoardController = null;
#if !DEBUG
    // Startup splash is Release-only; in Debug all ShowStartupStatus/
    // UpdateStartupStatus/CloseStartupStatus bodies compile to `return;`,
    // so the backing field would otherwise be unused (CS0414).
    private static StartupStatusForm? _startupStatusForm = null;
#endif
    private static bool _showSystemTrayIconAfterStartup = true;
    // Owns the sticky "license verified" flag and the background re-verification
    // monitor. Always present: the single runtime-gated build decides Free vs
    // Licensed at runtime from this enforcer's IsVerified flag.
    private static LicenseEnforcer? _licenseEnforcer = null;
    // Set true once startup has finished (splash closed, UI ready). Hotkeys are
    // ignored until then: a key pressed during the loading splash must not run
    // feature logic (and trip the license gate / pop a modal that freezes the
    // startup await) before the app and its license check are ready.
    private static volatile bool _startupComplete = false;
    // Shared one-shot/throttle state for license failure notices. Used by both the
    // full-version gate (via the LicenseEnforcer's injected accessor) and the
    // private-engine license notice, so it stays in Program rather than moving into
    // the enforcer.
    private static int _licenseFailureNoticeShown = 0;
    private static DateTime _lastEngineLicenseFailureNoticeUtc = DateTime.MinValue;
    private static readonly object _engineLicenseFailureNoticeLock = new();
    // Free Edition taskbar helper window. Instantiated only when running Free at
    // runtime (see Program.Lifecycle), null when Licensed.
    private static FreeEditionWindow? _freeEditionWindow = null;

    private sealed class ConfirmedPositionState
    {
        public string BoardPosition { get; init; } = "";
        public char UserColor { get; init; }
        public char SideToMove { get; init; }
        public bool WaitingForOpponent { get; init; }
        public string ArrowSourceFen { get; init; } = "";
    }

    private sealed class LegalTurnTransition
    {
        public char LastMover { get; init; }
        public char SideToMoveAfter { get; init; }
        public int PlyCount { get; init; }
    }

    // Overlay state
    // Migrated to LiveState (see Core/LiveState.cs); property shims preserve every
    // existing `_isTracking` / `_lastTrackedBox` read & write site unchanged.
    internal static bool _isTracking { get => _state.IsTracking; set => _state.IsTracking = value; }
    internal static Rect? _lastTrackedBox { get => _state.LastTrackedBox; set => _state.LastTrackedBox = value; }

    // Set true when we had a tracked board window and lost it
    // (minimize / hide / close). Cleared only when we successfully
    // re-acquire a real top-level window via vision + ResolveTopLevelWindow.
    // While set, TryQueueAnalysis refuses to fire - this prevents the
    // 250ms analysis-watchdog timer (and any other path) from queueing
    // analyses that would re-show arrows on top of whatever's behind
    // the board when its window is invisible. Vision can incorrectly
    // re-set _lastTrackedBox in this state by latching onto something
    // visible behind the board (taskbar previews, fragments, etc.) so
    // we can't rely on _lastTrackedBox.HasValue == false alone as the
    // "no window" signal.
    private static volatile bool _trackingLostWaitingForReacquire = false;
    // The HWND we were tracking when we lost it. Preserved across the
    // lost-tracking period so vision-driven re-acquisition can verify
    // it found the SAME board window - not a different app, the
    // desktop, or an Aero peek thumbnail. Cleared on F1 toggle (user
    // intent) or when re-acquired successfully.
    internal static IntPtr _lostHwndCache { get => _state.LostHwndCache; set => _state.LostHwndCache = value; }
    // Throttle state for HWND-rejected diag log (one entry per unique
    // HWND per second).
    private static IntPtr _lastRejectedHwndLogged = IntPtr.Zero;
    private static DateTime _lastRejectedHwndLogAtUtc = DateTime.MinValue;
    // Stability tracking for the dedicated lost-hwnd poll. Set when
    // IsTrackable on _lostHwndCache first returns true; cleared on
    // any blip back to false. Only after this stays set for 600ms+
    // do we trust that the user genuinely restored the window.
    private static DateTime? _lostAcquisitionCandidateSinceUtc = null;
    // Last-logged values for IsIconic/IsWindowVisible on the lost HWND,
    // so the diag log fires only on TRANSITIONS (not every frame).
    private static bool _lastLostIconicLogged = false;
    private static bool _lastLostVisibleLogged = false;
    // Set to true by the system-event hook when EVENT_SYSTEM_MINIMIZEEND
    // fires for our tracked or lost HWND. The OS only fires this for
    // actual user-initiated restore operations (clicking taskbar,
    // SC_RESTORE etc.) - NOT for the spurious iconic toggling we've
    // observed on some machines. The lost-tracking poll requires this
    // before releasing the latch, so OS-internal toggles can't trick us.
    // Reset whenever we set the latch.
    private static volatile bool _minimizeEndFiredForLostHwnd = false;
    // System tick count (ms since boot) at the moment the latch was set.
    // The polled recovery requires user input (mouse/keyboard) to have
    // occurred AFTER this timestamp before releasing the latch - that's
    // the strongest signal that a real user-initiated restore happened
    // (taskbar click, alt-tab, etc.) vs. a programmatic or OS-internal
    // un-minimize.
    private static uint _latchSetTick = 0;
    // Tracks whether the board was obscured by another window on the
    // previous frame. Used to log only on transitions (not every frame),
    // and to know we need to "resume" overlays when the obstruction goes
    // away.
    private static bool _boardObscuredLastFrame = false;

    // Window-tracking state. When non-zero, _trackedHwnd identifies the
    // top-level window containing the chess board, and _boardOffsetInWindow
    // holds the board's position relative to that window's top-left. As
    // long as the window passes WindowTracker.IsTrackable, we get the
    // board's current screen position via GetWindowRect + offset - this is
    // microseconds vs ~30ms for a vision detector pass, so the toolbar
    // and arrows can follow window movement essentially in real time.
    // Vision only re-runs when the window resizes, when periodic
    // re-verification is due, or when the HWND becomes invalid.
    internal static IntPtr _trackedHwnd { get => _state.TrackedHwnd; set => _state.TrackedHwnd = value; }
    private static WindowTracker.RECT _lastWindowRect;
    private static Rect _boardOffsetInWindow;
    private static System.Drawing.RectangleF _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
    private static IntPtr _lastForegroundBoardProbeHwnd = IntPtr.Zero;
    private static DateTime _lastForegroundBoardProbeUtc = DateTime.MinValue;
    private static readonly int _foregroundBoardProbeCooldownMs = 350;
    private static int _foregroundBoardProbeMisses = 0;
    private static DateTime _foregroundBoardProbeBackoffUntilUtc = DateTime.MinValue;
    private static DateTime _foregroundBoardProbeBudgetBackoffUntilUtc = DateTime.MinValue;
    private static int _externalBoardAcquisitionMisses = 0;
    private static DateTime _externalBoardAcquisitionBackoffUntilUtc = DateTime.MinValue;
    // Frames since the last full vision pass while window-tracking. We
    // run a periodic verification pass to catch in-window content changes
    // (the board layout reflow, board scrolled within the page, etc.)
    // that pure window movement can't tell us about.
    private static int _framesSinceWindowTrackVerify = 0;
    private const int WindowTrackVerifyIntervalFrames = 90; // idle verify; immediate verifies still run on movement/resize/refresh

    // Settings from toolbar - initialized with defaults
    private static bool _boardIsFlipped = false;
    private static int _maxArrowCount = BuildLimits.ClampLines(3);
    internal static bool _coachModeEnabled { get => _state.CoachModeEnabled; set => _state.CoachModeEnabled = value; }
    private static int _coachLevel = 5;
    private static int _coachMarkCount = 1;
    private static bool _coachCardEnabled = true;
    internal static bool _menuExpanded { get => _state.MenuExpanded; set => _state.MenuExpanded = value; }

    private static int GetLiveAnalysisMultiPvCount()
    {
        int count = _coachModeEnabled
            ? Math.Max(_maxArrowCount, _coachMarkCount)
            : _maxArrowCount;
        return BuildLimits.ClampLines(count);
    }

    // Performance tracking
    private static readonly Stopwatch _perfStopwatch = new();
    private static int _frameCount = 0;
    private static int _fenCount = 0;
    private static double _currentFps = 0;
    private static double _currentFenPerSec = 0;
    private static DateTime _lastPerfUpdate = DateTime.UtcNow;

    // === FPS DIAGNOSTICS ===
    // === DIAGNOSTIC LOGGING ===
    // Release diagnostics stay opt-in. Prefer diagnostics.ini next to the EXE
    // so the normal settings save cycle never rewrites the switch away.
    // Environment variables and settings.ini are also supported.
    private static bool _diagLoggingEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            RefreshReleaseDiagnosticsSettingsIfNeeded();
            return _releaseDiagLoggingEnabled;
#endif
        }
    }

    private const int DiagnosticsSettingsRefreshMs = 2000;
    private const string DiagnosticsFileName = "diagnostics.ini";
    private static readonly object _diagSettingsLock = new();
    private static DateTime _diagSettingsLastReadUtc = DateTime.MinValue;
#if !DEBUG
    // Release-only diagnostics switches. In Debug, _diagLoggingEnabled and
    // _boardTraceEnabled return a constant, so these backing fields are
    // referenced only in non-Debug builds.
    private static bool _releaseDiagLoggingEnabled = false;
    private static bool _releaseBoardTraceEnabled = false;
#endif
    private static readonly object _runtimeLogLock = new();
    private static readonly object _diagLogLock = new();
    private const string RuntimeLogFileName = "runtime.log";
    private const string DiagnosticLogFileName = "fps-diag.log";
#if !DEBUG
    // Release runtime logging is OFF by default: the per-call file append (open/
    // write/close under a lock) runs on the hot path and costs throughput. Opt
    // in for a diagnostic session with env CHESSKIT_LOG=1. Debug logging (to the
    // in-memory/console DebugRuntime) is unaffected.
    private static readonly bool _releaseRuntimeLogEnabled =
        Environment.GetEnvironmentVariable("CHESSKIT_LOG") is "1" or "true" or "TRUE";
#endif
    private static long _nextDiagLogSizeCheckTicks = 0;

#if DEBUG
    // Per-phase timing accumulators. Resets every 30 frames after logging.
    // Remove or wrap in #if DEBUG once the FPS investigation is complete.
    private static long _fpsDiagPhase1Sum = 0;
    private static long _fpsDiagPhase2Sum = 0;
    private static long _fpsDiagOtherSum = 0;
    private static int _fpsDiagFrames = 0;
    // Phase 2 sub-step breakdown: capture+convert vs diff vs full FEN
    private static long _fpsDiagP2CaptureSum = 0;
    private static long _fpsDiagP2MatSum = 0;          // board ROI creation / Mat view
    private static int _fpsDiagP2RegionWidth = 0;       // Last region size, for context
    private static int _fpsDiagP2RegionHeight = 0;
    private static long _fpsDiagP2PixelSum = 0;
    private static int _fpsDiagP2PixelSkips = 0;
    private static long _fpsDiagP2DiffSum = 0;
    private static long _fpsDiagP2FenSum = 0;
    private static int _fpsDiagP2FenCalls = 0;
#endif

    // Move-latency stopwatch: from first board-diff change to arrows visible.
    // T0 = first ChangedSquares > 0 after a static period.
    // T1 (existing): _lastConfirmedFenAtUtc, set when a new FEN is confirmed.
    // T2: when arrows are scheduled to display.
    // We log T1-T0 (detection-to-confirm, includes debouncing + FEN cost)
    // and T2-T1 (confirm-to-arrows, dominated by engine analysis time).
    internal static DateTime _latencyT0Utc { get => _state.LatencyT0Utc; set => _state.LatencyT0Utc = value; }
    internal static int _latencyT0ChangedSquares { get => _state.LatencyT0ChangedSquares; set => _state.LatencyT0ChangedSquares = value; }
    private static DateTime _optimisticFenGuardUntilUtc = DateTime.MinValue;
    private static string _optimisticFenGuardBoard = "";
    private static readonly int _optimisticFenGuardMs = 750;
    private static DateTime _lastOptimisticFenAppliedUtc = DateTime.MinValue;
    private static string _lastOptimisticBaseFen = "";
    private static string _lastOptimisticPredictedFen = "";
    private static string _lastOptimisticMoveText = "";
    private const int OptimisticCorrectionWindowMs = 3200;
    private const int OptimisticCorrectionMinAgeMs = 300;
    private const int OptimisticCorrectionMaxChangedSquares = 6;
    private static DateTime _rapidPostOptimisticMoveHoldUntilUtc = DateTime.MinValue;
    private const int BlitzRapidPostOptimisticMoveHoldMs = 40;
    private static bool _autoBlitzActive = false;
    private static DateTime _autoBlitzActiveUntilUtc = DateTime.MinValue;
    private static DateTime _lastExternalMoveCadenceUtc = DateTime.MinValue;
    private static int _rapidExternalMoveStreak = 0;
    private const int BlitzAutoFastMoveWindowMs = 1800;
    private const int BlitzAutoActivationStreak = 2;
    private const int BlitzAutoHoldMs = 9000;
    private const int BlitzRawBoardChangeArrowClearCooldownMs = 80;
    private const int BlitzModerateChangeSettleMs = 30;
    private const int BlitzMinimumDisplayDepth = 2;
    private const int BlitzFenPostMouseReleaseDelayMs = 20;
    private const int BlitzFastConfirmMaxChangedSquares = 8;
    private const int BlitzFastConfirmMaxPlies = 2;
    // Fast detection recovery for remote-engine sessions (and blitz). The
    // detector regularly sees 2-3 plies land at once during premove-speed
    // play; with maxPlies=1 those jumps could not legally bridge and fell
    // into multi-second stable-confirm stalls. The legal-path search is
    // pruned to moves touching squares that differ from the target, so 3
    // plies stays cheap, and results are memoized per observed board.
    private const int RemoteFastConfirmMaxPlies = 3;
    private const int RemoteFastConfirmMaxChangedSquares = 10;
    private static readonly double _lastMoveHighlightConflictConfidence = 0.76;
    private const int StaticLastMoveHighlightInitialAnalysisHoldMs = 2200;
    private const int StaticLastMoveHighlightProbeIntervalMs = 85;
    private const int InitialExternalOpeningSideMaxPlies = 4;
    private const int InitialExternalOpeningSideSearchBudgetMs = 80;
    private const int InitialExternalOpeningSideSearchMaxNodes = 14000;
    private static string _staticLastMoveHighlightHoldBoardPosition = "";
    private static string _staticLastMoveHighlightCompletedBoardPosition = "";
    private static DateTime _staticLastMoveHighlightHoldUntilUtc = DateTime.MinValue;
    private static int _staticLastMoveHighlightHoldGeneration = 0;
    private static DateTime _foregroundMismatchFenGuardUntilUtc = DateTime.MinValue;
    private static readonly int _foregroundMismatchFenGuardMs = 650;
    private static DateTime _foregroundNoBoardOverlayHoldUntilUtc = DateTime.MinValue;
    private static IntPtr _foregroundNoBoardOverlayHoldHwnd = IntPtr.Zero;
    private static readonly int _foregroundNoBoardOverlayHoldMs = 1800;

    // FEN detection
    private static BoardVisionDetector? _detector = null;
    internal static string _currentFEN { get => _state.CurrentFen; set => _state.CurrentFen = value; }
    private static string _executionMode = "CPU";
    private static Mat? _lastConfirmedBoardSnapshot = null;
    private static Mat? _lastConfirmedBoardDiffSnapshot = null;
    private static Mat? _lastConfirmedBoardPixelFingerprint = null;

    // Active UCI engine
    private static UCIEngine? _stockfish = null;
    private static string _stockfishPath = "";
    private static readonly object _liveEngineStartLock = new();
    private static bool _liveEngineStartInProgress = false;
    private static string _liveEngineStartPath = "";
    private static DateTime _lastLiveEngineStartFailureUtc = DateTime.MinValue;
    private static string _lastLiveEngineStartFailurePath = "";
    private const int LiveEngineStartFailureCooldownMs = 4000;
    // Analysis-board engine / engine-vs-engine match / game-analysis state now lives in
    // AnalysisBoardController (_analysisBoardController). Only the shared snapshot /
    // orientation state below remains in Program.
    private static bool _analysisInProgress = false;
    internal static List<MoveArrow>? _currentMoveArrows { get => _state.CurrentMoveArrows; set => _state.CurrentMoveArrows = value; }
    private static string _lastDisplayedArrowDepthFEN = "";
    private static int _lastDisplayedArrowDepth = 0;
    private static string _lastExternalTopMovePositionKey = "";
    private static string _lastExternalTopMoveUci = "";
    private static int _lastExternalTopMoveDepth = 0;
    private static double _lastExternalTopMoveScoreCp = double.NaN;
    private static string _pendingExternalTopMovePositionKey = "";
    private static string _pendingExternalTopMoveUci = "";
    private static int _pendingExternalTopMoveCount = 0;
    private static DateTime _pendingExternalTopMoveSinceUtc = DateTime.MinValue;
    private static DateTime _lastExternalTopMoveDisplayUtc = DateTime.MinValue;
    private static DateTime _firstExternalTopMovePositionDisplayUtc = DateTime.MinValue;
    private static string _lastExternalTopMoveSwitchCountPositionKey = "";
    private static int _lastExternalTopMoveSwitchCount = 0;
    private static string _externalArrowPerspectiveBoardKey = "";
    private static bool _externalArrowPerspectiveBlack = false;
    private static DateTime _externalArrowPerspectiveFirstDisplayUtc = DateTime.MinValue;
    private static DateTime _externalOverlayArrowsShownUtc = DateTime.MinValue;
    private static string _externalOverlayArrowsFen = "";
    private static int _externalOverlayArrowsCount = 0;
    private const int ExternalTopMoveSwitchConfirmMs = 520;
    private const int BlitzExternalTopMoveSwitchConfirmMs = 320;
    private const int ExternalTopMoveMinVisibleMs = 750;
    private const int BlitzExternalTopMoveMinVisibleMs = 450;
    private const int ExternalTopMovePendingMaxAgeMs = 1800;
    private const int ExternalTopMoveMaxSwitchesPerPosition = 1;
    private const int ExternalTopMoveSwitchWindowMs = 1000;
    private const int ExternalArrowPerspectiveSwitchWindowMs = 1000;
    private const int ExternalArrowStaleDisplayMemoryMs = 15000;
    internal static bool _showingMoves { get => _state.ShowingMoves; set => _state.ShowingMoves = value; }
    private static int _arrowDisplayGeneration = 0;
    private static int _arrowRenderToken = 0;
    private static DateTime _suppressCachedArrowRecoveryUntilUtc = DateTime.MinValue;
    private static DateTime _lastExternalArrowResultReadyUtc = DateTime.MinValue;
    private const int CachedArrowRecoverySuppressAfterPositionClearMs = 1600;
    private const int CachedArrowRecoverySuppressAfterResultReadyMs = 650;
    private const int ExternalArrowResultDirectRenderGraceMs = 450;
    // Remote analysis takes a network round-trip, so hiding arrows the moment
    // a position changes leaves the board visibly empty until the response
    // lands. Instead the previous arrows are held on screen and swapped
    // atomically when the new result draws; if nothing arrives within this
    // grace they hide so genuinely stale advice never lingers. Sized from
    // measured swap latency: steady-state paints land 210-330ms after the
    // confirm, but the first moves of a session (JIT + colder broker) were
    // measured up to ~916ms, so the grace covers those too.
    private const int ExternalArrowSwapGraceMs = 950;
    // Visual hold deadline. While set in the future, the per-frame
    // RefreshDisplayedArrows loop must not clear the overlay (the logical
    // arrow state is already cleared, which would otherwise hide the held
    // arrows within one frame). Position changes that arrive while a hold is
    // already pending do NOT extend the deadline, so total staleness stays
    // bounded by the FIRST unswapped hold's grace.
    private static DateTime _externalArrowHoldUntilUtc = DateTime.MinValue;
    // When the chain of position changes outpaces the engine, the staleness
    // bound must follow the age of what is actually ON SCREEN: a fresh paint
    // mid-chain rebases the deadline, otherwise a deadline armed two changes
    // ago blinks out arrows that just appeared.
    private static DateTime _externalArrowHoldStartUtc = DateTime.MinValue;
    private static DateTime _lastExternalArrowsShownUtc = DateTime.MinValue;
    // Locally a depth-4 preview is replaced within milliseconds; remotely each
    // streamed depth crosses the network, so a shallow best move stays visible
    // long enough to be seen relocating. Require a deeper floor before the
    // first remote arrows are shown.
    private const int RemoteMinimumExternalDisplayDepth = 10;
    private static DateTime _lastRawBoardChangeArrowClearUtc = DateTime.MinValue;
    private const int RawBoardChangeArrowClearCooldownMs = 120;
    private static DateTime _lastUnconfirmedFenArrowClearUtc = DateTime.MinValue;
    private const int UnconfirmedFenArrowClearCooldownMs = 180;
    private static DateTime _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;
    private static DateTime _lastExternalRawBoardChangeUtc = DateTime.MinValue;
    private const int ExternalRawBoardChangeSettleMs = 150;
    private const int ExternalLikelyMoveSettleMs = 0;
    private const int ExternalModerateChangeSettleMs = 80;
    private const int ExternalRiskyFenRepeatWindowMs = 900;
    private const int BlitzExternalRiskyFenRepeatWindowMs = 250;
    private const int ExternalNonLegalFenConfirmationThreshold = 2;
    private const int ExternalNonLegalFenConfirmationMinMs = 350;
    // If the detector keeps disagreeing with the confirmed position for this
    // long (candidate churn, no confirm), stop re-painting analysis of the
    // stale position: wrong-side arrows stuck over a moved-on board are worse
    // than no arrows. Measured failure: a misread capture on a highlighted
    // square stalled confirmation for 17s while stale arrows kept re-painting.
    private const int StaleConfirmRepaintBlockMs = 2500;
    private static string _lastLoggedVisionCandidateBoard = "";
    private const int ExternalNonLegalFenRepeatMaxGapMs = 900;
    private const int ExternalNonLegalFenMaxRecoverableChangedSquares = 4;
    private const int ExternalBoardSwitchChangedSquaresThreshold = 12;
    // Dead-zone rescue. Non-legal jumps with 5..11 changed squares are
    // neither "recoverable" (<=4) nor a board switch (>=12), so they used to
    // be rejected on every frame with the candidate reset - an UNRESOLVABLE
    // stall whenever detection missed 2-3 plies of fast play (measured 10-17s
    // blackouts across multiple sessions). If the SAME sane board keeps being
    // observed through that rejection for long enough, it is the real board:
    // let it into the candidate machinery and re-anchor.
    private const int StallReanchorMinObservations = 8;
    private const int StallReanchorMinSpanMs = 2000;
    private const int StallReanchorObservationMaxGapMs = 1200;
    private static string _rejectedJumpBoard = "";
    private static int _rejectedJumpCount = 0;
    private static DateTime _rejectedJumpFirstSeenUtc = DateTime.MinValue;
    private static DateTime _rejectedJumpLastSeenUtc = DateTime.MinValue;
    private static DateTime _lastVisionRejectLogUtc = DateTime.MinValue;
    private const int ExternalBoardSwitchConfirmationThreshold = 2;
    private const int ExternalBoardSwitchConfirmationMinMs = 250;
    private const int ExternalBoardSwitchAcceptWindowMs = 1500;
    private const int OptimisticMoveMaxChangedSquares = 6;
    private const int OptimisticSubsetMaxChangedSquares = 4;
    private static readonly int _quickArrowDepth = 6;
    private static readonly int _quickArrowThinkTimeMs = 35;
    private const int ExternalInfiniteDepthStep = 4;
    private const int ExternalInfiniteMaxDepth = 96;
    private const int ExternalInfiniteStepThinkTimeMs = 220;
    private const int ExternalCoachInfiniteTargetDepth = 18;
    private static string _lastQueuedAnalysisKey = "";
    private static UCIEngine? _lastAssertedLiveMultiPvEngine = null;
    private static int _lastAssertedLiveMultiPv = -1;
    private static bool _speculativeAnalysisEnabled = true;
    private static SpeculativeAnalysisMode _speculativeAnalysisMode = SpeculativeAnalysisMode.Balanced;

    // Speculative prefetch: precompute the engine's reply to the displayed top
    // move, so when the user plays it (the common case - the tool exists to be
    // followed) the next position's arrows are already in hand and paint in
    // ~detect+paint time instead of paying a full network round-trip. Keyed by
    // board-position + side-to-move (what the detector reliably reproduces);
    // served by stamping the cached result's AnalysisFen to the live request's
    // FEN so ApplyAnalysisResult's exact-FEN guard passes. FAIL-SAFE: a wrong
    // prediction simply never matches the next confirmed position - the guards
    // make it a cache miss that falls back to a normal request, never wrong
    // arrows.
    private sealed class SpeculativePrefetchEntry
    {
        public BestMoveResult Result = null!;
        public int Depth;
        public DateTime StoredUtc;
    }
    // Prefetch on/off is a CODE default, intentionally decoupled from the old
    // _speculativeAnalysisEnabled toolbar setting: that setting shipped as a
    // dead stub and persisted "False" into existing settings.ini files, which
    // would otherwise silently keep this feature off. Flip to false here to
    // disable globally.
    private static bool _speculativePrefetchEnabled = true;
    private static int _prefetchConfigLogged; // log the resolved state once
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpeculativePrefetchEntry> _speculativePrefetchCache = new(StringComparer.Ordinal);
    // Keys currently being prefetched, so the same prediction is never fired
    // twice (the same top moves recur across stream updates for one position).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _speculativePrefetchInFlightKeys = new(StringComparer.Ordinal);
    // How many of the displayed candidate moves to precompute the reply for.
    // Reverted to 1 (top move only) - the proven-stable behavior. Raising this
    // (with PrefetchConnectionCount) covers player deviations from the engine's
    // first choice, but only after the predicted-side/analyzed-side key
    // mismatch is fixed so the extra predictions actually hit.
    private const int SpeculativePrefetchTopMoves = 1;
    private const int SpeculativePrefetchTtlMs = 6000;
    private const int SpeculativePrefetchCacheMaxEntries = 48;
    // PV cache: how many of the displayed lines to cache, how many plies down
    // each line, and the minimum analysis depth worth caching a PV from.
    private const int PvCacheTopLines = 3;
    private const int PvCacheMaxPlies = 4;
    private const int PvCacheMinDepth = 6;
    private static BlitzModeSetting _blitzModeSetting = BlitzModeSetting.On;

    // Analysis toggle state
    internal static bool _continuousAnalysisEnabled { get => _state.ContinuousAnalysisEnabled; set => _state.ContinuousAnalysisEnabled = value; }
    internal static bool _analysisIsBlackPerspective { get => _state.AnalysisIsBlackPerspective; set => _state.AnalysisIsBlackPerspective = value; }
    private static bool _analysisBothEnabled = false;
    private static bool _pendingLiveAnalysisAfterEngineStart = false;
    private static bool _pendingLiveAnalysisBothAfterEngineStart = false;
    private static bool _pendingLiveAnalysisBlackPerspectiveAfterEngineStart = false;
    private static int _analysisSessionVersion = 0;
    private static int _analysisRunId = 0;
    private static CancellationTokenSource? _analysisCancellation = null;
    private static System.Threading.Timer? _analysisTimer = null;
    private static readonly object _analysisLock = new object();
    private const int AnalysisLockAcquireTimeoutMs = 20;
    private const string InitialBoardPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";
    private const string InitialBoardPositionRotated = "RNBKQBNR/PPPPPPPP/8/8/8/8/pppppppp/rnbkqbnr";

    // Confirmed state history tracking
    private static readonly List<ConfirmedPositionState> _recentConfirmedStates = new();
    private static readonly int _recentConfirmedStateLimit = 16;
    private static string _lastUserMoveFEN = "";
    internal static string _lastArrowSourceFEN { get => _state.LastArrowSourceFen; set => _state.LastArrowSourceFen = value; }
    private static string _acceptedExternalBoardSwitchFen = "";
    private static DateTime _acceptedExternalBoardSwitchUntilUtc = DateTime.MinValue;
    private static DateTime _lastExternalArrowGeometryUpdateUtc = DateTime.MinValue;
    private static string _lastExternalArrowGeometryKey = "";
    private static string _pendingFenCandidate = "";
    private static int _pendingFenCandidateCount = 0;
    private static readonly int _fenConfirmationThreshold = 1;
    private static DateTime _pendingFenCandidateStartedUtc = DateTime.MinValue;
    private static DateTime _pendingFenCandidateLastSeenUtc = DateTime.MinValue;
    private static DateTime _lastCurrentExternalBoardObservationUtc = DateTime.MinValue;
    private static DateTime _lastStaleExternalArrowDiagUtc = DateTime.MinValue;
    private static DateTime _lastStaleExternalFenProbeUtc = DateTime.MinValue;
    private static DateTime _lastExternalAnalysisFenHeartbeatUtc = DateTime.MinValue;
    private const int StaleExternalArrowDiagMinVisibleMs = 1200;
    private const int StaleExternalArrowDiagIntervalMs = 1000;
    private const int StaleExternalFenProbeIntervalMs = 1000;
    private const int ExternalAnalysisFenHeartbeatIntervalMs = 2200;
    private const int ExternalAnalysisFenHeartbeatMinConfirmedAgeMs = 1500;
    private static string _lastConfirmedFenForTiming = "";
    private static DateTime _lastConfirmedFenAtUtc = DateTime.MinValue;
    private static string _pendingConfirmedFenTarget = "";
    private static DateTime _freshGameResetUntilUtc = DateTime.MinValue;
    private static readonly int _freshGameResetWindowMs = 10000;
    private static string _outOfTurnFenCandidate = "";
    private static DateTime _outOfTurnFenCandidateSinceUtc = DateTime.MinValue;
    private static int _outOfTurnFenCandidateCount = 0;
    private const int OutOfTurnAnimationHoldMs = 420;
    private const int OutOfTurnAnimationConfirmations = 4;

    // Turn tracking
    private static char _userColor = 'w';
    internal static char _inferredSideToMove { get => _state.InferredSideToMove; set => _state.InferredSideToMove = value; }
    internal static bool _waitingForOpponentMove { get => _state.WaitingForOpponentMove; set => _state.WaitingForOpponentMove = value; }
    private static double _lastMoveLatencyMs = -1;
    private static string _lastDebugEvent = "";
    private static string _lastFenSentToEngine = "";

    // Board tracking with stability threshold
    private static readonly Queue<Rect> _boardHistory = new Queue<Rect>(3);
    internal static int _boardLostFrames { get => _state.BoardLostFrames; set => _state.BoardLostFrames = value; }
    private static readonly int _boardLostThreshold = 10;
    private static readonly int _arrowHideThreshold = 3;
    private static readonly int _evalBarHideThreshold = 2;
    // Toolbar fallback uses a slightly higher threshold than eval bar /
    // arrows, but not too high - we want minimizing the chess window to
    // promptly snap the toolbar back to top-of-screen, not leave it
    // floating over empty space for half a second. ~200ms at 30fps is
    // long enough to bridge transient detection issues, short enough to
    // feel responsive on real loss events.
    private static readonly int _toolbarFallbackThreshold = 6;
    private static int _boardContentLostFrames = 0;
    private static readonly int _boardContentLostThreshold = 4;
    private static readonly int _boardFullScanInterval = 900;
    private static readonly int _fastBoardFullScanInterval = 45;
    private static readonly int _postMoveFastBoardScanMs = 600;
    private static readonly int _boardDetectionAcquireMinIntervalMs = 1500;
    private static readonly int _healthyBoardVerifyCooldownMs = 30000;
    private static readonly int _localBoardSearchCooldownMs = 30000;
    private static readonly int _recoveryBoardSearchCooldownMs = 3500;
    private static readonly int _externalBoardGeometrySettleMs = 300;
    private static readonly int _postOffsetShiftGeometrySettleMs = 1200;
    private static readonly double _localBoardSearchPaddingFactor = 0.75;
    private static readonly int _localBoardSearchMinPaddingPx = 180;
    private static readonly int _invalidFenRefreshThreshold = 2;
    internal static int _invalidFenFrames { get => _state.InvalidFenFrames; set => _state.InvalidFenFrames = value; }
    internal static bool _requestBoardRefresh { get => _state.RequestBoardRefresh; set => _state.RequestBoardRefresh = value; }
    private static DateTime _fastBoardScanUntilUtc = DateTime.MinValue;
    private static DateTime _lastHealthyBoardVerifyUtc = DateTime.MinValue;
    private static DateTime _lastLocalBoardSearchUtc = DateTime.MinValue;
    private static DateTime _lastRecoveryBoardSearchUtc = DateTime.MinValue;
    private static DateTime _externalBoardGeometryUnstableUntilUtc = DateTime.MinValue;
    private static bool _mouseButtonWasDown = false;
    private static DateTime _fenDetectionPausedUntilUtc = DateTime.MinValue;

    // Time we last rejected a structurally-impossible FEN at observation
    // time. When YOLO is producing garbage (typical during fast window
    // motion when capture races), each YOLO call costs ~180ms but always
    // returns the same impossible FEN. Running it every frame collapses
    // framerate to ~5 FPS, starving the per-frame overlay positioning
    // and creating the perceived "freeze" during fast drags. We use this
    // timestamp to apply a brief cooldown - pretend the diff said "no
    // change" for a short interval after a rejection.
    private static DateTime _lastFenRejectionUtc = DateTime.MinValue;
    private static readonly int _fenRejectionCooldownMs = 200;

    // Recovery from a broken board crop: after a hard window resize / minimize-
    // restore (or a garbled board getting confirmed mid-resize), the detector
    // can stay unable to confirm anything - every frame decodes as garbage, or
    // detection flip-flops around a wrong confirmed board. We force a full board
    // re-acquire (HandleBoardContentLostInHealthyWindow) only when ALL hold:
    //  - IsExternalDetectionStalled (can't re-see the current board nor confirm a new one),
    //  - the tracked window has been physically STILL >= WindowStableForReacquireMs
    //    (so we re-detect a SETTLED board, never a blurred mid-drag frame, and
    //    never thrash while the user is still resizing),
    //  - no GENUINE confirmation for >= ExternalDetectionStallReacquireMs
    //    (_lastConfirmedFenAtUtc; a flip-flop re-observation does NOT update it,
    //    which is why this beats a continuous-stall timer), and
    //  - StallReacquireCooldownMs has passed since the last re-acquire (so a board
    //    that genuinely can't be found yet retries calmly instead of thrashing).
    // Reuses _windowStableSinceUtc + _lastConfirmedFenAtUtc; only the cooldown
    // timestamp is new. ZERO impact on normal play - the detector re-sees the
    // board every frame there, so IsExternalDetectionStalled is never sustained.
    private static DateTime _lastStallReacquireUtc = DateTime.MinValue;
    private const double ExternalDetectionStallReacquireMs = 4000;
    private const double WindowStableForReacquireMs = 2000;
    private const double StallReacquireCooldownMs = 5000;

    // Set when a HARD window layout jump (maximize/restore, big resize) is seen.
    // For a few seconds after, the same-window board-reflow consistency check is
    // kept loosened so the fresh, very-different rect re-grounds at its true new
    // size instead of being ignored as an inconsistent "alternate" (which left
    // the board tracked at the pre-jump size, oversizing/displacing the arrows).
    // The existing 1.4s resize-settle window was too short for the re-detect to
    // land. DateTime.MinValue = inactive (normal play and fullscreen unaffected).
    private static DateTime _hardLayoutRegroundUntilUtc = DateTime.MinValue;

    // Time the window position/size last changed between frames. Used by
    // the verify-cycle offset-update logic to decide whether vision's
    // result can be trusted: during fast moves or active resizes the
    // capture races GetWindowRect, so detected offsets are unreliable.
    // After the window has been stable for 1+ second though, vision is
    // trustworthy even if the offset differs a lot from the cached value
    // - that's the legitimate "user just finished resizing" case.
    private static DateTime _windowStableSinceUtc = DateTime.MinValue;
    private static readonly int _windowSettledTrustMs = 1000;
    private static DateTime _trackedWindowResizeGraceUntilUtc = DateTime.MinValue;
    private static readonly int _trackedWindowResizeArrowHoldMs = 350;
    private static DateTime _trackedWindowLastResizeUtc = DateTime.MinValue;
    private static readonly int _trackedWindowResizeSettleMs = 1400;
    private static System.Drawing.Point _lastMouseFocusScanPoint = System.Drawing.Point.Empty;
    private static DateTime _lastMouseFocusScanUtc = DateTime.MinValue;
    private static readonly int _mouseFocusBoardScanCooldownMs = 30000;

    // Set true on any frame where the tracked window's position or size
    // moved more than the stability threshold versus the previous frame.
    // Used to detect a "just settled" transition (was unstable last frame,
    // stable this frame) so we can force an immediate verify cycle -
    // otherwise we'd wait up to 2 seconds for the periodic verify, which
    // is the user-visible "freeze for seconds and snap" after resizes
    // and minimize/maximize.
    private static bool _windowWasUnstableLastFrame = false;

    // Time at which we should fire another window-track verify cycle,
    // independent of the periodic 60-frame schedule. Set when an offset
    // update was rejected because the window was just-settled but the
    // stability window hadn't yet expired - without this, we'd have to
    // wait up to 2 seconds for the next periodic verify, prolonging the
    // post-resize/maximize freeze the user sees.
    private static DateTime _scheduledVerifyUtc = DateTime.MinValue;
    // Set true while a board-obstructing UI element is visible (engine
    // dropdown context menu, file open dialog, etc.). The board window
    // is partially occluded during these - running YOLO on it produces
    // garbage FENs and bogus board-rect tracking. We pause Phase 2 entirely
    // while this is active, plus a short grace window after it closes so
    // the board window has time to repaint before we sample it again.
    private static volatile bool _obstructingUiActive = false;
    private static volatile bool _obstructingUiPreservesAnalysis = false;
    private static DateTime _obstructingUiGraceUntilUtc = DateTime.MinValue;
    private static readonly int _obstructingUiGraceMs = 250;
    private static DateTime _foregroundBoardSwitchSuppressedUntilUtc = DateTime.MinValue;
    private static readonly int _foregroundBoardSwitchSuppressMs = 2500;
    private static DateTime _recentMouseInteractionUntilUtc = DateTime.MinValue;
    private static DateTime _suppressOptimisticFenAfterMouseUntilUtc = DateTime.MinValue;
    private static DateTime _lastMouseOptimisticFenSkipLogUtc = DateTime.MinValue;
    private static readonly int _moverInferenceMouseGuardMs = 700;
    // Settle window after local mouse activity before the optimistic fast-FEN
    // path may confirm a move. This used to be 1200ms, then 200ms. The trace
    // showed the user's own move STILL falling to the slow remote-vision
    // confirm (~500-800ms) because this window kept the fast local diff-confirm
    // off until the drop was already settling. Lowered to 90ms: the path's own
    // gate already requires a COMPLETE legal move in the diff (a mid-animation
    // frame has the piece off its source but not yet on the destination, so it
    // matches nothing and is skipped), and any misread is rolled back by the
    // optimistic-correction machinery. This lets the user's own move confirm
    // locally - no vision round-trip - right as the piece lands.
    private static readonly int _localMouseOptimisticFenSuppressMs = 90;
    private static readonly int _mouseOptimisticFenSkipLogIntervalMs = 350;
    private static DateTime _transitionNoiseIgnoreUntilUtc = DateTime.MinValue;
    private const int BlitzPostInteractionNoiseIgnoreMs = 180;
    private const int BlitzPostConfirmedPositionNoiseIgnoreMs = 160;
    private static int _suspectEmptyFenFrames = 0;
    private static readonly int _suspectEmptyFenThreshold = 3;

    // Evaluation bar state
    private static bool _evalBarEnabled = false;
    private static EvalDisplayMode _evalDisplayMode = EvalDisplayMode.Bar;
    private static double _lastEvaluation = 0.0;

    // Engine lines state
    private static bool _engineLinesEnabled = false;
    internal static List<MoveVariation>? _lastAnalysisVariations { get => _state.LastAnalysisVariations; set => _state.LastAnalysisVariations = value; }


    // Reference to overlay forms
    private static OverlayForm? _overlay = null;
    private static EvalBarForm? _evalBar = null;
    private static EngineLinesForm? _engineLines = null;
    private static SettingsToolbarForm? _settingsToolbar = null;
    private static bool _settingsToolbarHidden = false;
    private static DateTime _lastToggleOverlayHotkeyUtc = DateTime.MinValue;
    private const int ToggleOverlayToolbarRecoveryDoubleTapMs = 650;
    private static AnalysisBoardForm? _analysisBoardForm = null;
    private static GameAnalysisForm? _gameAnalysisForm = null;
    private static readonly DebugHudPresenter _debugHudPresenter = new(() => (_settingsToolbar is { IsHandleCreated: true, Visible: true }) ? new System.Drawing.Point(_settingsToolbar.Left, _settingsToolbar.Bottom + 8) : (System.Drawing.Point?)null);
    private static OrientationPromptHost? _orientationPromptHost = null;
    private static int _externalPrimeInFlight = 0;
    private static readonly object _analysisBoardStateLock = new();
    private static bool _analysisBoardVisible = false;
    private static Rectangle _analysisBoardScreenRect = Rectangle.Empty;
    private static Rectangle _analysisBoardWindowScreenRect = Rectangle.Empty;
    private static string _analysisBoardFen = "";
    private static bool _analysisBoardIsFlipped = false;
    private static bool _analysisBoardHasTrackedHistory = false;
    internal static bool _currentFenIsAnalysisBoard { get => _state.CurrentFenIsAnalysisBoard; set => _state.CurrentFenIsAnalysisBoard = value; }
    private static bool _lastAnalysisBoardSnapshotVisible = false;
    private static bool _analysisTargetIsAnalysisBoard = false;
    internal static bool _externalBoardDetectedFlipped { get => _state.ExternalBoardDetectedFlipped; set => _state.ExternalBoardDetectedFlipped = value; }
    private static readonly Dictionary<string, bool> _manualOrientationOverrides = new();
    private static readonly Dictionary<string, LinkedListNode<string>> _manualOrientationOverrideNodes = new();
    private static readonly LinkedList<string> _manualOrientationOverrideOrder = new();
    private static readonly HashSet<string> _dismissedOrientationPrompts = new();
    private static readonly Dictionary<string, LinkedListNode<string>> _dismissedOrientationPromptNodes = new();
    private static readonly LinkedList<string> _dismissedOrientationPromptOrder = new();
    private const int ManualOrientationOverrideLimit = 1000;
    private const int DismissedOrientationPromptLimit = 1000;
    private static string _pendingOrientationPromptBoardPosition = "";
    private static char _pendingOrientationPromptReferenceColor = 'w';
    private static bool _orientationPromptVisible = false;
    private static bool _pendingOrientationPromptIsAnalysisBoard = false;
    private static readonly HashSet<string> _pendingOrientationPromptObservedBoards = new(StringComparer.Ordinal);
    internal static bool _externalOrientationLockedForCurrentGame { get => _state.ExternalOrientationLockedForCurrentGame; set => _state.ExternalOrientationLockedForCurrentGame = value; }
    // Streak tracking for orientation auto-lock: count consecutive confident
    // decisions agreeing before locking, so transient false positives don't
    // prematurely lock the wrong orientation.
    private static bool _orientationConfirmStreakFlipped = false;
    private static int _orientationConfirmStreakCount = 0;
    private const int OrientationLockStreakThreshold = 3;
    internal static int _externalTrackedPositionCount { get => _state.ExternalTrackedPositionCount; set => _state.ExternalTrackedPositionCount = value; }
    private static DateTime _recentOrientationDecisionUntilUtc = DateTime.MinValue;
    private static bool _recentOrientationDecisionFlipped = false;
    private static bool _recentOrientationDecisionIsAnalysisBoard = false;
    private static char _recentOrientationDecisionReferenceColor = 'w';
    private const int RecentOrientationDecisionWindowMs = 8000;
    private static DateTime _nextAnalysisAttemptUtc = DateTime.MinValue;
    private static DateTime _stableNoArrowSinceUtc = DateTime.MinValue;
    private static DateTime _lastStableNoArrowRecoveryUtc = DateTime.MinValue;

    // Coaching depth tracking
    private static DateTime _lastCoachingTime = DateTime.MinValue;
    private static string _lastCoachDisplayPositionKey = "";
    private static string _lastCoachDisplaySignature = "";
    private static DateTime _lastCoachDisplayUtc = DateTime.MinValue;
    private static string _pendingCoachDisplayKey = "";
    private static int _pendingCoachDisplayCount = 0;
    private static DateTime _pendingCoachDisplaySinceUtc = DateTime.MinValue;
    private const int CoachOverlaySwitchConfirmMs = 550;
    private static string _lastCoachLoadingSignature = "";
    private static string _lastCoachLoadingPositionKey = "";
    private static int _lastCoachLoadingDepth = 0;
    private static readonly object _coachOverlaySquaresLock = new();
    private static string _lastCoachOverlaySquaresPositionKey = "";
    private static HashSet<string> _lastCoachOverlaySquares = new(StringComparer.OrdinalIgnoreCase);

    // Feature flags
    private static bool _boardTraceEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            RefreshReleaseDiagnosticsSettingsIfNeeded();
            return _releaseBoardTraceEnabled;
#endif
        }
    }

    private static void RefreshReleaseDiagnosticsSettingsIfNeeded()
    {
#if !DEBUG
        DateTime now = DateTime.UtcNow;
        if ((now - _diagSettingsLastReadUtc).TotalMilliseconds < DiagnosticsSettingsRefreshMs)
            return;

        lock (_diagSettingsLock)
        {
            now = DateTime.UtcNow;
            if ((now - _diagSettingsLastReadUtc).TotalMilliseconds < DiagnosticsSettingsRefreshMs)
                return;

            bool latencyLogEnabled =
                IsTruthyDiagnosticsValue(Environment.GetEnvironmentVariable("CHESSKIT_DIAG_LOG")) ||
                IsTruthyDiagnosticsValue(Environment.GetEnvironmentVariable("CHESSKIT_LATENCY_LOG"));
            bool boardTraceEnabled =
                IsTruthyDiagnosticsValue(Environment.GetEnvironmentVariable("CHESSKIT_BOARD_TRACE_LOG"));

            // Release diagnostics are ENV-VAR-ONLY: a published build must never
            // write fps-diag.log for a normal user. A persisted settings.ini flag
            // or a stray diagnostics.ini sidecar no longer enables logging in
            // shipped builds - only an explicit CHESSKIT_DIAG_LOG / CHESSKIT_LATENCY_LOG
            // / CHESSKIT_BOARD_TRACE_LOG env var (which a normal install never sets)
            // turns it on for a support session.

            _releaseDiagLoggingEnabled = latencyLogEnabled || boardTraceEnabled;
            _releaseBoardTraceEnabled = boardTraceEnabled;
            _diagSettingsLastReadUtc = now;
        }
#endif
    }

    private static void TryLoadDiagnosticsSidecar(ref bool latencyLogEnabled, ref bool boardTraceEnabled)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, DiagnosticsFileName);
            if (!File.Exists(path))
                return;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith(";", StringComparison.Ordinal) ||
                    line.StartsWith("#", StringComparison.Ordinal) ||
                    (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();
                if (!IsTruthyDiagnosticsValue(value))
                    continue;

                if (key.Equals("DiagnosticsLatencyLogEnabled", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("LatencyLog", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Latency", StringComparison.OrdinalIgnoreCase))
                {
                    latencyLogEnabled = true;
                }
                else if (key.Equals("DiagnosticsBoardTraceEnabled", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("BoardTrace", StringComparison.OrdinalIgnoreCase))
                {
                    boardTraceEnabled = true;
                }
            }
        }
        catch
        {
            // Diagnostics must never affect startup or tracking.
        }
    }

    private static bool IsTruthyDiagnosticsValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    // ELO limiting state
    private static int _maxEloRating = 2000;
    private static bool _eloLimitEnabled = false;
    private static bool _humanAdaptiveEnabled = true;
    private static HumanPlayProfile _humanPlayProfile = HumanPlayProfile.Balanced;

    internal static void Log(string message)
    {
#if DEBUG
        DebugRuntime.WriteLine(message);
#else
        if (!_releaseRuntimeLogEnabled)
            return;

        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, RuntimeLogFileName);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_runtimeLogLock)
            {
                RotateRuntimeLogIfNeeded(logPath);
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Runtime logging must never affect tracking, analysis, or overlay rendering.
        }
#endif
    }

    // Bridge for PredictiveVision shadow measurement (Experiment A). Routes to
    // the same runtime log the user already collects, so shadow stats land in
    // the file they copy back. Centralised so it can be throttled/disabled later.
    internal static void PredictLog(string message) => Log(message);

    private static void InitializeReleaseRuntimeLog()
    {
#if !DEBUG
        if (!_releaseRuntimeLogEnabled)
            return;

        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, RuntimeLogFileName);
            string line =
                $"{Environment.NewLine}[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] " +
                $"[INFO] Runtime log started. BaseDirectory={AppContext.BaseDirectory}{Environment.NewLine}";
            lock (_runtimeLogLock)
            {
                RotateRuntimeLogIfNeeded(logPath);
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging is diagnostic only.
        }
#endif
    }

    private static void RotateRuntimeLogIfNeeded(string logPath)
    {
#if !DEBUG
        const long maxBytes = 8L * 1024L * 1024L;
        if (!File.Exists(logPath))
            return;

        var info = new FileInfo(logPath);
        if (info.Length <= maxBytes)
            return;

        string previousPath = Path.Combine(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory, "runtime.previous.log");
        try
        {
            if (File.Exists(previousPath))
                File.Delete(previousPath);

            File.Move(logPath, previousPath);
        }
        catch
        {
            // If rotation fails, keep appending; never break runtime behavior.
        }
#else
        _ = logPath;
#endif
    }

    /// <summary>
    /// Writes an opt-in diagnostic line to fps-diag.log next to the executable.
    /// Debug builds enable it by default; Release builds require settings.ini/env opt-in.
    /// </summary>
    private static void LogDiag(string tag, string message)
    {
        if (!_diagLoggingEnabled) return;
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
        AppendDiagnosticLine(line);
    }

    private static void AppendDiagnosticLine(string line)
    {
        try
        {
            string logPath = Path.Combine(AppContext.BaseDirectory, DiagnosticLogFileName);
            lock (_diagLogLock)
            {
                long nowTicks = DateTime.UtcNow.Ticks;
                if (nowTicks >= Volatile.Read(ref _nextDiagLogSizeCheckTicks))
                {
                    Volatile.Write(ref _nextDiagLogSizeCheckTicks, nowTicks + TimeSpan.TicksPerSecond);
                    RotateDiagnosticLogIfNeeded(logPath);
                }

                File.AppendAllText(logPath, line);
            }
        }
        catch { /* never let logging break the app */ }
    }

    private static void RotateDiagnosticLogIfNeeded(string logPath)
    {
#if !DEBUG
        const long maxBytes = 16L * 1024L * 1024L;
        if (!File.Exists(logPath))
            return;

        var info = new FileInfo(logPath);
        if (info.Length <= maxBytes)
            return;

        string previousPath = Path.Combine(Path.GetDirectoryName(logPath) ?? AppContext.BaseDirectory, "fps-diag.previous.log");
        try
        {
            if (File.Exists(previousPath))
                File.Delete(previousPath);

            File.Move(logPath, previousPath);
        }
        catch
        {
            // Keep appending if rotation fails; diagnostics must never affect runtime.
        }
#else
        _ = logPath;
#endif
    }

    private static void UpdateConsoleTitle(string title)
    {
#if DEBUG
        Console.Title = title;
#endif
    }

    private static void DisposeAllEngines()
    {
        try { _analysisTimer?.Dispose(); } catch { }
        _analysisTimer = null;

        try { _stockfish?.Dispose(); } catch { }
        _stockfish = null;

        _analysisBoardController?.DisposeEnginesAndTimers();
    }

    private static string GetCurrentEngineDisplayName()
    {
        if (string.IsNullOrWhiteSpace(_stockfishPath))
            return "None";

        string fileName = Path.GetFileNameWithoutExtension(_stockfishPath);
        return string.IsNullOrWhiteSpace(fileName) ? "None" : fileName;
    }

    private static bool IsHumanEnginePath(string? enginePath)
    {
        if (string.IsNullOrWhiteSpace(enginePath))
            return false;

        string engineName = Path.GetFileNameWithoutExtension(enginePath).ToLowerInvariant();
        return engineName.Contains("human");
    }

    private static void CleanupOrphanedEngineProcesses(string enginesDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(enginesDir) || !Directory.Exists(enginesDir))
                return;

            string enginesRoot = Path.GetFullPath(enginesDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            int currentProcessId = Environment.ProcessId;
            int killedCount = 0;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == currentProcessId || process.HasExited)
                        continue;

                    string? processPath = null;
                    try { processPath = process.MainModule?.FileName; } catch { }
                    if (string.IsNullOrWhiteSpace(processPath))
                        continue;

                    string fullPath = Path.GetFullPath(processPath);
                    if (!fullPath.StartsWith(enginesRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Log($"[ENGINE] Startup cleanup: killing orphaned engine {Path.GetFileName(fullPath)} pid={process.Id}");
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        process.Kill();
                    }
                    killedCount++;
                }
                catch
                {
                    // Process may exit while enumerating, or deny MainModule access.
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                }
            }

            if (killedCount > 0)
                Log($"[ENGINE] Startup cleanup removed {killedCount} orphaned engine process(es).");
        }
        catch (Exception ex)
        {
            Log($"[ENGINE] Startup cleanup failed: {ex.Message}");
        }
    }

    private static void ApplyEngineSpecificSettings(UCIEngine engine)
    {
        engine.EloLimitEnabled = _eloLimitEnabled;
        engine.MaxEloRating = _maxEloRating;
        engine.AdaptiveHuman = _humanAdaptiveEnabled;
        engine.HumanPlayProfile = _humanPlayProfile;
    }

    private static void LogCurrentEngine(string context)
    {
        Log($"[ENGINE] {context}: {GetCurrentEngineDisplayName()}");
        RefreshDebugView();
    }

    // An engine path is usable if a local binary exists OR the broker serves
    // that engine remotely (remote-only engines like the human model have no
    // local file). Engine-start guards use this so remote-only engines are
    // selectable and startable.
    private static bool IsUsableEnginePath(string? enginePath)
    {
        if (string.IsNullOrWhiteSpace(enginePath))
            return false;
        return File.Exists(enginePath) || RemoteEngineClient.IsEngineRemotelyServed(enginePath);
    }

    private static string ResolveInitialEnginePath()
    {
        var enginesDir = Path.Combine(AppContext.BaseDirectory, "engines");

        try
        {
            var engineManager = new EngineManager(enginesDir);
            engineManager.LoadSettings();

            var selectedPath = engineManager.GetCurrentEnginePath();
            // Honor a saved remote selection too (remote:// paths have no local
            // file), not just local binaries.
            if (!string.IsNullOrWhiteSpace(selectedPath) && IsUsableEnginePath(selectedPath))
            {
                Log($"[INFO] Initial engine selected: {selectedPath}");
                return selectedPath;
            }
        }
        catch (Exception ex)
        {
            Log($"[WARNING] Failed to load saved engine selection: {ex.Message}");
        }

        var stockfishPath = Path.Combine(enginesDir, "stockfish-windows-x86-64-avx2.exe");
        if (!File.Exists(stockfishPath) && Directory.Exists(enginesDir))
        {
            var stockfishFiles = Directory.GetFiles(enginesDir, "stockfish*.exe");
            if (stockfishFiles.Length > 0)
            {
                stockfishPath = stockfishFiles[0];
                Log($"[INFO] Found Stockfish fallback: {Path.GetFileName(stockfishPath)}");
            }
        }

        if (!File.Exists(stockfishPath))
        {
            Log("[WARNING] No UCI engine found in /engines folder");
            Log("[WARNING] Move suggestions (F2/F4) will be disabled");
            Log("");
            return "";
        }

        return stockfishPath;
    }

    private static bool RunStartupFlow()
    {
        bool shouldContinue = true;
        bool persistStartupFlow = !BuildLimits.IsDebugFreeEditionOverride;
        var settingsManager = new AppSettingsManager(Path.Combine(AppContext.BaseDirectory, "settings.ini"));
        var settings = settingsManager.Load();

        void ShowFlow()
        {
            bool legacyFreeTermsAccepted = BuildLimits.IsFreeEdition &&
                settings.LegacyFreeTermsAccepted &&
                (string.Equals(settings.LegacyFreeTermsVersion, StartupTermsVersion, StringComparison.Ordinal) ||
                 string.Equals(settings.LegacyFreeTermsVersion, "2026-05-04", StringComparison.Ordinal));
            bool termsAccepted = persistStartupFlow &&
                ((settings.StartupTermsAccepted &&
                  string.Equals(settings.StartupTermsVersion, StartupTermsVersion, StringComparison.Ordinal)) ||
                 legacyFreeTermsAccepted);

            if (!termsAccepted)
            {
                using var termsForm = new FreeTermsForm();
                if (termsForm.ShowDialog() != DialogResult.OK)
                {
                    shouldContinue = false;
                    return;
                }

                if (persistStartupFlow)
                {
                    settings.StartupTermsAccepted = true;
                    settings.StartupTermsVersion = StartupTermsVersion;
                    settings.LegacyFreeTermsAccepted = BuildLimits.IsFreeEdition;
                    settings.LegacyFreeTermsVersion = BuildLimits.IsFreeEdition ? StartupTermsVersion : settings.LegacyFreeTermsVersion;
                    settingsManager.Save(settings);
                }
            }

            bool legacyFreeWelcomeCompleted = BuildLimits.IsFreeEdition && settings.LegacyFreeWelcomeCompleted;
            if (!persistStartupFlow || (!settings.StartupWelcomeCompleted && !legacyFreeWelcomeCompleted))
            {
                using var welcomeForm = new FreeWelcomeForm();
                if (welcomeForm.ShowDialog() == DialogResult.OK)
                {
                    if (persistStartupFlow)
                    {
                        settings.StartupWelcomeCompleted = true;
                        settings.LegacyFreeWelcomeCompleted = BuildLimits.IsFreeEdition;
                        settingsManager.Save(settings);
                    }
                }
            }
        }

        try
        {
            if (_overlay != null && !_overlay.IsDisposed && _overlay.IsHandleCreated)
            {
                if (_overlay.InvokeRequired)
                    _overlay.Invoke(new Action(ShowFlow));
                else
                    ShowFlow();
            }
            else
            {
                ShowFlow();
            }
        }
        catch
        {
            shouldContinue = false;
        }

        return shouldContinue;
    }

    private static void CloseUiThreadAfterStartupCancel()
    {
        try
        {
            CloseStartupStatus();
            if (_overlay != null && !_overlay.IsDisposed && _overlay.IsHandleCreated)
            {
                _overlay.BeginInvoke(new Action(() =>
                {
                    _overlay.Close();
                    Application.ExitThread();
                }));
            }
        }
        catch
        {
            // The process is leaving before engines/detector start; best effort.
        }
    }

    private static void ShowStartupStatus(string status, int? progressPercent = null, bool indeterminate = false)
    {
#if DEBUG
        return;
#else
        void ShowOrUpdate()
        {
            if (_startupStatusForm == null || _startupStatusForm.IsDisposed)
            {
                _startupStatusForm = new StartupStatusForm();
                AppIcon.ApplyTo(_startupStatusForm);
                _startupStatusForm.Show();
            }

            _startupStatusForm.SetStatus(status, progressPercent, indeterminate);
            _startupStatusForm.TopMost = true;
            _startupStatusForm.BringToFront();
        }

        try
        {
            if (_overlay?.InvokeRequired == true)
                _overlay.BeginInvoke(new Action(ShowOrUpdate));
            else
                ShowOrUpdate();
        }
        catch
        {
        }
#endif
    }

    private static void UpdateStartupStatus(string status, int? progressPercent = null, bool indeterminate = false)
    {
#if DEBUG
        return;
#else
        try
        {
            if (_startupStatusForm == null || _startupStatusForm.IsDisposed)
            {
                ShowStartupStatus(status, progressPercent, indeterminate);
                return;
            }

            if (_startupStatusForm.InvokeRequired)
                _startupStatusForm.BeginInvoke(new Action(() => _startupStatusForm?.SetStatus(status, progressPercent, indeterminate)));
            else
                _startupStatusForm.SetStatus(status, progressPercent, indeterminate);
        }
        catch
        {
        }
#endif
    }

    private static void CloseStartupStatus()
    {
#if DEBUG
        return;
#else
        void CloseForm()
        {
            try
            {
                if (_startupStatusForm != null && !_startupStatusForm.IsDisposed)
                    _startupStatusForm.Close();
            }
            catch
            {
            }
            finally
            {
                _startupStatusForm = null;
            }
        }

        try
        {
            if (_startupStatusForm?.InvokeRequired == true)
                _startupStatusForm.BeginInvoke(new Action(CloseForm));
            else if (_overlay?.InvokeRequired == true)
                _overlay.BeginInvoke(new Action(CloseForm));
            else
                CloseForm();
        }
        catch
        {
        }
#endif
    }

#if DEBUG
    private static bool IsDebugFreeEditionArgument(string arg)
    {
        return string.Equals(arg, "--free", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-free", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/free", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "free", StringComparison.OrdinalIgnoreCase);
    }
#endif

}
