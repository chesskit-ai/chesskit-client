using ChessKit;
using Chess;
using OpenCvSharp;
using System.Diagnostics;
using static ChessKit.FenText;
using static ChessKit.AnalysisResultUtil;

// Arrow refresh, last-move highlight, optimistic move inference, snapshot/orientation.
partial class Program
{
    private static void RefreshDisplayedArrows()
    {
        // Defense in depth: while the lost-tracking latch is set, refuse
        // to redraw arrows from any cached source. The cleared cache in
        // HideOverlaysAfterWindowGone is the primary guard, but this
        // catches any path that might re-populate _currentMoveArrows
        // through some other route (e.g., a delayed callback that
        // captured the arrows before the cancel).
        if (IsExternalBoardOutputSuspended())
        {
            ClearActiveArrows();
            return;
        }

        if (!_showingMoves &&
            !_currentFenIsAnalysisBoard &&
            !IsActiveAnalysisBoardFen(_currentFEN) &&
            DateTime.UtcNow < _lastExternalArrowResultReadyUtc.AddMilliseconds(ExternalArrowResultDirectRenderGraceMs))
        {
            return;
        }

        // While a swap hold is pending, the logical arrow state is already
        // cleared but the overlay is intentionally still showing the previous
        // arrows. Without this guard the _currentMoveArrows == null branch
        // below would hide them within one frame, recreating the post-move
        // blank window the hold exists to bridge.
        if (!_showingMoves &&
            !_currentFenIsAnalysisBoard &&
            !IsActiveAnalysisBoardFen(_currentFEN) &&
            DateTime.UtcNow < _externalArrowHoldUntilUtc)
        {
            return;
        }

        if (!CanDisplayArrowsForCurrentState())
        {
            ClearActiveArrows();
            return;
        }

        if (_coachModeEnabled && !_currentFenIsAnalysisBoard && !IsActiveAnalysisBoardFen(_currentFEN))
        {
            if (_overlay != null && _showingMoves && _lastTrackedBox.HasValue && HasDisplayedCoachOverlayForCurrentPosition())
            {
                var coachRect = _lastTrackedBox.Value;
                _overlay.BeginInvoke(new Action(() =>
                    _overlay.SetBoardScreenPosition(new Rectangle(coachRect.X, coachRect.Y, coachRect.Width, coachRect.Height))));
            }
            return;
        }

        if (_currentMoveArrows == null || !_currentMoveArrows.Any() || !_lastTrackedBox.HasValue)
        {
            ClearActiveArrows();
            return;
        }

        bool displayFlipped = GetEffectiveBoardFlipped(_currentFEN);
        int stableDepth = GetStableDisplayedArrowDepth(_currentFEN);
        var arrowsToShow = _currentMoveArrows
            .Take(_maxArrowCount)
            .Select(a => new MoveArrow
            {
                FromFile = a.FromFile,
                FromRank = a.FromRank,
                ToFile = a.ToFile,
                ToRank = a.ToRank,
                Strength = a.Strength,
                IsFlipped = displayFlipped,
                PromotionPiece = a.PromotionPiece,
                MovingSide = a.MovingSide,
                Depth = stableDepth > 0 ? Math.Max(a.Depth, stableDepth) : a.Depth
            })
            .ToList();
        if (!arrowsToShow.Any())
        {
            ClearActiveArrows();
            return;
        }

        if (IsActiveAnalysisBoardFen(_currentFEN) && _analysisBoardForm != null)
        {
            if (_analysisBoardForm.InvokeRequired)
            {
                _analysisBoardForm.BeginInvoke(new Action(() =>
                {
                    if (CanDisplayArrowsForCurrentState())
                    {
                        _analysisBoardForm.SetAnalysisArrows(arrowsToShow);
                        _showingMoves = true;
                    }
                    else
                    {
                        _analysisBoardForm.ClearAnalysisArrows();
                        _showingMoves = false;
                    }
                }));
            }
            else
            {
                if (CanDisplayArrowsForCurrentState())
                {
                    _analysisBoardForm.SetAnalysisArrows(arrowsToShow);
                    _showingMoves = true;
                }
                else
                {
                    _analysisBoardForm.ClearAnalysisArrows();
                    _showingMoves = false;
                }
            }

            return;
        }

        if (_overlay == null)
            return;

        var r = _lastTrackedBox.Value;
        int generation = _arrowDisplayGeneration;
        int renderToken = Volatile.Read(ref _arrowRenderToken);
        _overlay.BeginInvoke(new Action(() =>
        {
            if (renderToken != Volatile.Read(ref _arrowRenderToken))
                return;

            lock (_analysisLock)
            {
                if (CanDisplayArrowsForCurrentState())
                {
                    _showingMoves = true;
                    _overlay.ShowMoveArrows(new Rectangle(r.X, r.Y, r.Width, r.Height), arrowsToShow, generation, 60000);
                    if (!_currentFenIsAnalysisBoard && !IsActiveAnalysisBoardFen(_currentFEN))
                        RememberExternalOverlayArrowsShown(string.IsNullOrWhiteSpace(_lastArrowSourceFEN) ? _currentFEN : _lastArrowSourceFEN, arrowsToShow.Count);
                }
            }
        }));
    }

    private static bool HasDisplayedCoachOverlayForCurrentPosition()
    {
        return _coachModeEnabled &&
               _showingMoves &&
               _lastAnalysisVariations is { Count: > 0 } &&
               IsSameArrowSourcePosition(_currentFEN);
    }

    private static void UpdateConfirmedBoardSnapshot(Mat boardView)
    {
        _lastConfirmedBoardSnapshot?.Dispose();
        _lastConfirmedBoardSnapshot = boardView.Clone();

        _lastConfirmedBoardDiffSnapshot?.Dispose();
        _lastConfirmedBoardDiffSnapshot = _detector?.CreateBoardDiffSnapshot(boardView);

        _lastConfirmedBoardPixelFingerprint?.Dispose();
        _lastConfirmedBoardPixelFingerprint = _detector?.CreateBoardPixelFingerprint(boardView);
    }

