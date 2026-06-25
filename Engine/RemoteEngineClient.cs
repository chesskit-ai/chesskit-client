using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessKit
{
    internal sealed class RemoteEngineClient : IDisposable
    {
        private const int DefaultBufferBytes = 1 << 20;
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // Latest free-limit state + round-trip latency from any remote-engine
        // response, surfaced to the toolbar. RemoteEngineClient is per-engine, but
        // the toolbar only cares about the most recent analysis response regardless
        // of which instance produced it, so this state is static and lock-guarded.
        private static readonly object FreeStateLock = new();
        private static long _lastEngineRoundTripMs;
        private static bool _lastEngineFreeLimited;
        private static int _lastEngineFreeMovesRemaining;
        private static int _lastEngineFreeCooldownSeconds;
        private static string? _lastEngineErrorCode;
        private static bool _lastEngineHasResult;

        public readonly struct FreeStateSnapshot
        {
            public FreeStateSnapshot(bool hasResult, long roundTripMs, bool isFreeLimited, int freeMovesRemaining, int freeCooldownSeconds, string? errorCode)
            {
                HasResult = hasResult;
                RoundTripMs = roundTripMs;
                IsFreeLimited = isFreeLimited;
                FreeMovesRemaining = freeMovesRemaining;
                FreeCooldownSeconds = freeCooldownSeconds;
                ErrorCode = errorCode;
            }

            public bool HasResult { get; }
            public long RoundTripMs { get; }
            public bool IsFreeLimited { get; }
            // Moves left in the current Free window, and seconds until it resets
            // (>0 only while in cooldown). Both 0 for a Licensed response.
            public int FreeMovesRemaining { get; }
            public int FreeCooldownSeconds { get; }
            // rate_capped / busy when the server free-limited a request, else null.
            public string? ErrorCode { get; }
        }

        public static FreeStateSnapshot GetFreeStateSnapshot()
        {
            lock (FreeStateLock)
            {
                return new FreeStateSnapshot(
                    _lastEngineHasResult,
                    _lastEngineRoundTripMs,
                    _lastEngineFreeLimited,
                    _lastEngineFreeMovesRemaining,
                    _lastEngineFreeCooldownSeconds,
                    _lastEngineErrorCode);
            }
        }

        private static void RecordEngineFreeState(RemoteEngineResponse response, long roundTripMs)
        {
            lock (FreeStateLock)
            {
                _lastEngineHasResult = true;
                _lastEngineRoundTripMs = roundTripMs;
                _lastEngineFreeLimited = response.FreeLimited;
                _lastEngineFreeMovesRemaining = Math.Max(0, response.FreeMovesRemaining);
                _lastEngineFreeCooldownSeconds = Math.Max(0, response.FreeCooldownSeconds);
                // Only surface a free-limit code; other failure codes are noise here.
                _lastEngineErrorCode =
                    string.Equals(response.ErrorCode, "rate_capped", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(response.ErrorCode, "busy", StringComparison.OrdinalIgnoreCase)
                        ? response.ErrorCode
                        : null;
            }

            // Feed the central server-driven Free state. The server governs the
            // limit; this is what drives the watermark countdown and the analysis
            // pause. A licensed response carries free:false, which clears it.
            FreeTierServerState.Report(response.FreeLimited, response.FreeMovesRemaining, response.FreeCooldownSeconds);
        }

        private const string WarmupFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
        // The cold WebSocket dial through Cloudflare is ~5s (measured); the warmup
        // and the gating ping must both out-wait it, or the remote engine never
        // activates and the toolbar loops "Connecting" -> "Engine unavailable".
        private const int WarmupTimeoutMs = 10000;
        // Budget the connection-establishing ping (UCIEngine.StartAsync) gives the
        // cold dial. Once the socket is warm, real pings return in ms; this is slack.
        private const int GatingConnectBudgetMs = 10000;
        private const int KeepAliveCheckIntervalMs = 20000;
        private const int KeepAliveIdleThresholdMs = 45000;
        private const int KeepAliveMaxConsecutiveFailures = 3;

        private readonly RemoteEngineSettings _settings;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private ClientWebSocket? _ws;
        private bool _disposed;
        private System.Threading.Timer? _keepAliveTimer;
        private int _warmupStarted;
        private long _lastActivityTicks;
        private volatile bool _serverSupportsStop;
        private volatile string? _inFlightStreamingRequestId;

        private RemoteEngineClient(string engineName, RemoteEngineSettings settings)
        {
            EngineName = engineName;
            _settings = settings;
        }

        public string EngineName { get; }
        public string Endpoint => _settings.Endpoint;

        // Engine selections are now EXPLICIT about source. A remote selection
        // carries this path prefix (e.g. "remote://stockfish"); anything else
        // is a real local binary path. The source travels with the selection,
        // so the user can pick Stockfish local OR remote and we honor it -
        // instead of deciding by engine name.
        public const string RemoteEnginePathPrefix = "remote://";

        public static RemoteEngineClient? TryCreate(string enginePath)
        {
            RemoteEngineSettings settings = RemoteEngineSettings.Load();
            if (!settings.Enabled)
                return null;

            // Only a "remote://" selection routes to the server. A real local
            // path runs the local engine process (returns null here).
            if (string.IsNullOrWhiteSpace(enginePath) ||
                !enginePath.StartsWith(RemoteEnginePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? engineName = MapEngineName(enginePath);
            return string.IsNullOrWhiteSpace(engineName) ? null : new RemoteEngineClient(engineName, settings);
        }

        /// <summary>The engines the broker is configured to serve (fixed set).</summary>
        public static IReadOnlyList<string> GetRemoteEngineNames()
        {
            RemoteEngineSettings settings = RemoteEngineSettings.Load();
            if (!settings.Enabled)
                return System.Array.Empty<string>();
            return new[] { "stockfish", "lc0", "humanuci" };
        }

        /// <summary>
        /// Whether the engine identified by this path would be served by the
        /// remote broker (mirrors TryCreate's decision without allocating a
        /// client). Lets the engine list and selection guards treat a
        /// remote-only engine (e.g. the server-hosted human model, which has
        /// no local binary) as a usable, selectable engine.
        /// </summary>
        public static bool IsEngineRemotelyServed(string enginePath)
        {
            // Only an explicit "remote://" selection is remotely served; a real
            // local path is never "remote" now (source travels with the path).
            if (string.IsNullOrWhiteSpace(enginePath) ||
                !enginePath.StartsWith(RemoteEnginePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            RemoteEngineSettings settings = RemoteEngineSettings.Load();
            if (!settings.Enabled)
                return false;

            return !string.IsNullOrWhiteSpace(MapEngineName(enginePath));
        }

        public static bool IsAuthoritativeFailure(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("remote:license", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("remote:quota", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("remote:invalid", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("no active license", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("too many active engine jobs", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("invalid hwid", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Pays the first-request costs (TCP dial, broker per-worker engine
        /// option setup, cold transposition tables) before the user's first
        /// move instead of on it, then keeps the connection alive so mid-game
        /// requests never cold-dial. Idempotent; runs in the background.
        /// </summary>
        public void BeginBackgroundWarmup(int multiPv, int threads, int hashMb)
        {
            if (_disposed || Interlocked.Exchange(ref _warmupStarted, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                // A real analysis is already in flight or queued: it pays the
                // cold-start cost itself, and a warmup behind it on the send
                // gate would only delay the NEXT request.
                if (_sendGate.CurrentCount == 0)
                {
                    ArrowTimeline.Log("REMOTE_WARMUP_SKIPPED", reason: "real request already in flight");
                    StartKeepAlive();
                    return;
                }

                var watch = Stopwatch.StartNew();
                ArrowTimeline.Log("REMOTE_WARMUP_START", extra: $"{EngineName}@{Endpoint}");
                try
                {
                    // Hard cap: the warmup serializes ahead of any real request
                    // that arrives moments later on the single-slot send gate,
                    // so it must never hold it for long. If the broker is slow
                    // the timeout closes the connection and the real request
                    // redials - no worse than the cold start being avoided.
                    using var warmupCts = new CancellationTokenSource(WarmupTimeoutMs);
                    BestMoveResult result = await AnalyzeAsync(
                        WarmupFen,
                        thinkTimeMs: 50,
                        depth: 1,
                        multiPv,
                        threads,
                        hashMb,
                        fixedDepthOnly: true,
                        warmupCts.Token).ConfigureAwait(false);

                    ArrowTimeline.Log(
                        "REMOTE_WARMUP_DONE",
                        ms: watch.Elapsed.TotalMilliseconds,
                        extra: result.Success ? "ok" : result.Error);
                    DebugRuntime.WriteLine($"[RemoteEngine] Warmup {(result.Success ? "ok" : "failed")} in {watch.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    ArrowTimeline.Log("REMOTE_WARMUP_DONE", ms: watch.Elapsed.TotalMilliseconds, extra: $"error: {ex.Message}");
                    DebugRuntime.WriteLine($"[RemoteEngine] Warmup failed: {ex.Message}");
                }

                StartKeepAlive();
            });
        }

        private void StartKeepAlive()
        {
            if (_disposed || _keepAliveTimer != null)
                return;

            int consecutiveFailures = 0;
            _keepAliveTimer = new System.Threading.Timer(
                async _ =>
                {
                    try
                    {
                        if (_disposed)
                            return;

                        // A request in flight (or queued) is keep-alive enough,
                        // and a ping would only block behind the send gate.
                        if (_sendGate.CurrentCount == 0)
                            return;

                        long last = Interlocked.Read(ref _lastActivityTicks);
                        if (last != 0 && (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalMilliseconds < KeepAliveIdleThresholdMs)
                            return;

                        bool ok = await PingAsync().ConfigureAwait(false);
                        ArrowTimeline.Log("REMOTE_KEEPALIVE", extra: ok ? "ok" : "failed");
                        if (ok)
                        {
                            consecutiveFailures = 0;
                        }
                        else if (++consecutiveFailures >= KeepAliveMaxConsecutiveFailures)
                        {
                            // The broker is gone (or the session fell back to
                            // the local engine); stop pinging a dead endpoint
                            // every cycle for the rest of the session.
                            ArrowTimeline.Log("REMOTE_KEEPALIVE", extra: "stopped after repeated failures");
                            try { _keepAliveTimer?.Dispose(); } catch { }
                            _keepAliveTimer = null;
                        }
                    }
                    catch
                    {
                        // Never let a keep-alive failure escape the timer thread.
                    }
                },
                null,
                KeepAliveCheckIntervalMs,
                KeepAliveCheckIntervalMs);
        }

        public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Must out-wait the ~5s cold Cloudflare WS dial (see GatingConnectBudgetMs);
                // the old 3s cap timed out on the connect and stranded the remote engine.
                timeoutCts.CancelAfter(Math.Min(_settings.TimeoutMs, GatingConnectBudgetMs));

                var request = new RemoteEngineRequest
                {
                    Type = "ping",
                    RequestId = Guid.NewGuid().ToString("N")
                };

                RemoteEngineResponse response = await SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                if (response.Ok)
                {
                    // New brokers advertise the in-flight stop capability in
                    // the pong message. Never send stop frames to a broker
                    // that has not advertised it: an old broker would read
                    // the frame as the NEXT request and desync the protocol.
                    _serverSupportsStop = response.Message?.Contains("stop", StringComparison.OrdinalIgnoreCase) == true;
                }
                return response.Ok;
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[RemoteEngine] Ping failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ask the broker to stop the in-flight streaming analysis. The
        /// broker finishes the stream with a final (possibly shallow) result,
        /// so the connection stays warm instead of being torn down - aborts
        /// happen on nearly every move in fast games and each teardown used
        /// to cost the next request a fresh TCP dial. Returns false when
        /// there is nothing to stop or the broker lacks the capability (the
        /// caller falls back to hard cancellation).
        /// </summary>
        public bool TryRequestStop()
        {
            if (_disposed || !_serverSupportsStop)
                return false;

            string? requestId = _inFlightStreamingRequestId;
            ClientWebSocket? ws = _ws;
            if (requestId == null || ws == null)
                return false;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Safe concurrent write: the streaming request holds the
                    // send gate (so the keep-alive cannot write) and has
                    // finished writing its own frame - this is the only
                    // writer while the stream reader awaits packets.
                    var stopMessage = new RemoteEngineRequest { Type = "stop", RequestId = requestId };
                    await WriteMessageAsync(ws, stopMessage, CancellationToken.None).ConfigureAwait(false);
                    ArrowTimeline.Log("REMOTE_STOP_SENT", reqId: requestId);
                }
                catch
                {
                    // The fallback hard-cancel covers a failed stop write.
                }
            });
            return true;
        }

        public async Task<BestMoveResult> AnalyzeAsync(
            string fen,
            int thinkTimeMs,
            int depth,
            int multiPv,
            int threads,
            int hashMb,
            bool fixedDepthOnly,
            CancellationToken cancellationToken,
            Action<BestMoveResult>? updateHandler = null)
        {
            int timeoutMs = BuildClientTimeoutMs(thinkTimeMs, depth);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var request = new RemoteEngineRequest
            {
                Type = "analyze",
                RequestId = Guid.NewGuid().ToString("N"),
                Hwid = HardwareIdentity.GetHardwareId(),
                Engine = EngineName,
                Fen = fen,
                Depth = depth > 0 ? depth : null,
                MoveTimeMs = Math.Max(0, thinkTimeMs),
                MultiPv = Math.Clamp(multiPv <= 0 ? 3 : multiPv, 1, BuildLimits.MaxLines),
                Threads = Math.Clamp(threads <= 0 ? 1 : threads, 1, 64),
                HashMb = Math.Clamp(hashMb <= 0 ? 32 : hashMb, 1, 4096),
                FixedDepthOnly = fixedDepthOnly,
                StreamUpdates = updateHandler != null
            };

            try
            {
                ArrowTimeline.Log("REMOTE_REQ", fen: fen, reqId: request.RequestId, depth: depth, extra: $"think={thinkTimeMs} stream={updateHandler != null}");
                Stopwatch remoteWatch = Stopwatch.StartNew();
                RemoteEngineResponse response = updateHandler == null
                    ? await SendAsync(request, timeoutCts.Token).ConfigureAwait(false)
                    : await SendStreamingAsync(request, fen, thinkTimeMs, updateHandler, timeoutCts.Token).ConfigureAwait(false);
                remoteWatch.Stop();
                ArrowTimeline.Log(
                    "REMOTE_RESP",
                    fen: fen,
                    reqId: request.RequestId,
                    depth: response.AnalysisDepth ?? depth,
                    ms: remoteWatch.Elapsed.TotalMilliseconds,
                    extra: $"ok={response.Ok} broker={response.ElapsedMs?.ToString() ?? "?"} queue={response.QueueMs?.ToString() ?? "?"} best={response.BestMove ?? "-"}");
                LogAnalysisTiming(response, remoteWatch.ElapsedMilliseconds, depth, thinkTimeMs);
                if (!response.Ok)
                {
                    string prefix = string.IsNullOrWhiteSpace(response.ErrorCode)
                        ? "remote"
                        : $"remote:{response.ErrorCode}";
                    return new BestMoveResult
                    {
                        Success = false,
                        Error = $"[{prefix}] {FirstNonEmpty(response.Error, response.Message, "Remote engine request failed.")}",
                        AnalysisFen = fen
                    };
                }

                var result = ToBestMoveResult(response, fen, thinkTimeMs);

                if (!result.Success)
                    result.Error = "Remote engine returned no best move.";

                if (result.AnalysisDepth <= 0 && result.Variations.Count > 0)
                    result.AnalysisDepth = result.Variations.Max(v => v.Depth);

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[RemoteEngine] {EngineName} failed rt=?ms depth={depth} think={thinkTimeMs}: {ex.Message}");
                return new BestMoveResult
                {
                    Success = false,
                    Error = $"Remote engine unavailable: {ex.Message}",
                    AnalysisFen = fen
                };
            }
        }

        private async Task<RemoteEngineResponse> SendStreamingAsync(
            RemoteEngineRequest request,
            string fen,
            int thinkTimeMs,
            Action<BestMoveResult> updateHandler,
            CancellationToken cancellationToken)
        {
            _inFlightStreamingRequestId = request.RequestId;
            try
            {
                return await SendStreamingCoreAsync(request, fen, thinkTimeMs, updateHandler, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _inFlightStreamingRequestId = null;
            }
        }

        private async Task<RemoteEngineResponse> SendStreamingCoreAsync(
            RemoteEngineRequest request,
            string fen,
            int thinkTimeMs,
            Action<BestMoveResult> updateHandler,
            CancellationToken cancellationToken)
        {
            return await SendCoreAsync(
                request,
                async stream =>
                {
                    while (true)
                    {
                        RemoteEngineResponse response = await ReadMessageAsync<RemoteEngineResponse>(stream, cancellationToken).ConfigureAwait(false)
                            ?? new RemoteEngineResponse { Ok = false, Error = "Remote engine returned an empty response." };

                        bool isUpdate = response.IsUpdate == true && response.IsFinal != true;
                        if (!isUpdate)
                            return response;

                        if (response.Ok)
                        {
                            BestMoveResult update = ToBestMoveResult(response, fen, thinkTimeMs);
                            if (update.Success)
                            {
                                ArrowTimeline.Log("REMOTE_STREAM", fen: fen, reqId: request.RequestId, depth: update.AnalysisDepth, extra: $"best={update.BestMove ?? "-"}");
                                DebugRuntime.WriteLine($"[RemoteEngine] {EngineName} stream depth={update.AnalysisDepth} worker={response.WorkerId ?? "?"} best={update.BestMove ?? "-"}");
                                updateHandler(update);
                            }
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        private int BuildClientTimeoutMs(int thinkTimeMs, int depth)
        {
            int requested = Math.Max(0, thinkTimeMs);
            int byDepth = Math.Max(0, depth) * 900;
            int baseline = Math.Max(_settings.TimeoutMs, Math.Max(requested + 5000, byDepth + 3000));
            return Math.Clamp(baseline, 3000, 180000);
        }

        private async Task<RemoteEngineResponse> SendAsync(RemoteEngineRequest request, CancellationToken cancellationToken)
        {
            return await SendCoreAsync(
                request,
                async stream => await ReadMessageAsync<RemoteEngineResponse>(stream, cancellationToken).ConfigureAwait(false)
                    ?? new RemoteEngineResponse { Ok = false, Error = "Remote engine returned an empty response." },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<RemoteEngineResponse> SendCoreAsync(
            RemoteEngineRequest request,
            Func<ClientWebSocket, Task<RemoteEngineResponse>> readResponse,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Exception? lastFailure = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        ClientWebSocket ws = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                        await WriteMessageAsync(ws, request, cancellationToken).ConfigureAwait(false);
                        return await readResponse(ws).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Aborting a streaming analysis mid-response can only
                        // be done by dropping the socket (the broker has no
                        // cancel message), but the next request then pays the
                        // dial again - measured ~200ms per abort during fast
                        // move bursts. Pre-dial in the background so it finds
                        // a warm socket.
                        CloseConnection();
                        ScheduleBackgroundReconnect();
                        throw;
                    }
                    catch (Exception ex) when (IsConnectionFailure(ex) && attempt == 0)
                    {
                        lastFailure = ex;
                        CloseConnection();
                        DebugRuntime.WriteLine($"[RemoteEngine] reconnecting after {ex.GetType().Name}: {ex.Message}");
                    }
                }

                throw lastFailure ?? new IOException("Remote engine request failed.");
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private static BestMoveResult ToBestMoveResult(RemoteEngineResponse response, string fen, int thinkTimeMs)
        {
            var result = new BestMoveResult
            {
                Success = !string.IsNullOrWhiteSpace(response.BestMove) || (response.Variations?.Count ?? 0) > 0,
                BestMove = response.BestMove,
                PonderMove = response.PonderMove,
                Variations = response.Variations ?? new List<MoveVariation>(),
                AnalysisDepth = Math.Max(0, response.AnalysisDepth ?? 0),
                ThinkTime = thinkTimeMs,
                AnalysisFen = fen
            };

            if (string.IsNullOrWhiteSpace(result.BestMove) && result.Variations.Count > 0)
                result.BestMove = result.Variations[0].Moves.FirstOrDefault();

            return result;
        }

        private async Task<ClientWebSocket> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            if (_ws is { State: WebSocketState.Open })
                return _ws;

            CloseConnection();
            var ws = new ClientWebSocket();
            var dialWatch = Stopwatch.StartNew();
            await ws.ConnectAsync(new Uri(_settings.Endpoint), cancellationToken).ConfigureAwait(false);
            ArrowTimeline.Log("REMOTE_CONNECT", ms: dialWatch.Elapsed.TotalMilliseconds, extra: Endpoint);
            _ws = ws;
            return _ws;
        }

        private void ScheduleBackgroundReconnect()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(80).ConfigureAwait(false);
                    if (_disposed)
                        return;
                    // If the gate is taken, a real request is already dialing.
                    if (!await _sendGate.WaitAsync(0).ConfigureAwait(false))
                        return;
                    try
                    {
                        using var cts = new CancellationTokenSource(3000);
                        await EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sendGate.Release();
                    }
                }
                catch
                {
                    // The next request dials normally.
                }
            });
        }

        private static bool IsConnectionFailure(Exception ex) =>
            ex is IOException or SocketException or ObjectDisposedException or EndOfStreamException or InvalidDataException;

        private void CloseConnection()
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _keepAliveTimer?.Dispose(); } catch { }
            _keepAliveTimer = null;
            CloseConnection();
            _sendGate.Dispose();
        }

        private void LogAnalysisTiming(RemoteEngineResponse response, long roundTripMs, int depth, int thinkTimeMs)
        {
            RecordEngineFreeState(response, roundTripMs);
            string brokerMs = response.ElapsedMs?.ToString() ?? "?";
            string queueMs = response.QueueMs?.ToString() ?? "?";
            string worker = string.IsNullOrWhiteSpace(response.WorkerId) ? "?" : response.WorkerId;
            string best = string.IsNullOrWhiteSpace(response.BestMove) ? "-" : response.BestMove;
            string status = response.Ok ? "ok" : $"fail:{FirstNonEmpty(response.ErrorCode, response.Error, response.Message, "?")}";
            DebugRuntime.WriteLine($"[RemoteEngine] {EngineName} {status} rt={roundTripMs}ms broker={brokerMs}ms queue={queueMs}ms worker={worker} depth={response.AnalysisDepth ?? depth} think={thinkTimeMs} best={best}");
        }

        private static async Task WriteMessageAsync<T>(ClientWebSocket ws, T value, CancellationToken cancellationToken)
        {
            // One WS message == one JSON frame; the server bridge adds the broker's
            // 4-byte length prefix on the TCP side.
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            await ws.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T?> ReadMessageAsync<T>(ClientWebSocket ws, CancellationToken cancellationToken)
        {
            // One WS message == one JSON frame (the bridge stripped the broker's
            // length prefix). Reassemble any continuation fragments before parsing.
            using var ms = new MemoryStream();
            byte[] buffer = new byte[64 * 1024];
            while (true)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new EndOfStreamException();
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
                if (ms.Length > DefaultBufferBytes)
                    throw new InvalidDataException($"Remote engine message exceeds {DefaultBufferBytes} bytes.");
            }
            if (ms.Length == 0)
                return default;
            return JsonSerializer.Deserialize<T>(ms.GetBuffer().AsSpan(0, (int)ms.Length), JsonOptions);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException();
                offset += read;
            }
        }

        private static string? MapEngineName(string enginePath)
        {
            // A "remote://stockfish" selection maps to "stockfish".
            if (!string.IsNullOrEmpty(enginePath) &&
                enginePath.StartsWith(RemoteEnginePathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string remoteName = enginePath.Substring(RemoteEnginePathPrefix.Length).Trim().ToLowerInvariant();
                if (remoteName.Contains("human")) return "humanuci";
                if (remoteName.Contains("lc0") || remoteName.Contains("leela")) return "lc0";
                if (remoteName.Contains("stockfish")) return "stockfish";
                return string.IsNullOrWhiteSpace(remoteName) ? null : remoteName;
            }

            string name = Path.GetFileNameWithoutExtension(enginePath).ToLowerInvariant();
            if (name.Contains("humanuci") || name.Contains("humanpolicy") || name.Contains("human"))
                return "humanuci";
            if (name.Contains("lc0") || name.Contains("leela"))
                return "lc0";
            if (name.Contains("stockfish"))
                return "stockfish";
            return null;
        }

        private static string FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

        private sealed class RemoteEngineSettings
        {
            // The endpoint the public client ships with. Reached via Cloudflare
            // WebSocket so the origin IP stays out of the open-source client. A
            // settings.ini / env override is honored ONLY if it is a well-formed
            // ws:// or wss:// URL (see NormalizeEndpoint).
            public const string DefaultEndpoint = "wss://chesskit.ai/engine/v1/stream";

            public bool Enabled { get; init; }
            public string Endpoint { get; init; } = DefaultEndpoint;
            public int TimeoutMs { get; init; } = 15000;

            public static RemoteEngineSettings Load()
            {
                var settings = new AppSettings();
                try
                {
                    settings = new AppSettingsManager(Path.Combine(AppContext.BaseDirectory, "settings.ini")).Load();
                }
                catch
                {
                }

                bool enabled = settings.RemoteEngineEnabled;
                string endpoint = NormalizeEndpoint(settings.RemoteEngineHost);
                int timeoutMs = settings.RemoteEngineTimeoutMs > 0 ? settings.RemoteEngineTimeoutMs : 15000;

                string? enabledEnv = Environment.GetEnvironmentVariable("CHESSKIT_REMOTE_ENGINE_ENABLED");
                if (!string.IsNullOrWhiteSpace(enabledEnv))
                    enabled = IsTruthy(enabledEnv);

                string? endpointEnv = Environment.GetEnvironmentVariable("CHESSKIT_REMOTE_ENGINE_HOST");
                if (!string.IsNullOrWhiteSpace(endpointEnv))
                    endpoint = NormalizeEndpoint(endpointEnv);

                string? timeoutEnv = Environment.GetEnvironmentVariable("CHESSKIT_REMOTE_ENGINE_TIMEOUT_MS");
                if (int.TryParse(timeoutEnv, out int envTimeout) && envTimeout > 0)
                    timeoutMs = envTimeout;

                return new RemoteEngineSettings
                {
                    Enabled = enabled,
                    Endpoint = endpoint,
                    TimeoutMs = Math.Clamp(timeoutMs, 3000, 180000),
                };
            }

            private static bool IsTruthy(string value)
            {
                string normalized = value.Trim().ToLowerInvariant();
                return normalized is "1" or "true" or "yes" or "on";
            }

            // Honor an endpoint override ONLY when it is a well-formed ws:// or
            // wss:// absolute URL. Anything else - empty, or a legacy bare host/IP
            // such as "74.50.72.114" left behind in an old settings.ini from the
            // pre-WebSocket (raw-TCP :8091) build - is IGNORED in favor of the
            // default. Without this, a stale RemoteEngineHost became the endpoint
            // verbatim, new Uri(endpoint) threw, and every remote analysis failed as
            // "Engine unavailable" before a single packet was sent. This silently
            // migrates upgraders off the dead setting.
            private static string NormalizeEndpoint(string? raw)
            {
                raw = raw?.Trim();
                if (!string.IsNullOrEmpty(raw)
                    && Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss))
                {
                    return raw;
                }
                return DefaultEndpoint;
            }
        }

        private sealed class RemoteEngineRequest
        {
            public string Type { get; init; } = "";
            public string RequestId { get; init; } = "";
            public string? Hwid { get; init; }
            public string? Engine { get; init; }
            public string? Fen { get; init; }
            public int? Depth { get; init; }
            public int? MoveTimeMs { get; init; }
            public int? MultiPv { get; init; }
            public int? Threads { get; init; }
            public int? HashMb { get; init; }
            public bool? FixedDepthOnly { get; init; }
            public bool? StreamUpdates { get; init; }
        }

        private sealed class RemoteEngineResponse
        {
            public bool Ok { get; init; }
            public string? Error { get; init; }
            public string? ErrorCode { get; init; }
            public string? Message { get; init; }
            public string? Engine { get; init; }
            public string? WorkerId { get; init; }
            public string? BestMove { get; init; }
            public string? PonderMove { get; init; }
            public List<MoveVariation>? Variations { get; init; }
            public int? AnalysisDepth { get; init; }
            public int? QueueMs { get; init; }
            public int? ElapsedMs { get; init; }
            public bool? IsUpdate { get; init; }
            public bool? IsFinal { get; init; }
            // Server-side free-limit tagging. ErrorCode already carries
            // rate_capped/busy on failures.
            [System.Text.Json.Serialization.JsonPropertyName("free")]
            public bool FreeLimited { get; init; }
            // Moves left in the current Free window, and (only while in cooldown)
            // the seconds until the window resets. The server governs the limit;
            // the client just displays these.
            [System.Text.Json.Serialization.JsonPropertyName("freeMovesRemaining")]
            public int FreeMovesRemaining { get; init; }
            [System.Text.Json.Serialization.JsonPropertyName("freeCooldownSeconds")]
            public int FreeCooldownSeconds { get; init; }
        }
    }
}
