using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace ChessKit
{
    /// <summary>
    /// Sends low-volume, first-party application-usage events. The implementation
    /// is compiled only for explicitly enabled distributions; ordinary source
    /// builds retain the same no-op API and contain no active transport.
    /// </summary>
    internal static class AppUsageTelemetryClient
    {
#if CHESSKIT_APP_USAGE_TELEMETRY
        private const string ProductionEndpoint = "https://chesskit.ai/api/app/usage";
        private const int QueueCapacity = 128;
        private const int FenDedupeCapacity = 4096;
        private static readonly TimeSpan ReleaseHeartbeatInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan LimitThrottleInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan MaximumExitFlush = TimeSpan.FromSeconds(2);
        private static readonly object StateLock = new();
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        private static readonly Uri Endpoint = ResolveEndpoint();
        private static readonly TimeSpan HeartbeatInterval = ResolveHeartbeatInterval();
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        private static readonly Lazy<string> AppVersion = new(GetAppVersion);
        private static readonly string ProcessSessionId = Guid.NewGuid().ToString("N").ToLowerInvariant();

        private static readonly HashSet<string> AllowedEventNames = new(StringComparer.Ordinal)
        {
            "app_launch",
            "app_heartbeat",
            "app_shutdown",
            "feature_open",
            "feature_toggle",
            "position_detected",
            "move_detected",
            "analysis_result",
            "free_limit_hit",
            "free_cooldown_blocked",
            "app_error"
        };

        private static readonly HashSet<string> AllowedMetadataKeys = new(StringComparer.Ordinal)
        {
            "mode",
            "feature",
            "transport",
            "status",
            "reason",
            "route",
            "engine_type",
            "engine_name",
            "depth",
            "lines",
            "latency_ms",
            "duration_ms",
            "active_ms",
            "success",
            "enabled",
            "board_visible",
            "window_tracked",
            "source_kind",
            "product",
            "license_state",
            "license_plan",
            "plan",
            "app_edition",
            "is_demo",
            "move_count",
            "moves_remaining",
            "move_limit",
            "plies_used",
            "ply_limit",
            "limit_scope",
            "limit_reached",
            "cooldown_blocked"
        };

        private static readonly HashSet<string> ReservedMetadataKeys = new(StringComparer.Ordinal)
        {
            "product",
            "license_state",
            "license_plan",
            "plan",
            "app_edition",
            "is_demo"
        };

        private static SessionState? _session;
        private static bool _consentGranted;
        private static bool _launchEventQueued;
        private static Func<string>? _licensePlanProvider;

        public static bool IsAvailable => true;

        public static bool IsRunning
        {
            get
            {
                lock (StateLock)
                    return _consentGranted && _session != null;
            }
        }

        /// <summary>
        /// Starts one telemetry session only when the caller has persisted an
        /// affirmative usage-diagnostics choice. Call this after license state is
        /// known so launch events describe the correct runtime edition.
        /// </summary>
        public static void StartSession(bool consentGranted, Func<string>? licensePlanProvider = null)
        {
            if (licensePlanProvider != null)
            {
                lock (StateLock)
                    _licensePlanProvider = licensePlanProvider;
            }

            if (!consentGranted)
            {
                StopSession();
                return;
            }

            SessionState state;
            bool queueLaunch;
            lock (StateLock)
            {
                _consentGranted = true;
                if (_session != null)
                    return;

                state = new SessionState(ProcessSessionId, _licensePlanProvider);
                _session = state;
                queueLaunch = !_launchEventQueued;
                if (queueLaunch)
                    _launchEventQueued = true;
            }

            state.Start();
            if (queueLaunch)
                TryEnqueue(state, CreatePayload(state, "app_launch", "app", success: true));
        }

        /// <summary>
        /// Applies a live consent change. Disabling immediately cancels transport,
        /// clears the in-memory queue, and sends no further event (including a
        /// shutdown event). Enabling starts a fresh per-launch-style session.
        /// </summary>
        public static void SetConsent(bool enabled)
        {
            if (enabled)
                StartSession(consentGranted: true);
            else
                StopSession();
        }

        public static void StopSession()
        {
            SessionState? state;
            lock (StateLock)
            {
                _consentGranted = false;
                state = _session;
                _session = null;
            }

            if (state == null)
                return;

            state.StopImmediately();
            DisposeStateWhenComplete(state);
        }

        public static void QueueFeatureOpen(string feature, string source = "toolbar")
        {
            if (!TryGetSafeIdentifier(feature, out string safeFeature) ||
                !TryGetSafeIdentifier(source, out string safeSource))
            {
                return;
            }

            QueueKnownEvent(
                "feature_open",
                safeSource,
                new Dictionary<string, object?> { ["feature"] = safeFeature },
                success: true);
        }

        public static void QueueFeatureToggle(string feature, bool enabled, string source = "toolbar")
        {
            if (!TryGetSafeIdentifier(feature, out string safeFeature) ||
                !TryGetSafeIdentifier(source, out string safeSource))
            {
                return;
            }

            QueueKnownEvent(
                "feature_toggle",
                safeSource,
                new Dictionary<string, object?>
                {
                    ["feature"] = safeFeature,
                    ["enabled"] = enabled
                },
                success: true);
        }

        public static void QueuePositionDetected(string source, string fen, string mode)
        {
            SessionState? state = GetActiveSession();
            if (state == null ||
                !TryGetSafeIdentifier(source, out string safeSource) ||
                !TryGetSafeIdentifier(mode, out string safeMode) ||
                !state.TryRememberFenEvent("position_detected", safeSource, fen, out string dedupeKey))
            {
                return;
            }

            TelemetryPayload payload = CreatePayload(state, "position_detected", safeSource, success: true);
            AddSanitizedMetadata(payload.Metadata, new Dictionary<string, object?>
            {
                ["mode"] = safeMode,
                ["board_visible"] = true
            });
            if (!TryEnqueue(state, payload))
                state.ForgetFenEvent(dedupeKey);
        }

        public static void QueueMoveDetected(
            string source,
            string fen,
            string mode,
            string limitScope,
            int? remainingMoves = null,
            int? limitMoves = null,
            int? pliesUsed = null,
            int? plyLimit = null)
        {
            SessionState? state = GetActiveSession();
            if (state == null ||
                !TryGetSafeIdentifier(source, out string safeSource) ||
                !TryGetSafeIdentifier(mode, out string safeMode) ||
                !TryGetSafeIdentifier(limitScope, out string safeScope))
            {
                return;
            }
            state.ResetCooldownState(safeScope);
            if (!state.TryRememberFenEvent("move_detected", safeSource, fen, out string dedupeKey))
                return;

            TelemetryPayload payload = CreatePayload(state, "move_detected", safeSource, success: true);
            payload.MoveCount = 1;
            payload.RemainingMoves = NormalizeNonNegative(remainingMoves);
            payload.LimitMoves = NormalizeNonNegative(limitMoves);
            AddSanitizedMetadata(payload.Metadata, new Dictionary<string, object?>
            {
                ["mode"] = safeMode,
                ["limit_scope"] = safeScope,
                ["move_count"] = 1,
                ["moves_remaining"] = payload.RemainingMoves,
                ["move_limit"] = payload.LimitMoves,
                ["plies_used"] = NormalizeNonNegative(pliesUsed),
                ["ply_limit"] = NormalizeNonNegative(plyLimit)
            });
            if (!TryEnqueue(state, payload))
                state.ForgetFenEvent(dedupeKey);
        }

        public static void QueueAnalysisResult(string source, string fen, int? depth = null, int? lines = null)
        {
            SessionState? state = GetActiveSession();
            if (state == null ||
                !TryGetSafeIdentifier(source, out string safeSource) ||
                !state.TryRememberFenEvent("analysis_result", safeSource, fen, out string dedupeKey))
            {
                return;
            }

            TelemetryPayload payload = CreatePayload(state, "analysis_result", safeSource, success: true);
            AddSanitizedMetadata(payload.Metadata, new Dictionary<string, object?>
            {
                ["mode"] = "analysis_result",
                ["depth"] = NormalizeNonNegative(depth),
                ["lines"] = NormalizeNonNegative(lines)
            });
            if (!TryEnqueue(state, payload))
                state.ForgetFenEvent(dedupeKey);
        }

        public static void QueueFreeLimitHit(
            string scope,
            bool cooldownBlocked,
            int? remainingMoves = null,
            int? limitMoves = null,
            int? pliesUsed = null,
            int? plyLimit = null)
        {
            SessionState? state = GetActiveSession();
            if (state == null || !TryGetSafeIdentifier(scope, out string safeScope))
                return;

            bool newlyEnteredCooldown = cooldownBlocked && state.TryMarkCooldownStarted(safeScope);
            bool reportCooldownBlock = cooldownBlocked && !newlyEnteredCooldown;
            string eventName = reportCooldownBlock ? "free_cooldown_blocked" : "free_limit_hit";
            if (!state.TryPassLimitThrottle(eventName, safeScope, out DateTime throttleAcceptedAt))
                return;

            TelemetryPayload payload = CreatePayload(state, eventName, safeScope, success: false);
            payload.ErrorCode = eventName == "free_cooldown_blocked"
                ? "free_cooldown_blocked"
                : "free_limit_reached";
            payload.LimitReached = true;
            payload.CooldownBlocked = reportCooldownBlock;
            payload.RemainingMoves = NormalizeNonNegative(remainingMoves);
            payload.LimitMoves = NormalizeNonNegative(limitMoves);
            AddSanitizedMetadata(payload.Metadata, new Dictionary<string, object?>
            {
                ["limit_scope"] = safeScope,
                ["limit_reached"] = true,
                ["cooldown_blocked"] = reportCooldownBlock,
                ["moves_remaining"] = payload.RemainingMoves,
                ["move_limit"] = payload.LimitMoves,
                ["plies_used"] = NormalizeNonNegative(pliesUsed),
                ["ply_limit"] = NormalizeNonNegative(plyLimit)
            });
            if (!TryEnqueue(state, payload))
            {
                state.RollbackLimitThrottle(eventName, safeScope, throttleAcceptedAt);
                if (newlyEnteredCooldown)
                    state.ResetCooldownState(safeScope);
            }
        }

        /// <summary>
        /// Queues a diagnostic category only when both values are already stable,
        /// lowercase identifier-like tokens. Raw exception text is deliberately
        /// rejected rather than sanitized into a potentially identifying string.
        /// </summary>
        public static void QueueError(string source, string errorCode)
        {
            if (!TryGetSafeIdentifier(source, out string safeSource) ||
                !TryGetSafeIdentifier(errorCode, out string safeErrorCode))
            {
                return;
            }

            SessionState? state = GetActiveSession();
            if (state == null)
                return;

            string throttleScope = safeSource + "_" + safeErrorCode;
            if (!state.TryPassLimitThrottle("app_error", throttleScope, out DateTime throttleAcceptedAt))
                return;

            TelemetryPayload payload = CreatePayload(state, "app_error", safeSource, success: false);
            payload.ErrorCode = safeErrorCode;
            if (!TryEnqueue(state, payload))
                state.RollbackLimitThrottle("app_error", throttleScope, throttleAcceptedAt);
        }

        /// <summary>
        /// General event entry point for future stable callsites. Event names and
        /// metadata keys are allowlisted; string values must be identifier tokens.
        /// </summary>
        public static void QueueEvent(
            string eventName,
            string source,
            IReadOnlyDictionary<string, object?>? metadata = null)
        {
            if (!TryGetSafeIdentifier(eventName, out string safeEventName) ||
                !AllowedEventNames.Contains(safeEventName) ||
                !TryGetSafeIdentifier(source, out string safeSource))
            {
                return;
            }

            QueueKnownEvent(safeEventName, safeSource, metadata, success: true);
        }

        public static void QueueHeartbeat()
        {
            SessionState? state = GetActiveSession();
            if (state != null)
                QueueHeartbeat(state);
        }

        public static void FlushForExit() => FlushForExit(MaximumExitFlush);

        public static void FlushForExit(TimeSpan timeout)
        {
            FlushForExitAsync(timeout).GetAwaiter().GetResult();
        }

        public static Task FlushForExitAsync() => FlushForExitAsync(MaximumExitFlush);

        public static Task FlushForExitAsync(TimeSpan timeout)
        {
            TimeSpan boundedTimeout = timeout <= TimeSpan.Zero
                ? MaximumExitFlush
                : timeout > MaximumExitFlush
                    ? MaximumExitFlush
                    : timeout;
            return FlushCoreAsync(boundedTimeout);
        }

        private static void QueueKnownEvent(
            string eventName,
            string source,
            IReadOnlyDictionary<string, object?>? metadata,
            bool success)
        {
            SessionState? state = GetActiveSession();
            if (state == null)
                return;

            TelemetryPayload payload = CreatePayload(state, eventName, source, success);
            AddSanitizedMetadata(payload.Metadata, metadata);
            TryEnqueue(state, payload);
        }

        private static SessionState? GetActiveSession()
        {
            lock (StateLock)
                return _consentGranted ? _session : null;
        }

        private static bool IsCurrentSession(SessionState state)
        {
            lock (StateLock)
                return _consentGranted && ReferenceEquals(_session, state);
        }

        private static bool TryEnqueue(SessionState state, TelemetryPayload payload)
        {
            return IsCurrentSession(state) && state.TryEnqueue(payload);
        }

        private static void QueueHeartbeat(SessionState state)
        {
            if (!IsCurrentSession(state))
                return;

            DateTime now = DateTime.UtcNow;
            long elapsedMs = state.TakeHeartbeatDurationMs(now);
            TelemetryPayload payload = CreatePayload(state, "app_heartbeat", "app", success: true, capturedAtUtc: now);
            payload.DurationMs = elapsedMs;
            payload.ActiveMs = elapsedMs;
            payload.Metadata["duration_ms"] = elapsedMs;
            payload.Metadata["active_ms"] = elapsedMs;
            state.TryEnqueue(payload);
        }

        private static TelemetryPayload CreatePayload(
            SessionState state,
            string eventName,
            string source,
            bool success,
            DateTime? capturedAtUtc = null)
        {
            bool isFree = BuildLimits.IsFreeEdition;
            string licensePlan = state.GetSafeLicensePlan();
            var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["product"] = "chesskit",
                ["license_state"] = isFree ? "free" : "licensed",
                ["license_plan"] = licensePlan,
                ["app_edition"] = isFree ? "free_demo" : "licensed_release",
                ["is_demo"] = isFree
            };

            return new TelemetryPayload
            {
                EventName = eventName,
                SessionId = state.SessionId,
                LicenseState = isFree ? "free" : "licensed",
                LicensePlan = licensePlan,
                Source = source,
                AppVersion = AppVersion.Value,
                Build = GetBuildLabel(),
                CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow,
                Success = success,
                ErrorCode = "",
                Metadata = metadata
            };
        }

        private static void AddSanitizedMetadata(
            Dictionary<string, object?> destination,
            IReadOnlyDictionary<string, object?>? metadata)
        {
            if (metadata == null)
                return;

            foreach ((string key, object? value) in metadata)
            {
                if (!AllowedMetadataKeys.Contains(key) || ReservedMetadataKeys.Contains(key) || value == null)
                    continue;

                if (TrySanitizeMetadataValue(value, out object? safeValue))
                    destination[key] = safeValue;
            }
        }

        private static bool TrySanitizeMetadataValue(object value, out object? safeValue)
        {
            safeValue = null;
            switch (value)
            {
                case bool boolValue:
                    safeValue = boolValue;
                    return true;
                case byte or sbyte or short or ushort or int or uint or long:
                    try
                    {
                        safeValue = Math.Clamp(Convert.ToInt64(value), -1_000_000_000L, 1_000_000_000L);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                case ulong ulongValue:
                    safeValue = (long)Math.Min(ulongValue, 1_000_000_000UL);
                    return true;
                case float floatValue when float.IsFinite(floatValue):
                    safeValue = Math.Clamp((double)floatValue, -1_000_000_000d, 1_000_000_000d);
                    return true;
                case double doubleValue when double.IsFinite(doubleValue):
                    safeValue = Math.Clamp(doubleValue, -1_000_000_000d, 1_000_000_000d);
                    return true;
                case decimal decimalValue:
                    safeValue = Math.Clamp(decimalValue, -1_000_000_000m, 1_000_000_000m);
                    return true;
                case string stringValue when TryGetSafeIdentifier(stringValue, out string safeString):
                    safeValue = safeString;
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryGetSafeIdentifier(string? value, out string safeValue)
        {
            safeValue = "";
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (trimmed.Length > 64)
                return false;

            var builder = new StringBuilder(trimmed.Length);
            foreach (char ch in trimmed)
            {
                if (ch >= 'A' && ch <= 'Z')
                    builder.Append((char)(ch + ('a' - 'A')));
                else if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')
                    builder.Append(ch);
                else
                    return false;
            }

            safeValue = builder.ToString();
            return safeValue.Length > 0;
        }

        private static int? NormalizeNonNegative(int? value)
        {
            return value.HasValue ? Math.Clamp(value.Value, 0, 1_000_000) : null;
        }

        private static async Task PumpAsync(SessionState state)
        {
            try
            {
                while (await state.Queue.Reader.WaitToReadAsync(state.SendCancellation.Token).ConfigureAwait(false))
                {
                    while (state.Queue.Reader.TryRead(out TelemetryPayload? payload))
                        await SendOnceAsync(payload, state.SendCancellation.Token).ConfigureAwait(false);
                }

                // Preserve lifecycle ordering: the final shutdown marker is sent
                // only after every event that was already accepted into the queue.
                if (state.TryTakeFinalPayload(out TelemetryPayload remainingFinalPayload))
                    await SendOnceAsync(remainingFinalPayload, state.SendCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                DebugRuntime.WriteLine("[AppUsageTelemetry] Background pump stopped.");
            }
        }

        private static async Task HeartbeatLoopAsync(SessionState state)
        {
            try
            {
                using var timer = new PeriodicTimer(HeartbeatInterval);
                while (await timer.WaitForNextTickAsync(state.HeartbeatCancellation.Token).ConfigureAwait(false))
                    QueueHeartbeat(state);
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                DebugRuntime.WriteLine("[AppUsageTelemetry] Heartbeat loop stopped.");
            }
        }

        private static async Task SendOnceAsync(TelemetryPayload payload, CancellationToken cancellationToken)
        {
            try
            {
                string hwid = GetSafeHardwareId();
                if (hwid.Length == 0)
                    return;

                payload.Hwid = hwid;
                if (payload.EventName == "app_heartbeat")
                {
                    try
                    {
                        SystemUsageSnapshot usage = SystemUsageTelemetry.Capture();
                        payload.ProcessCpuPercent = usage.ProcessCpuPercent;
                        payload.SystemCpuPercent = usage.SystemCpuPercent;
                        payload.GpuPercent = usage.GpuPercent;
                    }
                    catch
                    {
                        // Optional measurements must never prevent the heartbeat.
                    }
                }

                string json = JsonSerializer.Serialize(payload, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await Http.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    DebugRuntime.WriteLine($"[AppUsageTelemetry] Send rejected: HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
                DebugRuntime.WriteLine("[AppUsageTelemetry] Send failed: network_error.");
            }
            catch
            {
                DebugRuntime.WriteLine("[AppUsageTelemetry] Send failed: unexpected_error.");
            }
        }

        private static async Task FlushCoreAsync(TimeSpan timeout)
        {
            SessionState? state;
            lock (StateLock)
            {
                state = _session;
                _session = null;
                _consentGranted = false;
            }

            if (state == null)
                return;

            state.BeginClosing();
            DateTime now = DateTime.UtcNow;
            TelemetryPayload shutdown = CreatePayload(state, "app_shutdown", "app", success: !state.HasError, capturedAtUtc: now);
            shutdown.DurationMs = Math.Max(0L, (long)(now - state.LaunchedAtUtc).TotalMilliseconds);
            shutdown.ActiveMs = shutdown.DurationMs;
            shutdown.Metadata["duration_ms"] = shutdown.DurationMs;
            shutdown.Metadata["active_ms"] = shutdown.ActiveMs;
            state.TryEnqueueFinal(shutdown);
            state.CompleteQueue();

            Task completed = await Task.WhenAny(state.PumpTask, Task.Delay(timeout)).ConfigureAwait(false);
            if (completed == state.PumpTask)
            {
                try { await state.PumpTask.ConfigureAwait(false); } catch { }
                state.Dispose();
                return;
            }

            state.CancelTransport();
            DisposeStateWhenComplete(state);
        }

        private static void DisposeStateWhenComplete(SessionState state)
        {
            _ = state.PumpTask.ContinueWith(
                static (_, value) => ((SessionState)value!).Dispose(),
                state,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static string GetSafeHardwareId()
        {
            string hwid;
            try
            {
                hwid = HardwareIdentity.GetHardwareId();
            }
            catch
            {
                return "";
            }

            if (hwid.Length is < 8 or > 32)
                return "";

            foreach (char ch in hwid)
            {
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')))
                    return "";
            }

            return hwid;
        }

        private static string GetAppVersion()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string? informational = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational))
                    return informational.Split('+', 2)[0];

                Version? version = assembly.GetName().Version;
                return version == null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch
            {
                return "";
            }
        }

        private static string GetBuildLabel()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        private static Uri ResolveEndpoint()
        {
#if DEBUG
            string? overrideValue = Environment.GetEnvironmentVariable("CHESSKIT_APP_USAGE_ENDPOINT");
            if (Uri.TryCreate(overrideValue, UriKind.Absolute, out Uri? candidate) &&
                (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps) &&
                (candidate.IsLoopback || candidate.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
#endif
            return new Uri(ProductionEndpoint, UriKind.Absolute);
        }

        private static TimeSpan ResolveHeartbeatInterval()
        {
#if DEBUG
            string? overrideValue = Environment.GetEnvironmentVariable("CHESSKIT_APP_USAGE_HEARTBEAT_SECONDS");
            if (int.TryParse(overrideValue, out int seconds))
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300));
#endif
            return ReleaseHeartbeatInterval;
        }

        private sealed class SessionState : IDisposable
        {
            private readonly object _eventLock = new();
            private readonly HashSet<string> _seenFenEvents = new(StringComparer.Ordinal);
            private readonly Queue<string> _seenFenOrder = new();
            private readonly Dictionary<string, DateTime> _lastLimitEventUtc = new(StringComparer.Ordinal);
            private readonly HashSet<string> _cooldownScopes = new(StringComparer.Ordinal);
            private readonly Func<string>? _licensePlanProvider;
            private TelemetryPayload? _finalPayload;
            private int _accepting = 1;
            private int _disposed;
            private int _hadError;
            private long _lastHeartbeatUtcTicks;

            public SessionState(string sessionId, Func<string>? licensePlanProvider)
            {
                _licensePlanProvider = licensePlanProvider;
                SessionId = sessionId;
                LaunchedAtUtc = DateTime.UtcNow;
                _lastHeartbeatUtcTicks = LaunchedAtUtc.Ticks;
                Queue = Channel.CreateBounded<TelemetryPayload>(new BoundedChannelOptions(QueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    // Producers never await this channel: TryWrite drops a new
                    // non-lifecycle event when full. The shutdown marker uses a
                    // separate single-item slot so it cannot grow this queue.
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            }

            public string SessionId { get; }
            public DateTime LaunchedAtUtc { get; }
            public Channel<TelemetryPayload> Queue { get; }
            public CancellationTokenSource HeartbeatCancellation { get; } = new();
            public CancellationTokenSource SendCancellation { get; } = new();
            public Task PumpTask { get; private set; } = Task.CompletedTask;
            public Task HeartbeatTask { get; private set; } = Task.CompletedTask;
            public bool HasError => Volatile.Read(ref _hadError) != 0;

            public void Start()
            {
                PumpTask = Task.Run(() => PumpAsync(this));
                HeartbeatTask = Task.Run(() => HeartbeatLoopAsync(this));
            }

            public bool TryEnqueue(TelemetryPayload payload)
            {
                bool accepted = Volatile.Read(ref _accepting) == 1 && Queue.Writer.TryWrite(payload);
                if (accepted && payload.EventName == "app_error")
                    Interlocked.Exchange(ref _hadError, 1);
                return accepted;
            }

            public void BeginClosing()
            {
                Interlocked.Exchange(ref _accepting, 0);
                try { HeartbeatCancellation.Cancel(); } catch { }
            }

            public void TryEnqueueFinal(TelemetryPayload payload)
            {
                Interlocked.Exchange(ref _finalPayload, payload);
            }

            public bool TryTakeFinalPayload(out TelemetryPayload payload)
            {
                TelemetryPayload? candidate = Interlocked.Exchange(ref _finalPayload, null);
                payload = candidate!;
                return candidate != null;
            }

            public void CompleteQueue()
            {
                Queue.Writer.TryComplete();
            }

            public void CancelTransport()
            {
                try { SendCancellation.Cancel(); } catch { }
            }

            public void StopImmediately()
            {
                Interlocked.Exchange(ref _accepting, 0);
                try { HeartbeatCancellation.Cancel(); } catch { }
                Queue.Writer.TryComplete();
                try { SendCancellation.Cancel(); } catch { }
            }

            public long TakeHeartbeatDurationMs(DateTime nowUtc)
            {
                long previousTicks = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, nowUtc.Ticks);
                return Math.Max(0L, (long)TimeSpan.FromTicks(Math.Max(0L, nowUtc.Ticks - previousTicks)).TotalMilliseconds);
            }

            public bool TryRememberFenEvent(string eventName, string source, string fen, out string key)
            {
                key = "";
                if (string.IsNullOrWhiteSpace(fen) || fen.Length > 256)
                    return false;

                byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(fen.Trim()));
                key = $"{eventName}|{source}|{Convert.ToHexString(digest)}";
                lock (_eventLock)
                {
                    if (_seenFenEvents.Contains(key))
                        return false;

                    if (_seenFenOrder.Count >= FenDedupeCapacity)
                    {
                        string oldest = _seenFenOrder.Dequeue();
                        _seenFenEvents.Remove(oldest);
                    }

                    _seenFenEvents.Add(key);
                    _seenFenOrder.Enqueue(key);
                    return true;
                }
            }

            public void ForgetFenEvent(string key)
            {
                if (key.Length == 0)
                    return;
                lock (_eventLock)
                    _seenFenEvents.Remove(key);
            }

            public bool TryPassLimitThrottle(string eventName, string scope, out DateTime acceptedAtUtc)
            {
                string key = $"{eventName}|{scope}";
                DateTime now = DateTime.UtcNow;
                acceptedAtUtc = now;
                lock (_eventLock)
                {
                    if (_lastLimitEventUtc.TryGetValue(key, out DateTime last) && now - last < LimitThrottleInterval)
                        return false;

                    _lastLimitEventUtc[key] = now;
                    return true;
                }
            }

            public void RollbackLimitThrottle(string eventName, string scope, DateTime acceptedAtUtc)
            {
                string key = $"{eventName}|{scope}";
                lock (_eventLock)
                {
                    if (_lastLimitEventUtc.TryGetValue(key, out DateTime current) && current == acceptedAtUtc)
                        _lastLimitEventUtc.Remove(key);
                }
            }

            public bool TryMarkCooldownStarted(string scope)
            {
                lock (_eventLock)
                    return _cooldownScopes.Add(scope);
            }

            public void ResetCooldownState(string scope)
            {
                lock (_eventLock)
                    _cooldownScopes.Remove(scope);
            }

            public string GetSafeLicensePlan()
            {
                if (_licensePlanProvider == null)
                    return "";

                try
                {
                    return TryGetSafeIdentifier(_licensePlanProvider(), out string plan) ? plan : "";
                }
                catch
                {
                    return "";
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                HeartbeatCancellation.Dispose();
                SendCancellation.Dispose();
            }
        }

        private sealed class TelemetryPayload
        {
            public string EventName { get; set; } = "";
            public string Hwid { get; set; } = "";
            public string SessionId { get; set; } = "";
            public string LicenseState { get; set; } = "";
            public string LicensePlan { get; set; } = "";
            public string Source { get; set; } = "";
            public string AppVersion { get; set; } = "";
            public string Build { get; set; } = "";
            public DateTime CapturedAtUtc { get; set; }
            public int? MoveCount { get; set; }
            public bool? LimitReached { get; set; }
            public bool? CooldownBlocked { get; set; }
            public int? RemainingMoves { get; set; }
            public int? LimitMoves { get; set; }
            public long? DurationMs { get; set; }
            public long? ActiveMs { get; set; }
            public bool? Success { get; set; }
            public string ErrorCode { get; set; } = "";
            public double? ProcessCpuPercent { get; set; }
            public double? SystemCpuPercent { get; set; }
            public double? GpuPercent { get; set; }
            public Dictionary<string, object?> Metadata { get; set; } = new(StringComparer.Ordinal);
        }
#else
        public static bool IsAvailable => false;
        public static bool IsRunning => false;
        public static void StartSession(bool consentGranted, Func<string>? licensePlanProvider = null) { }
        public static void SetConsent(bool enabled) { }
        public static void StopSession() { }
        public static void QueueFeatureOpen(string feature, string source = "toolbar") { }
        public static void QueueFeatureToggle(string feature, bool enabled, string source = "toolbar") { }
        public static void QueuePositionDetected(string source, string fen, string mode) { }
        public static void QueueMoveDetected(string source, string fen, string mode, string limitScope, int? remainingMoves = null, int? limitMoves = null, int? pliesUsed = null, int? plyLimit = null) { }
        public static void QueueAnalysisResult(string source, string fen, int? depth = null, int? lines = null) { }
        public static void QueueFreeLimitHit(string scope, bool cooldownBlocked, int? remainingMoves = null, int? limitMoves = null, int? pliesUsed = null, int? plyLimit = null) { }
        public static void QueueError(string source, string errorCode) { }
        public static void QueueEvent(string eventName, string source, IReadOnlyDictionary<string, object?>? metadata = null) { }
        public static void QueueHeartbeat() { }
        public static void FlushForExit() { }
        public static void FlushForExit(TimeSpan timeout) { }
        public static Task FlushForExitAsync() => Task.CompletedTask;
        public static Task FlushForExitAsync(TimeSpan timeout) => Task.CompletedTask;
#endif
    }
}
