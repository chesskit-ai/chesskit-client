using ChessKit;
using System.Globalization;
using static ChessKit.FenText;

// Taskbar-icon teardown, hide-confirmation, and the settings-change handler.
partial class Program
{
    private static void SetSettingsToolbarHidden(bool hidden, bool persist)
    {
        _settingsToolbarHidden = hidden;

        if (persist)
        {
            var settings = _appSettingsManager.Load();
            settings.SettingsToolbarHidden = hidden;
            _appSettingsManager.Save(settings);
        }

        _settingsToolbar?.SyncSettingsToolbarHiddenState(hidden);

        _systemTray?.RefreshShowToolbarMenuText();

        if (hidden)
        {
            _settingsToolbar?.SetBoardVisible(false);
            _settingsToolbar?.SetEnabled(false);
        }
    }

    private static bool ConfirmHideTaskbarIconIfNeeded()
    {
        AppSettings settings = _appSettingsManager.Load();
        if (settings.SystemTrayHideConfirmed)
            return true;

        DialogResult result = ShowTopMostMessageBox(
            "Hide the Chess Kit system tray icon?\n\n" +
            "You can bring Chess Kit back at any time by pressing F1, which shows the floating toolbar. " +
            "From the toolbar menu you can enable the system tray icon again for future launches.\n\n" +
            "This confirmation is shown only once.",
            "Hide System Tray Icon",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.OK)
            return false;

        settings.SystemTrayHideConfirmed = true;
        _appSettingsManager.Save(settings);
        return true;
    }

    private static void DisposeTaskbarIcon()
    {
        // Taskbar access window (null when a Licensed user runs tray-only).
        _taskbarWindow?.Dispose();

        _systemTray?.Dispose();
    }

