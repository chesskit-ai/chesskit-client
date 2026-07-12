using ChessKit;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Globalization;
using static ChessKit.BoardGeometry;

partial class Program
{

    [STAThread]
    static async Task Main(string[] args)
    {
        // Pin the entire app to US English so numbers (e.g. the toolbar's
        // "FPS: 0.5") format with a period decimal separator regardless of the
        // operating system's regional settings.
        var enUsCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = enUsCulture;
        CultureInfo.DefaultThreadCurrentUICulture = enUsCulture;
        CultureInfo.CurrentCulture = enUsCulture;
        CultureInfo.CurrentUICulture = enUsCulture;

        bool createdNew = false;

        // Last-resort crash capture: a fatal exception on any thread (incl. the
        // overlay UI thread and background tasks) is written to the runtime log
        // + a dedicated crash.log before the process dies, so a "did it crash or
        // did I close it" question is answerable from the logs. Best-effort only.
        InstallGlobalCrashHandlers();

        try
        {
#if DEBUG
            BuildLimits.SetDebugFreeEditionOverride(args.Any(IsDebugFreeEditionArgument));
#endif
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                ShowSingleInstancePromptAndExit();
                return;
            }

            // Always-on, low-volume lifecycle evidence. Unlike runtime.log this
            // has no hot-path writes and also falls back to LocalAppData when the
            // copied EXE directory is not writable.
            CrashDiagnostics.Initialize();

#if DEBUG
            DebugRuntime.Initialize();
#else
            InitializeReleaseRuntimeLog();
#endif
            // Raise the OS timer resolution to 1ms so the tracking loop's frame
            // pacing (Task.Delay) is not quantized to the ~15.6ms default, which
            // otherwise halves the effective capture cadence and adds jitter to
            // move->arrow latency. Reverted in the shutdown path below.
            EnableHighResolutionTimer();

            // Enable per-monitor DPI awareness
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }

            HotkeyBindings initialHotkeys;
            try
            {
                var appSettings = _appSettingsManager.Load();
                initialHotkeys = appSettings.Hotkeys?.Clone() ?? new HotkeyBindings();
                initialHotkeys.Normalize();
                _settingsToolbarHidden = appSettings.SettingsToolbarHidden;
                _evalDisplayMode = appSettings.EvalDisplayMode;
            }
            catch
            {
                initialHotkeys = new HotkeyBindings();
                _settingsToolbarHidden = false;
            }

            // Owns the hotkey input layer (listeners, bindings, dedup, key->command
            // resolution). Constructed before the UI/overlay exist; the overlay
            // accessor is a lazy delegate, so it is safe to capture _overlay here.
            _hotkeyController = new HotkeyController(
                initialBindings: initialHotkeys,
                isStartupComplete: () => _startupComplete,
                getOverlay: () => _overlay,
                log: Log);
            // The controller raises CommandTriggered once a key resolves to a
            // command and clears startup + dedup gating; the heavy action dispatcher
            // (touching analysis/arrows/tracking) stays in Program.
            _hotkeyController.CommandTriggered += HandleHotkeyCommand;

            // Owns the full-version license gate (startup verification + background
            // monitor). Always constructed: there is one runtime-gated build, and
            // the gate decides Free vs Licensed at runtime. The overlay accessor is
            // a lazy delegate, so it is safe to capture _overlay here before the UI
            // exists, exactly as HotkeyController does. The runtime-state teardown and
            // the shared failure-notice slot stay in Program and are injected.
            _licenseEnforcer = new LicenseEnforcer(
                log: Log,
                tryCopyTextToClipboard: TryCopyTextToClipboard,
                closeStartupStatus: CloseStartupStatus,
                getOverlay: () => _overlay,
                invalidateRuntimeState: InvalidateFullVersionLicenseRuntimeState,
                exchangeFailureNoticeShown: () => Interlocked.Exchange(ref _licenseFailureNoticeShown, 1),
                promptServerUnreachable: PromptLicenseServerUnreachable);

            // Wire the runtime edition gate to the sticky "license verified" flag.
            // Until EnforceAsync runs (or in a Debug forced-Free override) this
            // reports false, so the app is the limited Free Edition by default.
            BuildLimits.SetLicensedAccessor(HasVerifiedFullVersionLicense);

            CleanupOrphanedEngineProcesses(Path.Combine(AppContext.BaseDirectory, "engines"));

            var initialEnginePath = ResolveInitialEnginePath();
            _stockfishPath = initialEnginePath;
            LogCurrentEngine("Current engine");

            // Initialize overlay forms
            using var uiPumpReady = new ManualResetEvent(false);
            var uiThread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    _overlay = new OverlayForm();
                    _evalBar = new EvalBarForm();
                    _evalBar.SetDisplayMode(_evalDisplayMode);
                    _engineLines = new EngineLinesForm();
                    _settingsToolbar = new SettingsToolbarForm();
                    _hotkeyController!.CreateRegisteredListener();
                    _analysisBoardForm = new AnalysisBoardForm();
                    _gameAnalysisForm = new GameAnalysisForm();
                    _orientationPromptHost = new OrientationPromptHost();
                    _analysisBoardController = CreateAnalysisBoardController();
                    _analysisBoardForm.SnapshotChanged += UpdateAnalysisBoardSnapshot;
                    _analysisBoardForm.AnalysisModeChanged += _analysisBoardController.HandleAnalysisBoardAnalysisModeChanged;
                    _analysisBoardForm.MirrorModeChanged += _analysisBoardController.HandleAnalysisBoardMirrorModeChanged;
                    _analysisBoardForm.AnalysisSettingsChanged += _analysisBoardController.HandleAnalysisBoardAnalysisSettingsChanged;
                    _analysisBoardForm.MatchSettingsChanged += _analysisBoardController.HandleAnalysisBoardMatchSettingsChanged;
                    _analysisBoardForm.MatchCommandRequested += _analysisBoardController.HandleAnalysisBoardMatchCommandRequested;
                    _analysisBoardForm.GameAnalysisRequested += _analysisBoardController.HandleGameAnalysisRequested;
                    _gameAnalysisForm.AnalyzeRequested += _analysisBoardController.AnalyzeGameAsync;
                    _gameAnalysisForm.AnalysisCompleted += _analysisBoardController.HandleGameAnalysisCompleted;
                    _gameAnalysisForm.MoveSelected += _analysisBoardController.HandleGameAnalysisMoveSelected;
                    _analysisBoardController.HandleAnalysisBoardAnalysisSettingsChanged(_analysisBoardForm.GetAnalysisSettings());
                    _analysisBoardController.HandleAnalysisBoardMatchSettingsChanged(_analysisBoardForm.GetMatchSettings());
                    _orientationPromptHost.DirectionChosen += OnOrientationPromptDirectionChosen;
                    _orientationPromptHost.Dismissed += OnOrientationPromptDismissed;

                    // Subscribe to settings changes from toolbar
                    _settingsToolbar.SettingChanged += HandleSettingChanged;
                    BoardVisionDetector.ConnectionStateChanged += state =>
                    {
                        try { _settingsToolbar?.SyncVisionConnectionState(state); } catch { }
                    };

                    // Sync initial states
                    _settingsToolbar.SyncEvalBarState(_evalBarEnabled);
                    _settingsToolbar.SyncEngineLinesState(_engineLinesEnabled);
                    _settingsToolbar.SyncBoardFlippedState(_boardIsFlipped);
                    _settingsToolbar.SyncCoachModeState(_settingsToolbar.GetCoachModeEnabled());
                    _settingsToolbar.SyncSettingsToolbarHiddenState(_settingsToolbarHidden);
                    // Apply the persisted capture-exclusion preference to the just-
                    // created overlay surfaces (they register with CaptureExclusion
                    // defaulting ON; this honors a saved OFF). See CaptureExclusion.
                    CaptureExclusion.SetEnabled(_settingsToolbar.GetExcludeOverlaysFromCaptureEnabled());
                    _hotkeyController!.Bindings = _settingsToolbar.GetHotkeyBindings();
                    _hotkeyController.WireRegisteredListener();
                    bool showSystemTrayIcon = _settingsToolbar.GetShowTaskbarIcon();
#if DEBUG
                    showSystemTrayIcon = true;
#endif
                    _showSystemTrayIconAfterStartup = showSystemTrayIcon;

                    // Start with toolbar disabled
                    _settingsToolbar.SetEnabled(false);

                    // Subscribe to system-wide window-state events so we get
                    // INSTANT notification when the board is minimized/closed,
                    // instead of waiting up to a frame interval (which can be
                    // hundreds of ms during heavy vision work).
                    //
                    // CRITICAL: Hook MUST be registered on this UI thread, not
                    // the main thread. SetWinEventHook with WINEVENT_OUTOFCONTEXT
                    // delivers events via the calling thread's message pump -
                    // the main thread is the vision loop and has no pump, so
                    // events would never fire. Application.Run(_overlay) below
                    // is what pumps messages on this thread.
                    bool hookOk = WindowTracker.RegisterWindowStateHook(OnSystemWindowEvent);
                    if (_diagLoggingEnabled)
                    {
                        LogDiag("WINTRACK", hookOk
                            ? "system-event hook registered (UI thread)"
                            : "FAILED to register system-event hook");
                    }

                    _ = _overlay.Handle;
                    // Prove that the message pump has processed at least one
                    // queued callback before Main performs any synchronous
                    // Invoke calls during startup.
                    _overlay.BeginInvoke(new Action(() =>
                    {
                        try { uiPumpReady.Set(); } catch { }
                    }));
                    Application.Run(_overlay);
                    CrashDiagnostics.WriteLifecycleEvent(
                        "UI_MESSAGE_LOOP_RETURNED",
                        $"expected={_uiShutdownExpected} overlayDisposed={_overlay?.IsDisposed ?? true}");

