using ChessKit;
using Chess;
using static ChessKit.FenText;

// Analysis scheduling, FEN confirmation, optimistic correction, legal turn transitions.
partial class Program
{
    private static void ResetAnalysisSchedulingState()
    {
        _lastQueuedAnalysisKey = "";
        _nextAnalysisAttemptUtc = DateTime.MinValue;
    }

    private static void DeferNextAnalysisAttempt(int delayMs)
    {
        DateTime candidate = DateTime.UtcNow.AddMilliseconds(delayMs);
        if (candidate > _nextAnalysisAttemptUtc)
            _nextAnalysisAttemptUtc = candidate;
    }

    private static volatile bool _cancelAbortInFlight = false;
    private static volatile bool _liveEngineSettingsInFlight = false;
    private static int _liveEngineSettingsGeneration = 0;
    private static int _arrowCountSettingsGeneration = 0;

    private static void CancelPendingAnalysis(string reason)
    {
        BumpAnalysisSessionVersion();
        var workerCancellation = Interlocked.Exchange(ref _analysisCancellation, null);
        if (workerCancellation != null)
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try { ((CancellationTokenSource)state!).Cancel(); } catch { }
            }, workerCancellation);
        }
        _analysisInProgress = false;
        ResetAnalysisSchedulingState();
        // Don't defer the next analysis. The defer was originally a 350ms
        // wait to let the async abort complete - but it caused legitimate
        // post-position-change analysis requests to be silently rejected,
        // and TryQueueAnalysis(force:true) now handles the IsAnalyzing
        // race directly via a retry. Deferring on top would just delay
        // opponent-move analysis by several hundred ms for no benefit.

        if (_stockfish != null && !_stockfish.IsAnalyzing && !_stockfish.IsInfiniteAnalysisRunning)
        {
            LogDiag("CANCEL", $"abort skipped (idle, reason={reason})");
        }
        else if (_stockfish != null && !_cancelAbortInFlight)
        {
            // Debounce: only one abort task at a time. If we're already
            // waiting on a previous abort to complete (which takes ~50ms),
            // don't spawn another. The session-version bump above already
            // discards stale results from any in-flight analyses, so we
            // don't need a second abort to stop them. Sending multiple
            // "stop" commands in rapid succession can destabilize some
            // engine versions over many cycles.
            _cancelAbortInFlight = true;
            LogDiag("CANCEL", $"abort spawn ({reason})");
            Task.Run(async () =>
            {
                try
                {
                    await _stockfish.AbortCurrentAnalysisAsync();
                    Log($"[UCIEngine] Analysis abort requested ({reason})");
                }
                catch (Exception ex)
                {
                    LogDiag("CANCEL", $"abort threw: {ex.GetType().Name}: {ex.Message}");
                    Log($"[UCIEngine] Analysis abort failed ({reason}): {ex.Message}");
                }
                finally
                {
                    _cancelAbortInFlight = false;
                }
            });
        }
        else if (_cancelAbortInFlight)
        {
            LogDiag("CANCEL", $"abort skipped (in-flight, reason={reason})");
        }
    }

    private static int BumpAnalysisSessionVersion()
    {
        return Interlocked.Increment(ref _analysisSessionVersion);
    }

    private static int GetAnalysisSessionVersion()
    {
        return Volatile.Read(ref _analysisSessionVersion);
    }

    private static void ApplyLiveEngineSetting(Func<UCIEngine, Task> applySetting, string reason, bool clearDepthTracking = false)
    {
        var engine = _stockfish;
        if (engine == null)
            return;

        int generation = Interlocked.Increment(ref _liveEngineSettingsGeneration);
        _liveEngineSettingsInFlight = true;
        BumpAnalysisSessionVersion();
        _analysisInProgress = false;
        _lastQueuedAnalysisKey = "";
        ResetAnalysisSchedulingState();
        ClearDisplayedArrowDepthMemory();

        if (clearDepthTracking)
        {
            if (_currentFenIsAnalysisBoard)
            {
                ClearActiveArrows();
            }
            else
            {
                ClearExternalArrows();
            }
        }

        Task.Run(async () =>
        {
            try
            {
                await engine.AbortCurrentAnalysisAsync();
                if (generation != Volatile.Read(ref _liveEngineSettingsGeneration) || !ReferenceEquals(engine, _stockfish))
                    return;

                await applySetting(engine);
                if (generation != Volatile.Read(ref _liveEngineSettingsGeneration) || !ReferenceEquals(engine, _stockfish))
                    return;

                bool ready = await engine.EnsureReadyAsync(1500);
                if (!ready)
                    LogDiag("SETTINGS", $"ready check timed out after live engine setting ({reason})");

                if (clearDepthTracking)
                    engine.ClearAllDepthTracking();

                Log($"[Settings] {reason}");
            }
            catch (Exception ex)
            {
                LogDiag("SETTINGS", $"live engine setting failed ({reason}): {ex.GetType().Name}: {ex.Message}");
                Log($"[Settings] Failed to apply engine setting ({reason}): {ex.Message}");
            }
            finally
            {
                if (generation == Volatile.Read(ref _liveEngineSettingsGeneration))
                {
                    _liveEngineSettingsInFlight = false;
                    QueueExternalAnalysisAfterUiSettles($"settings changed: {reason}");
                }
            }
        });
    }

    private static void ScheduleArrowCountEngineSetting(int multipv)
    {
        var engine = _stockfish;
        if (engine == null)
            return;

        int generation = Interlocked.Increment(ref _arrowCountSettingsGeneration);
        _liveEngineSettingsInFlight = true;
        BumpAnalysisSessionVersion();
        _analysisInProgress = false;
        _lastQueuedAnalysisKey = "";
        ResetAnalysisSchedulingState();

        if (_currentFenIsAnalysisBoard)
        {
            ClearActiveArrows();
        }
        else
        {
            ClearExternalArrows();
        }

        Task.Run(async () =>
        {
            await Task.Delay(180);
            if (generation != Volatile.Read(ref _arrowCountSettingsGeneration))
                return;

            ApplyLiveEngineSetting(
                liveEngine => liveEngine.SendCommandAsync($"setoption name MultiPV value {multipv}"),
                $"Engine MultiPV updated to: {multipv}");
        });
    }

    private static void QueueExternalAnalysisAfterUiSettles(string reason)
    {
        if (!_continuousAnalysisEnabled || IsActiveAnalysisBoardFen(_currentFEN) || IsExternalBoardOutputSuspended())
            return;

        string fen = _currentFEN;
        bool perspective = _analysisIsBlackPerspective;

        Task.Run(async () =>
        {
            await Task.Delay(_obstructingUiGraceMs + 120);

            if (_obstructingUiActive || _menuExpanded || _trackingLostWaitingForReacquire)
                return;
            if (!_continuousAnalysisEnabled || IsActiveAnalysisBoardFen(_currentFEN) || _currentFEN != fen || IsExternalBoardOutputSuspended())
                return;

            _analysisInProgress = false;
            ResetAnalysisSchedulingState();
            LogDiag("SETTINGS", $"requeue external analysis after {reason}");
            TryQueueAnalysis(perspective, force: true);
        });
    }

    private static bool TryQueueAnalysis(bool isBlackPerspective, bool force = false)
    {
        if (!EnsureLicensedFeatureAvailable("external board analysis"))
            return false;

        int analysisRunId;
        CancellationTokenSource analysisCancellation;
        bool lockTaken = false;
        try
        {
            if (!Monitor.TryEnter(_analysisLock, AnalysisLockAcquireTimeoutMs))
            {
                if (force)
                {
                    string retryFen = _currentFEN;
                    bool retryPerspective = isBlackPerspective;
                    int retrySessionVersion = GetAnalysisSessionVersion();
                    Task.Run(async () =>
                    {
                        await Task.Delay(90);
                        if (GetAnalysisSessionVersion() != retrySessionVersion) return;
                        if (_trackingLostWaitingForReacquire) return;
                        if (_currentFEN != retryFen) return;
                        if (!_continuousAnalysisEnabled) return;
                        TryQueueAnalysis(retryPerspective, force: true);
                    });
                }

                LogDiag("ENGINE", $"analysis queue skipped: analysis lock busy (force={force})");
                return false;
            }

            lockTaken = true;

            if (!_continuousAnalysisEnabled || _analysisInProgress || string.IsNullOrEmpty(_currentFEN) || !_isTracking)
            {
                LogDiag(
                    "ENGINE",
                    $"analysis queue skipped: enabled={_continuousAnalysisEnabled} inProgress={_analysisInProgress} hasFen={!string.IsNullOrEmpty(_currentFEN)} tracking={_isTracking}");
                return false;
            }

            // Free cooldown gate: while the SERVER has this Free session in a
            // cooldown, stop issuing NEW analysis. The pinned watermark keeps
            // counting down locally; we refresh it here so it ticks even though
            // nothing else fires, and analysis auto-resumes once the countdown
            // hits zero. Licensed sessions are never in cooldown. Existing arrows
            // may persist until the next analysis (intentional - keep it simple).
            if (FreeTierServerState.IsInCooldown)
            {
                UpdateFreeExternalWatermark();
                LogDiag("ENGINE", $"analysis queue skipped: Free cooldown ({FreeTierServerState.CooldownRemainingSeconds}s left)");
                return false;
            }

            // No-window guard. Set when our board window minimizes,
            // hides, or closes; cleared only when vision re-acquires a
            // real top-level window. While set, every TryQueueAnalysis
            // call returns false. Without this gate the 250ms analysis-
            // watchdog timer fires after the cancel and re-shows arrows
            // on top of whatever's behind the board - the "vanish,
            // reappear for 2s" pattern. We can't rely on _lastTrackedBox
            // alone because vision can latch onto something visible
            // behind the minimized window (taskbar previews, fragments,
            // adjacent UI) and re-set _lastTrackedBox post-loss.
            if (_trackingLostWaitingForReacquire)
            {
                LogDiag("ENGINE", "analysis queue skipped: waiting for board reacquire");
                return false;
            }
            // Belt-and-braces: if _trackedHwnd is set but the OS now
            // reports it as un-trackable (e.g. system-event hook fired
            // but the per-frame poll hasn't cleaned state yet), still
            // refuse.
            if (_trackedHwnd != IntPtr.Zero && !WindowTracker.IsTrackable(_trackedHwnd))
            {
                LogDiag("ENGINE", "analysis queue skipped: tracked window not trackable");
                return false;
            }
            if (IsExternalBoardOutputSuspended())
            {
                LogDiag("ENGINE", "analysis skipped while external board output is suspended");
                return false;
            }

            string pendingConfirmedFen = Volatile.Read(ref _pendingConfirmedFenTarget);
            if (!string.IsNullOrWhiteSpace(pendingConfirmedFen))
            {
                if (force)
                {
                    bool retryPerspective = isBlackPerspective;
                    Task.Run(async () =>
                    {
                        await Task.Delay(45);
                        if (!string.IsNullOrWhiteSpace(Volatile.Read(ref _pendingConfirmedFenTarget))) return;
                        if (_trackingLostWaitingForReacquire) return;
                        if (!_continuousAnalysisEnabled) return;
                        TryQueueAnalysis(retryPerspective, force: true);
                    });
                }
                return false;
            }

            if (TryStartStaticLastMoveHighlightInitialHold(_currentFEN))
            {
                LogDiag("ENGINE", "analysis queue skipped: waiting for static last-move highlight");
                return false;
            }

            if (_liveEngineSettingsInFlight)
            {
                if (force)
                {
                    string retryFen = _currentFEN;
                    bool retryPerspective = isBlackPerspective;
                    Task.Run(async () =>
                    {
                        await Task.Delay(120);
                        if (_liveEngineSettingsInFlight) return;
                        if (_trackingLostWaitingForReacquire) return;
                        if (_currentFEN != retryFen) return;
                        if (!_continuousAnalysisEnabled) return;
                        TryQueueAnalysis(retryPerspective, force: true);
                    });
                }
                LogDiag("ENGINE", "analysis queue skipped: live engine settings in flight");
                return false;
            }

            if (_cancelAbortInFlight)
            {
                // The previous position/window-change cancel is still draining
                // Stockfish's output. Starting a new infinite search before
                // that stop/isready handshake finishes lets old PV lines be
                // parsed as if they belonged to the new FEN.
                if (force)
                {
                    string retryFen = _currentFEN;
                    bool retryPerspective = isBlackPerspective;
                    int retrySessionVersion = GetAnalysisSessionVersion();
                    Task.Run(async () =>
                    {
                        await Task.Delay(90);
                        if (GetAnalysisSessionVersion() != retrySessionVersion) return;
                        if (_trackingLostWaitingForReacquire) return;
                        if (_currentFEN != retryFen) return;
                        if (!_continuousAnalysisEnabled) return;
                        TryQueueAnalysis(retryPerspective, force: true);
                    });
                }
                LogDiag("ENGINE", "analysis queue skipped: cancel/abort in flight");
                return false;
            }
            if (_stockfish != null && _stockfish.IsAnalyzing)
            {
                // Engine is still in _analyzing state - typically because a
                // previous CancelPendingAnalysis fired an async abort that
                // hasn't completed yet (~50-100ms). When the caller passed
                // force:true, they explicitly asked for this to run; schedule
                // a brief retry so the request isn't silently dropped.
                if (force)
                {
                    string retryFen = _currentFEN;
                    bool retryPerspective = isBlackPerspective;
                    int retrySessionVersion = GetAnalysisSessionVersion();
                    Task.Run(async () =>
                    {
                        await Task.Delay(80);
                        // Skip the retry if any of these have changed since
                        // we scheduled it. The session-version check
                        // catches CancelPendingAnalysis (e.g. window
                        // minimized between scheduling and now); the
                        // _trackedHwnd check catches the case where the
                        // board window went away entirely; the FEN
                        // check catches a position move; the
                        // continuous-analysis check catches a settings
                        // toggle. Without these, a retry queued while
                        // a fresh analysis was busy can fire AFTER the
                        // window was minimized and re-show arrows on
                        // top of whatever's behind the board - the
                        // user-visible "vanish, reappear for 2s, vanish"
                        // pattern.
                        if (GetAnalysisSessionVersion() != retrySessionVersion) return;
                        if (_trackingLostWaitingForReacquire) return;
                        if (_currentFEN != retryFen) return;
                        if (!_continuousAnalysisEnabled) return;
                        TryQueueAnalysis(retryPerspective, force: true);
                    });
                }
                LogDiag("ENGINE", "analysis queue skipped: engine is still analyzing");
                return false;
            }

            if (!force && _nextAnalysisAttemptUtc != DateTime.MinValue && DateTime.UtcNow < _nextAnalysisAttemptUtc)
            {
                LogDiag("ENGINE", "analysis queue skipped: deferred next attempt window");
                return false;
            }

            if (HasUnresolvedOrientationPromptForCurrentPosition(_currentFEN))
            {
                RefreshDebugView("Waiting for orientation choice");
                LogDiag("ENGINE", "analysis queue skipped: unresolved orientation prompt");
                return false;
            }

            if (!TryResolveOrientationDecision(_currentFEN, IsActiveAnalysisBoardFen(_currentFEN), GetOrientationPromptReferenceColor(_currentFEN), out _))
            {
                LogDiag("ENGINE", "analysis queue skipped: orientation unresolved");
                return false;
            }

            bool effectiveIsBlackPerspective = GetRequestedAnalysisPerspective(_currentFEN, isBlackPerspective);
            _analysisIsBlackPerspective = effectiveIsBlackPerspective;
            bool bypassWaiting = ShouldBypassWaitingForExternalBothMode(_currentFEN);

            if ((!bypassWaiting && _waitingForOpponentMove) || ShouldSuppressArrowsForPosition(_currentFEN, effectiveIsBlackPerspective))
            {
                LogDiag("ENGINE", $"analysis queue skipped: waiting={_waitingForOpponentMove} bypass={bypassWaiting} suppress={ShouldSuppressArrowsForPosition(_currentFEN, effectiveIsBlackPerspective)} perspective={(effectiveIsBlackPerspective ? "b" : "w")}");
                return false;
            }

            if (TryGetTerminalExternalPositionReason(_currentFEN, out string terminalReason, timeoutMs: 90))
            {
                string terminalFen = _currentFEN;
                Task.Run(() => TryHandleTerminalExternalPosition(terminalFen));
                LogDiag("TURN", $"analysis skipped for terminal external position ({terminalReason})");
                return false;
            }

            // Refresh the server-driven Free watermark for live external boards.
            // The server governs the cap now (the cooldown gate above pauses when
            // it bites), so this only updates the "FREE · N moves left" display and
            // never blocks the request.
            if (!IsActiveAnalysisBoardFen(_currentFEN))
                TryConsumeFreeExternalPly(_currentFEN);

            string analysisKey = GetAnalysisRequestKey(_currentFEN, effectiveIsBlackPerspective);
            bool hasUsableRenderedAssistance =
                _currentMoveArrows != null &&
                _currentMoveArrows.Any() &&
                IsSameArrowSourcePosition(_currentFEN);
            if (_coachModeEnabled)
            {
                hasUsableRenderedAssistance =
                    (_showingMoves || _lastAnalysisVariations != null) &&
                    IsSameArrowSourcePosition(_currentFEN);
            }

            bool infiniteAnalysis = _stockfish?.InfiniteAnalysis == true;
            int requestedDepth = BuildLimits.ClampDepth(_stockfish?.MaxDepth ?? 0);
            int displayedDepth = GetStableDisplayedArrowDepth(_currentFEN);
            int externalInfiniteStableDepth = Math.Max(_quickArrowDepth, 4);
            bool displayedDepthComplete = infiniteAnalysis
                ? (_coachModeEnabled
                    ? displayedDepth >= GetExternalCoachTargetDepth()
                    : !IsActiveAnalysisBoardFen(_currentFEN) && displayedDepth >= externalInfiniteStableDepth)
                : requestedDepth <= 0 || displayedDepth >= requestedDepth;

            if (!force && hasUsableRenderedAssistance && _lastQueuedAnalysisKey == analysisKey && displayedDepthComplete)
            {
                LogDiag("ENGINE", $"analysis queue skipped: rendered assistance already complete depth={displayedDepth}/{requestedDepth}");
                return false;
            }

            _lastQueuedAnalysisKey = analysisKey;
            _analysisInProgress = true;
            analysisRunId = Interlocked.Increment(ref _analysisRunId);
            analysisCancellation = new CancellationTokenSource();
            var previousCancellation = Interlocked.Exchange(ref _analysisCancellation, analysisCancellation);
            try { previousCancellation?.Cancel(); } catch { }
            isBlackPerspective = effectiveIsBlackPerspective;
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(_analysisLock);
        }

        AnalyzePosition(isBlackPerspective, analysisRunId, analysisCancellation);
        return true;
    }

    // The Free move cap is gone client-side: the SERVER governs the limit (a
    // move-count window + cooldown reported on each analysis response). This is
    // now just the per-analysis hook to refresh the server-driven watermark; it
    // never blocks analysis (the cooldown gate in TryQueueAnalysis handles pausing).
    private static bool TryConsumeFreeExternalPly(string fen)
    {
        UpdateFreeExternalWatermark();
        return true;
    }

    // "Limit reached" now means the SERVER put this Free session into a cooldown.
    // Arrow-clear/suppress sites use this to keep the watermark pinned and hold
    // back arrows during the pause. Licensed sessions are never in cooldown.
    private static bool IsFreeExternalAnalysisLimitReached()
    {
        return FreeTierServerState.IsInCooldown;
    }

    // Pushes the latest SERVER-driven Free state to the overlay watermark:
    // "FREE · N moves left" while serving, and a pinned "Free limit reached ·
    // resets in M:SS" countdown during a cooldown. Licensed sessions clear it
    // (FreeTierServerState.IsFreeLimited stays false).
    private static void UpdateFreeExternalWatermark()
    {
        if (_overlay == null)
            return;

        bool armed = FreeTierServerState.IsFreeLimited;
        int remainingMoves = FreeTierServerState.FreeMovesRemaining;
        int cooldownSeconds = FreeTierServerState.CooldownRemainingSeconds;
        bool inCooldown = armed && cooldownSeconds > 0;

        Action update = () =>
        {
            // During cooldown, pin the notice over the board even with no arrows.
            if (inCooldown && _lastTrackedBox.HasValue)
            {
                var box = _lastTrackedBox.Value;
                _overlay.ShowFreeWatermark(
                    new System.Drawing.Rectangle(box.X, box.Y, box.Width, box.Height),
                    remainingMoves,
                    cooldownSeconds,
                    inCooldown: true);
            }
            else if (inCooldown && !CanDrawExternalBoardOverlay())
            {
                // Cooldown but the board isn't drawable right now; reflect state
                // anyway so it pins as soon as the board is back.
                _overlay.SetFreeWatermarkStatus(armed, remainingMoves, cooldownSeconds, inCooldown: true);
            }
            else
            {
                _overlay.SetFreeWatermarkStatus(armed, remainingMoves, cooldownSeconds, inCooldown);
            }
        };

        try
        {
            if (_overlay.InvokeRequired)
                _overlay.BeginInvoke(update);
            else
                update();
        }
        catch
        {
            // Overlay may be closing. Free state will be refreshed on next analysis.
        }
    }

    private static bool CanDrawExternalBoardOverlay()
    {
        return _lastTrackedBox.HasValue &&
            !_trackingLostWaitingForReacquire &&
            _trackedHwnd != IntPtr.Zero &&
            WindowTracker.IsTrackable(_trackedHwnd) &&
            !WindowTracker.IsBoardObscured(_trackedHwnd, _lastTrackedBox.Value);
    }

    private static void MaybeRecoverStableSparseArrows()
    {
        DateTime now = DateTime.UtcNow;
        if (now < _suppressCachedArrowRecoveryUntilUtc ||
            (!_currentFenIsAnalysisBoard &&
             !IsActiveAnalysisBoardFen(_currentFEN) &&
             now < _lastExternalArrowResultReadyUtc.AddMilliseconds(CachedArrowRecoverySuppressAfterResultReadyMs)))
        {
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        if (!_continuousAnalysisEnabled ||
            _analysisInProgress ||
            _orientationPromptVisible ||
            _waitingForOpponentMove ||
            string.IsNullOrEmpty(_currentFEN) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsExternalBoardOutputSuspended() ||
            !_lastTrackedBox.HasValue ||
            HasUnresolvedOrientationPromptForCurrentPosition(_currentFEN))
        {
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        if (_stockfish != null && _stockfish.IsAnalyzing)
        {
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        if (_showingMoves)
        {
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        bool hasCachedArrowsForCurrentFen =
            _currentMoveArrows != null &&
            _currentMoveArrows.Any() &&
            IsSameArrowSourcePosition(_currentFEN);

        if (hasCachedArrowsForCurrentFen && CanDisplayArrowsForCurrentState())
        {
            ArrowTimeline.Log("ARROW_CACHE_RECOVER", fen: _currentFEN);
            Log("[ARROWS] Recovering hidden arrows from cache for stable position");
            RefreshDisplayedArrows();
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        if (!IsSparseBoardPosition(_currentFEN))
        {
            _stableNoArrowSinceUtc = DateTime.MinValue;
            return;
        }

        if (_stableNoArrowSinceUtc == DateTime.MinValue)
        {
            _stableNoArrowSinceUtc = now;
            return;
        }

        if ((now - _stableNoArrowSinceUtc).TotalMilliseconds < 900)
            return;

        if ((now - _lastStableNoArrowRecoveryUtc).TotalMilliseconds < 1800)
            return;

        bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
        if (TryQueueAnalysis(requestedPerspective, force: true))
        {
            _lastStableNoArrowRecoveryUtc = now;
            Log("[ARROWS] Forced sparse-board recovery analysis");
        }
    }

    private static bool IsSparseBoardPosition(string fen)
    {
        string board = GetBoardPosition(fen);
        if (string.IsNullOrEmpty(board))
            return false;

        int pieces = 0;
        foreach (char c in board)
        {
            if (char.IsLetter(c))
                pieces++;
        }

        return pieces <= 6;
    }

    private static void ResetPendingFenCandidate()
    {
        _pendingFenCandidate = "";
        _pendingFenCandidateCount = 0;
        _pendingFenCandidateStartedUtc = DateTime.MinValue;
        _pendingFenCandidateLastSeenUtc = DateTime.MinValue;
    }

    private static void NoteCurrentExternalBoardObservation(string observedFen)
    {
        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        string observedBoard = GetBoardPosition(observedFen);
        if (!string.IsNullOrWhiteSpace(currentBoard) &&
            !string.IsNullOrWhiteSpace(observedBoard) &&
            string.Equals(currentBoard, observedBoard, StringComparison.Ordinal))
        {
            _lastCurrentExternalBoardObservationUtc = DateTime.UtcNow;
        }
    }

    private static void BeginOptimisticFenGuard(string predictedFen, string baseFen = "", string moveText = "")
    {
        DateTime now = DateTime.UtcNow;
        _optimisticFenGuardBoard = GetBoardPosition(predictedFen);
        _optimisticFenGuardUntilUtc = now.AddMilliseconds(_optimisticFenGuardMs);
        _rapidPostOptimisticMoveHoldUntilUtc = now.AddMilliseconds(GetRapidPostOptimisticMoveHoldMs());
        _lastOptimisticBaseFen = baseFen;
        _lastOptimisticPredictedFen = predictedFen;
        _lastOptimisticMoveText = moveText;
        _lastOptimisticFenAppliedUtc = string.IsNullOrWhiteSpace(baseFen)
            ? DateTime.MinValue
            : now;
    }

    private static int GetRapidPostOptimisticMoveHoldMs()
        => BlitzRapidPostOptimisticMoveHoldMs;

    // Fast detection recovery is the only profile: the app is designed for
    // the fastest time controls, and multi-ply legal bridging beats stalls
    // with the local engine too. (Formerly blitz-or-remote gated.)
    private static bool ShouldUseFastDetectionRecovery()
        => true;

    private static bool ShouldIgnorePostOptimisticFenObservation(string observedFen, out string reason)
    {
        reason = "";

        if (DateTime.UtcNow >= _optimisticFenGuardUntilUtc ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        string observedBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(observedBoard) ||
            string.Equals(observedBoard, GetBoardPosition(_currentFEN), StringComparison.Ordinal) ||
            string.Equals(observedBoard, _optimisticFenGuardBoard, StringComparison.Ordinal))
        {
            return false;
        }

        // A legal transition arriving inside the guard window is the normal
        // cadence of fast games (replies, recaptures, premoves), not noise.
        // This bypass was blitz-only; without it every quick reply fell to
        // the slow stable-confirm path - the measured "first moves smooth,
        // later moves linger" degradation as the game speeds up.
        if (CanFastConfirmLegalExternalFen(observedFen, out string postFastConfirmReason))
        {
            LogDiag("FEN", $"accepted post-fast legal transition ({postFastConfirmReason}) raw={observedBoard}");
            return false;
        }

        LegalTurnTransition? legalNextMove = TryDetermineExternalTurnTransitionByLegalPath(_currentFEN, observedFen, maxPlies: 1);
        char expectedSide = _inferredSideToMove == 'b' ? 'b' : 'w';
        if (legalNextMove != null &&
            legalNextMove.PlyCount == 1 &&
            legalNextMove.LastMover == expectedSide)
        {
            return false;
        }

        reason = legalNextMove == null
            ? $"not legal next move after fast FEN, expected={expectedSide}"
            : $"unexpected mover={legalNextMove.LastMover}, expected={expectedSide}";
        return true;
    }

    private static bool ShouldIgnoreRapidPostOptimisticMoveObservation(string observedFen, out string reason)
    {
        reason = "";

        if (DateTime.UtcNow >= _rapidPostOptimisticMoveHoldUntilUtc ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        string observedBoard = GetBoardPosition(observedFen);
        string currentBoard = GetBoardPosition(_currentFEN);
        if (string.IsNullOrWhiteSpace(observedBoard) ||
            string.Equals(observedBoard, currentBoard, StringComparison.Ordinal) ||
            string.Equals(observedBoard, _optimisticFenGuardBoard, StringComparison.Ordinal))
        {
            return false;
        }

        // Same legal-transition bypass as the post-fast guard above: without
        // it, a reply landing within the rapid window not only waited but
        // RESET the pending candidate, forcing the slow path to start over.
        if (CanFastConfirmLegalExternalFen(observedFen, out string fastConfirmReason))
        {
            LogDiag("FEN", $"accepted rapid post-fast legal transition ({fastConfirmReason}) raw={observedBoard}");
            return false;
        }

        reason = $"new board appeared {GetRapidPostOptimisticMoveHoldMs()}ms after fast FEN";
        ResetPendingFenCandidate();
        return true;
    }

    private static string ConfirmFenObservation(string observedFen)
    {
        if (string.IsNullOrEmpty(observedFen) || observedFen == _currentFEN)
        {
            ResetPendingFenCandidate();
            if (!string.IsNullOrEmpty(observedFen))
            {
                NoteCurrentExternalBoardObservation(observedFen);
                TraceBoard($"reuse current raw={GetBoardPosition(observedFen)}");
            }
            return observedFen;
        }

        bool externalLiveObservation =
            !_currentFenIsAnalysisBoard &&
            !string.IsNullOrWhiteSpace(_currentFEN) &&
            !IsActiveAnalysisBoardFen(_currentFEN) &&
            !IsActiveAnalysisBoardFen(observedFen);
        if (externalLiveObservation)
        {
            string currentBoard = GetBoardPosition(_currentFEN);
            string observedBoard = GetBoardPosition(observedFen);
            if (!string.IsNullOrWhiteSpace(currentBoard) &&
                !string.IsNullOrWhiteSpace(observedBoard) &&
                string.Equals(currentBoard, observedBoard, StringComparison.Ordinal))
            {
                NoteCurrentExternalBoardObservation(observedFen);
                ResetPendingFenCandidate();
                TraceBoard($"reuse current board raw={observedBoard}");
                return "";
            }
        }

        if (TryAcceptOptimisticCorrectionFen(observedFen, out string earlyOptimisticCorrectionFen))
        {
            return earlyOptimisticCorrectionFen;
        }

        if (ShouldIgnorePostOptimisticFenObservation(observedFen, out string optimisticIgnoreReason))
        {
            LogDiag("FEN", $"post-fast observation ignored ({optimisticIgnoreReason}) raw={GetBoardPosition(observedFen)}");
            return "";
        }

        if (ShouldIgnoreRapidPostOptimisticMoveObservation(observedFen, out string rapidOptimisticReason))
        {
            LogDiag("FEN", $"rapid post-fast observation ignored ({rapidOptimisticReason}) raw={GetBoardPosition(observedFen)}");
            return "";
        }

        ClearStaleExternalArrowsOnLargeExternalBoardJump(observedFen, "pre-sanity board-context check");

        // Reject structurally-impossible FENs (no king, pawn on rank 1/8,
        // etc.) before they can be confirmed. Fast window movement can
        // produce partial screen captures that YOLO interprets as nonsense
        // boards. Without this gate, the corrupt FEN gets confirmed as the
        // new current position, which clears the (still-valid) arrows
        // showing the user's previous analysis. Treating these as if no
        // observation happened keeps _currentFEN unchanged, arrows stay
        // on screen, and we wait for a real frame to arrive.
        if (!UCIEngine.IsFenStructurallySane(observedFen, out string sanityReason))
        {
            _lastFenRejectionUtc = DateTime.UtcNow;
            if (_diagLoggingEnabled)
            {
                LogDiag("FEN", $"observation rejected ({sanityReason}): {observedFen}");
            }
            // Don't blow away the current pending candidate - a transient
            // garbage frame between two valid observations of the same
            // new FEN shouldn't reset the confirmation count for that
            // valid FEN. Just ignore this frame.
            return "";
        }

        if (ShouldIgnoreForegroundMismatchFenObservation(observedFen, out string foregroundMismatchReason))
        {
            LogDiag("FEN", $"foreground-mismatch observation ignored ({foregroundMismatchReason}) raw={GetBoardPosition(observedFen)}");
            return "";
        }

        if (ShouldIgnoreSingleSquareColorFlipObservation(observedFen, out string colorFlipReason))
        {
            LogDiag("FEN", $"observation rejected ({colorFlipReason}) raw={GetBoardPosition(observedFen)}");
            return "";
        }

        if (TryAcceptFreshGameResetObservation(observedFen, out string freshGameFen))
        {
            return freshGameFen;
        }

        ClearStaleExternalArrowsOnUnconfirmedFenCandidate(observedFen, "candidate pending");

        int requiredConfirmations = GetRequiredFenConfirmationCount();
        bool needsStableNonLegalExternalConfirmation =
            ShouldRequireStableExternalFenConfirmation(observedFen, out string stableExternalReason);
        bool boardSwitchCandidate =
            IsLargeExternalBoardSwitchCandidate(observedFen, out int boardSwitchChangedSquares, out string boardSwitchReason);
        DateTime now = DateTime.UtcNow;

        if (needsStableNonLegalExternalConfirmation &&
            !boardSwitchCandidate &&
            ShouldRejectUnrecoverableNonLegalExternalFenJump(observedFen, stableExternalReason, out string nonLegalJumpReason))
        {
            _lastFenRejectionUtc = now;
            ResetPendingFenCandidate();
            LogDiag("FEN", $"observation rejected ({nonLegalJumpReason}) raw={GetBoardPosition(observedFen)}");
            return "";
        }

        if (needsStableNonLegalExternalConfirmation &&
            _pendingFenCandidate == observedFen &&
            _lastCurrentExternalBoardObservationUtc > _pendingFenCandidateStartedUtc)
        {
            double currentSeenMs = (now - _lastCurrentExternalBoardObservationUtc).TotalMilliseconds;
            ArrowTimeline.Log("VISION_CANDIDATE_RESET", reason: "current board re-observed", ms: currentSeenMs, extra: GetBoardPosition(observedFen));
            TraceBoard(
                $"reset non-legal external candidate after current-board observation " +
                $"currentSeenMs={currentSeenMs:F0} raw={GetBoardPosition(observedFen)}");
            ResetPendingFenCandidate();
        }

        if (_pendingFenCandidate == observedFen)
        {
            double repeatGapMs = _pendingFenCandidateLastSeenUtc == DateTime.MinValue
                ? 0
                : (now - _pendingFenCandidateLastSeenUtc).TotalMilliseconds;

            if (needsStableNonLegalExternalConfirmation &&
                _pendingFenCandidateLastSeenUtc != DateTime.MinValue &&
                repeatGapMs > ExternalNonLegalFenRepeatMaxGapMs)
            {
                _pendingFenCandidateCount = 1;
                _pendingFenCandidateStartedUtc = now;
                ArrowTimeline.Log("VISION_CANDIDATE_RESET", reason: "repeat gap", ms: repeatGapMs, extra: GetBoardPosition(observedFen));
                TraceBoard(
                    $"reset non-legal external candidate repeat gap={repeatGapMs:F0}/{ExternalNonLegalFenRepeatMaxGapMs}ms " +
                    $"raw={GetBoardPosition(observedFen)}");
            }
            else
            {
                _pendingFenCandidateCount++;
                TraceBoard($"candidate repeat count={_pendingFenCandidateCount}/{_fenConfirmationThreshold} raw={GetBoardPosition(observedFen)}");
            }

            _pendingFenCandidateLastSeenUtc = now;
        }
        else
        {
            _pendingFenCandidate = observedFen;
            _pendingFenCandidateCount = 1;
            _pendingFenCandidateStartedUtc = now;
            _pendingFenCandidateLastSeenUtc = now;
            string candidateBoard = GetBoardPosition(observedFen);
            // Only log when the candidate actually changed: oscillating reads
            // alternate between a handful of boards, and the alternation is
            // exactly what the timeline needs to show without flooding.
            if (!string.Equals(_lastLoggedVisionCandidateBoard, candidateBoard, StringComparison.Ordinal))
            {
                _lastLoggedVisionCandidateBoard = candidateBoard;
                ArrowTimeline.Log("VISION_CANDIDATE", fen: observedFen, extra: $"current={GetBoardPosition(_currentFEN)}");
            }
            TraceBoard($"candidate new raw={GetBoardPosition(observedFen)}");
        }

        if (requiredConfirmations > _fenConfirmationThreshold &&
            _pendingFenCandidateCount == 1)
        {
            if (CanFastConfirmLegalExternalFen(observedFen, out string fastConfirmReason))
            {
                requiredConfirmations = _fenConfirmationThreshold;
                LogDiag("FEN", $"fast confirm legal external transition ({fastConfirmReason}) raw={GetBoardPosition(observedFen)}");
            }
            else
            {
                LogDiag("FEN", $"fast confirm skipped ({fastConfirmReason}) raw={GetBoardPosition(observedFen)}");
            }
        }

        if (needsStableNonLegalExternalConfirmation)
        {
            requiredConfirmations = Math.Max(
                requiredConfirmations,
                boardSwitchCandidate ? ExternalBoardSwitchConfirmationThreshold : ExternalNonLegalFenConfirmationThreshold);
            string holdReason = boardSwitchCandidate
                ? $"external board switch candidate ({boardSwitchReason})"
                : stableExternalReason;
            TraceBoard($"holding non-legal external candidate ({holdReason}) count={_pendingFenCandidateCount}/{requiredConfirmations} raw={GetBoardPosition(observedFen)}");
        }

        if (_pendingFenCandidateCount >= requiredConfirmations)
        {
            string confirmedFen = _pendingFenCandidate;
            double confirmationMs = _pendingFenCandidateStartedUtc == DateTime.MinValue
                ? 0
                : (DateTime.UtcNow - _pendingFenCandidateStartedUtc).TotalMilliseconds;
            if (needsStableNonLegalExternalConfirmation &&
                confirmationMs < ExternalNonLegalFenConfirmationMinMs)
            {
                if (boardSwitchCandidate && confirmationMs >= ExternalBoardSwitchConfirmationMinMs)
                {
                    // Board switches should recover quickly. The generic non-legal
                    // debounce is tuned for single-frame move animation noise.
                }
                else
                {
                    int minMs = boardSwitchCandidate
                        ? ExternalBoardSwitchConfirmationMinMs
                        : ExternalNonLegalFenConfirmationMinMs;
                    TraceBoard($"holding non-legal external candidate age={confirmationMs:F0}/{minMs}ms raw={GetBoardPosition(observedFen)}");
                    return "";
                }
            }

            Log($"[PERF] FEN confirmed in {confirmationMs:F0}ms after {_pendingFenCandidateCount} matching frames");
            TraceBoard($"accepted raw={GetBoardPosition(confirmedFen)} confirmMs={confirmationMs:F0}");
            if (boardSwitchCandidate)
            {
                MarkAcceptedExternalBoardSwitch(confirmedFen, boardSwitchChangedSquares, confirmationMs);
            }
            ResetPendingFenCandidate();
            return confirmedFen;
        }

        TraceBoard($"ignored pending count={_pendingFenCandidateCount}/{requiredConfirmations} raw={GetBoardPosition(observedFen)}");
        return "";
    }

    private static bool TryAcceptFreshGameResetObservation(string observedFen, out string acceptedFen)
    {
        acceptedFen = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen) ||
            !IsFreshGameResetObservation(observedFen))
        {
            return false;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        string observedBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(currentBoard) ||
            string.IsNullOrWhiteSpace(observedBoard) ||
            string.Equals(currentBoard, observedBoard, StringComparison.Ordinal))
        {
            return false;
        }

        bool legalFromCurrent = CanFastConfirmLegalExternalFen(observedFen, out string legalReason);
        if (legalFromCurrent)
            return false;

        int changedSquares = CountChangedBoardSquares(currentBoard, observedBoard);
        ResetPendingFenCandidate();
        ResetOutOfTurnCandidate();
        ClearOptimisticCorrectionState();
        _freshGameResetUntilUtc = DateTime.UtcNow.AddMilliseconds(_freshGameResetWindowMs);
        _lastCurrentExternalBoardObservationUtc = DateTime.MinValue;
        _lastUserMoveFEN = "";
        _lastArrowSourceFEN = "";
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        ResetConfirmedStateTimeline();
        ClearDisplayedArrowDepthMemory();
        ClearExternalArrows();

        LogDiag(
            "FEN",
            $"fresh game/opening reset accepted ({legalReason}, changedSquares={changedSquares}) raw={observedBoard}");

        acceptedFen = observedFen;
        return true;
    }

    private static bool HasDisplayedOrCachedExternalArrows()
    {
        return _showingMoves ||
               _currentMoveArrows is { Count: > 0 } ||
               _lastAnalysisVariations is { Count: > 0 } ||
               !string.IsNullOrWhiteSpace(_lastArrowSourceFEN) ||
               HasExternalVisibleArrowMemory();
    }

    private static bool TryGetLargeExternalBoardJumpInfo(
        string observedFen,
        out int changedSquares,
        out string currentBoard,
        out string observedBoard)
    {
        changedSquares = 0;
        currentBoard = "";
        observedBoard = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        currentBoard = GetBoardPosition(_currentFEN);
        observedBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(currentBoard) ||
            string.IsNullOrWhiteSpace(observedBoard) ||
            string.Equals(currentBoard, observedBoard, StringComparison.Ordinal))
        {
            return false;
        }

        changedSquares = CountChangedBoardSquares(currentBoard, observedBoard);
        return changedSquares >= ExternalBoardSwitchChangedSquaresThreshold;
    }

    private static bool IsLargeExternalBoardSwitchCandidate(
        string observedFen,
        out int changedSquares,
        out string reason)
    {
        reason = "";
        if (!TryGetLargeExternalBoardJumpInfo(observedFen, out changedSquares, out _, out _))
            return false;

        if (IsTrackedWindowResizeSettling() || IsExternalBoardGeometryUnstable())
        {
            reason = $"geometry unstable, changedSquares={changedSquares}";
            return false;
        }

        if (CanFastConfirmLegalExternalFen(observedFen, out string fastConfirmReason))
        {
            reason = $"legal transition ({fastConfirmReason})";
            return false;
        }

        reason = $"changedSquares={changedSquares}, {fastConfirmReason}";
        return true;
    }

    private static void ClearStaleExternalArrowsOnLargeExternalBoardJump(string observedFen, string reason)
    {
        if (!_continuousAnalysisEnabled ||
            _currentFenIsAnalysisBoard ||
            _orientationPromptVisible ||
            IsTrackedWindowResizeSettling() ||
            !HasDisplayedOrCachedExternalArrows() ||
            !TryGetLargeExternalBoardJumpInfo(observedFen, out int changedSquares, out _, out string observedBoard))
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _lastUnconfirmedFenArrowClearUtc.AddMilliseconds(UnconfirmedFenArrowClearCooldownMs))
            return;

        _lastUnconfirmedFenArrowClearUtc = now;
        // A large content jump means a different game/board context; holding
        // the previous context's arrows over it would be actively misleading.
        ClearDisplayedArrowsForPositionChange(allowHoldForPendingSwap: false);
        string message =
            $"cleared arrows for external board-context jump ({reason}, changedSquares={changedSquares}) raw={observedBoard}";
        Log($"[ARROWS] {message}");
        LogDiag("ARROWS", message);
    }

    private static void MarkAcceptedExternalBoardSwitch(string confirmedFen, int changedSquares, double confirmationMs)
    {
        _acceptedExternalBoardSwitchFen = confirmedFen;
        _acceptedExternalBoardSwitchUntilUtc = DateTime.UtcNow.AddMilliseconds(ExternalBoardSwitchAcceptWindowMs);

        _lastUserMoveFEN = "";
        _lastArrowSourceFEN = "";
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _waitingForOpponentMove = false;
        _externalOrientationLockedForCurrentGame = false;
        _externalTrackedPositionCount = 0;
        _lastCurrentExternalBoardObservationUtc = DateTime.MinValue;
        ResetConfirmedStateTimeline();
        ClearDisplayedArrowDepthMemory();
        ClearExternalArrows();

        Log($"[FEN] external board switch accepted changedSquares={changedSquares} confirmMs={confirmationMs:F0} raw={GetBoardPosition(confirmedFen)}");
        LogDiag("FEN", $"external board switch accepted changedSquares={changedSquares} confirmMs={confirmationMs:F0} fen={confirmedFen}");
    }

    private static bool IsAcceptedExternalBoardSwitchFen(string fen)
    {
        if (DateTime.UtcNow > _acceptedExternalBoardSwitchUntilUtc ||
            string.IsNullOrWhiteSpace(_acceptedExternalBoardSwitchFen) ||
            string.IsNullOrWhiteSpace(fen))
        {
            return false;
        }

        return string.Equals(
            GetBoardPosition(_acceptedExternalBoardSwitchFen),
            GetBoardPosition(fen),
            StringComparison.Ordinal);
    }

    private static void ClearAcceptedExternalBoardSwitch()
    {
        _acceptedExternalBoardSwitchFen = "";
        _acceptedExternalBoardSwitchUntilUtc = DateTime.MinValue;
    }

    private static bool TryAcceptOptimisticCorrectionFen(string observedFen, out string correctedFen)
    {
        correctedFen = "";

        if (string.IsNullOrWhiteSpace(_lastOptimisticBaseFen) ||
            string.IsNullOrWhiteSpace(_lastOptimisticPredictedFen) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            _lastOptimisticFenAppliedUtc == DateTime.MinValue ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        double optimisticAgeMs = (now - _lastOptimisticFenAppliedUtc).TotalMilliseconds;
        if (optimisticAgeMs < OptimisticCorrectionMinAgeMs)
            return false;

        if (optimisticAgeMs > OptimisticCorrectionWindowMs)
        {
            ClearOptimisticCorrectionState();
            return false;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        string predictedBoard = GetBoardPosition(_lastOptimisticPredictedFen);
        string baseBoard = GetBoardPosition(_lastOptimisticBaseFen);
        string observedBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(currentBoard) ||
            string.IsNullOrWhiteSpace(predictedBoard) ||
            string.IsNullOrWhiteSpace(baseBoard) ||
            string.IsNullOrWhiteSpace(observedBoard) ||
            !string.Equals(currentBoard, predictedBoard, StringComparison.Ordinal) ||
            string.Equals(observedBoard, predictedBoard, StringComparison.Ordinal) ||
            string.Equals(observedBoard, baseBoard, StringComparison.Ordinal))
        {
            return false;
        }

        int changedFromBase = CountChangedBoardSquares(baseBoard, observedBoard);
        if (changedFromBase <= 0 || changedFromBase > OptimisticCorrectionMaxChangedSquares)
            return false;

        if (TryDetermineExternalTurnTransitionByLegalPath(_currentFEN, observedFen, maxPlies: 1) != null)
            return false;

        LegalTurnTransition? baselineTransition = TryDetermineExternalTurnTransitionByLegalPath(_lastOptimisticBaseFen, observedFen, maxPlies: 1);
        if (baselineTransition is not { PlyCount: 1 })
            return false;

        string previousPredicted = _lastOptimisticPredictedFen;
        string previousBase = _lastOptimisticBaseFen;
        string previousMove = _lastOptimisticMoveText;

        LogDiag(
            "FAST-FEN",
            $"correcting optimistic move {previousMove} after remote FEN disagreed " +
            $"age={optimisticAgeMs:F0}ms changedFromBase={changedFromBase} " +
            $"predicted={GetBoardPosition(previousPredicted)} observed={observedBoard}");

        // Roll back the current position so ApplyConfirmedFen evaluates the
        // remote correction against the pre-optimistic board, not against the
        // mistaken optimistic prediction.
        _currentFEN = previousBase;
        ResetPendingFenCandidate();
        ResetOutOfTurnCandidate();
        ClearOptimisticCorrectionState();
        correctedFen = observedFen;
        return true;
    }

    private static void ClearOptimisticCorrectionState()
    {
        _lastOptimisticBaseFen = "";
        _lastOptimisticPredictedFen = "";
        _lastOptimisticMoveText = "";
        _lastOptimisticFenAppliedUtc = DateTime.MinValue;
    }

    private static void ClearStaleExternalArrowsOnUnconfirmedFenCandidate(string observedFen, string reason)
    {
        if (_currentFenIsAnalysisBoard ||
            !_continuousAnalysisEnabled ||
            _orientationPromptVisible ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return;
        }

        string currentBoard = GetBoardPosition(_currentFEN);
        string observedBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(currentBoard) ||
            string.IsNullOrWhiteSpace(observedBoard) ||
            string.Equals(currentBoard, observedBoard, StringComparison.Ordinal))
        {
            return;
        }

        if (!HasDisplayedOrCachedExternalArrows() || IsTrackedWindowResizeSettling())
            return;

        int changedSquares = CountChangedBoardSquares(currentBoard, observedBoard);
        DateTime now = DateTime.UtcNow;
        int riskyFenRepeatWindowMs = BlitzExternalRiskyFenRepeatWindowMs;
        bool recentMoveSignal =
            now < _recentMouseInteractionUntilUtc ||
            now < _lastExternalRawBoardChangeUtc.AddMilliseconds(riskyFenRepeatWindowMs + 250);
        bool legalTransitionCandidate = CanFastConfirmLegalExternalFen(observedFen, out _);
        // changedSquares above maxRiskyClearChangedSquares used to KEEP the
        // arrows (treated as pure noise), but premove-speed play routinely
        // jumps 2 plies at once (5-8 changed squares, not a legal successor)
        // and the old arrows then sat over the advanced board for the whole
        // re-anchor (~10s measured). With a recent move signal, any changed
        // board is now treated as a transition; this clear holds the arrows
        // through the swap, so a false positive costs one hide after the
        // grace, not an instant blink.
        bool plausibleTransientMoveCandidate =
            legalTransitionCandidate ||
            (recentMoveSignal && changedSquares > 0);

        if (!plausibleTransientMoveCandidate)
        {
            LogDiag("ARROWS", $"kept arrows for unconfirmed non-legal external FEN ({reason}, changedSquares={changedSquares}) raw={observedBoard}");
            return;
        }

        if (now < _lastUnconfirmedFenArrowClearUtc.AddMilliseconds(UnconfirmedFenArrowClearCooldownMs))
            return;

        _lastUnconfirmedFenArrowClearUtc = now;
        // This fires routinely DURING the opponent's piece animation (the
        // mid-move frame is not a legal successor), ~50ms before the real
        // FEN confirms - measured in arrow-timeline traces as the visible
        // "blink then vanish" after every move. A confirm and swap are
        // imminent, so hold here; genuine context switches (game change,
        // board switch) are handled by the large-jump clear, which does
        // hide immediately.
        ClearDisplayedArrowsForPositionChange();
        LogDiag("ARROWS", $"cleared arrows for unconfirmed external FEN ({reason}, changedSquares={changedSquares}) raw={observedBoard}");
    }

    private static bool ShouldRequireStableExternalFenConfirmation(string observedFen, out string reason)
    {
        reason = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        string oldBoard = GetBoardPosition(_currentFEN);
        string newBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            string.Equals(oldBoard, newBoard, StringComparison.Ordinal))
        {
            return false;
        }

        if (CanFastConfirmLegalExternalFen(observedFen, out string fastConfirmReason))
        {
            return false;
        }

        int changedSquares = CountChangedBoardSquares(oldBoard, newBoard);
        reason = $"{fastConfirmReason}, changedSquares={changedSquares}";
        return true;
    }

    private static bool ShouldRejectUnrecoverableNonLegalExternalFenJump(
        string observedFen,
        string stableExternalReason,
        out string reason)
    {
        reason = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        if (IsInitialBoardPosition(observedFen) ||
            IsLikelyFreshOpeningPosition(observedFen) ||
            HasFreshGameResetCandidate())
        {
            return false;
        }

        string oldBoard = GetBoardPosition(_currentFEN);
        string newBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            string.Equals(oldBoard, newBoard, StringComparison.Ordinal))
        {
            return false;
        }

        int changedSquares = CountChangedBoardSquares(oldBoard, newBoard);
        if (changedSquares <= ExternalNonLegalFenMaxRecoverableChangedSquares)
        {
            return false;
        }

        // Dead-zone rescue: this jump is too big to recover and too small to
        // count as a board switch, so rejecting it unconditionally would
        // stall detection for as long as the board stays here. Track repeated
        // observations of the SAME board; sustained presence means it is the
        // real position, not noise, so hand it to the candidate machinery
        // (which still requires its own stable confirmation to re-anchor).
        DateTime nowUtc = DateTime.UtcNow;
        if (!string.Equals(_rejectedJumpBoard, newBoard, StringComparison.Ordinal) ||
            nowUtc > _rejectedJumpLastSeenUtc.AddMilliseconds(StallReanchorObservationMaxGapMs))
        {
            _rejectedJumpBoard = newBoard;
            _rejectedJumpCount = 1;
            _rejectedJumpFirstSeenUtc = nowUtc;
        }
        else
        {
            _rejectedJumpCount++;
        }
        _rejectedJumpLastSeenUtc = nowUtc;

        if (_rejectedJumpCount >= StallReanchorMinObservations &&
            (nowUtc - _rejectedJumpFirstSeenUtc).TotalMilliseconds >= StallReanchorMinSpanMs)
        {
            ArrowTimeline.Log(
                "VISION_STALL_REANCHOR",
                fen: observedFen,
                count: _rejectedJumpCount,
                ms: (nowUtc - _rejectedJumpFirstSeenUtc).TotalMilliseconds,
                extra: $"changedSquares={changedSquares}");
            LogDiag("FEN", $"accepting persistent non-legal jump after {_rejectedJumpCount} observations over {(nowUtc - _rejectedJumpFirstSeenUtc).TotalSeconds:F1}s (changedSquares={changedSquares})");
            _rejectedJumpBoard = "";
            _rejectedJumpCount = 0;
            return false;
        }

        if (nowUtc >= _lastVisionRejectLogUtc.AddMilliseconds(1000))
        {
            _lastVisionRejectLogUtc = nowUtc;
            ArrowTimeline.Log("VISION_REJECT", reason: $"unrecoverable jump changedSquares={changedSquares}", count: _rejectedJumpCount, extra: newBoard);
        }

        reason =
            $"non-legal external jump changedSquares={changedSquares} > {ExternalNonLegalFenMaxRecoverableChangedSquares}; " +
            stableExternalReason;
        return true;
    }

    private static bool ShouldIgnoreSingleSquareColorFlipObservation(string observedFen, out string reason)
    {
        reason = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        string oldBoard = GetBoardPosition(_currentFEN);
        string newBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            string.Equals(oldBoard, newBoard, StringComparison.Ordinal) ||
            CountChangedBoardSquares(oldBoard, newBoard) != 1)
        {
            return false;
        }

        var oldPieces = ParseBoard(oldBoard);
        var newPieces = ParseBoard(newBoard);
        foreach (string square in oldPieces.Keys.Concat(newPieces.Keys).Distinct(StringComparer.Ordinal))
        {
            oldPieces.TryGetValue(square, out char oldPiece);
            newPieces.TryGetValue(square, out char newPiece);
            if (oldPiece == newPiece)
                continue;

            if (oldPiece != '\0' &&
                newPiece != '\0' &&
                char.ToUpperInvariant(oldPiece) == char.ToUpperInvariant(newPiece) &&
                char.IsUpper(oldPiece) != char.IsUpper(newPiece))
            {
                reason = $"single-square color flip at {square} ({oldPiece}->{newPiece})";
                ResetPendingFenCandidate();
                return true;
            }

            return false;
        }

        return false;
    }

    private static int GetRequiredFenConfirmationCount()
    {
        if (_currentFenIsAnalysisBoard || string.IsNullOrWhiteSpace(_currentFEN))
            return _fenConfirmationThreshold;

        DateTime now = DateTime.UtcNow;
        int riskyFenRepeatWindowMs = BlitzExternalRiskyFenRepeatWindowMs;
        bool recentRawBoardChange = now < _lastExternalRawBoardChangeUtc.AddMilliseconds(riskyFenRepeatWindowMs);
        bool recentLocalMouseInteraction = now < _recentMouseInteractionUntilUtc;

        return recentRawBoardChange || recentLocalMouseInteraction
            ? Math.Max(_fenConfirmationThreshold, 2)
            : _fenConfirmationThreshold;
    }

    private static bool ShouldIgnoreForegroundMismatchFenObservation(string observedFen, out string reason)
    {
        reason = "";

        if (DateTime.UtcNow >= _foregroundMismatchFenGuardUntilUtc ||
            _currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            return false;
        }

        string oldBoard = GetBoardPosition(_currentFEN);
        string newBoard = GetBoardPosition(observedFen);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            string.Equals(oldBoard, newBoard, StringComparison.Ordinal))
        {
            return false;
        }

        char expectedSide = _inferredSideToMove == 'b' ? 'b' : 'w';
        LegalTurnTransition? transition = TryDetermineExternalTurnTransitionByLegalPath(_currentFEN, observedFen, maxPlies: 1);
        if (transition != null &&
            transition.PlyCount == 1 &&
            transition.LastMover == expectedSide)
        {
            return false;
        }

        reason = transition == null
            ? $"no expected legal one-ply transition while foreground is not the tracked board, expected={expectedSide}"
            : $"unexpected mover={transition.LastMover} while foreground is not the tracked board, expected={expectedSide}";
        ResetPendingFenCandidate();
        return true;
    }

    private static bool CanFastConfirmLegalExternalFen(string observedFen, out string reason)
    {
        reason = "";

        if (_currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(_currentFEN) ||
            string.IsNullOrWhiteSpace(observedFen) ||
            IsActiveAnalysisBoardFen(_currentFEN) ||
            IsActiveAnalysisBoardFen(observedFen))
        {
            reason = "not an external live-board transition";
            return false;
        }

        if (IsExternalBoardGeometryUnstable())
        {
            reason = "board geometry unstable";
            return false;
        }

        if (IsTrackedWindowResizeSettling())
        {
            reason = "tracked window resize settling";
            return false;
        }

        string oldBoard = GetBoardPosition(_currentFEN);
        string newBoard = GetBoardPosition(observedFen);
        bool fastRecovery = ShouldUseFastDetectionRecovery();
        int maxChangedSquares = fastRecovery
            ? Math.Max(BlitzFastConfirmMaxChangedSquares, RemoteFastConfirmMaxChangedSquares)
            : 4;
        int changedSquares = CountChangedBoardSquares(oldBoard, newBoard);
        if (changedSquares <= 0 || changedSquares > maxChangedSquares)
        {
            reason = $"changedSquares={changedSquares}";
            return false;
        }

        char expectedSide = _inferredSideToMove == 'b' ? 'b' : 'w';
        int maxPlies = fastRecovery ? RemoteFastConfirmMaxPlies : 1;
        LegalTurnTransition? transition = TryDetermineExternalTurnTransitionByLegalPath(_currentFEN, observedFen, maxPlies);
        if (transition == null)
        {
            reason = $"no legal <= {maxPlies}-ply path for expected {expectedSide}";
            return false;
        }

        if (transition.PlyCount == 1)
        {
            if (transition.LastMover != expectedSide)
            {
                reason = $"unexpected mover={transition.LastMover}, expected={expectedSide}";
                return false;
            }

            reason = $"mover={expectedSide}, changedSquares={changedSquares}";
            return true;
        }

        if (!fastRecovery || transition.PlyCount > maxPlies)
        {
            reason = $"plyCount={transition.PlyCount}";
            return false;
        }

        // An even-ply path returns the move to the side we expected; an odd
        // path flips it. Either is acceptable as long as the path's outcome
        // is self-consistent (ambiguous paths were already rejected).
        char expectedSideAfter = transition.PlyCount % 2 == 0
            ? expectedSide
            : (expectedSide == 'w' ? 'b' : 'w');
        if (transition.SideToMoveAfter != expectedSideAfter)
        {
            reason = $"{transition.PlyCount}-ply recovery would leave {transition.SideToMoveAfter} to move, expected={expectedSideAfter}";
            return false;
        }

        reason = $"fast recovery {transition.PlyCount} plies, last={transition.LastMover}, next={transition.SideToMoveAfter}, changedSquares={changedSquares}";
        return true;
    }

    private static string MergeDetectedFenWithHistory(string previousFen, string detectedFen)
    {
        if (string.IsNullOrWhiteSpace(detectedFen))
            return detectedFen;

        var detectedParts = detectedFen.Split(' ');
        if (detectedParts.Length < 3)
            return detectedFen;

        if (string.IsNullOrWhiteSpace(previousFen))
        {
            detectedParts[2] = ValidateCastlingRights(detectedParts[0], detectedParts[2]);
            return string.Join(" ", detectedParts);
        }

        if (IsFreshGameResetObservation(detectedFen) || IsAcceptedExternalBoardSwitchFen(detectedFen))
        {
            detectedParts[2] = ValidateCastlingRights(detectedParts[0], detectedParts[2]);
            return string.Join(" ", detectedParts);
        }

        var previousParts = previousFen.Split(' ');
        string previousRights = previousParts.Length >= 3 ? previousParts[2] : "-";
        string detectedRights = ValidateCastlingRights(detectedParts[0], detectedParts[2]);

        detectedParts[2] = IntersectCastlingRights(previousRights, detectedRights);
        return string.Join(" ", detectedParts);
    }

    private static bool IsReturnToUserMovePosition(string boardPosition)
    {
        if (string.IsNullOrEmpty(_lastUserMoveFEN) || string.IsNullOrEmpty(boardPosition))
            return false;

        return GetBoardPosition(_lastUserMoveFEN) == boardPosition;
    }

    private static ConfirmedPositionState? FindRecentConfirmedState(string boardPosition)
    {
        if (string.IsNullOrEmpty(boardPosition))
            return null;

        for (int i = _recentConfirmedStates.Count - 1; i >= 0; i--)
        {
            ConfirmedPositionState state = _recentConfirmedStates[i];
            if (state.BoardPosition == boardPosition && state.UserColor == _userColor)
            {
                return state;
            }
        }

        return null;
    }

    private static bool TryRestoreRecentState(ConfirmedPositionState? state, string confirmedFen)
    {
        if (state == null)
            return false;

        _waitingForOpponentMove = state.WaitingForOpponent;
        _inferredSideToMove = state.SideToMove;
        _lastArrowSourceFEN = state.ArrowSourceFen;
        _lastUserMoveFEN = state.WaitingForOpponent ? confirmedFen : "";

        if (_waitingForOpponentMove)
        {
            ClearDisplayedArrowsForPositionChange();
        }

        return true;
    }

    private static bool ShouldRejectOutOfTurnExternalObservation(
        string confirmedFen,
        char? strictDetectedMover,
        ConfirmedPositionState? recentState,
        bool legalMultiPlyRecovery,
        bool highlightSupportsObservedMove)
    {
        if (!_analysisBothEnabled ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(confirmedFen) ||
            !strictDetectedMover.HasValue)
        {
            return false;
        }

        if (strictDetectedMover.Value == _inferredSideToMove)
            return false;

        if (highlightSupportsObservedMove)
            return false;

        if (legalMultiPlyRecovery)
            return false;

        if (recentState != null || IsInitialBoardPosition(confirmedFen) || IsLikelyFreshOpeningPosition(confirmedFen))
            return false;

        DateTime now = DateTime.UtcNow;
        bool animationWindow =
            now < _lastExternalRawBoardChangeUtc.AddMilliseconds(ExternalRiskyFenRepeatWindowMs + 250) ||
            now < _recentMouseInteractionUntilUtc;
        if (!animationWindow)
            return false;

        string boardPosition = GetBoardPosition(confirmedFen);
        if (!string.Equals(_outOfTurnFenCandidate, boardPosition, StringComparison.Ordinal))
        {
            _outOfTurnFenCandidate = boardPosition;
            _outOfTurnFenCandidateSinceUtc = now;
            _outOfTurnFenCandidateCount = 1;
            return true;
        }

        _outOfTurnFenCandidateCount++;
        double ageMs = _outOfTurnFenCandidateSinceUtc == DateTime.MinValue
            ? 0
            : (now - _outOfTurnFenCandidateSinceUtc).TotalMilliseconds;

        // If the "unexpected mover" board persists for several frames, treat
        // it as a genuine recovery signal. Brief streamer/premove/drag frames
        // are usually gone long before this point.
        return _outOfTurnFenCandidateCount < OutOfTurnAnimationConfirmations ||
               ageMs < OutOfTurnAnimationHoldMs;
    }

    private static void ResetOutOfTurnCandidate()
    {
        _outOfTurnFenCandidate = "";
        _outOfTurnFenCandidateSinceUtc = DateTime.MinValue;
        _outOfTurnFenCandidateCount = 0;
    }

    private static void RememberConfirmedState(string fen)
    {
        if (string.IsNullOrEmpty(fen))
            return;

        string boardPosition = GetBoardPosition(fen);
        if (string.IsNullOrEmpty(boardPosition))
            return;

        _recentConfirmedStates.RemoveAll(state =>
            state.BoardPosition == boardPosition &&
            state.UserColor == _userColor);

        _recentConfirmedStates.Add(new ConfirmedPositionState
        {
            BoardPosition = boardPosition,
            UserColor = _userColor,
            SideToMove = _inferredSideToMove,
            WaitingForOpponent = _waitingForOpponentMove,
            ArrowSourceFen = _lastArrowSourceFEN
        });

        while (_recentConfirmedStates.Count > _recentConfirmedStateLimit)
        {
            _recentConfirmedStates.RemoveAt(0);
        }
    }

    private static void ResetConfirmedStateTimeline()
    {
        _recentConfirmedStates.Clear();
    }

    private static bool ShouldWaitForOpponentAtAnalysisStart()
    {
        if (_analysisBothEnabled)
            return false;

        if (IsInitialBoardPosition(_currentFEN))
            return _userColor == 'b';

        if (_userColor == 'b' && IsLikelyFreshOpeningPosition(_currentFEN))
            return false;

        if (string.IsNullOrEmpty(_currentFEN))
            return false;

        string boardPosition = GetBoardPosition(_currentFEN);
        if (string.IsNullOrEmpty(boardPosition))
            return false;

        if (!string.IsNullOrEmpty(_lastUserMoveFEN) && boardPosition == GetBoardPosition(_lastUserMoveFEN))
            return true;

        return false;
    }

    private static bool IsLikelyFreshOpeningPosition(string fen)
    {
        if (string.IsNullOrEmpty(fen) || IsInitialBoardPosition(fen))
            return false;

        return TryInferFreshOpeningSideToMove(fen, out _, out char lastMover) &&
               lastMover == 'w';
    }

    private static bool TryInferFreshOpeningSideToMove(string fen, out char sideToMove, out char lastMover)
    {
        sideToMove = 'w';
        lastMover = '\0';

        if (string.IsNullOrWhiteSpace(fen) || IsInitialBoardPosition(fen) || IsActiveAnalysisBoardFen(fen))
            return false;

        LegalTurnTransition? openingMove = TryFindLegalTurnPath(
            $"{InitialBoardPosition} w KQkq - 0 1",
            GetBoardPosition(fen),
            'w',
            maxPlies: 1);
        if (openingMove is not { PlyCount: 1 })
            return false;

        lastMover = openingMove.LastMover;
        sideToMove = openingMove.SideToMoveAfter;
        return true;
    }

    private static bool ShouldWaitForOpponentWhenEnablingAnalysis(bool isBlackPerspective)
    {
        if (_analysisBothEnabled)
            return false;

        if (string.IsNullOrEmpty(_currentFEN))
            return false;

        char requestedColor = GetAnalysisSideForFen(_currentFEN, _analysisIsBlackPerspective);

        if (IsActiveAnalysisBoardFen(_currentFEN))
        {
            char? sideToMove = GetSideToMove(_currentFEN);
            if (sideToMove.HasValue)
            {
                return sideToMove.Value != requestedColor;
            }
        }

        if (requestedColor == 'b' && IsLikelyFreshOpeningPosition(_currentFEN))
            return false;

        // Treat a manual toggle as a resync request: show moves immediately for the
        // chosen side unless this is the starting position as Black, where White must
        // still play first.
        return requestedColor == 'b' && IsInitialBoardPosition(_currentFEN);
    }

    private static bool IsInitialBoardPosition(string fen)
    {
        if (string.IsNullOrEmpty(fen))
            return false;

        string boardPosition = GetBoardPosition(fen);
        return boardPosition == InitialBoardPosition || boardPosition == InitialBoardPositionRotated;
    }

    private static bool ShouldSuppressArrowsForPosition(string fen, bool isBlackPerspective)
    {
        if (!isBlackPerspective)
            return false;

        // The generic waiting logic already handles "black on a fresh board".
        // Suppressing again here for external boards can leave the UI stuck
        // after a completed game resets into a new starting position.
        return IsActiveAnalysisBoardFen(fen) && IsInitialBoardPosition(fen);
    }

    private static bool CanDisplayArrowsForCurrentState()
    {
        if (HasUnresolvedOrientationPromptForCurrentPosition())
            return false;

        if (!TryResolveOrientationDecision(_currentFEN, IsActiveAnalysisBoardFen(_currentFEN), GetOrientationPromptReferenceColor(_currentFEN), out _))
            return false;

        char requestedColor = GetAnalysisSideForFen(_currentFEN, _analysisIsBlackPerspective);
        char? sideToMove = GetSideToMove(_currentFEN);
        bool bypassWaiting = ShouldBypassWaitingForExternalBothMode(_currentFEN);
        bool isAnalysisBoardPosition = IsActiveAnalysisBoardFen(_currentFEN);

        if (BuildLimits.IsFreeEdition)
        {
            if (isAnalysisBoardPosition)
            {
                if (_analysisBoardController!.IsFreeLiveLimitReached())
                    return false;
            }
            else if (IsFreeExternalAnalysisLimitReached())
            {
                return false;
            }
        }

        // External detector FENs do not reliably carry side-to-move; they are
        // screen observations, not game-state authority. Trust that field only
        // for our internal analysis board, which owns the full move state.
        bool shouldTrustFenSideToMove = isAnalysisBoardPosition;

        if (shouldTrustFenSideToMove &&
            sideToMove.HasValue &&
            sideToMove.Value != requestedColor)
        {
            return false;
        }

        if (!isAnalysisBoardPosition)
        {
            if (IsExternalBoardOutputSuspended())
                return false;

            if (_trackedHwnd == IntPtr.Zero || !WindowTracker.IsTrackable(_trackedHwnd))
                return false;

            if (IsTerminalExternalPosition(_currentFEN, out _))
                return false;

            if (_lastTrackedBox.HasValue &&
                (_boardObscuredLastFrame || WindowTracker.IsBoardObscured(_trackedHwnd, _lastTrackedBox.Value)))
            {
                return false;
            }
        }

        return _continuousAnalysisEnabled &&
               (bypassWaiting || !_waitingForOpponentMove) &&
               !ShouldSuppressArrowsForPosition(_currentFEN, GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective));
    }

    private static bool TryHandleTerminalExternalPosition(string fen)
    {
        if (!TryGetTerminalExternalPositionReason(fen, out string reason, timeoutMs: 90))
            return false;

        if (!string.Equals(_currentFEN, fen, StringComparison.Ordinal))
            return false;

        CancelPendingAnalysis("terminal external position");
        ClearDisplayedArrowsForPositionChange(allowHoldForPendingSwap: false);
        _latencyT0Utc = DateTime.MinValue;
        _latencyT0ChangedSquares = 0;
        RefreshDebugView($"Terminal position: {reason}");
        LogDiag("TURN", $"terminal external position ({reason}); cleared arrows and skipped analysis");
        return true;
    }

    private static bool TryGetTerminalExternalPositionReason(string fen, out string reason, int timeoutMs)
    {
        reason = "";

        if (timeoutMs <= 0)
            return IsTerminalExternalPosition(fen, out reason);

        try
        {
            var task = Task.Run(() =>
            {
                bool terminal = IsTerminalExternalPosition(fen, out string terminalReason);
                return (terminal, terminalReason);
            });

            if (!task.Wait(timeoutMs))
            {
                LogDiag("TURN", $"terminal external probe timed out after {timeoutMs}ms board={GetBoardPosition(fen)}");
                return false;
            }

            if (!task.Result.terminal)
                return false;

            reason = task.Result.terminalReason;
            return true;
        }
        catch (Exception ex)
        {
            LogDiag("TURN", $"terminal external probe failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsTerminalExternalPosition(string fen, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(fen) || IsActiveAnalysisBoardFen(fen) || _currentFenIsAnalysisBoard)
            return false;

        try
        {
            string legalFen = ApplyInferredExternalTurnToFen(fen);
            ChessBoard board = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
            if (board.Moves(false, true).Any())
                return false;

            char side = GetSideToMove(legalFen) ?? _inferredSideToMove;
            string sideName = side == 'b' ? "black" : "white";
            string status = board.EndGame?.WonSide == PieceColor.White || board.EndGame?.WonSide == PieceColor.Black
                ? "checkmate"
                : "no legal moves";
            reason = $"{status}, {sideName} to move";
            return true;
        }
        catch (Exception ex)
        {
            LogDiag("TURN", $"terminal external probe skipped: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool IsAnalysisRequestStillCurrent(string capturedFEN, bool isBlackPerspective, int analysisSessionVersion)
    {
        lock (_analysisLock)
        {
            bool expectedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
            return _continuousAnalysisEnabled &&
                   GetAnalysisSessionVersion() == analysisSessionVersion &&
                   expectedPerspective == isBlackPerspective &&
                   _currentFEN == capturedFEN;
        }
    }

    private static void ApplyWaitingStateForCurrentPosition()
    {
        if (!_continuousAnalysisEnabled || string.IsNullOrEmpty(_currentFEN))
            return;

        if (_analysisBothEnabled && !IsActiveAnalysisBoardFen(_currentFEN))
        {
            _waitingForOpponentMove = false;
            return;
        }

        if (IsActiveAnalysisBoardFen(_currentFEN))
        {
            char? sideToMove = GetSideToMove(_currentFEN);
            if (sideToMove.HasValue)
            {
                bool shouldWaitForOpponent = sideToMove.Value != GetAnalysisSideForFen(_currentFEN, _analysisIsBlackPerspective);
                _waitingForOpponentMove = shouldWaitForOpponent;

                if (shouldWaitForOpponent)
                {
                    _currentMoveArrows = null;
                    _lastAnalysisVariations = null;
                    _lastArrowSourceFEN = "";
                    RefreshDebugView("Waiting for opponent (analysis board FEN)");
                    ClearActiveArrows();
                }

                return;
            }
        }

        bool shouldWait = ShouldWaitForOpponentAtAnalysisStart();
        if (!shouldWait)
        {
            _waitingForOpponentMove = false;
            return;
        }

        _waitingForOpponentMove = true;
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        RefreshDebugView("Waiting for opponent");
        ClearActiveArrows();
    }

    private static char? DetermineMoveOwner(string oldFEN, string newFEN, char? legalTransitionMover = null)
    {
        bool recentMouse = DateTime.UtcNow < _recentMouseInteractionUntilUtc;

        if (legalTransitionMover.HasValue)
        {
            LogDiag("TURN", $"legal transition mover={legalTransitionMover.Value}");
            ArrowTimeline.Log("TURN_DECISION", reason: "legal", extra: $"mover={legalTransitionMover.Value} mouse={recentMouse}");
            return legalTransitionMover;
        }

        char? detectedMover = DetectWhoMoved(oldFEN, newFEN);
        if (detectedMover.HasValue)
        {
            ArrowTimeline.Log("TURN_DECISION", reason: "detect", extra: $"mover={detectedMover.Value} mouse={recentMouse}");
            return detectedMover;
        }

        char? inferredMover = InferLikelyMover(oldFEN);
        if (inferredMover.HasValue)
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Inferred move by {(inferredMover.Value == 'w' ? "WHITE" : "BLACK")} from state");
        }
        ArrowTimeline.Log("TURN_DECISION", reason: inferredMover.HasValue ? "infer" : "none", extra: $"mover={inferredMover?.ToString() ?? "-"} mouse={recentMouse}");

        return inferredMover;
    }

    private static char? TryDetermineExternalMoverByLegalTransition(string oldFEN, string newFEN)
    {
        return TryDetermineExternalTurnTransitionByLegalPath(oldFEN, newFEN, maxPlies: 1)?.LastMover;
    }

    private static LegalTurnTransition? TryDetermineExternalTurnTransitionByLegalPath(string oldFEN, string newFEN, int maxPlies = 2)
    {
        if (string.IsNullOrWhiteSpace(oldFEN) ||
            string.IsNullOrWhiteSpace(newFEN) ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(oldFEN) ||
            IsActiveAnalysisBoardFen(newFEN))
        {
            return null;
        }

        string oldBoard = GetBoardPosition(oldFEN);
        string newBoard = GetBoardPosition(newFEN);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            string.Equals(oldBoard, newBoard, StringComparison.Ordinal))
        {
            return null;
        }

        char expectedSide = _inferredSideToMove == 'b' ? 'b' : 'w';
        char oppositeSide = expectedSide == 'w' ? 'b' : 'w';
        maxPlies = Math.Clamp(maxPlies, 1, 4);

        int changedSquares = CountChangedBoardSquares(oldBoard, newBoard);
        int maxUsefulChangedSquares = maxPlies switch
        {
            <= 1 => 4,
            2 => 8,
            _ => RemoteFastConfirmMaxChangedSquares,
        };
        if (changedSquares <= 0 || changedSquares > maxUsefulChangedSquares)
        {
            LogDiag("TURN", $"legal path skipped: changedSquares={changedSquares}, max={maxUsefulChangedSquares}");
            return null;
        }

        // The same observation is evaluated by several guards per frame and
        // across consecutive frames; the search result is deterministic for a
        // given (from, to, side, depth), so cache the last answer.
        string cacheKey = $"{oldBoard}|{newBoard}|{expectedSide}|{maxPlies}";
        if (string.Equals(_legalPathCacheKey, cacheKey, StringComparison.Ordinal))
        {
            return _legalPathCacheResult;
        }

        if (TryDetermineVisualCastlingTransition(oldFEN, newFEN, expectedSide, out LegalTurnTransition? expectedCastle, out string expectedCastleReason))
        {
            LogDiag("TURN", $"visual castling transition accepted ({expectedCastleReason})");
            return CacheLegalPathResult(cacheKey, expectedCastle);
        }

        if (TryDetermineVisualCastlingTransition(oldFEN, newFEN, oppositeSide, out LegalTurnTransition? recoveryCastle, out string recoveryCastleReason))
        {
            LogDiag("TURN", $"visual castling recovery transition accepted ({recoveryCastleReason})");
            return CacheLegalPathResult(cacheKey, recoveryCastle);
        }

        LegalTurnTransition? expectedPath = TryFindLegalTurnPath(oldFEN, newBoard, expectedSide, maxPlies);
        if (expectedPath != null)
        {
            return CacheLegalPathResult(cacheKey, expectedPath);
        }

        LegalTurnTransition? recoveryPath = TryFindLegalTurnPath(oldFEN, newBoard, oppositeSide, maxPlies);
        if (recoveryPath != null)
        {
            return CacheLegalPathResult(cacheKey, recoveryPath);
        }

        return CacheLegalPathResult(cacheKey, null);
    }

    private static string _legalPathCacheKey = "";
    private static LegalTurnTransition? _legalPathCacheResult;

    private static LegalTurnTransition? CacheLegalPathResult(string cacheKey, LegalTurnTransition? result)
    {
        _legalPathCacheKey = cacheKey;
        _legalPathCacheResult = result;
        return result;
    }

    private static bool TryDetermineVisualCastlingTransition(
        string oldFEN,
        string newFEN,
        char sideToMove,
        out LegalTurnTransition? transition,
        out string reason)
    {
        transition = null;
        reason = "";

        string oldBoard = GetBoardPosition(oldFEN);
        string newBoard = GetBoardPosition(newFEN);
        if (string.IsNullOrWhiteSpace(oldBoard) ||
            string.IsNullOrWhiteSpace(newBoard) ||
            CountChangedBoardSquares(oldBoard, newBoard) != 4)
        {
            return false;
        }

        char king = sideToMove == 'w' ? 'K' : 'k';
        char rook = sideToMove == 'w' ? 'R' : 'r';
        string rank = sideToMove == 'w' ? "1" : "8";

        if (IsVisualCastlingPattern(oldBoard, newBoard, king, rook, "e" + rank, "h" + rank, "g" + rank, "f" + rank))
        {
            transition = new LegalTurnTransition
            {
                LastMover = sideToMove,
                SideToMoveAfter = sideToMove == 'w' ? 'b' : 'w',
                PlyCount = 1
            };
            reason = $"{sideToMove} kingside castle by board pattern";
            return true;
        }

        if (IsVisualCastlingPattern(oldBoard, newBoard, king, rook, "e" + rank, "a" + rank, "c" + rank, "d" + rank))
        {
            transition = new LegalTurnTransition
            {
                LastMover = sideToMove,
                SideToMoveAfter = sideToMove == 'w' ? 'b' : 'w',
                PlyCount = 1
            };
            reason = $"{sideToMove} queenside castle by board pattern";
            return true;
        }

        return false;
    }

    private static bool IsVisualCastlingPattern(
        string oldBoard,
        string newBoard,
        char king,
        char rook,
        string kingFrom,
        string rookFrom,
        string kingTo,
        string rookTo)
    {
        if (GetPieceAtSquare(oldBoard, kingFrom) != king ||
            GetPieceAtSquare(oldBoard, rookFrom) != rook ||
            GetPieceAtSquare(oldBoard, kingTo) != '.' ||
            GetPieceAtSquare(oldBoard, rookTo) != '.')
        {
            return false;
        }

        if (GetPieceAtSquare(newBoard, kingFrom) != '.' ||
            GetPieceAtSquare(newBoard, rookFrom) != '.' ||
            GetPieceAtSquare(newBoard, kingTo) != king ||
            GetPieceAtSquare(newBoard, rookTo) != rook)
        {
            return false;
        }

        var expectedChangedSquares = new HashSet<string>(StringComparer.Ordinal)
        {
            kingFrom,
            rookFrom,
            kingTo,
            rookTo
        };
        var oldPieces = ParseBoard(oldBoard);
        var newPieces = ParseBoard(newBoard);
        foreach (string square in oldPieces.Keys.Concat(newPieces.Keys).Distinct(StringComparer.Ordinal))
        {
            oldPieces.TryGetValue(square, out char oldPiece);
            newPieces.TryGetValue(square, out char newPiece);
            oldPiece = oldPiece == '\0' ? '.' : oldPiece;
            newPiece = newPiece == '\0' ? '.' : newPiece;
            if (oldPiece == newPiece)
                continue;

            if (!expectedChangedSquares.Contains(square))
                return false;
        }

        return true;
    }

    private static int CountChangedBoardSquares(string oldBoard, string newBoard)
    {
        string oldExpanded = ExpandBoardPosition(oldBoard);
        string newExpanded = ExpandBoardPosition(newBoard);
        if (oldExpanded.Length != 64 || newExpanded.Length != 64)
            return 64;

        int changed = 0;
        for (int i = 0; i < 64; i++)
        {
            if (oldExpanded[i] != newExpanded[i])
                changed++;
        }
        return changed;
    }

    private static LegalTurnTransition? TryFindLegalTurnPath(
        string startFen,
        string targetBoardPosition,
        char startSide,
        int maxPlies)
    {
        // Progressive deepening: the shortest legal path is the most likely
        // real history, so search 1 ply, then 2, and so on. The pruning in
        // the recursion keeps deeper levels cheap.
        var matches = new List<LegalTurnTransition>();
        for (int plies = 1; plies <= Math.Max(1, maxPlies); plies++)
        {
            matches.Clear();
            TryFindLegalTurnPathRecursive(
                FenWithSideToMove(startFen, startSide),
                targetBoardPosition,
                startSide,
                plyDepth: 1,
                plies,
                matches);

            LegalTurnTransition? match = SelectLegalTurnTransitionMatch(matches, startSide);
            if (match != null)
                return match;
        }

        return null;
    }

    private static LegalTurnTransition? SelectLegalTurnTransitionMatch(List<LegalTurnTransition> matches, char startSide)
    {
        if (matches.Count == 0)
            return null;

        int shortest = matches.Min(m => m.PlyCount);
        var shortestMatches = matches
            .Where(m => m.PlyCount == shortest)
            .ToList();

        if (shortestMatches.Select(m => m.SideToMoveAfter).Distinct().Count() > 1)
        {
            LogDiag("TURN", $"legal path ambiguous after {shortest} ply/plies from {startSide}");
            return null;
        }

        LegalTurnTransition match = shortestMatches[0];
        if (match.PlyCount > 1)
        {
            LogDiag(
                "TURN",
                $"legal multi-ply recovery: {match.PlyCount} plies from {startSide}, last={match.LastMover}, next={match.SideToMoveAfter}");
        }

        return match;
    }

    private static void TryFindLegalTurnPathRecursive(
        string fen,
        string targetBoardPosition,
        char sideToMove,
        int plyDepth,
        int maxPlies,
        List<LegalTurnTransition> matches)
    {
        if (plyDepth > maxPlies)
            return;

        try
        {
            string legalFen = FenWithSideToMove(fen, sideToMove);
            var board = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
            // Prune to moves that touch a square still differing from the
            // target: any move on a shortest path must change at least one
            // such square (its origin empties or its destination gains a
            // piece that the target disagrees with right now). This is what
            // makes 3-4 ply searches affordable - typically 2-6 candidate
            // moves per node instead of ~35.
            string nodeExpanded = ExpandBoardPosition(GetBoardPosition(legalFen));
            string targetExpanded = ExpandBoardPosition(targetBoardPosition);
            foreach (var move in board.Moves(false, true).ToList())
            {
                if (!MoveTouchesDifferingSquare(ToUciMove(move), nodeExpanded, targetExpanded))
                    continue;

                var trial = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
                Move? matchingMove = trial.Moves(false, true)
                    .FirstOrDefault(m => string.Equals(ToUciMove(m), ToUciMove(move), StringComparison.OrdinalIgnoreCase));

                if (matchingMove == null)
                    continue;

                trial.Move(matchingMove);
                string trialFen = trial.ToFen();
                char nextSide = sideToMove == 'w' ? 'b' : 'w';
                if (string.Equals(GetBoardPosition(trialFen), targetBoardPosition, StringComparison.Ordinal))
                {
                    matches.Add(new LegalTurnTransition
                    {
                        LastMover = sideToMove,
                        SideToMoveAfter = nextSide,
                        PlyCount = plyDepth
                    });
                    continue;
                }

                if (plyDepth < maxPlies)
                {
                    TryFindLegalTurnPathRecursive(
                        FenWithSideToMove(trialFen, nextSide),
                        targetBoardPosition,
                        nextSide,
                        plyDepth + 1,
                        maxPlies,
                        matches);
                }
            }
        }
        catch (Exception ex)
        {
            LogDiag("TURN", $"legal transition path skipped side={sideToMove}, ply={plyDepth}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool MoveTouchesDifferingSquare(string uci, string nodeBoardExpanded, string targetBoardExpanded)
    {
        // Fail open: an unparseable move or board must not silently prune a
        // legitimate path.
        if (uci.Length < 4 || nodeBoardExpanded.Length != 64 || targetBoardExpanded.Length != 64)
            return true;

        int fromIndex = BoardSquareIndexFromUci(uci[0], uci[1]);
        int toIndex = BoardSquareIndexFromUci(uci[2], uci[3]);
        if (fromIndex < 0 || toIndex < 0)
            return true;

        return nodeBoardExpanded[fromIndex] != targetBoardExpanded[fromIndex] ||
            nodeBoardExpanded[toIndex] != targetBoardExpanded[toIndex];
    }

    private static int BoardSquareIndexFromUci(char file, char rank)
    {
        int f = file - 'a';
        int r = rank - '1';
        if (f is < 0 or > 7 || r is < 0 or > 7)
            return -1;
        // ExpandBoardPosition emits rank 8 first, files a..h within a rank.
        return (7 - r) * 8 + f;
    }

    private static bool CanSideLegallyReachBoardPosition(string oldFEN, string targetBoardPosition, char sideToMove)
    {
        try
        {
            string legalFen = FenWithSideToMove(oldFEN, sideToMove);
            var board = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
            var legalMoves = board.Moves(false, true).ToList();

            foreach (var move in legalMoves)
            {
                var trial = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
                Move? matchingMove = trial.Moves(false, true)
                    .FirstOrDefault(m => string.Equals(ToUciMove(m), ToUciMove(move), StringComparison.OrdinalIgnoreCase));

                if (matchingMove == null)
                    continue;

                trial.Move(matchingMove);
                if (string.Equals(GetBoardPosition(trial.ToFen()), targetBoardPosition, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogDiag("TURN", $"legal transition probe skipped side={sideToMove}: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static char? InferLikelyMover(string oldFEN)
    {
        if (DateTime.UtcNow < _recentMouseInteractionUntilUtc)
        {
            return null;
        }

        if (_waitingForOpponentMove)
        {
            return _userColor == 'w' ? 'b' : 'w';
        }

        if (_showingMoves && !string.IsNullOrEmpty(_lastArrowSourceFEN))
        {
            string oldPosition = GetBoardPosition(oldFEN);
            string arrowPosition = GetBoardPosition(_lastArrowSourceFEN);

            if (oldPosition == arrowPosition)
            {
                return _userColor;
            }
        }

        return null;
    }

    private static char? DetectWhoMoved(string oldFEN, string newFEN)
    {
        if (string.IsNullOrEmpty(oldFEN) || string.IsNullOrEmpty(newFEN))
            return null;

        var oldBoard = oldFEN.Split(' ')[0];
        var newBoard = newFEN.Split(' ')[0];

        if (oldBoard == newBoard)
            return null;

        var oldPieces = ParseBoard(oldBoard);
        var newPieces = ParseBoard(newBoard);

        int whiteSourceSquares = 0;
        int whiteDestinationSquares = 0;
        int blackSourceSquares = 0;
        int blackDestinationSquares = 0;

        foreach (var square in oldPieces.Keys.Union(newPieces.Keys))
        {
            char oldPiece = oldPieces.ContainsKey(square) ? oldPieces[square] : '.';
            char newPiece = newPieces.ContainsKey(square) ? newPieces[square] : '.';

            if (oldPiece == newPiece)
            {
                continue;
            }

            if (oldPiece != '.')
            {
                if (char.IsUpper(oldPiece))
                    whiteSourceSquares++;
                else
                    blackSourceSquares++;
            }

            if (newPiece != '.')
            {
                if (char.IsUpper(newPiece))
                    whiteDestinationSquares++;
                else
                    blackDestinationSquares++;
            }
        }

        bool whiteMoved = whiteSourceSquares > 0 && whiteDestinationSquares > 0 && blackDestinationSquares <= blackSourceSquares;
        bool blackMoved = blackSourceSquares > 0 && blackDestinationSquares > 0 && whiteDestinationSquares <= whiteSourceSquares;

        if (whiteMoved && !blackMoved)
            return 'w';

        if (blackMoved && !whiteMoved)
            return 'b';

        return null;
    }

    private static Dictionary<string, char> ParseBoard(string boardFEN)
    {
        var pieces = new Dictionary<string, char>();
        var ranks = boardFEN.Split('/');

        for (int rank = 0; rank < 8; rank++)
        {
            int file = 0;
            foreach (char c in ranks[rank])
            {
                if (char.IsDigit(c))
                {
                    file += (c - '0');
                }
                else
                {
                    string square = $"{(char)('a' + file)}{8 - rank}";
                    pieces[square] = c;
                    file++;
                }
            }
        }

        return pieces;
    }

    private static char GetPieceAtSquare(string boardFEN, string square)
    {
        if (string.IsNullOrWhiteSpace(boardFEN) || string.IsNullOrWhiteSpace(square) || square.Length != 2)
            return '.';

        var pieces = ParseBoard(boardFEN);
        return pieces.TryGetValue(square, out char piece) ? piece : '.';
    }

    static string Rotate180(string fen)
    {
        var parts = fen.Split(' ');
        var rows = parts[0].Split('/');

        for (int i = 0; i < rows.Length; i++)
            rows[i] = new string(rows[i].Reverse().ToArray());
        Array.Reverse(rows);
        parts[0] = string.Join("/", rows);

        // Rotate en-passant square if present
        if (parts.Length > 3 && parts[3] != "-" && parts[3].Length == 2)
        {
            char file = parts[3][0];
            char rank = parts[3][1];
            char file2 = (char)('a' + ('h' - file));
            char rank2 = (char)('1' + ('8' - rank));
            parts[3] = $"{file2}{rank2}";
        }

        return string.Join(" ", parts);
    }

    private static string ApplyInferredExternalTurnToFen(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen) || _currentFenIsAnalysisBoard)
            return fen;

        var parts = fen.Split(' ');
        if (parts.Length < 2)
            return fen;

        parts[1] = _inferredSideToMove == 'b' ? "b" : "w";
        return string.Join(" ", parts);
    }

}
