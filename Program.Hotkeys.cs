using ChessKit;

// Hotkey command dispatch, performance metrics, mouse/obstruction gating, arrow clearing.
//
// The hotkey *input* layer (the key listeners, binding state, duplicate-press
// suppression, and key->command resolution) lives in
// <see cref="ChessKit.HotkeyController"/>. The controller raises
// CommandTriggered, which Program wires to HandleHotkeyCommand below.
partial class Program
{
    private static void HandleHotkeyCommand(HotkeyCommand command)
    {
        Log($"[KEY] {command} triggered");

        switch (command)
        {
            case HotkeyCommand.ToggleOverlay:
                if (TryRecoverHiddenSettingsToolbarFromToggleHotkey())
                    break;

                // Toggle overlay on/off
                if (!_isTracking)
                {
                    TryEnableOverlayTrackingForUserAction("overlay and live board detection");
                }
                else
                {
                    _isTracking = false;
                    ResetPendingFenCandidate();
                    ResetConfirmedStateTimeline();
                    ResetConfirmedBoardSnapshot();
                    ResetAnalysisSchedulingState();

                    // Disabling the overlay should fully stop analysis and clear
                    // any arrows that were on screen - both the underlying
                    // arrow data and the rendered overlay. Without this,
                    // hitting F1 to disable then F1 to re-enable would leave
                    // stale arrows that reappear briefly until the next
                    // analysis cycle, and analysis state could carry over
                    // wait flags or session info that confuse the next
                    // session.
                    BumpAnalysisSessionVersion();
                    _continuousAnalysisEnabled = false;
                    _waitingForOpponentMove = false;
                    _analysisInProgress = false;
                    _currentMoveArrows = null;
                    _lastAnalysisVariations = null;
                    _lastArrowSourceFEN = "";
                    // Clear orientation lock too so the next time the overlay is
                    // re-enabled, orientation re-evaluates from scratch.
                    _externalOrientationLockedForCurrentGame = false;
                    _externalTrackedPositionCount = 0;
                    _orientationConfirmStreakCount = 0;
                    // Drop window tracking - next F1-on starts fresh.
                    _trackedHwnd = IntPtr.Zero;
                    _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
                    _framesSinceWindowTrackVerify = 0;
                    // Clear lost-tracking state too - F1 is the user's
                    // explicit "reset" gesture. After F1-off then F1-on,
                    // analysis should be ready to acquire any board
                    // window (including a freshly-opened one with a new
                    // HWND).
                    _trackingLostWaitingForReacquire = false;
                    _lostHwndCache = IntPtr.Zero;
                    _lostAcquisitionCandidateSinceUtc = null;
                    _lastLostIconicLogged = false;
                    _lastLostVisibleLogged = false;
                    _minimizeEndFiredForLostHwnd = false;
                    ClearActiveArrows();
                    ClearExternalArrows();
                    _analysisTimer?.Dispose();
                    _analysisTimer = null;
                    _settingsToolbar?.SyncAnalysisState("OFF");

                    _overlay?.HideOverlay();
                    _evalBar?.SetEnabled(false);
                    _engineLines?.SetEnabled(false);
                    _settingsToolbar?.SetEnabled(false);
                    Log($"[{DateTime.Now:HH:mm:ss}] Overlay DISABLED");
                    RefreshDebugView("Overlay disabled");
                }
                break;

            case HotkeyCommand.AnalyzeWhite:
                // Toggle continuous analysis - WHITE perspective  
                if (!_isTracking)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Overlay must be enabled first - press F1");
                    return;
                }
                if (_stockfish == null)
                {
                    if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                        RememberPendingLiveAnalysisAfterEngineStart(isBoth: false, blackPerspective: false);
                    return;
                }
                if (_stockfish != null)
                {
                    _stockfish.ClearAllDepthTracking();
                    ToggleContinuousAnalysis(false);
                }
                else
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Engine not available");
                }
                break;

