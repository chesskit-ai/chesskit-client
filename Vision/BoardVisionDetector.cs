using OpenCvSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessKit
{
    public sealed class BoardVisionDetector : IDisposable
    {
        public sealed class BoardDiffInfo
        {
            public int ChangedSquares { get; init; }
            public double AverageSquareDifference { get; init; }
            public double MaxSquareDifference { get; init; }
            public List<BoardDiffSquare> ChangedSquareDetails { get; init; } = new();
        }

        public sealed class BoardDiffSquare
        {
            public int File { get; init; }
            public int RankFromTop { get; init; }
            public double Difference { get; init; }
        }

        public sealed class BoardPixelChangeInfo
        {
            public bool IsComparable { get; init; }
            public bool HasMeaningfulChange { get; init; }
            public int ChangedPixels { get; init; }
            public double ChangedRatio { get; init; }
            public double MeanXor { get; init; }
        }

        public sealed class NetworkMetricsSnapshot
        {
            public static readonly NetworkMetricsSnapshot Empty = new("none", 0, 0, 0, 0, false, null);

            public NetworkMetricsSnapshot(
                string transport,
                double kilobytesPerSecond,
                double averagePacketKilobytes,
                int packetCount,
                long latencyMs = 0,
                bool isFreeLimited = false,
                string? freeReason = null)
            {
                Transport = transport;
                KilobytesPerSecond = kilobytesPerSecond;
                AveragePacketKilobytes = averagePacketKilobytes;
                PacketCount = packetCount;
                LatencyMs = latencyMs;
                IsFreeLimited = isFreeLimited;
                FreeReason = freeReason;
            }

            public string Transport { get; }
            public double KilobytesPerSecond { get; }
            public double AveragePacketKilobytes { get; }
            public int PacketCount { get; }
            // Round-trip latency of the most recent successful vision request (ms),
            // and the server's free-limit tagging from that response. 0 latency means
            // we have not measured a request yet.
            public long LatencyMs { get; }
            public bool IsFreeLimited { get; }
            public string? FreeReason { get; }
        }

        private const string VisionEndpoint = "https://chesskit.ai/screenshot-api/v1/detect";
        // Direct-IP TCP transport retired: vision now goes WS-primary + HTTP
        // fallback, both via chesskit.ai/Cloudflare, so no origin IP in the source.
        // (The unused TCP helper methods below are dead and will be swept.)
        private const string VisionTcpHost = "";
        private const int VisionTcpPort = 8080;
        private const string VisionStreamEndpoint = "wss://chesskit.ai/screenshot-api/v1/detect-stream";
        private const int PixelFingerprintSize = 64;
        private const int PixelFingerprintQuantizationStep = 8;
        private const int PixelFingerprintMinChangedPixels = 10;
        private const int DiffNormalizedSize = 256;
        private const double SquareDiffThreshold = 12.0;
        private const int RemoteVisionTimeoutMs = 6000;
        private const int VisionStreamTimeoutMs = 5000;
        private const int RemoteVisionCooldownMs = 10000;
        private const int RemoteVisionFailuresBeforeCooldown = 2;
        private const int WebSocketReceiveBufferSize = 1024 * 1024;
        private const int TcpReceiveBufferSize = 1024 * 1024;
        private const int VisionStreamReconnectMs = 10000;
        private const int VisionStreamIdleResetMs = 45000;
        private const int BoardDetectionMaxDimension = 640;
        private const int BoardDetectionJpegQuality = 58;
        // The server YOLO model input is 640x640 and letterboxes everything to
        // that before inference, so uploading a larger board crop is wasted
        // bandwidth - the dominant cost of remote FEN reads on high-res screens
        // / slow links. Cap the FEN upload at the model size; pieces are large
        // high-contrast shapes so a lower JPEG quality reads fine.
        private const int FenUploadMaxDimension = 640;
        private const int FenDetectionJpegQuality = 40;
        private const int DeltaPatchMaxSquares = 6;
        private const int DeltaPatchSize = 32;
        private const int DeltaPatchJpegQuality = 45;
        private const double DeltaSquareDiffThreshold = 12.0;
        private const string VisionPayloadFull = "full-v1";
        private const string VisionPayloadDelta = "square-delta-v1";
        private const int SlowVisionRequestMs = 900;
        private const int SlowVisionBackoffMs = 450;
        private const int BusyVisionBackoffMs = 1500;
        private const int BoardDetectionMinIntervalMs = 20000;
        private const int NetworkMetricsWindowSeconds = 10;
        private const int NetworkMetricsMaxSamples = 120;
        private static readonly bool UseVisionStream = true;
        private static readonly bool UseVisionTcpStream = true;
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMilliseconds(RemoteVisionTimeoutMs) };
        private static readonly object CircuitLock = new();
        private static readonly object StreamLock = new();
        private static readonly object VisionDeltaLock = new();
        private static readonly object BoardDetectionBudgetLock = new();
        private static readonly object NetworkMetricsLock = new();
        private static readonly Queue<NetworkMetricSample> NetworkMetricSamples = new();
        private static string CurrentVisionTransport = "none";
        private static DateTimeOffset RemoteVisionCooldownUntilUtc = DateTimeOffset.MinValue;
        private static DateTimeOffset NextVisionTcpConnectAttemptUtc = DateTimeOffset.MinValue;
        private static DateTimeOffset NextVisionStreamConnectAttemptUtc = DateTimeOffset.MinValue;
        private static int RemoteVisionConsecutiveFailures;
        private static int VisionTcpConnectInFlight;
        private static int VisionStreamConnectInFlight;
        private static int VisionRequestInFlight;
        private static TcpClient? VisionTcpClient;
        private static ClientWebSocket? VisionSocket;
        private static Mat? VisionDeltaBaseline;
        private static string VisionDeltaBaselineKey = "";
        private static long VisionDeltaFrameId;
        private static DateTimeOffset LastVisionStreamActivityUtc = DateTimeOffset.MinValue;
        private static DateTimeOffset AdaptiveVisionBackoffUntilUtc = DateTimeOffset.MinValue;
        private static DateTimeOffset LastBoardDetectionUploadUtc = DateTimeOffset.MinValue;
        private static int LastBoardDetectionThrottledFlag;
        private static BoardVisionConnectionState VisionConnectionState = BoardVisionConnectionState.Disconnected;
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public static event Action<BoardVisionConnectionState>? ConnectionStateChanged;

        public string ExecutionProvider => "Server";

        /// <summary>
        /// The vision stream transport currently in use: "ws" (persistent
        /// WebSocket stream), "http" (per-frame fallback), or "none" (not
        /// connected yet). Surfaced to the debug HUD so the live transport is
        /// visible at a glance.
        /// </summary>
        public static string CurrentTransport
        {
            get { lock (NetworkMetricsLock) { return CurrentVisionTransport; } }
        }

        /// <summary>The vision client's current connection state to the analysis server.</summary>
        public static BoardVisionConnectionState ConnectionState => VisionConnectionState;

        // Monotonic timestamp of the last reply we got back from the vision server — a
        // FEN, cooldown, or busy response all count, since any of them proves the server
        // is reachable. Used to tell a real outage apart from a momentary blip.
        private static long _lastVisionResponseTick;

        /// <summary>True when the vision server has answered within the last ~10s.</summary>
        public static bool HadRecentVisionResponse =>
            _lastVisionResponseTick != 0 && (Environment.TickCount64 - _lastVisionResponseTick) < 10_000;

        public static BoardVisionDetector CreateFromEmbeddedResource(string resourceName = "server-side")
        {
            _ = resourceName;
            var detector = new BoardVisionDetector();
            // Eagerly open the DNS-free IP TCP stream at startup so the very
            // first board detection uses it instead of the chesskit.ai HTTP
            // fallback (which needs DNS and can stall on a transient blip).
            try { StartVisionStreamConnectInBackground(); } catch { }
            return detector;
        }

        private static void LogVision(string message) => global::Program.Log(message);

        public static bool LastBoardDetectionWasThrottled =>
            System.Threading.Volatile.Read(ref LastBoardDetectionThrottledFlag) != 0;

        public static void ClearBoardDetectionAttemptStatus()
        {
            System.Threading.Volatile.Write(ref LastBoardDetectionThrottledFlag, 0);
        }

        public static int GetBoardDetectionCooldownRemainingMs(int? minIntervalOverrideMs = null)
        {
            lock (BoardDetectionBudgetLock)
            {
                if (LastBoardDetectionUploadUtc == DateTimeOffset.MinValue)
                    return 0;

                int minIntervalMs = Math.Max(0, minIntervalOverrideMs ?? BoardDetectionMinIntervalMs);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                double elapsedMs = (now - LastBoardDetectionUploadUtc).TotalMilliseconds;
                return Math.Max(0, minIntervalMs - (int)Math.Floor(elapsedMs));
            }
        }

        public static bool IsBoardDetectionUploadReady(int? minIntervalOverrideMs = null) =>
            GetBoardDetectionCooldownRemainingMs(minIntervalOverrideMs) <= 0;

        private static bool TryReserveBoardDetectionUpload(int? minIntervalOverrideMs, out int remainingCooldownMs)
        {
            lock (BoardDetectionBudgetLock)
            {
                int minIntervalMs = Math.Max(0, minIntervalOverrideMs ?? BoardDetectionMinIntervalMs);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (LastBoardDetectionUploadUtc != DateTimeOffset.MinValue &&
                    (now - LastBoardDetectionUploadUtc).TotalMilliseconds < minIntervalMs)
                {
                    remainingCooldownMs = Math.Max(
                        0,
                        minIntervalMs - (int)Math.Floor((now - LastBoardDetectionUploadUtc).TotalMilliseconds));
                    return false;
                }

                LastBoardDetectionUploadUtc = now;
                remainingCooldownMs = 0;
                return true;
            }
        }

        public static NetworkMetricsSnapshot GetNetworkMetricsSnapshot()
        {
            long latencyMs;
            bool isFreeLimited;
            string? freeReason;
            lock (VisionFreeStateLock)
            {
                latencyMs = _lastVisionLatencyMs;
                isFreeLimited = _lastVisionFreeLimited;
                freeReason = _lastVisionFreeReason;
            }

            lock (NetworkMetricsLock)
            {
                PruneNetworkMetrics(DateTimeOffset.UtcNow);
                if (NetworkMetricSamples.Count == 0)
                    return new NetworkMetricsSnapshot(CurrentVisionTransport, 0, 0, 0, latencyMs, isFreeLimited, freeReason);

                long totalBytes = 0;
                DateTimeOffset first = NetworkMetricSamples.Peek().TimestampUtc;
                DateTimeOffset last = first;
                foreach (NetworkMetricSample sample in NetworkMetricSamples)
                {
                    totalBytes += sample.Bytes;
                    last = sample.TimestampUtc;
                }

                double elapsedSeconds = Math.Max(1.0, (last - first).TotalSeconds);
                double kilobytesPerSecond = (totalBytes / 1024.0) / elapsedSeconds;
                double averagePacketKilobytes = totalBytes / 1024.0 / NetworkMetricSamples.Count;
                return new NetworkMetricsSnapshot(CurrentVisionTransport, kilobytesPerSecond, averagePacketKilobytes, NetworkMetricSamples.Count, latencyMs, isFreeLimited, freeReason);
            }
        }

        public string ProcessBoard(Mat boardImage, Rect boardRect, bool isBlackPerspective = false, bool forceFullFrame = false)
        {
            try
            {
                using var board = new Mat(boardImage, boardRect);
                // Normalize the crop to the model's 640px input before upload -
                // downscaling big boards AND upscaling small ones. The model
                // expects the board to fill its frame, and the delta/baseline/
                // patch pipeline keys off the upload size, so every board must
                // arrive at one consistent 640 regardless of on-screen size. A
                // sub-640 board left at native size sat small inside the server's
                // letterboxed frame and churned the delta baseline every resize -
                // the cause of detection dying below ~640px.
                bool resizedForModel = TryResizeForModel(board, FenUploadMaxDimension, out Mat scaledBoard);
                Mat uploadBoard = resizedForModel ? scaledBoard : board;
                try
                {
                    var request = new RemoteVisionRequest
                    {
                        Mode = "fen",
                        Hwid = HardwareIdentity.GetHardwareId(),
                        IsBlackPerspective = isBlackPerspective
                    };
                    RemoteVisionResponse? response = SendVisionRequest(request, uploadBoard, FenDetectionJpegQuality, forceFullFrame);
                    if (response?.Ok == true && !string.IsNullOrWhiteSpace(response.Fen))
                    {
                        return response.Fen;
                    }

                    return "8/8/8/8/8/8/8/8 w KQkq - 0 1";
                }
                finally
                {
                    if (resizedForModel)
                        scaledBoard.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogVision($"[BoardVision] Server FEN failed: {ex.Message}");
                return "8/8/8/8/8/8/8/8 w KQkq - 0 1";
            }
        }

        public Rect? DetectBoard(Mat frame, System.Drawing.Point? preferredPoint = null, int? minIntervalOverrideMs = null)
        {
            try
            {
                ClearBoardDetectionAttemptStatus();
                if (!TryReserveBoardDetectionUpload(minIntervalOverrideMs, out _))
                {
                    System.Threading.Volatile.Write(ref LastBoardDetectionThrottledFlag, 1);
                    return null;
                }

                Mat uploadFrame = CreateBoardDetectionUploadFrame(frame, out double scaleX, out double scaleY, out bool disposeUploadFrame);
                var request = new RemoteVisionRequest
                {
                    Mode = "board",
                    Hwid = HardwareIdentity.GetHardwareId(),
                    PreferredX = preferredPoint.HasValue ? (int)Math.Round(preferredPoint.Value.X * scaleX) : null,
                    PreferredY = preferredPoint.HasValue ? (int)Math.Round(preferredPoint.Value.Y * scaleY) : null
                };
                try
                {
                    RemoteVisionResponse? response = SendVisionRequest(request, uploadFrame, BoardDetectionJpegQuality);
                    if (response?.Ok != true || response.BoardRect == null)
                        return null;

                    var rect = response.BoardRect;
                    if (rect.Width <= 0 || rect.Height <= 0)
                        return null;

                    return new Rect(
                        (int)Math.Round(rect.X / scaleX),
                        (int)Math.Round(rect.Y / scaleY),
                        (int)Math.Round(rect.Width / scaleX),
                        (int)Math.Round(rect.Height / scaleY));
                }
                finally
                {
                    if (disposeUploadFrame)
                        uploadFrame.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogVision($"[BoardVision] Server board detect failed: {ex.Message}");
                return null;
            }
        }

        private static RemoteVisionResponse? SendVisionRequest(RemoteVisionRequest request, Mat image, int jpegQuality, bool forceFullFrame = false)
        {
            if (image.Empty())
                return null;
            // Vision runs entirely over chesskit.ai (fronted by Cloudflare): a
            // WebSocket stream as the primary transport with a multipart HTTP
            // fallback. The old direct-IP TCP transport is gone, so the origin IP
            // never ships in the (open-source) client.
            if (!CanAttemptRemoteVision())
                return null;
            if (DateTimeOffset.UtcNow < AdaptiveVisionBackoffUntilUtc)
                return null;
            if (Interlocked.Exchange(ref VisionRequestInFlight, 1) == 1)
                return null;

            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (forceFullFrame)
                {
                    ResetVisionDeltaState();
                }

                RemoteVisionResponse? response;
                try
                {
                    response = SendVisionFallbackRequest(request, image, jpegQuality, forceFullFrame);
                }
                catch (Exception ex)
                {
                    LogVision($"[BoardVision] Vision request failed ({ex.Message}); skipping this frame.");
                    return null;
                }
                RecordRemoteVisionSuccess();
                RecordVisionRequestDuration(started.ElapsedMilliseconds);
                RecordVisionFreeState(response, started.ElapsedMilliseconds);
                return response;
            }
            catch
            {
                RecordRemoteVisionFailure();
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref VisionRequestInFlight, 0);
            }
        }

        private static RemoteVisionResponse? SendVisionFallbackRequest(RemoteVisionRequest request, Mat image, int jpegQuality, bool forceFullFrame = false)
        {
            if (UseVisionStream && IsVisionStreamOpen())
            {
                try
                {
                    return SendVisionStreamRequest(request, image, jpegQuality, forceFullFrame);
                }
                catch (Exception ex) when (IsVisionTransportFailure(ex))
                {
                    LogVision($"[BoardVision] Vision WebSocket stream unavailable, using HTTP fallback: {ex.Message}");
                    DisposeVisionSocket();
                }
            }
            else
            {
                StartVisionStreamConnectInBackground();
            }

            SetConnectionState(BoardVisionConnectionState.HttpFallback);
            Cv2.ImEncode(".jpg", image, out byte[] jpegBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality));
            return SendVisionHttpRequest(request, jpegBytes);
        }

        private static RemoteVisionResponse? SendVisionHttpRequest(RemoteVisionRequest request, byte[] jpegBytes)
        {
            LogVision($"[BoardVision] payload kind=http-full imageBytes={jpegBytes.Length} mode={request.Mode}");
            RecordNetworkPacket("http", jpegBytes.Length);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(request, JsonOptions)), "metadata");
            using var imageContent = new ByteArrayContent(jpegBytes);
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(imageContent, "screenshot", "vision.jpg");

            using HttpResponseMessage httpResponse = Http.PostAsync(VisionEndpoint, form).GetAwaiter().GetResult();
            string body = httpResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!httpResponse.IsSuccessStatusCode)
            {
                LogVision($"[BoardVision] Server rejected vision request: HTTP {(int)httpResponse.StatusCode} {body}");
                if ((int)httpResponse.StatusCode == 503 || (int)httpResponse.StatusCode == 429)
                    AdaptiveVisionBackoffUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(BusyVisionBackoffMs);
                return null;
            }

            return JsonSerializer.Deserialize<RemoteVisionResponse>(body, JsonOptions);
        }

        private static bool IsVisionTransportFailure(Exception ex)
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (current is WebSocketException ||
                    current is SocketException ||
                    current is IOException ||
                    current is TimeoutException ||
                    current is OperationCanceledException)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsVisionRequestTimeout(Exception ex)
        {
            for (Exception? current = ex; current != null; current = current.InnerException)
            {
                if (current is TimeoutException || current is OperationCanceledException)
                    return true;

                if (current is SocketException socketEx &&
                    socketEx.SocketErrorCode == SocketError.TimedOut)
                    return true;

                string message = current.Message;
                if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("did not properly respond", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("failed to respond", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Returns true and a NEW Mat (caller disposes) resized so its longest
        // side is EXACTLY targetDim; false only when src is already that size.
        // Unlike a downscale-only resize this also UPSCALES boards smaller than
        // targetDim, so the model always sees a board that fills its 640 frame
        // and the delta/baseline/patch pipeline always keys off one consistent
        // size - the fix for detection dying once the board fell below ~640px.
        // Area shrinks cleanly; cubic keeps upscaled pieces legible.
        private static bool TryResizeForModel(Mat src, int targetDim, out Mat resized)
        {
            resized = null!;
            if (src == null || src.Empty())
                return false;
            int longest = Math.Max(src.Width, src.Height);
            if (longest == targetDim)
                return false;
            double scale = targetDim / (double)longest;
            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));
            resized = new Mat();
            InterpolationFlags interp = longest > targetDim ? InterpolationFlags.Area : InterpolationFlags.Cubic;
            Cv2.Resize(src, resized, new OpenCvSharp.Size(w, h), 0, 0, interp);
            return true;
        }

        private static Mat CreateBoardDetectionUploadFrame(Mat frame, out double scaleX, out double scaleY, out bool disposeUploadFrame)
        {
            scaleX = 1.0;
            scaleY = 1.0;
            disposeUploadFrame = false;

            // Normalize the board-detection frame to the model's input in BOTH
            // directions - downscaling big frames and UPSCALING small ones. Small
            // windows were previously sent at native size, so the detector saw a
            // tiny board and stopped locating it sooner than necessary; this lets
            // it find the board on much smaller windows, matching the FEN-crop
            // normalization. (Below a certain real size the board still can't be
            // read - too few pixels per piece - but that floor is physical.)
            int longestSide = Math.Max(frame.Width, frame.Height);
            if (longestSide == BoardDetectionMaxDimension)
                return frame;

            double scale = BoardDetectionMaxDimension / (double)longestSide;
            int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
            int height = Math.Max(1, (int)Math.Round(frame.Height * scale));

            var resized = new Mat();
            InterpolationFlags interp = longestSide > BoardDetectionMaxDimension
                ? InterpolationFlags.Area
                : InterpolationFlags.Cubic;
            Cv2.Resize(frame, resized, new OpenCvSharp.Size(width, height), 0, 0, interp);
            scaleX = width / (double)frame.Width;
            scaleY = height / (double)frame.Height;
            disposeUploadFrame = true;
            return resized;
        }

        private static RemoteVisionResponse? SendVisionStreamRequest(RemoteVisionRequest request, Mat image, int jpegQuality, bool forceFullFrame = false)
        {
            VisionStreamPayload payload = BuildVisionStreamPayload(request, image, jpegQuality, allowDelta: !forceFullFrame);
            RemoteVisionResponse? response = SendVisionStreamPayload(payload.Bytes);
            if (response?.NeedFullFrame == true && payload.IsDelta)
            {
                ResetVisionDeltaState();
                LogVision("[BoardVision] Delta baseline missing on server; resending full frame.");
                payload = BuildVisionStreamPayload(request, image, jpegQuality, allowDelta: false);
                response = SendVisionStreamPayload(payload.Bytes);
            }
            return response;
        }

        // Per-request timing breakdown (last TCP vision request), surfaced only
        // in the slow-request log so we can tell whether a slow request is CPU
        // (payload build), upload (socket write), or server/RTT (wait+receive).
        private static long _lastVisionBuildMs;
        private static long _lastVisionWriteMs;
        private static long _lastVisionRecvMs;
        private static int _lastVisionPayloadBytes;

        // Latest free-limit state + round-trip latency observed from a successful
        // vision response, surfaced to the toolbar via GetNetworkMetricsSnapshot.
        // Guarded by its own lock so the metrics-poll thread reads a consistent triple.
        private static readonly object VisionFreeStateLock = new();
        private static long _lastVisionLatencyMs;
        private static bool _lastVisionFreeLimited;
        private static string? _lastVisionFreeReason;

        // Change-triggered logging of the server's Free signal so the session log
        // shows exactly what the server reports (free / movesLeft / cooldownSec)
        // and the resulting local countdown — ground truth for diagnosing a stuck
        // or premature Free limit. Only logs when the triple changes (≈1/sec while
        // a cooldown ticks), never per-frame.
        private static bool _loggedFreeInit;
        private static bool _lastLoggedFreeLimited;
        private static int _lastLoggedFreeMoves = int.MinValue;
        private static int _lastLoggedFreeCooldown = int.MinValue;

        private static void RecordVisionFreeState(RemoteVisionResponse? response, long latencyMs)
        {
            if (response != null)
            {
                _lastVisionResponseTick = Environment.TickCount64;
                // Vision is the authoritative Free signal for the local-engine flow:
                // the server governs the per-HWID move window + cooldown and tags
                // every response (free / freeMovesRemaining / freeCooldownSeconds).
                // Free analysis runs on the bundled local Stockfish and never reaches
                // the remote-engine broker, so this is what arms the watermark and the
                // cooldown gate. Licensed responses carry no free tag (free=false),
                // which clears the state.
                global::ChessKit.FreeTierServerState.Report(
                    response.FreeLimited,
                    response.FreeMovesRemaining,
                    response.FreeCooldownSeconds);

                if (!_loggedFreeInit ||
                    response.FreeLimited != _lastLoggedFreeLimited ||
                    response.FreeMovesRemaining != _lastLoggedFreeMoves ||
                    response.FreeCooldownSeconds != _lastLoggedFreeCooldown)
                {
                    _loggedFreeInit = true;
                    _lastLoggedFreeLimited = response.FreeLimited;
                    _lastLoggedFreeMoves = response.FreeMovesRemaining;
                    _lastLoggedFreeCooldown = response.FreeCooldownSeconds;
                    int localCooldownSec = global::ChessKit.FreeTierServerState.CooldownRemainingSeconds;
                    LogVision(
                        $"[FREE] server free={response.FreeLimited} movesLeft={response.FreeMovesRemaining} " +
                        $"cooldownSec={response.FreeCooldownSeconds} reason={response.FreeReason ?? "-"} " +
                        $"=> localCooldown={localCooldownSec}s");
                }
            }

            lock (VisionFreeStateLock)
            {
                _lastVisionLatencyMs = latencyMs;
                if (response != null)
                {
                    _lastVisionFreeLimited = response.FreeLimited;
                    _lastVisionFreeReason = response.FreeReason;
                }
            }
        }

        private static RemoteVisionResponse? SendVisionTcpStreamRequest(RemoteVisionRequest request, Mat image, int jpegQuality, bool forceFullFrame = false)
        {
            var buildSw = System.Diagnostics.Stopwatch.StartNew();
            VisionStreamPayload payload = BuildVisionStreamPayload(request, image, jpegQuality, allowDelta: !forceFullFrame);
            _lastVisionBuildMs = buildSw.ElapsedMilliseconds;
            RemoteVisionResponse? response = SendVisionTcpStreamPayload(payload.Bytes);
            if (response?.NeedFullFrame == true && payload.IsDelta)
            {
                ResetVisionDeltaState();
                LogVision("[BoardVision] Delta baseline missing on TCP server; resending full frame.");
                payload = BuildVisionStreamPayload(request, image, jpegQuality, allowDelta: false);
                response = SendVisionTcpStreamPayload(payload.Bytes);
            }
            return response;
        }

        private static RemoteVisionResponse? SendVisionTcpStreamPayload(byte[] payload)
        {
            lock (StreamLock)
            {
                TcpClient client = VisionTcpClient?.Connected == true
                    ? VisionTcpClient
                    : throw new IOException("vision TCP stream is not connected");

                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = VisionStreamTimeoutMs;
                stream.WriteTimeout = VisionStreamTimeoutMs;
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
                var ioSw = System.Diagnostics.Stopwatch.StartNew();
                stream.Write(header);
                stream.Write(payload, 0, payload.Length);
                RecordNetworkPacket("tcp", payload.Length);
                _lastVisionWriteMs = ioSw.ElapsedMilliseconds;
                _lastVisionPayloadBytes = payload.Length;

                ioSw.Restart();
                string body = ReceiveVisionTcpStreamText(stream);
                _lastVisionRecvMs = ioSw.ElapsedMilliseconds;
                LastVisionStreamActivityUtc = DateTimeOffset.UtcNow;
                return JsonSerializer.Deserialize<RemoteVisionResponse>(body, JsonOptions);
            }
        }

        private static RemoteVisionResponse? SendVisionStreamPayload(byte[] payload)
        {
            lock (StreamLock)
            {
                ClientWebSocket socket = VisionSocket?.State == WebSocketState.Open
                    ? VisionSocket
                    : throw new IOException("vision stream is not connected");
                using var cts = new CancellationTokenSource(VisionStreamTimeoutMs);
                socket.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token).GetAwaiter().GetResult();
                RecordNetworkPacket("ws", payload.Length);
                string body = ReceiveVisionStreamText(socket, cts.Token);
                LastVisionStreamActivityUtc = DateTimeOffset.UtcNow;
                return JsonSerializer.Deserialize<RemoteVisionResponse>(body, JsonOptions);
            }
        }

        private static bool IsVisionTcpStreamOpen()
        {
            lock (StreamLock)
            {
                if (VisionTcpClient?.Connected != true)
                    return false;

                if (DateTimeOffset.UtcNow - LastVisionStreamActivityUtc > TimeSpan.FromMilliseconds(VisionStreamIdleResetMs))
                {
                    DisposeVisionTcpClient();
                    SetConnectionState(BoardVisionConnectionState.Disconnected);
                    LogVision("[BoardVision] Vision TCP stream idle too long; reconnecting before next request.");
                    return false;
                }

                return true;
            }
        }

        private static bool IsVisionStreamOpen()
        {
            lock (StreamLock)
            {
                if (VisionSocket?.State != WebSocketState.Open)
                    return false;

                if (DateTimeOffset.UtcNow - LastVisionStreamActivityUtc > TimeSpan.FromMilliseconds(VisionStreamIdleResetMs))
                {
                    DisposeVisionSocket();
                    SetConnectionState(BoardVisionConnectionState.Disconnected);
                    LogVision("[BoardVision] Vision stream idle too long; reconnecting before next request.");
                    return false;
                }

                return true;
            }
        }

        private static void StartVisionTcpConnectInBackground()
        {
            if (!UseVisionTcpStream || DateTimeOffset.UtcNow < NextVisionTcpConnectAttemptUtc)
                return;
            if (Interlocked.Exchange(ref VisionTcpConnectInFlight, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                SetConnectionState(BoardVisionConnectionState.Connecting);
                using var cts = new CancellationTokenSource(RemoteVisionTimeoutMs);
                var client = new TcpClient
                {
                    NoDelay = true,
                    ReceiveBufferSize = TcpReceiveBufferSize,
                    SendBufferSize = TcpReceiveBufferSize,
                    ReceiveTimeout = VisionStreamTimeoutMs,
                    SendTimeout = VisionStreamTimeoutMs
                };
                try
                {
                    await client.ConnectAsync(VisionTcpHost, VisionTcpPort, cts.Token).ConfigureAwait(false);
                    lock (StreamLock)
                    {
                        DisposeVisionTcpClient();
                        VisionTcpClient = client;
                        LastVisionStreamActivityUtc = DateTimeOffset.UtcNow;
                        client = null!;
                    }
                    SetConnectionState(BoardVisionConnectionState.Connected);
                    SetCurrentVisionTransport("tcp");
                    LogVision($"[BoardVision] Vision TCP stream connected to {VisionTcpHost}:{VisionTcpPort}.");
                }
                catch (Exception ex)
                {
                    try { client?.Dispose(); } catch { }
                    NextVisionTcpConnectAttemptUtc = DateTimeOffset.UtcNow.AddMilliseconds(VisionStreamReconnectMs);
                    SetConnectionState(BoardVisionConnectionState.HttpFallback);
                    LogVision($"[BoardVision] Background vision TCP stream connect failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref VisionTcpConnectInFlight, 0);
                }
            });
        }

        private static void StartVisionStreamConnectInBackground()
        {
            if (!UseVisionStream || DateTimeOffset.UtcNow < NextVisionStreamConnectAttemptUtc)
                return;
            if (Interlocked.Exchange(ref VisionStreamConnectInFlight, 1) == 1)
                return;

            _ = Task.Run(async () =>
            {
                SetConnectionState(BoardVisionConnectionState.Connecting);
                using var cts = new CancellationTokenSource(RemoteVisionTimeoutMs);
                var socket = new ClientWebSocket();
                try
                {
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
#if NET8_0_OR_GREATER
                    socket.Options.DangerousDeflateOptions = new WebSocketDeflateOptions
                    {
                        ClientMaxWindowBits = 15,
                        ServerMaxWindowBits = 15
                    };
#endif
                    await socket.ConnectAsync(new Uri(VisionStreamEndpoint), cts.Token).ConfigureAwait(false);
                    lock (StreamLock)
                    {
                        DisposeVisionSocket();
                        VisionSocket = socket;
                        LastVisionStreamActivityUtc = DateTimeOffset.UtcNow;
                        socket = null!;
                    }
                    SetConnectionState(BoardVisionConnectionState.Connected);
                    SetCurrentVisionTransport("ws");
                }
                catch (Exception ex)
                {
                    try { socket?.Dispose(); } catch { }
                    NextVisionStreamConnectAttemptUtc = DateTimeOffset.UtcNow.AddMilliseconds(VisionStreamReconnectMs);
                    SetConnectionState(BoardVisionConnectionState.HttpFallback);
                    LogVision($"[BoardVision] Background vision stream connect failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref VisionStreamConnectInFlight, 0);
                }
            });
        }

        private static string ReceiveVisionTcpStreamText(NetworkStream stream)
        {
            Span<byte> header = stackalloc byte[4];
            ReadVisionTcpExact(stream, header);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > TcpReceiveBufferSize)
                throw new IOException("vision TCP stream returned an invalid response length");

            byte[] buffer = new byte[length];
            ReadVisionTcpExact(stream, buffer);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }

        private static void ReadVisionTcpExact(NetworkStream stream, Span<byte> buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer[offset..]);
                if (read <= 0)
                    throw new IOException("vision TCP stream closed");
                offset += read;
            }
        }

        private static string ReceiveVisionStreamText(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[WebSocketReceiveBufferSize];
            WebSocketReceiveResult result;
            do
            {
                result = socket.ReceiveAsync(buffer, cancellationToken).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    DisposeVisionSocket();
                    SetConnectionState(BoardVisionConnectionState.Disconnected);
                    throw new IOException("vision stream closed");
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                throw new IOException("vision stream returned a non-text response");

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        private static VisionStreamPayload BuildVisionStreamPayload(RemoteVisionRequest request, Mat image, int jpegQuality, bool allowDelta)
        {
            if (allowDelta && TryBuildDeltaVisionStreamPayload(request, image, out var deltaPayload))
                return deltaPayload;

            Cv2.ImEncode(".jpg", image, out byte[] jpegBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, jpegQuality));

            long frameId = 0;
            if (string.Equals(request.Mode, "fen", StringComparison.OrdinalIgnoreCase))
                frameId = UpdateVisionDeltaBaseline(request, image);

            RemoteVisionRequest payloadRequest = CloneVisionRequest(request);
            payloadRequest.PayloadKind = VisionPayloadFull;
            payloadRequest.FrameId = frameId;
            payloadRequest.BaseFrameId = 0;
            payloadRequest.ImageWidth = image.Width;
            payloadRequest.ImageHeight = image.Height;
            payloadRequest.DeltaPatchSize = 0;
            payloadRequest.Patches = null;

            byte[] payloadBytes = BuildCompressedVisionPayload(payloadRequest, jpegBytes);
            LogVision($"[BoardVision] payload kind=stream-full payloadBytes={payloadBytes.Length} imageBytes={jpegBytes.Length} mode={request.Mode}");
            return new VisionStreamPayload(payloadBytes, isDelta: false);
        }

        private static bool TryBuildDeltaVisionStreamPayload(RemoteVisionRequest request, Mat image, out VisionStreamPayload payload)
        {
            payload = default!;
            if (!string.Equals(request.Mode, "fen", StringComparison.OrdinalIgnoreCase) || !IsMatReadable(image))
                return false;

            try
            {
                string key = BuildVisionDeltaBaselineKey(request, image);
                lock (VisionDeltaLock)
                {
                    if (VisionDeltaBaseline == null ||
                        !IsMatReadable(VisionDeltaBaseline) ||
                        !string.Equals(VisionDeltaBaselineKey, key, StringComparison.Ordinal) ||
                        VisionDeltaFrameId <= 0)
                    {
                        return false;
                    }

                    List<DeltaSquare> changedSquares = FindDeltaChangedSquares(VisionDeltaBaseline, image);
                    if (changedSquares.Count <= 0 || changedSquares.Count > DeltaPatchMaxSquares)
                        return false;

                    using var patchBytes = new MemoryStream();
                    var patches = new List<RemoteVisionPatch>(changedSquares.Count);
                    foreach (DeltaSquare square in changedSquares)
                    {
                        Rect rect = GetDeltaSquareRect(image.Width, image.Height, square.File, square.RankFromTop);
                        if (rect.Width <= 0 || rect.Height <= 0)
                            return false;

                        using var roi = new Mat(image, rect);
                        using var patchImage = new Mat();
                        Cv2.Resize(roi, patchImage, new OpenCvSharp.Size(DeltaPatchSize, DeltaPatchSize), 0, 0, InterpolationFlags.Area);
                        Cv2.ImEncode(".jpg", patchImage, out byte[] encodedPatch, new ImageEncodingParam(ImwriteFlags.JpegQuality, DeltaPatchJpegQuality));
                        if (encodedPatch.Length == 0)
                            return false;

                        int offset = (int)patchBytes.Length;
                        patchBytes.Write(encodedPatch, 0, encodedPatch.Length);
                        patches.Add(new RemoteVisionPatch
                        {
                            X = rect.X,
                            Y = rect.Y,
                            Width = rect.Width,
                            Height = rect.Height,
                            PayloadOffset = offset,
                            PayloadLength = encodedPatch.Length,
                            Encoding = "jpeg"
                        });
                    }

                    long baseFrameId = VisionDeltaFrameId;
                    long frameId = unchecked(baseFrameId + 1);
                    if (frameId <= 0)
                        frameId = 1;

                    RemoteVisionRequest payloadRequest = CloneVisionRequest(request);
                    payloadRequest.PayloadKind = VisionPayloadDelta;
                    payloadRequest.FrameId = frameId;
                    payloadRequest.BaseFrameId = baseFrameId;
                    payloadRequest.ImageWidth = image.Width;
                    payloadRequest.ImageHeight = image.Height;
                    payloadRequest.DeltaPatchSize = DeltaPatchSize;
                    payloadRequest.Patches = patches;

                    byte[] payloadBytes = BuildCompressedVisionPayload(payloadRequest, patchBytes.ToArray());
                    UpdateVisionDeltaBaselineLocked(key, image, frameId);
                    LogVision($"[BoardVision] payload kind=stream-delta payloadBytes={payloadBytes.Length} patchBytes={patchBytes.Length} patches={patches.Count} mode={request.Mode}");
                    payload = new VisionStreamPayload(payloadBytes, isDelta: true);
                    return true;
                }
            }
            catch (Exception ex) when (ex is OpenCVException || ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            {
                LogVision($"[BoardVision] Delta build failed, using full frame: {ex.Message}");
                ResetVisionDeltaState();
                return false;
            }
        }

        private static long UpdateVisionDeltaBaseline(RemoteVisionRequest request, Mat image)
        {
            if (!IsMatReadable(image))
                return 0;

            string key = BuildVisionDeltaBaselineKey(request, image);
            lock (VisionDeltaLock)
            {
                long frameId = unchecked(VisionDeltaFrameId + 1);
                if (frameId <= 0)
                    frameId = 1;

                UpdateVisionDeltaBaselineLocked(key, image, frameId);
                return frameId;
            }
        }

        private static void UpdateVisionDeltaBaselineLocked(string key, Mat image, long frameId)
        {
            Mat clone = image.Clone();
            VisionDeltaBaseline?.Dispose();
            VisionDeltaBaseline = clone;
            VisionDeltaBaselineKey = key;
            VisionDeltaFrameId = frameId;
        }

        private static void ResetVisionDeltaState()
        {
            lock (VisionDeltaLock)
            {
                VisionDeltaBaseline?.Dispose();
                VisionDeltaBaseline = null;
                VisionDeltaBaselineKey = "";
                VisionDeltaFrameId = 0;
            }
        }

        private static string BuildVisionDeltaBaselineKey(RemoteVisionRequest request, Mat image)
            => $"{request.Hwid}|{request.Mode}|{request.IsBlackPerspective}|{image.Width}x{image.Height}";

        private static RemoteVisionRequest CloneVisionRequest(RemoteVisionRequest request) => new()
        {
            Mode = request.Mode,
            Hwid = request.Hwid,
            IsBlackPerspective = request.IsBlackPerspective,
            PreferredX = request.PreferredX,
            PreferredY = request.PreferredY
        };

        private static List<DeltaSquare> FindDeltaChangedSquares(Mat previous, Mat current)
        {
            using var normalizedPrevious = NormalizeBoardForDiff(previous);
            using var normalizedCurrent = NormalizeBoardForDiff(current);
            using var diff = new Mat();
            Cv2.Absdiff(normalizedPrevious, normalizedCurrent, diff);

            int squareSize = DiffNormalizedSize / 8;
            var changedSquares = new List<DeltaSquare>();
            for (int rank = 0; rank < 8; rank++)
            {
                for (int file = 0; file < 8; file++)
                {
                    using var squareRoi = new Mat(diff, new Rect(file * squareSize, rank * squareSize, squareSize, squareSize));
                    if (Cv2.Mean(squareRoi).Val0 >= DeltaSquareDiffThreshold)
                    {
                        changedSquares.Add(new DeltaSquare(file, rank));
                    }
                }
            }
            return changedSquares;
        }

        private static Rect GetDeltaSquareRect(int width, int height, int file, int rankFromTop)
        {
            int x0 = file * width / 8;
            int y0 = rankFromTop * height / 8;
            int x1 = (file + 1) * width / 8;
            int y1 = (rankFromTop + 1) * height / 8;
            return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
        }

        private static byte[] BuildCompressedVisionPayload(RemoteVisionRequest request, byte[] imageBytes)
        {
            byte[] metadata = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            using var raw = new MemoryStream(4 + metadata.Length + imageBytes.Length);
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, metadata.Length);
            raw.Write(header);
            raw.Write(metadata, 0, metadata.Length);
            raw.Write(imageBytes, 0, imageBytes.Length);

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] bytes = raw.ToArray();
                gzip.Write(bytes, 0, bytes.Length);
            }
            return compressed.ToArray();
        }

        private static bool CanAttemptRemoteVision()
        {
            lock (CircuitLock)
            {
                return DateTimeOffset.UtcNow >= RemoteVisionCooldownUntilUtc;
            }
        }

        private static void RecordRemoteVisionSuccess()
        {
            lock (CircuitLock)
            {
                RemoteVisionConsecutiveFailures = 0;
                RemoteVisionCooldownUntilUtc = DateTimeOffset.MinValue;
            }
        }

        private static void RecordVisionRequestDuration(long elapsedMs)
        {
            if (elapsedMs >= SlowVisionRequestMs)
            {
                int backoffMs = Math.Min(1500, SlowVisionBackoffMs + (int)Math.Min(700, elapsedMs - SlowVisionRequestMs));
                AdaptiveVisionBackoffUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(backoffMs);
                LogVision($"[BoardVision] Slow vision request {elapsedMs}ms (build={_lastVisionBuildMs}ms write={_lastVisionWriteMs}ms wait+recv={_lastVisionRecvMs}ms payload={_lastVisionPayloadBytes}B); backing off local sends for {backoffMs}ms.");
            }
        }

        private static void RecordRemoteVisionFailure()
        {
            lock (CircuitLock)
            {
                RemoteVisionConsecutiveFailures++;
                if (RemoteVisionConsecutiveFailures >= RemoteVisionFailuresBeforeCooldown)
                {
                    RemoteVisionCooldownUntilUtc = DateTimeOffset.UtcNow.AddMilliseconds(RemoteVisionCooldownMs);
                    RemoteVisionConsecutiveFailures = 0;
                    DisposeVisionTcpClient();
                    DisposeVisionSocket();
                    SetConnectionState(BoardVisionConnectionState.Cooldown);
                    LogVision($"[BoardVision] Remote vision unavailable; pausing detection requests for {RemoteVisionCooldownMs}ms.");
                }
                else
                {
                    DisposeVisionTcpClient();
                    DisposeVisionSocket();
                    SetConnectionState(BoardVisionConnectionState.Disconnected);
                }
            }
        }

        private static void DisposeVisionSocket()
        {
            try { VisionSocket?.Dispose(); } catch { }
            VisionSocket = null;
            if (string.Equals(CurrentVisionTransport, "ws", StringComparison.Ordinal))
                SetCurrentVisionTransport("none");
            LastVisionStreamActivityUtc = DateTimeOffset.MinValue;
            ResetVisionDeltaState();
        }

        private static void DisposeVisionTcpClient()
        {
            try { VisionTcpClient?.Dispose(); } catch { }
            VisionTcpClient = null;
            if (string.Equals(CurrentVisionTransport, "tcp", StringComparison.Ordinal))
                SetCurrentVisionTransport("none");
            LastVisionStreamActivityUtc = DateTimeOffset.MinValue;
            ResetVisionDeltaState();
        }

        private static void SetConnectionState(BoardVisionConnectionState state)
        {
            if (VisionConnectionState == state)
                return;

            VisionConnectionState = state;
            try { ConnectionStateChanged?.Invoke(state); } catch { }
        }

        private static void SetCurrentVisionTransport(string transport)
        {
            lock (NetworkMetricsLock)
            {
                CurrentVisionTransport = string.IsNullOrWhiteSpace(transport) ? "none" : transport;
            }
        }

        private static void RecordNetworkPacket(string transport, int bytes)
        {
            if (bytes <= 0)
                return;

            lock (NetworkMetricsLock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                CurrentVisionTransport = string.IsNullOrWhiteSpace(transport) ? "none" : transport;
                NetworkMetricSamples.Enqueue(new NetworkMetricSample(now, bytes));
                PruneNetworkMetrics(now);
            }
        }

        private static void PruneNetworkMetrics(DateTimeOffset now)
        {
            DateTimeOffset cutoff = now.AddSeconds(-NetworkMetricsWindowSeconds);
            while (NetworkMetricSamples.Count > 0 &&
                   (NetworkMetricSamples.Peek().TimestampUtc < cutoff || NetworkMetricSamples.Count > NetworkMetricsMaxSamples))
            {
                NetworkMetricSamples.Dequeue();
            }
        }

        public BoardDiffInfo EstimateBoardDiff(Mat previousBoard, Mat currentBoard)
        {
            try
            {
                if (!IsMatReadable(previousBoard) || !IsMatReadable(currentBoard))
                    return FullBoardChangedDiff();

                using var normalizedPrevious = NormalizeBoardForDiff(previousBoard);
                using var normalizedCurrent = NormalizeBoardForDiff(currentBoard);
                return EstimateNormalizedBoardDiff(normalizedPrevious, normalizedCurrent);
            }
            catch
            {
                return FullBoardChangedDiff();
            }
        }

        public Mat CreateBoardDiffSnapshot(Mat boardImage) => NormalizeBoardForDiff(boardImage);

        public Mat CreateBoardPixelFingerprint(Mat boardImage) => NormalizeBoardForPixelFingerprint(boardImage);

        public BoardPixelChangeInfo EstimateBoardPixelChangeFromFingerprint(Mat previousFingerprint, Mat currentBoard)
        {
            try
            {
                if (!IsPixelFingerprintReadable(previousFingerprint) || !IsMatReadable(currentBoard))
                    return FullBoardPixelChange();

                using var currentFingerprint = NormalizeBoardForPixelFingerprint(currentBoard);
                if (!IsPixelFingerprintReadable(currentFingerprint))
                    return FullBoardPixelChange();

                using var xor = new Mat();
                Cv2.BitwiseXor(previousFingerprint, currentFingerprint, xor);
                int changedPixels = Cv2.CountNonZero(xor);
                double changedRatio = changedPixels / (double)(PixelFingerprintSize * PixelFingerprintSize);
                double meanXor = Cv2.Mean(xor).Val0;

                return new BoardPixelChangeInfo
                {
                    IsComparable = true,
                    HasMeaningfulChange = changedPixels >= PixelFingerprintMinChangedPixels,
                    ChangedPixels = changedPixels,
                    ChangedRatio = changedRatio,
                    MeanXor = meanXor
                };
            }
            catch
            {
                return FullBoardPixelChange();
            }
        }

        public BoardDiffInfo EstimateBoardDiffFromSnapshot(Mat normalizedPreviousBoard, Mat currentBoard)
        {
            try
            {
                if (!IsNormalizedDiffMatReadable(normalizedPreviousBoard) || !IsMatReadable(currentBoard))
                    return FullBoardChangedDiff();

                using var normalizedCurrent = NormalizeBoardForDiff(currentBoard);
                return EstimateNormalizedBoardDiff(normalizedPreviousBoard, normalizedCurrent);
            }
            catch
            {
                return FullBoardChangedDiff();
            }
        }

        private static BoardDiffInfo EstimateNormalizedBoardDiff(Mat normalizedPrevious, Mat normalizedCurrent)
        {
            using var diff = new Mat();
            Cv2.Absdiff(normalizedPrevious, normalizedCurrent, diff);

            int squareSize = DiffNormalizedSize / 8;
            int changedSquares = 0;
            double totalSquareDifference = 0;
            double maxSquareDifference = 0;
            var changedSquareDetails = new List<BoardDiffSquare>();

            for (int rank = 0; rank < 8; rank++)
            {
                for (int file = 0; file < 8; file++)
                {
                    using var squareRoi = new Mat(diff, new Rect(file * squareSize, rank * squareSize, squareSize, squareSize));
                    double squareDifference = Cv2.Mean(squareRoi).Val0;
                    totalSquareDifference += squareDifference;
                    maxSquareDifference = Math.Max(maxSquareDifference, squareDifference);

                    if (squareDifference >= SquareDiffThreshold)
                    {
                        changedSquares++;
                        changedSquareDetails.Add(new BoardDiffSquare
                        {
                            File = file,
                            RankFromTop = rank,
                            Difference = squareDifference
                        });
                    }
                }
            }

            return new BoardDiffInfo
            {
                ChangedSquares = changedSquares,
                AverageSquareDifference = totalSquareDifference / 64.0,
                MaxSquareDifference = maxSquareDifference,
                ChangedSquareDetails = changedSquareDetails
            };
        }

        private static BoardDiffInfo FullBoardChangedDiff() => new()
        {
            ChangedSquares = 64,
            AverageSquareDifference = 255,
            MaxSquareDifference = 255
        };

        private static Mat NormalizeBoardForDiff(Mat boardImage)
        {
            if (!IsMatReadable(boardImage))
                return new Mat(DiffNormalizedSize, DiffNormalizedSize, MatType.CV_8UC1, Scalar.All(0));

            using var gray = new Mat();
            using var resized = new Mat();
            int channels = boardImage.Channels();
            if (channels == 1)
                boardImage.CopyTo(gray);
            else if (channels == 4)
                Cv2.CvtColor(boardImage, gray, ColorConversionCodes.BGRA2GRAY);
            else
                Cv2.CvtColor(boardImage, gray, ColorConversionCodes.BGR2GRAY);

            Cv2.Resize(gray, resized, new OpenCvSharp.Size(DiffNormalizedSize, DiffNormalizedSize), 0, 0, InterpolationFlags.Area);
            var normalized = new Mat();
            Cv2.GaussianBlur(resized, normalized, new OpenCvSharp.Size(5, 5), 0);
            return normalized;
        }

        private static Mat NormalizeBoardForPixelFingerprint(Mat boardImage)
        {
            if (!IsMatReadable(boardImage))
                return new Mat(PixelFingerprintSize, PixelFingerprintSize, MatType.CV_8UC1, Scalar.All(0));

            using var gray = new Mat();
            using var resized = new Mat();
            using var blurred = new Mat();
            int channels = boardImage.Channels();
            if (channels == 1)
                boardImage.CopyTo(gray);
            else if (channels == 4)
                Cv2.CvtColor(boardImage, gray, ColorConversionCodes.BGRA2GRAY);
            else
                Cv2.CvtColor(boardImage, gray, ColorConversionCodes.BGR2GRAY);

            Cv2.Resize(gray, resized, new OpenCvSharp.Size(PixelFingerprintSize, PixelFingerprintSize), 0, 0, InterpolationFlags.Area);
            Cv2.GaussianBlur(resized, blurred, new OpenCvSharp.Size(3, 3), 0);

            var quantized = new Mat();
            Cv2.ConvertScaleAbs(blurred, quantized, 1.0 / PixelFingerprintQuantizationStep, 0);
            return quantized;
        }

        private static BoardPixelChangeInfo FullBoardPixelChange() => new()
        {
            IsComparable = false,
            HasMeaningfulChange = true,
            ChangedPixels = PixelFingerprintSize * PixelFingerprintSize,
            ChangedRatio = 1.0,
            MeanXor = 255
        };

        private static bool IsMatReadable(Mat? mat)
        {
            try
            {
                return mat != null && !mat.IsDisposed && !mat.Empty() && mat.Width > 0 && mat.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNormalizedDiffMatReadable(Mat? mat)
        {
            if (!IsMatReadable(mat))
                return false;

            try
            {
                return mat!.Width == DiffNormalizedSize &&
                    mat.Height == DiffNormalizedSize &&
                    mat.Type() == MatType.CV_8UC1;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPixelFingerprintReadable(Mat? mat)
        {
            if (!IsMatReadable(mat))
                return false;

            try
            {
                return mat!.Width == PixelFingerprintSize &&
                    mat.Height == PixelFingerprintSize &&
                    mat.Type() == MatType.CV_8UC1;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            DisposeVisionTcpClient();
            DisposeVisionSocket();
            ResetVisionDeltaState();
        }

        private sealed class VisionStreamPayload
        {
            public VisionStreamPayload(byte[] bytes, bool isDelta)
            {
                Bytes = bytes;
                IsDelta = isDelta;
            }

            public byte[] Bytes { get; }
            public bool IsDelta { get; }
        }

        private readonly record struct NetworkMetricSample(DateTimeOffset TimestampUtc, int Bytes);

        private readonly struct DeltaSquare
        {
            public DeltaSquare(int file, int rankFromTop)
            {
                File = file;
                RankFromTop = rankFromTop;
            }

            public int File { get; }
            public int RankFromTop { get; }
        }

        private sealed class RemoteVisionRequest
        {
            public string Mode { get; set; } = "";
            public string Hwid { get; set; } = "";
            public bool IsBlackPerspective { get; set; }
            public int? PreferredX { get; set; }
            public int? PreferredY { get; set; }
            public string PayloadKind { get; set; } = "";
            public long FrameId { get; set; }
            public long BaseFrameId { get; set; }
            public int ImageWidth { get; set; }
            public int ImageHeight { get; set; }
            public int DeltaPatchSize { get; set; }
            public List<RemoteVisionPatch>? Patches { get; set; }
        }

        private sealed class RemoteVisionPatch
        {
            public int X { get; set; }
            public int Y { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int PayloadOffset { get; set; }
            public int PayloadLength { get; set; }
            public string Encoding { get; set; } = "";
        }

        private sealed class RemoteVisionResponse
        {
            public bool Ok { get; init; }
            public string? Fen { get; init; }
            public RemoteRect? BoardRect { get; init; }
            public bool NeedFullFrame { get; init; }
            public string? Message { get; init; }
            // Server-side free-limit tagging.
            [JsonPropertyName("free")]
            public bool FreeLimited { get; init; }
            [JsonPropertyName("freeReason")]
            public string? FreeReason { get; init; }
            [JsonPropertyName("freeMovesRemaining")]
            public int FreeMovesRemaining { get; init; }
            [JsonPropertyName("freeCooldownSeconds")]
            public int FreeCooldownSeconds { get; init; }
        }

        private sealed class RemoteRect
        {
            [JsonPropertyName("x")]
            public int X { get; init; }
            [JsonPropertyName("y")]
            public int Y { get; init; }
            [JsonPropertyName("width")]
            public int Width { get; init; }
            [JsonPropertyName("height")]
            public int Height { get; init; }
        }
    }

    public enum BoardVisionConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Cooldown,
        HttpFallback
    }
}
