using ChessKit;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Diagnostics;
using static ChessKit.BoardGeometry;
using static ChessKit.FenText;

// External board and host-window tracking, foreground handling, board detection.
partial class Program
{
    private static string InferCastlingRightsFromBoard(string boardFEN)
    {
        var pieces = ParseBoard(boardFEN);
        if (pieces.Count == 0)
        {
            return "-";
        }

        var rights = new System.Text.StringBuilder(4);
        if (pieces.TryGetValue("e1", out char whiteKing) && whiteKing == 'K')
        {
            if (pieces.TryGetValue("h1", out char whiteKingRook) && whiteKingRook == 'R') rights.Append('K');
            if (pieces.TryGetValue("a1", out char whiteQueenRook) && whiteQueenRook == 'R') rights.Append('Q');
        }

        if (pieces.TryGetValue("e8", out char blackKing) && blackKing == 'k')
        {
            if (pieces.TryGetValue("h8", out char blackKingRook) && blackKingRook == 'r') rights.Append('k');
            if (pieces.TryGetValue("a8", out char blackQueenRook) && blackQueenRook == 'r') rights.Append('q');
        }

        return rights.Length > 0 ? rights.ToString() : "-";
    }

    private static Rect SmoothBoardPosition(Rect newRect)
    {
        newRect = NormalizeExternalBoardRect(newRect);

        if (IsInFastBoardScanMode())
        {
            _boardHistory.Clear();
            _boardHistory.Enqueue(newRect);
            return newRect;
        }

        if (_lastTrackedBox.HasValue && IsSignificantBoardMove(_lastTrackedBox.Value, newRect))
        {
            _fastBoardScanUntilUtc = DateTime.UtcNow.AddMilliseconds(_postMoveFastBoardScanMs);
            _externalBoardGeometryUnstableUntilUtc = DateTime.UtcNow.AddMilliseconds(_externalBoardGeometrySettleMs);
            _boardHistory.Clear();
            _boardHistory.Enqueue(newRect);
            return newRect;
        }

        _boardHistory.Enqueue(newRect);
        if (_boardHistory.Count > 3)
        {
            _boardHistory.Dequeue();
        }

        if (_boardHistory.Count < 2)
        {
            return newRect;
        }

        int avgX = (int)_boardHistory.Average(r => r.X);
        int avgY = (int)_boardHistory.Average(r => r.Y);
        int avgW = (int)_boardHistory.Average(r => r.Width);
        int avgH = (int)_boardHistory.Average(r => r.Height);

        return NormalizeExternalBoardRect(new Rect(avgX, avgY, avgW, avgH));
    }

    private static void RequestApplicationExit()
    {
        DialogResult result = ShowTopMostMessageBox(
            "Exit Chess Kit?",
            "Confirm Exit",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (result != DialogResult.Yes)
            return;

        if (_stockfish != null) _stockfish.ClearAllDepthTracking();
        Log($"[{DateTime.Now:HH:mm:ss}] Exit confirmed");
        Environment.Exit(0);
    }

    private static void HandleInvalidFenDetection()
    {
        if (ShouldIgnoreTransientInvalidFen())
        {
            return;
        }

        _invalidFenFrames++;
        if (_invalidFenFrames >= _invalidFenRefreshThreshold)
        {
            _requestBoardRefresh = true;
            _fastBoardScanUntilUtc = DateTime.UtcNow.AddMilliseconds(_postMoveFastBoardScanMs);
            ResetSuspectEmptyFenFrames();
            MarkFreshGameResetCandidate();
        }
    }

    private static void MarkFreshGameResetCandidate()
    {
        _freshGameResetUntilUtc = DateTime.UtcNow.AddMilliseconds(_freshGameResetWindowMs);
        _lastUserMoveFEN = "";
        _lastArrowSourceFEN = "";
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        ResetConfirmedStateTimeline();
        ClearDisplayedArrowDepthMemory();
        ClearExternalArrows();
    }

    private static bool HasFreshGameResetCandidate()
    {
        return DateTime.UtcNow < _freshGameResetUntilUtc;
    }

    private static bool IsFreshGameResetObservation(string fen)
    {
        return IsInitialBoardPosition(fen) || IsLikelyFreshOpeningPosition(fen);
    }

    private static void BeginTransitionNoiseGuard(int durationMs)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(durationMs);
        if (until > _transitionNoiseIgnoreUntilUtc)
        {
            _transitionNoiseIgnoreUntilUtc = until;
        }
    }

    // The app is designed for the fastest time controls: the former blitz
    // mode's timing values are the only profile now (one consistent fast
    // track instead of two switching behaviors). The slower legacy values
    // remain defined for reference/rollback only.
    private static int GetPostInteractionNoiseIgnoreMs()
        => BlitzPostInteractionNoiseIgnoreMs;

    private static int GetPostConfirmedPositionNoiseIgnoreMs()
        => BlitzPostConfirmedPositionNoiseIgnoreMs;

    private static bool ShouldIgnoreTransientInvalidFen()
    {
        return DateTime.UtcNow < _transitionNoiseIgnoreUntilUtc;
    }

    private static bool IsInFastBoardScanMode()
    {
        return DateTime.UtcNow < _fastBoardScanUntilUtc;
    }

    private static bool IsExternalBoardGeometryUnstable()
    {
        return DateTime.UtcNow < _externalBoardGeometryUnstableUntilUtc;
    }

    private static bool IsTrackedWindowResizeSettling()
    {
        return _trackedHwnd != IntPtr.Zero &&
               _trackedWindowLastResizeUtc != DateTime.MinValue &&
               DateTime.UtcNow < _trackedWindowLastResizeUtc.AddMilliseconds(_trackedWindowResizeSettleMs);
    }

    private static void UpdateBoardWindowProjection(Rect boardRect, WindowTracker.RECT windowRect)
    {
        boardRect = NormalizeExternalBoardRect(boardRect);

        if (windowRect.Width <= 0 || windowRect.Height <= 0 || boardRect.Width <= 0 || boardRect.Height <= 0)
        {
            _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
            return;
        }

        _boardRelativeInWindow = new System.Drawing.RectangleF(
            (boardRect.X - windowRect.Left) / (float)windowRect.Width,
            (boardRect.Y - windowRect.Top) / (float)windowRect.Height,
            boardRect.Width / (float)windowRect.Width,
            boardRect.Height / (float)windowRect.Height);
    }

    private static bool HasUsableBoardWindowProjection()
    {
        return !_boardRelativeInWindow.IsEmpty &&
               _boardRelativeInWindow.Width > 0.02f &&
               _boardRelativeInWindow.Height > 0.02f &&
               _boardRelativeInWindow.Width < 1.2f &&
               _boardRelativeInWindow.Height < 1.2f;
    }

    private static Rect ProjectTrackedBoardRect(WindowTracker.RECT windowRect, bool preferScaledProjection)
    {
        if (preferScaledProjection && HasUsableBoardWindowProjection() && windowRect.Width > 0 && windowRect.Height > 0)
        {
            int projectedX = windowRect.Left + (int)Math.Round(_boardRelativeInWindow.X * windowRect.Width);
            int projectedY = windowRect.Top + (int)Math.Round(_boardRelativeInWindow.Y * windowRect.Height);
            int projectedW = (int)Math.Round(_boardRelativeInWindow.Width * windowRect.Width);
            int projectedH = (int)Math.Round(_boardRelativeInWindow.Height * windowRect.Height);

            return NormalizeExternalBoardRect(new Rect(projectedX, projectedY, projectedW, projectedH));
        }

        return NormalizeExternalBoardRect(WindowTracker.ProjectBoardRect(windowRect, _boardOffsetInWindow));
    }