            case HotkeyCommand.AnalyzeBlack:
                // Toggle continuous analysis - BLACK perspective
                if (!_isTracking)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Overlay must be enabled first - press F1");
                    return;
                }
                if (_stockfish != null)
                {
                    _stockfish.ClearAllDepthTracking();
                    ToggleContinuousAnalysis(true);
                }
                else
                {
                    if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                        RememberPendingLiveAnalysisAfterEngineStart(isBoth: false, blackPerspective: true);
                }
                break;

            case HotkeyCommand.AnalyzeBoth:
                // Toggle continuous analysis - both sides
                if (!_isTracking)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Overlay must be enabled first - press F1");
                    return;
                }

                if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                {
                    RememberPendingLiveAnalysisAfterEngineStart(isBoth: true, blackPerspective: _analysisIsBlackPerspective);
                    return;
                }

                if (_stockfish != null)
                {
                    _stockfish.ClearAllDepthTracking();

                    if (_analysisBothEnabled && _continuousAnalysisEnabled)
                    {
                        _analysisBothEnabled = false;
                        ToggleContinuousAnalysis(_analysisIsBlackPerspective);
                    }
                    else
                    {
                        _analysisBothEnabled = true;
                        bool desiredBlackPerspective = _currentFenIsAnalysisBoard
                            ? false
                            : GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
                        ToggleContinuousAnalysis(desiredBlackPerspective);
                    }
                }
                else
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Engine not available");
                }
                break;

            case HotkeyCommand.CopyFen:
                // Copy FEN
                if (!string.IsNullOrEmpty(_currentFEN))
                {
                    var thread = new Thread(() =>
                    {
                        try { Clipboard.SetText(_currentFEN); }
                        catch { }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                    Log($"[{DateTime.Now:HH:mm:ss}] FEN copied: {_currentFEN}");
                }
                break;

            case HotkeyCommand.ToggleEngineLines:
                // Toggle engine lines display
                _engineLinesEnabled = !_engineLinesEnabled;

                if (_settingsToolbar != null)
                {
                    _settingsToolbar.SyncEngineLinesState(_engineLinesEnabled);
                }

                if (_engineLines != null)
                {
                    if (_engineLinesEnabled)
                    {
                        if (_isTracking)
                        {
                            _engineLines.SetEnabled(true);
                            if (_lastTrackedBox.HasValue)
                            {
                                var r = _lastTrackedBox.Value;
                                _engineLines.UpdatePosition(new Rectangle(r.X, r.Y, r.Width, r.Height));
                                if (_lastAnalysisVariations != null)
                                {
                                    var limitedVariations = _lastAnalysisVariations.Take(_maxArrowCount).ToList();
                                    _engineLines.UpdateVariations(limitedVariations, _analysisIsBlackPerspective);
                                }
                            }
                            Log($"[{DateTime.Now:HH:mm:ss}] Engine lines ENABLED");
                        }
                        else
                        {
                            Log($"[{DateTime.Now:HH:mm:ss}] Engine lines enabled (waiting for overlay)");
                        }
                    }
                    else
                    {
                        _engineLines.SetEnabled(false);
                        Log($"[{DateTime.Now:HH:mm:ss}] Engine lines DISABLED");
                    }
                }
                break;

            case HotkeyCommand.ToggleEvalBar:
                // Toggle evaluation bar
                _evalBarEnabled = !_evalBarEnabled;

                if (_settingsToolbar != null)
                {
                    _settingsToolbar.SyncEvalBarState(_evalBarEnabled);
                }

                if (_evalBar != null)
                {
                    if (_evalBarEnabled)
                    {
                        if (_isTracking)
                        {
                            _evalBar.SetEnabled(true);
                            if (_lastTrackedBox.HasValue)
                            {
                                var r = _lastTrackedBox.Value;
                                _evalBar.UpdatePosition(new Rectangle(r.X, r.Y, r.Width, r.Height));
                                _evalBar.UpdateEvaluation(_lastEvaluation);
                            }
                            Log($"[{DateTime.Now:HH:mm:ss}] Evaluation bar ENABLED");
                        }
                        else
                        {
                            Log($"[{DateTime.Now:HH:mm:ss}] Evaluation bar enabled (waiting for overlay)");
                        }
                    }
                    else
                    {
                        _evalBar.SetEnabled(false);
                        Log($"[{DateTime.Now:HH:mm:ss}] Evaluation bar DISABLED");
                    }
                }
                break;
        }
    }

    private static bool TryRecoverHiddenSettingsToolbarFromToggleHotkey()
    {
        DateTime nowUtc = DateTime.UtcNow;
        bool isDoubleTap = (nowUtc - _lastToggleOverlayHotkeyUtc).TotalMilliseconds <= ToggleOverlayToolbarRecoveryDoubleTapMs;
        _lastToggleOverlayHotkeyUtc = nowUtc;

        if (!_settingsToolbarHidden || !isDoubleTap)
            return false;

        Log("[Toolbar] Hidden settings bar restored by double-tap overlay hotkey");
        RestoreSettingsToolbarFromTaskbar();
        return true;
    }

    private static void UpdatePerformanceMetrics()
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastPerfUpdate).TotalSeconds;

        if (elapsed >= 1.0)
        {
            if (_isTracking)
            {
                // Displayed "FPS" = actual vision roundtrips/s (data really sent to
                // the server), not capture-loop passes - the loop spins at ~60 even
                // when the board is unchanged and nothing is uploaded, which made
                // the HUD read 60 while idle. Capture rate stays in the log below.
                _currentFps = BoardVisionDetector.GetVisionRoundtripsPerSecond();
                _currentFenPerSec = _fenCount / elapsed;

                double captureFps = _frameCount / elapsed;
                Log($"[METRICS] Frames: {_frameCount}, FENs: {_fenCount}, Elapsed: {elapsed:F2}s");
                Log($"[METRICS] Vision RT/s: {_currentFps:F1}, CaptureFps: {captureFps:F1}, FEN/s: {_currentFenPerSec:F1}");
            }
            else
            {
                _currentFps = 0;
                _currentFenPerSec = 0;
            }

            _frameCount = 0;
            _fenCount = 0;
            _lastPerfUpdate = now;

            _settingsToolbar?.UpdatePerformanceMetrics(_currentFps, _currentFenPerSec, BoardVisionDetector.GetNetworkMetricsSnapshot(), RemoteEngineClient.GetFreeStateSnapshot());

            // Surface WHY arrows aren't showing (and self-heal a never-started
            // engine) so a Free/suspended session is never left with a silently
            // blank overlay. Cheap, runs once/sec, no-ops when arrows are flowing.
            _settingsToolbar?.SetAnalysisStatusHint(ComputeLiveAnalysisStatusHintAndSelfHeal());

            // Mirror feedback: when the analysis board is mirroring but the source
            // board is covered or not yet detected, the capture can't read it and
            // the mirror silently freezes — surface why on the board itself.
            UpdateAnalysisBoardMirrorPausedHint();

            // Keep the overlay eval bar's orientation in sync with the displayed board,
            // so the side at the bottom (White normally, Black when flipped) fills upward.
            _evalBar?.SetBoardFlipped(_currentFenIsAnalysisBoard ? _analysisBoardIsFlipped : _externalBoardDetectedFlipped);

            RefreshDebugView();

            string analysisStatus = _continuousAnalysisEnabled ?
                $"Analysis: {(_analysisBothEnabled ? "BOTH" : (_analysisIsBlackPerspective ? "BLACK" : "WHITE"))}" :
                "Analysis: OFF";

            string menuStatus = _menuExpanded ? " | Menu: OPEN" : "";

            UpdateConsoleTitle($"Chess Kit | Engine: {GetCurrentEngineDisplayName()} | FPS: {_currentFps:F1} | {_executionMode} | {analysisStatus}{menuStatus}");

            // Push the same snapshot to the floating debug HUD if it's open.
            if (_debugHudPresenter.IsEnabled)
            {
                string analysisSide = _continuousAnalysisEnabled
                    ? (_analysisBothEnabled ? "BOTH" : (_analysisIsBlackPerspective ? "BLACK" : "WHITE"))
                    : "OFF";
                int arrows = _currentMoveArrows?.Count ?? 0;
                _debugHudPresenter.UpdateMetrics(
                    fps: _currentFps,
                    fenPerSec: _currentFenPerSec,
                    captureMode: ScreenCapture.GetCaptureMode(),
                    engineName: GetCurrentEngineDisplayName(),
                    executionMode: _executionMode,
                    tracking: _isTracking,
                    boardTracked: _lastTrackedBox.HasValue,
                    analysisOn: _continuousAnalysisEnabled,
                    analysisSide: analysisSide,
                    arrowCount: arrows,
                    waitingForOpponent: _waitingForOpponentMove,
                    lastMoveLatencyMs: _lastMoveLatencyMs,
                    lastEvent: _lastDebugEvent ?? "",
                    lastEngineFen: _lastFenSentToEngine ?? "",
                    streamTransport: BoardVisionDetector.CurrentTransport,
                    mirrorActive: _analysisBoardController?.IsMirrorEnabled ?? false);
            }
        }
    }

    // Pushes the "mirror paused" hint to the analysis board when mirror mode is on
    // but the source board can't be read: covered by another window, or no source
    // detected yet. The screen capture only sees what is visually on top, so a
    // covered source silently freezes the mirror; this explains why.
    private static void UpdateAnalysisBoardMirrorPausedHint()
    {
        var controller = _analysisBoardController;
        if (controller == null)
            return;

        if (!controller.IsMirrorEnabled)
        {
            controller.UpdateMirrorPausedHint(false, "");
            return;
        }

        bool hasSource = _trackedHwnd != IntPtr.Zero &&
                         _lastTrackedBox.HasValue &&
                         WindowTracker.IsTrackable(_trackedHwnd);

        if (!hasSource)
        {
            controller.UpdateMirrorPausedHint(true,
                "Mirror paused — waiting for a source board. Open the game you want to mirror.");
        }
        else if (_boardObscuredLastFrame)
        {
            controller.UpdateMirrorPausedHint(true,
                "Mirror paused — keep the source board visible (it's covered by another window).");
        }
        else
        {
            controller.UpdateMirrorPausedHint(false, "");
        }
    }

    private static void PrintInstructions()
    {
#if DEBUG
        RefreshDebugView("Waiting for input");
#else
        // Release builds have no console/debug view; the floating toolbar is
        // the only status surface, so there is nothing to print here.
#endif
    }

    private static void RefreshDebugView(string? lastEvent = null)
    {
#if DEBUG
        DebugRuntime.UpdateStatus(new DebugConsoleSnapshot
        {
            IsTracking = _isTracking,
            AnalysisEnabled = _continuousAnalysisEnabled,
            AnalysisSide = _continuousAnalysisEnabled ? (_analysisBothEnabled ? "BOTH" : (_analysisIsBlackPerspective ? "BLACK" : "WHITE")) : "OFF",
            WaitingForOpponent = _waitingForOpponentMove,
            BoardTracked = _lastTrackedBox.HasValue,
            ShowingArrows = _showingMoves,
            ArrowCount = _currentMoveArrows?.Count ?? 0,
            Fps = _currentFps,
            FenPerSecond = _currentFenPerSec,
            CurrentFen = _currentFEN,
            LastUserMoveFen = _lastUserMoveFEN,
            ExecutionMode = _executionMode,
            CurrentEngine = GetCurrentEngineDisplayName(),
            CaptureMode = ScreenCapture.GetCaptureMode()
        }, lastEvent);
#else
        _ = lastEvent;
#endif
    }

    private static bool ShouldPauseFenDetectionForMouseInteraction()
    {
        bool leftDown = MouseInput.IsMouseButtonDown(0x01);
        bool rightDown = MouseInput.IsMouseButtonDown(0x02);
        bool isMouseDown = leftDown || rightDown;
        var now = DateTime.UtcNow;
        int postMouseReleaseDelayMs = GetFenPostMouseReleaseDelayMs();

        if (isMouseDown)
        {
            if (!_mouseButtonWasDown)
            {
                ArrowTimeline.Log("FEN_PAUSE", reason: "mouse down");
                Log("[INPUT] Mouse button down - pausing FEN detection");
                RefreshDebugView("Mouse button down - pausing FEN detection");
            }

            _mouseButtonWasDown = true;
            _fenDetectionPausedUntilUtc = now.AddMilliseconds(postMouseReleaseDelayMs);
            _recentMouseInteractionUntilUtc = now.AddMilliseconds(_moverInferenceMouseGuardMs);
            _suppressOptimisticFenAfterMouseUntilUtc = now.AddMilliseconds(_localMouseOptimisticFenSuppressMs);
            return true;
        }

        if (_mouseButtonWasDown)
        {
            _mouseButtonWasDown = false;
            ArrowTimeline.Log("FEN_RESUME", reason: "mouse up", ms: postMouseReleaseDelayMs);
            Log($"[INPUT] Mouse button up - waiting {postMouseReleaseDelayMs}ms before FEN detection");
            RefreshDebugView("Mouse button up - waiting for board to settle");
            _recentMouseInteractionUntilUtc = now.AddMilliseconds(_moverInferenceMouseGuardMs);
            _suppressOptimisticFenAfterMouseUntilUtc = now.AddMilliseconds(_localMouseOptimisticFenSuppressMs);
            BeginTransitionNoiseGuard(GetPostInteractionNoiseIgnoreMs());
        }

        return now < _fenDetectionPausedUntilUtc;
    }

    private static int GetFenPostMouseReleaseDelayMs()
        => BlitzFenPostMouseReleaseDelayMs;

    private static bool ShouldPauseFenDetectionForObstructingUi()
    {
        var now = DateTime.UtcNow;
        if (_obstructingUiActive)
        {
            // Keep refreshing the grace window while popup is open, so when
            // it closes the grace period is _obstructingUiGraceMs from THAT
            // moment, not from when it opened.
            _obstructingUiGraceUntilUtc = now.AddMilliseconds(_obstructingUiGraceMs);
            return true;
        }
        return now < _obstructingUiGraceUntilUtc;
    }

    private static void SetObstructingUiActive(bool active, bool preserveAnalysis = false)
    {
        if (active)
        {
            bool wasActive = _obstructingUiActive;
            bool wasSoft = _obstructingUiPreservesAnalysis;
            bool shouldStaySoft = preserveAnalysis && (!wasActive || wasSoft);
            bool needsHardReset = !preserveAnalysis && (!wasActive || wasSoft);

            _obstructingUiActive = true;
            _obstructingUiPreservesAnalysis = shouldStaySoft;

            if (!wasActive)
            {
                string mode = shouldStaySoft ? "soft" : "hard";
                Log($"[INPUT] Obstructing UI shown ({mode}) - pausing board detection");
                RefreshDebugView("Obstructing UI shown - pausing board detection");
            }

            if (needsHardReset)
            {
                // Cancel any in-flight analysis. Its result would apply to a
                // position the engine can't currently see anyway, and the next
                // confirmed FEN after the popup closes will trigger a fresh one.
                CancelPendingAnalysis("obstructing UI shown");
                // Clear arrows so we don't render stale arrows over a partially
                // occluded board; they'd look glitchy as the user interacts
                // with the popup.
                ClearActiveArrows();
                _showingMoves = false;
            }
        }
        else
        {
            if (!_obstructingUiActive) return;
            bool wasSoft = _obstructingUiPreservesAnalysis;
            _obstructingUiActive = false;
            _obstructingUiPreservesAnalysis = false;
            _obstructingUiGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(_obstructingUiGraceMs);
            if (wasSoft)
                SuppressForegroundBoardSwitchesBriefly("soft obstructing UI dismissed");

            // Re-prime the noise guard so any transient FENs caught during
            // the board's repaint after popup-close are ignored.
            BeginTransitionNoiseGuard(GetPostInteractionNoiseIgnoreMs());
            // Clear pending FEN candidate so we don't accept a half-detected
            // frame from during the obstruction.
            ResetPendingFenCandidate();
            Log($"[INPUT] Obstructing UI dismissed - {_obstructingUiGraceMs}ms grace before resuming");
            RefreshDebugView("Obstructing UI dismissed - waiting for repaint");
        }
    }

    private static void ClearDisplayedArrowsForPositionChange(bool allowHoldForPendingSwap = true)
    {
        bool hadVisibleArrows = _showingMoves || DateTime.UtcNow < _externalArrowHoldUntilUtc;
        Interlocked.Increment(ref _arrowDisplayGeneration);
        Interlocked.Increment(ref _arrowRenderToken);
        _lastArrowSourceFEN = "";
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        ClearDisplayedArrowDepthMemory();
        _showingMoves = false;
        _suppressCachedArrowRecoveryUntilUtc = DateTime.UtcNow.AddMilliseconds(CachedArrowRecoverySuppressAfterPositionClearMs);
        _lastExternalArrowResultReadyUtc = DateTime.MinValue;

        if (_currentFenIsAnalysisBoard)
        {
            ClearActiveArrows("position change (analysis board)");
        }
        else if (allowHoldForPendingSwap &&
            hadVisibleArrows &&
            _stockfish != null &&
            !_waitingForOpponentMove)
        {
            // Hold the previous arrows on screen and let the next
            // ApplyAnalysisResult swap them in atomically, instead of clearing
            // to a blank and repainting. The logical state above is already
            // cleared - stale renders are token-guarded.
            //
            // This applies to BOTH local and remote engines. It was originally
            // remote-only (the round-trip made the clear->repaint blank
            // obvious), but a side-by-side test showed the LOCAL path's
            // instant-clear also flickers after the user's own move: the mouse
            // drag pauses detection, so the old arrows briefly sit stale on the
            // new board, then the clear blanks them, then ~150ms later the fast
            // local result repaints - a visible blink. With humanuci (remote)
            // the user saw NO flicker precisely because the hold-then-swap was
            // active. Local analysis just makes the hold shorter (the swap
            // lands sooner), so the seamless swap is strictly better here too.
            HoldExternalArrowsForPendingSwap();
        }
        else
        {
            // The inner log gate cannot fire here (_showingMoves was already
            // reset above), so record the visible clear explicitly.
            if (hadVisibleArrows)
            {
                ArrowTimeline.Log("ARROW_CLEARED", fen: _currentFEN, reason: "position change (no hold)");
            }
            ClearExternalArrows(reason: "position change");
        }
    }

    private static void HoldExternalArrowsForPendingSwap()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (nowUtc < _externalArrowHoldUntilUtc)
        {
            // A hold from an earlier position change is still pending. The
            // staleness bound tracks the age of the VISIBLE content: if new
            // arrows were painted since that hold began, what is on screen is
            // fresh, so the deadline rebases to the paint time; otherwise the
            // earlier deadline stands so rapid changes cannot extend truly
            // stale arrows. (Measured: the unrebased deadline blinked out
            // arrows painted 184ms earlier mid move-burst.)
            if (_lastExternalArrowsShownUtc > _externalArrowHoldStartUtc)
            {
                DateTime rebased = _lastExternalArrowsShownUtc.AddMilliseconds(ExternalArrowSwapGraceMs);
                if (rebased > _externalArrowHoldUntilUtc)
                {
                    _externalArrowHoldUntilUtc = rebased;
                    _externalArrowHoldStartUtc = _lastExternalArrowsShownUtc;
                }
                ArrowTimeline.Log("ARROW_HOLD", fen: _currentFEN, reason: "chained - deadline rebased to last paint");
            }
            else
            {
                ArrowTimeline.Log("ARROW_HOLD", fen: _currentFEN, reason: "chained - earlier deadline kept");
            }
            return;
        }

        DateTime holdStartUtc = nowUtc;
        _externalArrowHoldStartUtc = nowUtc;
        _externalArrowHoldUntilUtc = nowUtc.AddMilliseconds(ExternalArrowSwapGraceMs);
        ArrowTimeline.Log("ARROW_HOLD", fen: _currentFEN, reason: "position change - awaiting remote result");
        Task.Run(async () =>
        {
            await Task.Delay(ExternalArrowSwapGraceMs);
            // The deadline may have been rebased forward by a fresh paint in a
            // chained change. If it is still in the future, this task is the
            // stale one: leave the field alone (the refresh loop's null-arrows
            // clear is the backstop once the rebased deadline passes).
            if (DateTime.UtcNow < _externalArrowHoldUntilUtc)
                return;
            _externalArrowHoldUntilUtc = DateTime.MinValue;
            var overlay = _overlay;
            if (overlay == null)
                return;
            try
            {
                // Revalidate on the UI thread, serialized with arrow draws:
                // a result arriving near the deadline either painted already
                // (_showingMoves / result-ready timestamps advanced) or its
                // queued draw carries a newer generation than the hide below,
                // so in either message order the fresh arrows survive. The
                // hide deliberately does NOT bump the generation or touch
                // program state - the logical state was cleared at hold start.
                overlay.BeginInvoke(new Action(() =>
                {
                    if (_showingMoves)
                        return;
                    // A result that arrived but could not draw (tracking lost
                    // between apply and the draw action) must not disarm the
                    // hide, or the held arrows float until the overlay's 60s
                    // expiry; only a result that can still display counts.
                    if (_lastExternalArrowResultReadyUtc > holdStartUtc && CanDisplayArrowsForCurrentState())
                        return;
                    ArrowTimeline.Log("ARROW_HOLD_EXPIRED", fen: _currentFEN, ms: ExternalArrowSwapGraceMs);
                    overlay.HideArrows(Volatile.Read(ref _arrowDisplayGeneration));
                }));
            }
            catch
            {
                // Overlay may be closing; nothing to hide.
            }
        });
    }

    private static void ClearStaleExternalArrowsOnRawBoardChange(int changedSquares)
    {
        if (changedSquares < 2 ||
            _currentFenIsAnalysisBoard ||
            !_continuousAnalysisEnabled ||
            _orientationPromptVisible)
        {
            return;
        }

        if (IsTrackedWindowResizeSettling())
        {
            if (_overlay != null && _showingMoves && _lastTrackedBox.HasValue)
            {
                var r = _lastTrackedBox.Value;
                _overlay.BeginInvoke(new Action(() =>
                    _overlay.SetBoardScreenPosition(new Rectangle(r.X, r.Y, r.Width, r.Height))));
            }
            LogDiag("ARROWS", $"ignored raw board change during resize settle ({changedSquares} squares)");
            return;
        }

        bool hasDisplayedOrCachedArrows =
            _showingMoves ||
            _currentMoveArrows is { Count: > 0 } ||
            _lastAnalysisVariations is { Count: > 0 } ||
            !string.IsNullOrWhiteSpace(_lastArrowSourceFEN);

        if (!hasDisplayedOrCachedArrows)
            return;

        DateTime now = DateTime.UtcNow;
        int clearCooldownMs = BlitzRawBoardChangeArrowClearCooldownMs;
        if (now < _lastRawBoardChangeArrowClearUtc.AddMilliseconds(clearCooldownMs))
            return;

        _lastRawBoardChangeArrowClearUtc = now;

        if (changedSquares >= 12 && IsForegroundDetachedFromTrackedBoardForArrowSafety())
        {
            ClearDisplayedArrowsForPositionChange(allowHoldForPendingSwap: false);
            LogDiag("ARROWS", $"cleared arrows during foreground/no-board raw board change ({changedSquares} squares)");
            return;
        }

        // Do not hide/abort on raw pixels alone. Streamed boards and live
        // mouse drags can change several squares without a real committed
        // move: the hand lifts a piece, hovers it, then puts it back. Hiding
        // here caused the Release-build "blink in place" experience because
        // the confirmed FEN was still the old position, so the same arrows
        // were immediately restored. Confirmed FEN changes still clear via
        // ClearDisplayedArrowsForPositionChange; this path just keeps the
        // existing arrows visually stable while the detector waits for a
        // repeated, structurally sane FEN.
        if (_overlay != null && _showingMoves && _lastTrackedBox.HasValue)
        {
            var r = _lastTrackedBox.Value;
            _overlay.BeginInvoke(new Action(() =>
                _overlay.SetBoardScreenPosition(new Rectangle(r.X, r.Y, r.Width, r.Height))));
        }

        LogDiag("ARROWS", $"holding arrows during raw board change ({changedSquares} squares)");
    }

    private static bool IsForegroundDetachedFromTrackedBoardForArrowSafety()
    {
        if (_trackedHwnd == IntPtr.Zero)
            return false;

        IntPtr foreground = WindowTracker.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _trackedHwnd)
            return false;

        return IsForegroundNoBoardOverlayHoldActiveFor(foreground) ||
               IsForegroundBoardProbeBackedOffFor(foreground) ||
               DateTime.UtcNow < _foregroundMismatchFenGuardUntilUtc;
    }

    private static void RecoverFromIllegalInfiniteAnalysis(string capturedFEN, bool isBlackPerspective, int analysisSessionVersion, string stageLabel)
    {
        var engine = _stockfish;
        if (engine?.InfiniteAnalysis != true || IsActiveAnalysisBoardFen(capturedFEN))
            return;

        LogDiag("ENGINE", $"stopping stale infinite stream after illegal {stageLabel} PV for {capturedFEN}");
        Task.Run(async () =>
        {
            try
            {
                await engine.AbortCurrentAnalysisAsync();
            }
            catch (Exception ex)
            {
                LogDiag("ENGINE", $"stale infinite recovery abort failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await Task.Delay(80);
                if (GetAnalysisSessionVersion() == analysisSessionVersion &&
                    _continuousAnalysisEnabled &&
                    string.Equals(_currentFEN, capturedFEN, StringComparison.Ordinal))
                {
                    TryQueueAnalysis(isBlackPerspective, force: true);
                }
            }
        });
    }

    private static void ClearActiveArrows(string? reason = null)
    {
        // The built-in analysis board owns its own analysis lifecycle. Global/external
        // analysis clears must not wipe its preview lines when a browser board starts.
        // Log only when something was plausibly visible: this runs every frame
        // from the refresh loop while idle, which would otherwise flood the
        // timeline with no-op clears.
        if (_showingMoves || _externalArrowHoldUntilUtc != DateTime.MinValue)
        {
            ArrowTimeline.Log("ARROW_CLEARED", fen: _currentFEN, reason: reason ?? "unspecified");
        }
        _externalArrowHoldUntilUtc = DateTime.MinValue;
        ResetCoachOverlayStability();
        Interlocked.Increment(ref _arrowRenderToken);
        Interlocked.Increment(ref _arrowDisplayGeneration);
        _showingMoves = false;
        ClearExternalOverlayArrowMemory();
        if (_overlay != null)
        {
            int generation = _arrowDisplayGeneration;
            Action clearAction = () =>
            {
                bool keepFreeLimitWatermark =
                    BuildLimits.IsFreeEdition &&
                    IsFreeExternalAnalysisLimitReached() &&
                    CanDrawExternalBoardOverlay();
                _overlay.HideArrows(generation, preserveFreeLimitWatermark: keepFreeLimitWatermark);
                _showingMoves = false;
            };

            if (_overlay.InvokeRequired)
            {
                try { _overlay.BeginInvoke(clearAction); }
                catch { _showingMoves = false; }
            }
            else
            {
                clearAction();
            }
        }
        else
        {
            _showingMoves = false;
        }
    }

    private static void ClearExternalArrows(bool preserveFreeLimitWatermark = false, string? reason = null)
    {
        if (_showingMoves || _externalArrowHoldUntilUtc != DateTime.MinValue)
        {
            ArrowTimeline.Log("ARROW_CLEARED", fen: _currentFEN, reason: reason ?? "external clear");
        }
        _externalArrowHoldUntilUtc = DateTime.MinValue;
        ResetCoachOverlayStability();
        Interlocked.Increment(ref _arrowRenderToken);
        Interlocked.Increment(ref _arrowDisplayGeneration);
        _showingMoves = false;
        ClearExternalOverlayArrowMemory();
        if (_overlay != null)
        {
            int generation = _arrowDisplayGeneration;
            Action clearAction = () =>
            {
                bool keepFreeLimitWatermark = preserveFreeLimitWatermark ||
                    (BuildLimits.IsFreeEdition && IsFreeExternalAnalysisLimitReached() && CanDrawExternalBoardOverlay());
                _overlay.HideArrows(generation, preserveFreeLimitWatermark: keepFreeLimitWatermark);
                _showingMoves = false;
            };

            if (_overlay.InvokeRequired)
            {
                try { _overlay.BeginInvoke(clearAction); }
                catch { _showingMoves = false; }
            }
            else
            {
                clearAction();
            }
        }
        else
        {
            _showingMoves = false;
        }
    }

}
