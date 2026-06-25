using ChessKit;
using OpenCvSharp;
using System.Diagnostics;
using static ChessKit.FenText;

// System window events, confirmed-FEN application, and orientation prompts.
partial class Program
{
    private static void OnSystemWindowEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        IntPtr tracked = _trackedHwnd;
        IntPtr lost = _lostHwndCache;
        // Match against both the active tracked HWND and the lost one.
        // Without checking _lostHwndCache, we'd miss MINIMIZE_END events
        // that fire after we've already cleared _trackedHwnd in
        // HideOverlaysAfterWindowGone - which is exactly the moment
        // we'd want to know about a real user-initiated restore.
        bool matchesTracked = tracked != IntPtr.Zero && hwnd == tracked;
        bool matchesLost = lost != IntPtr.Zero && hwnd == lost;
        if (!matchesTracked && !matchesLost) return;

        string eventName = WinEventHelper.GetWinEventName(eventType);
        if (_diagLoggingEnabled)
        {
            LogDiag(
                "WINTRACK",
                $"system event: {eventName} hwnd=0x{hwnd.ToInt64():X} idObject={idObject} idChild={idChild} matchesTracked={matchesTracked} matchesLost={matchesLost}");
        }

        bool isWindowObjectEvent = WinEventHelper.IsWinEventWindowObject(idObject, idChild);
        bool isTrackedWindowGoneEvent =
            (eventType == WindowTracker.EVENT_SYSTEM_MINIMIZESTART && isWindowObjectEvent)
            || (eventType == WindowTracker.EVENT_OBJECT_DESTROY && isWindowObjectEvent);