    private static bool TryHoldProjectedExternalArrowsDuringTrackedResize()
    {
        bool hasDisplayedAssistance =
            _currentMoveArrows is { Count: > 0 } ||
            HasDisplayedCoachOverlayForCurrentPosition();

        if (_currentFenIsAnalysisBoard ||
            DateTime.UtcNow > _trackedWindowResizeGraceUntilUtc ||
            _trackedHwnd == IntPtr.Zero ||
            !hasDisplayedAssistance ||
            !WindowTracker.IsTrackable(_trackedHwnd) ||
            !WindowTracker.TryGetWindowRect(_trackedHwnd, out var winRect))
        {
            return false;
        }

        var projected = ProjectTrackedBoardRect(winRect, preferScaledProjection: true);
        _lastTrackedBox = projected;
        _lastWindowRect = winRect;
        _boardLostFrames = 0;
        _boardContentLostFrames = 0;

        _evalBar?.SetBoardVisible(true);
        _engineLines?.SetBoardVisible(true);
        _settingsToolbar?.SetBoardVisible(true);
        if (_overlay != null && _showingMoves)
        {
            _overlay.BeginInvoke(new Action(() =>
                _overlay.SetBoardScreenPosition(new Rectangle(projected.X, projected.Y, projected.Width, projected.Height))));
        }
        return true;
    }

    private static bool HasHealthyTrackedExternalWindow()
    {
        return _trackedHwnd != IntPtr.Zero &&
               _lastTrackedBox.HasValue &&
               WindowTracker.IsTrackable(_trackedHwnd);
    }

    private static void ResetForegroundBoardProbeBackoff(string reason)
    {
        if (_foregroundBoardProbeMisses > 0 && _diagLoggingEnabled)
            LogDiag("WINTRACK", $"foreground probe backoff cleared ({reason})");

        _foregroundBoardProbeMisses = 0;
        _foregroundBoardProbeBackoffUntilUtc = DateTime.MinValue;
        _foregroundBoardProbeBudgetBackoffUntilUtc = DateTime.MinValue;
    }

    private static void RecordForegroundBoardProbeMiss(IntPtr hwnd)
    {
        if (hwnd != _lastForegroundBoardProbeHwnd)
            _foregroundBoardProbeMisses = 0;

        _foregroundBoardProbeMisses++;
        int backoffMs = GetNoBoardProbeBackoffMs(_foregroundBoardProbeMisses);
        _foregroundBoardProbeBackoffUntilUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);