    private static bool TryGetLastMoveHighlightHint(Mat boardView, out LastMoveHighlightHint hint)
    {
        hint = new LastMoveHighlightHint();

        if (boardView == null ||
            boardView.Empty() ||
            _currentFenIsAnalysisBoard)
        {
            return false;
        }

        if (!LastMoveHighlightDetector.TryDetect(boardView, out var result) ||
            !result.HasReliablePair ||
            result.Squares.Count != 2)
        {
            return false;
        }

        var squares = result.Squares
            .Select(s => ScreenDiffSquareToAlgebraic(s.File, s.RankFromTop, _externalBoardDetectedFlipped))
            .Where(s => s.Length == 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (squares.Count != 2)
            return false;

        hint = new LastMoveHighlightHint
        {
            Squares = squares,
            Confidence = result.Confidence,
            Summary = $"{string.Join(",", squares.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))} conf={result.Confidence:F2}"
        };

        return true;
    }

    private static bool TryGetLastMoveHighlightHint(
        Mat boardView,
        BoardVisionDetector.BoardDiffInfo boardDiff,
        out LastMoveHighlightHint hint)
    {
        if (boardDiff.ChangedSquares <= 0 ||
            boardDiff.ChangedSquareDetails.Count != boardDiff.ChangedSquares ||
            !TryGetLastMoveHighlightHint(boardView, out hint))
        {
            hint = new LastMoveHighlightHint();
            return false;
        }

        if (!HighlightSquaresFitChangedSquares(boardDiff, hint.Squares))
        {
            hint = new LastMoveHighlightHint();
            return false;
        }

        return true;
    }

    private static bool TryApplyStaticLastMoveHighlightTurnHint(string confirmedFen, Mat? boardView)
    {
        if (!_analysisBothEnabled ||
            boardView == null ||
            boardView.Empty() ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(confirmedFen) ||
            IsInitialBoardPosition(confirmedFen) ||
            _externalTrackedPositionCount > 1 ||
            DateTime.UtcNow < _recentMouseInteractionUntilUtc)
        {
            return false;
        }

        if (!TryGetLastMoveHighlightHint(boardView, out var hint) ||
            hint.Confidence < 0.55 ||
            !TryInferLastMoverFromStaticHighlight(confirmedFen, hint.Squares, out char lastMover, out string moveText))
        {
            return false;
        }

        char inferredSideToMove = lastMover == 'w' ? 'b' : 'w';
        bool changedSide = _inferredSideToMove != inferredSideToMove;
        if (changedSide)
        {
            LogDiag(
                "HILITE",
                $"static last-move highlight inferred {moveText} by {lastMover}; side-to-move={inferredSideToMove} ({hint.Summary})");
        }

        _inferredSideToMove = inferredSideToMove;
        if (changedSide)
        {
            CompleteStaticLastMoveHighlightInitialHold(GetBoardPosition(confirmedFen));
        }

        if (changedSide && string.Equals(_currentFEN, confirmedFen, StringComparison.Ordinal))
        {
            CancelPendingAnalysis("static last-move highlight changed side");
            // The displayed arrows are advice for the wrong side - holding
            // them through the swap would keep misleading advice visible.
            ClearDisplayedArrowsForPositionChange(allowHoldForPendingSwap: false);
            ResetAnalysisSchedulingState();
        }

        return changedSide;
    }

    private static bool TryApplyStaticLastMoveHighlightTurnHintAndQueue(string confirmedFen, Mat? boardView)
    {
        bool changedSide = TryApplyStaticLastMoveHighlightTurnHint(confirmedFen, boardView);
        if (changedSide &&
            _continuousAnalysisEnabled &&
            !IsActiveAnalysisBoardFen(confirmedFen) &&
            string.Equals(_currentFEN, confirmedFen, StringComparison.Ordinal))
        {
            bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
            TryQueueAnalysis(requestedPerspective, force: true);
        }

        return changedSide;
    }

    private static bool TryLastMoveHighlightSupportsObservedTransition(
        string oldFen,
        string newFen,
        char mover,
        Mat? boardView,
        out string summary)
    {
        summary = "";

        if (boardView == null ||
            boardView.Empty() ||
            string.IsNullOrWhiteSpace(oldFen) ||
            string.IsNullOrWhiteSpace(newFen) ||
            IsActiveAnalysisBoardFen(oldFen) ||
            IsActiveAnalysisBoardFen(newFen) ||
            !TryGetLastMoveHighlightHint(boardView, out var hint) ||
            hint.Confidence < 0.62)
        {
            return false;
        }

        string targetBoard = GetBoardPosition(newFen);
        if (string.IsNullOrWhiteSpace(targetBoard))
            return false;

        try
        {
            string startFen = FenWithSideToMove(oldFen, mover);
            var board = ChessBoard.LoadFromFen(startFen, AutoEndgameRules.All);
            foreach (var move in board.Moves(false, true))
            {
                if (!MoveEndpointSquares(move).SetEquals(hint.Squares))
                    continue;

                var trial = ChessBoard.LoadFromFen(startFen, AutoEndgameRules.All);
                string moveText = ToUciMove(move);
                var matchingMove = trial.Moves(false, true)
                    .FirstOrDefault(candidate => string.Equals(ToUciMove(candidate), moveText, StringComparison.OrdinalIgnoreCase));
                if (matchingMove == null)
                    continue;

                trial.Move(matchingMove);
                if (string.Equals(GetBoardPosition(trial.ToFen()), targetBoard, StringComparison.Ordinal))
                {
                    summary = $"{moveText} by {mover} ({hint.Summary})";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogDiag("HILITE", $"transition highlight probe skipped side={mover}: {ex.GetType().Name}: {ex.Message}");
        }

        return false;
    }

    private static bool TryStartStaticLastMoveHighlightInitialHold(string fen)
    {
        if (!ShouldHoldInitialBothAnalysisForStaticHighlight(fen))
            return false;

        string boardPosition = GetBoardPosition(fen);
        DateTime now = DateTime.UtcNow;
        if (string.Equals(_staticLastMoveHighlightHoldBoardPosition, boardPosition, StringComparison.Ordinal) &&
            now < _staticLastMoveHighlightHoldUntilUtc)
        {
            return true;
        }

        if (string.Equals(_staticLastMoveHighlightHoldBoardPosition, boardPosition, StringComparison.Ordinal) &&
            now >= _staticLastMoveHighlightHoldUntilUtc)
        {
            CompleteStaticLastMoveHighlightInitialHold(boardPosition);
            return false;
        }

        if (now >= _staticLastMoveHighlightHoldUntilUtc)
        {
            _staticLastMoveHighlightHoldBoardPosition = "";
            _staticLastMoveHighlightHoldUntilUtc = DateTime.MinValue;
        }

        if (string.Equals(_staticLastMoveHighlightCompletedBoardPosition, boardPosition, StringComparison.Ordinal))
            return false;

        _staticLastMoveHighlightHoldBoardPosition = boardPosition;
        _staticLastMoveHighlightHoldUntilUtc = now.AddMilliseconds(StaticLastMoveHighlightInitialAnalysisHoldMs);
        int generation = Interlocked.Increment(ref _staticLastMoveHighlightHoldGeneration);
        LogDiag("HILITE", $"holding initial W+B analysis for static last-move highlight ({StaticLastMoveHighlightInitialAnalysisHoldMs}ms max)");
        _ = Task.Run(() => ProbeStaticLastMoveHighlightInitialHoldAsync(fen, boardPosition, generation));
        return true;
    }

    private static bool ShouldHoldInitialBothAnalysisForStaticHighlight(string fen)
    {
        if (!_analysisBothEnabled ||
            !_continuousAnalysisEnabled ||
            _currentFenIsAnalysisBoard ||
            IsActiveAnalysisBoardFen(fen) ||
            IsInitialBoardPosition(fen) ||
            string.IsNullOrWhiteSpace(fen) ||
            _externalTrackedPositionCount > 1 ||
            !_lastTrackedBox.HasValue ||
            _trackedHwnd == IntPtr.Zero ||
            _trackingLostWaitingForReacquire)
        {
            return false;
        }

        string boardPosition = GetBoardPosition(fen);
        if (string.Equals(_staticLastMoveHighlightCompletedBoardPosition, boardPosition, StringComparison.Ordinal))
            return false;

        if (_externalTrackedPositionCount <= 3 &&
            CountChangedBoardSquares(InitialBoardPosition, boardPosition) <= InitialExternalOpeningSideMaxPlies * 4 &&
            TryInferOpeningSideToMoveByLegalPath(fen, out char openingSideToMove, out char openingLastMover, out int openingPlies))
        {
            _inferredSideToMove = openingSideToMove;
            CompleteStaticLastMoveHighlightInitialHold(boardPosition);
            LogTurnInference(
                $"initial W+B static highlight hold skipped; opening path plies={openingPlies}, last={openingLastMover}, side-to-move={_inferredSideToMove} count={_externalTrackedPositionCount} board={boardPosition}");
            return false;
        }

        return true;
    }

    private static async Task ProbeStaticLastMoveHighlightInitialHoldAsync(string fen, string boardPosition, int generation)
    {
        try
        {
            while (true)
            {
                await Task.Delay(StaticLastMoveHighlightProbeIntervalMs).ConfigureAwait(false);

                if (generation != Volatile.Read(ref _staticLastMoveHighlightHoldGeneration))
                    return;
                if (!_continuousAnalysisEnabled || !_analysisBothEnabled || _trackingLostWaitingForReacquire)
                {
                    CancelStaticLastMoveHighlightInitialHold();
                    return;
                }
                if (!string.Equals(GetBoardPosition(_currentFEN), boardPosition, StringComparison.Ordinal))
                {
                    CancelStaticLastMoveHighlightInitialHold();
                    return;
                }

                if (TryCaptureExternalBoardSnapshot(_lastTrackedBox!.Value, out var snapshot))
                {
                    using (snapshot)
                    {
                        if (TryApplyStaticLastMoveHighlightTurnHint(_currentFEN, snapshot))
                        {
                            CompleteStaticLastMoveHighlightInitialHold(boardPosition);
                            TryQueueAnalysis(GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective), force: true);
                            return;
                        }
                    }
                }

                if (DateTime.UtcNow >= _staticLastMoveHighlightHoldUntilUtc)
                {
                    CompleteStaticLastMoveHighlightInitialHold(boardPosition);
                    LogDiag("HILITE", "static last-move highlight hold expired; allowing initial W+B analysis");
                    TryQueueAnalysis(GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective), force: true);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogDiag("HILITE", $"static last-move highlight hold probe failed: {ex.GetType().Name}: {ex.Message}");
            CompleteStaticLastMoveHighlightInitialHold(boardPosition);
            TryQueueAnalysis(GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective), force: true);
        }
    }

    private static void CompleteStaticLastMoveHighlightInitialHold(string boardPosition)
    {
        if (!string.IsNullOrWhiteSpace(boardPosition))
        {
            _staticLastMoveHighlightCompletedBoardPosition = boardPosition;
        }

        _staticLastMoveHighlightHoldBoardPosition = "";
        _staticLastMoveHighlightHoldUntilUtc = DateTime.MinValue;
        Interlocked.Increment(ref _staticLastMoveHighlightHoldGeneration);
    }

    private static void CancelStaticLastMoveHighlightInitialHold()
    {
        _staticLastMoveHighlightHoldBoardPosition = "";
        _staticLastMoveHighlightCompletedBoardPosition = "";
        _staticLastMoveHighlightHoldUntilUtc = DateTime.MinValue;
        Interlocked.Increment(ref _staticLastMoveHighlightHoldGeneration);
    }

    private static bool TryInferLastMoverFromStaticHighlight(
        string fen,
        HashSet<string> highlightSquares,
        out char lastMover,
        out string moveText)
    {
        lastMover = '\0';
        moveText = "";

        if (highlightSquares.Count != 2)
            return false;

        string boardFen = GetBoardPosition(fen);
        var occupied = highlightSquares
            .Select(square => new { Square = square, Piece = GetPieceAtSquare(boardFen, square) })
            .Where(item => item.Piece != '.')
            .ToList();

        if (occupied.Count != 1)
            return false;

        string destination = occupied[0].Square;
        string source = highlightSquares.First(square => !string.Equals(square, destination, StringComparison.OrdinalIgnoreCase));
        char piece = occupied[0].Piece;
        char mover = char.IsUpper(piece) ? 'w' : 'b';

        if (!IsPlausibleLastMoveGeometry(piece, source, destination, boardFen))
            return false;

        lastMover = mover;
        moveText = $"{source}{destination}";
        return true;
    }

    private static bool IsPlausibleLastMoveGeometry(char piece, string source, string destination, string boardFen)
    {
        if (!TrySquareToCoordinates(source, out int fromFile, out int fromRank) ||
            !TrySquareToCoordinates(destination, out int toFile, out int toRank))
        {
            return false;
        }

        int dx = Math.Abs(toFile - fromFile);
        int dy = Math.Abs(toRank - fromRank);
        char lowerPiece = char.ToLowerInvariant(piece);

        return lowerPiece switch
        {
            'k' => (dx <= 1 && dy <= 1 && dx + dy > 0) || (dx == 2 && dy == 0),
            'q' => (dx == dy || dx == 0 || dy == 0) && IsClearLine(boardFen, fromFile, fromRank, toFile, toRank),
            'r' => (dx == 0 || dy == 0) && IsClearLine(boardFen, fromFile, fromRank, toFile, toRank),
            'b' => dx == dy && IsClearLine(boardFen, fromFile, fromRank, toFile, toRank),
            'n' => (dx == 1 && dy == 2) || (dx == 2 && dy == 1),
            'p' => IsPlausiblePawnLastMove(piece, fromFile, fromRank, toFile, toRank),
            _ => false
        };
    }

    private static bool IsPlausiblePawnLastMove(char piece, int fromFile, int fromRank, int toFile, int toRank)
    {
        int direction = char.IsUpper(piece) ? 1 : -1;
        int dx = Math.Abs(toFile - fromFile);
        int dy = toRank - fromRank;

        if (dy != direction)
        {
            bool fromStartingRank = char.IsUpper(piece) ? fromRank == 1 : fromRank == 6;
            if (!(dx == 0 && fromStartingRank && dy == direction * 2))
                return false;
        }

        return dx <= 1;
    }

    private static bool TrySquareToCoordinates(string square, out int file, out int rank)
    {
        file = 0;
        rank = 0;

        if (string.IsNullOrWhiteSpace(square) || square.Length != 2)
            return false;

        char fileChar = char.ToLowerInvariant(square[0]);
        char rankChar = square[1];
        if (fileChar < 'a' || fileChar > 'h' || rankChar < '1' || rankChar > '8')
            return false;

        file = fileChar - 'a';
        rank = rankChar - '1';
        return true;
    }

    private static bool IsClearLine(string boardFen, int fromFile, int fromRank, int toFile, int toRank)
    {
        int stepFile = Math.Sign(toFile - fromFile);
        int stepRank = Math.Sign(toRank - fromRank);
        int file = fromFile + stepFile;
        int rank = fromRank + stepRank;

        while (file != toFile || rank != toRank)
        {
            string square = $"{(char)('a' + file)}{rank + 1}";
            if (GetPieceAtSquare(boardFen, square) != '.')
                return false;

            file += stepFile;
            rank += stepRank;
        }

        return true;
    }

    private static bool HighlightSquaresFitChangedSquares(BoardVisionDetector.BoardDiffInfo boardDiff, HashSet<string> highlightSquares)
    {
        foreach (var changedSquares in BuildChangedSquareMappings(boardDiff))
        {
            if (highlightSquares.IsSubsetOf(changedSquares))
                return true;
        }

        return false;
    }

    private static bool TryApplyOptimisticChangedSquaresFen(BoardVisionDetector.BoardDiffInfo boardDiff, Mat boardView)
    {
        if (boardDiff.ChangedSquares < 2 || boardDiff.ChangedSquares > OptimisticMoveMaxChangedSquares)
            return false;

        DateTime now = DateTime.UtcNow;
        if (now < _suppressOptimisticFenAfterMouseUntilUtc)
        {
            if (now >= _lastMouseOptimisticFenSkipLogUtc.AddMilliseconds(_mouseOptimisticFenSkipLogIntervalMs))
            {
                _lastMouseOptimisticFenSkipLogUtc = now;
                double remainingMs = (_suppressOptimisticFenAfterMouseUntilUtc - now).TotalMilliseconds;
                LogDiag(
                    "FAST-FEN",
                    $"skipped during local mouse settle ({boardDiff.ChangedSquares} changed squares, remainingMs={remainingMs:F0})");
            }

            return false;
        }

        // Note: this path is deliberately NOT gated on
        // _recentMouseInteractionUntilUtc (700ms). The user's own moves are
        // exactly the case that needs the fast path - the mouse settle window
        // above already covers post-release repaint noise, and the optimistic
        // correction machinery rolls back a misread.
        bool rapidHoldActive = now < _rapidPostOptimisticMoveHoldUntilUtc;
        // Chained fast confirms are essential with the remote engine: a quick
        // reply that loses the fast path waits out the slow stable-confirm
        // AND a network round-trip, which the user sees as arrows lingering.
        // The legality matching below plus the optimistic-correction rollback
        // keep a misread recoverable.
        bool allowChainedFastConfirm = ShouldUseFastDetectionRecovery();
        if (rapidHoldActive && !allowChainedFastConfirm)
        {
            LogDiag("FAST-FEN", $"skipped chained optimistic move during rapid post-fast hold ({boardDiff.ChangedSquares} changed squares)");
            return false;
        }

        if (rapidHoldActive && allowChainedFastConfirm)
            LogDiag("FAST-FEN", $"bypassed rapid post-fast hold ({boardDiff.ChangedSquares} changed squares)");

        if (boardDiff.ChangedSquareDetails.Count != boardDiff.ChangedSquares)
            return false;

        if (string.IsNullOrWhiteSpace(_currentFEN) || _currentFenIsAnalysisBoard)
            return false;

        if (IsExternalBoardGeometryUnstable())
            return false;

        if (ShouldIgnoreCoachOverlayBoardDiff(boardDiff))
            return false;

        try
        {
            bool hasHighlightHint = TryGetLastMoveHighlightHint(boardView, boardDiff, out var highlightHint);
            string legalFen = ApplyInferredExternalTurnToFen(_currentFEN);
            var board = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
            var legalMoves = board.Moves(false, true);
            var matchingMovesByUci = new Dictionary<string, Move>(StringComparer.OrdinalIgnoreCase);

            foreach (var changedSquares in BuildChangedSquareMappings(boardDiff))
            {
                foreach (var candidateMove in legalMoves)
                {
                    if (!MoveDiffFitsChangedSquares(candidateMove, MoveAffectedSquares(candidateMove), changedSquares, GetBoardPosition(legalFen)))
                        continue;

                    string uci = ToUciMove(candidateMove);
                    if (!matchingMovesByUci.ContainsKey(uci))
                        matchingMovesByUci[uci] = candidateMove;
                }
            }

            if (hasHighlightHint && matchingMovesByUci.Count > 0)
            {
                var highlightMatchedMoves = matchingMovesByUci
                    .Where(kvp => MoveEndpointSquares(kvp.Value).SetEquals(highlightHint.Squares))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

                if (highlightMatchedMoves.Count == 1)
                {
                    matchingMovesByUci = highlightMatchedMoves;
                    LogDiag("HILITE", $"last-move highlight matched fast move ({highlightHint.Summary})");
                }
                else if (highlightMatchedMoves.Count == 0 &&
                         matchingMovesByUci.Count == 1 &&
                         highlightHint.Confidence >= _lastMoveHighlightConflictConfidence)
                {
                    string candidate = matchingMovesByUci.Keys.First();
                    LogDiag("FAST-FEN", $"skipped: highlight {highlightHint.Summary} conflicts with candidate {candidate}");
                    return false;
                }
            }

            if (matchingMovesByUci.Count != 1)
                return false;

            var move = matchingMovesByUci.Values.First();
            string moveText = ToUciMove(move);
            board.Move(move);
            string predictedFen = board.ToFen();

            if (string.IsNullOrWhiteSpace(predictedFen) || predictedFen == _currentFEN)
                return false;

            string optimisticBaseFen = _currentFEN;
            // Local fast-confirm: a move recognized from the board-diff with NO
            // vision round-trip. This is the fast path for the user's own move
            // (their drop changes the board locally); the timeline event lets us
            // confirm it fires instead of the slower remote-vision confirm.
            ArrowTimeline.Log("FAST_FEN_CONFIRM", reason: moveText, count: boardDiff.ChangedSquares);
            Log($"[FAST-FEN] Applied legal diff move {moveText} from {boardDiff.ChangedSquares} changed squares");
            LogDiag("FAST-FEN", $"applied {moveText} from {boardDiff.ChangedSquares} changed squares");
            BeginOptimisticFenGuard(predictedFen, optimisticBaseFen, moveText);
            ResetPendingFenCandidate();
            ResetOutOfTurnCandidate();
            ApplyConfirmedFen(predictedFen, boardView, beginNoiseGuard: false, logPrefix: "[FAST-FEN]", allowOutOfTurnHold: false);
            _analysisBoardController!.MirrorExternalFen(ApplyInferredExternalTurnToFen(predictedFen), _externalBoardDetectedFlipped);
            return true;
        }
        catch (Exception ex)
        {
            Log($"[FAST-FEN] skipped: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerable<HashSet<string>> BuildChangedSquareMappings(BoardVisionDetector.BoardDiffInfo boardDiff)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool[] candidateFlips = _externalOrientationLockedForCurrentGame
            ? new[] { _externalBoardDetectedFlipped }
            : new[] { _externalBoardDetectedFlipped, !_externalBoardDetectedFlipped };

        foreach (bool flipped in candidateFlips)
        {
            var squares = boardDiff.ChangedSquareDetails
                .Select(s => ScreenDiffSquareToAlgebraic(s.File, s.RankFromTop, flipped))
                .Where(s => s.Length == 2)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (squares.Count != boardDiff.ChangedSquares)
                continue;

            string key = string.Join(",", squares.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            if (seen.Add(key))
                yield return squares;
        }
    }

    private static bool ShouldIgnoreCoachOverlayBoardDiff(BoardVisionDetector.BoardDiffInfo boardDiff)
    {
        if (!_coachModeEnabled ||
            !_showingMoves ||
            _currentFenIsAnalysisBoard ||
            boardDiff.ChangedSquares <= 0 ||
            boardDiff.ChangedSquareDetails.Count != boardDiff.ChangedSquares)
        {
            return false;
        }

        string currentKey = GetArrowPositionKey(_currentFEN);
        HashSet<string> coachSquares;
        lock (_coachOverlaySquaresLock)
        {
            if (string.IsNullOrWhiteSpace(currentKey) ||
                !string.Equals(_lastCoachOverlaySquaresPositionKey, currentKey, StringComparison.Ordinal) ||
                _lastCoachOverlaySquares.Count == 0)
            {
                return false;
            }

            coachSquares = new HashSet<string>(_lastCoachOverlaySquares, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var changedSquares in BuildChangedSquareMappings(boardDiff))
        {
            if (changedSquares.Count > 0 && changedSquares.IsSubsetOf(coachSquares))
            {
                string changed = string.Join(",", changedSquares.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                string coach = string.Join(",", coachSquares.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                LogDiag("COACH", $"ignored self-overlay board diff ({changed}) within coach marks ({coach})");
                return true;
            }
        }

        return false;
    }

    private static void RememberCoachOverlaySquares(string fen, CoachOverlayData data)
    {
        string key = GetArrowPositionKey(fen);
        var squares = data.Marks
            .Select(mark => BoardCoordinatesToAlgebraic(mark.File, mark.Rank))
            .Where(square => square.Length == 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_coachOverlaySquaresLock)
        {
            _lastCoachOverlaySquaresPositionKey = key;
            _lastCoachOverlaySquares = squares;
        }
    }

    private static string BoardCoordinatesToAlgebraic(int file, int rank)
    {
        if (file < 0 || file > 7 || rank < 0 || rank > 7)
            return string.Empty;

        return $"{(char)('a' + file)}{(char)('1' + rank)}";
    }

    private static bool MoveDiffFitsChangedSquares(Move move, HashSet<string> moveSquares, HashSet<string> changedSquares, string boardPosition)
    {
        if (moveSquares.SetEquals(changedSquares))
            return true;

        if (RequiresExactOptimisticDiff(move, boardPosition))
            return false;

        // Chess sites often animate/highlight the previous move. On the next
        // move, diff sees the two old highlight squares revert plus the real
        // move squares change. If only one legal move fits inside that small
        // changed-square set, we can still safely skip the full piece detector.
        // Larger sets are too often transitional board paint/highlight frames;
        // let the normal confirmation path handle those so turn parity stays
        // authoritative in bullet games.
        return changedSquares.Count <= OptimisticSubsetMaxChangedSquares &&
               moveSquares.Count <= changedSquares.Count &&
               moveSquares.IsSubsetOf(changedSquares);
    }

    private static bool RequiresExactOptimisticDiff(Move move, string boardPosition)
    {
        if (move.IsCastling)
            return true;

        char movingPiece = GetPieceAtSquare(boardPosition, PositionToSquare(move.OriginalPosition));
        return char.ToLowerInvariant(movingPiece) is 'k' or 'r';
    }

    private static int GetExternalRawBoardChangeSettleMs(BoardVisionDetector.BoardDiffInfo boardDiff)
    {
        if (boardDiff.ChangedSquares <= 0)
            return 0;

        if (IsExternalBoardGeometryUnstable() ||
            IsTrackedWindowResizeSettling())
        {
            return ExternalRawBoardChangeSettleMs;
        }

        if (boardDiff.ChangedSquares <= OptimisticMoveMaxChangedSquares)
            return ExternalLikelyMoveSettleMs;

        if (boardDiff.ChangedSquares <= 10)
            return BlitzModerateChangeSettleMs;

        return ExternalRawBoardChangeSettleMs;
    }

    private static string ScreenDiffSquareToAlgebraic(int file, int rankFromTop, bool flipped)
    {
        if (file < 0 || file > 7 || rankFromTop < 0 || rankFromTop > 7)
            return string.Empty;

        if (flipped)
        {
            char flippedFile = (char)('h' - file);
            char flippedRank = (char)('1' + rankFromTop);
            return $"{flippedFile}{flippedRank}";
        }

        char standardFile = (char)('a' + file);
        char standardRank = (char)('8' - rankFromTop);
        return $"{standardFile}{standardRank}";
    }

    private static HashSet<string> MoveAffectedSquares(Move move)
    {
        var squares = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PositionToSquare(move.OriginalPosition),
            PositionToSquare(move.NewPosition)
        };

        if (move.IsCastling)
        {
            string from = PositionToSquare(move.OriginalPosition);
            string to = PositionToSquare(move.NewPosition);
            if (from == "e1" && to == "g1") { squares.Add("h1"); squares.Add("f1"); }
            else if (from == "e1" && to == "c1") { squares.Add("a1"); squares.Add("d1"); }
            else if (from == "e8" && to == "g8") { squares.Add("h8"); squares.Add("f8"); }
            else if (from == "e8" && to == "c8") { squares.Add("a8"); squares.Add("d8"); }
        }

        if (move.IsEnPassant)
        {
            squares.Add($"{(char)('a' + move.NewPosition.X)}{(char)('1' + move.OriginalPosition.Y)}");
        }

        return squares;
    }

    private static HashSet<string> MoveEndpointSquares(Move move)
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PositionToSquare(move.OriginalPosition),
            PositionToSquare(move.NewPosition)
        };
    }

    private static bool IsBoardImageRotatedFromPrevious(Mat previousBoard, Mat currentBoard)
    {
        if (previousBoard == null || currentBoard == null || previousBoard.Empty() || currentBoard.Empty())
            return false;

        try
        {
            using var previous = NormalizeBoardSnapshot(previousBoard);
            using var current = NormalizeBoardSnapshot(currentBoard);
            using var rotatedPrevious = new Mat();
            Cv2.Rotate(previous, rotatedPrevious, RotateFlags.Rotate180);

            using var normalDiff = new Mat();
            using var rotatedDiff = new Mat();
            Cv2.Absdiff(previous, current, normalDiff);
            Cv2.Absdiff(rotatedPrevious, current, rotatedDiff);

            double normalMean = Cv2.Mean(normalDiff).Val0;
            double rotatedMean = Cv2.Mean(rotatedDiff).Val0;

            return rotatedMean < 35.0 && rotatedMean < normalMean * 0.55;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyExternalBoardVisualFlip(Mat boardView, string reason)
    {
        _externalBoardDetectedFlipped = !_externalBoardDetectedFlipped;
        UpdateConfirmedBoardSnapshot(boardView);
        ResetPendingFenCandidate();
        ResetAnalysisSchedulingState();

        // Keep cached engine arrows, but force their screen projection to be rebuilt
        // against the newly observed board orientation.
        RefreshDisplayedArrows();
        bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
        TryQueueAnalysis(requestedPerspective, force: true);
        if (!_currentFenIsAnalysisBoard && !string.IsNullOrWhiteSpace(_currentFEN))
        {
            _analysisBoardController!.MirrorExternalFen(ApplyInferredExternalTurnToFen(_currentFEN), _externalBoardDetectedFlipped, force: true);
        }

        Log($"[{DateTime.Now:HH:mm:ss}] External board visual flip detected from {reason} -> flipped={_externalBoardDetectedFlipped}");
    }

    private static Mat NormalizeBoardSnapshot(Mat board)
    {
        var normalized = new Mat();
        if (board.Channels() == 1)
        {
            board.CopyTo(normalized);
        }
        else
        {
            Cv2.CvtColor(board, normalized, ColorConversionCodes.BGR2GRAY);
        }

        if (normalized.Width != 256 || normalized.Height != 256)
        {
            var resized = new Mat();
            Cv2.Resize(normalized, resized, new OpenCvSharp.Size(256, 256), 0, 0, InterpolationFlags.Area);
            normalized.Dispose();
            return resized;
        }

        return normalized;
    }

    private static void ResetConfirmedBoardSnapshot()
    {
        _lastConfirmedBoardSnapshot?.Dispose();
        _lastConfirmedBoardSnapshot = null;
        _lastConfirmedBoardDiffSnapshot?.Dispose();
        _lastConfirmedBoardDiffSnapshot = null;
        _lastConfirmedBoardPixelFingerprint?.Dispose();
        _lastConfirmedBoardPixelFingerprint = null;
    }

    private static void UpdateAnalysisBoardSnapshot(AnalysisBoardSnapshot snapshot)
    {
        bool positionChanged = false;
        int sessionVersion = 0;
        lock (_analysisBoardStateLock)
        {
            if (snapshot.Visible != _lastAnalysisBoardSnapshotVisible)
            {
                _analysisBoardController!.ClearLastAnalysisKey();
                _lastAnalysisBoardSnapshotVisible = snapshot.Visible;
            }

            positionChanged =
                !string.Equals(_analysisBoardFen, snapshot.Fen ?? "", StringComparison.Ordinal) ||
                _analysisBoardIsFlipped != snapshot.BoardFlipped;

            _analysisBoardVisible = snapshot.Visible;
            _analysisBoardIsFlipped = snapshot.BoardFlipped;
            _analysisBoardHasTrackedHistory = snapshot.HasTrackedHistory;
            _analysisBoardScreenRect = snapshot.BoardScreenBounds;
            _analysisBoardWindowScreenRect = snapshot.WindowScreenBounds;
            _analysisBoardFen = snapshot.Fen ?? "";

            if (positionChanged)
            {
                _analysisBoardController!.ClearLastAnalysisKey();
                sessionVersion = _analysisBoardController!.AnalysisSessionVersion;
            }
        }

        if (positionChanged && _analysisBoardController!.AnalysisEnabled && sessionVersion == _analysisBoardController!.AnalysisSessionVersion)
        {
            _analysisBoardController!.TryQueueAnalysisBoardAnalysis(sessionVersion);
        }
    }

    private static bool TryGetActiveAnalysisBoardSnapshot(out AnalysisBoardSnapshot snapshot)
    {
        lock (_analysisBoardStateLock)
        {
            if (_analysisBoardVisible && !_analysisBoardScreenRect.IsEmpty && !string.IsNullOrWhiteSpace(_analysisBoardFen))
            {
                snapshot = new AnalysisBoardSnapshot
                {
                    Visible = true,
                    BoardFlipped = _analysisBoardIsFlipped,
                    HasTrackedHistory = _analysisBoardHasTrackedHistory,
                    BoardScreenBounds = _analysisBoardScreenRect,
                    WindowScreenBounds = _analysisBoardWindowScreenRect,
                    Fen = _analysisBoardFen
                };
                return true;
            }
        }

        snapshot = new AnalysisBoardSnapshot();
        return false;
    }

    private static bool TryGetStoredAnalysisBoardSnapshot(out AnalysisBoardSnapshot snapshot)
    {
        lock (_analysisBoardStateLock)
        {
            if (!string.IsNullOrWhiteSpace(_analysisBoardFen))
            {
                snapshot = new AnalysisBoardSnapshot
                {
                    Visible = _analysisBoardVisible,
                    BoardFlipped = _analysisBoardIsFlipped,
                    HasTrackedHistory = _analysisBoardHasTrackedHistory,
                    BoardScreenBounds = _analysisBoardScreenRect,
                    WindowScreenBounds = _analysisBoardWindowScreenRect,
                    Fen = _analysisBoardFen
                };
                return true;
            }
        }

        snapshot = new AnalysisBoardSnapshot();
        return false;
    }

    private static bool GetEffectiveBoardFlipped(string capturedFEN)
    {
        string boardPosition = GetBoardPosition(capturedFEN);
        if (!string.IsNullOrWhiteSpace(boardPosition) &&
            TryGetManualOrientationOverride(boardPosition, out bool manualFlipped))
        {
            return manualFlipped;
        }

        if (_analysisTargetIsAnalysisBoard && _currentFenIsAnalysisBoard)
        {
            lock (_analysisBoardStateLock)
            {
                if (_analysisBoardVisible &&
                    !string.IsNullOrWhiteSpace(_analysisBoardFen) &&
                    string.Equals(_analysisBoardFen, capturedFEN, StringComparison.Ordinal))
                {
                    return _analysisBoardIsFlipped;
                }
            }
        }

        return _externalBoardDetectedFlipped;
    }

    private static bool ApplyExternalDisplayOrientation(bool? observedFlipped, string reason)
    {
        if (!observedFlipped.HasValue)
            return false;

        bool observed = observedFlipped.Value;
        if (_externalOrientationLockedForCurrentGame && _externalBoardDetectedFlipped != observed)
        {
            TraceBoard(
                $"orientation display kept locked flipped={_externalBoardDetectedFlipped} " +
                $"despite observed={observed} ({reason})");
            return false;
        }

        if (_externalBoardDetectedFlipped == observed)
            return false;

        _externalBoardDetectedFlipped = observed;
        TraceBoard($"orientation display updated flipped={observed} ({reason})");
        return true;
    }

    private static bool ShouldBypassWaitingForExternalBothMode(string fen)
    {
        return _analysisBothEnabled && !IsActiveAnalysisBoardFen(fen);
    }

    private static bool IsActiveAnalysisBoardFen(string fen)
    {
        if (!_analysisTargetIsAnalysisBoard || !_currentFenIsAnalysisBoard)
            return false;

        lock (_analysisBoardStateLock)
        {
            return _analysisBoardVisible &&
                   !string.IsNullOrWhiteSpace(_analysisBoardFen) &&
                   string.Equals(_analysisBoardFen, fen, StringComparison.Ordinal);
        }
    }

    private static bool CurrentFenMatchesStoredAnalysisBoard()
    {
        if (string.IsNullOrWhiteSpace(_currentFEN))
            return false;

        lock (_analysisBoardStateLock)
        {
            return !string.IsNullOrWhiteSpace(_analysisBoardFen) &&
                   string.Equals(_analysisBoardFen, _currentFEN, StringComparison.Ordinal);
        }
    }

    private static void DetachAnalysisBoardFromExternalTracking()
    {
        if (!_currentFenIsAnalysisBoard && !CurrentFenMatchesStoredAnalysisBoard())
            return;

        _currentFenIsAnalysisBoard = false;
        _analysisTargetIsAnalysisBoard = false;
        _currentFEN = "";
        _lastTrackedBox = null;
        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        ResetAnalysisSchedulingState();
        ResetPendingFenCandidate();
        ResetConfirmedBoardSnapshot();
    }

    private static bool ShouldIgnoreDetectedAnalysisBoard(Rect boardRect)
    {
        lock (_analysisBoardStateLock)
        {
            if (!_analysisBoardVisible && _analysisBoardScreenRect.IsEmpty)
                return false;

            Rectangle detected = new Rectangle(boardRect.X, boardRect.Y, boardRect.Width, boardRect.Height);
            Rectangle exclusion = GetAnalysisBoardExternalDetectionExclusionRect();
            if (exclusion.IsEmpty)
                return false;

            System.Drawing.Point detectedCenter = new System.Drawing.Point(detected.Left + detected.Width / 2, detected.Top + detected.Height / 2);
            if (exclusion.Contains(detectedCenter))
                return true;

            Rectangle overlap = Rectangle.Intersect(detected, exclusion);
            if (overlap.IsEmpty)
                return false;

            double detectedArea = Math.Max(1, detected.Width * detected.Height);
            double overlapArea = overlap.Width * overlap.Height;
            return overlapArea / detectedArea >= 0.35;
        }
    }

    private static Rectangle GetAnalysisBoardExternalDetectionExclusionRect()
    {
        Rectangle exclusion = _analysisBoardScreenRect;

        if (exclusion.IsEmpty)
            return Rectangle.Empty;

        return Rectangle.Inflate(exclusion, 24, 24);
    }

    private static char GetRequestedAnalysisColor(string fen)
    {
        if (_analysisBothEnabled)
        {
            if (IsActiveAnalysisBoardFen(fen))
            {
                char? sideToMove = GetSideToMove(fen);
                if (sideToMove.HasValue)
                {
                    return sideToMove.Value;
                }
            }

            return _inferredSideToMove;
        }

        return _userColor;
    }

    private static char GetAnalysisSideForFen(string fen, bool isBlackPerspective)
    {
        if (!IsActiveAnalysisBoardFen(fen) && !_analysisBothEnabled)
            return isBlackPerspective ? 'b' : 'w';

        return GetRequestedAnalysisColor(fen);
    }

    private static bool GetRequestedAnalysisPerspective(string fen, bool fallbackIsBlackPerspective)
    {
        if (_analysisBothEnabled)
        {
            if (TryGetLockedExternalArrowPerspective(fen, out bool lockedPerspective, out string lockReason))
            {
                LogDiag("TURN", $"using locked external arrow perspective {(lockedPerspective ? "BLACK" : "WHITE")} ({lockReason})");
                return lockedPerspective;
            }

            char requestedColor = GetRequestedAnalysisColor(fen);
            return requestedColor == 'b';
        }

        return fallbackIsBlackPerspective;
    }

    private static bool TryGetLockedExternalArrowPerspective(string fen, out bool isBlackPerspective, out string reason)
    {
        isBlackPerspective = false;
        reason = "";

        if (!_analysisBothEnabled ||
            _currentFenIsAnalysisBoard ||
            string.IsNullOrWhiteSpace(fen) ||
            IsActiveAnalysisBoardFen(fen) ||
            !_showingMoves ||
            _currentMoveArrows is not { Count: > 0 } ||
            string.IsNullOrWhiteSpace(_externalArrowPerspectiveBoardKey) ||
            _externalArrowPerspectiveFirstDisplayUtc == DateTime.MinValue)
        {
            return false;
        }

        string boardKey = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(boardKey) ||
            !string.Equals(boardKey, _externalArrowPerspectiveBoardKey, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(_lastArrowSourceFEN) ||
            !string.Equals(GetBoardPosition(_lastArrowSourceFEN), boardKey, StringComparison.Ordinal))
        {
            return false;
        }

        double visibleAgeMs = (DateTime.UtcNow - _externalArrowPerspectiveFirstDisplayUtc).TotalMilliseconds;
        if (visibleAgeMs < ExternalArrowPerspectiveSwitchWindowMs)
            return false;

        isBlackPerspective = _externalArrowPerspectiveBlack;
        reason = $"same board visible for {visibleAgeMs:F0}ms";
        return true;
    }

    private static bool ShouldRotateFenForAnalysis(string capturedFEN, bool isBlackPerspective)
    {
        // External detector FENs are normalized when orientation is resolved.
        // The overlay still needs the display-flipped flag for drawing, but the
        // engine must analyze the normalized chess position itself. Rotating
        // again here makes Stockfish analyze a different position and can yield
        // plausible-looking but tactically absurd arrows.
        if (!IsActiveAnalysisBoardFen(capturedFEN))
            return false;

        if (_analysisTargetIsAnalysisBoard && _currentFenIsAnalysisBoard)
        {
            lock (_analysisBoardStateLock)
            {
                if (_analysisBoardVisible &&
                    !string.IsNullOrWhiteSpace(_analysisBoardFen) &&
                    string.Equals(_analysisBoardFen, capturedFEN, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        _ = isBlackPerspective;
        return GetEffectiveBoardFlipped(capturedFEN);
    }

    private static bool TryApplyAnalysisBoardTurnStateForRequestedColor(char requestedColor)
    {
        if (!IsActiveAnalysisBoardFen(_currentFEN))
            return false;

        char? sideToMove = GetSideToMove(_currentFEN);
        if (!sideToMove.HasValue)
            return false;

        bool shouldWaitForOpponent = sideToMove.Value != requestedColor;
        _waitingForOpponentMove = shouldWaitForOpponent;

        if (shouldWaitForOpponent)
        {
            _currentMoveArrows = null;
            _lastAnalysisVariations = null;
            _lastArrowSourceFEN = "";
            ClearActiveArrows();
        }

        RefreshDebugView($"Analysis board side to move: {sideToMove.Value}");
        return true;
    }

    private static bool ShouldUseAuthoritativeAnalysisBoardTurn(string fen)
    {
        return IsActiveAnalysisBoardFen(fen);
    }

    private static void ProcessAnalysisBoardSnapshot(AnalysisBoardSnapshot snapshot)
    {
        // If we're actively watching a live external board, don't let the
        // internal Analysis Board hijack the shared overlay / confirmed-FEN
        // pipeline. The analysis board can still keep its own local snapshot and
        // analysis, but the live W/B/W+B experience must remain authoritative.
        if (!_analysisBoardController!.MatchRunning &&
            _continuousAnalysisEnabled &&
            _isTracking &&
            !_currentFenIsAnalysisBoard &&
            !string.IsNullOrWhiteSpace(_currentFEN))
        {
            return;
        }

        var boardRect = snapshot.BoardScreenBounds;
        var windowRect = snapshot.WindowScreenBounds;
        var trackedRect = new Rect(boardRect.X, boardRect.Y, boardRect.Width, boardRect.Height);

        EnsureAnalysisBoardToolbarClearance(windowRect);

        _boardLostFrames = 0;
        _invalidFenFrames = 0;
        _requestBoardRefresh = false;
        _lastTrackedBox = trackedRect;

        _evalBar?.SetBoardVisible(false);
        _engineLines?.SetBoardVisible(false);
        ShowToolbarAtFallbackPosition();

        // The internal analysis board is already authoritative and should not inherit
        // castling/turn/history adjustments from the screen-detection merge path.
        string confirmedFen = snapshot.Fen;
        _analysisTargetIsAnalysisBoard = true;
        ApplyConfirmedFen(confirmedFen, null, false, "[TEST FEN]", true);
        RefreshDisplayedArrows();
    }

    private static void UpdateInferredSideToMoveForExternalBoard(string fen)
    {
        if (IsActiveAnalysisBoardFen(fen))
            return;

        char previousSide = _inferredSideToMove;
        string boardPosition = GetBoardPosition(fen);
        if (IsInitialBoardPosition(fen))
        {
            _inferredSideToMove = 'w';
            if (previousSide != _inferredSideToMove)
            {
                LogTurnInference("external initial board inferred side-to-move=w");
            }
            return;
        }

        bool initialBothOpeningWindow =
            _analysisBothEnabled &&
            _externalTrackedPositionCount <= 3 &&
            !string.IsNullOrWhiteSpace(boardPosition) &&
            CountChangedBoardSquares(InitialBoardPosition, boardPosition) <= InitialExternalOpeningSideMaxPlies * 4;

        if (initialBothOpeningWindow)
        {
            if (TryInferOpeningSideToMoveByLegalPath(fen, out char openingSideToMove, out char openingLastMover, out int openingPlies))
            {
                _inferredSideToMove = openingSideToMove;
                if (previousSide != _inferredSideToMove || _externalTrackedPositionCount <= 3)
                {
                    LogTurnInference(
                        $"initial W+B side inferred from opening path plies={openingPlies}, last={openingLastMover}, side-to-move={_inferredSideToMove} count={_externalTrackedPositionCount} board={boardPosition}");
                }
                return;
            }

            if (TryInferFreshOpeningSideToMove(fen, out char freshOpeningSideToMove, out char freshOpeningLastMover))
            {
                _inferredSideToMove = freshOpeningSideToMove;
                if (previousSide != _inferredSideToMove || _externalTrackedPositionCount <= 3)
                {
                    LogTurnInference(
                        $"initial W+B side inferred from one-ply opening last={freshOpeningLastMover}, side-to-move={_inferredSideToMove} count={_externalTrackedPositionCount} board={boardPosition}");
                }
                return;
            }

            if (TryGetExternalFenSideToMoveFallback(fen, out char initialFenSideToMove))
            {
                _inferredSideToMove = initialFenSideToMove;
                LogTurnInference(
                    $"initial W+B side fallback from detected FEN side-to-move={_inferredSideToMove} count={_externalTrackedPositionCount} board={boardPosition}");
                return;
            }
        }

        if (TryInferFreshOpeningSideToMove(fen, out char inferredSideToMove, out char lastMover))
        {
            _inferredSideToMove = inferredSideToMove;
            if (previousSide != _inferredSideToMove || _externalTrackedPositionCount <= 1)
            {
                LogTurnInference(
                    $"external fresh opening inferred last={lastMover}, side-to-move={_inferredSideToMove} board={boardPosition}");
            }
            return;
        }

        if (_analysisBothEnabled &&
            _externalTrackedPositionCount <= 1 &&
            TryGetExternalFenSideToMoveFallback(fen, out char fenSideToMove))
        {
            _inferredSideToMove = fenSideToMove;
            LogTurnInference(
                $"initial W+B side fallback from detected FEN side-to-move={_inferredSideToMove} board={boardPosition}");
        }
    }

    private static void LogTurnInference(string message)
    {
        Log($"[TURN] {message}");
        LogDiag("TURN", message);
    }

    private static bool TryInferOpeningSideToMoveByLegalPath(string fen, out char sideToMove, out char lastMover, out int plies)
    {
        sideToMove = 'w';
        lastMover = '\0';
        plies = 0;

        if (string.IsNullOrWhiteSpace(fen) ||
            IsInitialBoardPosition(fen) ||
            IsActiveAnalysisBoardFen(fen))
        {
            return false;
        }

        string targetBoard = GetBoardPosition(fen);
        if (string.IsNullOrWhiteSpace(targetBoard) ||
            string.Equals(targetBoard, InitialBoardPositionRotated, StringComparison.Ordinal))
        {
            return false;
        }

        int changedFromInitial = CountChangedBoardSquares(InitialBoardPosition, targetBoard);
        if (changedFromInitial <= 0 || changedFromInitial > InitialExternalOpeningSideMaxPlies * 4)
            return false;

        LegalTurnTransition? openingPath = TryFindOpeningLegalTurnPathFast(
            $"{InitialBoardPosition} w KQkq - 0 1",
            targetBoard,
            'w',
            InitialExternalOpeningSideMaxPlies);

        if (openingPath == null)
            return false;

        sideToMove = openingPath.SideToMoveAfter;
        lastMover = openingPath.LastMover;
        plies = openingPath.PlyCount;
        return true;
    }

    private static LegalTurnTransition? TryFindOpeningLegalTurnPathFast(
        string startFen,
        string targetBoardPosition,
        char startSide,
        int maxPlies)
    {
        maxPlies = Math.Clamp(maxPlies, 1, InitialExternalOpeningSideMaxPlies);
        var matches = new List<LegalTurnTransition>();
        int visitedNodes = 0;
        var stopwatch = Stopwatch.StartNew();

        TryFindOpeningLegalTurnPathFastRecursive(
            FenWithSideToMove(startFen, startSide),
            targetBoardPosition,
            startSide,
            plyDepth: 1,
            maxPlies: 1,
            matches,
            stopwatch,
            ref visitedNodes);

        LegalTurnTransition? onePlyMatch = SelectLegalTurnTransitionMatch(matches, startSide);
        if (onePlyMatch != null || maxPlies <= 1)
            return onePlyMatch;

        matches.Clear();
        visitedNodes = 0;
        stopwatch.Restart();

        TryFindOpeningLegalTurnPathFastRecursive(
            FenWithSideToMove(startFen, startSide),
            targetBoardPosition,
            startSide,
            plyDepth: 1,
            maxPlies,
            matches,
            stopwatch,
            ref visitedNodes);

        LegalTurnTransition? match = SelectLegalTurnTransitionMatch(matches, startSide);
        if (match == null && (stopwatch.ElapsedMilliseconds >= InitialExternalOpeningSideSearchBudgetMs ||
                              visitedNodes >= InitialExternalOpeningSideSearchMaxNodes))
        {
            LogDiag(
                "TURN",
                $"opening path inference budget exhausted after {stopwatch.ElapsedMilliseconds}ms/{visitedNodes} nodes");
        }

        return match;
    }

    private static void TryFindOpeningLegalTurnPathFastRecursive(
        string fen,
        string targetBoardPosition,
        char sideToMove,
        int plyDepth,
        int maxPlies,
        List<LegalTurnTransition> matches,
        Stopwatch stopwatch,
        ref int visitedNodes)
    {
        if (plyDepth > maxPlies ||
            stopwatch.ElapsedMilliseconds >= InitialExternalOpeningSideSearchBudgetMs ||
            visitedNodes >= InitialExternalOpeningSideSearchMaxNodes)
        {
            return;
        }

        try
        {
            string legalFen = FenWithSideToMove(fen, sideToMove);
            var board = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
            foreach (var move in board.Moves(false, true).ToList())
            {
                if (stopwatch.ElapsedMilliseconds >= InitialExternalOpeningSideSearchBudgetMs ||
                    visitedNodes >= InitialExternalOpeningSideSearchMaxNodes)
                {
                    return;
                }

                visitedNodes++;

                var trial = ChessBoard.LoadFromFen(legalFen, AutoEndgameRules.All);
                Move? matchingMove = trial.Moves(false, true)
                    .FirstOrDefault(m => string.Equals(ToUciMove(m), ToUciMove(move), StringComparison.OrdinalIgnoreCase));

                if (matchingMove == null)
                    continue;

                trial.Move(matchingMove);
                string trialFen = trial.ToFen();
                string trialBoard = GetBoardPosition(trialFen);
                char nextSide = sideToMove == 'w' ? 'b' : 'w';
                if (string.Equals(trialBoard, targetBoardPosition, StringComparison.Ordinal))
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
                    int remainingPlies = maxPlies - plyDepth;
                    int targetDistance = CountChangedBoardSquares(trialBoard, targetBoardPosition);
                    if (targetDistance > remainingPlies * 2)
                        continue;

                    TryFindOpeningLegalTurnPathFastRecursive(
                        FenWithSideToMove(trialFen, nextSide),
                        targetBoardPosition,
                        nextSide,
                        plyDepth + 1,
                        maxPlies,
                        matches,
                        stopwatch,
                        ref visitedNodes);
                }
            }
        }
        catch (Exception ex)
        {
            LogDiag("TURN", $"opening path inference skipped side={sideToMove}, ply={plyDepth}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool TryGetExternalFenSideToMoveFallback(string fen, out char sideToMove)
    {
        sideToMove = 'w';

        if (string.IsNullOrWhiteSpace(fen) ||
            IsActiveAnalysisBoardFen(fen))
        {
            return false;
        }

        char? fenSideToMove = GetSideToMove(fen);
        if (fenSideToMove is not ('w' or 'b'))
            return false;

        sideToMove = fenSideToMove.Value;
        return true;
    }

    private static void EnsureAnalysisBoardToolbarClearance(Rectangle windowRect)
    {
        if (_settingsToolbar == null || _analysisBoardForm == null || windowRect.IsEmpty)
            return;

        Rectangle workingArea = Screen.FromRectangle(windowRect).WorkingArea;
        int requiredGap = _settingsToolbar.GetCurrentToolbarHeight() + 10;
        int minimumWindowTop = workingArea.Top + requiredGap;

        if (windowRect.Y >= minimumWindowTop)
            return;

        int newTop = minimumWindowTop;

        if (_analysisBoardForm.InvokeRequired)
        {
            _analysisBoardForm.BeginInvoke(new Action(() =>
            {
                _analysisBoardForm.Top = newTop;
            }));
        }
        else
        {
            _analysisBoardForm.Top = newTop;
        }
    }

    private static Rectangle GetToolbarFallbackAnchorRect()
    {
        Rectangle screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int width = Math.Min(820, Math.Max(720, screen.Width / 2));
        int x = screen.Left + (screen.Width - width) / 2;
        int y = screen.Top + 42;
        return new Rectangle(x, y, width, 80);
    }

    /// <summary>
    /// Hide all overlay UI immediately. Thread-safe - does cross-thread
    /// marshalling via BeginInvoke. Does NOT touch main-loop-owned
    /// state (_trackedHwnd, _lastTrackedBox, etc.). Safe to call from
    /// the WinEvent hook worker thread for instant visual response.
    /// </summary>
    private static void HideOverlaysVisually()
    {
        // During a swap hold the previous arrows are still painted while
        // _showingMoves is false - they must vanish with the window too, or
        // they float over whatever is behind it until the hold expires.
        bool holdPending = DateTime.UtcNow < _externalArrowHoldUntilUtc;
        if ((_showingMoves || holdPending) && _overlay != null)
        {
            _externalArrowHoldUntilUtc = DateTime.MinValue;
            int hideGen = Interlocked.Increment(ref _arrowDisplayGeneration);
            Interlocked.Increment(ref _arrowRenderToken);
            try
            {
                _overlay.BeginInvoke(new Action(() => _overlay.HideArrows(hideGen, preserveFreeLimitWatermark: false)));
            }
            catch { /* form may have been disposed */ }
            _showingMoves = false;
        }

        // SetBoardVisible on these forms internally marshals to their
        // own UI thread, so this is thread-safe to call from anywhere.
        try { _evalBar?.SetBoardVisible(false); } catch { }
        try { _engineLines?.SetBoardVisible(false); } catch { }
        // Keep controls reachable even when the tracked chess window is
        // minimized, hidden behind another window, or temporarily gone.
        // Arrows/eval/lines are tied to the board and must disappear, but
        // the toolbar should snap back to its neutral top-center position.
        try { ShowToolbarAtFallbackPosition(); } catch { }
    }

    /// <summary>
    /// Hide overlays AND reset window-tracking state. Only safe to
    /// call from the main loop thread. Used by the per-frame poll
    /// when IsTrackable returns false.
    /// </summary>
    private static void HideOverlaysAfterWindowGone(string reason)
    {
        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", $"tracked window not trackable (minimized/hidden/closed) - hiding overlays immediately ({reason})");
        }

        // Cancel any pending analysis. Without this, an analysis that
        // started before the minimize completes a second or two later
        // and re-shows arrows on top of whatever's behind the board.
        try
        {
            CancelPendingAnalysis($"window not trackable ({reason})");
        }
        catch { }

        HideOverlaysVisually();

        // State cleanup (only safe on main loop thread).
        IntPtr oldTrackedHwnd = _trackedHwnd;
        bool oldTrackedWasMinimized = false;
        try { oldTrackedWasMinimized = oldTrackedHwnd != IntPtr.Zero && WindowTracker.IsIconic(oldTrackedHwnd); }
        catch { oldTrackedWasMinimized = false; }

        _lostHwndCache = oldTrackedWasMinimized ? IntPtr.Zero : oldTrackedHwnd;
        _trackedHwnd = IntPtr.Zero;
        _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
        ClearForegroundNoBoardOverlayHold("tracked window gone");
        _framesSinceWindowTrackVerify = 0;
        _lastTrackedBox = null;
        _boardHistory.Clear();
        ResetConfirmedBoardSnapshot();
        _boardLostFrames = 0;
        _boardContentLostFrames = 0;
        // NOTE: We deliberately KEEP _currentMoveArrows /
        // _lastAnalysisVariations / _lastArrowSourceFEN intact across
        // the lost-tracking period. On restore, the dedicated lost-hwnd
        // poll redraws these instantly via RefreshDisplayedArrows -
        // before vision/analysis even runs - so arrows reappear with
        // zero latency. The "stale arrows showing during minimize" bug
        // that originally motivated clearing them is prevented by other
        // means: vision is paused while the latch is set, and
        // RefreshDisplayedArrows itself early-returns when the latch
        // is set. So the cache can never be displayed during the lost
        // period - only on legitimate re-acquisition.
        // Latch the "tracking lost" flag so TryQueueAnalysis refuses
        // to fire until we re-acquire a real top-level window. If the old
        // board window was explicitly minimized, do NOT wait for that same
        // HWND to return: the user may have another board window already
        // visible/focused, and normal foreground acquisition should be free
        // to switch to it immediately.
        _trackingLostWaitingForReacquire = !oldTrackedWasMinimized;
        _lostAcquisitionCandidateSinceUtc = null;
        _lastLostIconicLogged = false;
        _lastLostVisibleLogged = false;
        _minimizeEndFiredForLostHwnd = false;
        _latchSetTick = WindowTracker.GetSystemTick();
    }

    /// <summary>
    /// The tracked window is still alive and stationary, but repeated
    /// verification scans no longer find a board inside it. Treat that as
    /// content-level board loss: the page may have navigated, the board may
    /// be hidden, or the game surface may have been replaced. Unlike window
    /// loss, we do not latch to the old HWND; we clear stale visuals and let
    /// normal acquisition find the next real board.
    /// </summary>
    private static void HandleBoardContentLostInHealthyWindow()
    {
        if (_diagLoggingEnabled)
        {
            LogDiag("WINTRACK", "board content disappeared inside healthy tracked window - clearing cached board state");
        }

        try
        {
            CancelPendingAnalysis("board content disappeared");
        }
        catch { }

        _boardContentLostFrames = 0;
        _boardLostFrames = 0;
        _framesSinceWindowTrackVerify = 0;
        _scheduledVerifyUtc = DateTime.MinValue;
        _trackedHwnd = IntPtr.Zero;
        _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
        ClearForegroundNoBoardOverlayHold("board content disappeared");
        _lastTrackedBox = null;
        _boardHistory.Clear();
        ResetConfirmedBoardSnapshot();
        ResetPendingFenCandidate();

        _currentMoveArrows = null;
        _lastAnalysisVariations = null;
        _lastArrowSourceFEN = "";
        _showingMoves = false;
        ClearDisplayedArrowDepthMemory();

        try { ClearExternalArrows(); } catch { }
        try { _evalBar?.SetBoardVisible(false); } catch { }
        try { _engineLines?.SetBoardVisible(false); } catch { }

        ShowToolbarAtFallbackPosition();
        _requestBoardRefresh = true;
        _fastBoardScanUntilUtc = DateTime.UtcNow.AddMilliseconds(_postMoveFastBoardScanMs);
        RefreshDebugView("Board disappeared from tracked window");
    }

    /// <summary>
    /// Win32 system-event callback. Fires from a worker thread the
    /// moment ANY window minimizes / un-minimizes / is destroyed. We
    /// filter to only react when the event is for our tracked
    /// board window. The whole point of this hook is to avoid
    /// the polling latency of the main loop - minimize feedback
    /// drops from up to ~1s to ~10ms.
    /// </summary>
}
