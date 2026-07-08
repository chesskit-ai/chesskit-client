using ChessKit;
using Chess;
using System.Diagnostics;
using System.Globalization;
using static ChessKit.FenText;
using static ChessKit.AnalysisResultUtil;

// Position-change handling, analysis results, coach overlay, prefetch, arrow building.
partial class Program
{
    private static void HandlePositionChange(string oldFEN, string newFEN)
    {
        long positionStepTicks = Stopwatch.GetTimestamp();
        void MarkPositionStep(string step)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double elapsedMs = (nowTicks - positionStepTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= 100)
            {
                LogDiag("FEN", $"position-change substep slow: {step} took {elapsedMs:F0}ms old={GetBoardPosition(oldFEN)} new={GetBoardPosition(newFEN)}");
            }
            positionStepTicks = nowTicks;
        }

        if (!string.Equals(GetBoardPosition(oldFEN), GetBoardPosition(newFEN), StringComparison.Ordinal))
        {
            CancelStaticLastMoveHighlightInitialHold();
        }
        MarkPositionStep("static highlight cancel");

        if (_stockfish != null && !string.IsNullOrEmpty(oldFEN))
        {
            CancelPendingAnalysis("position changed");
            MarkPositionStep("cancel pending analysis");
            _stockfish.ResetPosition(oldFEN);
            MarkPositionStep("reset engine position");
            _stockfish.ClearPositionCache(oldFEN); // Add this line
            MarkPositionStep("clear position cache");
            Log($"[{DateTime.Now:HH:mm:ss}] Position changed - reset analysis and cache");
            MarkPositionStep("runtime log");
        }
    }

    private static bool ApplyAnalysisResult(
        BestMoveResult result,
        string capturedFEN,
        string expectedAnalysisFen,
        bool isBlackPerspective,
        bool displayFlipped,
        bool analysisWasRotated,
        string stageLabel,
        DateTime analysisStartedUtc,
        int analysisSessionVersion)
    {
        if (!(result.Success && result.Variations.Any()))
            return false;

        if (!AnalysisResultMatchesRequestedFen(result, expectedAnalysisFen))
        {
            ArrowTimeline.Log("RESULT_DISCARD", fen: capturedFEN, stage: stageLabel, reason: "result fen mismatch");
            LogDiag("ENGINE", $"discarded stale {stageLabel} result: resultFen={result.AnalysisFen} expectedFen={expectedAnalysisFen}");
            return false;
        }

        if (!IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion))
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Analysis mode changed - discarding {stageLabel} result");
            return false;
        }

        if (_currentFEN != capturedFEN)
        {
            ArrowTimeline.Log("RESULT_DISCARD", fen: capturedFEN, stage: stageLabel, reason: "position changed before result");
            Log($"[{DateTime.Now:HH:mm:ss}] Position changed - discarding {stageLabel} result");
            return false;
        }

        // The confirmed position is only trustworthy while the detector agrees
        // with it. If an unconfirmed-FEN disagreement fired after the last
        // confirm and no confirm has landed for a while, the real board has
        // moved on and detection is stalled (e.g. a misread piece on a
        // highlighted square oscillating the candidate) - re-painting analysis
        // of the stale position would pin wrong-side arrows over the new
        // board until the stall resolves.
        if (!IsActiveAnalysisBoardFen(capturedFEN) && IsExternalDetectionStalled())
        {
            ArrowTimeline.Log("RESULT_DISCARD", fen: capturedFEN, stage: stageLabel, reason: "persistent unconfirmed disagreement - detection stalled");
            Log($"[{DateTime.Now:HH:mm:ss}] Detection disagrees with confirmed position - discarding {stageLabel} result");
            return false;
        }

        string pendingConfirmedFen = Volatile.Read(ref _pendingConfirmedFenTarget);
        if (!string.IsNullOrWhiteSpace(pendingConfirmedFen) &&
            !string.Equals(pendingConfirmedFen, capturedFEN, StringComparison.Ordinal))
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Accepted new FEN is applying - discarding stale {stageLabel} result");
            return false;
        }

        if (ShouldSuppressArrowsForPosition(capturedFEN, isBlackPerspective))
        {
            _waitingForOpponentMove = true;
            _currentMoveArrows = null;
            _lastAnalysisVariations = null;
            _lastArrowSourceFEN = "";
            RefreshDebugView("Suppressed arrows for black on starting position");
            ClearActiveArrows();

            Log($"[{DateTime.Now:HH:mm:ss}] Suppressing arrows for black on initial position");
            return false;
        }

        bool isAnalysisBoardPosition = IsActiveAnalysisBoardFen(capturedFEN);
        if (!isAnalysisBoardPosition && IsExternalBoardOutputSuspended())
        {
            LogDiag("ENGINE", $"discarded {stageLabel} result while external board output is suspended");
            return false;
        }

        int achievedDepth = GetBestResultDepth(result);
        char expectedMovingSide =
            (isAnalysisBoardPosition ? GetSideToMove(capturedFEN) : null) ??
            (isBlackPerspective ? 'b' : 'w');

        if (!isAnalysisBoardPosition &&
            _coachModeEnabled &&
            !IsExternalCoachDepthReadyForDisplay(result, out int coachTargetDepth))
        {
            ShowExternalCoachLoadingOverlay(
                capturedFEN,
                isBlackPerspective,
                displayFlipped,
                achievedDepth,
                coachTargetDepth,
                stageLabel,
                analysisSessionVersion);

            Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} coach overlay at depth {achievedDepth}; waiting for coach target depth {coachTargetDepth}");
            return true;
        }

        if (!isAnalysisBoardPosition && !IsExternalAnalysisDepthReadyForDisplay(result, stageLabel))
        {
            int requestedDepth = _stockfish?.InfiniteAnalysis == true ? Math.Max(_quickArrowDepth, 4) : Math.Clamp(_stockfish?.MaxDepth ?? achievedDepth, 0, 30);
            ArrowTimeline.Log("RESULT_HELD", fen: capturedFEN, stage: stageLabel, depth: achievedDepth, reason: "below display depth floor");
            Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} arrows at depth {achievedDepth}; waiting for safer external depth (requested {requestedDepth})");
            return false;
        }

        if (!isAnalysisBoardPosition &&
            _currentMoveArrows != null &&
            _currentMoveArrows.Any() &&
            IsSameArrowSourcePosition(capturedFEN))
        {
            int displayedDepth = GetStableDisplayedArrowDepth(capturedFEN);
            if (achievedDepth > 0 && displayedDepth > 0 && achievedDepth < displayedDepth)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} arrows at depth {achievedDepth}; already displaying depth {displayedDepth}");
                return false;
            }
        }

        if (!isAnalysisBoardPosition && IsTerminalExternalPosition(capturedFEN, out string terminalReason))
        {
            LogDiag("TURN", $"discarded {stageLabel} arrows for terminal external position ({terminalReason})");
            TryHandleTerminalExternalPosition(capturedFEN);
            return false;
        }

        int displayedResultDepth = isAnalysisBoardPosition
            ? GetBestResultDepth(result)
            : Math.Max(GetStableDisplayedArrowDepth(capturedFEN), achievedDepth);
        var displayVariations = isAnalysisBoardPosition
            ? result.Variations
            : NormalizeExternalDisplayedVariationDepths(result.Variations, displayedResultDepth);

        _lastAnalysisVariations = displayVariations;
        if (!isAnalysisBoardPosition && result.Variations.Count < Math.Min(_maxArrowCount, 5))
        {
            Log(
                $"[{DateTime.Now:HH:mm:ss}] Engine returned {result.Variations.Count}/{Math.Min(_maxArrowCount, 5)} requested lines " +
                $"for {stageLabel} result at depth {GetBestResultDepth(result)}");
        }

        if (isAnalysisBoardPosition && _analysisBoardForm != null)
        {
            var limitedVariations = displayVariations.Take(_maxArrowCount).ToList();
            _analysisBoardForm.BeginInvoke(new Action(() =>
            {
                if (IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion))
                {
                    _analysisBoardForm.SetAnalysisVariations(limitedVariations, isBlackPerspective, displayedResultDepth);
                }
            }));
        }
        else if (_engineLinesEnabled && _engineLines != null)
        {
            var limitedVariations = displayVariations.Take(_maxArrowCount).ToList();
            _engineLines.UpdateVariations(limitedVariations, isBlackPerspective);
        }

        foreach (var v in displayVariations.Take(_maxArrowCount))
        {
            if (v.Moves.Any())
            {
                Log($"  Move: {v.Moves.First()} | Score: {v.GetScoreDisplay()} | Depth: {v.Depth}");
            }
        }

        string legalityFen = analysisWasRotated ? capturedFEN : expectedAnalysisFen;

        if (!isAnalysisBoardPosition && _coachModeEnabled)
        {
            var coachData = BuildCoachOverlayData(
                capturedFEN,
                displayVariations,
                expectedMovingSide,
                displayFlipped,
                analysisWasRotated,
                displayedResultDepth,
                _coachLevel,
                _coachMarkCount);
            coachData.ShowPanel = _coachCardEnabled;

            bool shouldRenderCoachOverlay = TryStabilizeCoachOverlayData(
                capturedFEN,
                expectedMovingSide,
                displayFlipped,
                coachData,
                out var stableCoachData,
                out string coachHoldReason);

            if (shouldRenderCoachOverlay)
            {
                _currentMoveArrows = null;
                _lastArrowSourceFEN = capturedFEN;
                int coachRenderToken = Interlocked.Increment(ref _arrowRenderToken);
                int coachGeneration = Interlocked.Increment(ref _arrowDisplayGeneration);

                if (_latencyT0Utc != DateTime.MinValue)
                {
                    try
                    {
                        bool latencyBelongsToCapturedFen =
                            _lastConfirmedFenAtUtc != DateTime.MinValue &&
                            _lastConfirmedFenAtUtc >= _latencyT0Utc &&
                            string.Equals(_lastConfirmedFenForTiming, capturedFEN, StringComparison.Ordinal);

                        if (latencyBelongsToCapturedFen)
                        {
                            DateTime t2 = DateTime.UtcNow;
                            double t2MinusT0Ms = (t2 - _latencyT0Utc).TotalMilliseconds;
                            double t2MinusT1Ms = (t2 - _lastConfirmedFenAtUtc).TotalMilliseconds;
                            int markCount = stableCoachData.Marks.Count;
                            int depth = displayedResultDepth;

                            if (_diagLoggingEnabled)
                            {
                                string logLine = $"{DateTime.Now:HH:mm:ss.fff} [LATENCY] T2-T0 (detect?coach) = {t2MinusT0Ms:F0}ms (T2-T1 analysis = {t2MinusT1Ms:F0}ms, {markCount} marks, depth {depth}){Environment.NewLine}";
                                AppendDiagnosticLine(logLine);
                            }

                            _lastMoveLatencyMs = t2MinusT0Ms;
                            _lastDebugEvent = $"Move at {DateTime.Now:HH:mm:ss}: {t2MinusT0Ms:F0}ms ({markCount} coach marks, d{depth})";
                            _latencyT0Utc = DateTime.MinValue;
                            _latencyT0ChangedSquares = 0;
                        }
                        else
                        {
                            LogDiag("LATENCY", $"ignored stale T2 for {GetBoardPosition(capturedFEN)} while waiting for {GetBoardPosition(_pendingFenCandidate)}");
                        }
                    }
                    catch { }
                }

                lock (_analysisLock)
                {
                    if (!_continuousAnalysisEnabled)
                    {
                        Log($"[{DateTime.Now:HH:mm:ss}] Analysis was disabled - not showing {stageLabel} coach overlay");
                        return false;
                    }
                }

                if (_lastTrackedBox.HasValue && _overlay != null)
                {
                    var r = _lastTrackedBox.Value;
                    int generation = coachGeneration;
                    _overlay.BeginInvoke(new Action(() =>
                    {
                        if (coachRenderToken != Volatile.Read(ref _arrowRenderToken))
                            return;

                        lock (_analysisLock)
                        {
                            if (IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion) &&
                                CanDisplayArrowsForCurrentState())
                            {
                                _showingMoves = true;
                                RememberCoachOverlaySquares(capturedFEN, stableCoachData);
                                _overlay.ShowCoachOverlay(new Rectangle(r.X, r.Y, r.Width, r.Height), stableCoachData, generation, 60000);
                                RememberDisplayedArrowDepth(capturedFEN, achievedDepth);
                                double stageLatencyMs = (DateTime.UtcNow - analysisStartedUtc).TotalMilliseconds;
                                Log($"[{DateTime.Now:HH:mm:ss}] Showing coach overlay | Focus {stableCoachData.ComplexityScore} | {stableCoachData.Marks.Count} marks | {stableCoachData.Detail} | Depth {displayedResultDepth} | {stageLabel} | stage {stageLatencyMs:F0}ms");
                                RefreshDebugView($"Coach focus {stableCoachData.ComplexityScore} ({stageLabel})");
                            }
                        }
                    }));
                }
            }
            else
            {
                LogDiag("COACH", $"holding {stageLabel} coach overlay ({coachHoldReason})");
            }

            var coachBestVar = result.Variations.First();
            if (_evalBarEnabled && _evalBar != null)
            {
                double eval = coachBestVar.Score;
                bool isMate = coachBestVar.ScoreType == "mate";
                int mateIn = coachBestVar.MateIn ?? 0;

                if (isBlackPerspective)
                {
                    eval = -eval;
                    mateIn = -mateIn;
                }

                _lastEvaluation = eval;
                _evalBar.UpdateEvaluation(eval, isMate, mateIn);
            }

            return true;
        }

        var arrows = new List<MoveArrow>();
        int strength = 1;
        // Display cap stays at 5/_maxArrowCount even when the Bullet profile
        // requests MultiPV 10 - lines beyond the cap feed the PV cache
        // (PopulateAnalysisPvCache sees the full variation list), not the
        // screen.
        int arrowsToShow = Math.Min(_maxArrowCount, Math.Min(displayVariations.Count, 5));

        foreach (var variation in displayVariations.Take(arrowsToShow))
        {
            if (!variation.Moves.Any()) continue;

            var move = variation.Moves.First();
            var (originalFromFile, originalFromRank, originalToFile, originalToRank, promotionPiece) = UCIEngine.ParseMove(move);
            var fromFile = originalFromFile;
            var fromRank = originalFromRank;
            var toFile = originalToFile;
            var toRank = originalToRank;

            if (analysisWasRotated)
            {
                fromFile = 7 - fromFile;
                fromRank = 7 - fromRank;
                toFile = 7 - toFile;
                toRank = 7 - toRank;
            }

            bool arrowMoveResolved = isAnalysisBoardPosition
                ? TryResolveLegalAnalysisBoardArrowMove(
                    capturedFEN,
                    expectedMovingSide,
                    fromFile,
                    fromRank,
                    toFile,
                    toRank,
                    out fromFile,
                    out fromRank,
                    out toFile,
                    out toRank)
                : TryResolveStrictExternalArrowMove(
                    legalityFen,
                    expectedMovingSide,
                    fromFile,
                    fromRank,
                    toFile,
                    toRank,
                    out fromFile,
                    out fromRank,
                    out toFile,
                    out toRank);

            if (!arrowMoveResolved)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Skipping illegal rendered move candidate: {move}");
                continue;
            }

            if (fromFile >= 0 && fromRank >= 0 && toFile >= 0 && toRank >= 0)
            {
                arrows.Add(new MoveArrow
                {
                    FromFile = fromFile,
                    FromRank = fromRank,
                    ToFile = toFile,
                    ToRank = toRank,
                    Strength = strength,
                    IsFlipped = displayFlipped,
                    PromotionPiece = promotionPiece,
                    MovingSide = expectedMovingSide,
                    Depth = variation.Depth > 0 ? variation.Depth : displayedResultDepth
                });
                strength++;
            }
        }

        if (!arrows.Any())
        {
            if (!isAnalysisBoardPosition &&
                _currentMoveArrows != null &&
                _currentMoveArrows.Any() &&
                IsSameArrowSourcePosition(capturedFEN))
            {
                Log($"[{DateTime.Now:HH:mm:ss}] No legal renderable arrows from {stageLabel} result; keeping existing same-position arrows");
                return false;
            }

            _currentMoveArrows = null;
            _lastArrowSourceFEN = "";
            ClearActiveArrows();
            Log($"[{DateTime.Now:HH:mm:ss}] No legal renderable arrows from {stageLabel} result; cleared stale arrows");
            RecoverFromIllegalInfiniteAnalysis(capturedFEN, isBlackPerspective, analysisSessionVersion, stageLabel);
            return false;
        }

        // Skip-the-intermediate ("jump to latest"): during the user's own move,
        // a quick sequence can be confirmed in two steps - the opponent's just-
        // caught-up move (read via slow vision) lands first and would paint
        // wrong-side arrows, then the user's own move resolves ~150ms later.
        // If the board has had a RAW change since this analysis began AND we are
        // within the user's mouse-interaction window, this result is already an
        // intermediate the user has moved past - hold the current arrows and let
        // the settled position's analysis paint, so the wrong-side flash never
        // shows. Scoped to the mouse window so ordinary noise can't suppress a
        // valid paint, and only ever HOLDS (never shows wrong arrows).
        if (!isAnalysisBoardPosition &&
            DateTime.UtcNow < _recentMouseInteractionUntilUtc &&
            analysisStartedUtc != DateTime.MinValue &&
            _lastExternalRawBoardChangeUtc > analysisStartedUtc)
        {
            ArrowTimeline.Log("PAINT_SKIP_INTERMEDIATE", fen: capturedFEN, stage: stageLabel, reason: "superseded by newer board change during own move");
            if (TryRepaintCurrentExternalArrows(capturedFEN, isBlackPerspective, analysisSessionVersion, "intermediate superseded during own move"))
                return true;
            // If we can't hold cleanly, fall through and paint normally rather
            // than leave the board without arrows.
            Log($"[{DateTime.Now:HH:mm:ss}] Could not hold superseded {stageLabel} result; painting anyway");
        }

        bool isFinalExternalRefinedResult = !isAnalysisBoardPosition &&
            IsFinalExternalRefinedResult(stageLabel, achievedDepth);

        if (!isAnalysisBoardPosition &&
            !isFinalExternalRefinedResult &&
            ShouldHoldExternalTopMoveSwitch(
                capturedFEN,
                displayVariations,
                expectedMovingSide,
                achievedDepth,
                stageLabel,
                out string topMoveHoldReason))
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} arrow redraw briefly; {topMoveHoldReason}");
            if (TryRepaintCurrentExternalArrows(capturedFEN, isBlackPerspective, analysisSessionVersion, topMoveHoldReason))
                return true;

            Log($"[{DateTime.Now:HH:mm:ss}] Stable arrow repaint was unavailable; drawing {stageLabel} arrows instead");
        }

        if (!isAnalysisBoardPosition &&
            !isFinalExternalRefinedResult &&
            ShouldHoldExternalArrowSetAfterSwitchWindow(capturedFEN, arrows, out string arrowSetHoldReason))
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} arrow redraw briefly; {arrowSetHoldReason}");
            if (TryRepaintCurrentExternalArrows(capturedFEN, isBlackPerspective, analysisSessionVersion, arrowSetHoldReason))
                return true;

            Log($"[{DateTime.Now:HH:mm:ss}] Stable arrow repaint was unavailable; drawing {stageLabel} arrows instead");
        }

        if (!isAnalysisBoardPosition &&
            !isFinalExternalRefinedResult &&
            _currentMoveArrows != null &&
            _currentMoveArrows.Any() &&
            IsSameArrowSourcePosition(capturedFEN) &&
            ShouldHoldExternalArrowGeometryUpdate(capturedFEN, arrows, achievedDepth))
        {
            Log($"[{DateTime.Now:HH:mm:ss}] Holding {stageLabel} arrow redraw briefly; geometry is still settling at depth {achievedDepth}");
            if (TryRepaintCurrentExternalArrows(capturedFEN, isBlackPerspective, analysisSessionVersion, "geometry settling"))
                return true;

            Log($"[{DateTime.Now:HH:mm:ss}] Stable arrow repaint was unavailable; drawing {stageLabel} arrows instead");
        }

        if (!isAnalysisBoardPosition &&
            ShouldKeepCurrentExternalArrowOverlay(capturedFEN, arrows, out string keepReason))
        {
            _currentMoveArrows = arrows;
            _lastArrowSourceFEN = capturedFEN;
            RememberDisplayedArrowDepth(capturedFEN, achievedDepth);
            RememberExternalArrowPerspectiveDisplay(capturedFEN, isBlackPerspective);
            RememberExternalTopMoveDisplay(capturedFEN, displayVariations, achievedDepth);
            LogDiag("ARROWS", $"kept current arrows without overlay redraw ({keepReason})");
            return true;
        }

        _currentMoveArrows = arrows;
        _lastArrowSourceFEN = capturedFEN;
        if (!isAnalysisBoardPosition)
            _lastExternalArrowResultReadyUtc = DateTime.UtcNow;
        if (!isAnalysisBoardPosition)
        {
            RememberExternalArrowGeometryUpdate(capturedFEN, arrows);
            RememberExternalArrowPerspectiveDisplay(capturedFEN, isBlackPerspective);
            RememberExternalTopMoveDisplay(capturedFEN, displayVariations, achievedDepth);

            // Arrows for this position are now committed. Speculatively
            // precompute the reply to the top move so that when the user plays
            // it (the usual case), the next position is already analyzed. Skip
            // when this very result came FROM the prefetch cache (no point
            // predicting from a prediction) and for shallow intermediate
            // stages (their top move may still change).
            if (!string.Equals(stageLabel, "prefetch", StringComparison.Ordinal) &&
                displayVariations.Count > 0)
            {
                int reqDepth = GetPrefetchRequestedDepth();
                if (achievedDepth >= Math.Min(reqDepth, RemoteMinimumExternalDisplayDepth))
                    MaybeFireSpeculativePrefetch(expectedAnalysisFen, displayVariations, reqDepth);

                // PV cache (free, local+remote): each displayed line carries its
                // principal variation (the engine's predicted continuation), so
                // the positions a few plies down each line are already analyzed.
                // Cache them keyed by position; when the user plays a shown move
                // (or the opponent follows the line), the reply paints instantly
                // from cache with no re-analysis and no vision round-trip.
                if (achievedDepth >= PvCacheMinDepth)
                    PopulateAnalysisPvCache(expectedAnalysisFen, displayVariations, achievedDepth);
            }
        }
        int renderToken = Interlocked.Increment(ref _arrowRenderToken);
        int arrowGeneration = Interlocked.Increment(ref _arrowDisplayGeneration);
        ArrowTimeline.Log("ARROW_SCHEDULED", fen: capturedFEN, stage: stageLabel, depth: achievedDepth, count: arrows?.Count ?? 0, extra: $"gen={arrowGeneration}");

        // === LATENCY DIAG: log T2-T0 (detection ? arrows set) and T2-T1 ===
        // T2-T0 is the full user-visible latency: from "board changed" to
        // "arrows are ready to render."
        // T2-T1 is the analysis portion alone (engine think + parsing).
        if (_latencyT0Utc != DateTime.MinValue)
        {
            try
            {
                bool latencyBelongsToCapturedFen =
                    _lastConfirmedFenAtUtc != DateTime.MinValue &&
                    _lastConfirmedFenAtUtc >= _latencyT0Utc &&
                    string.Equals(_lastConfirmedFenForTiming, capturedFEN, StringComparison.Ordinal);

                if (latencyBelongsToCapturedFen)
                {
                    DateTime t2 = DateTime.UtcNow;
                    double t2MinusT0Ms = (t2 - _latencyT0Utc).TotalMilliseconds;
                    double t2MinusT1Ms = (t2 - _lastConfirmedFenAtUtc).TotalMilliseconds;
                    int arrowCount = arrows?.Count ?? 0;
                    int depth = displayedResultDepth;

                    if (_diagLoggingEnabled)
                    {
                        string logLine = $"{DateTime.Now:HH:mm:ss.fff} [LATENCY] T2-T0 (detect?arrows) = {t2MinusT0Ms:F0}ms (T2-T1 analysis = {t2MinusT1Ms:F0}ms, {arrowCount} arrows, depth {depth}){Environment.NewLine}";
                        AppendDiagnosticLine(logLine);
                    }

                    // Stash latency for the Debug HUD display (independent of disk log).
                    _lastMoveLatencyMs = t2MinusT0Ms;
                    _lastDebugEvent = $"Move at {DateTime.Now:HH:mm:ss}: {t2MinusT0Ms:F0}ms ({arrowCount} arrows, d{depth})";

                    // Reset for next move only after measuring the confirmed FEN.
                    _latencyT0Utc = DateTime.MinValue;
                    _latencyT0ChangedSquares = 0;
                }
                else
                {
                    LogDiag("LATENCY", $"ignored stale T2 for {GetBoardPosition(capturedFEN)} while waiting for {GetBoardPosition(_pendingFenCandidate)}");
                }
            }
            catch { }
        }

        lock (_analysisLock)
        {
            if (!_continuousAnalysisEnabled)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Analysis was disabled - not showing {stageLabel} arrows");
                return false;
            }
        }

        if (arrows is not null && arrows.Count > 0 && _lastTrackedBox.HasValue)
        {
            var r = _lastTrackedBox.Value;

            if (IsActiveAnalysisBoardFen(capturedFEN) && _analysisBoardForm != null)
            {
                _analysisBoardForm.BeginInvoke(new Action(() =>
                {
                    if (renderToken != Volatile.Read(ref _arrowRenderToken))
                        return;

                    if (IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion) &&
                        CanDisplayArrowsForCurrentState())
                    {
                        _showingMoves = true;
                        _analysisBoardForm.SetAnalysisArrows(arrows);
                        RememberDisplayedArrowDepth(capturedFEN, achievedDepth);
                        double stageLatencyMs = (DateTime.UtcNow - analysisStartedUtc).TotalMilliseconds;
                        string totalLatencySuffix = "";
                        if (_lastConfirmedFenForTiming == capturedFEN && _lastConfirmedFenAtUtc != DateTime.MinValue)
                        {
                            double totalLatencyMs = (DateTime.UtcNow - _lastConfirmedFenAtUtc).TotalMilliseconds;
                            totalLatencySuffix = $" | total {totalLatencyMs:F0}ms since FEN confirm";
                        }

                        Log($"[{DateTime.Now:HH:mm:ss}] Showing {arrows.Count} arrows | Depth {displayedResultDepth} | {stageLabel} | stage {stageLatencyMs:F0}ms{totalLatencySuffix}");
                        RefreshDebugView($"Showing {arrows.Count} arrows ({stageLabel})");
                    }
                    else
                    {
                        _analysisBoardForm.ClearAnalysisArrows();
                        _showingMoves = false;
                    }
                }));
            }
            else if (_overlay != null)
            {
                int generation = arrowGeneration;
                _overlay.BeginInvoke(new Action(() =>
                {
                    if (renderToken != Volatile.Read(ref _arrowRenderToken))
                        return;

                    lock (_analysisLock)
                    {
                        if (IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion) &&
                            CanDisplayArrowsForCurrentState())
                        {
                            _showingMoves = true;
                            _lastExternalArrowsShownUtc = DateTime.UtcNow;
                            _overlay.ShowMoveArrows(new Rectangle(r.X, r.Y, r.Width, r.Height), arrows, generation, 60000);
                            RememberExternalOverlayArrowsShown(capturedFEN, arrows.Count);
                            RememberDisplayedArrowDepth(capturedFEN, achievedDepth);
                            double stageLatencyMs = (DateTime.UtcNow - analysisStartedUtc).TotalMilliseconds;
                            string totalLatencySuffix = "";
                            if (_lastConfirmedFenForTiming == capturedFEN && _lastConfirmedFenAtUtc != DateTime.MinValue)
                            {
                                double totalLatencyMs = (DateTime.UtcNow - _lastConfirmedFenAtUtc).TotalMilliseconds;
                                totalLatencySuffix = $" | total {totalLatencyMs:F0}ms since FEN confirm";
                            }

                            Log($"[{DateTime.Now:HH:mm:ss}] Showing {arrows.Count} arrows | Depth {displayedResultDepth} | {stageLabel} | stage {stageLatencyMs:F0}ms{totalLatencySuffix}");
                            RefreshDebugView($"Showing {arrows.Count} arrows ({stageLabel})");
                        }
                    }
                }));
            }
        }

        var bestVar = result.Variations.First();
        if (!isAnalysisBoardPosition && _evalBarEnabled && _evalBar != null)
        {
            double eval = bestVar.Score;
            bool isMate = bestVar.ScoreType == "mate";
            int mateIn = bestVar.MateIn ?? 0;

            if (isBlackPerspective)
            {
                eval = -eval;
                mateIn = -mateIn;
            }

            _lastEvaluation = eval;
            _evalBar.UpdateEvaluation(eval, isMate, mateIn);
        }
        else if (isAnalysisBoardPosition)
        {
            double eval = bestVar.Score;
            if (isBlackPerspective)
                eval = -eval;

            _lastEvaluation = eval;
        }

        ScreenshotTelemetryClient.QueueAnalysisResult(
            capturedFEN,
            displayVariations,
            displayedResultDepth,
            displayFlipped,
            isAnalysisBoardPosition ? "analysis_board" : "external_board");

        return true;
    }

    private static List<MoveVariation> NormalizeExternalDisplayedVariationDepths(
        IReadOnlyList<MoveVariation> variations,
        int displayedDepth)
    {
        if (displayedDepth <= 0)
            return variations.ToList();

        return variations
            .Select(v => new MoveVariation
            {
                Rank = v.Rank,
                Depth = Math.Max(v.Depth, displayedDepth),
                Score = v.Score,
                ScoreType = v.ScoreType,
                MateIn = v.MateIn,
                Moves = v.Moves.ToList()
            })
            .ToList();
    }

    private static bool TryStabilizeCoachOverlayData(
        string capturedFen,
        char expectedMovingSide,
        bool displayFlipped,
        CoachOverlayData candidate,
        out CoachOverlayData displayData,
        out string reason)
    {
        reason = "";
        displayData = CloneCoachOverlayData(candidate);
        displayData.ComplexityScore = QuantizeCoachComplexityScore(displayData.ComplexityScore);

        string positionKey = $"{GetArrowPositionKey(capturedFen)}|{expectedMovingSide}|{displayFlipped}|L{_coachLevel}|M{_coachMarkCount}";
        string signature = BuildStableCoachDisplaySignature(displayData);
        DateTime now = DateTime.UtcNow;

        bool hasDisplayedCoach =
            _coachModeEnabled &&
            _showingMoves &&
            !string.IsNullOrWhiteSpace(_lastCoachDisplayPositionKey) &&
            !string.IsNullOrWhiteSpace(_lastCoachDisplaySignature);
        bool replacingCoachLoadingOverlay = !string.IsNullOrWhiteSpace(_lastCoachLoadingSignature);

        if (hasDisplayedCoach &&
            !replacingCoachLoadingOverlay &&
            string.Equals(positionKey, _lastCoachDisplayPositionKey, StringComparison.Ordinal) &&
            string.Equals(signature, _lastCoachDisplaySignature, StringComparison.Ordinal))
        {
            reason = "unchanged stable coach suggestion";
            return false;
        }

        string candidateKey = $"{positionKey}|{signature}";
        if (hasDisplayedCoach && !replacingCoachLoadingOverlay)
        {
            if (!string.Equals(_pendingCoachDisplayKey, candidateKey, StringComparison.Ordinal))
            {
                _pendingCoachDisplayKey = candidateKey;
                _pendingCoachDisplayCount = 1;
                _pendingCoachDisplaySinceUtc = now;
            }
            else
            {
                _pendingCoachDisplayCount++;
            }

            double pendingMs = _pendingCoachDisplaySinceUtc == DateTime.MinValue
                ? 0
                : (now - _pendingCoachDisplaySinceUtc).TotalMilliseconds;
            if (_pendingCoachDisplayCount < 2 || pendingMs < CoachOverlaySwitchConfirmMs)
            {
                reason = $"candidate not stable yet ({_pendingCoachDisplayCount} frames, {pendingMs:F0}ms)";
                return false;
            }
        }

        _lastCoachDisplayPositionKey = positionKey;
        _lastCoachDisplaySignature = signature;
        _lastCoachDisplayUtc = now;
        _pendingCoachDisplayKey = "";
        _pendingCoachDisplayCount = 0;
        _pendingCoachDisplaySinceUtc = DateTime.MinValue;
        _lastCoachLoadingSignature = "";
        return true;
    }

    private static CoachOverlayData CloneCoachOverlayData(CoachOverlayData source)
    {
        return new CoachOverlayData
        {
            ComplexityScore = source.ComplexityScore,
            Title = source.Title,
            Detail = source.Detail,
            Depth = source.Depth,
            TargetDepth = source.TargetDepth,
            IsLoading = source.IsLoading,
            ShowPanel = source.ShowPanel,
            Marks = source.Marks
                .Select(mark => new CoachSquareMark
                {
                    File = mark.File,
                    Rank = mark.Rank,
                    Strength = mark.Strength,
                    IsFlipped = mark.IsFlipped,
                    Label = mark.Label
                })
                .ToList()
        };
    }

    private static string BuildStableCoachDisplaySignature(CoachOverlayData data)
    {
        string marks = string.Join("|", data.Marks
            .OrderBy(mark => mark.Strength)
            .ThenBy(mark => mark.File)
            .ThenBy(mark => mark.Rank)
            .Select(mark => $"{mark.File},{mark.Rank}:{mark.Strength}:{mark.Label}:{mark.IsFlipped}"));

        return $"{data.ShowPanel}|{data.Title}|{data.Detail}|{marks}";
    }

    private static void ResetCoachOverlayStability()
    {
        _lastCoachDisplayPositionKey = "";
        _lastCoachDisplaySignature = "";
        _lastCoachDisplayUtc = DateTime.MinValue;
        _pendingCoachDisplayKey = "";
        _pendingCoachDisplayCount = 0;
        _pendingCoachDisplaySinceUtc = DateTime.MinValue;
        _lastCoachLoadingSignature = "";
        _lastCoachLoadingPositionKey = "";
        _lastCoachLoadingDepth = 0;
        lock (_coachOverlaySquaresLock)
        {
            _lastCoachOverlaySquaresPositionKey = "";
            _lastCoachOverlaySquares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static CoachOverlayData BuildCoachOverlayData(
        string fen,
        IReadOnlyList<MoveVariation> variations,
        char movingSide,
        bool displayFlipped,
        bool analysisWasRotated,
        int depth,
        int coachLevel,
        int requestedMarkCount)
    {
        var usable = variations.Where(v => v.Moves.Any()).Take(5).ToList();
        if (usable.Count == 0)
        {
            return new CoachOverlayData
            {
                ComplexityScore = 0,
                Title = "Coach",
                Detail = "No stable engine line",
                Depth = depth
            };
        }

        var best = usable[0];
        double bestCp = GetCoachScoreCentipawns(best);
        double secondCp = usable.Count > 1 ? GetCoachScoreCentipawns(usable[1]) : bestCp - 75.0;
        double gapCp = Math.Max(0.0, bestCp - secondCp);
        int closeLines = usable.Count(v => bestCp - GetCoachScoreCentipawns(v) <= 35.0);
        bool mateLine = string.Equals(best.ScoreType, "mate", StringComparison.OrdinalIgnoreCase);
        int mateDistance = mateLine ? Math.Abs(best.MateIn ?? 0) : 0;
        bool shortMate = mateLine && (mateDistance == 0 || mateDistance <= 3);
        double effectiveGapCp = usable.Count == 1 && mateLine ? Math.Max(gapCp, 220.0) : gapCp;
        bool firstMoveCapture = IsCoachMoveCaptureOrPromotion(fen, best.Moves[0], analysisWasRotated);

        double standout = Clamp01((effectiveGapCp - 18.0) / 115.0);
        double forcing = mateLine ? 1.0 : Clamp01((effectiveGapCp - 45.0) / 150.0);
        double advantage = Clamp01(Math.Max(0.0, bestCp) / 220.0);
        double choicePressure = Clamp01((closeLines - 1.0) / 3.0);
        double hiddenBonus = (!firstMoveCapture && best.Moves.Count >= 3) ? 0.12 : (mateLine ? 0.0 : -0.05);

        int complexity = (int)Math.Round(Math.Clamp(
            18.0 +
            standout * 34.0 +
            forcing * 18.0 +
            advantage * 14.0 +
            choicePressure * 10.0 +
            hiddenBonus * 100.0,
            0.0,
            100.0), MidpointRounding.AwayFromZero);
        if (shortMate)
            complexity = Math.Max(complexity, 88);
        else if (mateLine)
            complexity = Math.Max(complexity, 76);
        else if (usable.Count == 1 && best.Moves.Count > 0)
            complexity = Math.Max(complexity, 56);

        string detail;
        if (mateLine)
            detail = "Forcing sequence";
        else if (effectiveGapCp >= 80.0 && !firstMoveCapture)
            detail = "Hidden candidate stands out";
        else if (effectiveGapCp >= 60.0)
            detail = "Best line matters";
        else if (closeLines >= 3)
            detail = "Several playable plans";
        else
            detail = "Low forcing pressure";

        int level = Math.Clamp(coachLevel, 1, 10);
        int threshold = 92 - level * 7;
        if (shortMate)
            threshold = Math.Min(threshold, 38);
        else if (mateLine)
            threshold = Math.Min(threshold, 50);
        else if (effectiveGapCp >= 100.0)
            threshold = Math.Min(threshold, 55);

        int levelMarkCap = level <= 3 ? 1 : level <= 7 ? 2 : 3;
        int maxMarks = Math.Min(Math.Clamp(requestedMarkCount, 1, 3), levelMarkCap);
        var marks = new List<CoachSquareMark>();

        if (complexity >= threshold)
        {
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            int strength = 1;
            bool bestLineIsForcing = mateLine || effectiveGapCp >= 80.0 || usable.Count == 1;
            AddBestLineCoachMarks(
                best,
                analysisWasRotated,
                displayFlipped,
                maxMarks,
                marks,
                occupied,
                ref strength,
                includeSourceFallback: shortMate && level >= 4);

            foreach (var variation in usable)
            {
                if (marks.Count >= maxMarks)
                    break;

                if (ReferenceEquals(variation, best) || bestLineIsForcing)
                    continue;

                if (!TrySelectCoachSquare(
                        fen,
                        variation,
                        analysisWasRotated,
                        allowObviousFirstMove: mateLine || effectiveGapCp >= 100.0 || level >= 7,
                        out int file,
                        out int rank))
                {
                    continue;
                }

                string key = $"{file},{rank}";
                if (!occupied.Add(key))
                    continue;

                marks.Add(new CoachSquareMark
                {
                    File = file,
                    Rank = rank,
                    Strength = strength,
                    IsFlipped = displayFlipped,
                    Label = strength.ToString(CultureInfo.InvariantCulture)
                });
                strength++;
            }
        }

        string title = complexity switch
        {
            >= 78 => "Coach Critical",
            >= 58 => "Coach Sharp",
            >= 38 => "Coach Watch",
            _ => "Coach Quiet"
        };

        return new CoachOverlayData
        {
            ComplexityScore = complexity,
            Title = title,
            Detail = detail,
            Depth = depth,
            Marks = marks
        };
    }

    private static void AddBestLineCoachMarks(
        MoveVariation variation,
        bool analysisWasRotated,
        bool displayFlipped,
        int maxMarks,
        List<CoachSquareMark> marks,
        HashSet<string> occupied,
        ref int strength,
        bool includeSourceFallback)
    {
        var sourceFallbacks = new List<(int File, int Rank)>();
        int pliesToInspect = Math.Min(variation.Moves.Count, 5);
        for (int ply = 0; ply < pliesToInspect && marks.Count < maxMarks; ply += 2)
        {
            if (!TryGetAdjustedMoveSquares(
                    variation.Moves[ply],
                    analysisWasRotated,
                    out int fromFile,
                    out int fromRank,
                    out int toFile,
                    out int toRank,
                    out _))
            {
                continue;
            }

            TryAddCoachMark(marks, occupied, maxMarks, toFile, toRank, displayFlipped, ref strength);
            if (includeSourceFallback && ply == 0)
                sourceFallbacks.Add((fromFile, fromRank));
        }

        foreach (var square in sourceFallbacks)
        {
            if (marks.Count >= maxMarks)
                break;

            TryAddCoachMark(marks, occupied, maxMarks, square.File, square.Rank, displayFlipped, ref strength);
        }
    }

    private static bool TryAddCoachMark(
        List<CoachSquareMark> marks,
        HashSet<string> occupied,
        int maxMarks,
        int file,
        int rank,
        bool displayFlipped,
        ref int strength)
    {
        if (marks.Count >= maxMarks || file < 0 || file > 7 || rank < 0 || rank > 7)
            return false;

        string key = $"{file},{rank}";
        if (!occupied.Add(key))
            return false;

        marks.Add(new CoachSquareMark
        {
            File = file,
            Rank = rank,
            Strength = strength,
            IsFlipped = displayFlipped,
            Label = strength.ToString(CultureInfo.InvariantCulture)
        });
        strength++;
        return true;
    }

    private static bool TrySelectCoachSquare(
        string fen,
        MoveVariation variation,
        bool analysisWasRotated,
        bool allowObviousFirstMove,
        out int file,
        out int rank)
    {
        file = -1;
        rank = -1;

        int fallbackFile = -1;
        int fallbackRank = -1;
        int pliesToInspect = Math.Min(variation.Moves.Count, 5);
        for (int ply = 0; ply < pliesToInspect; ply += 2)
        {
            if (!TryGetAdjustedMoveTarget(variation.Moves[ply], analysisWasRotated, out int toFile, out int toRank, out _))
                continue;

            if (fallbackFile < 0)
            {
                fallbackFile = toFile;
                fallbackRank = toRank;
            }

            bool obvious = IsCoachMoveCaptureOrPromotion(fen, variation.Moves[ply], analysisWasRotated);
            if (!obvious || ply > 0 || (ply == 0 && allowObviousFirstMove))
            {
                file = toFile;
                rank = toRank;
                return true;
            }
        }

        if (fallbackFile >= 0)
        {
            file = fallbackFile;
            rank = fallbackRank;
            return true;
        }

        return false;
    }

    private static bool IsCoachMoveCaptureOrPromotion(string fen, string uciMove, bool analysisWasRotated)
    {
        if (!TryGetAdjustedMoveTarget(uciMove, analysisWasRotated, out int toFile, out int toRank, out char promotionPiece))
            return false;

        return promotionPiece != '\0' || GetFenPieceAt(fen, toFile, toRank) != '\0';
    }

    private static bool TryGetAdjustedMoveTarget(
        string uciMove,
        bool analysisWasRotated,
        out int toFile,
        out int toRank,
        out char promotionPiece)
    {
        toFile = -1;
        toRank = -1;
        promotionPiece = '\0';

        try
        {
            var (_, _, originalToFile, originalToRank, parsedPromotion) = UCIEngine.ParseMove(uciMove);
            toFile = originalToFile;
            toRank = originalToRank;
            promotionPiece = parsedPromotion;

            if (analysisWasRotated)
            {
                toFile = 7 - toFile;
                toRank = 7 - toRank;
            }

            return toFile >= 0 && toFile <= 7 && toRank >= 0 && toRank <= 7;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetAdjustedMoveSquares(
        string uciMove,
        bool analysisWasRotated,
        out int fromFile,
        out int fromRank,
        out int toFile,
        out int toRank,
        out char promotionPiece)
    {
        fromFile = -1;
        fromRank = -1;
        toFile = -1;
        toRank = -1;
        promotionPiece = '\0';

        try
        {
            var (parsedFromFile, parsedFromRank, parsedToFile, parsedToRank, parsedPromotion) = UCIEngine.ParseMove(uciMove);
            fromFile = parsedFromFile;
            fromRank = parsedFromRank;
            toFile = parsedToFile;
            toRank = parsedToRank;
            promotionPiece = parsedPromotion;

            if (analysisWasRotated)
            {
                fromFile = 7 - fromFile;
                fromRank = 7 - fromRank;
                toFile = 7 - toFile;
                toRank = 7 - toRank;
            }

            return fromFile >= 0 && fromFile <= 7 &&
                   fromRank >= 0 && fromRank <= 7 &&
                   toFile >= 0 && toFile <= 7 &&
                   toRank >= 0 && toRank <= 7;
        }
        catch
        {
            return false;
        }
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static int GetDisplayedAnalysisDepth()
    {
        if (_lastAnalysisVariations != null && _lastAnalysisVariations.Any(v => v.Depth > 0))
            return _lastAnalysisVariations.Max(v => v.Depth);

        return 0;
    }

    private static bool IsSameArrowSourcePosition(string fen)
    {
        return !string.IsNullOrWhiteSpace(_lastArrowSourceFEN) &&
               string.Equals(GetArrowPositionKey(_lastArrowSourceFEN), GetArrowPositionKey(fen), StringComparison.Ordinal);
    }

    private static int GetStableDisplayedArrowDepth(string fen)
    {
        string key = GetArrowPositionKey(fen);
        if (!string.IsNullOrWhiteSpace(fen) &&
            string.Equals(_lastDisplayedArrowDepthFEN, key, StringComparison.Ordinal) &&
            _lastDisplayedArrowDepth > 0)
        {
            return _lastDisplayedArrowDepth;
        }

        return GetDisplayedAnalysisDepth();
    }

    private static void RememberDisplayedArrowDepth(string fen, int depth)
    {
        if (string.IsNullOrWhiteSpace(fen) || depth <= 0)
            return;

        string key = GetArrowPositionKey(fen);
        if (!string.Equals(_lastDisplayedArrowDepthFEN, key, StringComparison.Ordinal))
        {
            _lastDisplayedArrowDepthFEN = key;
            _lastDisplayedArrowDepth = depth;
            return;
        }

        if (depth > _lastDisplayedArrowDepth)
            _lastDisplayedArrowDepth = depth;
    }

    private static bool ShouldHoldExternalTopMoveSwitch(
        string fen,
        IReadOnlyList<MoveVariation> variations,
        char expectedMovingSide,
        int achievedDepth,
        string stageLabel,
        out string reason)
    {
        reason = "";
        if (variations.Count == 0 || variations[0].Moves.Count == 0)
            return false;

        string positionKey = GetArrowPositionKey(fen);
        string candidateMove = NormalizeUciMoveKey(variations[0].Moves[0]);
        if (string.IsNullOrWhiteSpace(positionKey) || string.IsNullOrWhiteSpace(candidateMove))
            return false;

        bool sameDisplayedPosition =
            _showingMoves &&
            _currentMoveArrows is { Count: > 0 } &&
            IsSameArrowSourcePosition(fen) &&
            string.Equals(_lastExternalTopMovePositionKey, positionKey, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_lastExternalTopMoveUci);

        if (!sameDisplayedPosition)
            return false;

        if (string.Equals(candidateMove, _lastExternalTopMoveUci, StringComparison.OrdinalIgnoreCase))
            return false;

        DateTime now = DateTime.UtcNow;
        double positionVisibleAgeMs = _firstExternalTopMovePositionDisplayUtc == DateTime.MinValue
            ? (_lastExternalTopMoveDisplayUtc == DateTime.MinValue
                ? double.MaxValue
                : (now - _lastExternalTopMoveDisplayUtc).TotalMilliseconds)
            : (now - _firstExternalTopMovePositionDisplayUtc).TotalMilliseconds;
        if (positionVisibleAgeMs > ExternalArrowStaleDisplayMemoryMs)
        {
            string positionAgeText = positionVisibleAgeMs == double.MaxValue
                ? "unknown"
                : $"{positionVisibleAgeMs:F0}ms";
            LogDiag("ARROWS", $"reset stale external top-move stability ({positionAgeText})");
            ResetExternalArrowDisplayStability();
            return false;
        }

        if (positionVisibleAgeMs >= ExternalTopMoveSwitchWindowMs)
        {
            string positionAgeText = positionVisibleAgeMs == double.MaxValue
                ? "unknown"
                : $"{positionVisibleAgeMs:F0}ms";
            reason =
                $"top move switch window expired ({positionAgeText}); keeping {_lastExternalTopMoveUci} over {candidateMove}";
            return true;
        }

        if (string.Equals(_lastExternalTopMoveSwitchCountPositionKey, positionKey, StringComparison.Ordinal) &&
            _lastExternalTopMoveSwitchCount >= ExternalTopMoveMaxSwitchesPerPosition)
        {
            reason =
                $"top move already switched once for this position; keeping {_lastExternalTopMoveUci} over {candidateMove}";
            return true;
        }

        double pendingAgeMs = _pendingExternalTopMoveSinceUtc == DateTime.MinValue
            ? double.MaxValue
            : (now - _pendingExternalTopMoveSinceUtc).TotalMilliseconds;

        bool samePending =
            pendingAgeMs <= ExternalTopMovePendingMaxAgeMs &&
            string.Equals(_pendingExternalTopMovePositionKey, positionKey, StringComparison.Ordinal) &&
            string.Equals(_pendingExternalTopMoveUci, candidateMove, StringComparison.OrdinalIgnoreCase);

        if (samePending)
        {
            _pendingExternalTopMoveCount++;
        }
        else
        {
            _pendingExternalTopMovePositionKey = positionKey;
            _pendingExternalTopMoveUci = candidateMove;
            _pendingExternalTopMoveCount = 1;
            _pendingExternalTopMoveSinceUtc = now;
            pendingAgeMs = 0;
        }

        int confirmMs = BlitzExternalTopMoveSwitchConfirmMs;
        int minVisibleMs = BlitzExternalTopMoveMinVisibleMs;
        double visibleAgeMs = _lastExternalTopMoveDisplayUtc == DateTime.MinValue
            ? double.MaxValue
            : (now - _lastExternalTopMoveDisplayUtc).TotalMilliseconds;
        bool candidateStable =
            _pendingExternalTopMoveCount >= 2 &&
            pendingAgeMs >= confirmMs;

        if (candidateStable && visibleAgeMs >= minVisibleMs)
        {
            ResetPendingExternalTopMoveSwitch();
            return false;
        }

        string visibleAgeText = visibleAgeMs == double.MaxValue
            ? "unknown"
            : $"{visibleAgeMs:F0}ms";
        string positionVisibleAgeText = positionVisibleAgeMs == double.MaxValue
            ? "unknown"
            : $"{positionVisibleAgeMs:F0}ms";
        reason =
            $"top move switch {_lastExternalTopMoveUci}->{candidateMove} not stable yet " +
            $"({_pendingExternalTopMoveCount} frames, {pendingAgeMs:F0}ms, visible {visibleAgeText}, position {positionVisibleAgeText})";
        return true;
    }

    private static void RememberExternalTopMoveDisplay(
        string fen,
        IReadOnlyList<MoveVariation> variations,
        int achievedDepth)
    {
        if (variations.Count == 0 || variations[0].Moves.Count == 0)
            return;

        string positionKey = GetArrowPositionKey(fen);
        string moveKey = NormalizeUciMoveKey(variations[0].Moves[0]);
        DateTime now = DateTime.UtcNow;
        bool changedPosition =
            !string.Equals(_lastExternalTopMovePositionKey, positionKey, StringComparison.Ordinal);
        bool changedDisplayedMove =
            changedPosition ||
            !string.Equals(_lastExternalTopMoveUci, moveKey, StringComparison.OrdinalIgnoreCase);
        bool switchedSamePosition =
            string.Equals(_lastExternalTopMovePositionKey, positionKey, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_lastExternalTopMoveUci) &&
            !string.Equals(_lastExternalTopMoveUci, moveKey, StringComparison.OrdinalIgnoreCase);

        if (changedPosition || _firstExternalTopMovePositionDisplayUtc == DateTime.MinValue)
        {
            _firstExternalTopMovePositionDisplayUtc = now;
        }

        _lastExternalTopMovePositionKey = positionKey;
        _lastExternalTopMoveUci = moveKey;
        _lastExternalTopMoveDepth = achievedDepth;
        _lastExternalTopMoveScoreCp = GetExternalTopMoveScoreCp(variations[0], GetSideToMove(fen) ?? _inferredSideToMove);
        if (changedDisplayedMove || _lastExternalTopMoveDisplayUtc == DateTime.MinValue)
        {
            _lastExternalTopMoveDisplayUtc = now;
        }
        if (switchedSamePosition)
        {
            if (!string.Equals(_lastExternalTopMoveSwitchCountPositionKey, positionKey, StringComparison.Ordinal))
            {
                _lastExternalTopMoveSwitchCountPositionKey = positionKey;
                _lastExternalTopMoveSwitchCount = 0;
            }

            _lastExternalTopMoveSwitchCount++;
        }
        else if (!string.Equals(_lastExternalTopMoveSwitchCountPositionKey, positionKey, StringComparison.Ordinal))
        {
            _lastExternalTopMoveSwitchCountPositionKey = positionKey;
            _lastExternalTopMoveSwitchCount = 0;
        }
        ResetPendingExternalTopMoveSwitch();
    }

    private static void RememberExternalArrowPerspectiveDisplay(string fen, bool isBlackPerspective)
    {
        if (_currentFenIsAnalysisBoard || IsActiveAnalysisBoardFen(fen))
            return;

        string boardKey = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(boardKey))
            return;

        DateTime now = DateTime.UtcNow;
        bool changedBoard = !string.Equals(_externalArrowPerspectiveBoardKey, boardKey, StringComparison.Ordinal);
        if (changedBoard || _externalArrowPerspectiveFirstDisplayUtc == DateTime.MinValue)
        {
            _externalArrowPerspectiveBoardKey = boardKey;
            _externalArrowPerspectiveBlack = isBlackPerspective;
            _externalArrowPerspectiveFirstDisplayUtc = now;
            LogDiag("TURN", $"external arrow perspective started {(isBlackPerspective ? "BLACK" : "WHITE")} for board={boardKey}");
            return;
        }

        if (_externalArrowPerspectiveBlack == isBlackPerspective)
            return;

        double visibleAgeMs = (now - _externalArrowPerspectiveFirstDisplayUtc).TotalMilliseconds;
        if (visibleAgeMs < ExternalArrowPerspectiveSwitchWindowMs)
        {
            _externalArrowPerspectiveBlack = isBlackPerspective;
            LogDiag("TURN", $"external arrow perspective switched within window to {(isBlackPerspective ? "BLACK" : "WHITE")} at {visibleAgeMs:F0}ms");
        }
        else
        {
            LogDiag("TURN", $"ignored late external arrow perspective memory switch to {(isBlackPerspective ? "BLACK" : "WHITE")} at {visibleAgeMs:F0}ms");
        }
    }

    private static void RememberExternalOverlayArrowsShown(string fen, int arrowCount)
    {
        if (_currentFenIsAnalysisBoard || IsActiveAnalysisBoardFen(fen))
            return;

        _externalOverlayArrowsShownUtc = DateTime.UtcNow;
        _externalOverlayArrowsFen = fen;
        _externalOverlayArrowsCount = Math.Max(0, arrowCount);
    }

    private static void ClearExternalOverlayArrowMemory()
    {
        _externalOverlayArrowsShownUtc = DateTime.MinValue;
        _externalOverlayArrowsFen = "";
        _externalOverlayArrowsCount = 0;
    }

    private static bool HasExternalVisibleArrowMemory()
    {
        return _externalOverlayArrowsShownUtc != DateTime.MinValue ||
               _externalArrowPerspectiveFirstDisplayUtc != DateTime.MinValue ||
               _lastExternalTopMoveDisplayUtc != DateTime.MinValue;
    }

    private static DateTime GetExternalVisibleArrowSinceUtc()
    {
        DateTime since = DateTime.MinValue;

        if (_externalOverlayArrowsShownUtc != DateTime.MinValue)
            since = _externalOverlayArrowsShownUtc;

        if (_externalArrowPerspectiveFirstDisplayUtc != DateTime.MinValue &&
            (since == DateTime.MinValue || _externalArrowPerspectiveFirstDisplayUtc < since))
        {
            since = _externalArrowPerspectiveFirstDisplayUtc;
        }

        if (_lastExternalTopMoveDisplayUtc != DateTime.MinValue &&
            (since == DateTime.MinValue || _lastExternalTopMoveDisplayUtc < since))
        {
            since = _lastExternalTopMoveDisplayUtc;
        }

        return since;
    }

    private static bool TryRepaintCurrentExternalArrows(
        string fen,
        bool isBlackPerspective,
        int analysisSessionVersion,
        string reason)
    {
        if (_overlay == null ||
            !_lastTrackedBox.HasValue ||
            _currentMoveArrows == null ||
            _currentMoveArrows.Count == 0 ||
            !IsSameArrowSourcePosition(fen))
        {
            return false;
        }

        if (_showingMoves)
        {
            LogDiag("ARROWS", $"kept stable arrows while holding redraw ({reason})");
            return true;
        }

        var arrows = _currentMoveArrows
            .Select(a => new MoveArrow
            {
                FromFile = a.FromFile,
                FromRank = a.FromRank,
                ToFile = a.ToFile,
                ToRank = a.ToRank,
                Strength = a.Strength,
                IsFlipped = a.IsFlipped,
                PromotionPiece = a.PromotionPiece,
                MovingSide = a.MovingSide,
                Depth = a.Depth
            })
            .ToList();
        var r = _lastTrackedBox.Value;
        int repaintToken = Volatile.Read(ref _arrowRenderToken);
        int generation = _arrowDisplayGeneration;

        _overlay.BeginInvoke(new Action(() =>
        {
            if (repaintToken != Volatile.Read(ref _arrowRenderToken))
                return;

            lock (_analysisLock)
            {
                if (IsAnalysisRequestStillCurrent(fen, isBlackPerspective, analysisSessionVersion) &&
                    CanDisplayArrowsForCurrentState())
                {
                    _showingMoves = true;
                    _overlay.ShowMoveArrows(new Rectangle(r.X, r.Y, r.Width, r.Height), arrows, generation, 60000);
                    RememberExternalOverlayArrowsShown(fen, arrows.Count);
                    LogDiag("ARROWS", $"repainted stable arrows while holding redraw ({reason})");
                }
            }
        }));

        return true;
    }

    private static bool ShouldKeepCurrentExternalArrowOverlay(
        string fen,
        List<MoveArrow> arrows,
        out string reason)
    {
        reason = "";
        if (!_showingMoves ||
            _currentMoveArrows == null ||
            _currentMoveArrows.Count == 0 ||
            arrows.Count == 0 ||
            !IsSameArrowSourcePosition(fen))
        {
            return false;
        }

        string currentGeometryKey = GetExternalArrowGeometryKey(fen, _currentMoveArrows);
        string nextGeometryKey = GetExternalArrowGeometryKey(fen, arrows);
        if (string.IsNullOrWhiteSpace(currentGeometryKey) ||
            string.IsNullOrWhiteSpace(nextGeometryKey) ||
            !string.Equals(currentGeometryKey, nextGeometryKey, StringComparison.Ordinal))
        {
            return false;
        }

        reason = "geometry unchanged";
        return true;
    }

    private static bool ShouldHoldExternalArrowSetAfterSwitchWindow(
        string fen,
        List<MoveArrow> arrows,
        out string reason)
    {
        reason = "";
        if (!_showingMoves ||
            _currentMoveArrows == null ||
            _currentMoveArrows.Count == 0 ||
            arrows.Count == 0 ||
            !IsSameArrowSourcePosition(fen))
        {
            return false;
        }

        DateTime firstDisplayUtc = _firstExternalTopMovePositionDisplayUtc;
        if (firstDisplayUtc == DateTime.MinValue)
            firstDisplayUtc = _lastExternalArrowGeometryUpdateUtc;
        if (firstDisplayUtc == DateTime.MinValue)
            return false;

        double visibleAgeMs = (DateTime.UtcNow - firstDisplayUtc).TotalMilliseconds;
        if (visibleAgeMs > ExternalArrowStaleDisplayMemoryMs)
        {
            LogDiag("ARROWS", $"reset stale external arrow-set stability ({visibleAgeMs:F0}ms)");
            ResetExternalArrowDisplayStability();
            return false;
        }

        if (visibleAgeMs < ExternalTopMoveSwitchWindowMs)
            return false;

        string currentGeometryKey = GetExternalArrowBoardGeometryKey(_lastArrowSourceFEN, _currentMoveArrows);
        string nextGeometryKey = GetExternalArrowBoardGeometryKey(fen, arrows);
        if (string.IsNullOrWhiteSpace(currentGeometryKey) ||
            string.IsNullOrWhiteSpace(nextGeometryKey) ||
            string.Equals(currentGeometryKey, nextGeometryKey, StringComparison.Ordinal))
        {
            return false;
        }

        reason = $"arrow set switch window expired ({visibleAgeMs:F0}ms); keeping current arrows";
        return true;
    }

    private static bool IsFinalExternalRefinedResult(string stageLabel, int achievedDepth)
    {
        if (!string.Equals(stageLabel, "refined", StringComparison.OrdinalIgnoreCase) ||
            _stockfish?.InfiniteAnalysis == true)
        {
            return false;
        }

        int requestedDepth = Math.Clamp(_stockfish?.MaxDepth ?? 0, 0, 30);
        return requestedDepth <= 0 || achievedDepth >= requestedDepth;
    }


    private static void ResetPendingExternalTopMoveSwitch()
    {
        _pendingExternalTopMovePositionKey = "";
        _pendingExternalTopMoveUci = "";
        _pendingExternalTopMoveCount = 0;
        _pendingExternalTopMoveSinceUtc = DateTime.MinValue;
    }

    private static void ResetExternalTopMoveStability()
    {
        _lastExternalTopMovePositionKey = "";
        _lastExternalTopMoveUci = "";
        _lastExternalTopMoveDepth = 0;
        _lastExternalTopMoveScoreCp = double.NaN;
        _lastExternalTopMoveDisplayUtc = DateTime.MinValue;
        _firstExternalTopMovePositionDisplayUtc = DateTime.MinValue;
        _lastExternalTopMoveSwitchCountPositionKey = "";
        _lastExternalTopMoveSwitchCount = 0;
        _externalArrowPerspectiveBoardKey = "";
        _externalArrowPerspectiveBlack = false;
        _externalArrowPerspectiveFirstDisplayUtc = DateTime.MinValue;
        ResetPendingExternalTopMoveSwitch();
    }

    private static void ResetExternalArrowDisplayStability()
    {
        _lastExternalArrowGeometryKey = "";
        _lastExternalArrowGeometryUpdateUtc = DateTime.MinValue;
        ResetExternalTopMoveStability();
    }

    private static bool ShouldHoldExternalArrowGeometryUpdate(string fen, List<MoveArrow> arrows, int achievedDepth)
    {
        string key = GetExternalArrowGeometryKey(fen, arrows);
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, _lastExternalArrowGeometryKey, StringComparison.Ordinal))
            return false;

        int displayedDepth = GetStableDisplayedArrowDepth(fen);
        if (displayedDepth <= 0 || achievedDepth <= displayedDepth)
            return false;

        // Streamed MultiPV can briefly flip between two candidate orders at
        // adjacent depths. Holding geometry-only redraws for one UI beat keeps
        // arrows responsive without making them visually strobe.
        return DateTime.UtcNow < _lastExternalArrowGeometryUpdateUtc.AddMilliseconds(160);
    }

    private static void RememberExternalArrowGeometryUpdate(string fen, List<MoveArrow> arrows)
    {
        string key = GetExternalArrowGeometryKey(fen, arrows);
        if (string.IsNullOrWhiteSpace(key) ||
            string.Equals(key, _lastExternalArrowGeometryKey, StringComparison.Ordinal))
        {
            return;
        }

        _lastExternalArrowGeometryKey = key;
        _lastExternalArrowGeometryUpdateUtc = DateTime.UtcNow;
    }

    private static string GetExternalArrowGeometryKey(string fen, List<MoveArrow> arrows)
    {
        if (string.IsNullOrWhiteSpace(fen) || arrows.Count == 0)
            return "";

        return GetArrowPositionKey(fen) + "|" + GetExternalArrowGeometryParts(arrows);
    }

    private static string GetExternalArrowBoardGeometryKey(string fen, List<MoveArrow> arrows)
    {
        if (string.IsNullOrWhiteSpace(fen) || arrows.Count == 0)
            return "";

        return GetBoardPosition(fen) + "|" + GetExternalArrowGeometryParts(arrows);
    }

    private static string GetExternalArrowGeometryParts(List<MoveArrow> arrows)
    {
        return string.Join(";",
            arrows.Select(a =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{a.FromFile},{a.FromRank}>{a.ToFile},{a.ToRank}:{a.PromotionPiece}:{a.Strength:F3}")));
    }

    private static void ClearDisplayedArrowDepthMemory()
    {
        _lastDisplayedArrowDepthFEN = "";
        _lastDisplayedArrowDepth = 0;
        ResetExternalArrowDisplayStability();
    }

    private static bool IsExternalDetectionStalled()
    {
        if (_lastConfirmedFenAtUtc == DateTime.MinValue)
            return false;

        // "Agreement" is either a confirmed position OR the detector seeing
        // the current board again (a lifted piece put back, transient noise
        // resolving). Without the latter, a player picking up a piece to
        // think would latch the disagreement until their next actual move,
        // blanking arrows for the whole pause.
        DateTime lastAgreementUtc = _lastConfirmedFenAtUtc > _lastCurrentExternalBoardObservationUtc
            ? _lastConfirmedFenAtUtc
            : _lastCurrentExternalBoardObservationUtc;

        return _lastUnconfirmedFenArrowClearUtc > lastAgreementUtc &&
            (DateTime.UtcNow - lastAgreementUtc).TotalMilliseconds > StaleConfirmRepaintBlockMs;
    }

    private static bool IsExternalAnalysisDepthReadyForDisplay(BestMoveResult result, string stageLabel)
    {
        bool infiniteAnalysis = _stockfish?.InfiniteAnalysis == true;
        // The remote raised floor only applies to intermediate (quick/stream)
        // stages: a refined result is the engine's final, possibly time-capped
        // answer and must always be displayable, or low-depth configurations
        // would never show arrows at all. A served "prefetch" result is also
        // final (precomputed at the requested depth) and must display reliably.
        bool remoteIntermediateStage = _stockfish?.IsRemoteEngineActive == true &&
            !string.Equals(stageLabel, "refined", StringComparison.Ordinal) &&
            !string.Equals(stageLabel, "prefetch", StringComparison.Ordinal);
        int requestedDepth = infiniteAnalysis ? Math.Max(_quickArrowDepth, 4) : Math.Clamp(_stockfish?.MaxDepth ?? 0, 0, 30);
        if (infiniteAnalysis && remoteIntermediateStage)
        {
            // Remote infinite analysis runs as one streamed deep search; the
            // shallow-depth packets relocate just as visibly as in the fixed
            // depth path, so they get the same floor instead of the quick
            // preview depth.
            requestedDepth = Math.Max(requestedDepth, RemoteMinimumExternalDisplayDepth);
        }
        if (requestedDepth <= 1)
            return true;

        int achievedDepth = GetBestResultDepth(result);
        if (achievedDepth <= 0)
            return true;

        // External boards are noisy compared with the built-in analysis board:
        // they can reset puzzles, animate opponent replies, or briefly expose
        // stale frames. Showing a depth-1 preview when the user asked for a
        // real depth makes those transient reads look like confident advice.
        // Keep instant previews for very low settings, but require a small
        // safety floor once the requested depth is 5+. Remote intermediate
        // stages get a deeper floor: their shallow streamed depths arrive a
        // full network round-trip apart, so a depth-4 preview stays on screen
        // long enough to be seen relocating once deeper results land. The
        // floor stays at least 2 below the requested depth so MaxDepth 5-9
        // configurations keep a progressive preview before the final result.
        // One floor for everyone: the former blitz depth-2 preview floor is
        // gone - it traded arrow stability for an earlier shallow guess,
        // which reads as the arrow jumping. The remote floor already paints
        // in ~300ms; speed comes from detection, not from shallower arrows.
        // For ordinary depth settings (<= 14) intermediate stream packets
        // must reach the FULL requested depth before painting: a depth-10
        // preview repainted by a different depth-12 final ~50ms later was
        // measured as a visible double-flicker (9 per game), and the final
        // arrives only a few dozen ms after the preview anyway. Deeper
        // searches keep a progressive floor - there the gap is real.
        int defaultFloor = remoteIntermediateStage
            ? (requestedDepth <= 14
                ? requestedDepth
                : Math.Clamp(requestedDepth - 2, 4, RemoteMinimumExternalDisplayDepth))
            : 4;
        int minimumDisplayDepth = requestedDepth <= 4
            ? requestedDepth
            : Math.Min(requestedDepth, defaultFloor);

        return achievedDepth >= minimumDisplayDepth;
    }

    private static int GetExternalCoachTargetDepth()
    {
        if (_stockfish?.InfiniteAnalysis == true)
            return Math.Max(1, BuildLimits.ClampDepth(ExternalCoachInfiniteTargetDepth));

        int requestedDepth = BuildLimits.ClampDepth(_stockfish?.MaxDepth ?? _settingsToolbar?.GetMaxDepth() ?? BuildLimits.MaxDepth);
        return requestedDepth <= 0 ? 1 : requestedDepth;
    }

    private static bool IsExternalCoachDepthReadyForDisplay(BestMoveResult result, out int targetDepth)
    {
        targetDepth = GetExternalCoachTargetDepth();
        if (targetDepth <= 1)
            return true;

        int achievedDepth = GetBestResultDepth(result);
        return achievedDepth <= 0 || achievedDepth >= targetDepth;
    }

    private static void ShowExternalCoachLoadingOverlay(
        string capturedFEN,
        bool isBlackPerspective,
        bool displayFlipped,
        int achievedDepth,
        int targetDepth,
        string stageLabel,
        int analysisSessionVersion)
    {
        if (_overlay == null || !_lastTrackedBox.HasValue)
            return;

        int safeTargetDepth = Math.Max(1, targetDepth);
        string positionKey = GetArrowPositionKey(capturedFEN);
        if (!string.Equals(_lastCoachLoadingPositionKey, positionKey, StringComparison.Ordinal))
        {
            ResetCoachOverlayStability();
            _lastCoachLoadingPositionKey = positionKey;
            _lastCoachLoadingDepth = 0;
        }

        int safeAchievedDepth = Math.Clamp(achievedDepth, 0, safeTargetDepth);
        if (safeAchievedDepth < _lastCoachLoadingDepth)
        {
            LogDiag("COACH", $"holding loading progress at depth {_lastCoachLoadingDepth}/{safeTargetDepth}; ignored lower {stageLabel} depth {safeAchievedDepth}");
            return;
        }

        safeAchievedDepth = Math.Max(safeAchievedDepth, _lastCoachLoadingDepth);
        string loadingSignature = $"{positionKey}|{safeAchievedDepth}/{safeTargetDepth}:{displayFlipped}";
        if (string.Equals(_lastCoachLoadingSignature, loadingSignature, StringComparison.Ordinal))
            return;

        _lastCoachLoadingSignature = loadingSignature;
        _lastCoachLoadingPositionKey = positionKey;
        _lastCoachLoadingDepth = safeAchievedDepth;
        _currentMoveArrows = null;
        _lastArrowSourceFEN = capturedFEN;

        var data = new CoachOverlayData
        {
            ComplexityScore = (int)Math.Round(safeAchievedDepth * 100.0 / safeTargetDepth),
            Title = "Coach Thinking",
            Detail = "Waiting for stable depth",
            Depth = safeAchievedDepth,
            TargetDepth = safeTargetDepth,
            IsLoading = true,
            ShowPanel = _coachCardEnabled
        };

        var r = _lastTrackedBox.Value;
        int renderToken = Interlocked.Increment(ref _arrowRenderToken);
        int generation = Interlocked.Increment(ref _arrowDisplayGeneration);

        _overlay.BeginInvoke(new Action(() =>
        {
            if (renderToken != Volatile.Read(ref _arrowRenderToken))
                return;

            lock (_analysisLock)
            {
                if (IsAnalysisRequestStillCurrent(capturedFEN, isBlackPerspective, analysisSessionVersion) &&
                    CanDisplayArrowsForCurrentState())
                {
                    _showingMoves = true;
                    _overlay.ShowCoachOverlay(new Rectangle(r.X, r.Y, r.Width, r.Height), data, generation, 60000);
                    RefreshDebugView($"Coach thinking d{safeAchievedDepth}/{safeTargetDepth}");
                }
            }
        }));
    }

    private static List<MoveArrow> BuildArrowsForFen(
        string fen,
        IEnumerable<MoveVariation> variations,
        char expectedMovingSide,
        bool displayFlipped,
        int maxArrowCount)
    {
        var arrows = new List<MoveArrow>();
        int strength = 1;

        foreach (var variation in variations.Take(Math.Min(maxArrowCount, 5)))
        {
            if (!variation.Moves.Any())
                continue;

            var move = variation.Moves.First();
            var (originalFromFile, originalFromRank, originalToFile, originalToRank, promotionPiece) = UCIEngine.ParseMove(move);
            int fromFile = originalFromFile;
            int fromRank = originalFromRank;
            int toFile = originalToFile;
            int toRank = originalToRank;

            if (!TryResolveLegalAnalysisBoardArrowMove(
                    fen,
                    expectedMovingSide,
                    originalFromFile,
                    originalFromRank,
                    originalToFile,
                    originalToRank,
                    out fromFile,
                    out fromRank,
                    out toFile,
                    out toRank))
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Skipping illegal rendered test-board move candidate: {move}");
                continue;
            }

            arrows.Add(new MoveArrow
            {
                FromFile = fromFile,
                FromRank = fromRank,
                ToFile = toFile,
                ToRank = toRank,
                Strength = strength,
                IsFlipped = displayFlipped,
                PromotionPiece = promotionPiece,
                MovingSide = expectedMovingSide,
                Depth = variation.Depth
            });
            strength++;
        }

        return arrows;
    }

    private static int GetNextExternalInfiniteTargetDepth(int displayedDepth, int quickDepth)
    {
        int baseline = Math.Max(displayedDepth, quickDepth);
        if (baseline <= 0)
            return Math.Max(1, quickDepth);

        int next = baseline + ExternalInfiniteDepthStep;
        return Math.Clamp(next, Math.Max(1, quickDepth), ExternalInfiniteMaxDepth);
    }

    private static async Task EnsureLiveAnalysisMultiPvAsync(UCIEngine engine, int desiredMultiPv)
    {
        // MaxEnginePvLines, NOT ClampLines/MaxLines: the Bullet profile asks
        // for more lines than the display cap to feed the PV cache; clamping
        // to the display cap here would silently re-assert 6 and undo it.
        desiredMultiPv = Math.Clamp(desiredMultiPv, 1, BuildLimits.MaxEnginePvLines);
        if (desiredMultiPv <= 0)
            desiredMultiPv = 1;

        if (ReferenceEquals(_lastAssertedLiveMultiPvEngine, engine) &&
            _lastAssertedLiveMultiPv == desiredMultiPv)
        {
            return;
        }

        await engine.SendCommandAsync($"setoption name MultiPV value {desiredMultiPv}");
        if (desiredMultiPv > 1)
        {
            bool ready = await engine.EnsureReadyAsync(900);
            if (!ready)
                LogDiag("ENGINE", $"MultiPV {desiredMultiPv} ready check timed out before analysis");
        }

        _lastAssertedLiveMultiPvEngine = engine;
        _lastAssertedLiveMultiPv = desiredMultiPv;
        Log($"[{DateTime.Now:HH:mm:ss}] Analysis MultiPV asserted: {desiredMultiPv}");
    }

    private static int GetPrefetchRequestedDepth()
    {
        var engine = _stockfish;
        if (engine == null)
            return 12;
        return engine.InfiniteAnalysis ? 12 : Math.Clamp(engine.MaxDepth, 1, 30);
    }


    private static string? PredictNextAnalysisFen(string fenForAnalysis, string uciMove)
    {
        try
        {
            var board = ChessBoard.LoadFromFen(fenForAnalysis, AutoEndgameRules.All);
            Move? matching = board.Moves(false, true)
                .FirstOrDefault(m => string.Equals(ToUciMove(m), uciMove, StringComparison.OrdinalIgnoreCase));
            if (matching == null)
                return null;
            board.Move(matching);
            return board.ToFen();
        }
        catch
        {
            return null;
        }
    }

    // Speculative prefetch + the PV cache are OFF for Free Edition. The server's
    // per-move Free counter counts DISTINCT board placements, so prefetched
    // predictions over-counted the budget (and cache hits under-counted it),
    // making the countdown tick per half-move. With both off, every actual
    // position hits the engine exactly once and the countdown ticks cleanly per
    // full move. Licensed sessions keep prefetch for responsiveness.
    private static bool SpeculativePrefetchActive => _speculativePrefetchEnabled && !BuildLimits.IsFreeEdition;

    private static void MaybeFireSpeculativePrefetch(string fenForAnalysis, IReadOnlyList<MoveVariation> variations, int requestedDepth)
    {
        if (!SpeculativePrefetchActive || variations == null || variations.Count == 0)
            return;

        var engine = _stockfish;
        if (engine == null || !engine.IsRemotePrefetchAvailable)
            return;

        // One-time visibility into why prefetch may be idle, so a trace with no
        // PREFETCH_HIT lines is self-explanatory.
        if (Interlocked.Exchange(ref _prefetchConfigLogged, 1) == 0)
            ArrowTimeline.Log("PREFETCH_CONFIG", extra: $"enabled={_speculativePrefetchEnabled} available={engine.IsRemotePrefetchAvailable} slots={engine.PrefetchSlotCount}");

        int depth = requestedDepth <= 0 ? 12 : Math.Clamp(requestedDepth, 1, 30);
        int slot = 0;
        int candidates = Math.Min(SpeculativePrefetchTopMoves, variations.Count);

        for (int i = 0; i < candidates; i++)
        {
            string? move = variations[i].Moves.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(move))
                continue;

            string? predicted = PredictNextAnalysisFen(fenForAnalysis, move);
            if (predicted == null)
                continue;

            string key = SpeculativePrefetchKeyFromFen(predicted);
            if (key.Length == 0)
                continue;

            // Already cached fresh and deep enough - skip.
            if (_speculativePrefetchCache.TryGetValue(key, out var existing) &&
                (DateTime.UtcNow - existing.StoredUtc).TotalMilliseconds < SpeculativePrefetchTtlMs &&
                existing.Depth >= depth)
            {
                continue;
            }

            // Per-key dedup: don't fire a prediction that's already in flight
            // (the same top moves recur on every stream update for a position).
            if (!_speculativePrefetchInFlightKeys.TryAdd(key, 1))
                continue;

            int firedSlot = slot++;
            string predictedFen = predicted;
            string firedKey = key;
            int rank = i;
            ArrowTimeline.Log("PREFETCH_FIRED", fen: predictedFen, depth: depth, extra: $"key={firedKey} rank={rank}");

            Task.Run(async () =>
            {
                try
                {
                    int thinkMs = Math.Max(120, depth * 18);
                    BestMoveResult result = await engine.PrefetchAnalyzeAsync(predictedFen, depth, thinkMs, firedSlot, CancellationToken.None).ConfigureAwait(false);
                    if (result.Success && result.Variations.Any())
                    {
                        result.AnalysisFen = predictedFen;
                        _speculativePrefetchCache[firedKey] = new SpeculativePrefetchEntry
                        {
                            Result = result,
                            Depth = GetBestResultDepth(result),
                            StoredUtc = DateTime.UtcNow,
                        };
                        TrimSpeculativePrefetchCache();
                        ArrowTimeline.Log("PREFETCH_STORED", fen: predictedFen, depth: GetBestResultDepth(result), extra: $"key={firedKey} rank={rank}");
                    }
                    else
                    {
                        ArrowTimeline.Log("PREFETCH_EMPTY", fen: predictedFen, extra: result.Error ?? "no variations");
                    }
                }
                catch (Exception ex)
                {
                    ArrowTimeline.Log("PREFETCH_ERROR", extra: $"{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _speculativePrefetchInFlightKeys.TryRemove(firedKey, out _);
                }
            });
        }
    }

    private static void PopulateAnalysisPvCache(string fenForAnalysis, IReadOnlyList<MoveVariation> variations, int achievedDepth)
    {
        if (!SpeculativePrefetchActive || variations == null || variations.Count == 0 || string.IsNullOrWhiteSpace(fenForAnalysis))
            return;

        DateTime now = DateTime.UtcNow;
        int lines = Math.Min(variations.Count, PvCacheTopLines);
        int filled = 0;

        for (int i = 0; i < lines; i++)
        {
            var pv = variations[i].Moves;
            if (pv == null || pv.Count < 2)
                continue;

            string fen = fenForAnalysis;
            int maxPlies = Math.Min(PvCacheMaxPlies, pv.Count - 1);
            for (int ply = 0; ply < maxPlies; ply++)
            {
                string? nextFen = PredictNextAnalysisFen(fen, pv[ply]);
                if (nextFen == null)
                    break;

                // The continuation from the position AFTER pv[ply] is pv[ply+1..],
                // whose first move is the best reply there.
                var remaining = new List<string>(pv.Count - ply - 1);
                for (int k = ply + 1; k < pv.Count; k++)
                    remaining.Add(pv[k]);
                if (remaining.Count == 0)
                    break;

                string key = SpeculativePrefetchKeyFromFen(nextFen);
                int entryDepth = Math.Max(1, achievedDepth - (ply + 1));
                if (key.Length > 0 &&
                    (!_speculativePrefetchCache.TryGetValue(key, out var existing) ||
                     existing.Depth < entryDepth ||
                     (now - existing.StoredUtc).TotalMilliseconds > SpeculativePrefetchTtlMs))
                {
                    var result = new BestMoveResult
                    {
                        Success = true,
                        BestMove = remaining[0],
                        Variations = new List<MoveVariation>
                        {
                            new MoveVariation { Rank = 1, Depth = entryDepth, Moves = new List<string>(remaining) }
                        },
                        AnalysisDepth = entryDepth,
                        AnalysisFen = nextFen,
                    };
                    _speculativePrefetchCache[key] = new SpeculativePrefetchEntry
                    {
                        Result = result,
                        Depth = entryDepth,
                        StoredUtc = now,
                    };
                    filled++;
                }

                fen = nextFen;
            }
        }

        if (filled > 0)
        {
            TrimSpeculativePrefetchCache();
            ArrowTimeline.Log("PV_CACHE_FILL", fen: fenForAnalysis, depth: achievedDepth, count: filled);
        }
    }

    private static void TrimSpeculativePrefetchCache()
    {
        if (_speculativePrefetchCache.Count <= SpeculativePrefetchCacheMaxEntries)
            return;
        // Drop the oldest entries; the cache is tiny so this is cheap.
        foreach (var kvp in _speculativePrefetchCache
            .OrderBy(e => e.Value.StoredUtc)
            .Take(_speculativePrefetchCache.Count - SpeculativePrefetchCacheMaxEntries)
            .ToList())
        {
            _speculativePrefetchCache.TryRemove(kvp.Key, out _);
        }
    }

    private static bool TryServeSpeculativePrefetch(
        string capturedFEN,
        string fenForAnalysis,
        bool isBlackPerspective,
        bool displayFlipped,
        bool analysisWasRotated,
        int requestedDepth,
        int analysisSessionVersion,
        out bool wireStillNeeded)
    {
        wireStillNeeded = true;
        if (!SpeculativePrefetchActive)
            return false;

        // The cache is engine-agnostic: remote prefetch AND local PV entries
        // both serve from here. (Previously gated to remote-only.)
        var engine = _stockfish;
        if (engine == null)
            return false;

        string key = SpeculativePrefetchKeyFromFen(fenForAnalysis);
        if (key.Length == 0)
            return false;

        if (!_speculativePrefetchCache.TryGetValue(key, out var entry))
        {
            ArrowTimeline.Log("PREFETCH_MISS", fen: capturedFEN, extra: $"key={key}");
            // Dedicated Bullet-profile hit-rate telemetry: the profile's whole
            // value is cache coverage, so make its hits/misses greppable.
            if (_bulletProfileEnabled)
                ArrowTimeline.Log("BULLET", fen: capturedFEN, extra: "prefetch-cache MISS");
            return false;
        }

        if ((DateTime.UtcNow - entry.StoredUtc).TotalMilliseconds > SpeculativePrefetchTtlMs)
        {
            _speculativePrefetchCache.TryRemove(key, out _);
            ArrowTimeline.Log("PREFETCH_MISS", fen: capturedFEN, extra: "stale");
            if (_bulletProfileEnabled)
                ArrowTimeline.Log("BULLET", fen: capturedFEN, extra: "prefetch-cache MISS (stale)");
            return false;
        }

        // Stamp the cached result onto THIS request's exact FEN so the apply
        // guard accepts it. board+side already match (same key), so the moves
        // are valid for the live position.
        var served = new BestMoveResult
        {
            Success = entry.Result.Success,
            BestMove = entry.Result.BestMove,
            PonderMove = entry.Result.PonderMove,
            Variations = entry.Result.Variations,
            AnalysisDepth = entry.Result.AnalysisDepth,
            ThinkTime = entry.Result.ThinkTime,
            AnalysisFen = fenForAnalysis,
        };

        bool applied = ApplyAnalysisResult(
            served,
            capturedFEN,
            fenForAnalysis,
            isBlackPerspective,
            displayFlipped,
            analysisWasRotated,
            "prefetch",
            DateTime.UtcNow,
            analysisSessionVersion);

        if (!applied)
            return false;

        ArrowTimeline.Log("PREFETCH_HIT", fen: capturedFEN, depth: entry.Depth, extra: $"key={key}");
        if (_bulletProfileEnabled)
            ArrowTimeline.Log("BULLET", fen: capturedFEN, depth: entry.Depth, extra: "prefetch-cache HIT");
        // Skip the wire entirely when the cached answer is already at least as
        // deep as requested; otherwise the arrows are showing now and the wire
        // runs only to refine.
        wireStillNeeded = entry.Depth < requestedDepth;
        return true;
    }

    private static void AnalyzePosition(bool isBlackPerspective, int analysisRunId, CancellationTokenSource analysisCancellation)
    {
        if (!_isTracking || _stockfish == null || string.IsNullOrEmpty(_currentFEN))
        {
            _analysisInProgress = false;
            analysisCancellation.Dispose();
            return;
        }

        string perspectiveStr = isBlackPerspective ? "BLACK" : "WHITE";
        var engine = _stockfish;
        CancellationToken analysisToken = analysisCancellation.Token;

        Task.Run(async () =>
        {
            Action<BestMoveResult>? streamHandler = null;
            try
            {
                if (analysisToken.IsCancellationRequested)
                {
                    if (analysisRunId == Volatile.Read(ref _analysisRunId))
                        _analysisInProgress = false;
                    LogDiag("ENGINE", "analysis canceled before start");
                    return;
                }

                if (IsExternalBoardOutputSuspended())
                {
                    if (analysisRunId == Volatile.Read(ref _analysisRunId))
                        _analysisInProgress = false;
                    LogDiag("ENGINE", "analysis canceled before start while external board output is suspended");
                    return;
                }

                Log($"[{DateTime.Now:HH:mm:ss}] Starting analysis for {perspectiveStr}");
                string capturedFEN = _currentFEN;
                int analysisSessionVersion = GetAnalysisSessionVersion();

                bool useAuthoritativeAnalysisBoardTurn = ShouldUseAuthoritativeAnalysisBoardTurn(capturedFEN);
                bool bypassWaiting = ShouldBypassWaitingForExternalBothMode(capturedFEN);

                if (!bypassWaiting &&
                    !useAuthoritativeAnalysisBoardTurn &&
                    isBlackPerspective &&
                    ShouldWaitForOpponentAtAnalysisStart())
                {
                    _waitingForOpponentMove = true;
                    _currentMoveArrows = null;
                    _lastArrowSourceFEN = "";
                    RefreshDebugView("Waiting for White's first move");
                    ClearActiveArrows("waiting for White's first move");

                    Log($"[{DateTime.Now:HH:mm:ss}] Waiting for White's first move");
                    _analysisInProgress = false;
                    return;
                }

                if (!bypassWaiting && _waitingForOpponentMove)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Waiting for opponent's move");
                    RefreshDebugView("Waiting for opponent's move");
                    ArrowTimeline.Log("ANALYZE_SKIPPED", fen: capturedFEN, reason: "waiting for opponent");
                    ClearActiveArrows("analysis gated - waiting for opponent");
                    _analysisInProgress = false;
                    return;
                }

                if (!IsActiveAnalysisBoardFen(capturedFEN) && IsExternalDetectionStalled())
                {
                    // The board has visibly moved on from capturedFEN but no
                    // new position has confirmed; analyzing the stale FEN
                    // would only produce results the apply path discards.
                    //
                    // Escalation: a brief disagreement is normal (a move mid-
                    // confirmation, a lifted piece). But when the tracked window
                    // has been physically STILL for a beat yet the detector still
                    // can't confirm anything for far longer than a real
                    // confirmation takes, the board crop itself is broken - a hard
                    // window resize / minimize-restore left it mis-sized so every
                    // frame decodes as garbage, or a garbled board got confirmed
                    // mid-resize and detection now flip-flops around it. Waiting
                    // freezes the arrows; force a full board re-acquire, which
                    // drops the stale crop and re-detects the board from scratch
                    // at its real new size. Gating on window-stillness means we
                    // re-detect only a settled board (never a blurred mid-drag
                    // frame) and never thrash while the user is still resizing;
                    // gating on _lastConfirmedFenAtUtc (not a continuous-stall
                    // timer) survives the flip-flop a wrong confirm causes.
                    DateTime nowUtc = DateTime.UtcNow;
                    double windowStillMs = _windowStableSinceUtc == DateTime.MinValue
                        ? double.MaxValue
                        : (nowUtc - _windowStableSinceUtc).TotalMilliseconds;
                    double sinceLastConfirmMs = _lastConfirmedFenAtUtc == DateTime.MinValue
                        ? double.MaxValue
                        : (nowUtc - _lastConfirmedFenAtUtc).TotalMilliseconds;
                    bool reacquireOnCooldown = _lastStallReacquireUtc != DateTime.MinValue &&
                        (nowUtc - _lastStallReacquireUtc).TotalMilliseconds < StallReacquireCooldownMs;
                    bool brokenCropRecoverable =
                        _trackedHwnd != IntPtr.Zero &&
                        WindowTracker.IsTrackable(_trackedHwnd) &&
                        windowStillMs >= WindowStableForReacquireMs &&
                        sinceLastConfirmMs >= ExternalDetectionStallReacquireMs &&
                        !reacquireOnCooldown;
                    if (brokenCropRecoverable)
                    {
                        _lastStallReacquireUtc = nowUtc;
                        ArrowTimeline.Log("VISION_STALL_REACQUIRE", fen: capturedFEN, ms: sinceLastConfirmMs);
                        Log($"[{DateTime.Now:HH:mm:ss}] Detection stalled {sinceLastConfirmMs / 1000.0:F1}s with the window still - forcing board re-acquire");
                        HandleBoardContentLostInHealthyWindow();
                        _analysisInProgress = false;
                        return;
                    }

                    ArrowTimeline.Log("ANALYZE_SKIPPED", fen: capturedFEN, reason: "detection disagrees with confirmed position");
                    Log($"[{DateTime.Now:HH:mm:ss}] Detection disagrees with confirmed position - skipping analysis");
                    _analysisInProgress = false;
                    return;
                }

                ArrowTimeline.Log("ANALYZE_START", fen: capturedFEN, extra: $"session={analysisSessionVersion}");

                string fenForAnalysis = capturedFEN;
                bool effectiveBoardIsFlipped = GetEffectiveBoardFlipped(capturedFEN);
                bool needsRotation = ShouldRotateFenForAnalysis(capturedFEN, isBlackPerspective);

                if (isBlackPerspective)
                {
                    fenForAnalysis = needsRotation ? Rotate180(capturedFEN) : capturedFEN;
                    Log($"[{DateTime.Now:HH:mm:ss}] Playing as BLACK - rotation: {needsRotation}");
                }
                else
                {
                    fenForAnalysis = needsRotation ? Rotate180(capturedFEN) : capturedFEN;
                    Log($"[{DateTime.Now:HH:mm:ss}] Playing as WHITE - rotation: {needsRotation}");
                }

                // Fix the FEN and validate castling rights
                var parts = fenForAnalysis.Split(' ');
                if (parts.Length >= 2)
                {
                    if (!useAuthoritativeAnalysisBoardTurn)
                    {
                        parts[1] = isBlackPerspective ? "b" : "w";
                    }

                    if (parts.Length >= 3)
                    {
                        string boardPosition = parts[0];
                        string sourceCastling = parts[2] == "-"
                            ? InferCastlingRightsFromBoard(boardPosition)
                            : parts[2];
                        string validCastling = ValidateCastlingRights(boardPosition, sourceCastling);

                        if (validCastling != parts[2])
                        {
                            Log($"[{DateTime.Now:HH:mm:ss}] Fixed invalid castling: {parts[2]} -> {validCastling}");
                            parts[2] = validCastling;
                        }
                    }

                    fenForAnalysis = string.Join(" ", parts);
                }

                Log($"[{DateTime.Now:HH:mm:ss}] Analyzing: {fenForAnalysis}");

                // Stash the exact FEN sent to the engine so the Debug HUD can
                // show it. Critical for diagnosing arrow-direction bugs: if
                // the rendered board doesn't match this string, we know the
                // detector disagrees with what the engine is seeing.
                _lastFenSentToEngine = fenForAnalysis;

                bool infiniteAnalysis = engine.InfiniteAnalysis;
                bool progressiveExternalInfinite = infiniteAnalysis && !IsActiveAnalysisBoardFen(capturedFEN);
                int requestedDepth = infiniteAnalysis ? ExternalInfiniteMaxDepth : Math.Clamp(engine.MaxDepth, 0, 30);
                int quickDepth = requestedDepth <= 0
                    ? 0
                    : Math.Min(_quickArrowDepth, requestedDepth);
                int quickThinkTimeMs = requestedDepth <= 0
                    ? 18
                    : requestedDepth <= 4
                        ? 24
                        : _quickArrowThinkTimeMs;

                int displayedDepthBeforeAnalysis = IsSameArrowSourcePosition(capturedFEN)
                    ? GetStableDisplayedArrowDepth(capturedFEN)
                    : 0;
                bool refinementOnly =
                    displayedDepthBeforeAnalysis >= quickDepth &&
                    (infiniteAnalysis || displayedDepthBeforeAnalysis < requestedDepth) &&
                    _currentMoveArrows != null &&
                    _currentMoveArrows.Any();

                // Speculative prefetch serve: if the reply to this exact
                // position was precomputed while it was the opponent's turn,
                // paint it now (no wire round-trip). When the cached depth is
                // already at/above the requested depth, skip the wire request
                // entirely - the common fast path. Otherwise the arrows are up
                // and the normal request continues only to refine. The finally
                // block below restores _analysisInProgress on the skip path.
                // Cache serve works for BOTH engines now: remote prefetch
                // entries AND local PV-cache entries live in the same cache.
                // (Was remote-only.) On a hit the arrows paint instantly; if the
                // cached line is at least as deep as requested we skip the
                // engine call entirely, otherwise it paints now and the engine
                // refines (e.g. a 1-line PV preview -> full multi-line analysis).
                if (!infiniteAnalysis &&
                    !refinementOnly &&
                    !IsActiveAnalysisBoardFen(capturedFEN) &&
                    TryServeSpeculativePrefetch(
                        capturedFEN, fenForAnalysis, isBlackPerspective,
                        effectiveBoardIsFlipped, needsRotation, requestedDepth,
                        analysisSessionVersion, out bool wireStillNeeded) &&
                    !wireStillNeeded)
                {
                    Log($"[{DateTime.Now:HH:mm:ss}] Served arrows from cache; skipping engine call");
                    return;
                }

                if (infiniteAnalysis && !refinementOnly)
                {
                    try
                    {
                        await engine.AbortCurrentAnalysisAsync();
                        await Task.Delay(30, analysisToken);
                    }
                    catch (Exception ex)
                    {
                        LogDiag("ENGINE", $"infinite pre-option reset failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                int desiredMultiPv = GetLiveAnalysisMultiPvCount();
                if (!(infiniteAnalysis && refinementOnly))
                {
                    await EnsureLiveAnalysisMultiPvAsync(engine, desiredMultiPv);
                }

                bool useSingleRemoteStreamingSearch =
                    engine.IsRemoteEngineActive &&
                    !refinementOnly &&
                    requestedDepth > quickDepth &&
                    quickDepth > 0;

                bool allowStreamedExternalUpdates =
                    useSingleRemoteStreamingSearch ||
                    infiniteAnalysis ||
                    IsActiveAnalysisBoardFen(capturedFEN) ||
                    requestedDepth > 18;

                if (allowStreamedExternalUpdates)
                {
                    streamHandler = update =>
                    {
                        ApplyAnalysisResult(
                            update,
                            capturedFEN,
                            fenForAnalysis,
                            isBlackPerspective,
                            effectiveBoardIsFlipped,
                            needsRotation,
                            "stream",
                            DateTime.UtcNow,
                            analysisSessionVersion);
                    };
                    engine.AnalysisUpdated += streamHandler;
                }

                BestMoveResult quickResult;
                bool quickApplied;
                int quickAchievedDepth;

                if (useSingleRemoteStreamingSearch)
                {
                    quickResult = new BestMoveResult { Success = false, Error = "Skipped quick pass for remote streaming search" };
                    quickApplied = false;
                    quickAchievedDepth = Math.Max(displayedDepthBeforeAnalysis, quickDepth);
                    Log($"[{DateTime.Now:HH:mm:ss}] Skipping separate quick remote pass; waiting for streamed depth updates");
                }
                else if (infiniteAnalysis && refinementOnly)
                {
                    quickResult = new BestMoveResult { Success = false, Error = "Skipped quick pass for infinite same-position refinement" };
                    quickApplied = true;
                    quickAchievedDepth = displayedDepthBeforeAnalysis;
                    Log($"[{DateTime.Now:HH:mm:ss}] Continuing infinite arrows from depth {displayedDepthBeforeAnalysis}");
                }
                else if (infiniteAnalysis)
                {
                    DateTime quickAnalysisStartedUtc = DateTime.UtcNow;
                    quickResult = await engine.GetBestMoveAsync(
                        fenForAnalysis,
                        quickThinkTimeMs,
                        quickDepth,
                        analysisToken);

                    quickApplied = ApplyAnalysisResult(
                        quickResult,
                        capturedFEN,
                        fenForAnalysis,
                        isBlackPerspective,
                        effectiveBoardIsFlipped,
                        needsRotation,
                        "quick",
                        quickAnalysisStartedUtc,
                        analysisSessionVersion);

                    quickAchievedDepth = GetBestResultDepth(quickResult);
                }
                else if (refinementOnly)
                {
                    quickResult = new BestMoveResult { Success = false, Error = "Skipped quick pass for same-position refinement" };
                    quickApplied = true;
                    quickAchievedDepth = displayedDepthBeforeAnalysis;
                    Log($"[{DateTime.Now:HH:mm:ss}] Refining existing arrows from depth {displayedDepthBeforeAnalysis} toward {requestedDepth}");
                }
                else
                {
                    DateTime quickAnalysisStartedUtc = DateTime.UtcNow;
                    quickResult = await engine.GetBestMoveAsync(
                        fenForAnalysis,
                        quickThinkTimeMs,
                        quickDepth,
                        analysisToken);

                    quickApplied = ApplyAnalysisResult(
                        quickResult,
                        capturedFEN,
                        fenForAnalysis,
                        isBlackPerspective,
                        effectiveBoardIsFlipped,
                        needsRotation,
                        "quick",
                        quickAnalysisStartedUtc,
                        analysisSessionVersion);

                    quickAchievedDepth = GetBestResultDepth(quickResult);
                }
                bool refinedApplied = false;
                BestMoveResult? result = null;
                bool refinementNeeded = infiniteAnalysis || (requestedDepth > 0 && quickAchievedDepth < requestedDepth);
                if (refinementNeeded)
                {
                    DateTime refinedAnalysisStartedUtc = DateTime.UtcNow;
                    if (progressiveExternalInfinite)
                    {
                        int targetDepth = _coachModeEnabled
                            ? GetExternalCoachTargetDepth()
                            : GetNextExternalInfiniteTargetDepth(quickAchievedDepth, quickDepth);
                        int thinkMs = _coachModeEnabled
                            ? Math.Max(ExternalInfiniteStepThinkTimeMs, targetDepth * 90)
                            : Math.Max(ExternalInfiniteStepThinkTimeMs, targetDepth * 18);
                        result = await engine.GetBestMoveAsync(fenForAnalysis, thinkMs, targetDepth, analysisToken);
                    }
                    else
                    {
                        if (infiniteAnalysis)
                        {
                            result = await engine.GetBestMoveIterativeInfinite(fenForAnalysis, analysisToken);
                        }
                        else if (!IsActiveAnalysisBoardFen(capturedFEN) && requestedDepth is > 0 and <= 18)
                        {
                            int refinedThinkTimeMs = Math.Max(_quickArrowThinkTimeMs, requestedDepth * 18);
                            result = await engine.GetBestMoveAsync(
                                fenForAnalysis,
                                refinedThinkTimeMs,
                                requestedDepth,
                                analysisToken,
                                fixedDepthOnly: true);
                        }
                        else
                        {
                            result = await engine.GetBestMoveIterativeAsync(fenForAnalysis, analysisToken);
                        }
                    }

                    refinedApplied = ApplyAnalysisResult(
                        result,
                        capturedFEN,
                        fenForAnalysis,
                        isBlackPerspective,
                        effectiveBoardIsFlipped,
                        needsRotation,
                        "refined",
                        refinedAnalysisStartedUtc,
                        analysisSessionVersion);
                }

                if (!(quickApplied || refinedApplied))
                {
                    string failureMessage = GetEngineFailureMessage(engine, result?.Error ?? quickResult.Error ?? "No variations");
                    Log($"[{DateTime.Now:HH:mm:ss}] Analysis failed: {failureMessage}");
                    if (IsPrivateEngineLicenseFailure(failureMessage))
                    {
                        RevertLiveAnalysisAfterPrivateEngineLicenseFailure(failureMessage);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogDiag("ENGINE", $"analysis worker canceled (run={analysisRunId})");
            }
            catch (Exception ex)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] Analysis error: {ex.Message}");
            }
            finally
            {
                if (streamHandler != null)
                {
                    engine.AnalysisUpdated -= streamHandler;
                }
                Interlocked.CompareExchange(ref _analysisCancellation, null, analysisCancellation);
                analysisCancellation.Dispose();
                if (analysisRunId == Volatile.Read(ref _analysisRunId))
                    _analysisInProgress = false;
            }
        });
    }

    private static void ToggleContinuousAnalysis(bool isBlackPerspective)
    {
        lock (_analysisLock)
        {
            _analysisTargetIsAnalysisBoard = false;
            DetachAnalysisBoardFromExternalTracking();

            if (_continuousAnalysisEnabled && _analysisIsBlackPerspective == isBlackPerspective)
            {
                // Toggle OFF
                BumpAnalysisSessionVersion();
                _continuousAnalysisEnabled = false;
                _analysisTimer?.Dispose();
                _analysisTimer = null;
                _waitingForOpponentMove = false;
                _analysisInProgress = false;
                _lastArrowSourceFEN = "";
                ResetAnalysisSchedulingState();
                ResetConfirmedStateTimeline();

                // Immediately hide arrows
                ClearActiveArrows();

                // Sync with toolbar
                if (_settingsToolbar != null)
                {
                    _settingsToolbar.SyncAnalysisState("OFF");
                }

                Log($"[{DateTime.Now:HH:mm:ss}] Analysis DISABLED");
                RefreshDebugView("Analysis disabled");
            }
            else
            {
                // If switching from one color to another
                if (_continuousAnalysisEnabled)
                {
                    BumpAnalysisSessionVersion();
                    _analysisInProgress = false;
                    _analysisTimer?.Dispose();
                    _analysisTimer = null;
                    ClearActiveArrows();
                    _lastArrowSourceFEN = "";
                    ResetAnalysisSchedulingState();
                    ResetConfirmedStateTimeline();
                }

                // Toggle ON with new perspective
                BumpAnalysisSessionVersion();
                _continuousAnalysisEnabled = true;
                _analysisIsBlackPerspective = isBlackPerspective;
                _userColor = isBlackPerspective ? 'b' : 'w';
                _waitingForOpponentMove = ShouldWaitForOpponentWhenEnablingAnalysis(isBlackPerspective);
                _currentMoveArrows = null;
                _lastAnalysisVariations = null;
                _lastArrowSourceFEN = "";
                ResetAnalysisSchedulingState();
                ResetPendingFenCandidate();
                ResetConfirmedStateTimeline();

                if (_waitingForOpponentMove)
                {
                    ClearExternalArrows();
                }
                else
                {
                    _showingMoves = false;
                }

                _analysisTimer?.Dispose();

                _analysisTimer = new System.Threading.Timer(
                    _ => {
                        if (_continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN))
                        {
                            TryQueueAnalysis(_analysisIsBlackPerspective);
                        }
                    },
                    null,
                    TimeSpan.FromMilliseconds(500),
                    TimeSpan.FromMilliseconds(250)
                );

                if (_settingsToolbar != null)
                {
                    _settingsToolbar.SyncAnalysisState(_analysisBothEnabled ? "BOTH" : (isBlackPerspective ? "BLACK" : "WHITE"));
                }

                string perspective = _analysisBothEnabled ? "W+B" : (isBlackPerspective ? "BLACK" : "WHITE");
                Log($"[{DateTime.Now:HH:mm:ss}] Analysis ENABLED ({perspective})");
                RefreshDebugView($"Analysis enabled ({perspective})");
                _analysisBoardController!.SuspendAnalysisBoardAnalysisForLiveBoard();
                _analysisBoardController!.RefreshAnalysisBoardSnapshotForExternalDetection();

                if (!_lastTrackedBox.HasValue)
                {
                    SchedulePrimeExternalBoardTracking("analysis enable");
                }

                if (!IsActiveAnalysisBoardFen(_currentFEN))
                {
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                }
            }
        }
    }

    private static void ResyncContinuousAnalysis(bool isBlackPerspective)
    {
        lock (_analysisLock)
        {
            _analysisTargetIsAnalysisBoard = false;
            DetachAnalysisBoardFromExternalTracking();

            BumpAnalysisSessionVersion();
            _continuousAnalysisEnabled = true;
            _analysisIsBlackPerspective = isBlackPerspective;
            _userColor = isBlackPerspective ? 'b' : 'w';
            _analysisInProgress = false;
            _waitingForOpponentMove = ShouldWaitForOpponentWhenEnablingAnalysis(isBlackPerspective);
            _currentMoveArrows = null;
            _lastAnalysisVariations = null;
            _lastArrowSourceFEN = "";

            ResetAnalysisSchedulingState();
            ResetPendingFenCandidate();
            ResetConfirmedStateTimeline();

            if (_waitingForOpponentMove)
            {
                ClearExternalArrows();
            }
            else
            {
                _showingMoves = false;
            }

            _analysisTimer?.Dispose();
            _analysisTimer = new System.Threading.Timer(
                _ =>
                {
                    if (_continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN))
                    {
                        TryQueueAnalysis(_analysisIsBlackPerspective);
                    }
                },
                null,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(250)
            );

            if (_settingsToolbar != null)
            {
                _settingsToolbar.SyncAnalysisState(_analysisBothEnabled ? "BOTH" : (isBlackPerspective ? "BLACK" : "WHITE"));
            }

            string perspective = isBlackPerspective ? "BLACK" : "WHITE";
            Log($"[{DateTime.Now:HH:mm:ss}] Analysis RESYNC ({perspective})");
            RefreshDebugView($"Analysis resync ({perspective})");
            _analysisBoardController!.SuspendAnalysisBoardAnalysisForLiveBoard();
            _analysisBoardController!.RefreshAnalysisBoardSnapshotForExternalDetection();

            if (!_lastTrackedBox.HasValue)
            {
                SchedulePrimeExternalBoardTracking("analysis resync");
            }

            if (!IsActiveAnalysisBoardFen(_currentFEN))
            {
                TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
            }
        }
    }

}
