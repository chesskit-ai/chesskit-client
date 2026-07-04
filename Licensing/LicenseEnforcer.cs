namespace ChessKit
{
    /// <summary>
    /// Owns the full-version license gate: startup verification, the background
    /// re-verification monitor, and the user-facing "license expired" notice.
    ///
    /// REVENUE-CRITICAL. There is a single runtime-gated build: the app runs as the
    /// limited Free Edition unless an active license is verified this session, in
    /// which case it is Licensed. This class never aborts startup — if no license is
    /// found (including a network error) the app simply continues as Free. The
    /// background monitor may later UPGRADE Free -> Licensed when a purchase lands,
    /// but it must never downgrade: once verified this session, the app stays
    /// Licensed for the life of the process (a Licensed session must never silently
    /// drop a user back to Free over a transient check).
    ///
    /// The enforcer talks to the rest of the app only through the delegates injected
    /// at construction time. It calls the licensing primitives
    /// (<see cref="LicenseValidator"/>, <see cref="HardwareIdentity"/>) and the
    /// shared <see cref="NoticeDialogForm"/> directly.
    /// </summary>
    internal sealed class LicenseEnforcer
    {
        private readonly Action<string> _log;
        private readonly Func<string, bool> _tryCopyTextToClipboard;
        private readonly Action _closeStartupStatus;
        private readonly Func<Control?> _getOverlay;
        // Program's InvalidateFullVersionLicenseRuntimeState: tears down the live
        // analysis/arrow state. Stays in Program because it is deeply entangled with
        // Program's analysis fields; the monitor invokes it via this delegate.
        private readonly Action<string> _invalidateRuntimeState;
        // Atomically claims the one-shot "license failure notice" slot shared with
        // Program's engine/feature-block notices. Returns the prior value, mirroring
        // the original Interlocked.Exchange(ref _licenseFailureNoticeShown, 1).
        private readonly Func<int> _exchangeFailureNoticeShown;

        // Shown ONLY when the server is unreachable during the startup check (a
        // NetworkError), so a paying user is never silently dropped to Free over a
        // transient outage. Returns the user's choice (Retry / ContinueFree / Exit).
        // Null (e.g. Debug) => behave as before and continue as Free.
        private readonly Func<LicenseValidationResult, LicenseUnreachableChoice>? _promptServerUnreachable;

        // STICKY for the life of the process: latched true the first time a license
        // verifies this session and NEVER reset to false. The rest of the app reads
        // it (via Program.HasVerifiedFullVersionLicense) to decide Free vs Licensed.
        // volatile because the background monitor writes it from a worker thread
        // while the hot loop reads it. A Licensed session must never downgrade to
        // Free, so nothing in this class (or its callers) ever clears it.
        private volatile bool _fullVersionLicenseVerified = false;

        private CancellationTokenSource? _licenseMonitorCancellation = null;
        private Task? _licenseMonitorTask = null;

        public LicenseEnforcer(
            Action<string> log,
            Func<string, bool> tryCopyTextToClipboard,
            Action closeStartupStatus,
            Func<Control?> getOverlay,
            Action<string> invalidateRuntimeState,
            Func<int> exchangeFailureNoticeShown,
            Func<LicenseValidationResult, LicenseUnreachableChoice>? promptServerUnreachable = null)
        {
            _log = log;
            _tryCopyTextToClipboard = tryCopyTextToClipboard;
            _closeStartupStatus = closeStartupStatus;
            _getOverlay = getOverlay;
            _invalidateRuntimeState = invalidateRuntimeState;
            _exchangeFailureNoticeShown = exchangeFailureNoticeShown;
            _promptServerUnreachable = promptServerUnreachable;
        }

        /// <summary>
        /// True once an active full-version license has been verified this session.
        /// Sticky for the life of the process: the gate the rest of the app reads to
        /// decide Free vs Licensed.
        /// </summary>
        public bool IsVerified => _fullVersionLicenseVerified;

        /// <summary>
        /// Intentionally a no-op. Kept so existing call sites (Program's
        /// runtime-state teardown) compile, but a Licensed session must NEVER
        /// downgrade to Free, so the sticky verified flag is never cleared.
        /// </summary>
        public void MarkUnverified()
        {
            // Deliberately does nothing: the licensed flag is sticky for the process
            // lifetime. See _fullVersionLicenseVerified.
        }

        /// <summary>
        /// Startup license verification. ALWAYS returns true (the app never aborts
        /// startup): if an active license is verified it latches Licensed and starts
        /// the background monitor; otherwise the app continues as the limited Free
        /// Edition and no blocking "License required" dialog is shown. The monitor is
        /// started either way so a later purchase can upgrade Free -> Licensed.
        /// </summary>
        public async Task<bool> EnforceAsync()
        {
            while (true)
            {
                LicenseValidationResult result;
                try
                {
                    result = await LicenseValidator.ValidateFullVersionAsync();
                }
                catch (Exception ex)
                {
                    // Unexpected fault: we couldn't confirm the license either way.
                    // Treat it as a server-unreachable network error so the user is
                    // offered the same Retry / Free / Exit choice below rather than a
                    // silent (and alarming) drop to Free.
                    _log($"[LICENSE] Verification error: {ex.Message}");
                    result = new LicenseValidationResult
                    {
                        State = LicenseValidationState.NetworkError,
                        HardwareId = HardwareIdentity.GetHardwareId(),
                        Message = ex.Message
                    };
                }

                if (result.IsLicensed)
                {
                    _fullVersionLicenseVerified = true;
                    LicenseStatusInfo.SetReason(LicenseInactiveReason.None);
                    _log($"[LICENSE] Active license verified. Plan={result.Plan}, Expires={result.ExpiresAtUtc:O}");
                    StartMonitor(result);
                    return true;
                }

                // The server was REACHED and gave a real answer of "no" (not licensed /
                // expired / suspended / revoked), or an untrusted response. That is not
                // an outage, so keep the established silent Free behaviour — we must not
                // nag a genuinely-unlicensed user with a connection dialog. Derive the
                // reason from the precise status for the watermark/chip.
                if (result.State != LicenseValidationState.NetworkError)
                {
                    LicenseInactiveReason reason = LicenseStatusInfo.FromStatus(result.Status);
                    LicenseStatusInfo.SetReason(reason);
                    _log($"[LICENSE] No active license; continuing as Free Edition. State={result.State}, Status={result.Status}, Reason={reason}");
                    StartMonitor(result);
                    return true;
                }

                // NetworkError => the servers are unreachable. Do NOT silently fall to
                // Free: to a paying user that looks exactly like a revoked license. Ask.
                // (No prompt wired, e.g. Debug -> behave as before and continue Free.)
                LicenseUnreachableChoice choice = _promptServerUnreachable?.Invoke(result)
                    ?? LicenseUnreachableChoice.ContinueFree;

                if (choice == LicenseUnreachableChoice.Retry)
                {
                    _log("[LICENSE] Server unreachable; user chose to retry.");
                    continue;
                }

                if (choice == LicenseUnreachableChoice.Exit)
                {
                    // Caller aborts startup cleanly (mirrors RunStartupFlow == false).
                    _log("[LICENSE] Server unreachable; user chose to exit.");
                    return false;
                }

                // ContinueFree: run as Free for this session. The background monitor
                // keeps polling and will UPGRADE Free -> Licensed automatically once
                // the servers are reachable again — no restart needed.
                LicenseStatusInfo.SetReason(LicenseInactiveReason.Inactive);
                _log("[LICENSE] Server unreachable; user chose to continue in Free mode.");
                StartMonitor(result);
                return true;
            }
        }

        /// <summary>
        /// Starts the background re-verification monitor. Was
        /// <c>Program.StartFullVersionLicenseMonitor</c>. May UPGRADE Free -&gt;
        /// Licensed; never downgrades a session that has been verified.
        /// </summary>
        public void StartMonitor(LicenseValidationResult lastResult)
        {
            if (_licenseMonitorTask != null)
                return;

            _licenseMonitorCancellation = new CancellationTokenSource();
            CancellationToken token = _licenseMonitorCancellation.Token;
            _licenseMonitorTask = Task.Run(async () =>
            {
                LicenseValidationResult current = lastResult;
                while (!token.IsCancellationRequested)
                {
                    TimeSpan delay = GetNextMonitorDelay(current);
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        current = await LicenseValidator.ValidateFullVersionAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log($"[LICENSE] Background check error (ignored): {ex.Message}");
                        continue;
                    }

                    if (current.IsLicensed)
                    {
                        // Upgrade path (Free -> Licensed) and the steady-state
                        // licensed re-check both land here. Latch verified and
                        // clear any inactive reason so the watermark/chip drops.
                        _fullVersionLicenseVerified = true;
                        LicenseStatusInfo.SetReason(LicenseInactiveReason.None);
                        _log($"[LICENSE] Background check OK. Plan={current.Plan}, Expires={current.ExpiresAtUtc:O}");
                        continue;
                    }

                    if (current.State == LicenseValidationState.NetworkError)
                    {
                        string networkReason = string.IsNullOrWhiteSpace(current.Message)
                            ? "Temporary network issue while checking license."
                            : current.Message;
                        _log($"[LICENSE] Background check deferred: {networkReason}");
                        continue;
                    }

                    // License is not active. A Licensed session must NEVER downgrade
                    // to Free, so we do not clear the sticky flag and we do not tear
                    // down runtime state. If this session was never licensed (Free
                    // the whole time) there is nothing to invalidate either. Keep
                    // polling so a later purchase can still upgrade us.
                    string reason = string.IsNullOrWhiteSpace(current.Message)
                        ? "The current Chess Kit license is no longer active."
                        : current.Message;
                    _log($"[LICENSE] Background check reported inactive license (no downgrade): {reason}");
                    continue;
                }
            }, token);
        }

        /// <summary>
        /// Computes the delay before the next background license check. Was
        /// <c>Program.GetNextLicenseMonitorDelay</c>.
        /// </summary>
        private TimeSpan GetNextMonitorDelay(LicenseValidationResult result)
        {
            const int defaultMinutes = 10;
            const int minimumSeconds = 20;
            const int expiryGraceSeconds = 3;

            if (result.ExpiresAtUtc.HasValue && result.ServerTimeUtc.HasValue)
            {
                TimeSpan remaining = result.ExpiresAtUtc.Value - result.ServerTimeUtc.Value;
                if (remaining <= TimeSpan.Zero)
                    return TimeSpan.FromSeconds(minimumSeconds);

                if (remaining < TimeSpan.FromMinutes(defaultMinutes))
                    return remaining + TimeSpan.FromSeconds(expiryGraceSeconds);
            }

            return TimeSpan.FromMinutes(defaultMinutes);
        }

        /// <summary>
        /// Shows the one-shot "license expired" notice. Retained for completeness
        /// but no longer invoked from the monitor: the app never forces a Licensed
        /// session back to Free, so it does not interrupt the user with this dialog.
        /// </summary>
        private void ShowInvalidatedNotice(LicenseValidationResult result, string reason)
        {
            if (_exchangeFailureNoticeShown() != 0)
                return;

            void ShowNotice()
            {
                string hardwareId = string.IsNullOrWhiteSpace(result.HardwareId)
                    ? HardwareIdentity.GetHardwareId()
                    : result.HardwareId;
                bool copiedToClipboard = _tryCopyTextToClipboard(hardwareId);
                NoticeDialogForm.ShowNotice(
                    null,
                    "License required",
                    "License expired",
                    reason,
                    hardwareId,
                    result.State == LicenseValidationState.NetworkError ? NoticeDialogKind.Warning : NoticeDialogKind.Error,
                    copiedToClipboard,
                    _tryCopyTextToClipboard,
                    "https://chesskit.ai/purchase.php");
            }

            try
            {
                Control? overlay = _getOverlay();
                if (overlay?.InvokeRequired == true)
                    overlay.BeginInvoke(new Action(ShowNotice));
                else
                    ShowNotice();
            }
            catch
            {
            }
        }
    }
}