                    if (!_uiShutdownExpected)
                    {
                        var failure = new InvalidOperationException(
                            "The ChessKit UI message loop stopped unexpectedly.");
                        _uiThreadFailure = failure;
                        _uiMessageLoopStopped = true;
                        Environment.ExitCode = 1;
                        WriteCrashRecord("UiMessageLoop", failure, terminating: true);
                    }
                }
                catch (Exception ex)
                {
                    _uiThreadFailure = ex;
                    _uiMessageLoopStopped = true;
                    Environment.ExitCode = 1;
                    WriteCrashRecord("UiThread", ex, terminating: true);
                }
                finally
                {
                    _uiMessageLoopStopped = true;
                    try { uiPumpReady.Set(); } catch { }
                }
            })
            { IsBackground = true };
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();
            if (!uiPumpReady.WaitOne(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException(
                    "ChessKit's UI message pump did not start within 15 seconds.");
            }

            if (_uiThreadFailure != null)
                throw new InvalidOperationException("ChessKit's UI thread failed during startup.", _uiThreadFailure);
            if (_uiMessageLoopStopped)
                throw new InvalidOperationException("ChessKit's UI thread stopped during startup.");

            if (!RunStartupFlow())
            {
                if (_uiThreadFailure != null || (_uiMessageLoopStopped && !_uiShutdownExpected))
                {
                    throw new InvalidOperationException(
                        "ChessKit's UI thread stopped during the startup flow.",
                        _uiThreadFailure);
                }

                CrashDiagnostics.MarkCleanExit("startup canceled");
                CloseUiThreadAfterStartupCancel();
                return;
            }

#if DEBUG
            // Debug never contacts the real license server: the edition is driven by
            // the BuildLimits debug override (SetDebugFreeEditionOverride, set from args above).
            ShowStartupStatus(BuildLimits.IsFreeEdition ? "Preparing Chess Kit (Free)..." : "Preparing Chess Kit debug build...", 24);
#else
            // Single runtime-gated build: verify the license. EnforceAsync latches
            // Licensed if a valid license is found, and otherwise continues as the
            // limited Free Edition. The one case it can BLOCK is when the servers are
            // unreachable: rather than silently drop a paying user to Free (which
            // looks like a revoked license), it shows a modal offering Try again /
            // Continue in Free / Exit. Only "Exit" returns false -> abort startup.
            // The background monitor it starts can still upgrade Free -> Licensed later.
            ShowStartupStatus("Checking Chess Kit license...", 18, indeterminate: true);
            if (!await _licenseEnforcer!.EnforceAsync())
            {
                if (_uiThreadFailure != null || (_uiMessageLoopStopped && !_uiShutdownExpected))
                {
                    throw new InvalidOperationException(
                        "ChessKit's UI thread stopped during license verification.",
                        _uiThreadFailure);
                }

                // User chose to exit at the "servers unreachable" prompt.
                CrashDiagnostics.MarkCleanExit("license prompt exit");
                CloseUiThreadAfterStartupCancel();
                return;
            }
            UpdateStartupStatus(BuildLimits.IsFreeEdition ? "Starting Free Edition..." : "License verified.", 32);
            await Task.Delay(120);
#endif
            UpdateStartupStatus("Preparing system tray...", 42);
            InitializeTaskbarIconAfterStartup();
            StartStartupUpdateCheck();

            UpdateStartupStatus("Starting keyboard listener...", 52);
            // Initialize and start the low-level keyboard listener. The controller
            // owns the listener; its key-press handler resolves the command, marshals
            // onto the overlay thread, and routes through the same dedup pipeline as
            // before. The controller is disposed during shutdown cleanup below.
            _hotkeyController!.StartGlobalListener();
            Log("[INFO] Keyboard listener started");

            UpdateStartupStatus("Connecting to board recognition service...", 66, indeterminate: true);
            // Initialize server-side BoardVision client. The ONNX model is no longer embedded in the executable.
            try
            {
                _detector = BoardVisionDetector.CreateFromEmbeddedResource();
                _executionMode = _detector.ExecutionProvider;
                Log($"[INFO] BoardVision detector initialized - {_executionMode}");
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to initialize server-side detector: {ex.Message}");
                WriteCrashRecord("BoardVision initialization", ex, terminating: true);
                Environment.ExitCode = 1;
                CloseStartupStatus();
#if DEBUG
                Console.ReadKey(true);
#else
                MessageBox.Show($"Failed to initialize server-side BoardVision:\n{ex.Message}",
                    "Chess Kit - Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
#endif
                return;
            }

            // Initialize selected UCI engine if available
            if (_settingsToolbar != null)
            {
                _maxArrowCount = BuildLimits.ClampLines(_settingsToolbar.GetArrowCount());
                _coachModeEnabled = _settingsToolbar.GetCoachModeEnabled();
                _coachLevel = Math.Clamp(_settingsToolbar.GetCoachLevel(), 1, 10);
                _coachMarkCount = Math.Clamp(_settingsToolbar.GetCoachMarkCount(), 1, 3);
                _coachCardEnabled = _settingsToolbar.GetCoachCardEnabled();
                _eloLimitEnabled = _settingsToolbar.GetEloLimitEnabled();
                _maxEloRating = _settingsToolbar.GetMaxEloRating();
                _humanAdaptiveEnabled = _settingsToolbar.GetHumanAdaptiveEnabled();
                _humanPlayProfile = _settingsToolbar.GetHumanPlayProfile();
                _speculativeAnalysisEnabled = _settingsToolbar.GetSpeculativeAnalysisEnabled();
                _speculativeAnalysisMode = _settingsToolbar.GetSpeculativeAnalysisMode();
                _blitzModeSetting = _settingsToolbar.GetBlitzMode();
                // Free never runs the Bullet profile: the prefetch/PV cache it
                // exists to widen is force-off for Free (SpeculativePrefetchActive),
                // so honoring a persisted flag would only lower depth.
                _bulletProfileEnabled = !BuildLimits.IsFreeEdition && _settingsToolbar.GetBulletProfileEnabled();
                // Mid-session Free -> Licensed upgrade (background monitor):
                // re-apply a persisted Bullet profile through the normal settings
                // path, otherwise the toolbar row appears (runtime-gated) with a
                // checked-but-inert checkbox. Raised on a worker thread -
                // marshal via the overlay like the license prompt does.
                _licenseEnforcer!.LicenseUpgraded += () =>
                {
                    try
                    {
                        if (_settingsToolbar?.GetBulletProfileEnabled() == true && _overlay != null)
                        {
                            _overlay.BeginInvoke(new Action(() => HandleSettingChanged("BulletProfile", true)));
                        }
                    }
                    catch { }
                };
                _evalDisplayMode = _settingsToolbar.GetEvalDisplayMode();
                _evalBar?.SetDisplayMode(_evalDisplayMode);
            }

            if (!File.Exists(initialEnginePath))
            {
                UpdateStartupStatus("No engine found. External board tracking will still start.", 88);
                await Task.Delay(220);
            }
            else
            {
                Log($"[INFO] Live engine startup deferred until analysis is enabled: {Path.GetFileName(initialEnginePath)}");
                UpdateStartupStatus("Finalizing startup...", 88);
            }

            UpdateStartupStatus("ChessKit is ready.", 100);
            await Task.Delay(350);
            CloseStartupStatus();

            // The UI is live and the license check has completed; only now allow
            // global hotkeys to be handled (see _startupComplete).
            _startupComplete = true;

            // Setup cancellation
            using var cts = new CancellationTokenSource();

#if DEBUG
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
#endif

            PrintInstructions();
            RefreshDebugView("Startup complete");
            _perfStopwatch.Start();

            // Main loop
            while (!cts.IsCancellationRequested && !_uiMessageLoopStopped)
            {
                try
                {
                    var frameStart = _perfStopwatch.ElapsedMilliseconds;

                    // === FPS DIAGNOSTICS ===
                    // Phase timing for FPS investigation. Aggregated over a
                    // window of frames and printed periodically. Remove or
                    // wrap in #if DEBUG once the FPS issue is understood.
                    long phase1Ms = 0;
                    long phase2Ms = 0;
                    long phase1Start, phase1End, phase2Start, phase2End;

                    if (_isTracking)
                    {
                        if (_detector == null)
                        {
                            await Task.Delay(16, cts.Token);
                            continue;
                        }

                        // === LOST-WINDOW RECOVERY POLL ===
                        // While the lost-tracking latch is set, poll the
                        // cached HWND directly. This is the SOLE path that
                        // can release the latch (vision-based path was
                        // disabled because YOLO can hallucinate "boards"
                        // on whatever's visible behind a minimized window).
                        // We require IsTrackable=true for a sustained
                        // duration before committing - otherwise transient
                        // OS blips during minimize-animation could trick us.
                        // Detailed state logging here is intentional: when
                        // arrows reappear unexpectedly, this log tells us
                        // exactly what the OS reported for the window.
                        if (_trackingLostWaitingForReacquire && _lostHwndCache != IntPtr.Zero)
                        {
                            bool exists = WindowTracker.IsWindow(_lostHwndCache);
                            if (!exists)
                            {
                                if (_diagLoggingEnabled)
                                    LogDiag("WINTRACK", $"lost HWND 0x{_lostHwndCache.ToInt64():X} no longer exists - clearing cache, vision can re-acquire freely");
                                _lostHwndCache = IntPtr.Zero;
                                _trackingLostWaitingForReacquire = false;
                                _lostAcquisitionCandidateSinceUtc = null;
                            }
                            else
                            {
                                bool iconic = WindowTracker.IsIconic(_lostHwndCache);
                                bool visible = WindowTracker.IsWindowVisible(_lostHwndCache);
                                // Cloak-aware like IsTrackable: a window on another
                                // virtual desktop reads !iconic && visible, and the
                                // 300ms slow path would re-acquire it and re-show
                                // overlays on the WRONG desktop in a ~3Hz loop.
                                bool trackable = !iconic && visible && !WindowTracker.IsCloaked(_lostHwndCache);

                                // Log every OS-state transition while latch
                                // is set so we can see what the browser is
                                // actually doing during the reported "2s
                                // arrow reappear" window.
                                if (_diagLoggingEnabled
                                    && (iconic != _lastLostIconicLogged
                                        || visible != _lastLostVisibleLogged))
                                {
                                    LogDiag("WINTRACK", $"lost-hwnd state: iconic={iconic} visible={visible} trackable={trackable}");
                                    _lastLostIconicLogged = iconic;
                                    _lastLostVisibleLogged = visible;
                                }

                                DateTime nowPoll = DateTime.UtcNow;

                                // FAST PATH: if the system-event hook fired
                                // MINIMIZE_END for our HWND, the OS just
                                // confirmed a real user-initiated restore
                                // (the hook only fires for actual restore
                                // operations, not for spurious iconic toggles).
                                // Release the latch immediately as long as
                                // IsTrackable agrees - no stability wait.
                                // This makes restore latency essentially zero.
                                if (trackable && _minimizeEndFiredForLostHwnd)
                                {
                                    if (WindowTracker.TryGetWindowRect(_lostHwndCache, out var fastRect))
                                    {
                                        if (_diagLoggingEnabled)
                                            LogDiag("WINTRACK", $"window re-acquired (hwnd=0x{_lostHwndCache.ToInt64():X}, MINIMIZE_END fast path) - releasing analysis latch");
                                        _trackedHwnd = _lostHwndCache;
                                        _lastWindowRect = fastRect;
                                        // Project the board's screen position from
                                        // the cached offset within the window. This
                                        // is exactly where the board was before the
                                        // minimize, so cached arrows can be redrawn
                                        // there immediately.
                                        _lastTrackedBox = ProjectTrackedBoardRect(fastRect, preferScaledProjection: WindowTracker.WindowSizeChanged(_lastWindowRect, fastRect));
                                        _framesSinceWindowTrackVerify = int.MaxValue;
                                        _trackingLostWaitingForReacquire = false;
                                        _lostHwndCache = IntPtr.Zero;
                                        _lostAcquisitionCandidateSinceUtc = null;
                                        _minimizeEndFiredForLostHwnd = false;
                                        // Invalidate queued paints and wait for the
                                        // next verified board sample before drawing
                                        // cached arrows again. This avoids a brief
                                        // flash at stale coordinates after heavy
                                        // window switching/minimize stress.
                                        // Interlocked: the WinEvent hook thread also
                                        // bumps this generation (HideOverlaysVisually).
                                        Interlocked.Increment(ref _arrowDisplayGeneration);
                                        Interlocked.Increment(ref _arrowRenderToken);
                                        _showingMoves = false;
                                        ShowOverlaysForTrackedWindow();
                                        // Position the toolbar on top of the
                                        // restored window so it's visible
                                        // immediately, not after vision runs.
                                        if (_settingsToolbar != null)
                                        {
                                            _settingsToolbar.UpdateWindowPosition(new Rectangle(
                                                fastRect.Left, fastRect.Top,
                                                fastRect.Width, fastRect.Height));
                                        }
                                        // Fall through - rest of the frame proceeds
                                        // with normal tracking.
                                    }
                                }
                                // SLOW-PATH FALLBACK: hook didn't fire (OS quirk
                                // or initial detection without minimize history).
                                // Use IsTrackable stability check at 300ms.
                                else if (trackable)
                                {
                                    if (_lostAcquisitionCandidateSinceUtc == null)
                                    {
                                        _lostAcquisitionCandidateSinceUtc = nowPoll;
                                    }
                                    else if ((nowPoll - _lostAcquisitionCandidateSinceUtc.Value).TotalMilliseconds >= 300)
                                    {
                                        // 300ms continuous IsTrackable=true.
                                        // Treat as legitimate restore. Short
                                        // because the cache is cleared on loss,
                                        // vision is paused while the latch is
                                        // set, and obstruction is detected
                                        // separately - a brief flash would be
                                        // FRESH arrows on a genuinely-visible
                                        // window, which is correct behavior.
                                        if (WindowTracker.TryGetWindowRect(_lostHwndCache, out var recoveredRect))
                                        {
                                            if (_diagLoggingEnabled)
                                                LogDiag("WINTRACK", $"window re-acquired (hwnd=0x{_lostHwndCache.ToInt64():X}, stable for 300ms+) - releasing analysis latch");
                                            _trackedHwnd = _lostHwndCache;
                                            _lastWindowRect = recoveredRect;
                                            _lastTrackedBox = ProjectTrackedBoardRect(recoveredRect, preferScaledProjection: WindowTracker.WindowSizeChanged(_lastWindowRect, recoveredRect));
                                            // Force vision verify on next frame so the
                                            // board offset is freshly computed against
                                            // the restored window.
                                            _framesSinceWindowTrackVerify = int.MaxValue;
                                            _trackingLostWaitingForReacquire = false;
                                            _lostHwndCache = IntPtr.Zero;
                                            _lostAcquisitionCandidateSinceUtc = null;
                                            _minimizeEndFiredForLostHwnd = false;
                                            // Avoid drawing cached arrows until the
                                            // restored board has been verified by
                                            // vision. Otherwise old coordinates can
                                            // flash for a frame under stress.
                                            Interlocked.Increment(ref _arrowDisplayGeneration);
                                            Interlocked.Increment(ref _arrowRenderToken);
                                            _showingMoves = false;
                                            ShowOverlaysForTrackedWindow();
                                            if (_settingsToolbar != null)
                                            {
                                                _settingsToolbar.UpdateWindowPosition(new Rectangle(
                                                    recoveredRect.Left, recoveredRect.Top,
                                                    recoveredRect.Width, recoveredRect.Height));
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    // Reset timer on any blip back to iconic.
                                    if (_lostAcquisitionCandidateSinceUtc != null && _diagLoggingEnabled)
                                    {
                                        LogDiag("WINTRACK", "stability timer reset (window went iconic again)");
                                    }
                                    _lostAcquisitionCandidateSinceUtc = null;
                                }
                            }
                        }

                        // While the latch is set, skip everything else.
                        // Vision running would just re-populate cached state
                        // (_lastTrackedBox, _currentMoveArrows-via-refresh,
                        // toolbar position) on whatever's behind the
                        // minimized window. The lost-hwnd poll above is the
                        // ONLY useful work to do during this period - when
                        // it trips the latch off, normal flow resumes.
                        if (_trackingLostWaitingForReacquire)
                        {
                            await Task.Delay(33, cts.Token);
                            continue;
                        }

                        // Orientation is an explicit user decision point.
                        // While the prompt is visible, pause the whole
                        // tracking loop rather than only the FEN phase. Even
                        // "cheap" board scans can saturate weak/VM machines
                        // enough that the topmost prompt appears frozen and
                        // shows the system busy cursor.
                        if (_orientationPromptVisible)
                        {
                            await Task.Delay(50, cts.Token);
                            continue;
                        }

                        if (TryHandleForegroundWindowChange())
                        {
                            await Task.Delay(33, cts.Token);
                            continue;
                        }

                        // === Z-ORDER OBSTRUCTION CHECK ===
                        // Browser is trackable (not iconic, visible per OS) but
                        // its chess board may be covered by another window.
                        // Without this check, our overlays would float on top
                        // of whatever's covering the board (the other window),
                        // pretending to track a board that isn't actually
                        // visible. Use WindowFromPoint at the board's center
                        // and 4 mid-quadrants - if 3+ of 5 samples resolve to
                        // a different top-level window, treat as covered.
                        // No state reset: when the user moves the covering
                        // window away, vision/tracking resumes on the next
                        // frame with no ramp-up delay.
                        if (!_menuExpanded
                            && _trackedHwnd != IntPtr.Zero
                            && _lastTrackedBox.HasValue
                            && WindowTracker.IsTrackable(_trackedHwnd)
                            && WindowTracker.IsBoardObscured(_trackedHwnd, _lastTrackedBox.Value))
                        {
                            if (!_boardObscuredLastFrame && _diagLoggingEnabled)
                            {
                                LogDiag("WINTRACK", "board obscured by another window - hiding overlays");
                            }
                            _boardObscuredLastFrame = true;
                            HideOverlaysVisually();
                            await Task.Delay(33, cts.Token);
                            continue;
                        }
                        else if (_boardObscuredLastFrame)
                        {
                            if (_diagLoggingEnabled)
                                LogDiag("WINTRACK", "board un-obscured - resuming overlays");
                            _boardObscuredLastFrame = false;
                        }

                        // SKIP board detection if menu is expanded
                        if (_menuExpanded)
                        {
                            if (_trackedHwnd != IntPtr.Zero && !WindowTracker.IsTrackable(_trackedHwnd))
                            {
                                HideOverlaysAfterWindowGone("menu expanded, tracked window not visible");
                                await Task.Delay(33, cts.Token);
                                continue;
                            }

                            // Even with menu expanded, follow window
                            // movement via Win32 if we have a tracked HWND.
                            // The vision detector is suppressed (menu is
                            // open so we don't care about FEN changes
                            // right now), but window movement should still
                            // be tracked instantly.
                            if (_trackedHwnd != IntPtr.Zero
                                && WindowTracker.IsTrackable(_trackedHwnd)
                                && WindowTracker.TryGetWindowRect(_trackedHwnd, out var menuWinRect))
                            {
                                var projected = ProjectTrackedBoardRect(menuWinRect, preferScaledProjection: WindowTracker.WindowSizeChanged(_lastWindowRect, menuWinRect));
                                _lastTrackedBox = projected;
                                _lastWindowRect = menuWinRect;
                            }

                            // Keep using the last known board position
                            if (_lastTrackedBox.HasValue)
                            {
                                var cachedBoardRect = _lastTrackedBox.Value;
                                // Detection pauses while settings are open, but all
                                // surfaces still follow real window movement. The
                                // shared dispatcher suppresses identical geometry so
                                // this 60 FPS branch cannot flood the UI queue.
                                UpdateTrackedSurfaceGeometry(cachedBoardRect);
                            }
                            else
                            {
                                ShowToolbarAtFallbackPosition();
                            }

                            // Skip the rest of detection but still count frame
                            _frameCount++;
                            CheckPendingRawHideFuse();
                            UpdatePerformanceMetrics();

                            var menuFrameTime = _perfStopwatch.ElapsedMilliseconds - frameStart;
                            if (menuFrameTime < 16)
                            {
                                await Task.Delay((int)(16 - menuFrameTime), cts.Token);
                            }
                            continue;
                        }

                        // Phase 1: Board detection (full screen only when needed)
                        int boardScanInterval = DateTime.UtcNow < _fastBoardScanUntilUtc
                            ? _fastBoardFullScanInterval
                            : _boardFullScanInterval;
                        DateTime boardScanNowUtc = DateTime.UtcNow;
                        bool healthyVerifyCooldownElapsed =
                            _lastHealthyBoardVerifyUtc == DateTime.MinValue ||
                            (boardScanNowUtc - _lastHealthyBoardVerifyUtc).TotalMilliseconds >= _healthyBoardVerifyCooldownMs;
                        bool localSearchCooldownElapsed =
                            _lastLocalBoardSearchUtc == DateTime.MinValue ||
                            (boardScanNowUtc - _lastLocalBoardSearchUtc).TotalMilliseconds >= _localBoardSearchCooldownMs;
                        bool recoverySearchCooldownElapsed =
                            _lastRecoveryBoardSearchUtc == DateTime.MinValue ||
                            (boardScanNowUtc - _lastRecoveryBoardSearchUtc).TotalMilliseconds >= _recoveryBoardSearchCooldownMs;

                        phase1Start = _perfStopwatch.ElapsedMilliseconds;

                        // === WINDOW-TRACKING FAST PATH ===
                        // If we already know the parent window of the chess
                        // board, ask Win32 where that window is and derive the
                        // board's current screen position from cached offset.
                        // This is microseconds vs ~30ms for vision, so the
                        // overlay catches up to window movement instantly.
                        bool windowTrackHandledFrame = false;
                        bool windowTrackVerifyDue = false;
                        bool forceFullWindowReacquire = false;
                        bool mouseFocusVerifyDue = false;
                        if (_trackedHwnd != IntPtr.Zero)
                        {
                            if (!WindowTracker.IsTrackable(_trackedHwnd))
                            {
                                HideOverlaysAfterWindowGone("per-frame poll");
                            }
                            else if (WindowTracker.TryGetWindowRect(_trackedHwnd, out var winRect))
                            {
                                // Compute frame-over-frame window stability.
                                // We do this here (rather than only inside
                                // the verify path) because we use the
                                // "just-settled" transition to TRIGGER an
                                // immediate verify on the very next frame
                                // after a move/resize ends. Without that,
                                // we'd wait up to 2 seconds for the next
                                // periodic verify - the user-visible
                                // "arrows freeze for seconds and snap"
                                // experience after a resize or maximize.
                                const int windowMotionThreshold = 8;
                                int prevW0 = _lastWindowRect.Right - _lastWindowRect.Left;
                                int prevH0 = _lastWindowRect.Bottom - _lastWindowRect.Top;
                                int curW0 = winRect.Right - winRect.Left;
                                int curH0 = winRect.Bottom - winRect.Top;
                                bool windowMoving0 =
                                    Math.Abs(winRect.Left - _lastWindowRect.Left) > windowMotionThreshold ||
                                    Math.Abs(winRect.Top - _lastWindowRect.Top) > windowMotionThreshold;
                                bool windowResizing0 =
                                    Math.Abs(curW0 - prevW0) > windowMotionThreshold ||
                                    Math.Abs(curH0 - prevH0) > windowMotionThreshold;
                                bool hardWindowLayoutJump = windowResizing0 && IsHardWindowLayoutJump(prevW0, prevH0, curW0, curH0);
                                bool windowUnstableNow = windowMoving0 || windowResizing0;
                                bool windowJustSettled = _windowWasUnstableLastFrame && !windowUnstableNow;
                                _windowWasUnstableLastFrame = windowUnstableNow;

                                if (windowUnstableNow)
                                {
                                    _windowStableSinceUtc = DateTime.UtcNow;
                                    if (windowResizing0)
                                    {
                                        _trackedWindowLastResizeUtc = DateTime.UtcNow;
                                        _trackedWindowResizeGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(_trackedWindowResizeArrowHoldMs);
                                        _scheduledVerifyUtc = DateTime.UtcNow.AddMilliseconds(80);
                                        if (hardWindowLayoutJump)
                                        {
                                            forceFullWindowReacquire = true;
                                            _requestBoardRefresh = true;
                                            _scheduledVerifyUtc = DateTime.UtcNow;
                                            _trackedWindowResizeGraceUntilUtc = DateTime.UtcNow.AddMilliseconds(800);
                                            // The cached board geometry is now stale. Keep accepting a
                                            // re-grounded (very different) same-window rect for a few
                                            // seconds so the board size snaps to its true new scale,
                                            // instead of the fresh detection being ignored as an
                                            // inconsistent alternate and leaving arrows oversized.
                                            _hardLayoutRegroundUntilUtc = DateTime.UtcNow.AddMilliseconds(3000);
                                        }
                                    }
                                }

                                bool sizeChanged = WindowTracker.WindowSizeChanged(_lastWindowRect, winRect);
                                // Saturating increment: minimize/restore re-acquisition seeds this
                                // counter with int.MaxValue to force a verify next frame; a bare ++
                                // wrapped it to int.MinValue, silently disabling the periodic verify
                                // (the >= interval check stays false for ~2 billion frames) until a
                                // verify that could no longer trigger reset it. Saturate instead.
                                if (_framesSinceWindowTrackVerify < int.MaxValue)
                                    _framesSinceWindowTrackVerify++;
                                bool periodicVerifyDue = _framesSinceWindowTrackVerify >= WindowTrackVerifyIntervalFrames && healthyVerifyCooldownElapsed;
                                bool scheduledVerifyDue = _scheduledVerifyUtc != DateTime.MinValue
                                    && DateTime.UtcNow >= _scheduledVerifyUtc;
                                if (scheduledVerifyDue)
                                {
                                    _scheduledVerifyUtc = DateTime.MinValue;
                                }
                                mouseFocusVerifyDue = ShouldScanForBoardNearMouse(winRect);

                                if (windowUnstableNow)
                                {
                                    var projected = ProjectTrackedBoardRect(winRect, preferScaledProjection: windowResizing0);
                                    _lastTrackedBox = projected;
                                    _lastWindowRect = winRect;
                                    _boardLostFrames = 0;
                                    _boardContentLostFrames = 0;
                                    if (windowResizing0)
                                    {
                                        _trackedWindowLastResizeUtc = DateTime.UtcNow;
                                        _requestBoardRefresh = true;
                                    }

                                    MaybeShowOverlaysOnHealthyTrack();

                                    if (windowResizing0)
                                    {
                                        ClearExternalArrows();
                                    }
                                    // Push the projected rect to the overlay UNCONDITIONALLY
                                    // (not gated on _showingMoves): during a resize the
                                    // clear/re-show cycle races the UI thread's render token,
                                    // so shows get dropped while the previously painted arrow
                                    // pixels stay on the surface - anchored to the pre-resize
                                    // rect. This per-frame rect update rescales whatever the
                                    // overlay is showing (including those lingering pixels) to
                                    // the live board; it is rect-only, cheap, and no-ops when
                                    // the overlay is hidden.
                                    UpdateOverlayPositionIfChanged(new Rectangle(
                                        projected.X,
                                        projected.Y,
                                        projected.Width,
                                        projected.Height));

                                    windowTrackHandledFrame = !windowResizing0 && !hardWindowLayoutJump;
                                    windowTrackVerifyDue = windowResizing0 || hardWindowLayoutJump;
                                }
                                else if (sizeChanged || periodicVerifyDue || _requestBoardRefresh || windowJustSettled || scheduledVerifyDue || mouseFocusVerifyDue)
                                {
                                    // Window resized, periodic re-verification
                                    // is due, an explicit refresh was requested,
                                    // or the window just stopped moving/
                                    // resizing. Don't trust the cached offset;
                                    // let vision re-confirm. We still use the
                                    // cached offset for THIS frame so the
                                    // toolbar doesn't flash to fallback -
                                    // vision will refine on the same frame.
                                    if (windowJustSettled && _diagLoggingEnabled)
                                    {
                                        LogDiag("WINTRACK", "window just settled - forcing immediate verify");
                                    }
                                    var projected = ProjectTrackedBoardRect(winRect, preferScaledProjection: sizeChanged || windowJustSettled);
                                    _lastTrackedBox = projected;
                                    _lastWindowRect = winRect;
                                    _boardLostFrames = 0;
                                    MaybeShowOverlaysOnHealthyTrack();
                                    // Unconditional (see the resize branch above): lingering
                                    // arrow pixels must follow the live rect even while
                                    // _showingMoves is false mid-resize.
                                    UpdateOverlayPositionIfChanged(new Rectangle(
                                        projected.X,
                                        projected.Y,
                                        projected.Width,
                                        projected.Height));
                                    windowTrackVerifyDue = true;
                                    // Fall through to vision below to refresh.
                                }
                                else
                                {
                                    // Pure window movement - fast path. No
                                    // vision needed. Project board position,
                                    // update overlays, done.
                                    var projected = ProjectTrackedBoardRect(winRect, preferScaledProjection: sizeChanged);
                                    _lastTrackedBox = projected;
                                    _lastWindowRect = winRect;
                                    _boardLostFrames = 0;

                                    // Tell forms that board is visible (idempotent,
                                    // suppression-gated during minimize races).
                                    MaybeShowOverlaysOnHealthyTrack();

                                    windowTrackHandledFrame = true;
                                }
                            }
                        }

                        // If the IsTrackable check above just fired
                        // HideOverlaysAfterWindowGone (latch is now set
                        // mid-frame), skip the rest of Phase 1. Otherwise
                        // vision would run, find a board on whatever
                        // mid-minimize pixels are still on screen, and
                        // call SetBoardVisible(true) on all the overlay
                        // forms - re-showing the toolbar we just hid.
                        if (_trackingLostWaitingForReacquire)
                        {
                            await Task.Delay(33, cts.Token);
                            continue;
                        }

                        // === VISION-BASED DETECTION ===
                        // Always try the cheap cached-location scan each frame
                        // when we have a known board. Detecting that the board
                        // moved (or disappeared) within one frame is what makes
                        // the toolbar follow window drags responsively. The
                        // expensive full-screen scan still runs only on the
                        // schedule below.
                        Rect? cachedScanResult = null;
                        bool cachedScanRan = false;
                        bool cachedScanSkippedForBudget = false;
                        bool trackedWindowHealthyForBoardScan =
                            _trackedHwnd != IntPtr.Zero &&
                            WindowTracker.IsTrackable(_trackedHwnd);
                        // Event-driven verifies (window resize settled, scheduled verify,
                        // explicit refresh, confirmed geometry drift) get a FAST upload
                        // budget: they are rare, high-signal moments where the board rect
                        // is likely wrong and arrows are visibly misplaced. The default
                        // 20s budget exists to throttle routine polling, and letting it
                        // defer these repairs is what made a resized board sit with
                        // misaligned arrows for 20+ seconds.
                        int? localBoardDetectionMinInterval =
                            trackedWindowHealthyForBoardScan
                                ? ((windowTrackVerifyDue || DateTime.UtcNow < _geometryDriftVerifyPendingUntilUtc)
                                    ? GeometryDriftVerifyBudgetOverrideMs
                                    : (int?)null)
                                : _boardDetectionAcquireMinIntervalMs;
                        bool boardRefreshSearchAllowed = _requestBoardRefresh &&
                            (trackedWindowHealthyForBoardScan
                                ? healthyVerifyCooldownElapsed
                                : recoverySearchCooldownElapsed);
                        bool shouldRunLocalBoardSearch =
                            !forceFullWindowReacquire &&
                            !mouseFocusVerifyDue &&
                            !windowTrackHandledFrame &&
                            _lastTrackedBox.HasValue &&
                            (boardRefreshSearchAllowed ||
                                windowTrackVerifyDue ||
                                (!trackedWindowHealthyForBoardScan &&
                                    (_boardLostFrames > 0
                                        ? recoverySearchCooldownElapsed
                                        : localSearchCooldownElapsed)));
                        if (shouldRunLocalBoardSearch && _lastTrackedBox is Rect lastKnownBox)
                        {
                            _lastLocalBoardSearchUtc = boardScanNowUtc;
                            if (!trackedWindowHealthyForBoardScan)
                                _lastRecoveryBoardSearchUtc = boardScanNowUtc;
                            BoardVisionDetector.ClearBoardDetectionAttemptStatus();
                            cachedScanResult = TryDetectBoardNearLastKnownPosition(lastKnownBox, localBoardDetectionMinInterval);
                            cachedScanSkippedForBudget = BoardVisionDetector.LastBoardDetectionWasThrottled;
                            if (trackedWindowHealthyForBoardScan)
                                _lastHealthyBoardVerifyUtc = boardScanNowUtc;
                            cachedScanRan = true;
                        }

                        bool acquisitionBackoffActive =
                            _trackedHwnd == IntPtr.Zero &&
                            !_lastTrackedBox.HasValue &&
                            !forceFullWindowReacquire &&
                            DateTime.UtcNow < _externalBoardAcquisitionBackoffUntilUtc;

                        bool fullScanCooldownElapsed =
                            trackedWindowHealthyForBoardScan
                                ? healthyVerifyCooldownElapsed
                                : recoverySearchCooldownElapsed;
                        bool needFullScan = !windowTrackHandledFrame && (
                            !acquisitionBackoffActive &&
                            (forceFullWindowReacquire
                                || (_lastTrackedBox == null && fullScanCooldownElapsed)
                                || boardRefreshSearchAllowed
                                || (_boardLostFrames > _boardLostThreshold && recoverySearchCooldownElapsed)
                                || (_frameCount % boardScanInterval == 0 && fullScanCooldownElapsed)
                                || windowTrackVerifyDue
                                || mouseFocusVerifyDue
                                // Cached scan just failed. Trigger a recovery full
                                // scan on the recovery timer so prolonged board
                                // loss does not burn the network budget.
                                || (cachedScanRan && !cachedScanResult.HasValue && recoverySearchCooldownElapsed)));

                        // Enter the detection-result branch if either scan
                        // ran. needFullScan covers periodic + recovery cases;
                        // cachedScanRan covers normal per-frame tracking.
                        if (!windowTrackHandledFrame && (needFullScan || cachedScanRan))
                        {
                            Rect? boardRect = mouseFocusVerifyDue ? null : cachedScanResult;
                            bool rejectedAlternateBoardCandidate = false;
                            bool fullScanRan = false;
                            bool boardSearchSkippedForBudget = cachedScanSkippedForBudget;
                            int? fullBoardDetectionMinInterval =
                                (!trackedWindowHealthyForBoardScan || forceFullWindowReacquire || windowTrackVerifyDue || mouseFocusVerifyDue)
                                    ? _boardDetectionAcquireMinIntervalMs
                                    : null;

                            if (!boardRect.HasValue && needFullScan)
                            {
                                if (mouseFocusVerifyDue)
                                    LogDiag("WINTRACK", "mouse focus verify forcing full board scan");
                                fullScanRan = true;
                                if (trackedWindowHealthyForBoardScan)
                                    _lastHealthyBoardVerifyUtc = boardScanNowUtc;
                                else
                                    _lastRecoveryBoardSearchUtc = boardScanNowUtc;
                                // Need to find/refind the board - use full screen
                                using var fullBmp = ScreenCapture.CaptureVirtualScreen(out var fullScreenBounds);
                                using var fullFrame = BitmapConverter.ToMat(fullBmp);
                                BoardVisionDetector.ClearBoardDetectionAttemptStatus();
                                boardRect = DetectExternalBoardInFullFrame(
                                    fullFrame,
                                    fullScreenBounds,
                                    preferTrackedBoard: !mouseFocusVerifyDue,
                                    boardDetectionMinIntervalMs: fullBoardDetectionMinInterval);
                                if (BoardVisionDetector.LastBoardDetectionWasThrottled)
                                    boardSearchSkippedForBudget = true;
                            }

                            if (boardRect.HasValue && ShouldIgnoreDetectedAnalysisBoard(boardRect.Value))
                            {
                                boardRect = null;
                                _requestBoardRefresh = true;
                            }

                            if (boardRect.HasValue && ShouldRejectBoardBecauseNotForeground(boardRect.Value))
                            {
                                rejectedAlternateBoardCandidate = true;
                                boardRect = null;
                            }

                            if (boardRect.HasValue &&
                                _trackedHwnd != IntPtr.Zero &&
                                _lastTrackedBox.HasValue &&
                                !IsConsistentWithTrackedExternalBoard(
                                    boardRect.Value,
                                    _lastTrackedBox.Value,
                                    allowSameWindowResizeReflow: windowTrackVerifyDue || IsTrackedWindowResizeSettling() || DateTime.UtcNow < _hardLayoutRegroundUntilUtc))
                            {
                                if (!TryAcceptRelocatedBoardInTrackedWindow(boardRect.Value, "same-window board relocation"))
                                {
                                    LogDiag("WINTRACK", $"rejected alternate board candidate in tracked window: candidate={boardRect.Value.X},{boardRect.Value.Y} {boardRect.Value.Width}x{boardRect.Value.Height} tracked={_lastTrackedBox.Value.X},{_lastTrackedBox.Value.Y} {_lastTrackedBox.Value.Width}x{_lastTrackedBox.Value.Height}");
                                    rejectedAlternateBoardCandidate = true;
                                    boardRect = null;
                                    if (windowTrackVerifyDue || IsTrackedWindowResizeSettling())
                                    {
                                        _scheduledVerifyUtc = DateTime.UtcNow.AddMilliseconds(120);
                                    }
                                }
                            }

                            if (boardRect.HasValue &&
                                _trackedHwnd == IntPtr.Zero &&
                                !_trackingLostWaitingForReacquire &&
                                !IsPlausibleExternalBoardCandidate(boardRect.Value, "initial acquisition"))
                            {
                                boardRect = null;
                                _requestBoardRefresh = true;
                            }

                            if (boardRect.HasValue)
                            {
                                ResetExternalBoardAcquisitionBackoff("board found");
                                ResetForegroundBoardProbeBackoff("board found");
                                ClearForegroundNoBoardOverlayHold("visible board found");

                                // Board detected - reset lost frame counter
                                _boardLostFrames = 0;
                                _boardContentLostFrames = 0;
                                _invalidFenFrames = 0;
                                _requestBoardRefresh = false;
                                boardRect = NormalizeExternalBoardRect(boardRect.Value);

                                // Smooth the board position
                                var smoothedRect = SmoothBoardPosition(boardRect.Value);
                                // Only let smoothed (lagged) vision overwrite
                                // _lastTrackedBox when we DON'T have Win32
                                // tracking. With a tracked window, the fast
                                // path above (line ~729) already set
                                // _lastTrackedBox to a Win32-fresh,
                                // zero-lag projection - keep that and let
                                // vision only refine the cached offset
                                // below. Without this guard, every
                                // verify cycle (every ~60 frames) injects
                                // a 3-frame-averaged position that lags
                                // the actual window for one frame, and
                                // that lag propagates into the new offset.
                                if (_trackedHwnd == IntPtr.Zero)
                                {
                                    _lastTrackedBox = smoothedRect;
                                }

                                // === WINDOW-TRACKING ACQUISITION ===
                                // Vision found a board. Resolve which top-level
                                // window contains it and start tracking that
                                // window directly on subsequent frames.
                                if (_trackedHwnd == IntPtr.Zero && !_trackingLostWaitingForReacquire)
                                {
                                    // INITIAL acquisition path only - for vision-only
                                    // startup mode and the first detection of a window.
                                    // While the lost-tracking latch is set, this path
                                    // is skipped entirely; re-acquisition is handled
                                    // by the dedicated IsTrackable poll on
                                    // _lostHwndCache earlier in the frame. That gives
                                    // us a SINGLE trusted signal (the OS reporting
                                    // the same HWND as not-iconic for a sustained
                                    // duration), instead of vision hallucinating a
                                    // board in some random pixels.
                                    var hwnd = WindowTracker.ResolveTopLevelWindow(smoothedRect);
                                    if (hwnd != IntPtr.Zero
                                        && IsExternalForegroundCandidate(hwnd)
                                        && WindowTracker.TryGetWindowRect(hwnd, out var winRect))
                                    {
                                        SwitchTrackedExternalWindow(hwnd, smoothedRect, winRect, "vision acquisition");
                                    }
                                }
                                else if (windowTrackVerifyDue)
                                {
                                    // Re-confirm the offset between the chess
                                    // board and its parent window, in case
                                    // the board scrolled within the window.
                                    if (WindowTracker.TryGetWindowRect(_trackedHwnd, out var winRect))
                                    {
                                        // Bail on offset update if the window
                                        // is currently moving or resizing.
                                        // The offset's job is to capture
                                        // board position WITHIN the window's
                                        // content - not tied to the window's
                                        // screen position or its overall
                                        // size. During fast drags or
                                        // resizes, the screen-capture pixels
                                        // lag a few ms behind GetWindowRect,
                                        // so vision sees a transitional
                                        // frame and we'd compute an offset
                                        // that's nonsense (negative Y, sizes
                                        // 40% off, etc.). That nonsense then
                                        // poisons every subsequent fast-path
                                        // projection.
                                        const int windowMotionThreshold = 8;
                                        int prevW = _lastWindowRect.Right - _lastWindowRect.Left;
                                        int prevH = _lastWindowRect.Bottom - _lastWindowRect.Top;
                                        int curW = winRect.Right - winRect.Left;
                                        int curH = winRect.Bottom - winRect.Top;
                                        bool windowMoving =
                                            Math.Abs(winRect.Left - _lastWindowRect.Left) > windowMotionThreshold ||
                                            Math.Abs(winRect.Top - _lastWindowRect.Top) > windowMotionThreshold;
                                        bool windowResizing =
                                            Math.Abs(curW - prevW) > windowMotionThreshold ||
                                            Math.Abs(curH - prevH) > windowMotionThreshold;
                                        bool windowUnstable = windowMoving || windowResizing;

                                        // Reset the stability clock whenever the
                                        // window is in flux. We use this to
                                        // distinguish "window just moved/resized
                                        // and vision saw a transitional frame"
                                        // (untrustworthy) from "window has been
                                        // stable for a while and vision sees a
                                        // legitimately-changed board" (trust).
                                        if (windowUnstable)
                                        {
                                            _windowStableSinceUtc = DateTime.UtcNow;
                                        }

                                        _lastWindowRect = winRect;

                                        if (windowUnstable)
                                        {
                                            if (_diagLoggingEnabled)
                                            {
                                                LogDiag("WINTRACK", $"verify skipped - window unstable (move={windowMoving}, resize={windowResizing})");
                                            }
                                        }
                                        else
                                        {
                                            // Use the RAW vision result, not the
                                            // smoothed one - see earlier comment.
                                            Rect detectedBoardRect = NormalizeExternalBoardRect(boardRect.Value);
                                            var detectedOffset = WindowTracker.ComputeOffset(detectedBoardRect, winRect);

                                            // Sanity-check before committing.
                                            // Catches transitional frames vision
                                            // can produce when the window was
                                            // _just_ resized (window-stable
                                            // check above might pass on the
                                            // very first stable frame while
                                            // the board's content is still
                                            // re-laying-out). Reject if:
                                            // - any coord is negative
                                            // - board doesn't fit within window
                                            // - width vs cached differ too much
                                            //   (chess board can't suddenly
                                            //   grow 40% mid-resize)
                                            // - aspect ratio not roughly square
                                            bool offsetSane = true;
                                            string sanityFailReason = "";
                                            if (detectedOffset.X < 0 || detectedOffset.Y < 0)
                                            {
                                                offsetSane = false;
                                                sanityFailReason = "negative coord";
                                            }
                                            else if (detectedOffset.X + detectedOffset.Width > curW + 4 ||
                                                     detectedOffset.Y + detectedOffset.Height > curH + 4)
                                            {
                                                offsetSane = false;
                                                sanityFailReason = "exceeds window bounds";
                                            }
                                            else if (Math.Abs(detectedOffset.Width - detectedOffset.Height) > 30)
                                            {
                                                offsetSane = false;
                                                sanityFailReason = "not square";
                                            }
                                            else
                                            {
                                                // Compare to currently-cached size.
                                                // A cached width of 0 means we
                                                // haven't acquired yet - accept.
                                                int cachedW = _boardOffsetInWindow.Width;
                                                if (cachedW > 0)
                                                {
                                                    double sizeDelta = Math.Abs(detectedOffset.Width - cachedW) / (double)cachedW;
                                                    if (sizeDelta > 0.30)
                                                    {
                                                        // Big size change. Could be:
                                                        // (a) transient resize-race
                                                        //     (vision picked up
                                                        //     wrong element during
                                                        //     window animation), OR
                                                        // (b) legitimate change
                                                        //     (board window
                                                        //     was resized and
                                                        //     board scaled up).
                                                        // Distinguish via window
                                                        // stability time. If window
                                                        // has been still for >1s,
                                                        // vision is trustworthy.
                                                        double stableMs = _windowStableSinceUtc == DateTime.MinValue
                                                            ? double.MaxValue
                                                            : (DateTime.UtcNow - _windowStableSinceUtc).TotalMilliseconds;
                                                        bool sameTrackedWindowAfterResize = false;
                                                        if (IsTrackedWindowResizeSettling())
                                                        {
                                                            IntPtr detectedWindow = WindowTracker.ResolveTopLevelWindow(detectedBoardRect);
                                                            sameTrackedWindowAfterResize = detectedWindow == _trackedHwnd;
                                                        }

                                                        if (stableMs < _windowSettledTrustMs && !sameTrackedWindowAfterResize)
                                                        {
                                                            offsetSane = false;
                                                            sanityFailReason = $"size delta {sizeDelta:F2} during/just-after resize (stableMs={stableMs:F0})";

                                                            // Schedule a follow-up
                                                            // verify for when the
                                                            // stability window
                                                            // expires, plus a
                                                            // small grace. Without
                                                            // this we'd wait for
                                                            // the next periodic
                                                            // verify cycle which
                                                            // can be up to 2s.
                                                            double remainingMs = _windowSettledTrustMs - stableMs + 100;
                                                            _scheduledVerifyUtc = DateTime.UtcNow.AddMilliseconds(remainingMs);
                                                        }
                                                        // else: trust the new
                                                        // detection, large change
                                                        // is real
                                                    }
                                                }
                                            }

                                            if (!offsetSane)
                                            {
                                                if (_diagLoggingEnabled)
                                                {
                                                    LogDiag("WINTRACK", $"offset rejected ({sanityFailReason}): ({detectedOffset.X},{detectedOffset.Y}) {detectedOffset.Width}x{detectedOffset.Height}");
                                                }
                                            }
                                            else
                                            {
                                                // Only commit if it differs from
                                                // cached beyond YOLO jitter.
                                                const int jitterThreshold = 4;
                                                bool offsetChanged =
                                                    Math.Abs(detectedOffset.X - _boardOffsetInWindow.X) > jitterThreshold ||
                                                    Math.Abs(detectedOffset.Y - _boardOffsetInWindow.Y) > jitterThreshold ||
                                                    Math.Abs(detectedOffset.Width - _boardOffsetInWindow.Width) > jitterThreshold ||
                                                    Math.Abs(detectedOffset.Height - _boardOffsetInWindow.Height) > jitterThreshold;
                                                // A verify whose reading matches the cached offset
                                                // invalidates any pending minor shift: the previous
                                                // divergent reading was the outlier, not the cache.
                                                if (!offsetChanged)
                                                {
                                                    _pendingMinorBoardOffset = null;
                                                }
                                                if (offsetChanged)
                                                {
                                                    int shiftDx = Math.Abs(detectedOffset.X - _boardOffsetInWindow.X);
                                                    int shiftDy = Math.Abs(detectedOffset.Y - _boardOffsetInWindow.Y);
                                                    int shiftDw = Math.Abs(detectedOffset.Width - _boardOffsetInWindow.Width);
                                                    int shiftDh = Math.Abs(detectedOffset.Height - _boardOffsetInWindow.Height);
                                                    bool majorOffsetShift =
                                                        shiftDx > 16 ||
                                                        shiftDy > 16 ||
                                                        shiftDw > 12 ||
                                                        shiftDh > 12;

                                                    // VERIFY-CONSISTENCY DAMPING for minor shifts: the
                                                    // periodic vision verify can alternate between two
                                                    // near-identical board readings (e.g. a border edge
                                                    // in/out of the box at q58 under compression) with
                                                    // the window completely static. Committing every
                                                    // flip-flop moved the arrow anchor a few px on each
                                                    // verify - user-visible as arrows flickering between
                                                    // two positions. A minor shift is only committed when
                                                    // the NEXT verify reproduces it; major shifts (real
                                                    // layout changes) keep committing immediately.
                                                    if (!majorOffsetShift)
                                                    {
                                                        bool confirmsPending =
                                                            _pendingMinorBoardOffset.HasValue &&
                                                            Math.Abs(detectedOffset.X - _pendingMinorBoardOffset.Value.X) <= jitterThreshold &&
                                                            Math.Abs(detectedOffset.Y - _pendingMinorBoardOffset.Value.Y) <= jitterThreshold &&
                                                            Math.Abs(detectedOffset.Width - _pendingMinorBoardOffset.Value.Width) <= jitterThreshold &&
                                                            Math.Abs(detectedOffset.Height - _pendingMinorBoardOffset.Value.Height) <= jitterThreshold;
                                                        if (!confirmsPending)
                                                        {
                                                            _pendingMinorBoardOffset = detectedOffset;
                                                            // Always logged (rare event): the offset trail is
                                                            // the evidence for diagnosing which model/state
                                                            // produces misaligned anchors in the field.
                                                            Log($"[WINTRACK] minor offset shift pending re-confirmation: ({detectedOffset.X},{detectedOffset.Y}) {detectedOffset.Width}x{detectedOffset.Height} vs cached ({_boardOffsetInWindow.X},{_boardOffsetInWindow.Y}) {_boardOffsetInWindow.Width}x{_boardOffsetInWindow.Height}");
                                                            goto offsetShiftDeferred;
                                                        }
                                                    }
                                                    // While the board is genuinely gone (kingless-read streak:
                                                    // post-game screen, page reload) a "board" detected on the
                                                    // non-game page is a phantom - committing a large re-anchor
                                                    // to it abandons the real board's slot and costs ~20s+ of
                                                    // frozen arrows when the next game starts there. Hold the
                                                    // old anchor; the next game usually renders in the SAME
                                                    // place and confirms via the normal board-switch path.
                                                    if (_kinglessReadStreak >= KinglessBoardGoneStreak &&
                                                        (Math.Abs(shiftDw) > _boardOffsetInWindow.Width * 0.2 ||
                                                         Math.Abs(shiftDh) > _boardOffsetInWindow.Height * 0.2))
                                                    {
                                                        LogDiag("WINTRACK",
                                                            $"offset shift deferred: kingless-read streak (board likely absent) dw={shiftDw} dh={shiftDh}");
                                                        goto offsetShiftDeferred;
                                                    }

                                                    _pendingMinorBoardOffset = null;

                                                    _boardOffsetInWindow = detectedOffset;
                                                    UpdateBoardWindowProjection(detectedBoardRect, winRect);
                                                    _lastTrackedBox = detectedBoardRect;
                                                    _boardHistory.Clear();
                                                    // Drift votes measured against the old crop are void now.
                                                    ResetGeometryDriftState();
                                                    if (majorOffsetShift)
                                                    {
                                                        _trackedWindowLastResizeUtc = DateTime.UtcNow;
                                                        _externalBoardGeometryUnstableUntilUtc = DateTime.UtcNow.AddMilliseconds(_postOffsetShiftGeometrySettleMs);
                                                        BeginTransitionNoiseGuard(_postOffsetShiftGeometrySettleMs);
                                                        ResetPendingFenCandidate();
                                                        _invalidFenFrames = 0;
                                                        ClearExternalArrows();
                                                    }
                                                    // Always logged (rare event) - see the pending-shift log.
                                                    Log($"[WINTRACK] Offset shifted ({(majorOffsetShift ? "major" : "minor")}): ({_boardOffsetInWindow.X},{_boardOffsetInWindow.Y}) {_boardOffsetInWindow.Width}x{_boardOffsetInWindow.Height} dx={shiftDx} dy={shiftDy} dw={shiftDw} dh={shiftDh}");
                                                }
                                                else if (!HasUsableBoardWindowProjection())
                                                {
                                                    UpdateBoardWindowProjection(detectedBoardRect, winRect);
                                                }
                                            offsetShiftDeferred: ;
                                            }
                                        }
                                    }
                                    _framesSinceWindowTrackVerify = 0;
                                }

                                if (IsExternalBoardGeometryUnstable())
                                {
                                    if (!TryHoldProjectedExternalArrowsDuringTrackedResize())
                                    {
                                        ClearExternalArrows();
                                    }
                                }
                                else
                                {
                                    RefreshDisplayedArrows();
                                }

                                // Tell forms that board is visible
                                // (suppression-gated during minimize races).
                                MaybeShowOverlaysOnHealthyTrack();
                            }
                            else
                            {
                                if (!boardSearchSkippedForBudget &&
                                    fullScanRan &&
                                    _trackedHwnd == IntPtr.Zero &&
                                    !_lastTrackedBox.HasValue)
                                {
                                    RecordExternalBoardAcquisitionMiss();
                                }

                                // Vision didn't find a board this frame.
                                //
                                // If we have a healthy tracked window,
                                // this is almost certainly a capture race,
                                // NOT actual board loss: between our
                                // GetWindowRect call and the screen-capture
                                // pixel read (a few ms later), a fast drag
                                // can move the window further so the bytes
                                // we capture are off-window. The cached
                                // offset is still good; arrows are still
                                // tracking via the fast path; the board
                                // hasn't gone anywhere. Treating this as
                                // "lost" and incrementing _boardLostFrames
                                // would hide arrows after 3 verify cycles
                                // - exactly the 2-3 second freeze users
                                // see during fast circular motion.
                                bool windowStillHealthy =
                                    _trackedHwnd != IntPtr.Zero
                                    && WindowTracker.IsTrackable(_trackedHwnd);

                                if (windowStillHealthy)
                                {
                                    DateTime nowUtc = DateTime.UtcNow;
                                    double stableMs = _windowStableSinceUtc == DateTime.MinValue
                                        ? double.MaxValue
                                        : (nowUtc - _windowStableSinceUtc).TotalMilliseconds;

                                    if (boardSearchSkippedForBudget)
                                    {
                                        _boardContentLostFrames = 0;
                                        int retryMs = Math.Clamp(
                                            BoardVisionDetector.GetBoardDetectionCooldownRemainingMs(fullBoardDetectionMinInterval) + 75,
                                            250,
                                            20000);
                                        _scheduledVerifyUtc = nowUtc.AddMilliseconds(retryMs);
                                        if (_diagLoggingEnabled)
                                        {
                                            LogDiag("WINTRACK", $"board verify skipped by bandwidth budget; keeping tracked window and retrying in {retryMs}ms");
                                        }
                                    }
                                    else if (rejectedAlternateBoardCandidate)
                                    {
                                        _boardContentLostFrames = 0;
                                        _scheduledVerifyUtc = nowUtc.AddMilliseconds(180);
                                        if (_diagLoggingEnabled)
                                        {
                                            LogDiag("WINTRACK", "ignored alternate board candidate while tracked window stayed healthy");
                                        }
                                    }
                                    else if (stableMs >= _windowSettledTrustMs)
                                    {
                                        _boardContentLostFrames++;
                                        if (_boardContentLostFrames < _boardContentLostThreshold)
                                        {
                                            _scheduledVerifyUtc = nowUtc.AddMilliseconds(180);
                                        }
                                    }
                                    else
                                    {
                                        _boardContentLostFrames = 0;
                                    }

                                    if (_boardContentLostFrames >= _boardContentLostThreshold)
                                    {
                                        HandleBoardContentLostInHealthyWindow();
                                    }
                                    else if (_diagLoggingEnabled)
                                    {
                                        LogDiag("WINTRACK", $"vision verify failed but window healthy - keeping arrows (contentMiss={_boardContentLostFrames}/{_boardContentLostThreshold}, stableMs={stableMs:F0})");
                                    }
                                    ResetPendingFenCandidate();
                                }
                                else
                                {
                                    // Board not found
                                    _boardLostFrames++;
                                    ResetPendingFenCandidate();

                                    if (_boardLostFrames >= _arrowHideThreshold)
                                    {
                                        bool holdPendingOnLostBoard = DateTime.UtcNow < _externalArrowHoldUntilUtc;
                                        if ((_showingMoves || holdPendingOnLostBoard) && _overlay != null)
                                        {
                                            _externalArrowHoldUntilUtc = DateTime.MinValue;
                                            int generation = Interlocked.Increment(ref _arrowDisplayGeneration);
                                            _overlay.BeginInvoke(new Action(() => _overlay.HideArrows(generation, preserveFreeLimitWatermark: false)));
                                            _showingMoves = false;
                                        }
                                    }

                                    if (_boardLostFrames >= _evalBarHideThreshold)
                                    {
                                        _lastTrackedBox = null;
                                        _trackedHwnd = IntPtr.Zero;     // drop window tracking on board loss
                                        _boardRelativeInWindow = System.Drawing.RectangleF.Empty;
                                        _boardHistory.Clear();
                                        ResetConfirmedBoardSnapshot();

                                        _evalBar?.SetBoardVisible(false);
                                        _engineLines?.SetBoardVisible(false);
                                    }

                                    // Toolbar fallback uses its own longer threshold:
                                    // we don't want the toolbar to flash up to the
                                    // top of the screen during brief detection loss
                                    // (which happens routinely while a window is
                                    // being dragged or partially obscured).
                                    if (_boardLostFrames >= _toolbarFallbackThreshold)
                                    {
                                        ShowToolbarAtFallbackPosition();
                                    }
                                }
                            }
                        }
                        phase1End = _perfStopwatch.ElapsedMilliseconds;
                        phase1Ms = phase1End - phase1Start;

                        // Phase 2: FEN detection (optimized region capture)
                        phase2Start = _perfStopwatch.ElapsedMilliseconds;
                        if (_lastTrackedBox.HasValue && !IsExternalBoardOutputSuspended())
                        {
                            var trackedBox = _lastTrackedBox.Value;
                            bool forceStaleGateFenProbe = ShouldForceStaleExternalFenProbe("phase2-gate");
                            bool forceAnalysisHeartbeatFenProbe = !forceStaleGateFenProbe && ShouldForceExternalAnalysisFenHeartbeat("phase2-gate");
                            bool forceGateFenProbe = forceStaleGateFenProbe || forceAnalysisHeartbeatFenProbe;
                            if (!forceGateFenProbe && IsTrackedWindowResizeSettling())
                            {
                                ResetPendingFenCandidate();
                                _invalidFenFrames = 0;
                                if (_overlay != null && _showingMoves)
                                {
                                    var r = trackedBox;
                                    UpdateOverlayPositionIfChanged(new Rectangle(r.X, r.Y, r.Width, r.Height));
                                }
                            }
                            else if (!forceGateFenProbe && IsExternalBoardGeometryUnstable())
                            {
                                ResetPendingFenCandidate();
                                _invalidFenFrames = 0;
                                if (!TryHoldProjectedExternalArrowsDuringTrackedResize())
                                {
                                    ClearExternalArrows();
                                }
                            }
                            else if (!forceGateFenProbe && ShouldPauseFenDetectionForObstructingUi())
                            {
                                ResetPendingFenCandidate();
                                _invalidFenFrames = 0;
                            }
                            else if (!forceGateFenProbe && ShouldPauseFenDetectionForMouseInteraction())
                            {
                                // Preserve a pending candidate that is already a
                                // clean legal transition from the current position:
                                // that's the OPPONENT's move caught mid-confirm when
                                // the user grabbed a piece to reply. Resetting it
                                // every paused frame forced the whole confirm to
                                // restart after mouse-up - the measured "own move
                                // waits for the opponent backlog" stall (~600ms).
                                // Anything not legally bridgeable is still dropped:
                                // mid-drag pixel noise must not survive the pause.
                                if (!(_pendingFenCandidateCount > 0 &&
                                      !string.IsNullOrEmpty(_pendingFenCandidate) &&
                                      CanFastConfirmLegalExternalFen(_pendingFenCandidate, out _)))
                                {
                                    ResetPendingFenCandidate();
                                }
                                _invalidFenFrames = 0;
                            }
                            else
                            {
                                // Convert OpenCV Rect to System.Drawing.Rectangle for capture.
                                // The border must fit the fen-upload coord margin (3% of the
                                // board, see the ProcessBoard call): rank/file coordinate
                                // glyphs sit in the outermost pixels of the board, and a
                                // box-tight crop clips them - measured to HALVE the coord
                                // orientation-oracle's recall vs a 3%-margin crop.
                                int fenCropBorder = Math.Max(20, (int)Math.Ceiling(Math.Max(trackedBox.Width, trackedBox.Height) * 0.03));
                                Rectangle virtualScreen = GetVirtualScreenBounds();
                                var wantedCaptureRegion = new Rectangle(
                                    trackedBox.X - fenCropBorder,
                                    trackedBox.Y - fenCropBorder,
                                    trackedBox.Width + fenCropBorder * 2,
                                    trackedBox.Height + fenCropBorder * 2);
                                var captureRegion = Rectangle.Intersect(wantedCaptureRegion, virtualScreen);
                                if (captureRegion.Width <= 0 || captureRegion.Height <= 0)
                                {
                                    ResetPendingFenCandidate();
                                    _invalidFenFrames = 0;
                                    continue;
                                }

                                int localBoardX = trackedBox.X - captureRegion.X;
                                int localBoardY = trackedBox.Y - captureRegion.Y;
                                if (localBoardX < 0 || localBoardY < 0 ||
                                    localBoardX + trackedBox.Width > captureRegion.Width ||
                                    localBoardY + trackedBox.Height > captureRegion.Height)
                                {
                                    ResetPendingFenCandidate();
                                    _invalidFenFrames = 0;
                                    continue;
                                }

                                // === FPS DIAG: time the capture and convert sub-steps separately ===
                                long capStartMs = _perfStopwatch.ElapsedMilliseconds;

                                // Capture ONLY the board region, directly as
                                // an OpenCV Mat. This keeps the hot path out
                                // of the old Bitmap -> Mat roundtrip.
                                using var regionFrame = ScreenCapture.CaptureRegionMat(captureRegion);

                                long matStartMs = _perfStopwatch.ElapsedMilliseconds;
                                using var boardView = new Mat(regionFrame, new Rect(localBoardX, localBoardY, trackedBox.Width, trackedBox.Height));
                                long matEndMs = _perfStopwatch.ElapsedMilliseconds;

#if DEBUG
                                _fpsDiagP2CaptureSum += matStartMs - capStartMs;       // screen capture + direct Mat fill
                                _fpsDiagP2MatSum += matEndMs - matStartMs;             // board ROI creation
                                _fpsDiagP2RegionWidth = captureRegion.Width;
                                _fpsDiagP2RegionHeight = captureRegion.Height;
#endif

                                // Process the ENTIRE captured region, not a local rect
                                try
                                {
                                    BoardVisionDetector.BoardDiffInfo? boardDiff = null;
                                    bool shouldRunFenDetection = true;
                                    bool shouldRunSquareDiff = true;

                                    bool forceFullFenProbe = forceStaleGateFenProbe;
                                    bool forceFenDetectionProbe = forceGateFenProbe || ShouldForceStaleExternalFenProbe("heartbeat");
                                    if (forceFenDetectionProbe)
                                    {
                                        shouldRunSquareDiff = false;
                                        shouldRunFenDetection = true;
                                    }

                                    // Keep the external-board hot path aligned with the old known-good build:
                                    // square-diff first, then FEN if needed. The newer coarse pixel fingerprint
                                    // shortcut can false-negative small/blurred moves and starve the remote model.
#if DEBUG
                                    _fpsDiagP2PixelSum += 0;
#endif

                                    // === FPS DIAG: time the diff sub-step ===
                                    long diffStartMs = _perfStopwatch.ElapsedMilliseconds;

                                    if (shouldRunSquareDiff && !string.IsNullOrEmpty(_currentFEN) && _lastConfirmedBoardDiffSnapshot != null)
                                    {
                                        try
                                        {
                                            boardDiff = _detector.EstimateBoardDiffFromSnapshot(_lastConfirmedBoardDiffSnapshot, boardView);
                                        }
                                        catch (Exception ex) when (ex is OpenCVException || ex is ArgumentException || ex is InvalidOperationException || ex is AccessViolationException)
                                        {
                                            boardDiff = null;
                                            shouldRunFenDetection = true;
                                            LogDiag("DIFF", $"board diff failed; falling back to full FEN detection: {ex.GetType().Name}: {ex.Message}");
                                        }

                                        // === LATENCY DIAG: capture T0 = first frame where the board
                                        // diff sees a change after a static period. This is the
                                        // earliest point Chess Kit could possibly know "something
                                        // happened on the board."
                                        if (boardDiff != null && boardDiff.ChangedSquares > 0)
                                        {
                                            if (ShouldIgnoreCoachOverlayBoardDiff(boardDiff))
                                            {
                                                shouldRunFenDetection = false;
                                                _invalidFenFrames = 0;
                                                _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;
                                                _latencyT0Utc = DateTime.MinValue;
                                                _latencyT0ChangedSquares = 0;
                                            }
                                                else
                                                {
                                                    LogStaleExternalArrowObservation("diff-change", boardDiff: boardDiff);
                                                    ClearStaleExternalArrowsOnRawBoardChange(boardDiff.ChangedSquares);

                                                    if (_latencyT0Utc == DateTime.MinValue)
                                                {
                                                    _latencyT0Utc = DateTime.UtcNow;
                                                    _latencyT0ChangedSquares = boardDiff.ChangedSquares;
                                                }

                                                bool optimisticFenApplied = TryApplyOptimisticChangedSquaresFen(boardDiff, boardView);
                                                if (optimisticFenApplied)
                                                {
                                                    shouldRunFenDetection = false;
                                                    _invalidFenFrames = 0;
                                                    _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;
                                                }

                                                DateTime nowRawChange = DateTime.UtcNow;
                                                _lastExternalRawBoardChangeUtc = nowRawChange;
                                                if (!optimisticFenApplied && _externalRawBoardChangeSettleUntilUtc == DateTime.MinValue)
                                                {
                                                    int settleMs = GetExternalRawBoardChangeSettleMs(boardDiff);
                                                    _externalRawBoardChangeSettleUntilUtc = settleMs > 0
                                                        ? nowRawChange.AddMilliseconds(settleMs)
                                                        : nowRawChange;
                                                }

                                                if (!optimisticFenApplied && nowRawChange < _externalRawBoardChangeSettleUntilUtc)
                                                {
                                                    shouldRunFenDetection = false;
                                                }
                                            }
                                        }
                                        else if (boardDiff != null && boardDiff.ChangedSquares == 0 && _latencyT0Utc != DateTime.MinValue
                                                 && _lastConfirmedFenAtUtc > _latencyT0Utc)
                                        {
                                            // Diff settled back to zero AFTER we recorded a confirm,
                                            // meaning the move resolved cleanly. Reset so the next
                                            // change starts a fresh latency window.
                                            _latencyT0Utc = DateTime.MinValue;
                                            _latencyT0ChangedSquares = 0;
                                        }

                                        if (boardDiff != null && boardDiff.ChangedSquares == 0)
                                        {
                                            if (ShouldForceStaleExternalFenProbe("diff-zero", boardDiff: boardDiff))
                                            {
                                                shouldRunFenDetection = true;
                                                forceFullFenProbe = true;
                                            }
                                            else
                                            {
                                                LogStaleExternalArrowObservation("diff-zero", boardDiff: boardDiff);
                                                _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;
                                                TryApplyStaticLastMoveHighlightTurnHintAndQueue(_currentFEN, boardView);
                                                shouldRunFenDetection = false;
                                            }
                                        }
                                        else if (DateTime.UtcNow < _lastFenRejectionUtc.AddMilliseconds(_fenRejectionCooldownMs))
                                        {
                                            // Recent rejection cooldown: YOLO was
                                            // just producing garbage, almost
                                            // certainly will again. Skip this
                                            // frame so the main loop stays
                                            // responsive and arrows keep tracking.
                                            shouldRunFenDetection = false;
                                        }
                                    }

#if DEBUG
                                    _fpsDiagP2DiffSum += _perfStopwatch.ElapsedMilliseconds - diffStartMs;
#endif

                                    if (!shouldRunFenDetection)
                                    {
                                        _invalidFenFrames = 0;
                                    }
                                    else
                                    {
                                        // === FPS DIAG: time the full FEN sub-step ===
                                        long fenStartMs = _perfStopwatch.ElapsedMilliseconds;
                                        // Upload crop = board + ~3% margin (clamped to what the
                                        // capture region actually has on each side; screen-edge
                                        // clips shrink it gracefully). The margin keeps the
                                        // board-edge coordinate glyphs fully inside the sent
                                        // frame - a box-tight crop clips them and halves the
                                        // coord orientation-oracle recall (18% -> 46% measured).
                                        // Only the UPLOAD inflates; boardView (pixel-diff
                                        // snapshots) and arrow anchoring stay board-tight.
                                        int coordMargin = Math.Max(0, Math.Min(
                                            (int)Math.Ceiling(Math.Max(trackedBox.Width, trackedBox.Height) * 0.03),
                                            Math.Min(
                                                Math.Min(localBoardX, localBoardY),
                                                Math.Min(
                                                    captureRegion.Width - (localBoardX + trackedBox.Width),
                                                    captureRegion.Height - (localBoardY + trackedBox.Height)))));
                                        var fenUploadRect = new Rect(
                                            localBoardX - coordMargin,
                                            localBoardY - coordMargin,
                                            trackedBox.Width + coordMargin * 2,
                                            trackedBox.Height + coordMargin * 2);
                                        var fen = _detector.ProcessBoard(
                                            regionFrame, fenUploadRect, false, forceFullFenProbe,
                                            recordBoardRectSample: true,
                                            // Orientation grace: ship these frames at native res so
                                            // the server's coord model can actually read big-board
                                            // rank digits (crushed to ~6px by the 640 transform).
                                            coordProbeNative: DateTime.UtcNow < _orientationCoordGraceUntilUtc);
#if DEBUG
                                        _fpsDiagP2FenSum += _perfStopwatch.ElapsedMilliseconds - fenStartMs;
                                        _fpsDiagP2FenCalls++;
#endif

                                        bool suppressExternalFenForUnstableGeometry =
                                            !string.IsNullOrEmpty(fen) &&
                                            fen != "8/8/8/8/8/8/8/8 w KQkq - 0 1" &&
                                            !IsActiveAnalysisBoardFen(fen) &&
                                            IsExternalBoardGeometryUnstable();

                                        if (suppressExternalFenForUnstableGeometry)
                                        {
                                            // A detector response can arrive while the tracked crop is
                                            // still moving. Do not normalize, confirm, or rebase the
                                            // snapshot from that transitional image; doing so can pair a
                                            // rotated canonical FEN with the old display projection and
                                            // re-arm the raw-change fuse as soon as geometry settles.
                                            _invalidFenFrames = 0;
                                            ResetSuspectEmptyFenFrames();
                                            ResetPendingFenCandidate();
                                            LogDiag(
                                                "FEN",
                                                $"observation ignored while external board geometry is unstable " +
                                                $"(raw={GetBoardPosition(fen)})");
                                        }
                                        else if (!string.IsNullOrEmpty(fen) && fen != "8/8/8/8/8/8/8/8 w KQkq - 0 1")
                                        {
                                            _invalidFenFrames = 0;
                                            ResetSuspectEmptyFenFrames();
                                            _fenCount++;
                                            Log($"[FEN DETECTED] Count: {_fenCount}, FEN: {fen.Substring(0, Math.Min(30, fen.Length))}...");

                                            string candidateFen = fen;
                                            bool? detectedBoardFlipped = null;
                                            bool authoritativeOrientation = false;

                                            if (!IsActiveAnalysisBoardFen(fen))
                                            {
                                                candidateFen = NormalizeExternalDetectedFen(
                                                    fen,
                                                    out detectedBoardFlipped,
                                                    out authoritativeOrientation);
                                            }

                                            string currentBoardPosition = GetBoardPosition(_currentFEN);
                                            string candidateBoardPosition = GetBoardPosition(candidateFen);
                                            bool sameExternalVisualPlacement =
                                                !_currentFenIsAnalysisBoard &&
                                                !string.IsNullOrWhiteSpace(currentBoardPosition) &&
                                                string.Equals(
                                                    candidateBoardPosition,
                                                    currentBoardPosition,
                                                    StringComparison.Ordinal);

                                            if (candidateFen == _currentFEN || sameExternalVisualPlacement)
                                            {
                                                bool recoveredSamePositionAfterRawChange =
                                                    !_currentFenIsAnalysisBoard &&
                                                    _externalRawBoardChangeSettleUntilUtc != DateTime.MinValue;
                                                bool rawChangeStillSettling =
                                                    recoveredSamePositionAfterRawChange &&
                                                    DateTime.UtcNow < _externalRawBoardChangeSettleUntilUtc;

                                                if (rawChangeStillSettling &&
                                                    boardDiff is not { ChangedSquares: 0 })
                                                {
                                                    string diffText = boardDiff == null
                                                        ? "unknown"
                                                        : boardDiff.ChangedSquares.ToString(CultureInfo.InvariantCulture);
                                                    LogDiag(
                                                        "FEN",
                                                        $"same-FEN observation ignored after raw board change until diff settles " +
                                                        $"(changedSquares={diffText}, raw={GetBoardPosition(fen)}, candidate={GetBoardPosition(candidateFen)})");
                                                }
                                                else
                                                {
                                                    if (sameExternalVisualPlacement && candidateFen != _currentFEN)
                                                    {
                                                        LogDiag(
                                                            "FEN",
                                                            $"absorbing same visual board with metadata-only FEN difference " +
                                                            $"(current={_currentFEN}, observed={candidateFen})");
                                                    }

                                                    if (recoveredSamePositionAfterRawChange &&
                                                        boardDiff is not { ChangedSquares: 0 })
                                                    {
                                                        string diffText = boardDiff == null
                                                            ? "unknown"
                                                            : boardDiff.ChangedSquares.ToString(CultureInfo.InvariantCulture);
                                                        LogDiag(
                                                            "FEN",
                                                            $"same-FEN observation absorbed after raw-change settle " +
                                                            $"(changedSquares={diffText}, raw={GetBoardPosition(fen)}, candidate={GetBoardPosition(candidateFen)})");
                                                    }

                                                    LogStaleExternalArrowObservation("fen-same", observedFen: candidateFen);
                                                    UpdateConfirmedBoardSnapshot(boardView);
                                                    ResetPendingFenCandidate();
                                                    _externalRawBoardChangeSettleUntilUtc = DateTime.MinValue;
                                                    // This frame proved the visible placement is still the
                                                    // confirmed board. Rebase the diff snapshot above and
                                                    // disarm the raw-change fuse before its per-frame check can
                                                    // hide perfectly current arrows. Nonvisual FEN metadata
                                                    // (castling order, counters, inferred turn) must not keep a
                                                    // stale pixel baseline alive.
                                                    _pendingRawHideSinceUtc = DateTime.MinValue;
                                                    _latencyT0Utc = DateTime.MinValue;
                                                    _latencyT0ChangedSquares = 0;
                                                    _kinglessReadStreak = 0; // same-as-confirmed read = plausible board present

                                                    bool externalDisplayOrientationChanged =
                                                        !_currentFenIsAnalysisBoard &&
                                                            ApplyExternalDisplayOrientation(
                                                                detectedBoardFlipped,
                                                                "stable same-FEN observation",
                                                                authoritativeObservation: authoritativeOrientation);

                                                    bool staticHighlightChangedSide = TryApplyStaticLastMoveHighlightTurnHintAndQueue(_currentFEN, boardView);
                                                    if (externalDisplayOrientationChanged)
                                                    {
                                                        HandleExternalDisplayOrientationChanged(
                                                            "external display orientation changed");
                                                    }

                                                    if (!staticHighlightChangedSide &&
                                                        recoveredSamePositionAfterRawChange &&
                                                        _continuousAnalysisEnabled &&
                                                        !_showingMoves &&
                                                        _currentMoveArrows == null)
                                                    {
                                                        bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
                                                        TryQueueAnalysis(requestedPerspective, force: true);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                // Board-gone evidence: consecutive kingless raw reads.
                                                // One is noise; a streak flips the board-gone gates.
                                                string rawPlacementForStreak = GetBoardPosition(fen);
                                                _kinglessReadStreak =
                                                    (rawPlacementForStreak.IndexOf('K') < 0 || rawPlacementForStreak.IndexOf('k') < 0)
                                                        ? _kinglessReadStreak + 1
                                                        : 0;

                                                string confirmDiffText = boardDiff == null
                                                    ? "none"
                                                    : $"{boardDiff.ChangedSquares}sq avg={boardDiff.AverageSquareDifference:F2} max={boardDiff.MaxSquareDifference:F2}";
                                                TraceBoard(
                                                    $"confirm attempt current={GetBoardPosition(_currentFEN)} " +
                                                    $"raw={GetBoardPosition(fen)} candidate={GetBoardPosition(candidateFen)} " +
                                                    $"diff={confirmDiffText} forceFull={forceFullFenProbe}");

                                                string confirmedFen = ConfirmFenObservation(candidateFen);
                                                if (!string.IsNullOrEmpty(confirmedFen))
                                                {
                                                    bool externalBoardFlipChanged = ApplyExternalDisplayOrientation(
                                                        detectedBoardFlipped,
                                                        "confirmed FEN observation",
                                                        authoritativeObservation: authoritativeOrientation);
                                                    confirmedFen = MergeDetectedFenWithHistory(_currentFEN, confirmedFen);

                                                    // EXPERIMENT A (shadow): could we have inferred this move
                                                    // locally from the prior position + the changed squares,
                                                    // with no vision round-trip? Runs off-thread; _currentFEN
                                                    // here is still the PRIOR position (ApplyConfirmedFen runs
                                                    // next). Server result stays authoritative.
                                                    if (PredictiveVision.ShadowEnabled &&
                                                        boardDiff != null &&
                                                        !_currentFenIsAnalysisBoard &&
                                                        !string.IsNullOrEmpty(_currentFEN))
                                                    {
                                                        PredictiveVision.RunShadowAsync(
                                                            _currentFEN,
                                                            confirmedFen,
                                                            boardDiff.ChangedSquareDetails,
                                                            GetEffectiveBoardFlipped(_currentFEN));
                                                    }

                                                    ApplyConfirmedFen(confirmedFen, boardView);
                                                    _analysisBoardController!.MirrorExternalFen(ApplyInferredExternalTurnToFen(confirmedFen), _externalBoardDetectedFlipped);
                                                    if (externalBoardFlipChanged && string.Equals(confirmedFen, _currentFEN, StringComparison.Ordinal))
                                                    {
                                                        BumpAnalysisSessionVersion();
                                                        _analysisInProgress = false;
                                                        _currentMoveArrows = null;
                                                        _lastAnalysisVariations = null;
                                                        _lastArrowSourceFEN = "";
                                                        ClearActiveArrows();
                                                        ResetAnalysisSchedulingState();
                                                        bool requestedPerspective = GetRequestedAnalysisPerspective(_currentFEN, _analysisIsBlackPerspective);
                                                        TryQueueAnalysis(requestedPerspective, force: true);
                                                    }
                                                }
                                                else
                                                {
                                                    TraceBoard(
                                                        $"confirm rejected current={GetBoardPosition(_currentFEN)} " +
                                                        $"candidate={GetBoardPosition(candidateFen)} raw={GetBoardPosition(fen)} " +
                                                        $"diff={confirmDiffText}");
                                                    LogStaleExternalArrowObservation(
                                                        "fen-confirm-rejected",
                                                        boardDiff: boardDiff,
                                                        observedFen: candidateFen);
                                                }
                                            }
                                        }
                                        else if (fen == "8/8/8/8/8/8/8/8 w KQkq - 0 1")
                                        {
                                            ResetPendingFenCandidate();
                                            if (ShouldIgnoreTransientInvalidFen())
                                            {
                                                Log("[FEN] Ignored transient empty-board frame");
                                            }
                                            else
                                            {
                                                _suspectEmptyFenFrames++;
                                                if (_suspectEmptyFenFrames < _suspectEmptyFenThreshold)
                                                {
                                                    Log($"[FEN] Suspect empty-board frame {_suspectEmptyFenFrames}/{_suspectEmptyFenThreshold}");
                                                }
                                                else
                                                {
                                                    HandleInvalidFenDetection();
                                                    Log("[FEN] Empty board detected");
                                                    if (HasHealthyTrackedExternalWindow())
                                                    {
                                                        _boardContentLostFrames = 0;
                                                        _requestBoardRefresh = true;
                                                        _scheduledVerifyUtc = DateTime.UtcNow.AddMilliseconds(_boardDetectionAcquireMinIntervalMs);
                                                        LogDiag("WINTRACK", "empty FEN ignored while tracked window is healthy; keeping window attachment");
                                                    }
                                                    else
                                                    {
                                                        HandleBoardContentLostInHealthyWindow();
                                                    }
                                                    continue;
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    HandleInvalidFenDetection();
                                    Log($"[FEN ERROR] {ex.Message}");
                                }
                            }

                            // Per-frame calls are cheap after change-detection: static
                            // boards dispatch nothing, while real movement still tracks
                            // at the capture cadence.
                            UpdateTrackedSurfaceGeometry(trackedBox);

                            MaybeRecoverStableSparseArrows();
                            ReconcileExternalArrowOverlay();
                            // Geometry-drift self-heal runs OUTSIDE the reconciler:
                            // the reconciler stands down on every mouse interaction,
                            // and a user clicking around must not starve the very
                            // repair they are waiting for.
                            CheckExternalBoardGeometryDrift(DateTime.UtcNow);
                        }
                        phase2End = _perfStopwatch.ElapsedMilliseconds;
                        phase2Ms = phase2End - phase2Start;
                    }

                    // Only count frames when overlay is active
                    if (_isTracking)
                    {
                        _frameCount++;
                    }
                    CheckPendingRawHideFuse();
                    UpdatePerformanceMetrics();

#if DEBUG
                    // === FPS DIAGNOSTICS ===
                    // Aggregate phase timing over a window and log periodically.
                    var totalFrameMs = _perfStopwatch.ElapsedMilliseconds - frameStart;
                    _fpsDiagPhase1Sum += phase1Ms;
                    _fpsDiagPhase2Sum += phase2Ms;
                    _fpsDiagOtherSum += Math.Max(0, totalFrameMs - phase1Ms - phase2Ms);
                    _fpsDiagFrames++;
                    if (_fpsDiagFrames >= 30)
                    {
                        // Append to a log file next to the exe so we can read
                        // results without needing a console (this is a windowed
                        // Release build). Errors swallowed - diagnostic is
                        // best-effort, must never break the app.
                        if (_diagLoggingEnabled)
                        {
                            try
                            {
                                long fenAvg = _fpsDiagP2FenCalls > 0 ? _fpsDiagP2FenSum / _fpsDiagP2FenCalls : 0;
                                string logLine = $"{DateTime.Now:HH:mm:ss.fff} [FPS-DIAG] last30: phase1(board)={_fpsDiagPhase1Sum}ms phase2(fen)={_fpsDiagPhase2Sum}ms [p2-cap={_fpsDiagP2CaptureSum} p2-mat={_fpsDiagP2MatSum} p2-pixel={_fpsDiagP2PixelSum} pixel-skips={_fpsDiagP2PixelSkips} p2-diff={_fpsDiagP2DiffSum} p2-fen={_fpsDiagP2FenSum} fen-calls={_fpsDiagP2FenCalls} fen-avg={fenAvg}ms region={_fpsDiagP2RegionWidth}x{_fpsDiagP2RegionHeight} capture={ScreenCapture.GetCaptureMode()}] other={_fpsDiagOtherSum}ms total={_fpsDiagPhase1Sum + _fpsDiagPhase2Sum + _fpsDiagOtherSum}ms avg/frame={(_fpsDiagPhase1Sum + _fpsDiagPhase2Sum + _fpsDiagOtherSum) / 30}ms{Environment.NewLine}";
                                AppendDiagnosticLine(logLine);
                            }
                            catch { }
                        }
                        _fpsDiagPhase1Sum = 0;
                        _fpsDiagPhase2Sum = 0;
                        _fpsDiagOtherSum = 0;
                        _fpsDiagFrames = 0;
                        _fpsDiagP2CaptureSum = 0;
                        _fpsDiagP2MatSum = 0;
                        _fpsDiagP2PixelSum = 0;
                        _fpsDiagP2PixelSkips = 0;
                        _fpsDiagP2DiffSum = 0;
                        _fpsDiagP2FenSum = 0;
                        _fpsDiagP2FenCalls = 0;
                    }
#endif

                    // Target 60+ FPS
                    var frameTime = _perfStopwatch.ElapsedMilliseconds - frameStart;
                    if (frameTime < 16)
                    {
                        await Task.Delay((int)(16 - frameTime), cts.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now >= _lastMainLoopExceptionRecordUtc.AddSeconds(10))
                    {
                        _lastMainLoopExceptionRecordUtc = now;
                        Log($"[TRACKING LOOP] Frame failed: {ex}");
                        WriteCrashRecord("MainTrackingLoop", ex, terminating: false);
                    }

                    // Avoid a hot retry loop if a persistent frame-path fault
                    // starts failing before the normal frame pacing delay.
                    await Task.Delay(25);
                }
            }

            bool plannedMainShutdown = cts.IsCancellationRequested;
            bool uiThreadFailed = _uiThreadFailure != null ||
                (_uiMessageLoopStopped && !plannedMainShutdown);
            _uiShutdownExpected = true;

            // Cleanup
            DisableHighResolutionTimer();
            DisposeAllEngines();
            _detector?.Dispose();
            ScreenCapture.Cleanup();
            ResetConfirmedBoardSnapshot();
            if (_overlay != null && _overlay.IsHandleCreated)
            {
                _overlay.BeginInvoke(new Action(() =>
                {
                    _hotkeyController?.DisposeRegisteredListener();
                    DisposeTaskbarIcon();
                }));
            }
            DebugRuntime.Shutdown();
            if (createdNew && _singleInstanceMutex != null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            // Close forms
            if (_evalBar != null && _evalBar.IsHandleCreated)
            {
                _evalBar.BeginInvoke(new Action(() => _evalBar.Close()));
            }

            if (_engineLines != null && _engineLines.IsHandleCreated)
            {
                _engineLines.BeginInvoke(new Action(() => _engineLines.Close()));
            }


            if (_settingsToolbar != null && _settingsToolbar.IsHandleCreated)
            {
                _settingsToolbar.BeginInvoke(new Action(() => _settingsToolbar.Close()));
            }

            _orientationPromptHost?.Dispose();
            _orientationPromptHost = null;

            if (_overlay != null && _overlay.IsHandleCreated)
            {
                _overlay.BeginInvoke(new Action(Application.ExitThread));
            }
            uiThread.Join(1000);

            // Mirror the original `using var keyListener` disposal: tear down the
            // low-level global keyboard hook as Main's body unwinds.
            _hotkeyController?.Dispose();

            Log("\n[INFO] Shutdown complete");
            RefreshDebugView("Shutdown complete");
            if (uiThreadFailed)
            {
                CrashDiagnostics.WriteLifecycleEvent(
                    "UI_FAILURE_CLEANUP_COMPLETED",
                    $"failure={_uiThreadFailure?.GetType().FullName ?? "message-loop-return"}");
            }
            else
            {
                CrashDiagnostics.MarkCleanExit("main loop completed");
            }
        }
        catch (Exception ex)
        {
            Log($"[FATAL] {ex}");
            WriteCrashRecord("Program.Main", ex, terminating: true);
            Environment.ExitCode = 1;
#if DEBUG
            Console.ReadKey(true);
#else
            MessageBox.Show($"Fatal error:\n{ex.Message}", "Chess Kit - Fatal Error", 
                MessageBoxButtons.OK, MessageBoxIcon.Error);
#endif
        }
    }

}
