using Chess;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ChessKit
{
    /// <summary>
    /// Owns the standalone Analysis Board feature (engine-vs-engine match, live engine
    /// analysis loop, game review, FEN mirroring, and engine lifecycle for the analysis
    /// board).
    ///
    /// The controller reaches the rest of the app only through the delegates and
    /// shared-state accessors injected at construction time. Shared orientation /
    /// snapshot / "current FEN is analysis board" state is read and written here
    /// through those injected accessors; the arrow-move-resolution helpers and the
    /// live-analysis togglers that operate on the live overlay pipeline are owned
    /// elsewhere.
    /// </summary>
    public sealed class AnalysisBoardController
    {
        // ---- Injected live-core collaborators (no Program back-reference) ----
        private readonly Action<string> _log;
        private readonly Action<string> _refreshDebugView;
        private readonly Func<string, IEnumerable<MoveVariation>, char, bool, int, List<MoveArrow>> _buildArrowsForFen;
        private readonly Func<string, char?> _getSideToMove;
        private readonly Func<string, string> _getBoardPosition;
        private readonly Func<string, bool, bool> _ensureLicensedFeatureAvailable;
        private readonly OrientationDecisionDelegate _tryResolveOrientationDecision;
        private readonly Func<string?, bool> _isUsableEnginePath;
        private readonly Func<string?, bool> _isHumanEnginePath;
        private readonly Func<UCIEngine?, string, string> _getEngineFailureMessage;
        private readonly Func<string?, string?, string> _getEngineStartupFeedback;
        private readonly Func<string?, string?, bool> _isPrivateEngineStartupBlocked;
        private readonly Action<string> _showPrivateEngineLicenseNotice;
        private readonly Action<UCIEngine> _applyEngineSpecificSettings;
        private readonly Func<string, bool> _isActiveAnalysisBoardFen;
        private readonly SnapshotTryGetDelegate _tryGetActiveAnalysisBoardSnapshot;
        private readonly SnapshotTryGetDelegate _tryGetStoredAnalysisBoardSnapshot;

        // ---- Injected accessors for state that stays owned by Program ----
        private readonly Func<AnalysisBoardForm?> _analysisBoardForm;
        private readonly Func<GameAnalysisForm?> _getGameAnalysisForm;
        private readonly Action<GameAnalysisForm?> _setGameAnalysisForm;
        private readonly Func<string> _currentFen;
        private readonly Func<bool> _currentFenIsAnalysisBoard;
        private readonly Func<bool> _externalBoardDetectedFlipped;
        private readonly Func<string, string> _applyInferredExternalTurnToFen;
        private readonly Func<bool> _humanAdaptiveEnabled;
        private readonly Func<HumanPlayProfile> _humanPlayProfile;
        private readonly Func<int> _quickArrowThinkTimeMs;
        private readonly Func<int> _quickArrowDepth;
        private readonly Func<string> _initialBoardPosition;
        private readonly Func<string> _initialBoardPositionRotated;

        public delegate bool OrientationDecisionDelegate(string fen, bool isAnalysisBoard, char referenceColor, out bool? detectedBoardFlipped);
        public delegate bool SnapshotTryGetDelegate(out AnalysisBoardSnapshot snapshot);

        public AnalysisBoardController(
            Action<string> log,
            Action<string> refreshDebugView,
            Func<string, IEnumerable<MoveVariation>, char, bool, int, List<MoveArrow>> buildArrowsForFen,
            Func<string, char?> getSideToMove,
            Func<string, string> getBoardPosition,
            Func<string, bool, bool> ensureLicensedFeatureAvailable,
            OrientationDecisionDelegate tryResolveOrientationDecision,
            Func<string?, bool> isUsableEnginePath,
            Func<string?, bool> isHumanEnginePath,
            Func<UCIEngine?, string, string> getEngineFailureMessage,
            Func<string?, string?, string> getEngineStartupFeedback,
            Func<string?, string?, bool> isPrivateEngineStartupBlocked,
            Action<string> showPrivateEngineLicenseNotice,
            Action<UCIEngine> applyEngineSpecificSettings,
            Func<string, bool> isActiveAnalysisBoardFen,
            SnapshotTryGetDelegate tryGetActiveAnalysisBoardSnapshot,
            SnapshotTryGetDelegate tryGetStoredAnalysisBoardSnapshot,
            Func<AnalysisBoardForm?> analysisBoardForm,
            Func<GameAnalysisForm?> getGameAnalysisForm,
            Action<GameAnalysisForm?> setGameAnalysisForm,
            Func<string> currentFen,
            Func<bool> currentFenIsAnalysisBoard,
            Func<bool> externalBoardDetectedFlipped,
            Func<string, string> applyInferredExternalTurnToFen,
            Func<bool> humanAdaptiveEnabled,
            Func<HumanPlayProfile> humanPlayProfile,
            Func<int> quickArrowThinkTimeMs,
            Func<int> quickArrowDepth,
            Func<string> initialBoardPosition,
            Func<string> initialBoardPositionRotated)
        {
            _log = log;
            _refreshDebugView = refreshDebugView;
            _buildArrowsForFen = buildArrowsForFen;
            _getSideToMove = getSideToMove;
            _getBoardPosition = getBoardPosition;
            _ensureLicensedFeatureAvailable = ensureLicensedFeatureAvailable;
            _tryResolveOrientationDecision = tryResolveOrientationDecision;
            _isUsableEnginePath = isUsableEnginePath;
            _isHumanEnginePath = isHumanEnginePath;
            _getEngineFailureMessage = getEngineFailureMessage;
            _getEngineStartupFeedback = getEngineStartupFeedback;
            _isPrivateEngineStartupBlocked = isPrivateEngineStartupBlocked;
            _showPrivateEngineLicenseNotice = showPrivateEngineLicenseNotice;
            _applyEngineSpecificSettings = applyEngineSpecificSettings;
            _isActiveAnalysisBoardFen = isActiveAnalysisBoardFen;
            _tryGetActiveAnalysisBoardSnapshot = tryGetActiveAnalysisBoardSnapshot;
            _tryGetStoredAnalysisBoardSnapshot = tryGetStoredAnalysisBoardSnapshot;
            _analysisBoardForm = analysisBoardForm;
            _getGameAnalysisForm = getGameAnalysisForm;
            _setGameAnalysisForm = setGameAnalysisForm;
            _currentFen = currentFen;
            _currentFenIsAnalysisBoard = currentFenIsAnalysisBoard;
            _externalBoardDetectedFlipped = externalBoardDetectedFlipped;
            _applyInferredExternalTurnToFen = applyInferredExternalTurnToFen;
            _humanAdaptiveEnabled = humanAdaptiveEnabled;
            _humanPlayProfile = humanPlayProfile;
            _quickArrowThinkTimeMs = quickArrowThinkTimeMs;
            _quickArrowDepth = quickArrowDepth;
            _initialBoardPosition = initialBoardPosition;
            _initialBoardPositionRotated = initialBoardPositionRotated;
        }

        private void Log(string message) => _log(message);

        // ====================================================================
        // Feature-private state (moved verbatim from Program.State.cs)
        // ====================================================================
        private UCIEngine? _analysisBoardStockfish = null;
        private bool _analysisBoardEngineStartInProgress = false;
        private readonly object _analysisBoardEngineStartLock = new();
        private Task _analysisBoardEngineShutdownTask = Task.CompletedTask;
        private string _analysisBoardEngineStartupFailurePath = "";
        private string _analysisBoardEngineStartupFailureMessage = "";
        private string _analysisBoardEnginePath = "";
        private string _analysisBoardEngineName = "Engine";
        private int _analysisBoardEngineDepth = BuildLimits.ClampDepth(12);
        private bool _analysisBoardEngineInfinite = false;
        private int _analysisBoardLineCount = BuildLimits.ClampLines(3);
        private int _analysisBoardEngineThreads = 1;
        private int _analysisBoardEngineHashMb = 32;
        private UCIEngine? _analysisBoardMatchWhiteEngine = null;
        private UCIEngine? _analysisBoardMatchBlackEngine = null;
        private string _analysisBoardMatchWhiteEnginePath = "";
        private string _analysisBoardMatchWhiteEngineName = "White Engine";
        private string _analysisBoardMatchBlackEnginePath = "";
        private string _analysisBoardMatchBlackEngineName = "Black Engine";
        private string _analysisBoardMatchCompetitorAPath = "";
        private string _analysisBoardMatchCompetitorAName = "Engine A";
        private string _analysisBoardMatchCompetitorBPath = "";
        private string _analysisBoardMatchCompetitorBName = "Engine B";
        private int _analysisBoardMatchBaseSeconds = BuildLimits.ClampMatchSeconds(180);
        private string _analysisBoardMatchTimeControlKey = "3 min";
        private int _analysisBoardMatchGameLimit = BuildLimits.ClampMatchGameLimit(0);
        private int _analysisBoardMatchThreads = BuildLimits.ClampThreads(1);
        private int _analysisBoardMatchHashMb = BuildLimits.ClampHashMb(32);
        private bool _analysisBoardMatchRunning = false;
        private bool _analysisBoardMatchPaused = false;
        private bool _analysisBoardMatchStarting = false;
        private bool _analysisBoardMatchMoveInProgress = false;
        private int _analysisBoardMatchAnalysisPreviewVersion = 0;
        private System.Threading.Timer? _analysisBoardMatchTimer = null;
        private int _analysisBoardMatchSessionVersion = 0;
        private long _analysisBoardMatchWhiteRemainingMs = 180000;
        private long _analysisBoardMatchBlackRemainingMs = 180000;
        private DateTime _analysisBoardMatchTurnStartedUtc = DateTime.MinValue;
        private char _analysisBoardMatchTurnColor = 'w';
        private int _analysisBoardMatchWhiteWins = 0;
        private int _analysisBoardMatchBlackWins = 0;
        private int _analysisBoardMatchDraws = 0;
        private string _analysisBoardMatchStatus = "Ready for engine match.";
        private long _analysisBoardMatchDisplayVersion = 0;
        private bool _analysisBoardAnalysisEnabled = false;
        private string _analysisBoardAnalysisMode = "OFF";
        private System.Threading.Timer? _analysisBoardAnalysisTimer = null;
        private bool _analysisBoardAnalysisInProgress = false;
        private string _analysisBoardLastAnalysisKey = "";
        private bool _analysisBoardAnalysisRestartPending = false;
        private bool _analysisBoardAnalysisAbortInProgress = false;
        // Cancels the in-flight analysis-board search when a newer position supersedes
        // it, so a stale infinite search can't switch the shared engine back to its old
        // position (the thrash that made self-play lag grow with every move).
        private System.Threading.CancellationTokenSource? _analysisBoardAnalysisCts;
        private int _analysisBoardAnalysisSessionVersion = 0;
        private bool _analysisBoardMirrorEnabled = false;
        private string _lastMirroredExternalFen = "";

        /// <summary>True while analysis-board mirror mode is on (the board streams
        /// vision-detected external FENs — its only server-backed feature). Surfaced
        /// for the debug HUD and any code that must know mirror is active.</summary>
        public bool IsMirrorEnabled => _analysisBoardMirrorEnabled;

        // ====================================================================
        // Public façade for live-path / Program callers and form events
        // ====================================================================

        public bool MatchRunning => _analysisBoardMatchRunning;
        public bool AnalysisEnabled => _analysisBoardAnalysisEnabled;
        public string AnalysisMode => _analysisBoardAnalysisMode;
        public int LineCount => _analysisBoardLineCount;
        public long MatchBaseSeconds => _analysisBoardMatchBaseSeconds;
        public int AnalysisSessionVersion => _analysisBoardAnalysisSessionVersion;

        /// <summary>
        /// Clears the cached analysis key so the next queue cycle re-runs. Invoked by the
        /// shared snapshot-update path in Program when the analysis-board position/visibility
        /// changes (it owns the snapshot state; this owns the analysis key).
        /// </summary>
        public void ClearLastAnalysisKey() => _analysisBoardLastAnalysisKey = "";

        /// <summary>Disposes timers and engines owned by the analysis board feature.</summary>
        public void DisposeEnginesAndTimers()
        {
            try { _analysisBoardAnalysisTimer?.Dispose(); } catch { }
            _analysisBoardAnalysisTimer = null;

            try { _analysisBoardMatchTimer?.Dispose(); } catch { }
            _analysisBoardMatchTimer = null;

            try { _analysisBoardStockfish?.Dispose(); } catch { }
            _analysisBoardStockfish = null;

            try { _analysisBoardMatchWhiteEngine?.Dispose(); } catch { }
            _analysisBoardMatchWhiteEngine = null;

            try { _analysisBoardMatchBlackEngine?.Dispose(); } catch { }
            _analysisBoardMatchBlackEngine = null;
        }

        /// <summary>Tears down the analysis-board engine when a live engine switch disposes it too.</summary>
        public void OnLiveEngineSwitched()
        {
            _analysisBoardStockfish?.Dispose();
            _analysisBoardStockfish = null;
            _analysisBoardLastAnalysisKey = "";
        }

        /// <summary>Disables analysis-board analysis when the runtime license is invalidated.</summary>
        public void OnLicenseInvalidated()
        {
            _analysisBoardAnalysisEnabled = false;
        }

        /// <summary>Free Edition gate: whether the analysis board live assisted-move ply limit is exhausted.</summary>
        public bool IsFreeLiveLimitReached() => IsFreeAnalysisBoardLiveLimitReached();

        // ====================================================================
        // Moved implementation (verbatim except injected-collaborator rewiring)
        // ====================================================================

        public bool IsCurrentAnalysisBoardAnalysisStillValid(string fen, bool boardFlipped, char expectedMovingSide)
        {
            if (!_tryGetActiveAnalysisBoardSnapshot(out var snapshot))
                return false;

            if (!string.Equals(snapshot.Fen, fen, StringComparison.Ordinal))
            {
                return false;
            }

            if (snapshot.BoardFlipped != boardFlipped && !_analysisBoardMatchRunning)
                return false;

            char? liveSideToMove = _getSideToMove(snapshot.Fen);
            if (!liveSideToMove.HasValue || liveSideToMove.Value != expectedMovingSide)
                return false;

            return _analysisBoardAnalysisMode == "BOTH" ||
                   (_analysisBoardAnalysisMode == "WHITE" && expectedMovingSide == 'w') ||
                   (_analysisBoardAnalysisMode == "BLACK" && expectedMovingSide == 'b');
        }

        public void HandleAnalysisBoardAnalysisModeChanged(string mode)
        {
            _analysisBoardAnalysisSessionVersion++;
            _analysisBoardAnalysisTimer?.Dispose();
            _analysisBoardAnalysisTimer = null;
            _analysisBoardAnalysisInProgress = false;
            _analysisBoardAnalysisRestartPending = false;
            _analysisBoardAnalysisAbortInProgress = false;
            _analysisBoardLastAnalysisKey = "";
            _analysisBoardAnalysisMode = mode;
            ClearAnalysisBoardEngineStartupFailure();
            var modeForm = _analysisBoardForm();
            if (modeForm != null)
            {
                if (modeForm.InvokeRequired)
                {
                    modeForm.BeginInvoke(new Action(() =>
                    {
                        modeForm.ClearAnalysisArrows();
                        modeForm.ClearAnalysisVariations();
                        modeForm.SetAnalysisStatus(mode == "OFF"
                            ? "Select W, B, or W+B to start analysis."
                            : "Preparing analysis...");
                    }));
                }
                else
                {
                    modeForm.ClearAnalysisArrows();
                    modeForm.ClearAnalysisVariations();
                    modeForm.SetAnalysisStatus(mode == "OFF"
                        ? "Select W, B, or W+B to start analysis."
                        : "Preparing analysis...");
                }
            }

            if (mode == "OFF")
            {
                _analysisBoardAnalysisEnabled = false;
                DisposeAnalysisBoardAnalysisEngine();
                Log($"[{DateTime.Now:HH:mm:ss}] Analysis board analysis DISABLED");
                _refreshDebugView("Analysis board analysis disabled");
                return;
            }

            if (!_ensureLicensedFeatureAvailable("analysis board engine analysis", true))
            {
                _analysisBoardAnalysisEnabled = false;
                SetAnalysisBoardAnalysisStatus("License verification required.");
                _refreshDebugView("Analysis board analysis blocked by license");
                return;
            }

            _analysisBoardAnalysisEnabled = true;
            int sessionVersion = _analysisBoardAnalysisSessionVersion;
            _analysisBoardAnalysisTimer = new System.Threading.Timer(
                _ => TryQueueAnalysisBoardAnalysis(sessionVersion),
                null,
                TimeSpan.FromMilliseconds(80),
                TimeSpan.FromMilliseconds(300));

            Log($"[{DateTime.Now:HH:mm:ss}] Analysis board analysis ENABLED ({mode})");
            _refreshDebugView($"Analysis board analysis enabled ({mode})");
            TryQueueAnalysisBoardAnalysis(sessionVersion);
        }

        private void DisposeAnalysisBoardAnalysisEngine()
        {
            var oldEngine = _analysisBoardStockfish;
            _analysisBoardStockfish = null;
            lock (_analysisBoardEngineStartLock)
            {
                _analysisBoardEngineStartInProgress = false;
            }
            _analysisBoardAnalysisInProgress = false;
            _analysisBoardAnalysisRestartPending = false;
            _analysisBoardAnalysisAbortInProgress = false;

            if (oldEngine == null)
                return;

            ScheduleAnalysisBoardEngineDisposal(oldEngine, "analysis disabled");
        }

        private bool TryBeginAnalysisBoardEngineStart()
        {
            lock (_analysisBoardEngineStartLock)
            {
                if (_analysisBoardEngineStartInProgress)
                    return false;

                _analysisBoardEngineStartInProgress = true;
                return true;
            }
        }

        private void ScheduleAnalysisBoardEngineDisposal(UCIEngine? engine, string reason)
        {
            if (engine == null)
                return;

            Log($"[ANALYSIS SETTINGS] Disposing Analysis Board engine ({reason}).");
            Task disposalTask = Task.Run(async () => await DisposeAnalysisBoardEngineInstanceAsync(engine, reason));
            lock (_analysisBoardEngineStartLock)
            {
                _analysisBoardEngineShutdownTask = disposalTask;
            }
        }

        private async Task WaitForAnalysisBoardEngineShutdownAsync()
        {
            Task shutdownTask;
            lock (_analysisBoardEngineStartLock)
            {
                shutdownTask = _analysisBoardEngineShutdownTask;
            }

            await shutdownTask;
        }

        private async Task DisposeAnalysisBoardEngineInstanceAsync(UCIEngine engine, string reason)
        {
            try { await engine.AbortCurrentAnalysisAsync(); } catch { }
            try { await engine.StopInfiniteAnalysis(); } catch { }
            try
            {
                engine.Dispose();
                Log($"[ANALYSIS SETTINGS] Disposed Analysis Board engine ({reason}).");
            }
            catch (Exception ex)
            {
                Log($"[ANALYSIS SETTINGS] Failed to dispose Analysis Board engine ({reason}): {ex.Message}");
            }
        }

        private void ClearAnalysisBoardEngineStartupFailure()
        {
            _analysisBoardEngineStartupFailurePath = "";
            _analysisBoardEngineStartupFailureMessage = "";
        }

        private bool TryGetAnalysisBoardEngineStartupFailure(out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(_analysisBoardEngineStartupFailureMessage))
                return false;

            if (!string.Equals(_analysisBoardEngineStartupFailurePath, _analysisBoardEnginePath, StringComparison.OrdinalIgnoreCase))
                return false;

            message = _analysisBoardEngineStartupFailureMessage;
            return true;
        }

        public void HandleAnalysisBoardAnalysisSettingsChanged(AnalysisBoardAnalysisSettings settings)
        {
            string previousEnginePath = _analysisBoardEnginePath;
            string previousEngineName = _analysisBoardEngineName;
            int previousDepth = _analysisBoardEngineDepth;
            bool previousInfinite = _analysisBoardEngineInfinite;
            int previousLineCount = _analysisBoardLineCount;
            int previousThreads = _analysisBoardEngineThreads;
            int previousHashMb = _analysisBoardEngineHashMb;

            _analysisBoardEnginePath = settings.EnginePath ?? "";
            _analysisBoardEngineName = string.IsNullOrWhiteSpace(settings.EngineName) ? "Engine" : settings.EngineName;
            _analysisBoardEngineDepth = BuildLimits.ClampDepth(settings.Depth);
            _analysisBoardEngineInfinite = BuildLimits.AllowInfiniteAnalysis && settings.Infinite;
            _analysisBoardLineCount = BuildLimits.ClampLines(settings.LineCount);
            _analysisBoardEngineThreads = BuildLimits.ClampThreads(settings.Threads);
            _analysisBoardEngineHashMb = BuildLimits.ClampHashMb(settings.HashMb);

            bool engineChanged = !string.Equals(previousEnginePath, _analysisBoardEnginePath, StringComparison.OrdinalIgnoreCase);
            bool tuningChanged =
                previousDepth != _analysisBoardEngineDepth ||
                previousInfinite != _analysisBoardEngineInfinite ||
                previousLineCount != _analysisBoardLineCount ||
                previousThreads != _analysisBoardEngineThreads ||
                previousHashMb != _analysisBoardEngineHashMb;
            if (engineChanged || tuningChanged)
            {
                string previousName = string.IsNullOrWhiteSpace(previousEngineName) ? "(none)" : previousEngineName;
                string reason = engineChanged
                    ? $"engine {previousName} -> {_analysisBoardEngineName}"
                    : "analysis tuning changed";
                Log($"[ANALYSIS SETTINGS] Live analysis settings changed: {reason}; enabled={_analysisBoardAnalysisEnabled}; depth={_analysisBoardEngineDepth}; lines={_analysisBoardLineCount}; threads={_analysisBoardEngineThreads}; hash={_analysisBoardEngineHashMb}MB");
            }

            int multipv = BuildLimits.ClampLines(_analysisBoardLineCount);
            if (_analysisBoardMatchWhiteEngine != null || _analysisBoardMatchBlackEngine != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_analysisBoardMatchWhiteEngine != null)
                            await _analysisBoardMatchWhiteEngine.SendCommandAsync($"setoption name MultiPV value {multipv}");
                        if (_analysisBoardMatchBlackEngine != null)
                            await _analysisBoardMatchBlackEngine.SendCommandAsync($"setoption name MultiPV value {multipv}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[MATCH] Failed to update match MultiPV: {ex.Message}");
                    }
                });
            }

            var oldEngine = _analysisBoardStockfish;
            _analysisBoardStockfish = null;
            lock (_analysisBoardEngineStartLock)
            {
                _analysisBoardEngineStartInProgress = false;
            }
            _analysisBoardLastAnalysisKey = "";
            _analysisBoardAnalysisInProgress = false;
            _analysisBoardAnalysisRestartPending = false;
            _analysisBoardAnalysisAbortInProgress = false;
            _analysisBoardAnalysisSessionVersion++;
            ClearAnalysisBoardEngineStartupFailure();

            if (oldEngine != null)
            {
                Log("[ANALYSIS SETTINGS] Restarting Analysis Board engine for live settings change.");
                ScheduleAnalysisBoardEngineDisposal(oldEngine, "live settings change");
            }

            var settingsForm = _analysisBoardForm();
            if (settingsForm != null)
            {
                string depthText = _analysisBoardEngineInfinite ? "Infinite" : _analysisBoardEngineDepth.ToString();
                settingsForm.SetAnalysisStatus($"Engine: {_analysisBoardEngineName} | Depth {depthText} | T{_analysisBoardEngineThreads} H{_analysisBoardEngineHashMb}");
            }

            if (_analysisBoardAnalysisEnabled)
            {
                int sessionVersion = _analysisBoardAnalysisSessionVersion;
                _analysisBoardAnalysisTimer?.Dispose();
                _analysisBoardAnalysisTimer = new System.Threading.Timer(
                    _ => TryQueueAnalysisBoardAnalysis(sessionVersion),
                    null,
                    TimeSpan.FromMilliseconds(80),
                    TimeSpan.FromMilliseconds(_analysisBoardEngineInfinite ? 500 : 300));
                TryQueueAnalysisBoardAnalysis(sessionVersion);
            }
        }

        public void HandleAnalysisBoardMatchSettingsChanged(AnalysisBoardMatchSettings settings)
        {
            _analysisBoardMatchWhiteEnginePath = settings.WhiteEnginePath ?? "";
            _analysisBoardMatchWhiteEngineName = string.IsNullOrWhiteSpace(settings.WhiteEngineName) ? "White Engine" : settings.WhiteEngineName;
            _analysisBoardMatchBlackEnginePath = settings.BlackEnginePath ?? "";
            _analysisBoardMatchBlackEngineName = string.IsNullOrWhiteSpace(settings.BlackEngineName) ? "Black Engine" : settings.BlackEngineName;
            _analysisBoardMatchTimeControlKey = string.IsNullOrWhiteSpace(settings.TimeControlKey) ? "3 min" : settings.TimeControlKey;
            _analysisBoardMatchBaseSeconds = BuildLimits.ClampMatchSeconds(settings.BaseSeconds);
            _analysisBoardMatchGameLimit = BuildLimits.ClampMatchGameLimit(settings.GameLimit);
            _analysisBoardMatchThreads = BuildLimits.ClampThreads(settings.Threads);
            _analysisBoardMatchHashMb = BuildLimits.ClampHashMb(settings.HashMb);
            UpdateAnalysisBoardMatchDisplay();
        }

        public void HandleGameAnalysisRequested(GameAnalysisRequest request)
        {
            EnsureGameAnalysisForm();

            var gameForm = _getGameAnalysisForm();
            if (gameForm == null)
                return;

            int analysisDepth = _analysisBoardEngineInfinite && BuildLimits.AllowInfiniteAnalysis
                ? BuildLimits.ClampDepth(Math.Max(18, _analysisBoardEngineDepth))
                : BuildLimits.ClampDepth(_analysisBoardEngineDepth);
            bool sameOpenAnalysis =
                !gameForm.IsDisposed &&
                gameForm.Visible &&
                gameForm.HasSameRequest(request);

            if (!sameOpenAnalysis)
            {
                gameForm.LoadAnalysis(request, _analysisBoardEngineName, analysisDepth, _analysisBoardEngineThreads, _analysisBoardEngineHashMb);
            }

            gameForm.Show();
            gameForm.BringToFront();
            gameForm.Activate();
        }

        private void EnsureGameAnalysisForm()
        {
            var existing = _getGameAnalysisForm();
            if (existing != null && !existing.IsDisposed)
                return;

            var created = new GameAnalysisForm();
            created.AnalyzeRequested += AnalyzeGameAsync;
            created.AnalysisCompleted += HandleGameAnalysisCompleted;
            created.MoveSelected += HandleGameAnalysisMoveSelected;
            _setGameAnalysisForm(created);
        }

        public void HandleGameAnalysisCompleted(GameAnalysisWindowData data)
        {
            AnalysisBoardForm? form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            void apply() => form.ApplyGameAnalysisResults(data.MoveResults);

            if (form.InvokeRequired)
                form.BeginInvoke(new Action(apply));
            else
                apply();
        }

        public void HandleGameAnalysisMoveSelected(GameAnalysisMoveResult move)
        {
            AnalysisBoardForm? form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            void apply() => form.PreviewGameAnalysisMove(move);

            if (form.InvokeRequired)
                form.BeginInvoke(new Action(apply));
            else
                apply();
        }

        public async Task<GameAnalysisWindowData> AnalyzeGameAsync(GameAnalysisRequest request, int requestedDepth, IProgress<GameAnalysisProgress> progress)
        {
            if (!_ensureLicensedFeatureAvailable("game analysis", true))
                throw new InvalidOperationException("License verification is required before running game analysis.");

            string enginePath = _analysisBoardEnginePath;
            if (string.IsNullOrWhiteSpace(enginePath) || !_isUsableEnginePath(enginePath))
                throw new InvalidOperationException("Select a valid Analysis Board engine before running game analysis.");

            int depth = BuildLimits.ClampDepth(requestedDepth);
            int thinkTimeMs = depth <= 0 ? 35 : Math.Max(250, depth * 30);
            var movesToAnalyze = request.Moves.Take(BuildLimits.GameAnalysisPlyLimit).ToList();
            var (openingBoundaryIndex, endgameBoundaryIndex) = ComputePhaseBoundaries(movesToAnalyze);

            using var engine = new UCIEngine(enginePath)
            {
                InitialDepth = depth <= 0 ? 0 : Math.Min(depth, 10),
                MaxDepth = depth <= 0 ? 0 : Math.Max(depth, 10)
            };
            _applyEngineSpecificSettings(engine);

            if (!await engine.StartAsync())
            {
                string failureMessage = _getEngineFailureMessage(engine, $"Could not start {_analysisBoardEngineName}.");
                if (_isPrivateEngineStartupBlocked(enginePath, failureMessage))
                    _showPrivateEngineLicenseNotice(failureMessage);
                throw new InvalidOperationException(failureMessage);
            }

            await engine.SendCommandAsync($"setoption name Threads value {_analysisBoardEngineThreads}");
            await engine.SendCommandAsync($"setoption name Hash value {_analysisBoardEngineHashMb}");
            await engine.SendCommandAsync("setoption name MultiPV value 1");

            var fenEvaluations = new Dictionary<string, BestMoveResult>(StringComparer.Ordinal);
            var moveResults = new List<GameAnalysisMoveResult>();
            var bookPlyIndexes = BuildBookPlyIndexSet(movesToAnalyze);
            int totalMoves = Math.Max(1, movesToAnalyze.Count);
            for (int index = 0; index < movesToAnalyze.Count; index++)
            {
                GameAnalysisMoveInput move = movesToAnalyze[index];
                if (!fenEvaluations.ContainsKey(move.FenBefore))
                    fenEvaluations[move.FenBefore] = await AnalyzeFenForGameAsync(engine, move.FenBefore, thinkTimeMs, depth);
                if (!fenEvaluations.ContainsKey(move.FenAfter))
                    fenEvaluations[move.FenAfter] = await AnalyzeFenForGameAsync(engine, move.FenAfter, thinkTimeMs, depth);

                BestMoveResult beforeResult = fenEvaluations[move.FenBefore];
                BestMoveResult afterResult = fenEvaluations[move.FenAfter];

                var beforeScore = ExtractScore(beforeResult);
                var afterScore = ExtractScore(afterResult);
                double evalAfterForMover = -afterScore.Eval;
                double loss = Math.Max(0, beforeScore.Eval - evalAfterForMover);
                int lossCp = (int)Math.Round(loss * 100.0);
                string bestMoveSan = ResolveBestMoveSan(move.FenBefore, beforeResult.BestMove);

                string classification = bookPlyIndexes.Contains(move.PlyIndex)
                    ? "Book"
                    : ClassifyMove(lossCp, beforeScore.Eval, evalAfterForMover, move.MoveText, bestMoveSan);

                moveResults.Add(new GameAnalysisMoveResult
                {
                    PlyIndex = move.PlyIndex,
                    MoveNumber = move.MoveNumber,
                    IsWhiteMove = move.IsWhiteMove,
                    MoveText = move.MoveText,
                    FenBefore = move.FenBefore,
                    FenAfter = move.FenAfter,
                    BestMove = bestMoveSan,
                    EvalBefore = beforeScore.Eval,
                    EvalAfterForMover = evalAfterForMover,
                    Loss = lossCp,
                    Classification = classification,
                    Depth = beforeResult.AnalysisDepth,
                    IsMateScore = beforeScore.IsMate,
                    MateIn = beforeScore.MateIn
                });

                progress.Report(new GameAnalysisProgress
                {
                    MoveResults = moveResults.ToList(),
                    WhiteSummary = BuildGameAnalysisSummary(request.WhiteName, moveResults.Where(m => m.IsWhiteMove).ToList()),
                    BlackSummary = BuildGameAnalysisSummary(request.BlackName, moveResults.Where(m => !m.IsWhiteMove).ToList()),
                    StatusText = $"Analyzing move {index + 1}/{totalMoves} with {_analysisBoardEngineName}...",
                    ProgressPercent = (int)Math.Round(((index + 1) / (double)totalMoves) * 100.0),
                    OpeningBoundaryIndex = openingBoundaryIndex,
                    EndgameBoundaryIndex = endgameBoundaryIndex
                });
            }

            GameAnalysisSummary whiteSummary = BuildGameAnalysisSummary(request.WhiteName, moveResults.Where(m => m.IsWhiteMove).ToList());
            GameAnalysisSummary blackSummary = BuildGameAnalysisSummary(request.BlackName, moveResults.Where(m => !m.IsWhiteMove).ToList());
            string annotatedPgn = BuildLimits.AllowAnnotatedPgnExport ? BuildAnnotatedPgn(request, moveResults) : string.Empty;

            return new GameAnalysisWindowData
            {
                MoveResults = moveResults,
                WhiteSummary = whiteSummary,
                BlackSummary = blackSummary,
                AnnotatedPgn = annotatedPgn,
                StatusText = movesToAnalyze.Count < request.Moves.Count
                    ? $"Free Edition analysis complete: first {Math.Ceiling(movesToAnalyze.Count / 2.0).ToString(CultureInfo.InvariantCulture)} full moves with {_analysisBoardEngineName} at depth {depth.ToString(CultureInfo.InvariantCulture)}."
                    : $"Analysis complete with {_analysisBoardEngineName} at depth {depth.ToString(CultureInfo.InvariantCulture)}.",
                OpeningBoundaryIndex = openingBoundaryIndex,
                EndgameBoundaryIndex = endgameBoundaryIndex
            };
        }

        private async Task<BestMoveResult> AnalyzeFenForGameAsync(UCIEngine engine, string fen, int thinkTimeMs, int depth)
        {
            try
            {
                ChessBoard board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
                var legalMoves = board.Moves(false, true).ToList();
                if (legalMoves.Count == 0 || board.IsEndGame)
                {
                    return BuildSyntheticTerminalResult(board);
                }
            }
            catch
            {
                // Fall through to engine analysis if terminal probing fails.
            }

            BestMoveResult result = await engine.GetBestMoveAsync(fen, thinkTimeMs, depth);
            if (!result.Success)
                return new BestMoveResult
                {
                    Success = true,
                    BestMove = result.BestMove,
                    AnalysisDepth = depth,
                    Variations = result.Variations
                };

            return result;
        }

        private static BestMoveResult BuildSyntheticTerminalResult(ChessBoard board)
        {
            PieceColor sideToMove = board.Turn;
            var wonSide = board.EndGame?.WonSide;

            var variation = new MoveVariation
            {
                Rank = 1,
                Depth = 0,
                Score = 0,
                ScoreType = "cp",
                MateIn = null,
                Moves = new List<string>()
            };

            if (wonSide == PieceColor.White || wonSide == PieceColor.Black)
            {
                bool sideToMoveIsLosing = wonSide != sideToMove;
                variation.ScoreType = "mate";
                variation.MateIn = sideToMoveIsLosing ? -1 : 1;
                variation.Score = sideToMoveIsLosing ? -100.0 : 100.0;
            }

            return new BestMoveResult
            {
                Success = true,
                AnalysisDepth = 0,
                Variations = new List<MoveVariation> { variation }
            };
        }

        private static (double Eval, bool IsMate, int? MateIn) ExtractScore(BestMoveResult result)
        {
            MoveVariation? best = result.Variations.OrderBy(v => v.Rank).FirstOrDefault();
            if (best == null)
                return (0, false, null);

            if (string.Equals(best.ScoreType, "mate", StringComparison.OrdinalIgnoreCase))
            {
                int mateIn = best.MateIn ?? 0;
                double eval = mateIn >= 0 ? 100.0 : -100.0;
                return (eval, true, mateIn);
            }

            return (best.Score, false, null);
        }

        private static string ResolveBestMoveSan(string fenBefore, string? bestMove)
        {
            if (string.IsNullOrWhiteSpace(bestMove))
                return "-";

            try
            {
                ChessBoard board = ChessBoard.LoadFromFen(fenBefore, AutoEndgameRules.All);
                Move? move = board.Moves(false, true)
                    .FirstOrDefault(m => string.Equals(ToUciMove(m), bestMove, StringComparison.OrdinalIgnoreCase));
                if (move != null && !string.IsNullOrWhiteSpace(move.San))
                    return move.San;
            }
            catch
            {
            }

            return bestMove;
        }

        private static string ToUciMove(Move move)
        {
            string text = PositionToSquare(move.OriginalPosition) + PositionToSquare(move.NewPosition);
            if (move.IsPromotion && move.Promotion != null)
                text += char.ToLowerInvariant(move.Promotion.Type.AsChar);
            return text;
        }

        private static string PositionToSquare(Position position)
        {
            char file = (char)('a' + position.X);
            char rank = (char)('1' + position.Y);
            return $"{file}{rank}";
        }

        private static string ClassifyMove(int lossCp, double evalBeforeForMover, double evalAfterForMover, string playedMove, string bestMove)
        {
            if (lossCp < 10)
                return "Best";
            if (lossCp < 25)
                return "Good";
            if (lossCp < 50)
                return "Ok";

            if (IsMissedChance(lossCp, evalBeforeForMover, evalAfterForMover, playedMove, bestMove))
                return "Miss";

            if (lossCp < 90)
                return "Inaccuracy";
            if (lossCp < 180)
                return "Mistake";
            return "Blunder";
        }

        private static HashSet<int> BuildBookPlyIndexSet(List<GameAnalysisMoveInput> moves)
        {
            var bookPlyIndexes = new HashSet<int>();
            if (moves.Count == 0)
                return bookPlyIndexes;

            var played = new List<string>();
            foreach (GameAnalysisMoveInput move in moves)
            {
                string normalizedMove = NormalizeBookMoveToken(move.MoveText);
                if (string.IsNullOrWhiteSpace(normalizedMove))
                    break;

                played.Add(normalizedMove);
                if (!IsKnownOpeningPrefix(played))
                    break;

                bookPlyIndexes.Add(move.PlyIndex);
            }

            return bookPlyIndexes;
        }

        private static bool IsKnownOpeningPrefix(List<string> playedMoves)
        {
            if (playedMoves.Count == 0)
                return false;

            foreach (GameAnalysisOpeningLine line in GameAnalysisOpeningBook.Value)
            {
                if (line.Moves.Count < playedMoves.Count)
                    continue;

                bool matches = true;
                for (int i = 0; i < playedMoves.Count; i++)
                {
                    if (!string.Equals(line.Moves[i], playedMoves[i], StringComparison.OrdinalIgnoreCase))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return true;
            }

            return false;
        }

        private static readonly Lazy<List<GameAnalysisOpeningLine>> GameAnalysisOpeningBook = new(LoadGameAnalysisOpeningBook);

        private static List<GameAnalysisOpeningLine> LoadGameAnalysisOpeningBook()
        {
            Assembly assembly = typeof(AnalysisBoardController).Assembly;
            string[] resourceNames = assembly.GetManifestResourceNames()
                .Where(name => Regex.IsMatch(name, @"Assets\.lichess_openings\.[a-e]\.tsv$", RegexOptions.IgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var lines = new List<GameAnalysisOpeningLine>();
            foreach (string resourceName in resourceNames)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    continue;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                _ = reader.ReadLine();
                while (reader.ReadLine() is { } line)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3)
                        continue;

                    var openingMoves = Regex.Split(parts[2].Trim(), @"\s+")
                        .SelectMany(SplitJoinedBookMoveNumber)
                        .Select(NormalizeBookMoveToken)
                        .Where(move => !string.IsNullOrWhiteSpace(move))
                        .Where(move => !Regex.IsMatch(move, @"^\d+\.(\.\.)?$"))
                        .Where(move => !IsPgnHeaderOrResultForBook(move))
                        .ToList();

                    if (openingMoves.Count > 0)
                        lines.Add(new GameAnalysisOpeningLine(openingMoves));
                }
            }

            return lines;
        }

        private static IEnumerable<string> SplitJoinedBookMoveNumber(string token)
        {
            token = token.Trim();
            if (string.IsNullOrWhiteSpace(token))
                yield break;

            Match match = Regex.Match(token, @"^\d+\.(\.\.)?(.+)$");
            if (match.Success)
            {
                string move = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(move))
                    yield return move;
                yield break;
            }

            yield return token;
        }

        private static string NormalizeBookMoveToken(string move)
        {
            if (string.IsNullOrWhiteSpace(move))
                return string.Empty;

            string normalized = move.Trim();
            normalized = Regex.Replace(normalized, @"^\d+\.(\.\.)?", "");
            normalized = normalized.Replace("0-0-0", "O-O-O", StringComparison.OrdinalIgnoreCase)
                                   .Replace("0-0", "O-O", StringComparison.OrdinalIgnoreCase)
                                   .Replace("+", "", StringComparison.Ordinal)
                                   .Replace("#", "", StringComparison.Ordinal)
                                   .Replace("!", "", StringComparison.Ordinal)
                                   .Replace("?", "", StringComparison.Ordinal);
            return normalized.Trim();
        }

        private static bool IsPgnHeaderOrResultForBook(string token)
        {
            return token.StartsWith("[", StringComparison.Ordinal) ||
                   token is "1-0" or "0-1" or "1/2-1/2" or "*";
        }

        private sealed record GameAnalysisOpeningLine(List<string> Moves);

        private static bool IsMissedChance(int lossCp, double evalBeforeForMover, double evalAfterForMover, string playedMove, string bestMove)
        {
            if (lossCp < 80)
                return false;

            if (evalBeforeForMover < 1.5)
                return false;

            if (evalAfterForMover <= -1.0)
                return false;

            if (string.IsNullOrWhiteSpace(bestMove) || bestMove == "-")
                return false;

            string played = NormalizeMoveForMissComparison(playedMove);
            string best = NormalizeMoveForMissComparison(bestMove);
            return !string.Equals(played, best, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeMoveForMissComparison(string move)
        {
            if (string.IsNullOrWhiteSpace(move))
                return string.Empty;

            string normalized = move.Trim();
            normalized = normalized.Replace("+", "", StringComparison.Ordinal)
                                   .Replace("#", "", StringComparison.Ordinal)
                                   .Replace("!", "", StringComparison.Ordinal)
                                   .Replace("?", "", StringComparison.Ordinal);
            return normalized;
        }

        private static GameAnalysisSummary BuildGameAnalysisSummary(string sideName, List<GameAnalysisMoveResult> moves)
        {
            if (moves.Count == 0)
            {
                return new GameAnalysisSummary
                {
                    SideName = sideName,
                    Accuracy = 100
                };
            }

            List<GameAnalysisMoveResult> scoredMoves = moves
                .Where(m => !string.Equals(m.Classification, "Book", StringComparison.Ordinal))
                .ToList();
            double averageLoss = scoredMoves.Count == 0
                ? 0
                : scoredMoves.Average(m => Math.Max(0, m.Loss));
            double accuracy = scoredMoves.Count == 0
                ? 100
                : scoredMoves.Average(m => 100.0 * Math.Exp(-Math.Max(0, m.Loss) / 145.0));

            return new GameAnalysisSummary
            {
                SideName = sideName,
                BookMoves = moves.Count(m => string.Equals(m.Classification, "Book", StringComparison.Ordinal)),
                BestMoves = moves.Count(m => string.Equals(m.Classification, "Best", StringComparison.Ordinal)),
                GoodMoves = moves.Count(m => string.Equals(m.Classification, "Good", StringComparison.Ordinal)),
                OkMoves = moves.Count(m => string.Equals(m.Classification, "Ok", StringComparison.Ordinal)),
                Misses = moves.Count(m => string.Equals(m.Classification, "Miss", StringComparison.Ordinal)),
                Inaccuracies = moves.Count(m => string.Equals(m.Classification, "Inaccuracy", StringComparison.Ordinal)),
                Mistakes = moves.Count(m => string.Equals(m.Classification, "Mistake", StringComparison.Ordinal)),
                Blunders = moves.Count(m => string.Equals(m.Classification, "Blunder", StringComparison.Ordinal)),
                AverageCentipawnLoss = averageLoss,
                Accuracy = accuracy
            };
        }

        private static string BuildAnnotatedPgn(GameAnalysisRequest request, List<GameAnalysisMoveResult> results)
        {
            string normalizedResult = NormalizePgnResultForAnalysis(request.Result);
            var builder = new StringBuilder();
            builder.AppendLine("[Event \"Chess Kit Game Analysis\"]");
            builder.AppendLine("[Site \"Chess Kit Analysis Board\"]");
            builder.AppendLine($"[Date \"{DateTime.UtcNow:yyyy.MM.dd}\"]");
            builder.AppendLine($"[White \"{EscapePgnHeaderForAnalysis(request.WhiteName)}\"]");
            builder.AppendLine($"[Black \"{EscapePgnHeaderForAnalysis(request.BlackName)}\"]");
            builder.AppendLine($"[Result \"{normalizedResult}\"]");
            builder.AppendLine($"[TimeControl \"{EscapePgnHeaderForAnalysis(request.TimeControlKey)}\"]");
            builder.AppendLine();

            for (int i = 0; i < results.Count; i += 2)
            {
                GameAnalysisMoveResult white = results[i];
                builder.Append($"{white.MoveNumber.ToString(CultureInfo.InvariantCulture)}.{white.MoveText} {BuildMoveComment(white)}");
                if (i + 1 < results.Count)
                {
                    GameAnalysisMoveResult black = results[i + 1];
                    builder.Append($" {black.MoveText} {BuildMoveComment(black)}");
                }
                builder.Append(' ');
            }

            builder.Append(normalizedResult);
            return builder.ToString().Trim();
        }

        private static string BuildMoveComment(GameAnalysisMoveResult move)
        {
            string evalText = move.IsMateScore && move.MateIn.HasValue
                ? $"M{Math.Abs(move.MateIn.Value).ToString(CultureInfo.InvariantCulture)}"
                : move.EvalBefore.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
            return $"{{[%eval {evalText}] [%best {move.BestMove}] [%loss {Math.Max(0, move.Loss).ToString(CultureInfo.InvariantCulture)}] [%class {move.Classification}]}}";
        }

        private static string NormalizePgnResultForAnalysis(string result)
        {
            string normalized = (result ?? string.Empty).Trim();
            if (normalized.Contains("1-0", StringComparison.Ordinal))
                return "1-0";
            if (normalized.Contains("0-1", StringComparison.Ordinal))
                return "0-1";
            if (normalized.Contains("---", StringComparison.Ordinal) || normalized.Contains("1/2-1/2", StringComparison.Ordinal))
                return "1/2-1/2";
            return "*";
        }

        private static string EscapePgnHeaderForAnalysis(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static (int OpeningBoundaryIndex, int EndgameBoundaryIndex) ComputePhaseBoundaries(List<GameAnalysisMoveInput> moves)
        {
            int openingBoundaryIndex = Math.Min(Math.Max(10, moves.Count / 3), Math.Max(1, moves.Count - 1));
            int endgameBoundaryIndex = -1;

            for (int i = 0; i < moves.Count; i++)
            {
                if (IsEndgameFen(moves[i].FenAfter))
                {
                    endgameBoundaryIndex = i + 1;
                    break;
                }
            }

            if (endgameBoundaryIndex > 0 && endgameBoundaryIndex <= openingBoundaryIndex + 2)
                endgameBoundaryIndex = -1;

            return (openingBoundaryIndex, endgameBoundaryIndex);
        }

        private static bool IsEndgameFen(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return false;

            string board = fen.Split(' ')[0];
            int totalPieces = 0;
            int queens = 0;
            int rooks = 0;
            int bishops = 0;
            int knights = 0;

            foreach (char c in board)
            {
                if (!char.IsLetter(c))
                    continue;

                totalPieces++;
                switch (char.ToLowerInvariant(c))
                {
                    case 'q': queens++; break;
                    case 'r': rooks++; break;
                    case 'b': bishops++; break;
                    case 'n': knights++; break;
                }
            }

            if (totalPieces <= 8)
                return true;
            if (queens == 0 && rooks == 0 && bishops + knights <= 4 && totalPieces <= 12)
                return true;
            if (queens == 0 && rooks <= 2 && bishops + knights <= 2 && totalPieces <= 10)
                return true;
            if (queens <= 1 && rooks == 0 && bishops + knights <= 1 && totalPieces <= 10)
                return true;

            return false;
        }

        public void HandleAnalysisBoardMatchCommandRequested(AnalysisBoardMatchCommandType command)
        {
            switch (command)
            {
                case AnalysisBoardMatchCommandType.ToggleRunning:
                    if (_analysisBoardMatchRunning || _analysisBoardMatchStarting)
                        StopAnalysisBoardMatch("Match stopped.");
                    else
                        _ = StartAnalysisBoardMatchAsync();
                    break;
                case AnalysisBoardMatchCommandType.StopRunning:
                    if (_analysisBoardMatchRunning ||
                        _analysisBoardMatchStarting ||
                        _analysisBoardMatchWhiteEngine != null ||
                        _analysisBoardMatchBlackEngine != null)
                    {
                        StopAnalysisBoardMatch("Match stopped.");
                    }
                    break;
                case AnalysisBoardMatchCommandType.PauseResume:
                    ToggleAnalysisBoardMatchPause();
                    break;
                case AnalysisBoardMatchCommandType.RestartGame:
                    RestartAnalysisBoardMatchGame(resetScore: false);
                    break;
                case AnalysisBoardMatchCommandType.RestartMatch:
                    _analysisBoardMatchWhiteWins = 0;
                    _analysisBoardMatchBlackWins = 0;
                    _analysisBoardMatchDraws = 0;
                    RestartAnalysisBoardMatchGame(resetScore: true);
                    break;
                case AnalysisBoardMatchCommandType.ResetScore:
                    _analysisBoardMatchSessionVersion++;
                    _analysisBoardMatchStarting = false;
                    _analysisBoardMatchMoveInProgress = false;
                    _analysisBoardMatchPaused = false;
                    _analysisBoardMatchWhiteWins = 0;
                    _analysisBoardMatchBlackWins = 0;
                    _analysisBoardMatchDraws = 0;
                    _analysisBoardMatchCompetitorAPath = _analysisBoardMatchWhiteEnginePath;
                    _analysisBoardMatchCompetitorAName = string.IsNullOrWhiteSpace(_analysisBoardMatchWhiteEngineName) ? "Engine A" : _analysisBoardMatchWhiteEngineName;
                    _analysisBoardMatchCompetitorBPath = _analysisBoardMatchBlackEnginePath;
                    _analysisBoardMatchCompetitorBName = string.IsNullOrWhiteSpace(_analysisBoardMatchBlackEngineName) ? "Engine B" : _analysisBoardMatchBlackEngineName;
                    if (!_analysisBoardMatchRunning)
                    {
                        _analysisBoardMatchWhiteRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
                        _analysisBoardMatchBlackRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
                        _analysisBoardMatchTurnStartedUtc = DateTime.MinValue;
                    }
                    _analysisBoardMatchStatus = _analysisBoardMatchRunning ? "Match running." : "Ready for engine match.";
                    AnalysisBoardForm? matchResetForm = _analysisBoardForm();
                    if (matchResetForm != null)
                    {
                        long displayVersion = Interlocked.Increment(ref _analysisBoardMatchDisplayVersion);
                        string whiteClock = FormatMatchClock(_analysisBoardMatchWhiteRemainingMs);
                        string blackClock = FormatMatchClock(_analysisBoardMatchBlackRemainingMs);
                        void ApplyReset()
                        {
                            matchResetForm.ResetMatchScoreDisplay(
                                displayVersion,
                                _analysisBoardMatchRunning,
                                _analysisBoardMatchPaused,
                                whiteClock,
                                blackClock,
                                _analysisBoardMatchCompetitorAName,
                                _analysisBoardMatchCompetitorBName,
                                _analysisBoardMatchStatus);
                        }

                        if (matchResetForm.InvokeRequired)
                            matchResetForm.BeginInvoke(new Action(ApplyReset));
                        else
                            ApplyReset();
                    }
                    else
                    {
                        UpdateAnalysisBoardMatchDisplay(_analysisBoardMatchStatus);
                    }
                    break;
            }
        }

        private async Task StartAnalysisBoardMatchAsync()
        {
            var startForm = _analysisBoardForm();
            if (startForm == null || _analysisBoardMatchStarting)
                return;

            DisposeAnalysisBoardMatchEngines("new match start");
            startForm.ClearMatchPgnArchive();
            _analysisBoardMatchWhiteWins = 0;
            _analysisBoardMatchBlackWins = 0;
            _analysisBoardMatchDraws = 0;
            _analysisBoardMatchCompetitorAPath = _analysisBoardMatchWhiteEnginePath;
            _analysisBoardMatchCompetitorAName = string.IsNullOrWhiteSpace(_analysisBoardMatchWhiteEngineName) ? "Engine A" : _analysisBoardMatchWhiteEngineName;
            _analysisBoardMatchCompetitorBPath = _analysisBoardMatchBlackEnginePath;
            _analysisBoardMatchCompetitorBName = string.IsNullOrWhiteSpace(_analysisBoardMatchBlackEngineName) ? "Engine B" : _analysisBoardMatchBlackEngineName;
            startForm.ResetBoardToInitialPosition();
            _analysisBoardMatchWhiteRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchBlackRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchTurnStartedUtc = DateTime.MinValue;
            _analysisBoardMatchTurnColor = 'w';
            _analysisBoardMatchRunning = true;
            _analysisBoardMatchPaused = false;
            _analysisBoardMatchStarting = true;
            _analysisBoardMatchMoveInProgress = true;
            _analysisBoardMatchSessionVersion++;

            _analysisBoardMatchTimer?.Dispose();
            _analysisBoardMatchTimer = null;
            int sessionVersion = _analysisBoardMatchSessionVersion;
            UpdateAnalysisBoardMatchDisplay("Starting engines...");

            try
            {
                var whiteEngine = await EnsureAnalysisBoardMatchEngineAsync(true);
                if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || sessionVersion != _analysisBoardMatchSessionVersion)
                    return;

                if (whiteEngine == null)
                {
                    string failedName = string.IsNullOrWhiteSpace(_analysisBoardMatchWhiteEngineName) ? "White engine" : _analysisBoardMatchWhiteEngineName;
                    StopAnalysisBoardMatch($"Could not start {failedName}.");
                    return;
                }

                var blackEngine = await EnsureAnalysisBoardMatchEngineAsync(false);
                if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || sessionVersion != _analysisBoardMatchSessionVersion)
                    return;

                if (blackEngine == null)
                {
                    string failedName = string.IsNullOrWhiteSpace(_analysisBoardMatchBlackEngineName) ? "Black engine" : _analysisBoardMatchBlackEngineName;
                    StopAnalysisBoardMatch($"Could not start {failedName}.");
                    return;
                }

                _analysisBoardMatchStarting = false;
                _analysisBoardMatchMoveInProgress = false;
                _analysisBoardMatchTurnStartedUtc = DateTime.UtcNow;
                _analysisBoardMatchTimer = new System.Threading.Timer(_ => TickAnalysisBoardMatch(sessionVersion), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
                UpdateAnalysisBoardMatchDisplay("Ready.");
            }
            catch (Exception ex)
            {
                Log($"[MATCH] Start failed: {ex}");
                StopAnalysisBoardMatch("Could not start match.");
            }
        }

        private void StopAnalysisBoardMatch(string status)
        {
            Log($"[MATCH] Stop requested: {status}");
            _analysisBoardMatchRunning = false;
            _analysisBoardMatchPaused = false;
            _analysisBoardMatchStarting = false;
            _analysisBoardMatchMoveInProgress = false;
            _analysisBoardMatchSessionVersion++;
            _analysisBoardMatchTimer?.Dispose();
            _analysisBoardMatchTimer = null;
            DisposeAnalysisBoardMatchEngines(status);
            UpdateAnalysisBoardMatchDisplay(status);
        }

        private void DisposeAnalysisBoardMatchEngines(string reason)
        {
            var whiteEngine = _analysisBoardMatchWhiteEngine;
            var blackEngine = _analysisBoardMatchBlackEngine;
            _analysisBoardMatchWhiteEngine = null;
            _analysisBoardMatchBlackEngine = null;

            if (whiteEngine == null && blackEngine == null)
                return;

            Log($"[MATCH] Disposing match engines ({reason}).");
            _ = Task.Run(async () =>
            {
                await DisposeAnalysisBoardMatchEngineAsync(whiteEngine, "white", reason);
                if (!ReferenceEquals(blackEngine, whiteEngine))
                    await DisposeAnalysisBoardMatchEngineAsync(blackEngine, "black", reason);
            });
        }

        private async Task DisposeAnalysisBoardMatchEngineAsync(UCIEngine? engine, string label, string reason)
        {
            if (engine == null)
                return;

            try { await engine.AbortCurrentAnalysisAsync(); } catch { }
            try { await engine.StopInfiniteAnalysis(); } catch { }
            try
            {
                engine.Dispose();
                Log($"[MATCH] Disposed {label} match engine ({reason}).");
            }
            catch (Exception ex)
            {
                Log($"[MATCH] Failed to dispose {label} match engine ({reason}): {ex.Message}");
            }
        }

        private void ToggleAnalysisBoardMatchPause()
        {
            if (!_analysisBoardMatchRunning || _analysisBoardMatchStarting)
                return;

            if (_analysisBoardMatchPaused)
            {
                _analysisBoardMatchPaused = false;
                _analysisBoardMatchMoveInProgress = false;
                _analysisBoardMatchTurnStartedUtc = DateTime.UtcNow;
                _analysisBoardMatchSessionVersion++;
                int sessionVersion = _analysisBoardMatchSessionVersion;
                _analysisBoardMatchTimer?.Dispose();
                _analysisBoardMatchTimer = new System.Threading.Timer(_ => TickAnalysisBoardMatch(sessionVersion), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
                UpdateAnalysisBoardMatchDisplay("Match resumed.");
                return;
            }

            CommitAnalysisBoardMatchClock(_analysisBoardMatchTurnColor);
            _analysisBoardMatchPaused = true;
            _analysisBoardMatchMoveInProgress = false;
            _analysisBoardMatchTurnStartedUtc = DateTime.MinValue;
            _analysisBoardMatchSessionVersion++;
            _analysisBoardMatchTimer?.Dispose();
            _analysisBoardMatchTimer = null;
            UpdateAnalysisBoardMatchDisplay("Match paused.");
        }

        private void StopAnalysisBoardMatchWithAlert(string status, string message)
        {
            StopAnalysisBoardMatch(status);

            var form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            void ShowAlert()
            {
                try
                {
                    MessageBox.Show(form, message, "Engine Match Stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
            }

            if (form.InvokeRequired)
                form.BeginInvoke(new Action(ShowAlert));
            else
                ShowAlert();
        }

        private void SwapAnalysisBoardMatchSides()
        {
            (_analysisBoardMatchWhiteEngine, _analysisBoardMatchBlackEngine) = (_analysisBoardMatchBlackEngine, _analysisBoardMatchWhiteEngine);
            (_analysisBoardMatchWhiteEnginePath, _analysisBoardMatchBlackEnginePath) = (_analysisBoardMatchBlackEnginePath, _analysisBoardMatchWhiteEnginePath);
            (_analysisBoardMatchWhiteEngineName, _analysisBoardMatchBlackEngineName) = (_analysisBoardMatchBlackEngineName, _analysisBoardMatchWhiteEngineName);

            var form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            void ApplySwap()
            {
                form.SwapMatchEngineSelections();
            }

            if (form.InvokeRequired)
                form.Invoke(new Action(ApplySwap));
            else
                ApplySwap();
        }

        private async Task AdvanceAnalysisBoardMatchToNextGameAsync(string status, string? moveSuffix = null)
        {
            var form = _analysisBoardForm();
            if (form == null)
            {
                StopAnalysisBoardMatch(status);
                return;
            }

            _analysisBoardMatchMoveInProgress = false;
            _analysisBoardMatchSessionVersion++;
            _analysisBoardMatchTimer?.Dispose();
            _analysisBoardMatchTimer = null;

            void ApplyResultUi()
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(moveSuffix))
                        form.AppendResultToLatestMove(moveSuffix);
                    form.ArchiveCurrentMatchGame(
                        _analysisBoardMatchWhiteEngineName,
                        _analysisBoardMatchBlackEngineName,
                        moveSuffix ?? status,
                        _analysisBoardMatchTimeControlKey);
                    form.ClearAnalysisArrows();
                    form.ClearAnalysisVariations();
                }
                catch (Exception ex)
                {
                    Log($"[MATCH] ApplyResultUi failed: {ex}");
                }
            }

            if (form.InvokeRequired)
                form.Invoke(new Action(ApplyResultUi));
            else
                ApplyResultUi();

            Log($"[MATCH] Game finished: {status} | next game in 1200ms");
            UpdateAnalysisBoardMatchDisplay(status);

            int completedGames = _analysisBoardMatchWhiteWins + _analysisBoardMatchBlackWins + _analysisBoardMatchDraws;
            if (_analysisBoardMatchGameLimit > 0 && completedGames >= _analysisBoardMatchGameLimit)
            {
                StopAnalysisBoardMatch($"Match complete: {completedGames.ToString(CultureInfo.InvariantCulture)}/{_analysisBoardMatchGameLimit.ToString(CultureInfo.InvariantCulture)} games.");
                return;
            }

            await Task.Delay(1200);

            if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || _analysisBoardForm() != form || form.IsDisposed)
            {
                Log($"[MATCH] Next-game handoff aborted | running={_analysisBoardMatchRunning} paused={_analysisBoardMatchPaused} sameForm={_analysisBoardForm() == form} disposed={form.IsDisposed}");
                return;
            }

            SwapAnalysisBoardMatchSides();

            void ResetForNextGame()
            {
                form.ResetBoardToInitialPosition();
            }

            if (form.InvokeRequired)
                form.Invoke(new Action(ResetForNextGame));
            else
                ResetForNextGame();

            _analysisBoardMatchWhiteRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchBlackRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchTurnStartedUtc = DateTime.UtcNow;
            _analysisBoardMatchTurnColor = 'w';
            _analysisBoardMatchPaused = false;
            _analysisBoardMatchMoveInProgress = false;
            _analysisBoardMatchSessionVersion++;

            int sessionVersion = _analysisBoardMatchSessionVersion;
            _analysisBoardMatchTimer = new System.Threading.Timer(_ => TickAnalysisBoardMatch(sessionVersion), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            Log($"[MATCH] Next game started | session={sessionVersion} baseSeconds={_analysisBoardMatchBaseSeconds}");
            UpdateAnalysisBoardMatchDisplay("Match running.");
        }

        private void RestartAnalysisBoardMatchGame(bool resetScore)
        {
            var form = _analysisBoardForm();
            if (form == null)
                return;

            if (resetScore)
            {
                _analysisBoardMatchWhiteWins = 0;
                _analysisBoardMatchBlackWins = 0;
                _analysisBoardMatchDraws = 0;
                _analysisBoardMatchCompetitorAPath = _analysisBoardMatchWhiteEnginePath;
                _analysisBoardMatchCompetitorAName = string.IsNullOrWhiteSpace(_analysisBoardMatchWhiteEngineName) ? "Engine A" : _analysisBoardMatchWhiteEngineName;
                _analysisBoardMatchCompetitorBPath = _analysisBoardMatchBlackEnginePath;
                _analysisBoardMatchCompetitorBName = string.IsNullOrWhiteSpace(_analysisBoardMatchBlackEngineName) ? "Engine B" : _analysisBoardMatchBlackEngineName;
                form.ClearMatchPgnArchive();
            }

            form.ResetBoardToInitialPosition();
            _analysisBoardMatchWhiteRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchBlackRemainingMs = _analysisBoardMatchBaseSeconds * 1000L;
            _analysisBoardMatchTurnStartedUtc = DateTime.UtcNow;
            _analysisBoardMatchTurnColor = 'w';
            bool wasRunning = _analysisBoardMatchRunning;
            _analysisBoardMatchPaused = false;
            _analysisBoardMatchStarting = false;
            _analysisBoardMatchMoveInProgress = false;
            _analysisBoardMatchSessionVersion++;

            _analysisBoardMatchTimer?.Dispose();
            _analysisBoardMatchTimer = null;
            if (wasRunning)
            {
                int sessionVersion = _analysisBoardMatchSessionVersion;
                _analysisBoardMatchTimer = new System.Threading.Timer(_ => TickAnalysisBoardMatch(sessionVersion), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            }

            UpdateAnalysisBoardMatchDisplay(wasRunning ? "Ready." : (resetScore ? "Match restarted." : "Game restarted."));
        }

        private void TickAnalysisBoardMatch(int sessionVersion)
        {
            if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || sessionVersion != _analysisBoardMatchSessionVersion)
                return;

            long whiteMs = _analysisBoardMatchWhiteRemainingMs;
            long blackMs = _analysisBoardMatchBlackRemainingMs;
            if (_analysisBoardMatchTurnStartedUtc != DateTime.MinValue)
            {
                long elapsed = (long)(DateTime.UtcNow - _analysisBoardMatchTurnStartedUtc).TotalMilliseconds;
                if (_analysisBoardMatchTurnColor == 'w')
                    whiteMs = Math.Max(0, whiteMs - elapsed);
                else
                    blackMs = Math.Max(0, blackMs - elapsed);
            }

            if (whiteMs <= 0)
            {
                _analysisBoardMatchBlackWins++;
                _ = AdvanceAnalysisBoardMatchToNextGameAsync("White flagged. Black wins.", "0-1");
                return;
            }

            if (blackMs <= 0)
            {
                _analysisBoardMatchWhiteWins++;
                _ = AdvanceAnalysisBoardMatchToNextGameAsync("Black flagged. White wins.", "1-0");
                return;
            }

            UpdateAnalysisBoardMatchDisplay();
            if (_analysisBoardMatchMoveInProgress)
                return;

            if (!_tryGetStoredAnalysisBoardSnapshot(out var snapshot) || string.IsNullOrWhiteSpace(snapshot.Fen))
                return;

            char sideToMove = _getSideToMove(snapshot.Fen) ?? 'w';
            _analysisBoardMatchTurnColor = sideToMove;
            _analysisBoardMatchMoveInProgress = true;
            _ = PlayAnalysisBoardMatchMoveAsync(snapshot.Fen, sideToMove, sessionVersion);
        }

        private async Task PlayAnalysisBoardMatchMoveAsync(string fen, char sideToMove, int sessionVersion)
        {
            try
            {
                var engine = await EnsureAnalysisBoardMatchEngineAsync(sideToMove == 'w');
                if (engine == null)
                {
                    if (_analysisBoardMatchRunning && sessionVersion == _analysisBoardMatchSessionVersion)
                    {
                        string failedName = sideToMove == 'w' ? _analysisBoardMatchWhiteEngineName : _analysisBoardMatchBlackEngineName;
                        if (string.IsNullOrWhiteSpace(failedName))
                            failedName = sideToMove == 'w' ? "White engine" : "Black engine";
                        StopAnalysisBoardMatch($"Could not start {failedName}.");
                    }
                    return;
                }

                if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || sessionVersion != _analysisBoardMatchSessionVersion)
                    return;

                long remainingMs = sideToMove == 'w' ? _analysisBoardMatchWhiteRemainingMs : _analysisBoardMatchBlackRemainingMs;
                bool isLc0MatchEngine = IsLc0EnginePath(sideToMove == 'w' ? _analysisBoardMatchWhiteEnginePath : _analysisBoardMatchBlackEnginePath);
                int gamePly = GetFenGamePly(fen);
                int pieceCount = CountFenPieces(fen);
                int thinkTimeMs = CalculateAnalysisBoardMatchThinkTime(remainingMs, isLc0MatchEngine, gamePly, pieceCount);
                int depthTarget = _analysisBoardMatchBaseSeconds switch
                {
                    <= 15 => 1,
                    <= 30 => 4,
                    <= 60 => 10,
                    <= 180 => 0,
                    <= 300 => 0,
                    <= 600 => 0,
                    _ => 0
                };
                if (_analysisBoardMatchBaseSeconds >= 180)
                {
                    depthTarget = 0;
                }
                if (isLc0MatchEngine)
                {
                    depthTarget = _analysisBoardMatchBaseSeconds <= 30
                        ? Math.Max(1, depthTarget - 1)
                        : 0;
                }
                var result = await engine.GetBestMoveAsync(fen, thinkTimeMs, depthTarget);

                if (!_analysisBoardMatchRunning || _analysisBoardMatchPaused || sessionVersion != _analysisBoardMatchSessionVersion || _analysisBoardForm() == null)
                    return;

                string? move = result.Variations.FirstOrDefault()?.Moves.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(move))
                {
                    string? crashDescription = engine.GetRecentCrashDescription();
                    string? failureSummary = engine.GetRecentFailureSummary();
                    if (!string.IsNullOrWhiteSpace(crashDescription))
                    {
                        string failedName = sideToMove == 'w' ? _analysisBoardMatchWhiteEngineName : _analysisBoardMatchBlackEngineName;
                        if (string.IsNullOrWhiteSpace(failedName))
                            failedName = sideToMove == 'w' ? "White engine" : "Black engine";

                        string status = $"{failedName} crashed.";
                        Log($"[MATCH] {status} {crashDescription}");
                        string detail = !string.IsNullOrWhiteSpace(failureSummary) ? failureSummary : crashDescription;
                        StopAnalysisBoardMatchWithAlert(
                            status,
                            $"{failedName} crashed and could not continue the match.\r\n\r\n{detail}");
                        return;
                    }

                    Log($"[MATCH] No move available from engine; will retry | side={sideToMove} session={sessionVersion} fen={fen}");
                    return;
                }

                await ShowAnalysisBoardMatchMovePreviewAsync(fen, sideToMove, result, sessionVersion);

                var previewForm = _analysisBoardForm();
                bool applied = false;
                if (previewForm != null && previewForm.InvokeRequired)
                {
                    previewForm.Invoke(new Action(() =>
                    {
                        applied = previewForm.TryApplyEngineMove(move, out _);
                    }));
                }
                else if (previewForm != null)
                {
                    applied = previewForm.TryApplyEngineMove(move, out _);
                }

                if (!applied)
                {
                    Log($"[MATCH] Could not apply engine move; will retry | move={move} side={sideToMove} session={sessionVersion}");
                    return;
                }

                if (_tryGetStoredAnalysisBoardSnapshot(out var afterMoveSnapshot) && !string.IsNullOrWhiteSpace(afterMoveSnapshot.Fen))
                {
                    _ = ShowAnalysisBoardMatchPositionPreviewAsync(afterMoveSnapshot.Fen, sessionVersion);
                    await FinishAnalysisBoardMatchMoveAsync(afterMoveSnapshot.Fen, sessionVersion);
                }
            }
            catch (Exception ex)
            {
                Log($"[MATCH] Engine-vs-engine move failed: {ex.Message}");
                StopAnalysisBoardMatch("Match error.");
            }
            finally
            {
                if (sessionVersion == _analysisBoardMatchSessionVersion)
                {
                    CommitAnalysisBoardMatchClock(sideToMove);
                    _analysisBoardMatchMoveInProgress = false;
                    UpdateAnalysisBoardMatchDisplay();
                }
            }
        }

        private void CommitAnalysisBoardMatchClock(char sideToMove)
        {
            DateTime nowUtc = DateTime.UtcNow;
            if (_analysisBoardMatchTurnStartedUtc != DateTime.MinValue)
            {
                long elapsedMs = Math.Max(0, (long)(nowUtc - _analysisBoardMatchTurnStartedUtc).TotalMilliseconds);
                if (sideToMove == 'w')
                    _analysisBoardMatchWhiteRemainingMs = Math.Max(0, _analysisBoardMatchWhiteRemainingMs - elapsedMs);
                else
                    _analysisBoardMatchBlackRemainingMs = Math.Max(0, _analysisBoardMatchBlackRemainingMs - elapsedMs);
            }

            _analysisBoardMatchTurnStartedUtc = nowUtc;
        }

        private async Task ShowAnalysisBoardMatchMovePreviewAsync(string fen, char sideToMove, BestMoveResult result, int sessionVersion)
        {
            if (!_analysisBoardAnalysisEnabled ||
                string.Equals(_analysisBoardAnalysisMode, "OFF", StringComparison.OrdinalIgnoreCase) ||
                !_analysisBoardMatchRunning ||
                _analysisBoardMatchPaused ||
                sessionVersion != _analysisBoardMatchSessionVersion ||
                _analysisBoardForm() == null ||
                !(result.Success && result.Variations.Any()))
            {
                return;
            }

            if (_analysisBoardAnalysisMode == "WHITE" && sideToMove != 'w')
                return;
            if (_analysisBoardAnalysisMode == "BLACK" && sideToMove != 'b')
                return;

            bool boardFlipped = false;
            if (_tryGetActiveAnalysisBoardSnapshot(out var snapshot) &&
                string.Equals(snapshot.Fen, fen, StringComparison.Ordinal))
            {
                boardFlipped = snapshot.BoardFlipped;
            }

            var limitedVariations = result.Variations.Take(BuildLimits.ClampLines(_analysisBoardLineCount)).ToList();
            var arrows = _buildArrowsForFen(fen, limitedVariations, sideToMove, boardFlipped, _analysisBoardLineCount);
            if (arrows.Count == 0)
                return;

            string analysisKey = $"{_analysisBoardAnalysisMode}|{sideToMove}|{boardFlipped}|{fen}";
            _analysisBoardLastAnalysisKey = analysisKey;

            void ApplyPreview()
            {
                var applyForm = _analysisBoardForm();
                if (applyForm == null || applyForm.IsDisposed)
                    return;

                applyForm.SetAnalysisVariations(limitedVariations, sideToMove == 'b', result.AnalysisDepth);
                applyForm.SetAnalysisArrows(arrows);
            }

            var previewForm = _analysisBoardForm();
            if (previewForm != null && previewForm.InvokeRequired)
                previewForm.Invoke(new Action(ApplyPreview));
            else
                ApplyPreview();

            int holdMs = _analysisBoardMatchBaseSeconds switch
            {
                <= 15 => 35,
                <= 30 => 75,
                <= 60 => 110,
                <= 180 => 150,
                _ => 180
            };

            await Task.Delay(holdMs);
        }

        private async Task ShowAnalysisBoardMatchPositionPreviewAsync(string fen, int sessionVersion)
        {
            int previewVersion = Interlocked.Increment(ref _analysisBoardMatchAnalysisPreviewVersion);

            try
            {
                if (!_analysisBoardAnalysisEnabled ||
                    string.Equals(_analysisBoardAnalysisMode, "OFF", StringComparison.OrdinalIgnoreCase) ||
                    !_analysisBoardMatchRunning ||
                    _analysisBoardMatchPaused ||
                    sessionVersion != _analysisBoardMatchSessionVersion ||
                    _analysisBoardForm() == null ||
                    string.IsNullOrWhiteSpace(fen))
                {
                    return;
                }

                char sideToMove = _getSideToMove(fen) ?? 'w';
                if (_analysisBoardAnalysisMode == "WHITE" && sideToMove != 'w')
                    return;
                if (_analysisBoardAnalysisMode == "BLACK" && sideToMove != 'b')
                    return;

                bool boardFlipped = false;
                if (_tryGetActiveAnalysisBoardSnapshot(out var snapshot) &&
                    string.Equals(snapshot.Fen, fen, StringComparison.Ordinal))
                {
                    boardFlipped = snapshot.BoardFlipped;
                }

                if (_analysisBoardStockfish == null && TryBeginAnalysisBoardEngineStart())
                {
                    await EnsureAnalysisBoardEngineAsync();
                }

                var engine = _analysisBoardStockfish;
                if (engine == null || engine.IsAnalyzing)
                    return;

                int previewDepth = Math.Clamp(BuildLimits.ClampDepth(_analysisBoardEngineDepth) <= 0 ? 1 : BuildLimits.ClampDepth(_analysisBoardEngineDepth), 1, _analysisBoardMatchBaseSeconds <= 30 ? 3 : 4);
                int previewThinkMs = Math.Clamp(_analysisBoardMatchBaseSeconds <= 30 ? 35 : 70, 25, 90);
                var result = await engine.GetBestMoveAsync(fen, previewThinkMs, previewDepth);

                if (previewVersion != _analysisBoardMatchAnalysisPreviewVersion ||
                    !_analysisBoardAnalysisEnabled ||
                    !_analysisBoardMatchRunning ||
                    _analysisBoardMatchPaused ||
                    sessionVersion != _analysisBoardMatchSessionVersion ||
                    _analysisBoardForm() == null ||
                    !(result.Success && result.Variations.Any()))
                {
                    return;
                }

                var limitedVariations = result.Variations.Take(BuildLimits.ClampLines(_analysisBoardLineCount)).ToList();
                var arrows = _buildArrowsForFen(fen, limitedVariations, sideToMove, boardFlipped, _analysisBoardLineCount);
                if (arrows.Count == 0)
                    return;

                string analysisKey = $"{_analysisBoardAnalysisMode}|{sideToMove}|{boardFlipped}|{fen}";
                _analysisBoardLastAnalysisKey = analysisKey;

                void ApplyPreview()
                {
                    var applyForm = _analysisBoardForm();
                    if (applyForm == null || applyForm.IsDisposed)
                        return;

                    if (!IsCurrentAnalysisBoardAnalysisStillValid(fen, boardFlipped, sideToMove))
                        return;

                    applyForm.SetAnalysisVariations(limitedVariations, sideToMove == 'b', result.AnalysisDepth);
                    applyForm.SetAnalysisArrows(arrows);
                    applyForm.SetAnalysisStatus($"Depth {result.AnalysisDepth}...");
                }

                var previewForm = _analysisBoardForm();
                if (previewForm != null && previewForm.InvokeRequired)
                    previewForm.BeginInvoke(new Action(ApplyPreview));
                else
                    ApplyPreview();
            }
            catch (Exception ex)
            {
                Log($"[MATCH ANALYSIS] Post-move preview failed: {ex.Message}");
            }
        }

        private async Task FinishAnalysisBoardMatchMoveAsync(string fen, int sessionVersion)
        {
            try
            {
                if (sessionVersion != _analysisBoardMatchSessionVersion)
                    return;

                string? drawStatus = null;
                var drawForm = _analysisBoardForm();
                if (drawForm != null)
                {
                    if (drawForm.InvokeRequired)
                    {
                        drawForm.Invoke(new Action(() =>
                        {
                            drawStatus = drawForm.DetectCurrentMatchDrawStatus();
                        }));
                    }
                    else
                    {
                        drawStatus = drawForm.DetectCurrentMatchDrawStatus();
                    }
                }

                if (!string.IsNullOrWhiteSpace(drawStatus))
                {
                    if (sessionVersion != _analysisBoardMatchSessionVersion)
                        return;
                    _analysisBoardMatchDraws++;
                    await AdvanceAnalysisBoardMatchToNextGameAsync(drawStatus, "---");
                    return;
                }

                ChessBoard board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
                if (!board.IsEndGame)
                    return;

                string status = board.EndGame?.EndgameType.ToString() ?? "Game over";
                PieceColor? wonSide = board.EndGame?.WonSide;
                if (wonSide == PieceColor.White)
                {
                    AwardMatchWinForSide('w');
                    status = "White wins.";
                    await AdvanceAnalysisBoardMatchToNextGameAsync(status, "1-0");
                }
                else if (wonSide == PieceColor.Black)
                {
                    AwardMatchWinForSide('b');
                    status = "Black wins.";
                    await AdvanceAnalysisBoardMatchToNextGameAsync(status, "0-1");
                }
                else
                {
                    _analysisBoardMatchDraws++;
                    status = "--- Draw";
                    await AdvanceAnalysisBoardMatchToNextGameAsync(status, "---");
                }
            }
            catch
            {
                await Task.CompletedTask;
            }
        }

        private void AwardMatchWinForSide(char winningSide)
        {
            string enginePath = winningSide == 'w' ? _analysisBoardMatchWhiteEnginePath : _analysisBoardMatchBlackEnginePath;
            if (!string.IsNullOrWhiteSpace(_analysisBoardMatchCompetitorAPath) &&
                string.Equals(enginePath, _analysisBoardMatchCompetitorAPath, StringComparison.OrdinalIgnoreCase))
            {
                _analysisBoardMatchWhiteWins++;
                return;
            }

            if (!string.IsNullOrWhiteSpace(_analysisBoardMatchCompetitorBPath) &&
                string.Equals(enginePath, _analysisBoardMatchCompetitorBPath, StringComparison.OrdinalIgnoreCase))
            {
                _analysisBoardMatchBlackWins++;
                return;
            }

            if (winningSide == 'w')
                _analysisBoardMatchWhiteWins++;
            else
                _analysisBoardMatchBlackWins++;
        }

        private int CalculateAnalysisBoardMatchThinkTime(long remainingMs, bool isLc0Engine, int gamePly, int pieceCount)
        {
            if (_analysisBoardMatchBaseSeconds <= 15)
            {
                int bulletThink = remainingMs switch
                {
                    <= 2000 => (int)Math.Max(20, Math.Min(60, remainingMs / 20)),
                    <= 5000 => (int)Math.Max(25, Math.Min(70, remainingMs / 26)),
                    _ => 70
                };

                if (isLc0Engine)
                    bulletThink = (int)Math.Max(20, Math.Min(55, Math.Round(bulletThink * 0.75)));

                return bulletThink;
            }

            if (_analysisBoardMatchBaseSeconds <= 30)
            {
                int fastThink = remainingMs switch
                {
                    <= 3000 => (int)Math.Max(45, Math.Min(120, remainingMs / 12)),
                    <= 10000 => (int)Math.Max(70, Math.Min(180, remainingMs / 18)),
                    _ => 180
                };

                if (isLc0Engine)
                    fastThink = (int)Math.Max(45, Math.Min(140, Math.Round(fastThink * 0.75)));

                return fastThink;
            }

            MatchPhase phase = DetermineMatchPhase(gamePly, pieceCount);

            long baseMs = Math.Max(15000, _analysisBoardMatchBaseSeconds * 1000L);
            int minimum = baseMs switch
            {
                <= 30000 => phase switch
                {
                    MatchPhase.Opening => 220,
                    MatchPhase.Endgame => 260,
                    _ => 320
                },
                <= 60000 => phase switch
                {
                    MatchPhase.Opening => 320,
                    MatchPhase.Endgame => 380,
                    _ => 550
                },
                <= 180000 => phase switch
                {
                    MatchPhase.Opening => 450,
                    MatchPhase.Endgame => 650,
                    _ => 1100
                },
                <= 300000 => phase switch
                {
                    MatchPhase.Opening => 650,
                    MatchPhase.Endgame => 850,
                    _ => 1500
                },
                <= 600000 => phase switch
                {
                    MatchPhase.Opening => 900,
                    MatchPhase.Endgame => 1200,
                    _ => 2200
                },
                <= 900000 => phase switch
                {
                    MatchPhase.Opening => 1200,
                    MatchPhase.Endgame => 1500,
                    _ => 3000
                },
                <= 1800000 => phase switch
                {
                    MatchPhase.Opening => 1600,
                    MatchPhase.Endgame => 2200,
                    _ => 4200
                },
                _ => 9000
            };

            if (remainingMs <= 2000)
                return (int)Math.Max(120, Math.Min(500, remainingMs / 6));
            if (remainingMs <= 10000)
                return (int)Math.Max(260, Math.Min(1600, remainingMs / 4));

            int fractionDivisor = _analysisBoardMatchBaseSeconds switch
            {
                <= 60 => phase switch
                {
                    MatchPhase.Opening => 18,
                    MatchPhase.Endgame => 14,
                    _ => 12
                },
                <= 180 => phase switch
                {
                    MatchPhase.Opening => 26,
                    MatchPhase.Endgame => 18,
                    _ => 14
                },
                <= 300 => phase switch
                {
                    MatchPhase.Opening => 30,
                    MatchPhase.Endgame => 22,
                    _ => 16
                },
                <= 600 => phase switch
                {
                    MatchPhase.Opening => 34,
                    MatchPhase.Endgame => 24,
                    _ => 18
                },
                _ => phase switch
                {
                    MatchPhase.Opening => 38,
                    MatchPhase.Endgame => 28,
                    _ => 20
                }
            };

            int phaseCap = _analysisBoardMatchBaseSeconds switch
            {
                <= 60 => phase switch
                {
                    MatchPhase.Opening => 900,
                    MatchPhase.Endgame => 1300,
                    _ => 1800
                },
                <= 180 => phase switch
                {
                    MatchPhase.Opening => 1100,
                    MatchPhase.Endgame => 1800,
                    _ => 2800
                },
                <= 300 => phase switch
                {
                    MatchPhase.Opening => 1500,
                    MatchPhase.Endgame => 2400,
                    _ => 3600
                },
                <= 600 => phase switch
                {
                    MatchPhase.Opening => 2200,
                    MatchPhase.Endgame => 3200,
                    _ => 5000
                },
                _ => phase switch
                {
                    MatchPhase.Opening => 3200,
                    MatchPhase.Endgame => 4600,
                    _ => 7000
                }
            };

            long fractionBudget = remainingMs / fractionDivisor;
            long capped = Math.Max(minimum, Math.Min(phaseCap, fractionBudget));
            int thinkTime = (int)Math.Max(120, Math.Min(15000, capped));

            if (isLc0Engine)
            {
                // LC0 on CPU is materially slower per node than Stockfish in this
                // match mode. Give it a much tighter bullet budget so it can stay
                // on the clock, while keeping a gentler reduction for slower time
                // controls where quality matters more than raw move latency.
                double lc0Scale = _analysisBoardMatchBaseSeconds switch
                {
                    <= 15 => 0.16,
                    <= 30 => 0.18,
                    <= 60 => 0.24,
                    <= 180 => 0.32,
                    <= 300 => 0.36,
                    _ => 0.40
                };

                int lc0Cap = _analysisBoardMatchBaseSeconds switch
                {
                    <= 15 => 600,
                    <= 30 => 800,
                    <= 60 => 1200,
                    <= 180 => 2200,
                    <= 300 => 3000,
                    _ => 5000
                };

                thinkTime = (int)Math.Max(100, Math.Min(lc0Cap, Math.Round(thinkTime * lc0Scale)));
            }

            return thinkTime;
        }

        private enum MatchPhase
        {
            Opening,
            Middlegame,
            Endgame
        }

        private static MatchPhase DetermineMatchPhase(int gamePly, int pieceCount)
        {
            if (pieceCount <= 12 || gamePly >= 60)
                return MatchPhase.Endgame;
            if (gamePly < 16 && pieceCount >= 24)
                return MatchPhase.Opening;
            return MatchPhase.Middlegame;
        }

        private static int GetFenGamePly(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return 0;

            string[] parts = fen.Split(' ');
            if (parts.Length < 6)
                return 0;

            char sideToMove = parts[1].Length > 0 ? parts[1][0] : 'w';
            if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fullmove))
                return 0;

            fullmove = Math.Max(fullmove, 1);
            return (fullmove - 1) * 2 + (sideToMove == 'b' ? 1 : 0);
        }

        private static int CountFenPieces(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return 32;

            string boardPart = fen.Split(' ')[0];
            int count = 0;
            foreach (char c in boardPart)
            {
                if ("KQRBNPkqrbnp".IndexOf(c) >= 0)
                    count++;
            }

            return count;
        }

        private static bool IsLc0EnginePath(string? enginePath)
        {
            if (string.IsNullOrWhiteSpace(enginePath))
                return false;

            string fileName = Path.GetFileNameWithoutExtension(enginePath).ToLowerInvariant();
            return fileName.Contains("lc0") || fileName.Contains("leela");
        }

        private async Task<UCIEngine?> EnsureAnalysisBoardMatchEngineAsync(bool forWhite)
        {
            string path = forWhite ? _analysisBoardMatchWhiteEnginePath : _analysisBoardMatchBlackEnginePath;
            string name = forWhite ? _analysisBoardMatchWhiteEngineName : _analysisBoardMatchBlackEngineName;
            UCIEngine? existingEngine = forWhite ? _analysisBoardMatchWhiteEngine : _analysisBoardMatchBlackEngine;
            if (existingEngine != null)
            {
                if (string.Equals(existingEngine.GetEnginePath(), path, StringComparison.OrdinalIgnoreCase))
                    return existingEngine;

                if (forWhite)
                    _analysisBoardMatchWhiteEngine = null;
                else
                    _analysisBoardMatchBlackEngine = null;

                await DisposeAnalysisBoardMatchEngineAsync(
                    existingEngine,
                    forWhite ? "white" : "black",
                    "match engine selection changed");
            }

            if (string.IsNullOrWhiteSpace(path) || !_isUsableEnginePath(path))
                return null;

            var engine = new UCIEngine(path)
            {
                InitialDepth = 10,
                MaxDepth = BuildLimits.MaxDepth,
                InitialThinkTime = 250,
                MaxThinkTime = 8000,
                TimeIncrement = 25,
                DepthIncrement = 1,
                EloLimitEnabled = false,
                AdaptiveHuman = _humanAdaptiveEnabled(),
                HumanPlayProfile = _humanPlayProfile()
            };

            if (_isHumanEnginePath(path))
                UpdateAnalysisBoardMatchDisplay(_getEngineStartupFeedback(path, name));

            if (!await engine.StartAsync())
            {
                string failureMessage = _getEngineFailureMessage(engine, $"Could not start {name}.");
                engine.Dispose();
                Log($"[MATCH] {failureMessage}");
                if (_isPrivateEngineStartupBlocked(path, failureMessage))
                {
                    _showPrivateEngineLicenseNotice(failureMessage);
                    StopAnalysisBoardMatch(failureMessage);
                }
                return null;
            }

            await engine.SendCommandAsync($"setoption name Threads value {_analysisBoardMatchThreads}");
            await engine.SendCommandAsync($"setoption name Hash value {_analysisBoardMatchHashMb}");
            await engine.SendCommandAsync($"setoption name MultiPV value {BuildLimits.ClampLines(_analysisBoardLineCount)}");
            if (forWhite)
                _analysisBoardMatchWhiteEngine = engine;
            else
                _analysisBoardMatchBlackEngine = engine;
            if (_isHumanEnginePath(path))
                UpdateAnalysisBoardMatchDisplay($"{name} ready.");
            return engine;
        }

        private void UpdateAnalysisBoardMatchDisplay(string? statusOverride = null)
        {
            AnalysisBoardForm? form = _analysisBoardForm();
            if (form == null)
                return;

            if (!string.IsNullOrWhiteSpace(statusOverride))
                _analysisBoardMatchStatus = statusOverride;

            long whiteMs = _analysisBoardMatchWhiteRemainingMs;
            long blackMs = _analysisBoardMatchBlackRemainingMs;
            if (_analysisBoardMatchRunning && !_analysisBoardMatchPaused && _analysisBoardMatchTurnStartedUtc != DateTime.MinValue)
            {
                long elapsed = (long)(DateTime.UtcNow - _analysisBoardMatchTurnStartedUtc).TotalMilliseconds;
                if (_analysisBoardMatchTurnColor == 'w')
                    whiteMs = Math.Max(0, whiteMs - elapsed);
                else
                    blackMs = Math.Max(0, blackMs - elapsed);
            }

            string whiteClock = FormatMatchClock(whiteMs);
            string blackClock = FormatMatchClock(blackMs);
            long displayVersion = Interlocked.Increment(ref _analysisBoardMatchDisplayVersion);

            void Apply()
            {
                // Competitor A's score is _analysisBoardMatchWhiteWins, B's is _matchBlackWins
                // (per-engine, attributed by AwardMatchWinForSide). Tell the form which side
                // Competitor A is playing THIS game so each score follows its engine across swaps.
                bool whiteSideIsCompetitorA = string.IsNullOrWhiteSpace(_analysisBoardMatchCompetitorAPath)
                    || string.Equals(_analysisBoardMatchWhiteEnginePath, _analysisBoardMatchCompetitorAPath, StringComparison.OrdinalIgnoreCase);
                form.SetMatchDisplay(displayVersion, _analysisBoardMatchRunning, _analysisBoardMatchPaused, whiteClock, blackClock, _analysisBoardMatchCompetitorAName, _analysisBoardMatchWhiteWins, _analysisBoardMatchCompetitorBName, _analysisBoardMatchBlackWins, _analysisBoardMatchDraws, _analysisBoardMatchStatus, whiteSideIsCompetitorA);
            }

            if (form.InvokeRequired)
                form.BeginInvoke(new Action(Apply));
            else
                Apply();
        }

        private static string FormatMatchClock(long remainingMs)
        {
            remainingMs = Math.Max(0, remainingMs);
            TimeSpan remaining = TimeSpan.FromMilliseconds(remainingMs);
            if (remaining.TotalMinutes >= 1)
                return $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
            return $"{remaining.Seconds:00}.{remaining.Milliseconds / 100:0}";
        }

        public void HandleAnalysisBoardMirrorModeChanged(bool enabled)
        {
            _analysisBoardMirrorEnabled = enabled;
            _lastMirroredExternalFen = "";

            Log($"[{DateTime.Now:HH:mm:ss}] Analysis board mirror mode {(enabled ? "ENABLED" : "DISABLED")}");
            _refreshDebugView(enabled ? "Analysis board mirror enabled" : "Analysis board mirror disabled");

            // Mirror is the board's only server-backed feature, so the Free
            // watermark / cooldown is tied to it. Refresh immediately so it clears
            // the moment mirror turns off and reappears the moment it turns on.
            UpdateFreeAnalysisBoardLiveStatus();

            // Drop any stale "mirror paused" hint the instant mirror turns off;
            // while on, Program's ~1s tick keeps it current.
            if (!enabled)
                UpdateMirrorPausedHint(false, "");

            string currentFen = _currentFen();
            if (enabled && !_currentFenIsAnalysisBoard() && !string.IsNullOrWhiteSpace(currentFen))
            {
                MirrorExternalFen(_applyInferredExternalTurnToFen(currentFen), _externalBoardDetectedFlipped(), force: true);
            }
        }

        public void MirrorExternalFen(string fen, bool? boardFlipped = null, bool force = false)
        {
            var form = _analysisBoardForm();
            if (!_analysisBoardMirrorEnabled || form == null || string.IsNullOrWhiteSpace(fen))
                return;

            if (_currentFenIsAnalysisBoard() || _isActiveAnalysisBoardFen(fen))
                return;

            string mirrorKey = $"{(boardFlipped == true ? "1" : "0")}|{fen}";
            if (!force && string.Equals(mirrorKey, _lastMirroredExternalFen, StringComparison.Ordinal))
                return;

            _lastMirroredExternalFen = mirrorKey;

            void ApplyMirrorFen()
            {
                var applyForm = _analysisBoardForm();
                if (applyForm == null || !_analysisBoardMirrorEnabled)
                    return;

                if (applyForm.MirrorExternalFen(fen, boardFlipped))
                {
                    _analysisBoardLastAnalysisKey = "";
                    string shortFen = fen.Length > 48 ? fen[..48] + "..." : fen;
                    Log($"[MIRROR] Analysis board mirrored external FEN: {shortFen} | flipped={boardFlipped}");
                }
            }

            if (form.InvokeRequired)
            {
                form.BeginInvoke(new Action(ApplyMirrorFen));
            }
            else
            {
                ApplyMirrorFen();
            }
        }

        public void SetAnalysisBoardAnalysisStatus(string statusText)
        {
            var form = _analysisBoardForm();
            if (form == null)
                return;

            if (form.InvokeRequired)
            {
                form.BeginInvoke(new Action(() => form.SetAnalysisStatus(statusText)));
            }
            else
            {
                form.SetAnalysisStatus(statusText);
            }
        }

        public void SuspendAnalysisBoardAnalysisForLiveBoard(string statusText = "Paused while live board analysis is active.")
        {
            _analysisBoardLastAnalysisKey = "";
            _analysisBoardAnalysisInProgress = false;
            _analysisBoardAnalysisRestartPending = false;
            _analysisBoardAnalysisAbortInProgress = false;
            SetAnalysisBoardAnalysisStatus(statusText);

            var engine = _analysisBoardStockfish;
            if (engine == null)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await engine.AbortCurrentAnalysisAsync();
                }
                catch (Exception ex)
                {
                    Log($"[TEST ANALYSIS] Failed to suspend analysis board engine: {ex.Message}");
                }
            });
        }

        public void RefreshAnalysisBoardSnapshotForExternalDetection()
        {
            var form = _analysisBoardForm();
            if (form == null || form.IsDisposed || !form.Visible || form.WindowState == FormWindowState.Minimized)
                return;

            void RefreshNow()
            {
                if (form.IsDisposed)
                    return;

                form.RefreshExternalDetectionSnapshot();
            }

            if (form.InvokeRequired)
            {
                form.BeginInvoke(new Action(RefreshNow));
            }
            else
            {
                RefreshNow();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(180);
                    if (form.IsDisposed)
                        return;

                    if (form.InvokeRequired)
                        form.BeginInvoke(new Action(RefreshNow));
                    else
                        RefreshNow();
                }
                catch
                {
                }
            });
        }

        public void TryQueueAnalysisBoardAnalysis(int sessionVersion)
        {
            if (!_ensureLicensedFeatureAvailable("analysis board engine analysis", false))
            {
                SetAnalysisBoardAnalysisStatus("License verification required.");
                return;
            }

            if (!_analysisBoardAnalysisEnabled || sessionVersion != _analysisBoardAnalysisSessionVersion)
                return;

            if (_analysisBoardMatchRunning)
            {
                SetAnalysisBoardAnalysisStatus("Following match engine moves...");
                return;
            }

            if (!_tryGetActiveAnalysisBoardSnapshot(out var snapshot))
            {
                SetAnalysisBoardAnalysisStatus("Show the analysis board to analyze.");
                return;
            }

            var engine = _analysisBoardStockfish;
            if (engine == null)
            {
                if (TryGetAnalysisBoardEngineStartupFailure(out string startupFailure))
                {
                    SetAnalysisBoardAnalysisStatus(startupFailure);
                    return;
                }

                bool shouldStart = TryBeginAnalysisBoardEngineStart();

                SetAnalysisBoardAnalysisStatus(_getEngineStartupFeedback(_analysisBoardEnginePath, _analysisBoardEngineName));
                if (shouldStart)
                    _ = EnsureAnalysisBoardEngineAsync();
                return;
            }

            string fen = snapshot.Fen;
            if (string.IsNullOrWhiteSpace(fen))
            {
                SetAnalysisBoardAnalysisStatus("Waiting for a valid analysis-board position...");
                return;
            }

            char promptReferenceColor = _analysisBoardAnalysisMode switch
            {
                "BLACK" => 'b',
                _ => 'w'
            };

            if (!_tryResolveOrientationDecision(fen, true, promptReferenceColor, out _))
                return;

            var statusForm = _analysisBoardForm();
            if (statusForm != null)
            {
                bool isTerminal = false;
                string terminalStatus = string.Empty;
                if (statusForm.InvokeRequired)
                {
                    statusForm.Invoke(new Action(() =>
                    {
                        isTerminal = statusForm.TryGetTerminalAnalysisStatus(out terminalStatus);
                    }));
                }
                else
                {
                    isTerminal = statusForm.TryGetTerminalAnalysisStatus(out terminalStatus);
                }

                if (isTerminal)
                {
                    _analysisBoardLastAnalysisKey = "";
                    SetAnalysisBoardAnalysisStatus(terminalStatus);
                    _analysisBoardForm()?.BeginInvoke(new Action(() =>
                    {
                        var clearForm = _analysisBoardForm();
                        if (clearForm == null)
                            return;
                        clearForm.ClearAnalysisArrows();
                        clearForm.ClearAnalysisVariations();
                    }));
                    return;
                }
            }

            char sideToMove = _getSideToMove(fen) ?? 'w';
            char requestedColor = _analysisBoardAnalysisMode switch
            {
                "BLACK" => 'b',
                "BOTH" => sideToMove,
                _ => 'w'
            };

            if (_analysisBoardAnalysisMode != "BOTH" && sideToMove != requestedColor)
            {
                _analysisBoardLastAnalysisKey = "";
                string sideToMoveName = sideToMove == 'b' ? "Black" : "White";
                SetAnalysisBoardAnalysisStatus($"Waiting for {sideToMoveName} to move.");
                _analysisBoardForm()?.BeginInvoke(new Action(() =>
                {
                    var clearForm = _analysisBoardForm();
                    if (clearForm == null)
                        return;
                    clearForm.ClearAnalysisArrows();
                    clearForm.ClearAnalysisVariations();
                }));
                return;
            }

            string analysisKey = $"{_analysisBoardAnalysisMode}|{requestedColor}|{snapshot.BoardFlipped}|{fen}";
            // Refresh the server-driven Free watermark (remaining moves / cooldown).
            // The server governs the limit now, so this never blocks the request;
            // an active cooldown simply means the server returns no result and the
            // pinned watermark counts down.
            TryConsumeFreeAnalysisBoardLivePly(fen);

            if (_analysisBoardAnalysisInProgress)
            {
                if (analysisKey == _analysisBoardLastAnalysisKey)
                    return;

                SetAnalysisBoardAnalysisStatus("Position changed, restarting analysis...");
                _analysisBoardAnalysisRestartPending = true;
                _analysisBoardLastAnalysisKey = "";
                _analysisBoardForm()?.BeginInvoke(new Action(() =>
                {
                    var clearForm = _analysisBoardForm();
                    if (clearForm == null)
                        return;
                    clearForm.ClearAnalysisArrows();
                    clearForm.ClearAnalysisVariations();
                }));

                if (_analysisBoardAnalysisAbortInProgress)
                    return;

                _analysisBoardAnalysisAbortInProgress = true;
                try { _analysisBoardAnalysisCts?.Cancel(); } catch { }
                Log($"[TEST ANALYSIS] Position changed while engine was busy; aborting stale search and requeueing.");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await engine.AbortCurrentAnalysisAsync();
                    }
                    catch (Exception ex)
                    {
                        Log($"[TEST ANALYSIS] Abort failed during restart: {ex.Message}");
                    }

                    _analysisBoardAnalysisInProgress = false;
                    _analysisBoardAnalysisAbortInProgress = false;
                    if (sessionVersion == _analysisBoardAnalysisSessionVersion)
                    {
                        _analysisBoardAnalysisRestartPending = false;
                        TryQueueAnalysisBoardAnalysis(sessionVersion);
                    }
                });
                return;
            }

            if (!_analysisBoardEngineInfinite && analysisKey == _analysisBoardLastAnalysisKey)
                return;

            _analysisBoardLastAnalysisKey = analysisKey;
            _analysisBoardAnalysisInProgress = true;
            var analysisCts = new System.Threading.CancellationTokenSource();
            _analysisBoardAnalysisCts = analysisCts;
            SetAnalysisBoardAnalysisStatus("Waiting for engine lines...");
            _ = AnalyzeAnalysisBoardPositionAsync(engine, fen, requestedColor == 'b', snapshot.BoardFlipped, sessionVersion, analysisKey, analysisCts);
        }

        // The Free move cap is gone client-side: the SERVER governs the limit and
        // reports it on each analysis response (surfaced via FreeTierServerState).
        // This is now just the per-analysis hook to refresh the server-driven
        // watermark; it never blocks analysis here. (Live analysis-board requests
        // run through the same server engine, so the cooldown still pauses them
        // when the server stops returning results.)
        private bool TryConsumeFreeAnalysisBoardLivePly(string fen)
        {
            UpdateFreeAnalysisBoardLiveStatus();
            return true;
        }

        private void UpdateFreeAnalysisBoardLiveStatus()
        {
            AnalysisBoardForm? form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            // The analysis board runs a LOCAL engine, so its own analysis consumes
            // no server resource and is unlimited in Free. Its only server-backed
            // feature is mirror mode, which streams vision-detected FENs. So the
            // Free watermark / cooldown applies only while mirroring; with mirror
            // off the board is unmetered (no watermark, no limit) even if the
            // overlay's vision has armed the global Free state.
            bool armed = _analysisBoardMirrorEnabled && FreeTierServerState.IsFreeLimited;
            int remainingMoves = armed ? FreeTierServerState.FreeMovesRemaining : 0;
            int cooldownSeconds = armed ? FreeTierServerState.CooldownRemainingSeconds : 0;
            bool inCooldown = armed && cooldownSeconds > 0;

            void update() => form.SetFreeAnalysisLimitStatus(armed, remainingMoves, cooldownSeconds, inCooldown);

            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(update));
                else
                    update();
            }
            catch
            {
                // Analysis board may be closing; free status will refresh on next analysis.
            }
        }

        /// <summary>
        /// Shows or clears the "mirror paused" hint on the analysis board. Driven by
        /// Program's ~1s status tick, which knows whether the mirrored source board
        /// is currently readable (visible vs. covered/undetected).
        /// </summary>
        public void UpdateMirrorPausedHint(bool paused, string message)
        {
            AnalysisBoardForm? form = _analysisBoardForm();
            if (form == null || form.IsDisposed)
                return;

            void update() => form.SetMirrorPausedHint(paused, message);

            try
            {
                if (form.InvokeRequired)
                    form.BeginInvoke(new Action(update));
                else
                    update();
            }
            catch
            {
                // Analysis board may be closing; the hint refreshes on the next tick.
            }
        }

        // "Limit reached" now means the SERVER put this Free session into a cooldown.
        // Only mirror mode consumes a server resource (vision); manual / local-engine
        // analysis is never limited, so the cooldown gates the board only while
        // mirroring.
        private bool IsFreeAnalysisBoardLiveLimitReached()
        {
            return _analysisBoardMirrorEnabled && FreeTierServerState.IsInCooldown;
        }

        private async Task EnsureAnalysisBoardEngineAsync()
        {
            int startSessionVersion = _analysisBoardAnalysisSessionVersion;
            string enginePath = _analysisBoardEnginePath;
            string engineName = _analysisBoardEngineName;
            int engineDepth = _analysisBoardEngineDepth;
            bool engineInfinite = _analysisBoardEngineInfinite;
            int engineThreads = _analysisBoardEngineThreads;
            int engineHashMb = _analysisBoardEngineHashMb;
            int lineCount = _analysisBoardLineCount;

            try
            {
                await WaitForAnalysisBoardEngineShutdownAsync();

                if (_analysisBoardStockfish != null ||
                    startSessionVersion != _analysisBoardAnalysisSessionVersion ||
                    !string.Equals(enginePath, _analysisBoardEnginePath, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(enginePath) ||
                    !_isUsableEnginePath(enginePath))
                {
                    return;
                }

                var engine = new UCIEngine(enginePath)
                {
                    InitialDepth = Math.Min(BuildLimits.ClampDepth(engineDepth), 10),
                    MaxDepth = engineInfinite ? 99 : Math.Max(BuildLimits.ClampDepth(engineDepth), 10),
                    InitialThinkTime = _quickArrowThinkTimeMs(),
                    MaxThinkTime = 250,
                    TimeIncrement = 25,
                    DepthIncrement = 1,
                    EloLimitEnabled = false,
                    AdaptiveHuman = _humanAdaptiveEnabled(),
                    HumanPlayProfile = _humanPlayProfile()
                };

                SetAnalysisBoardAnalysisStatus(_getEngineStartupFeedback(enginePath, engineName));
                if (await engine.StartAsync())
                {
                    if (startSessionVersion != _analysisBoardAnalysisSessionVersion ||
                        !string.Equals(enginePath, _analysisBoardEnginePath, StringComparison.OrdinalIgnoreCase) ||
                        _analysisBoardStockfish != null)
                    {
                        Log($"[ANALYSIS SETTINGS] Disposing stale Analysis Board engine startup: {Path.GetFileName(enginePath)}");
                        await DisposeAnalysisBoardEngineInstanceAsync(engine, "stale startup");
                        return;
                    }

                    await engine.SendCommandAsync($"setoption name Threads value {engineThreads}");
                    await engine.SendCommandAsync($"setoption name Hash value {engineHashMb}");
                    await engine.SendCommandAsync($"setoption name MultiPV value {BuildLimits.ClampLines(lineCount)}");
                    _analysisBoardStockfish = engine;
                    ClearAnalysisBoardEngineStartupFailure();
                    Log($"[INFO] Analysis board engine initialized: {Path.GetFileName(enginePath)}");
                    if (_isHumanEnginePath(enginePath))
                        SetAnalysisBoardAnalysisStatus("Human Chess Engine ready.");

                    int sessionVersion = _analysisBoardAnalysisSessionVersion;
                    TryQueueAnalysisBoardAnalysis(sessionVersion);
                }
                else
                {
                    string failureMessage = _getEngineFailureMessage(engine, $"Could not start {engineName}.");
                    engine.Dispose();
                    _analysisBoardEngineStartupFailurePath = enginePath;
                    _analysisBoardEngineStartupFailureMessage = failureMessage;
                    Log($"[WARNING] Analysis board engine failed to start: {failureMessage}");
                    SetAnalysisBoardAnalysisStatus(failureMessage);
                    if (_isPrivateEngineStartupBlocked(enginePath, failureMessage))
                    {
                        _analysisBoardAnalysisEnabled = false;
                        _analysisBoardAnalysisTimer?.Dispose();
                        _analysisBoardAnalysisTimer = null;
                        _analysisBoardForm()?.BeginInvoke(new Action(() =>
                        {
                            var clearForm = _analysisBoardForm();
                            if (clearForm == null)
                                return;
                            clearForm.ClearAnalysisArrows();
                            clearForm.ClearAnalysisVariations();
                        }));
                        _showPrivateEngineLicenseNotice(failureMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WARNING] Analysis board engine error: {ex.Message}");
                string failureMessage = $"{engineName} error. {ex.Message}";
                _analysisBoardEngineStartupFailurePath = enginePath;
                _analysisBoardEngineStartupFailureMessage = failureMessage;
                SetAnalysisBoardAnalysisStatus(failureMessage);
            }
            finally
            {
                lock (_analysisBoardEngineStartLock)
                {
                    _analysisBoardEngineStartInProgress = false;
                }
            }
        }

        private async Task AnalyzeAnalysisBoardPositionAsync(UCIEngine engine, string fen, bool isBlackPerspective, bool boardFlipped, int sessionVersion, string analysisKey, System.Threading.CancellationTokenSource analysisCts)
        {
            Action<BestMoveResult>? streamHandler = null;
            try
            {
                bool TryApplyResult(BestMoveResult result, string stageLabel)
                {
                    var applyForm = _analysisBoardForm();
                    if (!_analysisBoardAnalysisEnabled ||
                        sessionVersion != _analysisBoardAnalysisSessionVersion ||
                        analysisKey != _analysisBoardLastAnalysisKey ||
                        applyForm == null)
                    {
                        return false;
                    }

                    if (!(result.Success && result.Variations.Any()))
                    {
                        return false;
                    }

                    char expectedMovingSide = _getSideToMove(fen) ?? (isBlackPerspective ? 'b' : 'w');
                    var arrows = _buildArrowsForFen(fen, result.Variations, expectedMovingSide, boardFlipped, _analysisBoardLineCount);
                    if (arrows.Count == 0)
                    {
                        return false;
                    }

                    var limitedVariations = result.Variations.Take(_analysisBoardLineCount).ToList();

                    applyForm.BeginInvoke(new Action(() =>
                    {
                        var invokeForm = _analysisBoardForm();
                        if (invokeForm != null &&
                            _analysisBoardAnalysisEnabled &&
                            sessionVersion == _analysisBoardAnalysisSessionVersion &&
                            analysisKey == _analysisBoardLastAnalysisKey &&
                            IsCurrentAnalysisBoardAnalysisStillValid(fen, boardFlipped, expectedMovingSide))
                        {
                            invokeForm.SetAnalysisVariations(limitedVariations, isBlackPerspective, result.AnalysisDepth);
                            invokeForm.SetAnalysisArrows(arrows);
                            invokeForm.SetAnalysisStatus(stageLabel == "preview"
                                ? $"Preview ready at depth {result.AnalysisDepth}..."
                                : stageLabel == "stream"
                                    ? $"Depth {result.AnalysisDepth}..."
                                    : $"Analysis ready at depth {result.AnalysisDepth}.");
                        }
                        else
                        {
                            // Late stream/final callbacks are normal while the user is dragging
                            // pieces around. Ignore stale updates; the queue/timer owns clearing
                            // and scheduling for the new position.
                        }
                    }));

                    return true;
                }

                streamHandler = update => TryApplyResult(update, "stream");
                engine.AnalysisUpdated += streamHandler;

                bool matchPreviewMode = _analysisBoardMatchRunning;
                int matchPreviewDepth = Math.Clamp(BuildLimits.ClampDepth(_analysisBoardEngineDepth) <= 0 ? 1 : BuildLimits.ClampDepth(_analysisBoardEngineDepth), 1, 4);
                int matchPreviewThinkMs = Math.Clamp(_analysisBoardMatchBaseSeconds <= 30 ? 35 : 75, 25, 100);
                BestMoveResult? result;

                if (matchPreviewMode)
                {
                    result = await engine.GetBestMoveAsync(fen, matchPreviewThinkMs, matchPreviewDepth);
                }
                else
                {
                    int requestedDepth = BuildLimits.ClampDepth(_analysisBoardEngineDepth);

                    if (_analysisBoardEngineInfinite)
                    {
                        result = await engine.GetBestMoveIterativeInfinite(fen, analysisCts.Token);
                    }
                    else
                    {
                        int previewDepth = requestedDepth <= 0
                            ? 0
                            : Math.Min(_quickArrowDepth(), Math.Min(requestedDepth, 4));
                        int previewThinkMs = requestedDepth <= 0
                            ? 18
                            : requestedDepth <= 4
                                ? 24
                                : Math.Min(_quickArrowThinkTimeMs(), 55);

                        var previewResult = await engine.GetBestMoveAsync(fen, previewThinkMs, previewDepth);
                        bool previewApplied = TryApplyResult(previewResult, "preview");
                        int previewAchievedDepth = previewResult.Variations.FirstOrDefault()?.Depth ?? previewResult.AnalysisDepth;

                        result = requestedDepth > 0 && previewAchievedDepth < requestedDepth
                            ? await engine.GetBestMoveAsync(fen, Math.Max(_quickArrowThinkTimeMs(), requestedDepth * 12), requestedDepth)
                            : previewResult;

                        if (previewApplied && ReferenceEquals(result, previewResult))
                        {
                            return;
                        }
                    }
                }

                if (!_analysisBoardAnalysisEnabled ||
                    sessionVersion != _analysisBoardAnalysisSessionVersion ||
                    analysisKey != _analysisBoardLastAnalysisKey ||
                    _analysisBoardForm() == null)
                {
                    return;
                }

                if (!(result.Success && result.Variations.Any()))
                {
                    bool isInfiniteWarmup = _analysisBoardEngineInfinite &&
                        string.Equals(result.Error, "Waiting for engine lines...", StringComparison.Ordinal);

                    if (!isInfiniteWarmup && analysisKey == _analysisBoardLastAnalysisKey)
                    {
                        _analysisBoardLastAnalysisKey = "";
                    }

                    Log($"[TEST ANALYSIS] No engine lines for current position: {result.Error ?? "empty result"}");
                    SetAnalysisBoardAnalysisStatus(isInfiniteWarmup
                        ? "Waiting for deeper engine lines..."
                        : "No engine lines yet, retrying...");
                    return;
                }

                TryApplyResult(result, "final");
            }
            catch (Exception ex)
            {
                Log($"[TEST ANALYSIS ERROR] {ex.Message}");
            }
            finally
            {
                if (streamHandler != null)
                {
                    engine.AnalysisUpdated -= streamHandler;
                }
                if (ReferenceEquals(_analysisBoardAnalysisCts, analysisCts))
                    _analysisBoardAnalysisCts = null;
                analysisCts.Dispose();
                _analysisBoardAnalysisInProgress = false;
                _analysisBoardAnalysisAbortInProgress = false;
                if (_analysisBoardAnalysisRestartPending &&
                    _analysisBoardAnalysisEnabled &&
                    sessionVersion == _analysisBoardAnalysisSessionVersion)
                {
                    _analysisBoardAnalysisRestartPending = false;
                    TryQueueAnalysisBoardAnalysis(sessionVersion);
                }
            }
        }
    }
}
