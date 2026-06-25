using ChessKit;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using static ChessKit.FenText;

// App lifecycle: single-instance, taskbar/tray, engine startup, and license gating.
partial class Program
{
    private static void ShowSingleInstancePromptAndExit()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch
        {
            // Ignore style initialization failures and still show the prompt.
        }

        using var prompt = new SingleInstancePromptForm();
        DialogResult result = prompt.ShowDialog();
        if (result == DialogResult.OK)
        {
            TryActivateExistingInstance();
        }
    }

    private static void TryActivateExistingInstance()
    {
        try
        {
            int currentProcessId = Environment.ProcessId;
            string currentProcessName = Process.GetCurrentProcess().ProcessName;
            var existingProcesses = Process
                .GetProcessesByName(currentProcessName)
                .Where(process => process.Id != currentProcessId)
                .ToArray();

            IntPtr handle = existingProcesses
                .Select(FindBestWindowForProcess)
                .FirstOrDefault(windowHandle => windowHandle != IntPtr.Zero);

            if (handle == IntPtr.Zero)
                return;

            ShowWindowAsync(handle, SW_RESTORE);
            SetForegroundWindow(handle);
        }
        catch
        {
            // If activation fails, just let the second instance exit quietly.
        }
    }

    private static IntPtr FindBestWindowForProcess(Process process)
    {
        try
        {
            if (process.MainWindowHandle != IntPtr.Zero)
                return process.MainWindowHandle;

            IntPtr bestHandle = IntPtr.Zero;
            EnumWindows((windowHandle, _) =>
            {
                GetWindowThreadProcessId(windowHandle, out int windowProcessId);
                if (windowProcessId == process.Id && IsWindowVisible(windowHandle))
                {
                    bestHandle = windowHandle;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return bestHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private const int SW_RESTORE = 9;
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static async Task TryRestartEngine()
    {
        try
        {
            Log("[Recovery] Attempting to restart engine...");

            var enginePath = !string.IsNullOrWhiteSpace(_stockfishPath) && IsUsableEnginePath(_stockfishPath)
                ? _stockfishPath
                : ResolveInitialEnginePath();

            if (IsUsableEnginePath(enginePath))
            {
                _stockfishPath = enginePath;
                LogCurrentEngine("Restarting engine");
                _stockfish = new UCIEngine(enginePath);
                _stockfish.InitialDepth = _settingsToolbar?.GetInitialDepth() ?? 8;
                _stockfish.MaxDepth = BuildLimits.ClampDepth(_settingsToolbar?.GetMaxDepth() ?? BuildLimits.MaxDepth);
                _stockfish.InfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && (_settingsToolbar?.GetInfiniteAnalysis() ?? false);
                _stockfish.InitialThinkTime = 50;
                _stockfish.MaxThinkTime = 2000;
                _stockfish.TimeIncrement = 100;
                _stockfish.DepthIncrement = 2;
                ApplyEngineSpecificSettings(_stockfish);

                ShowLiveEngineStartupFeedback(enginePath);
                if (await _stockfish.StartAsync())
                {
                    Log($"[Recovery] Engine restarted successfully: {Path.GetFileName(enginePath)}");
                    ShowLiveEngineReadyFeedback(enginePath);
                }
                else
                {
                    Log($"[Recovery] Failed to restart engine: {Path.GetFileName(enginePath)}");
                    ShowLiveEngineFailureFeedback(enginePath);
                    _stockfish = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Recovery] Error restarting engine: {ex.Message}");
            _stockfish = null;
        }
    }

    private static void InitializeTaskbarIconAfterStartup()
    {
        void Initialize()
        {
            _systemTray = new SystemTrayController(
                isSettingsToolbarHidden: () => _settingsToolbarHidden,
                onRestoreToolbar: RestoreSettingsToolbarFromTaskbar,
                onOpenAnalysisBoard: () =>
                {
                    if (_analysisBoardForm == null)
                        return;

                    if (_analysisBoardForm.InvokeRequired)
                        _analysisBoardForm.BeginInvoke(new Action(() => _analysisBoardForm.ShowAnalysisBoard()));
                    else
                        _analysisBoardForm.ShowAnalysisBoard();
                },
                onToggleOverlay: () => _hotkeyController!.TriggerToggleOverlay(),
                onShowHardwareId: ShowHardwareIdFromTaskbar,
                onShowLicenseStatus: ShowLicenseStatusFromTaskbar,
                onShowAbout: ShowAboutFromTaskbar,
                onVisitWebsite: OpenChessKitWebsite,
                confirmHideIcon: ConfirmHideTaskbarIconIfNeeded,
                persistShowTaskbarIcon: v => _settingsToolbar?.SyncTaskbarIconState(v),
                onExit: RequestApplicationExit);
            _systemTray.Initialize(_showSystemTrayIconAfterStartup);
            // Free Edition only: the taskbar helper window is a Free-user affordance
            // (upsell + quick actions). Licensed sessions don't get it.
            if (BuildLimits.IsFreeEdition)
            {
                _freeEditionWindow = new FreeEditionWindow(
                    onShowToolbar: RestoreSettingsToolbarFromTaskbar,
                    onOpenAnalysisBoard: () => _analysisBoardForm?.ShowAnalysisBoard(),
                    onToggleOverlay: () => _hotkeyController!.TriggerToggleOverlay(),
                    onShowHardwareId: ShowHardwareIdFromTaskbar,
                    onShowAbout: ShowAboutFromTaskbar,
                    onExit: RequestApplicationExit,
                    onUpgrade: ShowFreeUpsell,
                    onQuickStart: ShowQuickStartFromTaskbar,
                    onOpenSettings: OpenSettingsFromTaskbar,
                    onCheckForUpdates: CheckForUpdatesFromTaskbar);
                _freeEditionWindow.Initialize();
            }
        }

        if (_overlay?.IsHandleCreated == true)
        {
            if (_overlay.InvokeRequired)
                _overlay.BeginInvoke(new Action(Initialize));
            else
                Initialize();
        }
        else
        {
            Initialize();
        }
    }

    private static void RestoreSettingsToolbarFromTaskbar()
    {
        SetSettingsToolbarHidden(false, persist: true);
        _settingsToolbar?.SetEnabled(true);
        ShowToolbarAtFallbackPosition(ignoreHidden: true);
    }

    private static void ShowAboutFromTaskbar()
    {
        using var owner = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new System.Drawing.Size(1, 1),
            Opacity = 0,
            TopMost = true
        };
        owner.Show();

        try
        {
            AboutDialogForm.ShowAbout(owner, () => UpdateChecker.CheckForUpdateAsync(), HandleUpdateDialogChoice);
        }
        finally
        {
            owner.Close();
        }
    }

    private static void HandleUpdateDialogChoice(UpdateCheckResult result, UpdateDialogChoice choice)
    {
        switch (choice)
        {
            case UpdateDialogChoice.Download:
                try
                {
                    UpdateChecker.OpenDownloadPage(result.DownloadUrl);
                }
                catch (Exception ex)
                {
                    Log($"[UPDATE] Failed to open download page: {ex.Message}");
                }
                break;

            case UpdateDialogChoice.Ignore:
                if (!result.IsRequired)
                {
                    AppSettings latestSettings = _appSettingsManager.Load();
                    latestSettings.IgnoredUpdateVersion = result.LatestVersion;
                    _appSettingsManager.Save(latestSettings);
                    Log($"[UPDATE] User ignored version {result.LatestVersion}");
                }
                break;
        }
    }

    private static void ShowHardwareIdFromTaskbar()
    {
        string hardwareId = HardwareIdentity.GetHardwareId();

        using var owner = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new System.Drawing.Size(1, 1),
            Opacity = 0,
            TopMost = true
        };
        owner.Show();
        owner.Hide();

        using var dialog = new NoticeDialogForm(
            "HWID",
            "Chess Kit HWID",
            "Use this HWID when activating or checking a license for this computer.",
            hardwareId,
            NoticeDialogKind.Info,
            copiedToClipboard: false,
            TryCopyTextToClipboard);
        AppIcon.ApplyTo(dialog);
        dialog.Shown += (_, _) => dialog.BeginCopyHardwareIdAsync();
        dialog.ShowDialog(owner);
    }

    private static void OpenChessKitWebsite()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://chesskit.ai",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"[WARN] Failed to open Chess Kit website: {ex.Message}");
        }
    }

    // Free Edition actions surfaced as buttons in the Free Edition taskbar window
    // (FreeEditionWindow). ("Upgrade" reuses ShowFreeUpsell, defined just
    // below.) Always compiled; only reached when the window is shown (Free at
    // runtime).

    // Restore the toolbar and open its expanded settings panel.
    private static void OpenSettingsFromTaskbar()
    {
        RestoreSettingsToolbarFromTaskbar();
        var toolbar = _settingsToolbar;
        if (toolbar == null)
            return;

        void Expand() => toolbar.ShowExpandedSettings();
        if (toolbar.InvokeRequired)
            toolbar.BeginInvoke(new Action(Expand));
        else
            Expand();
    }

    // Short "how to use the Free Edition" guide.
    private static void ShowQuickStartFromTaskbar()
    {
        void Show()
        {
            using var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new System.Drawing.Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            owner.Show();
            try
            {
                NoticeDialogForm.ShowNotice(
                    owner,
                    "Quick start",
                    "Using Chess Kit (Free Edition)",
                    "1.  Open the chessboard you want to analyze in any window.\n" +
                    "2.  Press F1 (or \"Show toolbar\") to bring up the floating toolbar.\n" +
                    "3.  Click W, B, or W+B to analyze for White, Black, or both sides.\n" +
                    "4.  Best-move arrows and the evaluation bar are drawn over the board.\n\n" +
                    "The Free Edition gives full-speed arrows for about 15 moves, then a brief " +
                    "cooldown before assistance resumes. Upgrade for unlimited moves and the human-play engine.",
                    hardwareId: "",
                    NoticeDialogKind.Info,
                    copiedToClipboard: false,
                    copyAction: null,
                    purchaseUrl: "");
            }
            finally
            {
                owner.Close();
            }
        }

        if (_overlay != null && _overlay.InvokeRequired)
            _overlay.BeginInvoke(new Action(Show));
        else
            Show();
    }

    // Manual update check from the Free Edition window. Reuses the same update dialog +
    // choice handler as the Release startup check; shows a plain notice when the
    // app is already current or the check could not reach the server.
    private static async void CheckForUpdatesFromTaskbar()
    {
        UpdateCheckResult result;
        try
        {
            result = await UpdateChecker.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            Log($"[UPDATE] Manual Free Edition update check failed: {ex.Message}");
            result = new UpdateCheckResult { Message = ex.Message };
        }

        void Show()
        {
            if (result.IsUpdateAvailable)
            {
                UpdateDialogChoice choice = UpdateAvailableDialogForm.ShowUpdate(null, result);
                HandleUpdateDialogChoice(result, choice);
                return;
            }

            using var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new System.Drawing.Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            owner.Show();
            try
            {
                bool failed = string.IsNullOrWhiteSpace(result.LatestVersion) &&
                              !string.IsNullOrWhiteSpace(result.Message);
                string body = failed
                    ? $"Couldn't reach the update server.\n\n{result.Message}"
                    : $"You're on the latest version (v{result.CurrentVersion}).";
                NoticeDialogForm.ShowNotice(
                    owner,
                    "Updates",
                    failed ? "Update check failed" : "You're up to date",
                    body,
                    hardwareId: "",
                    NoticeDialogKind.Info,
                    copiedToClipboard: false,
                    copyAction: null,
                    purchaseUrl: "");
            }
            finally
            {
                owner.Close();
            }
        }

        if (_overlay != null && _overlay.InvokeRequired)
            _overlay.BeginInvoke(new Action(Show));
        else
            Show();
    }

    // Small upsell shown from the toolbar's "Free limit · Read more" affordance.
    // Reuses NoticeDialogForm in its no-HWID (upsell) mode: just eyebrow/title/body
    // + an Upgrade button that opens the purchase page.
    private static void ShowFreeUpsell()
    {
        void Show()
        {
            using var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new System.Drawing.Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            owner.Show();
            try
            {
                NoticeDialogForm.ShowNotice(
                    owner,
                    "Free Edition",
                    "You're on the Free Edition",
                    "Board detection and engine arrows work at full speed — you get about 15 " +
                    "moves, then a brief cooldown before they resume.\n\n" +
                    "Upgrade for unlimited moves, the human-play engine, and no watermark.",
                    hardwareId: "",
                    NoticeDialogKind.Info,
                    copiedToClipboard: false,
                    copyAction: null,
                    purchaseUrl: "https://chesskit.ai/purchase.php");
            }
            finally
            {
                owner.Close();
            }
        }

        if (_overlay != null && _overlay.InvokeRequired)
            _overlay.BeginInvoke(new Action(Show));
        else
            Show();
    }

    private static bool TryCopyTextToClipboard(string text)
    {
        static bool TryCopyOnCurrentThread(string value)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(value);
                    return true;
                }
                catch
                {
                    Thread.Sleep(35);
                }
            }

            return false;
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return TryCopyOnCurrentThread(text);

        bool copied = false;
        using var done = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try
            {
                copied = TryCopyOnCurrentThread(text);
            }
            finally
            {
                done.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        done.Wait(TimeSpan.FromSeconds(2));
        return copied;
    }

    // Neutralized downgrade path. A Licensed session must NEVER be forced back to
    // Free for the life of the process, so this no longer clears the verified flag
    // or tears down live analysis/arrow state. Retained (and still injected into the
    // LicenseEnforcer) so the call site compiles; the enforcer no longer invokes it.
    private static void InvalidateFullVersionLicenseRuntimeState(string reason)
    {
        _ = reason;
    }

    private static bool IsPrivateEngineLicenseFailure(string? message) =>
        !string.IsNullOrWhiteSpace(message) &&
        message.IndexOf("engine license", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsPrivateEngineStartupBlocked(string? enginePath, string? message) =>
        IsPrivateEngineLicenseFailure(message) ||
        (IsHumanEnginePath(enginePath) &&
            !string.IsNullOrWhiteSpace(message) &&
            message.IndexOf("handshake failed", StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool IsLicenseConnectionIssueMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.IndexOf("could not reach", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("did not respond in time", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("temporarily unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("unreadable response", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("empty response", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("chesskit.ai", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetEngineFailureMessage(UCIEngine? engine, string fallback)
    {
        if (engine == null)
            return fallback;

        string? failureSummary = engine.GetRecentFailureSummary();
        if (!string.IsNullOrWhiteSpace(failureSummary))
            return failureSummary;

        string? crashDescription = engine.GetRecentCrashDescription();
        return string.IsNullOrWhiteSpace(crashDescription) ? fallback : crashDescription;
    }

    private static string GetEngineStartupFeedback(string? enginePath, string? engineName = null)
    {
        if (IsHumanEnginePath(enginePath))
            return "Verifying Human Chess Engine license...";

        string name = string.IsNullOrWhiteSpace(engineName)
            ? Path.GetFileNameWithoutExtension(enginePath ?? "")
            : engineName;
        return string.IsNullOrWhiteSpace(name) ? "Starting engine..." : $"Starting {name}...";
    }

    private static void ShowLiveEngineStartupFeedback(string? enginePath, string? engineName = null)
    {
        if (!IsHumanEnginePath(enginePath))
            return;

        _settingsToolbar?.ShowTransientStatus(GetEngineStartupFeedback(enginePath, engineName), 8000);
    }

    private static void ShowLiveEngineReadyFeedback(string? enginePath, string? engineName = null)
    {
        if (!IsHumanEnginePath(enginePath))
            return;

        string name = string.IsNullOrWhiteSpace(engineName) ? "Human Chess Engine" : engineName;
        _settingsToolbar?.ShowTransientStatus($"{name} ready.", 2200);
    }

    private static void ShowLiveEngineFailureFeedback(string? enginePath)
    {
        if (IsHumanEnginePath(enginePath))
            _settingsToolbar?.ShowTransientStatus("Human Chess Engine license check failed.", 7000);
    }

    private static void RevertLiveAnalysisAfterPrivateEngineLicenseFailure(string message)
    {
        BumpAnalysisSessionVersion();
        lock (_analysisLock)
        {
            _continuousAnalysisEnabled = false;
            _analysisBothEnabled = false;
            _pendingLiveAnalysisAfterEngineStart = false;
            _pendingLiveAnalysisBothAfterEngineStart = false;
            _waitingForOpponentMove = false;
            _analysisInProgress = false;
            _analysisTimer?.Dispose();
            _analysisTimer = null;
            _currentMoveArrows = null;
            _lastAnalysisVariations = null;
            _lastArrowSourceFEN = "";
            ResetAnalysisSchedulingState();
        }

        ClearActiveArrows();
        _settingsToolbar?.SyncAnalysisState("OFF");
        RefreshDebugView("Private engine license failed");
        Log($"[LICENSE] Private engine blocked live analysis: {message}");
        ShowPrivateEngineLicenseNotice(message);
    }

    private static bool WarnIfLiveAnalysisEngineUnavailableForUserAction()
    {
        if (_stockfish != null)
            return false;

        // Usable = a local binary exists OR the broker serves this (remote://)
        // selection. Honors remote selections that haven't started yet.
        if (!string.IsNullOrWhiteSpace(_stockfishPath) && IsUsableEnginePath(_stockfishPath))
        {
            StartLiveEngineForUserActionInBackground(_stockfishPath);
            Log($"[{DateTime.Now:HH:mm:ss}] Engine is starting in the background");
            return true;
        }

        if (IsHumanEnginePath(_stockfishPath))
        {
            RevertLiveAnalysisAfterPrivateEngineLicenseFailure(
                "The selected Human Chess Engine is not licensed or could not complete license verification.");
            return true;
        }

        // Genuinely no engine selected/available - give visible feedback rather
        // than silently leaving the W/B/W+B buttons inert.
        Log($"[{DateTime.Now:HH:mm:ss}] Engine not available");
        _settingsToolbar?.ShowTransientStatus("Select an engine first (settings)", 4000);
        _settingsToolbar?.SyncAnalysisState("OFF");
        return true;
    }

    private static void StartLiveEngineForUserActionInBackground(string enginePath)
    {
        lock (_liveEngineStartLock)
        {
            if (_liveEngineStartInProgress)
            {
                Log($"[Settings] Engine start already in progress: {Path.GetFileName(_liveEngineStartPath)}");
                return;
            }

            if (string.Equals(_lastLiveEngineStartFailurePath, enginePath, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow < _lastLiveEngineStartFailureUtc.AddMilliseconds(LiveEngineStartFailureCooldownMs))
            {
                Log($"[Settings] Suppressing repeated engine start after recent failure: {Path.GetFileName(enginePath)}");
                return;
            }

            _liveEngineStartInProgress = true;
            _liveEngineStartPath = enginePath;
        }

        Task.Run(async () =>
        {
            bool started = false;
            try
            {
                started = await TryStartLiveEngineForUserActionAsync(enginePath);
                if (started)
                {
                    ResumeLiveAnalysisAfterEngineReady();
                }
                else
                {
                    lock (_liveEngineStartLock)
                    {
                        _lastLiveEngineStartFailureUtc = DateTime.UtcNow;
                        _lastLiveEngineStartFailurePath = enginePath;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Settings] Engine start failed in background: {ex.Message}");
                lock (_liveEngineStartLock)
                {
                    _lastLiveEngineStartFailureUtc = DateTime.UtcNow;
                    _lastLiveEngineStartFailurePath = enginePath;
                }
            }
            finally
            {
                lock (_liveEngineStartLock)
                {
                    _liveEngineStartInProgress = false;
                    _liveEngineStartPath = "";
                }
            }
        });
    }

    private static void ResumeLiveAnalysisAfterEngineReady()
    {
        if (_overlay?.InvokeRequired == true)
        {
            _overlay.BeginInvoke(new Action(ResumeLiveAnalysisAfterEngineReady));
            return;
        }

        if (!_isTracking || _stockfish == null)
            return;

        if (!_pendingLiveAnalysisAfterEngineStart && !_analysisBothEnabled && !_continuousAnalysisEnabled)
            return;

        bool pending = _pendingLiveAnalysisAfterEngineStart;
        bool pendingBoth = _pendingLiveAnalysisBothAfterEngineStart;
        bool pendingBlack = _pendingLiveAnalysisBlackPerspectiveAfterEngineStart;
        _pendingLiveAnalysisAfterEngineStart = false;

        if (pending)
            _analysisBothEnabled = pendingBoth;

        bool desiredBlackPerspective = (pending ? pendingBoth : _analysisBothEnabled)
            ? (_currentFenIsAnalysisBoard ? false : GetRequestedAnalysisPerspective(_currentFEN, pendingBlack))
            : (pending ? pendingBlack : _analysisIsBlackPerspective);

        BumpAnalysisSessionVersion();
        _analysisInProgress = false;
        ToggleContinuousAnalysis(desiredBlackPerspective);
    }

    private static void RememberPendingLiveAnalysisAfterEngineStart(bool isBoth, bool blackPerspective)
    {
        _pendingLiveAnalysisAfterEngineStart = true;
        _pendingLiveAnalysisBothAfterEngineStart = isBoth;
        _pendingLiveAnalysisBlackPerspectiveAfterEngineStart = blackPerspective;
    }

    private static bool IsLiveEngineStartInProgress()
    {
        lock (_liveEngineStartLock)
        {
            return _liveEngineStartInProgress;
        }
    }

    private static bool DidLiveEngineStartFailRecently()
    {
        lock (_liveEngineStartLock)
        {
            return !string.IsNullOrEmpty(_lastLiveEngineStartFailurePath) &&
                   DateTime.UtcNow < _lastLiveEngineStartFailureUtc.AddMilliseconds(LiveEngineStartFailureCooldownMs);
        }
    }

    // Computes the concise reason live analysis can't currently produce arrows
    // (for the toolbar status hint) AND self-heals the most important silent
    // dead-end: a continuous-analysis session whose engine never came up.
    //
    // Why this matters (Task: Free/suspended sessions getting no arrows): the
    // live engine is started LAZILY on the first W/B/W+B press. The first press
    // schedules a background start and a one-shot ResumeLiveAnalysisAfterEngineReady;
    // continuous analysis (and its 250ms dispatch timer) only actually turns on
    // once that resume fires. If the engine never comes up - or that resume is
    // missed for any reason - the session can sit with analysis "enabled" in the
    // UI but _stockfish == null, so TryQueueAnalysis/AnalyzePosition early-return
    // every tick and NOTHING is ever sent to the broker (no arrows, no error).
    // This is edition-agnostic in the code, but it is exactly the "detects
    // positions, never dispatches" symptom. The per-second metrics tick calls
    // this, so whenever we're tracking + analysis is enabled but the engine isn't
    // up, we (re)kick the SAME background start a real W+B press uses - which
    // dials the remote broker and, on success, resumes analysis. A licensed user
    // hits the identical path; the private/human engine stays gated because the
    // background start routes its license/handshake failure through
    // RevertLiveAnalysisAfterPrivateEngineLicenseFailure as before.
    //
    // Returns the hint string ("" = nothing to surface; the slot shows FPS).
    private static string ComputeLiveAnalysisStatusHintAndSelfHeal()
    {
        // Only speak about live EXTERNAL-board analysis. The analysis board form
        // drives its own engine/feedback; an idle session has nothing to explain.
        // We engage whenever continuous analysis is ON, OR a W/B/W+B press is still
        // waiting for the engine to come up (_pendingLiveAnalysisAfterEngineStart).
        // That second case is the critical one: if the FIRST press scheduled a
        // background start that then FAILED (e.g. the broker's free gate briefly
        // rejected the cold ping for a suspended HWID, or a transient network
        // blip), ResumeLiveAnalysisAfterEngineReady never ran, so continuous
        // analysis was never actually turned on - the button can look engaged
        // while _stockfish is null and nothing ever dispatches. Keying on the
        // pending flag lets us retry that start instead of dead-ending silently.
        bool analysisRequested = _continuousAnalysisEnabled || _pendingLiveAnalysisAfterEngineStart;
        if (!_isTracking || !analysisRequested || _currentFenIsAnalysisBoard)
            return "";

        // No engine selected at all - the W/B buttons are inert until one is
        // picked. (A genuinely missing selection; remote selections are usable.)
        if (string.IsNullOrWhiteSpace(_stockfishPath) || !IsUsableEnginePath(_stockfishPath))
            return "Select an engine first";

        // Server put this Free session in a cooldown: report the live countdown.
        // (Licensed sessions are never in cooldown.) Mirrors the watermark copy.
        if (FreeTierServerState.IsInCooldown)
        {
            int secs = FreeTierServerState.CooldownRemainingSeconds;
            return secs > 0
                ? $"Free limit — resets in {FreeTierServerState.FormatCooldown(secs)}"
                : "";
        }

        // Engine not up yet (or it went away). This is the silent dead-end we
        // self-heal: re-kick the background start unless one is already running
        // or we're in the post-failure cooldown.
        if (_stockfish == null)
        {
            if (IsHumanEnginePath(_stockfishPath))
                return "Engine rejected this device";

            if (IsLiveEngineStartInProgress())
                return "Connecting to engine…";

            if (DidLiveEngineStartFailRecently())
                return "Engine unavailable — retrying";

            // Self-heal: bring the selected engine up so analysis can dispatch.
            // Only (re)arm the pending-resume when continuous analysis isn't
            // already on: ResumeLiveAnalysisAfterEngineReady drives
            // ToggleContinuousAnalysis, which would TOGGLE OFF a session already
            // enabled on the same perspective. In practice _stockfish == null here
            // is the first/failed-start case (resume never ran, so analysis is
            // still off), so this just re-arms that resume; if analysis were
            // somehow already on, we still re-dial the engine but leave the
            // existing enabled state alone and let the running dispatch timer pick
            // up the freshly-ready engine on its next tick.
            if (!_continuousAnalysisEnabled)
                RememberPendingLiveAnalysisAfterEngineStart(_analysisBothEnabled, _analysisIsBlackPerspective);
            StartLiveEngineForUserActionInBackground(_stockfishPath);
            LogDiag("ENGINE", "self-heal: live analysis enabled but engine not started; (re)starting engine");
            return "Connecting to engine…";
        }

        // Engine object exists but a remote selection hasn't connected (broker
        // unreachable / ping failed / in retry-cooldown). Real requests will keep
        // retrying; tell the user instead of leaving a blank overlay.
        if (RemoteEngineClient.IsEngineRemotelyServed(_stockfishPath) && _stockfish.IsRemoteEngineActive != true)
            return "Engine unavailable — retrying";

        // Engine is up but no position has been read yet. The first board
        // acquisition + analysis round-trip can take several seconds (remote
        // vision latency), and a blank board makes new users assume it is broken
        // and close it. Surface a loader until the first FEN confirms - arrows
        // follow within a frame or two of that.
        if (string.IsNullOrEmpty(_currentFEN))
        {
            // No position read yet. Distinguish a real connectivity problem (DNS /
            // network / server down — the vision client can't reach chesskit.ai) from
            // the normal cold-start / no-board-on-screen wait, so a failure no longer
            // hides behind a generic "Initializing…".
            BoardVisionConnectionState visionState = BoardVisionDetector.ConnectionState;
            bool serverReachable =
                visionState == BoardVisionConnectionState.Connected ||
                visionState == BoardVisionConnectionState.HttpFallback ||
                visionState == BoardVisionConnectionState.Cooldown ||
                BoardVisionDetector.HadRecentVisionResponse;

            if (!serverReachable)
            {
                return visionState == BoardVisionConnectionState.Connecting
                    ? "Connecting to the analysis server…"
                    : "Can't reach the analysis server — check your connection";
            }

            return "Initializing…";
        }

        // Engine is up with a position. Anything else (waiting for the opponent's
        // move, analysis in flight, arrows already shown) is normal - say nothing.
        return "";
    }

    private static async Task<bool> TryStartLiveEngineForUserActionAsync(string enginePath)
    {
        UCIEngine? engine = null;
        try
        {
            string engineName = Path.GetFileNameWithoutExtension(enginePath);
            Log($"[Settings] Starting selected engine on demand: {Path.GetFileName(enginePath)}");
            ShowLiveEngineStartupFeedback(enginePath, engineName);

            engine = new UCIEngine(enginePath)
            {
                InitialDepth = _settingsToolbar?.GetInitialDepth() ?? 8,
                MaxDepth = BuildLimits.ClampDepth(_settingsToolbar?.GetMaxDepth() ?? BuildLimits.MaxDepth),
                InfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && (_settingsToolbar?.GetInfiniteAnalysis() ?? false),
                InitialThinkTime = 50,
                MaxThinkTime = 2000,
                TimeIncrement = 100,
                DepthIncrement = 2
            };

            ApplyEngineSpecificSettings(engine);

            if (!await engine.StartAsync())
            {
                string failureMessage = GetEngineFailureMessage(engine, $"Failed to start {Path.GetFileName(enginePath)}");
                Log($"[Settings] {failureMessage}");
                ShowLiveEngineFailureFeedback(enginePath);
                if (IsPrivateEngineStartupBlocked(enginePath, failureMessage))
                    RevertLiveAnalysisAfterPrivateEngineLicenseFailure(failureMessage);
                engine.Dispose();
                return false;
            }

            int threads = _settingsToolbar?.GetEngineThreads() ?? 4;
            int hash = _settingsToolbar?.GetHashSize() ?? 128;
            await engine.SendCommandAsync($"setoption name Threads value {threads}");
            await engine.SendCommandAsync($"setoption name Hash value {hash}");
            await engine.SendCommandAsync($"setoption name MultiPV value {GetLiveAnalysisMultiPvCount()}");

            _stockfish = engine;
            ShowLiveEngineReadyFeedback(enginePath, engineName);
            Log($"[Settings] Selected engine ready on demand: {Path.GetFileName(enginePath)}");
            return true;
        }
        catch (Exception ex)
        {
            Log($"[Settings] Failed to start selected engine on demand: {ex.Message}");
            ShowLiveEngineFailureFeedback(enginePath);
            try { engine?.Dispose(); } catch { }
            return false;
        }
    }

    private static void ShowPrivateEngineLicenseNotice(string message)
    {
        lock (_engineLicenseFailureNoticeLock)
        {
            if ((DateTime.UtcNow - _lastEngineLicenseFailureNoticeUtc).TotalSeconds < 8)
                return;

            _lastEngineLicenseFailureNoticeUtc = DateTime.UtcNow;
        }

        void ShowNotice()
        {
            string hardwareId = HardwareIdentity.GetHardwareId();
            bool copiedToClipboard = TryCopyTextToClipboard(hardwareId);
            bool connectionIssue = IsLicenseConnectionIssueMessage(message);
            NoticeDialogForm.ShowNotice(
                null,
                connectionIssue ? "Engine connection issue" : "Engine license required",
                connectionIssue ? "Could not verify Human Chess Engine" : "Human Chess Engine is not licensed",
                connectionIssue
                    ? $"ChessKit could not contact the license service, so no HWID/license decision was received. Check your connection and try again.\n\n{message}"
                    : $"This engine could not be started because the Human Chess Engine license was not approved for this HWID. Select another engine or purchase/activate the engine license to use it.\n\n{message}",
                hardwareId,
                NoticeDialogKind.Warning,
                copiedToClipboard,
                TryCopyTextToClipboard,
                purchaseUrl: connectionIssue ? "" : "https://chesskit.ai/purchase.php");
        }

        try
        {
            if (_overlay?.InvokeRequired == true)
                _overlay.BeginInvoke(new Action(ShowNotice));
            else
                ShowNotice();
        }
        catch
        {
        }
    }

    private static void ShowLicenseStatusFromTaskbar()
    {
        void ShowLoadingDialog()
        {
            string hardwareId = HardwareIdentity.GetHardwareId();
            var loadingItems = new List<LicenseStatusItem>
            {
                new()
                {
                    Name = "ChessKit App",
                    StateText = "Checking",
                    DetailText = "Contacting license server...",
                    VisualState = LicenseStatusVisualState.Info
                },
                new()
                {
                    Name = "Human Chess Engine",
                    StateText = "Checking",
                    DetailText = "Contacting license server...",
                    VisualState = LicenseStatusVisualState.Info
                }
            };

            var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new System.Drawing.Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            var dialog = new LicenseStatusDialogForm(
                loadingItems,
                hardwareId,
                TryCopyTextToClipboard,
                "https://chesskit.ai/purchase.php");
            var cancellation = new CancellationTokenSource();
            bool dialogClosed = false;

            AppIcon.ApplyTo(dialog);
            dialog.FormClosed += (_, _) =>
            {
                dialogClosed = true;
                try { cancellation.Cancel(); } catch { }
                try { cancellation.Dispose(); } catch { }
                try { owner.Dispose(); } catch { }
            };

            owner.Show();
            owner.Hide();
            dialog.Show(owner);

            _ = Task.Run(async () =>
            {
                List<LicenseStatusItem> items;

                try
                {
#if DEBUG
                    items = new List<LicenseStatusItem>
                    {
                        new()
                        {
                            Name = "ChessKit App",
                            StateText = "Debug",
                            DetailText = "Server license enforcement is skipped in Debug builds.",
                            VisualState = LicenseStatusVisualState.Info
                        },
                        new()
                        {
                            Name = "Human Chess Engine",
                            StateText = "Debug",
                            DetailText = "Engine license enforcement is skipped in Debug builds.",
                            VisualState = LicenseStatusVisualState.Info
                        }
                    };
#else
                    LicenseValidationResult appResult = await LicenseValidator.ValidateFullVersionAsync(cancellation.Token);
                    if (cancellation.IsCancellationRequested || dialogClosed)
                        return;

                    LicenseValidationResult engineResult = await LicenseValidator.ValidateProductAsync("HumanChessEngine", "EngineRelease", cancellation.Token);
                    if (cancellation.IsCancellationRequested || dialogClosed)
                        return;

                    items = new List<LicenseStatusItem>
                    {
                        BuildLicenseStatusItem("ChessKit App", appResult),
                        BuildLicenseStatusItem("Human Chess Engine", engineResult)
                    };
#endif
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested || dialogClosed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log($"[LICENSE] Status dialog check failed: {ex.Message}");
                    items = new List<LicenseStatusItem>
                    {
                        new()
                        {
                            Name = "ChessKit App",
                            StateText = "Check failed",
                            DetailText = "Could not complete the license status check. Try again in a moment.",
                            VisualState = LicenseStatusVisualState.Warning
                        },
                        new()
                        {
                            Name = "Human Chess Engine",
                            StateText = "Check failed",
                            DetailText = "Could not complete the engine license status check. Try again in a moment.",
                            VisualState = LicenseStatusVisualState.Warning
                        }
                    };
                }

                try
                {
                    if (!dialogClosed && !dialog.IsDisposed && dialog.IsHandleCreated)
                    {
                        dialog.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!dialogClosed && !dialog.IsDisposed)
                                    dialog.UpdateStatusItems(items);
                            }
                            catch
                            {
                            }
                        }));
                    }
                }
                catch
                {
                }
            });
        }

        try
        {
            if (_overlay?.InvokeRequired == true)
                _overlay.BeginInvoke(new Action(ShowLoadingDialog));
            else
                ShowLoadingDialog();
        }
        catch
        {
        }
    }

    private static LicenseStatusItem BuildLicenseStatusItem(string label, LicenseValidationResult result)
    {
        if (result.IsLicensed)
        {
            return new LicenseStatusItem
            {
                Name = label,
                StateText = "Licensed",
                DetailText = FormatLicenseExpiry(result),
                VisualState = LicenseStatusVisualState.Licensed
            };
        }

        LicenseStatusVisualState visualState = result.State == LicenseValidationState.NetworkError
            ? LicenseStatusVisualState.Warning
            : LicenseStatusVisualState.Error;

        string state = result.State switch
        {
            LicenseValidationState.NetworkError => "Network issue",
            LicenseValidationState.InvalidResponse => "Check failed",
            _ => "Not licensed"
        };

        string detail = string.IsNullOrWhiteSpace(result.Message)
            ? "This product is not active for this HWID."
            : result.Message;

        return new LicenseStatusItem
        {
            Name = label,
            StateText = state,
            DetailText = detail,
            VisualState = visualState
        };
    }

    private static string FormatLicenseExpiry(LicenseValidationResult result)
    {
        if (!result.ExpiresAtUtc.HasValue)
            return "Active license. No expiration date was provided.";

        DateTime nowUtc = result.ServerTimeUtc ?? DateTime.UtcNow;
        TimeSpan remaining = result.ExpiresAtUtc.Value - nowUtc;
        string exact = result.ExpiresAtUtc.Value.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        string relative = FormatRemainingLicenseTime(remaining);
        string plan = string.IsNullOrWhiteSpace(result.Plan) ? "" : $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.Plan)} plan. ";
        return $"{plan}Expires {exact} ({relative}).";
    }

    private static string FormatRemainingLicenseTime(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "expired";

        int days = (int)Math.Floor(remaining.TotalDays);
        if (days >= 2)
            return $"{days} days left";
        if (days == 1)
            return "1 day left";

        int hours = (int)Math.Floor(remaining.TotalHours);
        if (hours >= 2)
            return $"{hours} hours left";
        if (hours == 1)
            return "1 hour left";

        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return minutes == 1 ? "1 minute left" : $"{minutes} minutes left";
    }

    private static void StartStartupUpdateCheck()
    {
#if DEBUG
        // Debug never performs the network update check.
        return;
#else
        _ = Task.Run(async () =>
        {
            UpdateCheckResult result = await UpdateChecker.CheckForUpdateAsync();
            if (!result.IsUpdateAvailable)
            {
                if (!string.IsNullOrWhiteSpace(result.Message))
                    Log($"[UPDATE] No update shown: {result.Message}");
                return;
            }

            AppSettings settings = _appSettingsManager.Load();
            if (!result.IsRequired &&
                string.Equals(settings.IgnoredUpdateVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[UPDATE] Ignored version still current: {result.LatestVersion}");
                return;
            }

            void ShowUpdate()
            {
                UpdateDialogChoice choice = UpdateAvailableDialogForm.ShowUpdate(null, result);
                HandleUpdateDialogChoice(result, choice);
            }

            try
            {
                if (_overlay?.InvokeRequired == true)
                    _overlay.BeginInvoke(new Action(ShowUpdate));
                else
                    ShowUpdate();
            }
            catch (Exception ex)
            {
                Log($"[UPDATE] Failed to show update dialog: {ex.Message}");
            }
        });
#endif
    }

    // Sticky "is this session Licensed" flag. Free Edition when false. Latches true
    // the first time a license verifies this session and never goes back (see
    // LicenseEnforcer). This is the single source of truth the runtime edition gate
    // (BuildLimits.IsFreeEdition => !licensed) reads.
    private static bool HasVerifiedFullVersionLicense()
    {
        return _licenseEnforcer?.IsVerified ?? false;
    }

    // Core analysis features (external board tracking + analysis) run in BOTH
    // editions: Free runs them subject to the BuildLimits caps (move limits,
    // depth/threads, watermark), Licensed runs them uncapped. The old per-feature
    // license hard-gate only existed because the Free Edition was a separate build; in the
    // single runtime-gated build the limits live in BuildLimits, so this gate must
    // never block the limited Free Edition out of its core feature. It is kept as a
    // single chokepoint in case a future feature needs a hard Licensed-only gate.
    private static bool EnsureLicensedFeatureAvailable(string featureName, bool notifyUser = false)
    {
        _ = featureName;
        _ = notifyUser;
        return true;
    }

    // Builds the AnalysisBoardController with focused delegates/accessors into the
    // live core. Shared orientation/snapshot state stays owned by Program and is read
    // through these accessors so there is a single source of truth.
    private static AnalysisBoardController CreateAnalysisBoardController()
    {
        return new AnalysisBoardController(
            log: Log,
            refreshDebugView: RefreshDebugView,
            buildArrowsForFen: BuildArrowsForFen,
            getSideToMove: GetSideToMove,
            getBoardPosition: GetBoardPosition,
            ensureLicensedFeatureAvailable: (feature, notifyUser) => EnsureLicensedFeatureAvailable(feature, notifyUser),
            tryResolveOrientationDecision: TryResolveOrientationDecision,
            isUsableEnginePath: IsUsableEnginePath,
            isHumanEnginePath: IsHumanEnginePath,
            getEngineFailureMessage: GetEngineFailureMessage,
            getEngineStartupFeedback: (path, name) => GetEngineStartupFeedback(path, name),
            isPrivateEngineStartupBlocked: IsPrivateEngineStartupBlocked,
            showPrivateEngineLicenseNotice: ShowPrivateEngineLicenseNotice,
            applyEngineSpecificSettings: ApplyEngineSpecificSettings,
            isActiveAnalysisBoardFen: IsActiveAnalysisBoardFen,
            tryGetActiveAnalysisBoardSnapshot: TryGetActiveAnalysisBoardSnapshot,
            tryGetStoredAnalysisBoardSnapshot: TryGetStoredAnalysisBoardSnapshot,
            analysisBoardForm: () => _analysisBoardForm,
            getGameAnalysisForm: () => _gameAnalysisForm,
            setGameAnalysisForm: form => _gameAnalysisForm = form,
            currentFen: () => _currentFEN,
            currentFenIsAnalysisBoard: () => _currentFenIsAnalysisBoard,
            externalBoardDetectedFlipped: () => _externalBoardDetectedFlipped,
            applyInferredExternalTurnToFen: ApplyInferredExternalTurnToFen,
            humanAdaptiveEnabled: () => _humanAdaptiveEnabled,
            humanPlayProfile: () => _humanPlayProfile,
            quickArrowThinkTimeMs: () => _quickArrowThinkTimeMs,
            quickArrowDepth: () => _quickArrowDepth,
            initialBoardPosition: () => InitialBoardPosition,
            initialBoardPositionRotated: () => InitialBoardPositionRotated);
    }

}