    // Handle settings changes from toolbar
    private static void HandleSettingChanged(string setting, object value)
    {
        switch (setting)
        {
            case "Exit":
                RequestApplicationExit();
                break;

            case "OpenAnalysisBoard":
                if (_analysisBoardForm != null)
                {
                    if (_analysisBoardForm.InvokeRequired)
                    {
                        _analysisBoardForm.BeginInvoke(new Action(() => _analysisBoardForm.ShowAnalysisBoard()));
                    }
                    else
                    {
                        _analysisBoardForm.ShowAnalysisBoard();
                    }
                }
                break;

            case "ShowTaskbarIcon":
                _systemTray?.SetVisible(value is bool showTaskbarIcon && showTaskbarIcon, persist: true);
                break;

            case "ShowTaskbarWindow":
                // Licensed-only toggle for the taskbar access window (distinct
                // from ShowTaskbarIcon, which is the system TRAY icon). Free
                // keeps its window unconditionally - the toolbar hides the row
                // there, so any stray event is ignored.
                bool showTaskbarWindow = value is bool taskbarWindowOn && taskbarWindowOn;
                if (BuildLimits.IsFreeEdition)
                {
                    Log("[Settings] ShowTaskbarWindow ignored: the Free Edition taskbar window is mandatory");
                    break;
                }
                if (showTaskbarWindow && _taskbarWindow == null)
                    EnsureTaskbarWindowCreated();
                else
                    _taskbarWindow?.SetVisible(showTaskbarWindow);
                Log($"[Settings] Taskbar window {(showTaskbarWindow ? "shown" : "hidden")}");
                break;

            case "SettingsToolbarHidden":
                SetSettingsToolbarHidden(value is bool settingsToolbarHidden && settingsToolbarHidden, persist: true);
                break;

            case "ExcludeOverlaysFromCapture":
                // Toolbar already persisted the flag; just (re)apply the window
                // display affinity to every live overlay surface.
                bool excludeFromCapture = value is bool exclude && exclude;
                CaptureExclusion.SetEnabled(excludeFromCapture);
                Log($"[Settings] Overlays {(excludeFromCapture ? "hidden from" : "visible to")} screen capture");
                break;

            case "ShowHardwareId":
                ShowHardwareIdFromTaskbar();
                break;

            case "ShowLicenseStatus":
                ShowLicenseStatusFromTaskbar();
                break;

            case "ShowAbout":
                ShowAboutFromTaskbar();
                break;

            case "VisitWebsite":
                OpenChessKitWebsite();
                break;

            case "ShowFreeUpsell":
                ShowFreeUpsell();
                break;

            case "KeyBindingsChanged":
                if (value is HotkeyBindings bindings)
                {
                    var updatedHotkeys = bindings.Clone();
                    updatedHotkeys.Normalize();
                    var settings = _appSettingsManager.Load();
                    settings.Hotkeys = updatedHotkeys.Clone();
                    _appSettingsManager.Save(settings);
                    // Store the new bindings on the controller and rebind the
                    // system-hotkey fallback (rebind marshals to the UI thread when
                    // required), exactly as the original code did.
                    _hotkeyController?.SetBindings(updatedHotkeys);
                    Log("[Settings] Key bindings updated");
                }
                break;

            case "DebugHud":
                bool hudOn = (bool)value;
                _debugHudPresenter.SetEnabled(hudOn);
                Log($"[Settings] Debug HUD {(hudOn ? "ENABLED" : "DISABLED")}");
                break;

            case "MenuExpanded":
                _menuExpanded = (bool)value;
                if (_menuExpanded)
                {
                    Log($"[Settings] Menu expanded - pausing board detection");
                }
                else
                {
                    Log($"[Settings] Menu collapsed - resuming board detection");
                    QueueExternalAnalysisAfterUiSettles("settings menu collapsed");
                }
                break;

            case "ObstructingUiActive":
                // Toolbar fires this when a popup that overlaps the chess
                // board is shown/hidden (engine selector dropdown, file
                // dialog, etc.). Pauses board detection + analysis to
                // prevent feeding YOLO an occluded board image.
                SetObstructingUiActive((bool)value);
                break;

            case "SoftObstructingUiActive":
                // Lightweight popups such as the engine selector should pause
                // FEN reads while visible, but should not abort the current
                // analysis or hide arrows. Aborting on dropdown-open made the
                // menu itself flaky during active analysis.
                SetObstructingUiActive((bool)value, preserveAnalysis: true);
                break;

            case "ResetDepth":
                if (_stockfish != null)
                {
                    _stockfish.ClearAllDepthTracking();
                    Log($"[Settings] Depth tracking reset - starting from initial depth");
                }
                break;

            case "AnalysisWhite":
                bool enableWhite = (bool)value;
                if (enableWhite)
                {
                    bool wasAlreadyAnalyzingWhite = _continuousAnalysisEnabled
                        && !_analysisIsBlackPerspective
                        && !_analysisBothEnabled;
                    _analysisBothEnabled = false;
                    if (_stockfish != null) _stockfish.ClearAllDepthTracking();

                    // Only unlock orientation if the user is actually
                    // *changing* perspective. Re-clicking the same active
                    // button (e.g., W when W was already on) shouldn't reset
                    // the orientation cache - that caused per-frame orientation
                    // oscillation when stale state combined with weak heuristic
                    // signals.
                    if (!wasAlreadyAnalyzingWhite)
                    {
                        _externalOrientationLockedForCurrentGame = false;
                        _externalTrackedPositionCount = 0;
                        _orientationConfirmStreakCount = 0;
                    }

                    if (!_isTracking)
                    {
                        if (!TryEnableOverlayTrackingForUserAction("analysis"))
                        {
                            _settingsToolbar?.SyncAnalysisState("OFF");
                            return;
                        }
                    }
                    if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                    {
                        RememberPendingLiveAnalysisAfterEngineStart(isBoth: false, blackPerspective: false);
                        return;
                    }

                    if (_stockfish != null)
                    {
                        if (!_continuousAnalysisEnabled || _analysisIsBlackPerspective)
                        {
                            ToggleContinuousAnalysis(false);
                        }
                        else
                        {
                            ResyncContinuousAnalysis(false);
                        }
                    }
                }
                else
                {
                    _analysisBothEnabled = false;
                    if (_continuousAnalysisEnabled && !_analysisIsBlackPerspective)
                    {
                        ToggleContinuousAnalysis(false);
                    }
                }
                break;

            case "AnalysisBlack":
                bool enableBlack = (bool)value;
                if (enableBlack)
                {
                    bool wasAlreadyAnalyzingBlack = _continuousAnalysisEnabled
                        && _analysisIsBlackPerspective
                        && !_analysisBothEnabled;
                    _analysisBothEnabled = false;
                    if (_stockfish != null) _stockfish.ClearAllDepthTracking();

                    // Only unlock orientation if the user is actually changing
                    // perspective. See AnalysisWhite handler for rationale.
                    if (!wasAlreadyAnalyzingBlack)
                    {
                        _externalOrientationLockedForCurrentGame = false;
                        _externalTrackedPositionCount = 0;
                        _orientationConfirmStreakCount = 0;
                    }

                    if (!_isTracking)
                    {
                        if (!TryEnableOverlayTrackingForUserAction("analysis"))
                        {
                            _settingsToolbar?.SyncAnalysisState("OFF");
                            return;
                        }
                    }
                    if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                    {
                        RememberPendingLiveAnalysisAfterEngineStart(isBoth: false, blackPerspective: true);
                        return;
                    }

                    if (_stockfish != null)
                    {
                        if (!_continuousAnalysisEnabled || !_analysisIsBlackPerspective)
                        {
                            ToggleContinuousAnalysis(true);
                        }
                        else
                        {
                            ResyncContinuousAnalysis(true);
                        }
                    }
                }
                else
                {
                    _analysisBothEnabled = false;
                    if (_continuousAnalysisEnabled && _analysisIsBlackPerspective)
                    {
                        ToggleContinuousAnalysis(true);
                    }
                }
                break;

            case "AnalysisBoth":
                bool enableBoth = (bool)value;
                if (enableBoth)
                {
                    bool wasAlreadyAnalyzingBoth = _analysisBothEnabled && _continuousAnalysisEnabled;
                    _analysisBothEnabled = true;
                    if (_stockfish != null) _stockfish.ClearAllDepthTracking();

                    // Only unlock orientation if the user is actually changing
                    // perspective. See AnalysisWhite handler for rationale.
                    if (!wasAlreadyAnalyzingBoth)
                    {
                        _externalOrientationLockedForCurrentGame = false;
                        _externalTrackedPositionCount = 0;
                        _orientationConfirmStreakCount = 0;
                    }

                    if (!_isTracking)
                    {
                        if (!TryEnableOverlayTrackingForUserAction("analysis"))
                        {
                            _settingsToolbar?.SyncAnalysisState("OFF");
                            return;
                        }
                    }

                    if (WarnIfLiveAnalysisEngineUnavailableForUserAction())
                    {
                        RememberPendingLiveAnalysisAfterEngineStart(isBoth: true, blackPerspective: _analysisIsBlackPerspective);
                        return;
                    }

                    bool desiredBlackPerspective = _currentFenIsAnalysisBoard
                        ? false
                        : GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
                    ToggleContinuousAnalysis(desiredBlackPerspective);
                }
                else
                {
                    _analysisBothEnabled = false;
                    if (_continuousAnalysisEnabled)
                    {
                        ToggleContinuousAnalysis(_analysisIsBlackPerspective);
                    }
                }
                break;

            case "BoardFlipped":
                _boardIsFlipped = (bool)value;
                Log($"[Settings] Board flipped: {_boardIsFlipped}");

                if (_stockfish != null) _stockfish.ClearAllDepthTracking();
                BumpAnalysisSessionVersion();
                _analysisInProgress = false;
                _currentMoveArrows = null;
                _lastAnalysisVariations = null;
                _lastArrowSourceFEN = "";
                ClearExternalArrows();
                ResetAnalysisSchedulingState();
                if (!IsActiveAnalysisBoardFen(_currentFEN))
                {
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                }
                break;

            case "InitialDepth":
                if (_stockfish != null)
                {
                    _stockfish.InitialDepth = (int)value;
                    Log($"[Settings] Engine initial depth changed: {value} (confirmed: {_stockfish.InitialDepth})");
                }
                break;

            case "MaxDepth":
                if (_bulletProfileEnabled)
                {
                    // The Bullet profile owns the live depth while active;
                    // remember the slider value so disabling the profile
                    // restores what the user chose, not a stale stash. Runs
                    // even with no live engine (deferred startup / failed
                    // switch) - the stash must track the slider regardless.
                    _bulletProfileStashedDepth = BuildLimits.ClampDepth((int)value);
                    Log($"[Settings] Engine depth stashed while Bullet profile active: {_bulletProfileStashedDepth}");
                }
                else if (_stockfish != null)
                {
                    int depth = BuildLimits.ClampDepth((int)value);
                    _stockfish.MaxDepth = depth;
                    ApplyLiveEngineSetting(_ => Task.CompletedTask, $"Engine depth changed: {depth} (confirmed: {_stockfish.MaxDepth})", clearDepthTracking: true);
                }
                break;

            case "InfiniteAnalysis":
                if (_stockfish != null)
                {
                    bool enabled = value is bool infiniteEnabled && infiniteEnabled;
                    _stockfish.InfiniteAnalysis = enabled;
                    ApplyLiveEngineSetting(_ => Task.CompletedTask, $"Infinite analysis changed: {enabled}", clearDepthTracking: true);
                }
                break;

            case "EngineThreads":
                if (_stockfish != null)
                {
                    int threads = BuildLimits.ClampThreads((int)value);
                    ApplyLiveEngineSetting(
                        engine => engine.SendCommandAsync($"setoption name Threads value {threads}"),
                        $"Engine threads set to: {threads}",
                        clearDepthTracking: true);
                }
                break;

            case "ArrowCount":
                int oldCount = _maxArrowCount;
                _maxArrowCount = BuildLimits.ClampLines((int)value);
                Log($"[Settings] Max arrows changed: {oldCount} -> {_maxArrowCount}");
                if (_stockfish != null)
                {
                    int multipv = GetLiveAnalysisMultiPvCount();
                    ScheduleArrowCountEngineSetting(multipv);
                }

                if (!_liveEngineSettingsInFlight && _lastAnalysisVariations != null && IsActiveAnalysisBoardFen(_currentFEN) && _analysisBoardForm != null)
                {
                    var limitedVariations = _lastAnalysisVariations.Take(_maxArrowCount).ToList();
                    _analysisBoardForm.BeginInvoke(new Action(() =>
                        _analysisBoardForm.SetAnalysisVariations(limitedVariations, _analysisIsBlackPerspective, limitedVariations.FirstOrDefault()?.Depth ?? 0)));
                    Log($"[Settings] Updated analysis board lines to show {limitedVariations.Count} variations");
                }
                else if (!_liveEngineSettingsInFlight && _engineLinesEnabled && _engineLines != null && _lastAnalysisVariations != null)
                {
                    var limitedVariations = _lastAnalysisVariations.Take(_maxArrowCount).ToList();
                    _engineLines.UpdateVariations(limitedVariations, _analysisIsBlackPerspective);
                    Log($"[Settings] Updated engine lines to show {limitedVariations.Count} variations");
                }

                if (!_liveEngineSettingsInFlight && _showingMoves && _currentMoveArrows != null && _overlay != null)
                {
                    var limitedArrows = _currentMoveArrows.Take(_maxArrowCount).ToList();
                    if (_lastTrackedBox.HasValue && limitedArrows.Any() && CanDisplayArrowsForCurrentState())
                    {
                        var r = _lastTrackedBox.Value;
                        _overlay.BeginInvoke(new Action(() =>
                        {
                            if (CanDisplayArrowsForCurrentState())
                            {
                int generation = Interlocked.Increment(ref _arrowDisplayGeneration);
                                _overlay.ShowMoveArrows(new Rectangle(r.X, r.Y, r.Width, r.Height), limitedArrows, generation, 60000);
                                if (!_currentFenIsAnalysisBoard && !IsActiveAnalysisBoardFen(_currentFEN))
                                    RememberExternalOverlayArrowsShown(string.IsNullOrWhiteSpace(_lastArrowSourceFEN) ? _currentFEN : _lastArrowSourceFEN, limitedArrows.Count);
                                Log($"[Settings] Updated displayed arrows to show {limitedArrows.Count} arrows");
                            }
                        }));
                    }
                }
                break;

            case "CoachModeEnabled":
                _coachModeEnabled = value is bool coachEnabled && coachEnabled;
                _settingsToolbar?.SyncCoachModeState(_coachModeEnabled);
                Log($"[Settings] Coach mode {(_coachModeEnabled ? "enabled" : "disabled")}");
                if (_stockfish != null)
                    ScheduleArrowCountEngineSetting(GetLiveAnalysisMultiPvCount());

                if (_coachModeEnabled)
                {
                    ClearActiveArrows();
                    if (_continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                        TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                }
                else if (_continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                {
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                }
                else
                {
                    ClearActiveArrows();
                }
                break;

            case "CoachLevel":
                _coachLevel = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 10);
                Log($"[Settings] Coach level changed: {_coachLevel}");
                if (_coachModeEnabled && _continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                break;

            case "CoachMarkCount":
                _coachMarkCount = Math.Clamp(Convert.ToInt32(value, CultureInfo.InvariantCulture), 1, 3);
                Log($"[Settings] Coach marks changed: {_coachMarkCount}");
                if (_stockfish != null)
                    ScheduleArrowCountEngineSetting(GetLiveAnalysisMultiPvCount());
                if (_coachModeEnabled && _continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                break;

            case "CoachCardEnabled":
                _coachCardEnabled = value is bool coachCardEnabled && coachCardEnabled;
                Log($"[Settings] Coach card {(_coachCardEnabled ? "enabled" : "disabled")}");
                if (_coachModeEnabled && _continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                    TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                break;

            case "HashSize":
                if (_stockfish != null)
                {
                    int hash = Math.Clamp((int)value, 16, BuildLimits.MaxHashMb);
                    ApplyLiveEngineSetting(
                        engine => engine.SendCommandAsync($"setoption name Hash value {hash}"),
                        $"Hash size set to: {hash} MB",
                        clearDepthTracking: true);
                }
                break;

            case "ShowEvalBar":
                _evalBarEnabled = (bool)value;
                if (_evalBar != null)
                {
                    _evalBar.SetEnabled(_evalBarEnabled && _isTracking);
                }
                if (_evalBarEnabled)
                {
                    // Eval bar needs analysis running to have anything to
                    // display. If no perspective is currently selected
                    // (W/B/W+B all off), auto-enable W+B since the eval
                    // bar is a position-quality indicator, not a side-
                    // specific one. The user can change perspective later.
                    bool noPerspectiveSelected =
                        !_continuousAnalysisEnabled && !_analysisBothEnabled;
                    if (noPerspectiveSelected && _isTracking)
                    {
                        Log("[Settings] Eval bar enabled with no analysis - auto-enabling W+B");
                        _settingsToolbar?.SyncAnalysisState("BOTH");
                        // Reuse the AnalysisBoth handler so all the side
                        // effects (orientation reset, ToggleContinuousAnalysis,
                        // depth tracking reset) run consistently.
                        HandleSettingChanged("AnalysisBoth", true);
                    }

                    // Try to populate the eval bar with the most recent
                    // analysis we have. Without this, the bar shows 0.0
                    // until the next FEN change triggers a new analysis
                    // (because TryQueueAnalysis short-circuits when arrows
                    // are already cached for the current position).
                    bool cacheMatchesCurrent =
                        _lastAnalysisVariations != null &&
                        _lastAnalysisVariations.Any() &&
                        !string.IsNullOrEmpty(_lastArrowSourceFEN) &&
                        IsSameArrowSourcePosition(_currentFEN);

                    if (cacheMatchesCurrent)
                    {
                        var bestVar = _lastAnalysisVariations!.First();
                        double cachedEval = bestVar.Score;
                        bool cachedIsMate = bestVar.ScoreType == "mate";
                        int cachedMateIn = bestVar.MateIn ?? 0;
                        if (_analysisIsBlackPerspective)
                        {
                            cachedEval = -cachedEval;
                            cachedMateIn = -cachedMateIn;
                        }
                        _lastEvaluation = cachedEval;
                        _evalBar?.UpdateEvaluation(cachedEval, cachedIsMate, cachedMateIn);
                        Log($"[Settings] Eval bar populated from cached analysis: {cachedEval:F2}");
                    }
                    else if (_continuousAnalysisEnabled && !string.IsNullOrEmpty(_currentFEN) && _isTracking)
                    {
                        // No cached value matching current position - kick
                        // off a fresh analysis. force:true bypasses the
                        // cached-arrows short-circuit. The eval bar will
                        // populate when the analysis result is applied.
                        TryQueueAnalysis(_analysisIsBlackPerspective, force: true);
                        Log($"[Settings] Eval bar enabled - forcing fresh analysis for current position");
                    }
                }
                Log($"[Settings] Eval bar toggled: {_evalBarEnabled}");
                break;

            case "EvalDisplayMode":
                if (value is EvalDisplayMode evalDisplayMode)
                {
                    _evalDisplayMode = evalDisplayMode;
                    _evalBar?.SetDisplayMode(_evalDisplayMode);

                    var settings = _appSettingsManager.Load();
                    settings.EvalDisplayMode = _evalDisplayMode;
                    _appSettingsManager.Save(settings);

                    if (_evalBarEnabled && _lastTrackedBox.HasValue)
                    {
                        var r = _lastTrackedBox.Value;
                        _evalBar?.UpdatePosition(new Rectangle(r.X, r.Y, r.Width, r.Height));
                        _evalBar?.UpdateEvaluation(_lastEvaluation);
                    }

                    Log($"[Settings] Eval display mode changed: {_evalDisplayMode}");
                }
                break;

            case "ShowEngineLines":
                _engineLinesEnabled = (bool)value;
                if (_engineLines != null)
                {
                    _engineLines.SetEnabled(_engineLinesEnabled && _isTracking);
                }
                Log($"[Settings] Engine lines toggled: {_engineLinesEnabled}");
                break;

            case "SpeculativeAnalysisEnabled":
                _speculativeAnalysisEnabled = (bool)value;
                Log($"[Settings] Speculative analysis: {(_speculativeAnalysisEnabled ? "enabled" : "disabled")}");
                RefreshDebugView("Speculative analysis setting changed");
                break;

            case "SpeculativeAnalysisMode":
                _speculativeAnalysisMode = (SpeculativeAnalysisMode)value;
                Log($"[Settings] Speculative mode: {_speculativeAnalysisMode}");
                RefreshDebugView("Speculative mode changed");
                break;

            case "BlitzMode":
                _blitzModeSetting = (BlitzModeSetting)value;
                if (_blitzModeSetting != BlitzModeSetting.Auto)
                    ResetBlitzAutoDetection($"manual {_blitzModeSetting}");
                Log($"[Settings] Blitz mode: {_blitzModeSetting}");
                RefreshDebugView("Blitz mode changed");
                break;

            case "BulletProfile":
                _bulletProfileEnabled = value is bool bulletProfileEnabled && bulletProfileEnabled;
                Log($"[Settings] Bullet profile: {(_bulletProfileEnabled ? "enabled" : "disabled")}");
                if (_stockfish != null)
                {
                    if (_bulletProfileEnabled)
                    {
                        // Depth 6 keeps each MultiPV-10 pass short enough to land
                        // within a bullet move's think time. The MaxDepth WRITE is
                        // unconditional: it is inert while infinite analysis is
                        // active (every depth read branches on InfiniteAnalysis)
                        // but becomes correct the instant infinite ends - skipping
                        // it left the engine at slider depth x MultiPV 10, slower
                        // than either mode. Only the live-engine churn is gated.
                        _bulletProfileStashedDepth = _stockfish.MaxDepth;
                        _stockfish.MaxDepth = BulletProfileDepth;
                        if (!_stockfish.InfiniteAnalysis)
                        {
                            ApplyLiveEngineSetting(_ => Task.CompletedTask, $"Bullet profile depth applied: {BulletProfileDepth} (stashed {_bulletProfileStashedDepth})", clearDepthTracking: true);
                        }
                    }
                    else
                    {
                        // Prefer the depth stashed at enable time; a profile
                        // persisted from a previous session has no stash, so fall
                        // back to the toolbar slider - disable must always land
                        // on the user's chosen depth. Unconditional write for the
                        // same reason as the enable path: without it, disabling
                        // while infinite was active stranded depth 6 forever.
                        int restoreDepth = _bulletProfileStashedDepth > 0
                            ? BuildLimits.ClampDepth(_bulletProfileStashedDepth)
                            : BuildLimits.ClampDepth(_settingsToolbar?.GetMaxDepth() ?? BuildLimits.MaxDepth);
                        _bulletProfileStashedDepth = -1;
                        _stockfish.MaxDepth = restoreDepth;
                        if (!_stockfish.InfiniteAnalysis)
                        {
                            ApplyLiveEngineSetting(_ => Task.CompletedTask, $"Bullet profile depth restored: {restoreDepth}", clearDepthTracking: true);
                        }
                    }

                    // Re-assert MultiPV exactly like the ArrowCount case; the
                    // generation counters make this supersede the depth reset
                    // above cleanly.
                    ScheduleArrowCountEngineSetting(GetLiveAnalysisMultiPvCount());
                }
                else if (!_bulletProfileEnabled)
                {
                    _bulletProfileStashedDepth = -1;
                }
                RefreshDebugView("Bullet profile changed");
                break;

            case "EngineChanged":
                string newEnginePath = (string)value;
                if (IsUsableEnginePath(newEnginePath))
                {
                    Log($"[Settings] Switching engine to: {Path.GetFileName(newEnginePath)}");
                    _stockfishPath = newEnginePath;
                    LogCurrentEngine("Current engine");

                    bool resumeLiveAnalysis = _continuousAnalysisEnabled;
                    int switchGeneration = Interlocked.Increment(ref _liveEngineSettingsGeneration);

                    _liveEngineSettingsInFlight = true;
                    BumpAnalysisSessionVersion();
                    _analysisInProgress = false;
                    _lastQueuedAnalysisKey = "";
                    ResetAnalysisSchedulingState();
                    ClearDisplayedArrowDepthMemory();
                    _lastAssertedLiveMultiPvEngine = null;
                    _lastAssertedLiveMultiPv = -1;
                    SuppressForegroundBoardSwitchesBriefly("engine switch requested");

                    _analysisTimer?.Dispose();
                    _analysisTimer = null;

                    if (resumeLiveAnalysis)
                    {
                        ClearActiveArrows();
                        _showingMoves = false;
                    }

                    // Dispose old engine
                    var oldEngine = _stockfish;
                    _stockfish = null;
                    try { oldEngine?.Dispose(); } catch { }
                    _analysisBoardController!.OnLiveEngineSwitched();

                    // Create new engine instance
                    Task.Run(async () =>
                    {
                        UCIEngine? selectedEngine = null;
                        try
                        {
                            selectedEngine = new UCIEngine(newEnginePath);
                            selectedEngine.InitialDepth = _settingsToolbar?.GetInitialDepth() ?? 8;
                            selectedEngine.MaxDepth = GetLiveEngineConfiguredMaxDepth();
                            selectedEngine.InfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && (_settingsToolbar?.GetInfiniteAnalysis() ?? false);
                            selectedEngine.InitialThinkTime = 50;
                            selectedEngine.MaxThinkTime = 2000;
                            selectedEngine.TimeIncrement = 100;
                            selectedEngine.DepthIncrement = 2;

                            // Apply ELO settings if needed
                            ApplyEngineSpecificSettings(selectedEngine);

                            ShowLiveEngineStartupFeedback(newEnginePath, Path.GetFileNameWithoutExtension(newEnginePath));
                            if (await selectedEngine.StartAsync())
                            {
                                if (switchGeneration != Volatile.Read(ref _liveEngineSettingsGeneration))
                                {
                                    selectedEngine.Dispose();
                                    if (ReferenceEquals(_stockfish, selectedEngine))
                                        _stockfish = null;
                                    return;
                                }

                                _stockfish = selectedEngine;
                                // Apply settings
                                int threads = _settingsToolbar?.GetEngineThreads() ?? 4;
                                int hash = _settingsToolbar?.GetHashSize() ?? 128;

                                await selectedEngine.SendCommandAsync($"setoption name Threads value {threads}");
                                await selectedEngine.SendCommandAsync($"setoption name Hash value {hash}");
                                await selectedEngine.SendCommandAsync($"setoption name MultiPV value {GetLiveAnalysisMultiPvCount()}");

                                Log($"[Settings] Successfully switched to {Path.GetFileName(newEnginePath)}");
                                ShowLiveEngineReadyFeedback(newEnginePath, Path.GetFileNameWithoutExtension(newEnginePath));
                                SuppressForegroundBoardSwitchesBriefly("engine switch completed");

                                if (resumeLiveAnalysis && _isTracking && _continuousAnalysisEnabled)
                                {
                                    bool currentBothMode = _analysisBothEnabled;
                                    bool desiredBlackPerspective = currentBothMode &&
                                        !_currentFenIsAnalysisBoard &&
                                        !string.IsNullOrWhiteSpace(_currentFEN)
                                            ? GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective)
                                            : _analysisIsBlackPerspective;

                                    Log($"[Settings] Restarting live analysis after engine switch ({(currentBothMode ? "BOTH" : (desiredBlackPerspective ? "BLACK" : "WHITE"))})");
                                    ResyncContinuousAnalysis(desiredBlackPerspective);
                                }
                            }
                            else
                            {
                                string failureMessage = GetEngineFailureMessage(selectedEngine, $"Failed to start {Path.GetFileName(newEnginePath)}");
                                Log($"[Settings] {failureMessage}");
                                ShowLiveEngineFailureFeedback(newEnginePath);
                                if (resumeLiveAnalysis && IsPrivateEngineStartupBlocked(newEnginePath, failureMessage))
                                    RevertLiveAnalysisAfterPrivateEngineLicenseFailure(failureMessage);
                                selectedEngine.Dispose();
                                if (ReferenceEquals(_stockfish, selectedEngine))
                                    _stockfish = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[Settings] Error switching engine: {ex.Message}");
                            if (selectedEngine != null)
                            {
                                if (ReferenceEquals(_stockfish, selectedEngine))
                                    _stockfish = null;
                                try { selectedEngine.Dispose(); } catch { }
                            }
                            else
                            {
                                _stockfish = null;
                            }
                        }
                        finally
                        {
                            if (switchGeneration == Volatile.Read(ref _liveEngineSettingsGeneration))
                            {
                                _liveEngineSettingsInFlight = false;

                                if (resumeLiveAnalysis && _stockfish == null)
                                {
                                    lock (_analysisLock)
                                    {
                                        _continuousAnalysisEnabled = false;
                                        _analysisBothEnabled = false;
                                        _analysisInProgress = false;
                                        _analysisTimer?.Dispose();
                                        _analysisTimer = null;
                                        ResetAnalysisSchedulingState();
                                    }

                                    ClearActiveArrows();
                                    _settingsToolbar?.SyncAnalysisState("OFF");
                                    RefreshDebugView("Engine switch failed");
                                }
                            }
                        }
                    });
                }
                break;

            case "EloLimitEnabled":
                bool eloEnabled = (bool)value;
                _eloLimitEnabled = eloEnabled;
                if (_stockfish != null)
                {
                    string currentEnginePath = _stockfish.GetEnginePath();

                    Task.Run(async () =>
                    {
                        try
                        {
                            Log($"[Settings] Stopping engine for ELO change...");

                            // Stop any ongoing analysis first
                            if (_continuousAnalysisEnabled)
                            {
                                _continuousAnalysisEnabled = false;
                                _analysisTimer?.Dispose();
                                _analysisTimer = null;

                                if (_overlay != null)
                                {
                                    Interlocked.Increment(ref _arrowDisplayGeneration);
                                    int generation = _arrowDisplayGeneration;
                                    _overlay.BeginInvoke(new Action(() =>
                                    {
                                        _overlay.HideArrows(generation, preserveFreeLimitWatermark: false);
                                        _showingMoves = false;
                                    }));
                                }
                            }

                            // Dispose the old engine
                            _stockfish.Dispose();
                            _stockfish = null;

                            await Task.Delay(500);

                            // Create new engine
                            var newEngine = new UCIEngine(currentEnginePath);
                            newEngine.InitialDepth = _settingsToolbar?.GetInitialDepth() ?? 8;
                            newEngine.MaxDepth = GetLiveEngineConfiguredMaxDepth();
                            newEngine.InfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && (_settingsToolbar?.GetInfiniteAnalysis() ?? false);
                            newEngine.InitialThinkTime = 50;
                            newEngine.MaxThinkTime = 2000;
                            newEngine.TimeIncrement = 100;
                            newEngine.DepthIncrement = 2;

                            newEngine.EloLimitEnabled = eloEnabled;
                            newEngine.MaxEloRating = _maxEloRating;
                            newEngine.AdaptiveHuman = _humanAdaptiveEnabled;
                            newEngine.HumanPlayProfile = _humanPlayProfile;

                            Log($"[Settings] Starting engine with ELO limit: {eloEnabled} at {_maxEloRating}");

                            ShowLiveEngineStartupFeedback(currentEnginePath, Path.GetFileNameWithoutExtension(currentEnginePath));
                            if (await newEngine.StartAsync())
                            {
                                // Only assign if startup was successful
                                _stockfish = newEngine;
                                ShowLiveEngineReadyFeedback(currentEnginePath, Path.GetFileNameWithoutExtension(currentEnginePath));

                                // Apply other settings - but check if still valid
                                if (_stockfish != null)
                                {
                                    int threads = _settingsToolbar?.GetEngineThreads() ?? 4;
                                    int hash = _settingsToolbar?.GetHashSize() ?? 128;

                                    await _stockfish.SendCommandAsync($"setoption name Threads value {threads}");
                                    await _stockfish.SendCommandAsync($"setoption name Hash value {hash}");

                                    // MultiPV might already be set in StartAsync for ELO mode
                                    if (!eloEnabled)
                                    {
                                        await _stockfish.SendCommandAsync($"setoption name MultiPV value {GetLiveAnalysisMultiPvCount()}");
                                    }

                                    Log($"[Settings] Engine restarted successfully with ELO: {(eloEnabled ? $"limited to {_maxEloRating}" : "unlimited")}");
                                }
                            }
                            else
                            {
                                string failureMessage = GetEngineFailureMessage(newEngine, "Failed to start engine.");
                                Log($"[Settings] ERROR: {failureMessage}");
                                ShowLiveEngineFailureFeedback(currentEnginePath);
                                if (IsPrivateEngineStartupBlocked(currentEnginePath, failureMessage))
                                    RevertLiveAnalysisAfterPrivateEngineLicenseFailure(failureMessage);
                                newEngine.Dispose();
                                _stockfish = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[Settings] ERROR restarting engine: {ex.Message}");
                            _stockfish = null;
                        }
                    });
                }
                break;

            case "MaxEloRating":
                int maxElo = (int)value;
                _maxEloRating = maxElo;
                if (_stockfish != null && _eloLimitEnabled)
                {
                    string currentEnginePath = _stockfish.GetEnginePath();

                    Task.Run(async () =>
                    {
                        try
                        {
                            Log($"[Settings] Restarting engine with max ELO: {maxElo}");

                            // Stop any ongoing analysis
                            if (_continuousAnalysisEnabled)
                            {
                                _continuousAnalysisEnabled = false;
                                _analysisTimer?.Dispose();
                                _analysisTimer = null;

                                if (_overlay != null)
                                {
                                    Interlocked.Increment(ref _arrowDisplayGeneration);
                                    int generation = _arrowDisplayGeneration;
                                    _overlay.BeginInvoke(new Action(() =>
                                    {
                                        _overlay.HideArrows(generation, preserveFreeLimitWatermark: false);
                                        _showingMoves = false;
                                    }));
                                }
                            }

                            _stockfish.Dispose();
                            _stockfish = null;

                            await Task.Delay(500);

                            var newEngine = new UCIEngine(currentEnginePath);
                            newEngine.InitialDepth = _settingsToolbar?.GetInitialDepth() ?? 8;
                            newEngine.MaxDepth = GetLiveEngineConfiguredMaxDepth();
                            newEngine.InfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && (_settingsToolbar?.GetInfiniteAnalysis() ?? false);
                            newEngine.InitialThinkTime = 50;
                            newEngine.MaxThinkTime = 2000;
                            newEngine.TimeIncrement = 100;
                            newEngine.DepthIncrement = 2;

                            newEngine.EloLimitEnabled = true;
                            newEngine.MaxEloRating = maxElo;
                            newEngine.AdaptiveHuman = _humanAdaptiveEnabled;
                            newEngine.HumanPlayProfile = _humanPlayProfile;

                            ShowLiveEngineStartupFeedback(currentEnginePath, Path.GetFileNameWithoutExtension(currentEnginePath));
                            if (await newEngine.StartAsync())
                            {
                                _stockfish = newEngine;
                                ShowLiveEngineReadyFeedback(currentEnginePath, Path.GetFileNameWithoutExtension(currentEnginePath));

                                if (_stockfish != null)
                                {
                                    int threads = _settingsToolbar?.GetEngineThreads() ?? 4;
                                    int hash = _settingsToolbar?.GetHashSize() ?? 128;

                                    await _stockfish.SendCommandAsync($"setoption name Threads value {threads}");
                                    await _stockfish.SendCommandAsync($"setoption name Hash value {hash}");

                                    // MultiPV already set to 1 in StartAsync for ELO mode

                                    Log($"[Settings] Engine restarted with max ELO: {maxElo}");
                                }
                            }
                            else
                            {
                                string failureMessage = GetEngineFailureMessage(newEngine, "Failed to start engine.");
                                Log($"[Settings] ERROR: {failureMessage}");
                                ShowLiveEngineFailureFeedback(currentEnginePath);
                                if (IsPrivateEngineStartupBlocked(currentEnginePath, failureMessage))
                                    RevertLiveAnalysisAfterPrivateEngineLicenseFailure(failureMessage);
                                newEngine.Dispose();
                                _stockfish = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"[Settings] ERROR restarting engine: {ex.Message}");
                            _stockfish = null;
                        }
                    });
                }
                else if (!_eloLimitEnabled)
                {
                    Log($"[Settings] Max ELO set to {maxElo} (will apply when enabled)");
                }
                break;

            case "HumanAdaptiveEnabled":
                _humanAdaptiveEnabled = (bool)value;
                Log($"[Settings] Human adaptive mode: {(_humanAdaptiveEnabled ? "enabled" : "disabled")}");
                if (_stockfish != null && IsHumanEnginePath(_stockfish.GetEnginePath()))
                {
                    bool adaptive = _humanAdaptiveEnabled;
                    Task.Run(async () => await _stockfish.SendCommandAsync($"setoption name AdaptiveHuman value {(adaptive ? "true" : "false")}"));
                }
                break;

            case "HumanPlayProfile":
                _humanPlayProfile = (HumanPlayProfile)value;
                Log($"[Settings] Human play profile: {_humanPlayProfile}");
                if (_stockfish != null && IsHumanEnginePath(_stockfish.GetEnginePath()))
                {
                    string profile = _humanPlayProfile.ToString().ToLowerInvariant();
                    Task.Run(async () => await _stockfish.SendCommandAsync($"setoption name PlayProfile value {profile}"));
                }
                break;
        }
    }

    // Rest of the methods remain the same...
    // (ValidateCastlingRights, SmoothBoardPosition, DetectWhoMoved, ParseBoard, Rotate180, 
    // HandlePositionChange, AnalyzePosition, GetBoardPosition, ToggleContinuousAnalysis,
    // HandleKeyPress, UpdatePerformanceMetrics, PrintInstructions)

    // I'll continue with the remaining methods for completeness:

}