        if (_diagLoggingEnabled && (_foregroundBoardProbeMisses <= 3 || _foregroundBoardProbeMisses % 4 == 0))
        {
            LogDiag(
                "WINTRACK",
                $"foreground no-board probe backoff {backoffMs}ms (misses={_foregroundBoardProbeMisses}, hwnd=0x{hwnd.ToInt64():X})");
        }
    }

    private static bool IsForegroundBoardProbeBackedOffFor(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero &&
               hwnd == _lastForegroundBoardProbeHwnd &&
               DateTime.UtcNow < _foregroundBoardProbeBackoffUntilUtc;
    }

    private static void ResetExternalBoardAcquisitionBackoff(string reason)
    {
        if (_externalBoardAcquisitionMisses > 0 && _diagLoggingEnabled)
            LogDiag("WINTRACK", $"full-board acquisition backoff cleared ({reason})");

        _externalBoardAcquisitionMisses = 0;
        _externalBoardAcquisitionBackoffUntilUtc = DateTime.MinValue;
    }

    private static void RecordExternalBoardAcquisitionMiss()
    {
        _externalBoardAcquisitionMisses++;
        int backoffMs = GetNoBoardProbeBackoffMs(_externalBoardAcquisitionMisses);
        _externalBoardAcquisitionBackoffUntilUtc = DateTime.UtcNow.AddMilliseconds(backoffMs);

        if (_diagLoggingEnabled && (_externalBoardAcquisitionMisses <= 3 || _externalBoardAcquisitionMisses % 4 == 0))
        {
            LogDiag(
                "WINTRACK",
                $"full-board acquisition backoff {backoffMs}ms (misses={_externalBoardAcquisitionMisses})");
        }
    }

    private static bool TryHandleForegroundWindowChange()
    {
        if (!_isTracking || _detector == null || _menuExpanded)
            return false;

        IntPtr foreground = WindowTracker.GetForegroundWindow();
        if (!IsExternalForegroundCandidate(foreground))
        {
            if (ShouldHoldOverlaysForForegroundWithoutBoard(foreground))
            {
                BeginForegroundNoBoardOverlayHold(foreground, "foreground is not a board candidate");
                return true;
            }

            return false;
        }

        if (foreground == _trackedHwnd)
        {
            ClearForegroundNoBoardOverlayHold("tracked board is foreground");
            ResetForegroundBoardProbeBackoff("tracked board is foreground");
            return false;
        }

        if (IsForegroundNoBoardOverlayHoldActiveFor(foreground))
            return false;

        DateTime now = DateTime.UtcNow;
        if (foreground != _lastForegroundBoardProbeHwnd)
        {
            ResetForegroundBoardProbeBackoff("foreground changed");
            _foregroundBoardProbeBudgetBackoffUntilUtc = DateTime.MinValue;
        }

        bool foregroundProbeBackedOff =
            foreground == _lastForegroundBoardProbeHwnd &&
            now < _foregroundBoardProbeBackoffUntilUtc;
        bool foregroundProbeBudgetBackedOff =
            foreground == _lastForegroundBoardProbeHwnd &&
            now < _foregroundBoardProbeBudgetBackoffUntilUtc;

        bool probeThrottled =
            foreground == _lastForegroundBoardProbeHwnd &&
            ((_trackedHwnd == IntPtr.Zero &&
                now < _lastForegroundBoardProbeUtc.AddMilliseconds(_foregroundBoardProbeCooldownMs)) ||
             foregroundProbeBackedOff ||
             foregroundProbeBudgetBackedOff);

        if (!probeThrottled)
        {
            _lastForegroundBoardProbeHwnd = foreground;
            _lastForegroundBoardProbeUtc = now;
            bool hasHealthyTrackedBoard =
                _trackedHwnd != IntPtr.Zero &&
                _lastTrackedBox.HasValue &&
                WindowTracker.IsTrackable(_trackedHwnd);
            int? foregroundBoardDetectionMinInterval =
                hasHealthyTrackedBoard ? null : _boardDetectionAcquireMinIntervalMs;

            if (TryDetectExternalBoardInWindow(foreground, out var boardRect, out var windowRect, out var detectedFen, out var detectedBoardSnapshot, out bool foregroundProbeSkippedForBudget, foregroundBoardDetectionMinInterval))
            {
                try
                {
                    if (ShouldSuppressTransientForegroundBoardSwitch(foreground, boardRect, "foreground changed"))
                        return false;

                    SwitchTrackedExternalWindow(foreground, boardRect, windowRect, "foreground changed", detectedFen, detectedBoardSnapshot);
                    ResetForegroundBoardProbeBackoff("foreground board found");
                    ResetExternalBoardAcquisitionBackoff("foreground board found");
                }
                finally
                {
                    detectedBoardSnapshot?.Dispose();
                }
                return false;
            }

            if (foregroundProbeSkippedForBudget)
            {
                int retryMs = Math.Clamp(BoardVisionDetector.GetBoardDetectionCooldownRemainingMs(foregroundBoardDetectionMinInterval) + 75, 250, 20000);
                _foregroundBoardProbeBudgetBackoffUntilUtc = DateTime.UtcNow.AddMilliseconds(retryMs);
                if (_diagLoggingEnabled)
                {
                    LogDiag("WINTRACK", $"foreground probe skipped by bandwidth budget; keeping current tracking and retrying in {retryMs}ms");
                }
                return false;
            }

            RecordForegroundBoardProbeMiss(foreground);
        }

        if (_trackedHwnd != IntPtr.Zero && WindowTracker.IsTrackable(_trackedHwnd))
        {
            if (ShouldHideTrackedBoardForForegroundNoBoard(foreground))
            {
                _foregroundMismatchFenGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(_foregroundMismatchFenGuardMs);
                if (foreground == _lastForegroundBoardProbeHwnd && _foregroundBoardProbeMisses >= 2)
                {
                    if (_diagLoggingEnabled)
                    {
                        LogDiag("WINTRACK", $"foreground no-board confirmed; detaching obscured tracked board hwnd=0x{_trackedHwnd.ToInt64():X} foreground=0x{foreground.ToInt64():X}");
                    }

                    DetachExternalTrackingForForegroundChange(foreground);
                    return false;
                }

                BeginForegroundNoBoardOverlayHold(foreground, "foreground probe found no board");
                if (_diagLoggingEnabled)
                {
                    LogDiag("WINTRACK", $"foreground changed to non-board hwnd=0x{foreground.ToInt64():X}; hiding tracked board overlays hwnd=0x{_trackedHwnd.ToInt64():X}");
                }
            }
            else
            {
                ClearForegroundNoBoardOverlayHold("tracked board remains visible");
            }

            return false;
        }

        bool detachedExistingBoard = _trackedHwnd != IntPtr.Zero || _lastTrackedBox.HasValue;
        if (detachedExistingBoard)
        {
            DetachExternalTrackingForForegroundChange(foreground);
        }

        // If we do not currently have a tracked board and the focused-window
        // probe missed, do not block the normal full-screen acquisition pass.
        // Some browsers capture poorly through the foreground-window path after
        // focus changes, while the broader detector still sees the board just
        // fine. Returning true here left ChessKit stuck at toolbar fallback
        // until a minimize/restore event forced the dedicated recovery path.
        return detachedExistingBoard;
    }

    private static bool ShouldHoldOverlaysForForegroundWithoutBoard(IntPtr foreground)
    {
        return foreground != IntPtr.Zero &&
               foreground != _trackedHwnd &&
               _trackedHwnd != IntPtr.Zero &&
               WindowTracker.IsTrackable(_trackedHwnd) &&
               ShouldHideTrackedBoardForForegroundNoBoard(foreground) &&
               !IsChessKitOwnedWindow(foreground);
    }

    private static bool ShouldHideTrackedBoardForForegroundNoBoard(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero ||
            foreground == _trackedHwnd ||
            _trackedHwnd == IntPtr.Zero ||
            !_lastTrackedBox.HasValue ||
            !WindowTracker.IsTrackable(_trackedHwnd))
        {
            return false;
        }

        return WindowTracker.IsBoardObscured(_trackedHwnd, _lastTrackedBox.Value);
    }

    private static bool IsForegroundNoBoardOverlayHoldActive()
    {
        if (_foregroundNoBoardOverlayHoldUntilUtc == DateTime.MinValue)
            return false;

        if (DateTime.UtcNow < _foregroundNoBoardOverlayHoldUntilUtc)
            return true;

        ClearForegroundNoBoardOverlayHold("hold expired");
        return false;
    }

    private static bool IsForegroundNoBoardOverlayHoldActiveFor(IntPtr foreground)
    {
        if (_foregroundNoBoardOverlayHoldUntilUtc == DateTime.MinValue ||
            foreground == IntPtr.Zero ||
            foreground != _foregroundNoBoardOverlayHoldHwnd)
        {
            return false;
        }

        if (DateTime.UtcNow < _foregroundNoBoardOverlayHoldUntilUtc)
            return true;

        ClearForegroundNoBoardOverlayHold("hold expired");
        return false;
    }

    private static bool IsExternalBoardOutputSuspended()
    {
        if (_currentFenIsAnalysisBoard || IsActiveAnalysisBoardFen(_currentFEN))
            return false;

        if (_trackedHwnd == IntPtr.Zero || !_lastTrackedBox.HasValue)
            return true;

        if (!WindowTracker.IsTrackable(_trackedHwnd))
            return true;

        if (_trackingLostWaitingForReacquire)
            return true;

        bool foregroundNoBoardSignal =
            IsForegroundNoBoardOverlayHoldActive() ||
            IsForegroundBoardProbeBackedOffFor(WindowTracker.GetForegroundWindow());

        if (!foregroundNoBoardSignal)
            return false;

        return WindowTracker.IsBoardObscured(_trackedHwnd, _lastTrackedBox.Value);
    }

    private static void InvalidateExternalBoardOutput(string reason)
    {
        try { CancelPendingAnalysis(reason); } catch { }
        ResetAnalysisSchedulingState();
        Interlocked.Increment(ref _arrowRenderToken);
        Interlocked.Increment(ref _arrowDisplayGeneration);
    }

    private static void BeginForegroundNoBoardOverlayHold(IntPtr foreground, string reason)
    {
        DateTime now = DateTime.UtcNow;
        DateTime until = now.AddMilliseconds(_foregroundNoBoardOverlayHoldMs);
        bool logTransition =
            _foregroundNoBoardOverlayHoldUntilUtc == DateTime.MinValue ||
            foreground != _foregroundNoBoardOverlayHoldHwnd ||
            now >= _foregroundNoBoardOverlayHoldUntilUtc;

        _foregroundNoBoardOverlayHoldUntilUtc = until;
        _foregroundNoBoardOverlayHoldHwnd = foreground;
        _boardObscuredLastFrame = true;
        ResetPendingFenCandidate();

        if (logTransition)
        {
            InvalidateExternalBoardOutput($"foreground has no board ({reason})");
            HideOverlaysVisually();
        }

        if (logTransition && _diagLoggingEnabled)
        {
            LogDiag(
                "WINTRACK",
                $"foreground has no board; holding overlays for {_foregroundNoBoardOverlayHoldMs}ms ({reason}, foreground=0x{foreground.ToInt64():X})");
        }
    }

    private static void ClearForegroundNoBoardOverlayHold(string reason)
    {
        if (_foregroundNoBoardOverlayHoldUntilUtc == DateTime.MinValue)
            return;

        _foregroundNoBoardOverlayHoldUntilUtc = DateTime.MinValue;
        _foregroundNoBoardOverlayHoldHwnd = IntPtr.Zero;

        if (_diagLoggingEnabled)
            LogDiag("WINTRACK", $"foreground no-board overlay hold cleared ({reason})");
    }

    private static bool IsExternalForegroundCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        if (!WindowTracker.IsTrackable(hwnd))
            return false;

        if (WindowTracker.TryGetWindowRect(hwnd, out var windowRect) &&
            (windowRect.Width < 180 || windowRect.Height < 180))
        {
            return false;
        }

        if (IsKnownNonBoardHostWindow(hwnd))
            return false;

        return !IsChessKitOwnedWindow(hwnd);
    }

    private static void SuppressForegroundBoardSwitchesBriefly(string reason, int? durationMs = null)
    {
        int ms = durationMs ?? _foregroundBoardSwitchSuppressMs;
        DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
        if (until > _foregroundBoardSwitchSuppressedUntilUtc)
            _foregroundBoardSwitchSuppressedUntilUtc = until;

        LogDiag("WINTRACK", $"suppressing transient foreground board switches for {ms}ms ({reason})");
    }

    private static bool ShouldSuppressTransientForegroundBoardSwitch(IntPtr candidateHwnd, Rect candidateBoardRect, string reason)
    {
        if (candidateHwnd == IntPtr.Zero || candidateHwnd == _trackedHwnd)
            return false;

        DateTime now = DateTime.UtcNow;
        bool suppressionActive =
            _obstructingUiActive ||
            _liveEngineSettingsInFlight ||
            now < _foregroundBoardSwitchSuppressedUntilUtc;

        if (_trackedHwnd == IntPtr.Zero ||
            !_lastTrackedBox.HasValue ||
            !WindowTracker.IsTrackable(_trackedHwnd))
        {
            return false;
        }

        Rect trackedBoardRect = _lastTrackedBox.Value;
        if (!IsConsistentWithTrackedExternalBoard(candidateBoardRect, trackedBoardRect, allowSameWindowResizeReflow: true))
            return false;

        bool sameBoardForegroundProbe = reason.Contains("foreground", StringComparison.OrdinalIgnoreCase);
        if (!suppressionActive && !sameBoardForegroundProbe)
            return false;

        _foregroundMismatchFenGuardUntilUtc = now.AddMilliseconds(_foregroundMismatchFenGuardMs);
        LogDiag(
            "WINTRACK",
            $"ignored transient foreground board hwnd=0x{candidateHwnd.ToInt64():X} ({reason}); keeping healthy tracked board hwnd=0x{_trackedHwnd.ToInt64():X}");
        return true;
    }

    private static bool IsKnownNonBoardHostWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out int processId);
            if (processId <= 0)
                return false;

            using var process = Process.GetProcessById(processId);
            string name = process.ProcessName;

            // Shell windows are a common next-focus target when a browser
            // chessboard is minimized. Never dock the toolbar to them: if
            // no real board is visible, the toolbar belongs at fallback.
            return name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsChessKitOwnedWindow(IntPtr hwnd)
    {
        return IsFormHandle(hwnd, _overlay) ||
               IsFormHandle(hwnd, _evalBar) ||
               IsFormHandle(hwnd, _engineLines) ||
               IsFormHandle(hwnd, _settingsToolbar) ||
               IsFormHandle(hwnd, _analysisBoardForm) ||
               IsFormHandle(hwnd, _gameAnalysisForm) ||
               IsFormHandle(hwnd, _debugHudPresenter.Form);
    }

    private static bool IsFormHandle(IntPtr hwnd, Form? form)
    {
        if (hwnd == IntPtr.Zero || form == null || form.IsDisposed || !form.IsHandleCreated)
            return false;

        try
        {
            return form.Handle == hwnd;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDetectExternalBoardInWindow(
        IntPtr hwnd,
        out Rect boardRect,
        out WindowTracker.RECT windowRect,
        out string detectedFen,
        out Mat? detectedBoardSnapshot,
        out bool detectionSkippedForBudget,
        int? boardDetectionMinIntervalMs = null)
    {
        boardRect = default;
        windowRect = default;
        detectedFen = "";
        detectedBoardSnapshot = null;
        detectionSkippedForBudget = false;

        if (_detector == null ||
            hwnd == IntPtr.Zero ||
            !WindowTracker.IsTrackable(hwnd) ||
            !WindowTracker.TryGetWindowRect(hwnd, out windowRect))
        {
            return false;
        }

        if (windowRect.Width < 180 || windowRect.Height < 180)
        {
            LogDiag("WINTRACK", $"foreground probe skipped: hwnd=0x{hwnd.ToInt64():X} tiny window {windowRect.Left},{windowRect.Top} {windowRect.Width}x{windowRect.Height}");
            return false;
        }

        if (!BoardVisionDetector.IsBoardDetectionUploadReady(boardDetectionMinIntervalMs))
        {
            detectionSkippedForBudget = true;
            return false;
        }

        Rectangle requestedCaptureRegion = new Rectangle(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height);
        try
        {
            using var frame = ScreenCapture.CaptureRegionMatGdi(requestedCaptureRegion, out var captureRegion);
            if (frame.Empty() || captureRegion.IsEmpty)
            {
                LogDiag("WINTRACK", $"foreground probe failed: empty capture hwnd=0x{hwnd.ToInt64():X} requested={requestedCaptureRegion.X},{requestedCaptureRegion.Y} {requestedCaptureRegion.Width}x{requestedCaptureRegion.Height}");
                return false;
            }

            MaskAnalysisBoardRegion(frame, captureRegion);
            System.Drawing.Point mouse = Control.MousePosition;
            var preferredPoint = new System.Drawing.Point(
                mouse.X - captureRegion.X,
                mouse.Y - captureRegion.Y);
            BoardVisionDetector.ClearBoardDetectionAttemptStatus();
            Rect? localBoard = _detector.DetectBoard(frame, preferredPoint, boardDetectionMinIntervalMs);
            if (!localBoard.HasValue)
            {
                if (BoardVisionDetector.LastBoardDetectionWasThrottled)
                {
                    detectionSkippedForBudget = true;
                    return false;
                }

                LogDiag("WINTRACK", $"foreground probe failed: no board hwnd=0x{hwnd.ToInt64():X} requested={requestedCaptureRegion.X},{requestedCaptureRegion.Y} {requestedCaptureRegion.Width}x{requestedCaptureRegion.Height} actual={captureRegion.X},{captureRegion.Y} {captureRegion.Width}x{captureRegion.Height} frame={frame.Width}x{frame.Height}");
                return false;
            }

            Rect detected = localBoard.Value;
            var screenBoard = NormalizeExternalBoardRect(new Rect(
                captureRegion.X + detected.X,
                captureRegion.Y + detected.Y,
                detected.Width,
                detected.Height));

            if (ShouldIgnoreDetectedAnalysisBoard(screenBoard))
            {
                LogDiag("WINTRACK", $"foreground probe rejected analysis-board rect={screenBoard.X},{screenBoard.Y} {screenBoard.Width}x{screenBoard.Height}");
                return false;
            }

            IntPtr detectedWindow = WindowTracker.ResolveTopLevelWindow(screenBoard);
            if (detectedWindow != hwnd && !IsBoardRectInsideWindow(screenBoard, windowRect))
            {
                LogDiag("WINTRACK", $"foreground probe rejected board outside hwnd: detected=0x{detectedWindow.ToInt64():X} foreground=0x{hwnd.ToInt64():X} board={screenBoard.X},{screenBoard.Y} {screenBoard.Width}x{screenBoard.Height} window={windowRect.Left},{windowRect.Top} {windowRect.Width}x{windowRect.Height}");
                return false;
            }

            if (!TryCapturePlausibleExternalBoardCandidate(screenBoard, "foreground probe", out detectedFen, out detectedBoardSnapshot))
            {
                LogDiag("WINTRACK", $"foreground probe rejected implausible rect={screenBoard.X},{screenBoard.Y} {screenBoard.Width}x{screenBoard.Height}");
                return false;
            }

            boardRect = screenBoard;
            return true;
        }
        catch (Exception ex)
        {
            LogDiag("WINTRACK", $"foreground board probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool ShouldRejectBoardBecauseNotForeground(Rect boardRect)
    {
        IntPtr foreground = WindowTracker.GetForegroundWindow();
        IntPtr detectedWindow = WindowTracker.ResolveTopLevelWindow(boardRect);

        if (foreground != IntPtr.Zero && IsKnownNonBoardHostWindow(foreground))
        {
            if (detectedWindow != IntPtr.Zero && IsExternalForegroundCandidate(detectedWindow))
                return false;

            if (_diagLoggingEnabled)
            {
                LogDiag("WINTRACK", $"rejected board because foreground is a known non-board host hwnd=0x{foreground.ToInt64():X}");
            }
            return true;
        }

        if (!IsExternalForegroundCandidate(foreground))
            return false;

        if (detectedWindow == foreground)
            return false;

        bool foregroundHasNoBoard =
            IsForegroundNoBoardOverlayHoldActiveFor(foreground) ||
            IsForegroundBoardProbeBackedOffFor(foreground);

        if (foregroundHasNoBoard &&
            detectedWindow != IntPtr.Zero &&
            IsExternalForegroundCandidate(detectedWindow))
        {
            if (_diagLoggingEnabled)
            {
                LogDiag("WINTRACK", $"accepted visible non-foreground board while foreground has no board detected=0x{detectedWindow.ToInt64():X} foreground=0x{foreground.ToInt64():X}");
            }
            return false;
        }

        if (WindowTracker.TryGetWindowRect(foreground, out var foregroundRect) &&
            IsBoardRectInsideWindow(boardRect, foregroundRect))
        {
            return false;
        }

        if (_diagLoggingEnabled)
        {
            string detectedText = detectedWindow == IntPtr.Zero ? "none" : $"0x{detectedWindow.ToInt64():X}";
            LogDiag("WINTRACK", $"rejected board from non-foreground window detected={detectedText} foreground=0x{foreground.ToInt64():X}");
        }

        if (_trackedHwnd != IntPtr.Zero && WindowTracker.IsTrackable(_trackedHwnd))
        {
            _foregroundMismatchFenGuardUntilUtc = DateTime.UtcNow.AddMilliseconds(_foregroundMismatchFenGuardMs);
        }

        return true;
    }

    private static bool IsConsistentWithTrackedExternalBoard(
        Rect candidate,
        Rect tracked,
        bool allowSameWindowResizeReflow = false)
    {
        if (_boardLostFrames > _boardLostThreshold)
            return true;

        double trackedCenterX = tracked.X + tracked.Width / 2.0;
        double trackedCenterY = tracked.Y + tracked.Height / 2.0;
        double candidateCenterX = candidate.X + candidate.Width / 2.0;
        double candidateCenterY = candidate.Y + candidate.Height / 2.0;

        double centerDistance = Math.Sqrt(
            Math.Pow(candidateCenterX - trackedCenterX, 2) +
            Math.Pow(candidateCenterY - trackedCenterY, 2));

        double sizeDelta =
            Math.Abs(candidate.Width - tracked.Width) / (double)Math.Max(1, tracked.Width) +
            Math.Abs(candidate.Height - tracked.Height) / (double)Math.Max(1, tracked.Height);

        double maxDistance = Math.Max(96, Math.Min(tracked.Width, tracked.Height) * 0.45);
        if (centerDistance <= maxDistance && sizeDelta <= 0.45)
            return true;

        IntPtr candidateWindow = WindowTracker.ResolveTopLevelWindow(candidate);
        if (candidateWindow != IntPtr.Zero && candidateWindow != _trackedHwnd)
            return false;

        if (allowSameWindowResizeReflow && candidateWindow == _trackedHwnd)
            return true;

        return false;
    }

    private static bool ShouldScanForBoardNearMouse(WindowTracker.RECT trackedWindowRect)
    {
        if (!_lastTrackedBox.HasValue || _trackedHwnd == IntPtr.Zero)
            return false;

        System.Drawing.Point mouse = Control.MousePosition;
        if (mouse.X < trackedWindowRect.Left || mouse.X > trackedWindowRect.Right ||
            mouse.Y < trackedWindowRect.Top || mouse.Y > trackedWindowRect.Bottom)
        {
            return false;
        }

        Rect tracked = _lastTrackedBox.Value;
        Rectangle focusArea = Rectangle.Inflate(
            new Rectangle(tracked.X, tracked.Y, tracked.Width, tracked.Height),
            Math.Max(32, tracked.Width / 10),
            Math.Max(32, tracked.Height / 10));

        if (focusArea.Contains(mouse))
            return false;

        DateTime now = DateTime.UtcNow;
        if ((now - _lastMouseFocusScanUtc).TotalMilliseconds < _mouseFocusBoardScanCooldownMs)
            return false;

        _lastMouseFocusScanUtc = now;
        _lastMouseFocusScanPoint = mouse;
        return true;
    }

    private static void ResetExternalBoardAnalysisForBoardSwitch(string reason)
    {
        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"clearing external analysis for {reason}");
        }

        try { CancelPendingAnalysis(reason); } catch { }
        CancelStaticLastMoveHighlightInitialHold();

        _arrowDisplayGeneration++;
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        _currentFEN = "";
        _lastUserMoveFEN = "";
        _lastFenSentToEngine = "";
        _waitingForOpponentMove = false;
        _externalOrientationLockedForCurrentGame = false;
        _externalTrackedPositionCount = 0;
        _orientationConfirmStreakCount = 0;
        _invalidFenFrames = 0;
        ClearDisplayedArrowDepthMemory();
        ResetBlitzAutoDetection($"board switch: {reason}");
        ResetConfirmedStateTimeline();
        ResetPendingFenCandidate();
        ResetConfirmedBoardSnapshot();
        ClearExternalArrows();
    }

    private static bool IsBlitzModeActive()
    {
        if (_blitzModeSetting == BlitzModeSetting.On)
            return true;

        if (_blitzModeSetting == BlitzModeSetting.Off)
            return false;

        if (_autoBlitzActive && DateTime.UtcNow >= _autoBlitzActiveUntilUtc)
            SetAutoBlitzActive(false, "rapid live-board pace cooled down");

        return _autoBlitzActive;
    }

    private static void SetAutoBlitzActive(bool active, string reason)
    {
        if (_autoBlitzActive == active)
            return;

        _autoBlitzActive = active;
        if (!active)
            _autoBlitzActiveUntilUtc = DateTime.MinValue;

        Log($"[BLITZ] Auto {(active ? "enabled" : "disabled")}: {reason}");
        LogDiag("BLITZ", $"auto {(active ? "enabled" : "disabled")} ({reason})");
        RefreshDebugView(active ? "Blitz auto enabled" : "Blitz auto disabled");
    }

    private static void ResetBlitzAutoDetection(string reason)
    {
        bool wasActive = _autoBlitzActive;
        _autoBlitzActive = false;
        _autoBlitzActiveUntilUtc = DateTime.MinValue;
        _lastExternalMoveCadenceUtc = DateTime.MinValue;
        _rapidExternalMoveStreak = 0;

        if (wasActive)
        {
            Log($"[BLITZ] Auto disabled: {reason}");
            LogDiag("BLITZ", $"auto disabled ({reason})");
            RefreshDebugView("Blitz auto disabled");
        }
    }

    private static void RecordExternalConfirmedMoveForBlitzAuto(
        bool isFreshGameStart,
        LegalTurnTransition? legalTurnTransition,
        char? detectedMover,
        string confirmedFen)
    {
        if (_blitzModeSetting != BlitzModeSetting.Auto)
            return;

        if (_currentFenIsAnalysisBoard || IsActiveAnalysisBoardFen(confirmedFen))
            return;

        if (isFreshGameStart)
        {
            ResetBlitzAutoDetection("fresh game start");
            return;
        }

        bool reliableSingleMove =
            legalTurnTransition?.PlyCount == 1 ||
            detectedMover.HasValue;

        if (!reliableSingleMove)
            return;

        DateTime now = DateTime.UtcNow;
        double elapsedMs = _lastExternalMoveCadenceUtc == DateTime.MinValue
            ? double.MaxValue
            : (now - _lastExternalMoveCadenceUtc).TotalMilliseconds;

        _rapidExternalMoveStreak = elapsedMs <= BlitzAutoFastMoveWindowMs
            ? Math.Min(_rapidExternalMoveStreak + 1, 32)
            : 1;
        _lastExternalMoveCadenceUtc = now;

        if (_rapidExternalMoveStreak >= BlitzAutoActivationStreak)
        {
            _autoBlitzActiveUntilUtc = now.AddMilliseconds(BlitzAutoHoldMs);
            SetAutoBlitzActive(true, $"{_rapidExternalMoveStreak} rapid confirmed moves");
        }
        else if (_autoBlitzActive)
        {
            _autoBlitzActiveUntilUtc = now.AddMilliseconds(BlitzAutoHoldMs);
        }
    }

    private static bool TryAcceptRelocatedBoardInTrackedWindow(Rect candidate, string reason)
    {
        candidate = NormalizeExternalBoardRect(candidate);

        if (_trackedHwnd == IntPtr.Zero)
            return false;

        IntPtr candidateWindow = WindowTracker.ResolveTopLevelWindow(candidate);
        if (candidateWindow != _trackedHwnd)
            return false;

        double stableMs = _windowStableSinceUtc == DateTime.MinValue
            ? double.MaxValue
            : (DateTime.UtcNow - _windowStableSinceUtc).TotalMilliseconds;
        if (stableMs < Math.Min(_windowSettledTrustMs, 650))
            return false;

        if (!WindowTracker.TryGetWindowRect(_trackedHwnd, out var windowRect))
            return false;

        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"accepting relocated board inside tracked window ({reason}) rect={candidate.X},{candidate.Y} {candidate.Width}x{candidate.Height}");
        }

        try { CancelPendingAnalysis($"board relocated inside tracked window ({reason})"); } catch { }
        CancelStaticLastMoveHighlightInitialHold();

        _arrowDisplayGeneration++;
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        _currentFEN = "";
        _lastUserMoveFEN = "";
        _lastFenSentToEngine = "";
        _waitingForOpponentMove = false;
        _externalOrientationLockedForCurrentGame = false;
        _externalTrackedPositionCount = 0;
        _orientationConfirmStreakCount = 0;
        _boardOffsetInWindow = WindowTracker.ComputeOffset(candidate, windowRect);
        UpdateBoardWindowProjection(candidate, windowRect);
        _lastTrackedBox = candidate;
        _lastWindowRect = windowRect;
        _framesSinceWindowTrackVerify = 0;
        _boardLostFrames = 0;
        _boardContentLostFrames = 0;
        _invalidFenFrames = 0;
        _requestBoardRefresh = false;
        _scheduledVerifyUtc = DateTime.MinValue;
        _boardHistory.Clear();
        ResetConfirmedStateTimeline();
        ResetPendingFenCandidate();
        ResetConfirmedBoardSnapshot();
        ClearExternalArrows();

        _evalBar?.SetBoardVisible(true);
        _engineLines?.SetBoardVisible(true);
        _settingsToolbar?.SetBoardVisible(true);
        _settingsToolbar?.UpdateWindowPosition(new Rectangle(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height));
        return true;
    }

    private static void SwitchTrackedExternalWindow(
        IntPtr hwnd,
        Rect boardRect,
        WindowTracker.RECT windowRect,
        string reason,
        string? initialRawFen = null,
        Mat? initialBoardSnapshot = null)
    {
        boardRect = NormalizeExternalBoardRect(boardRect);

        if (!IsExternalForegroundCandidate(hwnd))
        {
            if (_diagLoggingEnabled)
            {
                LogDiag("WINTRACK", $"refused to track non-board host hwnd=0x{hwnd.ToInt64():X} ({reason})");
            }
            return;
        }

        bool windowChanged = _trackedHwnd != hwnd;

        if (windowChanged && ShouldSuppressTransientForegroundBoardSwitch(hwnd, boardRect, reason))
            return;

        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"tracking foreground board hwnd=0x{hwnd.ToInt64():X} ({reason}) rect={boardRect.X},{boardRect.Y} {boardRect.Width}x{boardRect.Height}");
        }

        ClearForegroundNoBoardOverlayHold($"tracking foreground board ({reason})");

        if (windowChanged)
        {
            try { CancelPendingAnalysis($"external window switched ({reason})"); } catch { }
            CancelStaticLastMoveHighlightInitialHold();
            _arrowDisplayGeneration++;
            _currentMoveArrows = null;
            _lastAnalysisVariations = null;
            _lastArrowSourceFEN = "";
            _currentFEN = "";
            Interlocked.Increment(ref _arrowRenderToken);
            _lastUserMoveFEN = "";
            _lastFenSentToEngine = "";
            _waitingForOpponentMove = false;
            _externalOrientationLockedForCurrentGame = false;
            _externalTrackedPositionCount = 0;
            _orientationConfirmStreakCount = 0;
            ResetConfirmedStateTimeline();
            ResetPendingFenCandidate();
            ResetConfirmedBoardSnapshot();
            ClearExternalArrows();
        }

        _analysisTargetIsAnalysisBoard = false;
        _currentFenIsAnalysisBoard = false;
        _foregroundMismatchFenGuardUntilUtc = DateTime.MinValue;
        _trackingLostWaitingForReacquire = false;
        _lostHwndCache = IntPtr.Zero;
        _lostAcquisitionCandidateSinceUtc = null;
        _minimizeEndFiredForLostHwnd = false;
        _trackedHwnd = hwnd;
        _lastWindowRect = windowRect;
        _boardOffsetInWindow = WindowTracker.ComputeOffset(boardRect, windowRect);
        UpdateBoardWindowProjection(boardRect, windowRect);
        _lastTrackedBox = boardRect;
        _framesSinceWindowTrackVerify = 0;
        _boardLostFrames = 0;
        _boardContentLostFrames = 0;
        _invalidFenFrames = 0;
        _requestBoardRefresh = false;
        _boardHistory.Clear();
        _windowStableSinceUtc = DateTime.UtcNow;
        _scheduledVerifyUtc = DateTime.MinValue;

        _evalBar?.SetBoardVisible(true);
        _engineLines?.SetBoardVisible(true);
        _settingsToolbar?.SetBoardVisible(true);
        _settingsToolbar?.UpdateWindowPosition(new Rectangle(windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height));
        PrimeTrackedExternalFenFromProbe(boardRect, initialRawFen, initialBoardSnapshot, reason);
        RefreshDebugView("Foreground board selected");
    }

    private static void PrimeTrackedExternalFenFromProbe(Rect boardRect, string? initialRawFen, Mat? initialBoardSnapshot, string reason)
    {
        boardRect = NormalizeExternalBoardRect(boardRect);

        if (_detector == null || _trackedHwnd == IntPtr.Zero)
            return;

        string rawFen = initialRawFen ?? "";
        Mat? ownedSnapshot = null;

        try
        {
            if (string.IsNullOrWhiteSpace(rawFen))
            {
                if (!TryCapturePlausibleExternalBoardCandidate(boardRect, $"tracked board prime ({reason})", out rawFen, out ownedSnapshot))
                    return;

                initialBoardSnapshot = ownedSnapshot;
            }

            if (string.IsNullOrWhiteSpace(rawFen) ||
                rawFen == "8/8/8/8/8/8/8/8 w KQkq - 0 1")
            {
                return;
            }

            string candidateFen = NormalizeExternalDetectedFen(rawFen, out bool? detectedBoardFlipped);
            if (!UCIEngine.IsFenStructurallySane(candidateFen, out string sanityReason))
            {
                LogDiag("WINTRACK", $"prime skipped invalid FEN ({sanityReason}) raw={rawFen}");
                return;
            }

            ApplyExternalDisplayOrientation(detectedBoardFlipped, $"tracked board prime ({reason})");

            string confirmedFen = MergeDetectedFenWithHistory(_currentFEN, candidateFen);
            if (string.Equals(confirmedFen, _currentFEN, StringComparison.Ordinal))
            {
                if (initialBoardSnapshot != null)
                {
                    UpdateConfirmedBoardSnapshot(initialBoardSnapshot);
                }

                bool staticHighlightChangedSide =
                    initialBoardSnapshot != null &&
                    TryApplyStaticLastMoveHighlightTurnHintAndQueue(_currentFEN, initialBoardSnapshot);

                if (!staticHighlightChangedSide &&
                    _continuousAnalysisEnabled &&
                    !_showingMoves &&
                    _currentMoveArrows == null &&
                    !IsActiveAnalysisBoardFen(_currentFEN))
                {
                    ResetAnalysisSchedulingState();
                    TryQueueAnalysis(GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective), force: true);
                }
                return;
            }

            ResetPendingFenCandidate();
            ApplyConfirmedFen(confirmedFen, initialBoardSnapshot, beginNoiseGuard: false, logPrefix: "[WINTRACK FEN]");
            _analysisBoardController!.MirrorExternalFen(ApplyInferredExternalTurnToFen(confirmedFen), _externalBoardDetectedFlipped);
        }
        catch (Exception ex)
        {
            LogDiag("WINTRACK", $"prime FEN failed ({reason}): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ownedSnapshot?.Dispose();
        }
    }

    private static void DetachExternalTrackingForForegroundChange(IntPtr foreground)
    {
        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"foreground switched away from tracked board to hwnd=0x{foreground.ToInt64():X}; no board found there");
        }

        try { CancelPendingAnalysis("foreground window changed"); } catch { }
        CancelStaticLastMoveHighlightInitialHold();

        _trackedHwnd = IntPtr.Zero;
        _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
        _foregroundMismatchFenGuardUntilUtc = DateTime.MinValue;
        ClearForegroundNoBoardOverlayHold("foreground tracking detached");
        _lostHwndCache = IntPtr.Zero;
        _trackingLostWaitingForReacquire = false;
        _lastTrackedBox = null;
        _boardHistory.Clear();
        _boardLostFrames = 0;
        _boardContentLostFrames = 0;
        _invalidFenFrames = 0;
        _requestBoardRefresh = false;
        _currentFenIsAnalysisBoard = false;
        _analysisTargetIsAnalysisBoard = false;
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        Interlocked.Increment(ref _arrowRenderToken);
        _currentFEN = "";
        ResetConfirmedBoardSnapshot();
        ResetPendingFenCandidate();
        ResetAnalysisSchedulingState();
        HideOverlaysVisually();
        RefreshDebugView("Foreground window has no board");
    }

    private static bool TryPrimeExternalBoardTracking()
    {
        if (_detector == null)
            return false;

        bool temporarilyMinimized = false;
        try
        {
            Rect? boardRect = CaptureAndDetectExternalBoard();

            if ((!boardRect.HasValue || ShouldIgnoreDetectedAnalysisBoard(boardRect.Value)) &&
                IsAnalysisBoardVisibleForExternalPriming())
            {
                temporarilyMinimized = TryTemporarilyMinimizeAnalysisBoardForExternalPriming();
                if (temporarilyMinimized)
                {
                    Thread.Sleep(140);
                    boardRect = CaptureAndDetectExternalBoard();
                }
            }

            if (!boardRect.HasValue || ShouldIgnoreDetectedAnalysisBoard(boardRect.Value))
                return false;

            _analysisTargetIsAnalysisBoard = false;
            _currentFenIsAnalysisBoard = false;
            _boardLostFrames = 0;
            _invalidFenFrames = 0;
            _requestBoardRefresh = false;
            _lastTrackedBox = SmoothBoardPosition(boardRect.Value);

            _evalBar?.SetBoardVisible(true);
            _engineLines?.SetBoardVisible(true);
            _settingsToolbar?.SetBoardVisible(true);
            return true;
        }
        catch (Exception ex)
        {
            Log($"[WARN] External board priming failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (temporarilyMinimized)
                RestoreAnalysisBoardAfterExternalPriming();
        }
    }

    private static void SchedulePrimeExternalBoardTracking(string reason)
    {
        if (_detector == null || Interlocked.Exchange(ref _externalPrimeInFlight, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                LogDiag("WINTRACK", $"background prime requested: {reason}");
                TryPrimeExternalBoardTracking();
            }
            catch (Exception ex)
            {
                LogDiag("WINTRACK", $"background prime failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _externalPrimeInFlight, 0);
            }
        });
    }

    private static Rect? CaptureAndDetectExternalBoard()
    {
        using var fullBmp = ScreenCapture.CaptureVirtualScreen(out var fullScreenBounds);
        using var fullFrame = BitmapConverter.ToMat(fullBmp);

        Rect? boardRect = DetectExternalBoardInFullFrame(
            fullFrame,
            fullScreenBounds,
            boardDetectionMinIntervalMs: _boardDetectionAcquireMinIntervalMs);
        if (boardRect.HasValue &&
            !ShouldIgnoreDetectedAnalysisBoard(boardRect.Value) &&
            !ShouldRejectBoardBecauseNotForeground(boardRect.Value) &&
            IsPlausibleExternalBoardCandidate(boardRect.Value, "masked full-screen probe"))
        {
            return boardRect;
        }

        if (_detector == null)
            return null;

        System.Drawing.Point mouse = Control.MousePosition;
        var preferredPoint = new System.Drawing.Point(
            mouse.X - fullScreenBounds.Left,
            mouse.Y - fullScreenBounds.Top);
        Rect? unmaskedBoardRect = _detector.DetectBoard(fullFrame, preferredPoint, minIntervalOverrideMs: 0);
        if (unmaskedBoardRect.HasValue)
        {
            Rect detected = unmaskedBoardRect.Value;
            unmaskedBoardRect = NormalizeExternalBoardRect(new Rect(
                fullScreenBounds.Left + detected.X,
                fullScreenBounds.Top + detected.Y,
                detected.Width,
                detected.Height));
        }

        if (unmaskedBoardRect.HasValue &&
            !ShouldIgnoreDetectedAnalysisBoard(unmaskedBoardRect.Value) &&
            !ShouldRejectBoardBecauseNotForeground(unmaskedBoardRect.Value) &&
            IsPlausibleExternalBoardCandidate(unmaskedBoardRect.Value, "unmasked full-screen probe"))
        {
            return unmaskedBoardRect;
        }

        return null;
    }

    private static bool IsPlausibleExternalBoardCandidate(Rect boardRect, string source)
    {
        bool ok = TryCapturePlausibleExternalBoardCandidate(boardRect, source, out _, out var boardSnapshot);
        boardSnapshot?.Dispose();
        return ok;
    }

    private static bool TryCapturePlausibleExternalBoardCandidate(Rect boardRect, string source, out string fen, out Mat? boardSnapshot)
    {
        fen = "";
        boardSnapshot = null;

        if (_detector == null)
            return false;

        if (boardRect.Width < 180 || boardRect.Height < 180)
            return false;

        try
        {
            Rectangle virtualScreen = GetVirtualScreenBounds();
            Rectangle wanted = Rectangle.Inflate(
                new Rectangle(boardRect.X, boardRect.Y, boardRect.Width, boardRect.Height),
                20,
                20);
            Rectangle captureRegion = Rectangle.Intersect(wanted, virtualScreen);
            if (captureRegion.IsEmpty)
                return false;

            int localX = boardRect.X - captureRegion.X;
            int localY = boardRect.Y - captureRegion.Y;
            if (localX < 0 || localY < 0 ||
                localX + boardRect.Width > captureRegion.Width ||
                localY + boardRect.Height > captureRegion.Height)
            {
                LogDiag("WINTRACK", $"rejected {source} board candidate: capture bounds clipped");
                return false;
            }

            using var frame = ScreenCapture.CaptureRegionMatGdi(captureRegion, out var actualCaptureRegion);
            if (frame.Empty() || actualCaptureRegion.IsEmpty)
                return false;

            int actualLocalX = boardRect.X - actualCaptureRegion.X;
            int actualLocalY = boardRect.Y - actualCaptureRegion.Y;
            if (actualLocalX < 0 || actualLocalY < 0 ||
                actualLocalX + boardRect.Width > actualCaptureRegion.Width ||
                actualLocalY + boardRect.Height > actualCaptureRegion.Height)
            {
                LogDiag("WINTRACK", $"rejected {source} board candidate: actual capture bounds clipped");
                return false;
            }

            var localBoardRect = new Rect(actualLocalX, actualLocalY, boardRect.Width, boardRect.Height);
            fen = _detector.ProcessBoard(frame, localBoardRect, false);
            if (string.IsNullOrWhiteSpace(fen) ||
                fen == "8/8/8/8/8/8/8/8 w KQkq - 0 1")
            {
                LogDiag("WINTRACK", $"rejected {source} board candidate: empty FEN");
                return false;
            }

            if (!UCIEngine.IsFenStructurallySane(fen, out string reason))
            {
                LogDiag("WINTRACK", $"rejected {source} board candidate: invalid FEN ({reason}) {fen}");
                return false;
            }

            using var boardView = new Mat(frame, localBoardRect);
            boardSnapshot = boardView.Clone();
            LogDiag("WINTRACK", $"accepted {source} board candidate after FEN sanity check: {fen}");
            return true;
        }
        catch (Exception ex)
        {
            LogDiag("WINTRACK", $"rejected {source} board candidate: FEN validation failed ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }

    private static bool TryCaptureExternalBoardSnapshot(Rect boardRect, out Mat? boardSnapshot)
    {
        boardSnapshot = null;

        if (boardRect.Width < 180 || boardRect.Height < 180)
            return false;

        try
        {
            Rectangle virtualScreen = GetVirtualScreenBounds();
            Rectangle wanted = Rectangle.Inflate(
                new Rectangle(boardRect.X, boardRect.Y, boardRect.Width, boardRect.Height),
                20,
                20);
            Rectangle captureRegion = Rectangle.Intersect(wanted, virtualScreen);
            if (captureRegion.IsEmpty)
                return false;

            using var frame = ScreenCapture.CaptureRegionMatGdi(captureRegion, out var actualCaptureRegion);
            if (frame.Empty() || actualCaptureRegion.IsEmpty)
                return false;

            int actualLocalX = boardRect.X - actualCaptureRegion.X;
            int actualLocalY = boardRect.Y - actualCaptureRegion.Y;
            if (actualLocalX < 0 || actualLocalY < 0 ||
                actualLocalX + boardRect.Width > actualCaptureRegion.Width ||
                actualLocalY + boardRect.Height > actualCaptureRegion.Height)
            {
                return false;
            }

            using var boardView = new Mat(frame, new Rect(actualLocalX, actualLocalY, boardRect.Width, boardRect.Height));
            boardSnapshot = boardView.Clone();
            return true;
        }
        catch
        {
            boardSnapshot?.Dispose();
            boardSnapshot = null;
            return false;
        }
    }

    private static bool IsAnalysisBoardVisibleForExternalPriming()
    {
        lock (_analysisBoardStateLock)
        {
            if (_analysisBoardVisible && !_analysisBoardScreenRect.IsEmpty)
                return true;
        }

        return _analysisBoardForm?.Visible == true && _analysisBoardForm.WindowState != FormWindowState.Minimized;
    }

    private static bool TryTemporarilyMinimizeAnalysisBoardForExternalPriming()
    {
        var form = _analysisBoardForm;
        if (form == null || form.IsDisposed)
            return false;

        try
        {
            if (form.InvokeRequired)
            {
                return (bool)form.Invoke(new Func<bool>(() => form.TryTemporarilyMinimizeForExternalPriming()));
            }

            return form.TryTemporarilyMinimizeForExternalPriming();
        }
        catch
        {
            return false;
        }
    }

    private static void RestoreAnalysisBoardAfterExternalPriming()
    {
        var form = _analysisBoardForm;
        if (form == null || form.IsDisposed)
            return;

        try
        {
            if (form.InvokeRequired)
            {
                form.BeginInvoke(new Action(form.RestoreAfterExternalPriming));
            }
            else
            {
                form.RestoreAfterExternalPriming();
            }
        }
        catch
        {
            // Best effort only. Failing to restore the helper board should not
            // break live-board attachment.
        }
    }

    private static Rect? DetectExternalBoardInFullFrame(
        Mat fullFrame,
        Rectangle frameScreenBounds,
        bool preferTrackedBoard = false,
        int? boardDetectionMinIntervalMs = null)
    {
        if (_detector == null || fullFrame.Empty())
            return null;

        using var maskedFrame = fullFrame.Clone();
        MaskAnalysisBoardRegion(maskedFrame, frameScreenBounds);

        var preferredScreenPoint = preferTrackedBoard
            ? GetTrackedBoardPreferredPoint()
            : Control.MousePosition;
        var preferredPoint = new System.Drawing.Point(
            preferredScreenPoint.X - frameScreenBounds.Left,
            preferredScreenPoint.Y - frameScreenBounds.Top);
        Rect? boardRect = _detector.DetectBoard(maskedFrame, preferredPoint, boardDetectionMinIntervalMs);
        if (boardRect.HasValue)
        {
            Rect detected = boardRect.Value;
            boardRect = NormalizeExternalBoardRect(new Rect(
                frameScreenBounds.Left + detected.X,
                frameScreenBounds.Top + detected.Y,
                detected.Width,
                detected.Height));
        }

        if (boardRect.HasValue && ShouldIgnoreDetectedAnalysisBoard(boardRect.Value))
            return null;

        return boardRect;
    }

    private static System.Drawing.Point GetTrackedBoardPreferredPoint()
    {
        if (_lastTrackedBox.HasValue)
        {
            Rect tracked = _lastTrackedBox.Value;
            return new System.Drawing.Point(
                tracked.X + tracked.Width / 2,
                tracked.Y + tracked.Height / 2);
        }

        return Control.MousePosition;
    }

    private static Rect? TryDetectBoardNearLastKnownPosition(Rect lastKnownRect, int? boardDetectionMinIntervalMs = null)
    {
        Rectangle searchRegion = GetLocalBoardSearchRegion(lastKnownRect);
        using var localFrame = ScreenCapture.CaptureRegionMatGdi(searchRegion, out var actualSearchRegion);
        if (localFrame.Empty() || actualSearchRegion.IsEmpty)
            return null;

        MaskAnalysisBoardRegion(localFrame, actualSearchRegion);

        System.Drawing.Point mouse = Control.MousePosition;
        var preferredPoint = new System.Drawing.Point(
            mouse.X - actualSearchRegion.X,
            mouse.Y - actualSearchRegion.Y);
        Rect? localBoardRect = _detector?.DetectBoard(localFrame, preferredPoint, boardDetectionMinIntervalMs);
        if (!localBoardRect.HasValue)
            return null;

        Rect detected = localBoardRect.Value;
        return NormalizeExternalBoardRect(new Rect(
            actualSearchRegion.X + detected.X,
            actualSearchRegion.Y + detected.Y,
            detected.Width,
            detected.Height));
    }

    private static void MaskAnalysisBoardRegion(Mat frame, Rectangle frameScreenBounds)
    {
        if (frame.Empty())
            return;

        Rectangle exclusion;
        lock (_analysisBoardStateLock)
        {
            bool formVisible = _analysisBoardForm?.Visible == true && _analysisBoardForm.WindowState != FormWindowState.Minimized;
            if (!_analysisBoardVisible && !formVisible)
                return;

            exclusion = GetAnalysisBoardExternalDetectionExclusionRect();
            if (exclusion.IsEmpty && formVisible)
            {
                exclusion = Rectangle.Inflate(_analysisBoardForm!.Bounds, 32, 32);
            }
        }

        if (exclusion.IsEmpty)
            return;

        Rectangle overlap = Rectangle.Intersect(frameScreenBounds, exclusion);
        if (overlap.IsEmpty)
            return;

        int x = Math.Max(0, overlap.Left - frameScreenBounds.Left);
        int y = Math.Max(0, overlap.Top - frameScreenBounds.Top);
        int right = Math.Min(frame.Width, overlap.Right - frameScreenBounds.Left);
        int bottom = Math.Min(frame.Height, overlap.Bottom - frameScreenBounds.Top);
        if (right <= x || bottom <= y)
            return;

        Cv2.Rectangle(frame, new Rect(x, y, right - x, bottom - y), Scalar.Black, -1);
    }

    private static Rectangle GetLocalBoardSearchRegion(Rect lastKnownRect)
    {
        int padding = Math.Max(
            _localBoardSearchMinPaddingPx,
            (int)(Math.Max(lastKnownRect.Width, lastKnownRect.Height) * _localBoardSearchPaddingFactor));

        Rectangle screenBounds = GetVirtualScreenBounds();

        int x = Math.Max(screenBounds.Left, lastKnownRect.X - padding);
        int y = Math.Max(screenBounds.Top, lastKnownRect.Y - padding);
        int right = Math.Min(screenBounds.Right, lastKnownRect.X + lastKnownRect.Width + padding);
        int bottom = Math.Min(screenBounds.Bottom, lastKnownRect.Y + lastKnownRect.Height + padding);

        return Rectangle.FromLTRB(x, y, right, bottom);
    }

}