        if (isTrackedWindowGoneEvent)
        {
            if (_diagLoggingEnabled)
            {
                string evt = eventType == WindowTracker.EVENT_OBJECT_DESTROY ? "destroyed" : "minimize-start";
                LogDiag("WINTRACK", $"system event: tracked window {evt} - hiding overlays instantly");
            }
            // Cancel any in-flight analysis. Without this, an analysis
            // that started before the minimize will complete a second
            // or two later and call ShowMoveArrows, making arrows
            // re-appear ON TOP of whatever's behind the board - the
            // user-visible "arrows vanish, reappear frozen for 2s,
            // vanish again" pattern. The session-version bump inside
            // CancelPendingAnalysis ensures the late result gets
            // discarded when it arrives.
            try
            {
                CancelPendingAnalysis("tracked window minimized/destroyed");
            }
            catch { /* never let hook handler throw */ }

            // Visual hide. State cleanup happens on the next main-
            // loop frame when IsTrackable returns false - mutating
            // main-loop state from this worker thread would race
            // with the loop's reads.
            HideOverlaysVisually();
        }
        else if (eventType == WindowTracker.EVENT_OBJECT_DESTROY && _diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"system event: ignored non-window destroy for tracked hwnd idObject={idObject} idChild={idChild}");
        }
        else if (eventType == WindowTracker.EVENT_SYSTEM_MINIMIZESTART && _diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"system event: ignored non-window minimize-start for tracked hwnd idObject={idObject} idChild={idChild}");
        }
        else if (eventType == WindowTracker.EVENT_SYSTEM_MINIMIZEEND && isWindowObjectEvent)
        {
            // The OS is telling us this window just un-minimized.
            // We use this as the AUTHORITATIVE signal for a legitimate
            // user-initiated restore - IsIconic polling is unreliable
            // on some machines (oscillates without user action), but
            // MINIMIZE_END only fires for actual restore operations.
            // Setting this flag is what allows the lost-tracking poll
            // in the main loop to release the latch.
            _minimizeEndFiredForLostHwnd = true;
            if (_diagLoggingEnabled)
            {
                LogDiag("WINTRACK", $"system event: minimize-end fired for hwnd=0x{hwnd.ToInt64():X} (matchesTracked={matchesTracked} matchesLost={matchesLost})");
            }
        }
        else if (eventType == WindowTracker.EVENT_SYSTEM_MINIMIZEEND && _diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"system event: ignored non-window minimize-end for tracked hwnd idObject={idObject} idChild={idChild}");
        }
    }

    private static void ShowToolbarAtFallbackPosition(bool ignoreHidden = false)
    {
        if (_settingsToolbar == null)
            return;

        if (_settingsToolbarHidden && !ignoreHidden)
            return;

        Rectangle fallbackAnchor = GetToolbarFallbackAnchorRect();
        _settingsToolbar.SetBoardVisible(true);
        _settingsToolbar.UpdatePosition(fallbackAnchor);
    }

    private static void ShowToolbarForUserAction()
    {
        if (_settingsToolbar == null)
            return;

        if (_settingsToolbarHidden)
            return;

        Log($"[Toolbar] Show requested by user action; trackedBox={_lastTrackedBox.HasValue} trackedHwnd=0x{_trackedHwnd.ToInt64():X} lostLatch={_trackingLostWaitingForReacquire}");
        _settingsToolbar.SetEnabled(true);

        if (_lastTrackedBox.HasValue &&
            !_trackingLostWaitingForReacquire &&
            (_trackedHwnd == IntPtr.Zero || WindowTracker.IsTrackable(_trackedHwnd)))
        {
            Rect r = _lastTrackedBox.Value;
            _settingsToolbar.SetBoardVisible(true);
            _settingsToolbar.UpdatePosition(new Rectangle(r.X, r.Y, r.Width, r.Height));
            return;
        }

        ShowToolbarAtFallbackPosition();
    }

    private static DialogResult ShowTopMostMessageBox(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        using var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new System.Drawing.Size(1, 1),
            Opacity = 0,
            TopMost = true
        };

        owner.Show();
        owner.BringToFront();
        owner.Activate();

        return MessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
    }

    private static bool TryEnableOverlayTrackingForUserAction(string featureName)
    {
        if (_isTracking)
            return true;

        if (!EnsureLicensedFeatureAvailable(featureName, notifyUser: true))
            return false;

        _isTracking = true;
        ResetPendingFenCandidate();
        ResetConfirmedStateTimeline();
        ResetConfirmedBoardSnapshot();
        ResetAnalysisSchedulingState();
        Log($"[{DateTime.Now:HH:mm:ss}] Overlay ENABLED");
        RefreshDebugView("Overlay enabled");

        if (_evalBarEnabled && _evalBar != null)
            _evalBar.SetEnabled(true);

        if (_engineLinesEnabled && _engineLines != null)
            _engineLines.SetEnabled(true);


        if (_settingsToolbar != null)
        {
            ShowToolbarForUserAction();
        }

        return true;
    }

    private static void ApplyConfirmedFen(
        string confirmedFen,
        Mat? confirmedBoardSnapshot = null,
        bool beginNoiseGuard = true,
        string logPrefix = "[FEN]",
        bool isAnalysisBoardSource = false,
        bool allowOutOfTurnHold = true)
    {
        if (string.IsNullOrEmpty(confirmedFen) || confirmedFen == _currentFEN)
        {
            if (!string.IsNullOrEmpty(confirmedFen))
            {
                _currentFenIsAnalysisBoard = isAnalysisBoardSource;
            }

            if (confirmedBoardSnapshot != null)
            {
                UpdateConfirmedBoardSnapshot(confirmedBoardSnapshot);
            }
            return;
        }

        long applyStepTicks = Stopwatch.GetTimestamp();
        void MarkApplyStep(string step)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedMs = (nowTicks - applyStepTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= 250)
            {
                LogDiag("FEN", $"apply confirmed step slow: {step} took {elapsedMs:F0}ms source={logPrefix} board={GetBoardPosition(confirmedFen)}");
            }
            applyStepTicks = nowTicks;
        }

        Volatile.Write(ref _pendingConfirmedFenTarget, confirmedFen);
        ResetAnalysisSchedulingState();
        _currentFenIsAnalysisBoard = isAnalysisBoardSource;
        TraceBoard($"apply confirmed source={(isAnalysisBoardSource ? "test" : "external")} board={GetBoardPosition(confirmedFen)} flipped={_externalBoardDetectedFlipped}");
        var oldParts = _currentFEN.Split(' ');
        var newParts = confirmedFen.Split(' ');

        var oldPos = oldParts.Length > 0 ? oldParts[0] : "";
        var newPos = newParts.Length > 0 ? newParts[0] : "";
        bool boardPositionChanged = !string.IsNullOrEmpty(_currentFEN) && oldPos != newPos;
        bool acceptedExternalBoardSwitch = IsAcceptedExternalBoardSwitchFen(confirmedFen);
        bool turnStateAuthoritativeFromTransition = false;

        if (boardPositionChanged)
        {
            ClearDisplayedArrowsForPositionChange();
            MarkApplyStep("pre-position-change arrow clear");

            bool suppressRecentStateRestore = HasFreshGameResetCandidate() || IsLikelyFreshOpeningPosition(confirmedFen);
            // A transition INTO the initial board position (standard or
            // rotated) is, by itself, a fresh-game signal. Without this, the
            // suppress-recent-state-restore gate was preventing the reset
            // when starting a new game right after a previous one finished -
            // which left _externalTrackedPositionCount and the cached
            // orientation from the prior game in place. That manifested as
            // arrows pointing the wrong way (e.g. pawns "moving backward")
            // when switching color between games.
            bool isFreshGameStart = IsInitialBoardPosition(confirmedFen);
            char? freshGameMover = null;
            ConfirmedPositionState? recentState = FindRecentConfirmedState(newPos);
            bool isReturnToRecent = !suppressRecentStateRestore && recentState != null;
            bool returnedToUserMovePosition = IsReturnToUserMovePosition(newPos);
            MarkApplyStep("recent-state lookup");
            int legalTransitionMaxPlies = ShouldUseFastDetectionRecovery() ? RemoteFastConfirmMaxPlies : 1;
            LegalTurnTransition? legalTurnTransition = TryDetermineExternalTurnTransitionByLegalPath(_currentFEN, confirmedFen, legalTransitionMaxPlies);
            MarkApplyStep("legal transition lookup");
            char? legalTransitionMover = legalTurnTransition?.LastMover;
            char? strictDetectedMover = legalTransitionMover ?? DetectWhoMoved(_currentFEN, confirmedFen);
            if (suppressRecentStateRestore &&
                !isFreshGameStart &&
                !strictDetectedMover.HasValue)
            {
                freshGameMover = DetectWhoMoved($"{InitialBoardPosition} w - - 0 1", confirmedFen);
            }
            MarkApplyStep("strict mover detection");
            string highlightObservedMoveSummary = "";
            bool highlightSupportsObservedMove =
                strictDetectedMover.HasValue &&
                TryLastMoveHighlightSupportsObservedTransition(
                    _currentFEN,
                    confirmedFen,
                    strictDetectedMover.Value,
                    confirmedBoardSnapshot,
                    out highlightObservedMoveSummary);
            MarkApplyStep("last-move highlight check");
            if (highlightSupportsObservedMove)
            {
                LogDiag("HILITE", $"last-move highlight supports observed transition {highlightObservedMoveSummary}");
            }

            if (!acceptedExternalBoardSwitch &&
                allowOutOfTurnHold &&
                ShouldRejectOutOfTurnExternalObservation(
                        confirmedFen,
                        strictDetectedMover,
                        recentState,
                        legalTurnTransition?.PlyCount > 1,
                        highlightSupportsObservedMove))
            {
                TraceBoard($"rejected transient out-of-turn observation mover={strictDetectedMover} expected={_inferredSideToMove} board={newPos}");
                LogDiag(
                    "TURN",
                    $"rejected transient out-of-turn observation mover={strictDetectedMover} expected={_inferredSideToMove} board={newPos}");
                Volatile.Write(ref _pendingConfirmedFenTarget, "");
                return;
            }
            else if (!allowOutOfTurnHold &&
                     strictDetectedMover.HasValue &&
                     strictDetectedMover.Value != _inferredSideToMove)
            {
                TraceBoard($"accepted fast legal transition despite stale expected turn mover={strictDetectedMover} expected={_inferredSideToMove} board={newPos}");
                LogDiag("TURN", $"fast legal transition bypassed out-of-turn hold mover={strictDetectedMover} expected={_inferredSideToMove} board={newPos}");
            }
            MarkApplyStep("out-of-turn rejection check");

            ResetOutOfTurnCandidate();
            char? whoMoved = DetermineMoveOwner(_currentFEN, confirmedFen, legalTransitionMover);
            MarkApplyStep("move owner detection");

            if (acceptedExternalBoardSwitch)
            {
                char sideToMove = GetSideToMove(confirmedFen) ?? (_inferredSideToMove == 'b' ? 'b' : 'w');
                Log($"[{DateTime.Now:HH:mm:ss}] External board switch detected");
                _externalTrackedPositionCount = 0;
                _inferredSideToMove = sideToMove;
                _waitingForOpponentMove = !_analysisBothEnabled && _inferredSideToMove != GetRequestedAnalysisColor(confirmedFen);
                _lastUserMoveFEN = "";
                _lastArrowSourceFEN = "";
                _currentMoveArrows = null;
                _lastAnalysisVariations = null;
                turnStateAuthoritativeFromTransition = true;
                RefreshDebugView("External board switch detected");
                if (_waitingForOpponentMove)
                {
                    ClearExternalArrows();
                }
            }
            else if (isFreshGameStart)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Fresh game reset detected");
                // Don't unlock orientation here - we may have just pinned it
                // from a raw initial-position match upstream. If orientation
                // was NOT pinned, leave it as-is (whatever the previous
                // detection produced) and let the next position re-evaluate.
                // The position counter reset is what matters for the
                // history-vs-fresh-evaluation gate.
                _externalTrackedPositionCount = 0;
                _inferredSideToMove = 'w';
                // Don't preemptively set waiting-for-opponent based purely on
                // requested color here. That assumption is correct for real
                // game starts but causes puzzles to get stuck in waiting
                // state when a transient initial-position pattern fires
                // during context transitions. Real game starts will pick up
                // the wait state correctly via DetectWhoMoved on the next
                // observed move.
                _waitingForOpponentMove = false;
                _lastArrowSourceFEN = "";
                // Clear last user move FEN so HasReliableOrientationHistory
                // doesn't keep returning true based on stale game-1 state and
                // suppress fresh orientation evaluation.
                _lastUserMoveFEN = "";
                _freshGameResetUntilUtc = DateTime.MinValue;
                turnStateAuthoritativeFromTransition = true;
                RefreshDebugView("Fresh game reset detected");
                if (_waitingForOpponentMove)
                {
                    ClearExternalArrows();
                }
            }
            else if (freshGameMover.HasValue)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Fresh game follow-up move by {(freshGameMover.Value == 'w' ? "WHITE" : "BLACK")}");
                _externalOrientationLockedForCurrentGame = false;
                _externalTrackedPositionCount = 1;
                _inferredSideToMove = freshGameMover.Value == 'w' ? 'b' : 'w';
                _waitingForOpponentMove = !_analysisBothEnabled && _inferredSideToMove != GetRequestedAnalysisColor(confirmedFen);
                _lastArrowSourceFEN = "";

                // If WE made this move, also clear cached arrow data so
                // stale arrows from before the move don't briefly reappear
                // when the wait flag lifts after the opponent's reply. The
                // whoMoved.HasValue branch below does the same; this branch
                // was missing it which caused the "arrows flash again right
                // after my move" glitch on the first 2-3 plies of a game.
                if (!_analysisBothEnabled && freshGameMover.Value == _userColor)
                {
                    _lastUserMoveFEN = confirmedFen;
                    _currentMoveArrows = null;
                    _lastAnalysisVariations = null;
                    ClearActiveArrows();
                }

                _freshGameResetUntilUtc = DateTime.MinValue;
                turnStateAuthoritativeFromTransition = true;
                RefreshDebugView($"Fresh game move by {(freshGameMover.Value == 'w' ? "WHITE" : "BLACK")}");
                if (_waitingForOpponentMove)
                {
                    ClearExternalArrows();
                }
            }
            else if (returnedToUserMovePosition)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Returned to post-user-move position");
                _waitingForOpponentMove = !_analysisBothEnabled;
                _inferredSideToMove = _userColor == 'w' ? 'b' : 'w';
                _lastArrowSourceFEN = "";
                turnStateAuthoritativeFromTransition = true;
                RefreshDebugView("Returned to post-user-move position");
                if (_waitingForOpponentMove)
                {
                    ClearExternalArrows();
                }
            }
            else if (!suppressRecentStateRestore && TryRestoreRecentState(recentState, confirmedFen))
            {
                // Prefer a known-position restore before move-owner inference.
                // Streamed boards often show a piece being lifted, hovered, and
                // placed back on its original square. That temporary frame can
                // look like a legal move and then a second "move back" by the
                // same side, which would incorrectly flip the inferred turn.
                // If the board position is one we already confirmed, reuse its
                // saved side-to-move / waiting state instead.
                Log($"[{DateTime.Now:HH:mm:ss}] Restored recent state for repeated position");
                RefreshDebugView("Restored recent state for repeated position");
                if (_analysisBothEnabled)
                {
                    _waitingForOpponentMove = false;
                }
                turnStateAuthoritativeFromTransition = true;
            }
            else if (whoMoved.HasValue)
            {
                ArrowTimeline.Log("MOVE_DETECTED", fen: confirmedFen, extra: $"by={(whoMoved.Value == 'w' ? "white" : "black")} user={(whoMoved.Value == _userColor)} both={_analysisBothEnabled}");
                Log($"[{DateTime.Now:HH:mm:ss}] Move by {(whoMoved.Value == 'w' ? "WHITE" : "BLACK")}");
                RefreshDebugView($"Move by {(whoMoved.Value == 'w' ? "WHITE" : "BLACK")}");
                _inferredSideToMove = whoMoved.Value == 'w' ? 'b' : 'w';

                if (_analysisBothEnabled)
                {
                    _waitingForOpponentMove = false;
                }
                else if (whoMoved.Value == _userColor)
                {
                    _waitingForOpponentMove = true;
                    _lastUserMoveFEN = confirmedFen;
                    // Suppress the per-frame cached-arrow recovery BEFORE the
                    // overlay is released. The recovery loop reads these fields
                    // without a lock, and in the window between this clear and
                    // the wait state settling it used to re-surface the old
                    // arrows for a few frames (visible as a brief flash-back
                    // right after the user's own move).
                    _suppressCachedArrowRecoveryUntilUtc = DateTime.UtcNow.AddMilliseconds(CachedArrowRecoverySuppressAfterPositionClearMs);
                    _currentMoveArrows = null;
                    _lastAnalysisVariations = null;
                    _lastArrowSourceFEN = "";
                    ClearActiveArrows("own move confirmed - waiting for opponent");
                }
                else
                {
                    _waitingForOpponentMove = false;
                }
                turnStateAuthoritativeFromTransition = true;
            }
            else if (!IsActiveAnalysisBoardFen(confirmedFen))
            {
                if (_analysisBothEnabled)
                {
                    _inferredSideToMove = _inferredSideToMove == 'w' ? 'b' : 'w';
                    _waitingForOpponentMove = false;

                    Log($"[{DateTime.Now:HH:mm:ss}] Ambiguous external move - parity fallback side to move: {_inferredSideToMove}");
                    RefreshDebugView($"Parity fallback side: {_inferredSideToMove}");
                    turnStateAuthoritativeFromTransition = true;
                }
                else
                {
                    _inferredSideToMove = _inferredSideToMove == 'w' ? 'b' : 'w';
                    if (_analysisBothEnabled)
                    {
                        _waitingForOpponentMove = false;
                    }

                    Log($"[{DateTime.Now:HH:mm:ss}] Fallback toggled inferred side to move: {_inferredSideToMove}");
                    RefreshDebugView($"Fallback inferred side: {_inferredSideToMove}");
                    turnStateAuthoritativeFromTransition = true;
                }
            }

            RecordExternalConfirmedMoveForBlitzAuto(
                isFreshGameStart,
                legalTurnTransition,
                strictDetectedMover,
                confirmedFen);
            MarkApplyStep("turn state update/record");

            HandlePositionChange(_currentFEN, confirmedFen);
            MarkApplyStep("position-change handling");
        }

        _currentFEN = confirmedFen;
        Volatile.Write(ref _pendingConfirmedFenTarget, "");
        if (!IsActiveAnalysisBoardFen(confirmedFen))
            _externalTrackedPositionCount = Math.Min(_externalTrackedPositionCount + 1, 1024);
        if (IsActiveAnalysisBoardFen(confirmedFen))
        {
            char? authoritativeSide = GetSideToMove(confirmedFen);
            if (authoritativeSide.HasValue)
            {
                _inferredSideToMove = authoritativeSide.Value;
            }
        }
        else
        {
            if (turnStateAuthoritativeFromTransition)
            {
                LogTurnInference(
                    $"kept confirmed transition side-to-move={_inferredSideToMove} board={GetBoardPosition(confirmedFen)}");
            }
            else
            {
                UpdateInferredSideToMoveForExternalBoard(confirmedFen);
            }
            TryApplyStaticLastMoveHighlightTurnHint(confirmedFen, confirmedBoardSnapshot);
        }
        MarkApplyStep("turn inference/static highlight");
        _lastConfirmedFenForTiming = confirmedFen;
        _lastConfirmedFenAtUtc = DateTime.UtcNow;
        _lastExternalAnalysisFenHeartbeatUtc = DateTime.MinValue;
        ArrowTimeline.Log("FEN_CONFIRMED", fen: confirmedFen, extra: $"waiting={_waitingForOpponentMove} side={_inferredSideToMove}");

        // === LATENCY DIAG: log T1-T0 (detection ? FEN confirm) ===
        // This is "how long from we-saw-something-change to we-know-the-new-position".
        // Includes per-frame debouncing + ProcessBoard cost.
        if (_diagLoggingEnabled && _latencyT0Utc != DateTime.MinValue)
        {
            try
            {
                double t1MinusT0Ms = (_lastConfirmedFenAtUtc - _latencyT0Utc).TotalMilliseconds;
                string logLine = $"{DateTime.Now:HH:mm:ss.fff} [LATENCY] T1-T0 (detect?confirm) = {t1MinusT0Ms:F0}ms (T0 saw {_latencyT0ChangedSquares} changed squares){Environment.NewLine}";
                AppendDiagnosticLine(logLine);
            }
            catch { }
        }

        if (confirmedBoardSnapshot != null)
        {
            UpdateConfirmedBoardSnapshot(confirmedBoardSnapshot);
        }
        else
        {
            ResetConfirmedBoardSnapshot();
        }
        MarkApplyStep("confirmed snapshot update");

        _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;

        if (beginNoiseGuard)
        {
            BeginTransitionNoiseGuard(GetPostConfirmedPositionNoiseIgnoreMs());
        }

        ApplyWaitingStateForCurrentPosition();
        RememberConfirmedState(_currentFEN);
        RefreshDebugView($"FEN confirmed: {confirmedFen}");
        MarkApplyStep("waiting-state/history");

        LogDiag("FEN", $"apply checkpoint before terminal probe board={GetBoardPosition(_currentFEN)}");
        if (TryHandleTerminalExternalPosition(_currentFEN))
        {
            MarkApplyStep("terminal-position check");
            Log($"{logPrefix} {confirmedFen}");
            if (acceptedExternalBoardSwitch)
                ClearAcceptedExternalBoardSwitch();
            return;
        }
        MarkApplyStep("terminal-position check");
        // force:true - HandlePositionChange above defers next analysis by
        // 350ms (to flush any in-flight engine work), but that defer must
        // not block the analysis of the just-confirmed FEN. Without force,
        // arrows arrive ~350ms late on every opponent move.
        LogDiag("FEN", $"apply checkpoint before queue analysis board={GetBoardPosition(_currentFEN)}");
        TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
        MarkApplyStep("queue analysis");
        Log($"{logPrefix} {confirmedFen}");
        if (acceptedExternalBoardSwitch)
            ClearAcceptedExternalBoardSwitch();
    }

    private static void ResetSuspectEmptyFenFrames()
    {
        _suspectEmptyFenFrames = 0;
    }

    private static string GetAnalysisRequestKey(string fen, bool isBlackPerspective)
    {
        bool effectiveIsBlackPerspective = GetRequestedAnalysisPerspective(fen, isBlackPerspective);
        bool effectiveBoardFlipped = GetEffectiveBoardFlipped(fen);
        return $"{(_analysisBothEnabled ? 'x' : 's')}|{(effectiveIsBlackPerspective ? 'b' : 'w')}|{(effectiveBoardFlipped ? '1' : '0')}|{fen}";
    }

    private sealed class OrientationAssessment
    {
        public int StandardScore { get; init; }
        public int FlippedScore { get; init; }
        public int StandardConflicts { get; init; }
        public int FlippedConflicts { get; init; }
        public int ScoreDelta => FlippedScore - StandardScore;
        public int AbsoluteScoreDelta => Math.Abs(ScoreDelta);
        public bool IsAmbiguous => AbsoluteScoreDelta < 40 && StandardConflicts == FlippedConflicts;
    }

    private sealed class LastMoveHighlightHint
    {
        public HashSet<string> Squares { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public double Confidence { get; init; }
        public string Summary { get; init; } = "";
    }

    private static void TraceBoard(string message)
    {
        if (_boardTraceEnabled)
        {
            Log($"[BOARD TRACE] {message}");
            LogDiag("BOARD", message);
        }
    }

    private static void LogStaleExternalArrowObservation(
        string source,
        BoardVisionDetector.BoardPixelChangeInfo? pixelChange = null,
        BoardVisionDetector.BoardDiffInfo? boardDiff = null,
        string? observedFen = null,
        bool forceLog = false)
    {
        if (!_continuousAnalysisEnabled ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            !HasDisplayedOrCachedExternalArrows())
        {
            return;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        if (string.IsNullOrWhiteSpace(currentBoard))
        {
            return;
        }

        string arrowBoard = !string.IsNullOrWhiteSpace(_lastArrowSourceFEN)
            ? GetBoardPosition(_lastArrowSourceFEN)
            : GetBoardPosition(_externalOverlayArrowsFen);

        DateTime now = DateTime.UtcNow;
        DateTime visibleSinceUtc = GetExternalVisibleArrowSinceUtc();
        if (visibleSinceUtc == DateTime.MinValue)
            visibleSinceUtc = _lastConfirmedFenAtUtc;
        if (visibleSinceUtc == DateTime.MinValue)
            return;

        double visibleMs = (now - visibleSinceUtc).TotalMilliseconds;
        if (visibleMs < StaleExternalArrowDiagMinVisibleMs ||
            (!forceLog && now < _lastStaleExternalArrowDiagUtc.AddMilliseconds(StaleExternalArrowDiagIntervalMs)))
        {
            return;
        }

        _lastStaleExternalArrowDiagUtc = now;

        string observedBoard = string.IsNullOrWhiteSpace(observedFen)
            ? ""
            : GetBoardPosition(observedFen);
        string observedText = string.IsNullOrWhiteSpace(observedBoard)
            ? "none"
            : observedBoard;
        string arrowText = string.IsNullOrWhiteSpace(arrowBoard)
            ? "unknown"
            : arrowBoard;
        string pixelText = pixelChange == null
            ? "none"
            : $"cmp={pixelChange.IsComparable},changed={pixelChange.HasMeaningfulChange},px={pixelChange.ChangedPixels},ratio={pixelChange.ChangedRatio:F4},xor={pixelChange.MeanXor:F2}";
        string diffText = boardDiff == null
            ? "none"
            : $"squares={boardDiff.ChangedSquares},avg={boardDiff.AverageSquareDifference:F2},max={boardDiff.MaxSquareDifference:F2}";
        string trackedRectText = _lastTrackedBox.HasValue
            ? $"{_lastTrackedBox.Value.X},{_lastTrackedBox.Value.Y} {_lastTrackedBox.Value.Width}x{_lastTrackedBox.Value.Height}"
            : "none";
        string hwndText = _trackedHwnd == IntPtr.Zero
            ? "none"
            : $"0x{_trackedHwnd.ToInt64():X}";

        IntPtr foreground = IntPtr.Zero;
        try { foreground = WindowTracker.GetForegroundWindow(); } catch { }
        string foregroundText = foreground == IntPtr.Zero
            ? "none"
            : $"0x{foreground.ToInt64():X}";

        var metrics = BoardVisionDetector.GetNetworkMetricsSnapshot();
        int arrowCount = _currentMoveArrows?.Count ?? _externalOverlayArrowsCount;
        string message =
            $"arrows visible on unchanged external board for {visibleMs:F0}ms " +
            $"source={source} side={(_externalArrowPerspectiveBlack ? "b" : "w")} " +
            $"arrows={arrowCount} current={currentBoard} arrow={arrowText} observed={observedText} " +
            $"pixel={pixelText} diff={diffText} " +
            $"tracked={trackedRectText} hwnd={hwndText} foreground={foregroundText} " +
            $"vision={metrics.Transport},pkts={metrics.PacketCount},kbps={metrics.KilobytesPerSecond:F1},avgKb={metrics.AveragePacketKilobytes:F1}";

        Log($"[STALE] {message}");
        LogDiag("STALE", message);
    }

    private static bool ShouldForceStaleExternalFenProbe(
        string source,
        BoardVisionDetector.BoardPixelChangeInfo? pixelChange = null,
        BoardVisionDetector.BoardDiffInfo? boardDiff = null)
    {
        if (!_continuousAnalysisEnabled ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            !HasDisplayedOrCachedExternalArrows())
        {
            return false;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        if (string.IsNullOrWhiteSpace(currentBoard))
        {
            return false;
        }

        // A visible arrow can be perfectly valid for several seconds while a
        // player thinks. Only force the expensive remote/FEN read when the
        // local capture path has actual evidence that the board changed.
        bool hasVisualChangeEvidence =
            boardDiff is { ChangedSquares: > 0 } ||
            pixelChange is { IsComparable: true, HasMeaningfulChange: true };
        if (!hasVisualChangeEvidence)
        {
            LogStaleExternalArrowObservation(
                $"{source}-no-change",
                pixelChange: pixelChange,
                boardDiff: boardDiff);
            return false;
        }

        DateTime now = DateTime.UtcNow;
        DateTime visibleSinceUtc = GetExternalVisibleArrowSinceUtc();
        if (visibleSinceUtc == DateTime.MinValue)
            visibleSinceUtc = _lastConfirmedFenAtUtc;
        if (visibleSinceUtc == DateTime.MinValue ||
            (now - visibleSinceUtc).TotalMilliseconds < StaleExternalArrowDiagMinVisibleMs ||
            now < _lastStaleExternalFenProbeUtc.AddMilliseconds(StaleExternalFenProbeIntervalMs))
        {
            return false;
        }

        _lastStaleExternalFenProbeUtc = now;
        LogStaleExternalArrowObservation(
            $"{source}-force-fen",
            pixelChange: pixelChange,
            boardDiff: boardDiff,
            forceLog: true);
        return true;
    }

    private static bool ShouldForceExternalAnalysisFenHeartbeat(string source)
    {
        if (!_continuousAnalysisEnabled ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            !_lastTrackedBox.HasValue ||
            _orientationPromptVisible ||
            _menuExpanded)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        if (_lastConfirmedFenAtUtc == DateTime.MinValue)
            return false;

        double confirmedAgeMs = (now - _lastConfirmedFenAtUtc).TotalMilliseconds;
        if (confirmedAgeMs < ExternalAnalysisFenHeartbeatMinConfirmedAgeMs ||
            now < _lastExternalAnalysisFenHeartbeatUtc.AddMilliseconds(ExternalAnalysisFenHeartbeatIntervalMs))
        {
            return false;
        }

        DateTime visibleSinceUtc = GetExternalVisibleArrowSinceUtc();
        bool hasCurrentArrows =
            _showingMoves &&
            ((_currentMoveArrows != null && _currentMoveArrows.Count > 0) || _externalOverlayArrowsCount > 0) &&
            IsSameArrowSourcePosition(_currentFEN) &&
            visibleSinceUtc != DateTime.MinValue;

        if (hasCurrentArrows)
        {
            return false;
        }

        _lastExternalAnalysisFenHeartbeatUtc = now;

        string arrowAgeText = visibleSinceUtc == DateTime.MinValue
            ? "none"
            : $"{(now - visibleSinceUtc).TotalMilliseconds:F0}ms";
        int arrowCount = _currentMoveArrows?.Count ?? _externalOverlayArrowsCount;
        var metrics = BoardVisionDetector.GetNetworkMetricsSnapshot();

        Log(
            $"[FEN] analysis heartbeat forcing external FEN probe " +
            $"source={source} board={GetBoardPosition(_currentFEN)} confirmedAge={confirmedAgeMs:F0}ms " +
            $"showing={_showingMoves} arrows={arrowCount} arrowAge={arrowAgeText} " +
            $"vision={metrics.Transport},pkts={metrics.PacketCount},kbps={metrics.KilobytesPerSecond:F1}");

        return true;
    }

    private static char GetOrientationPromptReferenceColor(string fen)
    {
        string boardPosition = GetBoardPosition(fen);
        char requestedColor = 'w';

        if (_continuousAnalysisEnabled)
            requestedColor = _analysisBothEnabled ? 'w' : GetRequestedAnalysisColor(fen);

        char otherColor = requestedColor == 'b' ? 'w' : 'b';

        if (BoardPositionHasReferencePawn(boardPosition, requestedColor))
            return requestedColor;

        if (BoardPositionHasReferencePawn(boardPosition, otherColor))
            return otherColor;

        return requestedColor;
    }

    private static bool HasReliableOrientationHistory(string boardPosition, bool isAnalysisBoard)
    {
        if (isAnalysisBoard)
            return _analysisBoardHasTrackedHistory;

        if (_externalOrientationLockedForCurrentGame)
            return true;

        if (_externalTrackedPositionCount >= 4)
            return true;

        return !string.IsNullOrWhiteSpace(_lastUserMoveFEN);
    }

    private static void TouchLruEntry(
        string key,
        Dictionary<string, LinkedListNode<string>> nodes,
        LinkedList<string> order)
    {
        if (nodes.TryGetValue(key, out LinkedListNode<string>? node))
        {
            if (node?.List == order)
            {
                order.Remove(node);
                order.AddLast(node);
            }
            else
            {
                nodes[key] = order.AddLast(key);
            }
        }
    }

    private static void AddOrUpdateLruEntry(
        string key,
        Dictionary<string, LinkedListNode<string>> nodes,
        LinkedList<string> order,
        int limit,
        Action<string>? onEvicted = null)
    {
        if (nodes.TryGetValue(key, out LinkedListNode<string>? existingNode))
        {
            if (existingNode?.List == order)
            {
                order.Remove(existingNode);
                order.AddLast(existingNode);
            }
            else
            {
                nodes[key] = order.AddLast(key);
            }
        }
        else
        {
            LinkedListNode<string> node = order.AddLast(key);
            nodes[key] = node;
        }

        while (order.Count > limit)
        {
            LinkedListNode<string>? oldest = order.First;
            if (oldest == null)
                break;

            order.RemoveFirst();
            nodes.Remove(oldest.Value);
            onEvicted?.Invoke(oldest.Value);
        }
    }

    private static bool IsOrientationPromptDismissed(string boardPosition)
    {
        if (_dismissedOrientationPrompts.Contains(boardPosition))
        {
            TouchLruEntry(boardPosition, _dismissedOrientationPromptNodes, _dismissedOrientationPromptOrder);
            return true;
        }

        return false;
    }

    private static void RememberDismissedOrientationPrompt(string boardPosition)
    {
        if (string.IsNullOrWhiteSpace(boardPosition))
            return;

        _dismissedOrientationPrompts.Add(boardPosition);
        AddOrUpdateLruEntry(
            boardPosition,
            _dismissedOrientationPromptNodes,
            _dismissedOrientationPromptOrder,
            DismissedOrientationPromptLimit,
            evicted => _dismissedOrientationPrompts.Remove(evicted));
    }

    private static void RemoveDismissedOrientationPrompt(string boardPosition)
    {
        if (string.IsNullOrWhiteSpace(boardPosition))
            return;

        _dismissedOrientationPrompts.Remove(boardPosition);
        if (_dismissedOrientationPromptNodes.TryGetValue(boardPosition, out LinkedListNode<string>? node))
        {
            _dismissedOrientationPromptNodes.Remove(boardPosition);
            if (node?.List == _dismissedOrientationPromptOrder)
            {
                _dismissedOrientationPromptOrder.Remove(node);
            }
        }
    }

    private static void RememberManualOrientationOverride(string boardPosition, bool flipped)
    {
        if (string.IsNullOrWhiteSpace(boardPosition))
            return;

        void remember(string key, bool value)
        {
            _manualOrientationOverrides[key] = value;
            AddOrUpdateLruEntry(
                key,
                _manualOrientationOverrideNodes,
                _manualOrientationOverrideOrder,
                ManualOrientationOverrideLimit,
                evicted => _manualOrientationOverrides.Remove(evicted));
        }

        remember(boardPosition, flipped);

        string rotatedBoardPosition = Rotate180(boardPosition);
        if (!string.Equals(rotatedBoardPosition, boardPosition, StringComparison.Ordinal))
        {
            // The prompt answer describes the display orientation on screen,
            // not a transform local to one raw board string. The same physical
            // board may later be observed as the 180-rotated raw position, but
            // the user's chosen on-screen pawn direction is still the same.
            remember(rotatedBoardPosition, flipped);
        }
    }

    private static bool TryGetManualOrientationOverride(string boardPosition, out bool flipped)
    {
        if (_manualOrientationOverrides.TryGetValue(boardPosition, out flipped))
        {
            TouchLruEntry(boardPosition, _manualOrientationOverrideNodes, _manualOrientationOverrideOrder);
            return true;
        }

        return false;
    }

    private static void RememberRecentOrientationDecision(bool flipped, bool isAnalysisBoard, char referenceColor)
    {
        _recentOrientationDecisionFlipped = flipped;
        _recentOrientationDecisionIsAnalysisBoard = isAnalysisBoard;
        _recentOrientationDecisionReferenceColor = referenceColor;
        _recentOrientationDecisionUntilUtc = DateTime.UtcNow.AddMilliseconds(RecentOrientationDecisionWindowMs);
    }

    private static bool TryGetRecentOrientationDecision(string boardPosition, bool isAnalysisBoard, char referenceColor, out bool flipped)
    {
        flipped = false;

        if (string.IsNullOrWhiteSpace(boardPosition) || !IsSparseExternalBoardPosition(boardPosition))
            return false;

        if (_recentOrientationDecisionUntilUtc == DateTime.MinValue || DateTime.UtcNow > _recentOrientationDecisionUntilUtc)
            return false;

        if (_recentOrientationDecisionIsAnalysisBoard != isAnalysisBoard)
            return false;

        if (_recentOrientationDecisionReferenceColor != referenceColor)
            return false;

        flipped = _recentOrientationDecisionFlipped;
        return true;
    }

    private static bool IsAwaitingOrientationDecision(string boardPosition, bool isAnalysisBoard)
    {
        if (!_orientationPromptVisible || _pendingOrientationPromptIsAnalysisBoard != isAnalysisBoard)
            return false;

        if (!string.IsNullOrWhiteSpace(boardPosition))
            _pendingOrientationPromptObservedBoards.Add(boardPosition);

        return !string.IsNullOrWhiteSpace(_pendingOrientationPromptBoardPosition);
    }

    private static bool CanPromptForOrientation(string boardPosition, bool isAnalysisBoard)
    {
        if (string.IsNullOrWhiteSpace(boardPosition))
            return false;

        if (!isAnalysisBoard && IsEmptyBoardPosition(boardPosition))
            return false;

        if (!isAnalysisBoard)
        {
            if (_boardLostFrames > 0 || _requestBoardRefresh || _invalidFenFrames > 0)
                return false;

            if (DateTime.UtcNow < _externalBoardGeometryUnstableUntilUtc)
                return false;
        }

        if (IsOrientationPromptDismissed(boardPosition))
            return false;

        if (IsAwaitingOrientationDecision(boardPosition, isAnalysisBoard))
            return false;

        return true;
    }

    private static Rectangle? GetOrientationPromptAnchorRect(bool isAnalysisBoard)
    {
        if (isAnalysisBoard)
        {
            if (!_analysisBoardScreenRect.IsEmpty)
                return _analysisBoardScreenRect;

            if (!_analysisBoardWindowScreenRect.IsEmpty)
                return _analysisBoardWindowScreenRect;
        }
        else if (_lastTrackedBox.HasValue)
        {
            Rect tracked = _lastTrackedBox.Value;
            return new Rectangle(tracked.X, tracked.Y, tracked.Width, tracked.Height);
        }

        return _settingsToolbar is { Visible: true } ? _settingsToolbar.Bounds : null;
    }

    private static bool HasUnresolvedOrientationPromptForCurrentPosition(string? fen = null)
    {
        if (!_orientationPromptVisible || string.IsNullOrWhiteSpace(_pendingOrientationPromptBoardPosition))
            return false;

        string targetFen = string.IsNullOrWhiteSpace(fen) ? _currentFEN : fen;
        if (string.IsNullOrWhiteSpace(targetFen))
            return false;

        return string.Equals(
            GetBoardPosition(targetFen),
            _pendingOrientationPromptBoardPosition,
            StringComparison.Ordinal);
    }

    private static void RequestOrientationPrompt(string boardPosition, char referenceColor, bool isAnalysisBoard)
    {
        if (!CanPromptForOrientation(boardPosition, isAnalysisBoard))
            return;

        _pendingOrientationPromptBoardPosition = boardPosition;
        _pendingOrientationPromptReferenceColor = referenceColor;
        _orientationPromptVisible = true;
        _pendingOrientationPromptIsAnalysisBoard = isAnalysisBoard;
        _pendingOrientationPromptObservedBoards.Clear();
        _pendingOrientationPromptObservedBoards.Add(boardPosition);

        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        if (_overlay != null)
        {
            int generation = ++_arrowDisplayGeneration;
            _overlay.BeginInvoke(new Action(() =>
            {
                _overlay.HideArrows(generation, preserveFreeLimitWatermark: false);
                _showingMoves = false;
            }));
        }
        else
        {
            _showingMoves = false;
        }
        CancelPendingAnalysis("orientation prompt");
        if (isAnalysisBoard)
            _analysisBoardController!.SetAnalysisBoardAnalysisStatus("Choose which way the selected pawns move.");

        TraceBoard($"prompt requested board={boardPosition} color={(referenceColor == 'b' ? "black" : "white")} source={(isAnalysisBoard ? "analysis" : "external")}");
        SetObstructingUiActive(true);

        if (_orientationPromptHost != null)
        {
            Rectangle? anchorRect = GetOrientationPromptAnchorRect(isAnalysisBoard);
            TraceBoard($"prompt anchor={(anchorRect.HasValue ? anchorRect.Value.ToString() : "null")} source={(isAnalysisBoard ? "analysis" : "external")}");
            _orientationPromptHost.ShowPrompt(referenceColor, anchorRect);
        }
    }

    private static void OnOrientationPromptDirectionChosen(bool pawnsMoveUp)
    {
        string boardPosition = _pendingOrientationPromptBoardPosition;
        char referenceColor = _pendingOrientationPromptReferenceColor;
        bool isAnalysisBoard = _pendingOrientationPromptIsAnalysisBoard;

        if (string.IsNullOrWhiteSpace(boardPosition))
            return;

        bool flipped = referenceColor == 'w' ? !pawnsMoveUp : pawnsMoveUp;
        RememberRecentOrientationDecision(flipped, isAnalysisBoard, referenceColor);
        foreach (string observedBoardPosition in _pendingOrientationPromptObservedBoards.ToArray())
        {
            RememberManualOrientationOverride(observedBoardPosition, flipped);
            RemoveDismissedOrientationPrompt(observedBoardPosition);
        }

        RememberManualOrientationOverride(boardPosition, flipped);
        RemoveDismissedOrientationPrompt(boardPosition);
        _orientationPromptVisible = false;
        _pendingOrientationPromptBoardPosition = "";
        _pendingOrientationPromptReferenceColor = 'w';
        _pendingOrientationPromptIsAnalysisBoard = false;
        _pendingOrientationPromptObservedBoards.Clear();
        if (!isAnalysisBoard)
        {
            _externalBoardDetectedFlipped = flipped;
            _externalOrientationLockedForCurrentGame = true;
        }

        _orientationPromptHost?.Hide();
        SetObstructingUiActive(false);

        TraceBoard($"prompt accepted board={boardPosition} color={(referenceColor == 'b' ? "black" : "white")} up={pawnsMoveUp} flipped={flipped} source={(isAnalysisBoard ? "analysis" : "external")}");
        CancelPendingAnalysis("orientation chosen");

        if (isAnalysisBoard)
        {
            lock (_analysisBoardStateLock)
            {
                _analysisBoardIsFlipped = flipped;
            }

            if (_analysisBoardForm != null)
            {
                if (_analysisBoardForm.InvokeRequired)
                    _analysisBoardForm.BeginInvoke(new Action(() => _analysisBoardForm.SetBoardFlipped(flipped)));
                else
                    _analysisBoardForm.SetBoardFlipped(flipped);
            }

            _analysisBoardController!.SetAnalysisBoardAnalysisStatus("Waiting for engine lines...");
            _analysisBoardController!.TryQueueAnalysisBoardAnalysis(_analysisBoardController!.AnalysisSessionVersion);
        }
        else if (!_currentFenIsAnalysisBoard && !string.IsNullOrWhiteSpace(_currentFEN) &&
            string.Equals(GetBoardPosition(_currentFEN), boardPosition, StringComparison.Ordinal))
        {
            string correctedFen = flipped ? Rotate180(_currentFEN) : _currentFEN;
            _currentFEN = correctedFen;
            RefreshDisplayedArrows();
            ResetAnalysisSchedulingState();
            bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
            TryQueueAnalysis(requestedPerspective, force: true);
            _analysisBoardController!.MirrorExternalFen(ApplyInferredExternalTurnToFen(_currentFEN), _externalBoardDetectedFlipped, force: true);
        }
    }

    private static void OnOrientationPromptDismissed()
    {
        foreach (string observedBoardPosition in _pendingOrientationPromptObservedBoards.ToArray())
        {
            RememberDismissedOrientationPrompt(observedBoardPosition);
        }

        if (!string.IsNullOrWhiteSpace(_pendingOrientationPromptBoardPosition))
        {
            RememberDismissedOrientationPrompt(_pendingOrientationPromptBoardPosition);
            TraceBoard($"prompt dismissed board={_pendingOrientationPromptBoardPosition}");
        }

        if (_pendingOrientationPromptIsAnalysisBoard)
            _analysisBoardController!.SetAnalysisBoardAnalysisStatus("Orientation unresolved. Choose Flip if needed.");

        _orientationPromptVisible = false;
        _pendingOrientationPromptBoardPosition = "";
        _pendingOrientationPromptReferenceColor = 'w';
        _pendingOrientationPromptIsAnalysisBoard = false;
        _pendingOrientationPromptObservedBoards.Clear();
        SetObstructingUiActive(false);
        CancelPendingAnalysis("orientation dismissed");
    }

    private static bool TryResolveOrientationDecision(
        string fen,
        bool isAnalysisBoard,
        char referenceColor,
        out bool? detectedBoardFlipped)
    {
        detectedBoardFlipped = null;

        string boardPosition = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(boardPosition))
            return true;

        if (!isAnalysisBoard &&
            TryInferExternalBoardFlippedFromOpposingPawnFiles(boardPosition, out int standardPawnFileConflicts, out int flippedPawnFileConflicts) is bool structuralFlipped)
        {
            detectedBoardFlipped = structuralFlipped;
            TraceBoard(
                $"orientation pawn-file invariant board={boardPosition} flipped={structuralFlipped} " +
                $"standardPawnFiles={standardPawnFileConflicts} flippedPawnFiles={flippedPawnFileConflicts} source=external");
            return true;
        }
        if (TryGetManualOrientationOverride(boardPosition, out bool manualFlipped))
        {
            detectedBoardFlipped = manualFlipped;
            return true;
        }

        if (!isAnalysisBoard && TryGetRecentOrientationDecision(boardPosition, isAnalysisBoard, referenceColor, out bool recentFlippedEarly))
        {
            detectedBoardFlipped = recentFlippedEarly;
            TraceBoard(
                $"orientation recent-carry board={boardPosition} flipped={recentFlippedEarly} color={(referenceColor == 'b' ? "black" : "white")} " +
                $"source=external");
            return true;
        }

        if (!isAnalysisBoard && _externalOrientationLockedForCurrentGame)
        {
            detectedBoardFlipped = _externalBoardDetectedFlipped;
            TraceBoard($"orientation locked board={boardPosition} flipped={detectedBoardFlipped.Value} source=external");
            return true;
        }

        bool hasReliableHistory = HasReliableOrientationHistory(boardPosition, isAnalysisBoard);
        if (hasReliableHistory)
        {
            bool historicalFlipped = isAnalysisBoard ? _analysisBoardIsFlipped : _externalBoardDetectedFlipped;

            // External-board continuity is valuable, but if a fresh
            // non-ambiguous board assessment strongly disagrees with the
            // carried-over orientation, prefer the fresh assessment. This
            // prevents stale flip history from making W+B spectator mode
            // analyze obviously mirrored positions and draw pawns backward.
            if (!isAnalysisBoard)
            {
                OrientationAssessment? continuityAssessment = AssessExternalBoardOrientation(fen);
                bool? continuityInferredFlip = TryInferExternalBoardFlipped(fen, continuityAssessment);

                if (continuityAssessment != null &&
                    continuityInferredFlip.HasValue &&
                    continuityInferredFlip.Value != historicalFlipped &&
                    (continuityAssessment.AbsoluteScoreDelta >= 60 ||
                     continuityAssessment.StandardConflicts != continuityAssessment.FlippedConflicts))
                {
                    detectedBoardFlipped = continuityInferredFlip.Value;
                    TraceBoard(
                        $"orientation continuity overridden board={boardPosition} historical={historicalFlipped} " +
                        $"fresh={continuityInferredFlip.Value} standard={continuityAssessment.StandardScore}/{continuityAssessment.StandardConflicts} " +
                        $"flipped={continuityAssessment.FlippedScore}/{continuityAssessment.FlippedConflicts} source=external");
                    return true;
                }
            }

            detectedBoardFlipped = historicalFlipped;
            TraceBoard(
                $"orientation continuity board={boardPosition} flipped={detectedBoardFlipped.Value} " +
                $"source={(isAnalysisBoard ? "analysis" : "external")}");
            return true;
        }

        if (ShouldPromptForSparseExternalOrientation(boardPosition, isAnalysisBoard, referenceColor) &&
            CanPromptForOrientation(boardPosition, isAnalysisBoard))
        {
            RequestOrientationPrompt(boardPosition, referenceColor, isAnalysisBoard);
            return false;
        }

        OrientationAssessment? assessment = AssessExternalBoardOrientation(fen);
        bool? inferredFlip = TryInferExternalBoardFlipped(fen, assessment);
        if (assessment != null)
        {
            TraceBoard(
                $"orientation raw={boardPosition} standard={assessment.StandardScore}/{assessment.StandardConflicts} " +
                $"flipped={assessment.FlippedScore}/{assessment.FlippedConflicts} chose={(inferredFlip.HasValue ? (inferredFlip.Value ? "flipped" : "standard") : "ambiguous")} " +
                $"source={(isAnalysisBoard ? "analysis" : "external")}");
        }

        if (inferredFlip.HasValue)
        {
            detectedBoardFlipped = inferredFlip.Value;
            // Lock this decision after N consecutive confident agreements,
            // which prevents per-frame oscillation while still allowing the
            // first answer to be overridden if it was a false positive from a
            // partial detection. The lock is cleared by perspective toggle,
            // F1 overlay-disable, or fresh-game-start.
            if (!isAnalysisBoard)
            {
                if (_orientationConfirmStreakFlipped == inferredFlip.Value)
                {
                    _orientationConfirmStreakCount++;
                }
                else
                {
                    _orientationConfirmStreakFlipped = inferredFlip.Value;
                    _orientationConfirmStreakCount = 1;
                }

                if (_orientationConfirmStreakCount >= OrientationLockStreakThreshold)
                {
                    _externalOrientationLockedForCurrentGame = true;
                }
            }
            return true;
        }

        if (TryGetRecentOrientationDecision(boardPosition, isAnalysisBoard, referenceColor, out bool recentFlipped))
        {
            detectedBoardFlipped = recentFlipped;
            TraceBoard(
                $"orientation recent-carry board={boardPosition} flipped={recentFlipped} color={(referenceColor == 'b' ? "black" : "white")} " +
                $"source={(isAnalysisBoard ? "analysis" : "external")}");
            return true;
        }

        bool shouldPrompt = isAnalysisBoard
            ? _analysisBoardController!.AnalysisEnabled && !string.Equals(_analysisBoardController!.AnalysisMode, "OFF", StringComparison.OrdinalIgnoreCase)
            : _continuousAnalysisEnabled;

        if (!HasReliableOrientationHistory(boardPosition, isAnalysisBoard))
        {
            if (shouldPrompt)
            {
                RequestOrientationPrompt(boardPosition, referenceColor, isAnalysisBoard);
                return false;
            }
        }

        detectedBoardFlipped = isAnalysisBoard ? _analysisBoardIsFlipped : _externalBoardDetectedFlipped;
        return true;
    }

    private static string NormalizeExternalDetectedFen(string fen, out bool? detectedBoardFlipped)
    {
        bool rawInitialPinned = TryPinExternalOrientationFromRawInitialFen(fen, out bool rawInitialFlipped);
        if (!TryResolveOrientationDecision(fen, isAnalysisBoard: false, GetOrientationPromptReferenceColor(fen), out detectedBoardFlipped))
            return fen;

        if (rawInitialPinned)
            detectedBoardFlipped = rawInitialFlipped;

        string normalizedFen = detectedBoardFlipped == true
            ? Rotate180(fen)
            : fen;
        NoteCurrentExternalBoardObservation(normalizedFen);
        return normalizedFen;
    }

    private static bool TryPinExternalOrientationFromRawInitialFen(string rawFen, out bool flipped)
    {
        flipped = _externalBoardDetectedFlipped;

        string rawBoard = GetBoardPosition(rawFen);
        if (rawBoard == InitialBoardPosition)
        {
            PinExternalBoardOrientation(false, "raw detected = standard initial position");
            flipped = false;
            return true;
        }

        if (rawBoard == InitialBoardPositionRotated)
        {
            PinExternalBoardOrientation(true, "raw detected = rotated initial position");
            flipped = true;
            return true;
        }

        return false;
    }

    private static void PinExternalBoardOrientation(bool flipped, string reason)
    {
        bool changed = !_externalOrientationLockedForCurrentGame || _externalBoardDetectedFlipped != flipped;
        _externalBoardDetectedFlipped = flipped;
        _externalOrientationLockedForCurrentGame = true;
        _orientationConfirmStreakFlipped = flipped;
        _orientationConfirmStreakCount = OrientationLockStreakThreshold;

        if (changed)
        {
            TraceBoard($"orientation pinned: {reason}; flipped={flipped}");
            LogDiag("ORIENTATION", $"pinned {reason}; flipped={flipped}");
        }
    }

    private static bool? TryInferExternalBoardFlipped(string fen, OrientationAssessment? assessment = null)
    {
        if (string.IsNullOrWhiteSpace(fen))
            return null;

        assessment ??= AssessExternalBoardOrientation(fen);
        if (assessment == null)
            return null;

        bool strongPreference =
            assessment.AbsoluteScoreDelta >= 40 ||
            assessment.StandardConflicts != assessment.FlippedConflicts;

        if (strongPreference)
        {
            return assessment.ScoreDelta > 0;
        }

        return null;
    }

    private static bool? TryInferExternalBoardFlippedFromOpposingPawnFiles(
        string boardPosition,
        out int standardConflicts,
        out int flippedConflicts)
    {
        standardConflicts = CountOpposingPawnFileOrderConflicts(boardPosition);
        string rotatedBoardPosition = Rotate180(boardPosition);
        flippedConflicts = CountOpposingPawnFileOrderConflicts(rotatedBoardPosition);

        if (standardConflicts == flippedConflicts)
            return null;

        return flippedConflicts < standardConflicts;
    }

    private static int CountOpposingPawnFileOrderConflicts(string boardPosition)
    {
        string[] rows = boardPosition.Split('/');
        if (rows.Length != 8)
            return int.MaxValue / 4;

        List<int>[] whitePawnRowsByFile = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();
        List<int>[] blackPawnRowsByFile = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();

        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            int fileIndex = 0;
            foreach (char c in rows[rowIndex])
            {
                if (char.IsDigit(c))
                {
                    fileIndex += c - '0';
                    continue;
                }

                if (fileIndex is < 0 or > 7)
                    continue;

                if (c == 'P')
                    whitePawnRowsByFile[fileIndex].Add(rowIndex);
                else if (c == 'p')
                    blackPawnRowsByFile[fileIndex].Add(rowIndex);

                fileIndex++;
            }
        }

        int conflicts = 0;
        for (int file = 0; file < 8; file++)
        {
            List<int> whiteRows = whitePawnRowsByFile[file];
            List<int> blackRows = blackPawnRowsByFile[file];

            if (whiteRows.Count == 0 || blackRows.Count == 0)
                continue;

            int whiteFrontmost = whiteRows.Min();
            int blackRearmost = blackRows.Max();
            if (blackRearmost >= whiteFrontmost)
                conflicts++;
        }

        return conflicts;
    }
    private static bool CanAutoFlipExternalBoardByVisualDiff(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
            return false;

        return !IsSparseExternalBoardPosition(GetBoardPosition(fen));
    }

    private static bool ShouldPromptForSparseExternalOrientation(string boardPosition, bool isAnalysisBoard, char referenceColor)
    {
        if (isAnalysisBoard || !IsSparseExternalBoardPosition(boardPosition))
            return false;

        return BoardPositionHasReferencePawn(boardPosition, referenceColor) ||
               !BoardPositionHasAnyPawn(boardPosition);
    }

    private static OrientationAssessment? AssessExternalBoardOrientation(string fen)
    {
        string boardPosition = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(boardPosition))
            return null;

        int standardScore = ScoreBoardOrientation(boardPosition, out int standardConflicts);
        string rotatedBoardPosition = GetBoardPosition(Rotate180($"{boardPosition} w - - 0 1"));
        int flippedScore = ScoreBoardOrientation(rotatedBoardPosition, out int flippedConflicts);

        return new OrientationAssessment
        {
            StandardScore = standardScore,
            FlippedScore = flippedScore,
            StandardConflicts = standardConflicts,
            FlippedConflicts = flippedConflicts
        };
    }

    private static int ScoreBoardOrientation(string boardPosition, out int conflictCount)
    {
        conflictCount = 0;

        string[] rows = boardPosition.Split('/');
        if (rows.Length != 8)
        {
            conflictCount = int.MaxValue;
            return int.MinValue / 4;
        }

        List<int>[] whitePawnRowsByFile = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();
        List<int>[] blackPawnRowsByFile = Enumerable.Range(0, 8).Select(_ => new List<int>()).ToArray();

        int? whiteKingRow = null;
        int? blackKingRow = null;
        int whitePawnRowSum = 0;
        int blackPawnRowSum = 0;
        int whitePawnCount = 0;
        int blackPawnCount = 0;
        int whitePieceRowSum = 0;
        int blackPieceRowSum = 0;
        int whitePieceCount = 0;
        int blackPieceCount = 0;
        int score = 0;

        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            int fileIndex = 0;
            foreach (char c in rows[rowIndex])
            {
                if (char.IsDigit(c))
                {
                    fileIndex += c - '0';
                    continue;
                }

                bool isWhite = char.IsUpper(c);
                char lower = char.ToLowerInvariant(c);

                if (lower == 'k')
                {
                    if (isWhite)
                        whiteKingRow = rowIndex;
                    else
                        blackKingRow = rowIndex;
                }

                if (isWhite)
                {
                    whitePieceRowSum += rowIndex;
                    whitePieceCount++;
                }
                else
                {
                    blackPieceRowSum += rowIndex;
                    blackPieceCount++;
                }

                if (lower == 'p')
                {
                    if (rowIndex == 0 || rowIndex == 7)
                    {
                        conflictCount++;
                        score -= 180;
                    }

                    if (isWhite)
                    {
                        whitePawnRowsByFile[fileIndex].Add(rowIndex);
                        whitePawnRowSum += rowIndex;
                        whitePawnCount++;
                    }
                    else
                    {
                        blackPawnRowsByFile[fileIndex].Add(rowIndex);
                        blackPawnRowSum += rowIndex;
                        blackPawnCount++;
                    }
                }

                fileIndex++;
            }
        }

        for (int file = 0; file < 8; file++)
        {
            List<int> whiteRows = whitePawnRowsByFile[file];
            List<int> blackRows = blackPawnRowsByFile[file];

            if (whiteRows.Count == 0 || blackRows.Count == 0)
                continue;

            int whiteFrontmost = whiteRows.Min();
            int blackRearmost = blackRows.Max();

            if (blackRearmost >= whiteFrontmost)
            {
                conflictCount++;
                score -= 1200;
            }
            else
            {
                score += 120;
            }
        }

        if (whiteKingRow.HasValue && blackKingRow.HasValue)
        {
            int gap = whiteKingRow.Value - blackKingRow.Value;
            if (gap > 0)
                score += Math.Min(24, gap * 6);
            else if (gap < 0)
                score -= Math.Min(24, Math.Abs(gap) * 6);
        }

        if (whitePawnCount > 0 && blackPawnCount > 0)
        {
            double whitePawnAverageRow = (double)whitePawnRowSum / whitePawnCount;
            double blackPawnAverageRow = (double)blackPawnRowSum / blackPawnCount;
            score += (int)Math.Round((whitePawnAverageRow - blackPawnAverageRow) * 10.0);
        }

        if (whitePieceCount > 0 && blackPieceCount > 0)
        {
            double whiteAverageRow = (double)whitePieceRowSum / whitePieceCount;
            double blackAverageRow = (double)blackPieceRowSum / blackPieceCount;
            score += (int)Math.Round((whiteAverageRow - blackAverageRow) * 4.0);
        }

        return score;
    }

}
