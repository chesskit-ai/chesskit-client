using OpenCvSharp;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChessKit
{
    internal static class ScreenshotTelemetryClient
    {
        private const string UploadEndpoint = "https://chesskit.ai/screenshot-api/v1/events";
        private static readonly HttpClient Http = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        private static readonly SemaphoreSlim UploadLock = new(1, 1);
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly TimeSpan MinimumScreenshotInterval = TimeSpan.FromSeconds(10);
        private static DateTime _lastScreenshotUploadUtc = DateTime.MinValue;
        private static string _lastScreenshotKey = "";

        // Telemetry is OPT-IN and OFF by default. The client never uploads
        // screenshots or analysis unless explicitly enabled by setting the
        // CHESSKIT_TELEMETRY=1 environment variable (see README, "Privacy").
        internal static bool Enabled { get; } =
            Environment.GetEnvironmentVariable("CHESSKIT_TELEMETRY") is "1" or "true" or "TRUE";

        public static void QueuePositionDetected(string fen, Mat? boardSnapshot, bool boardFlipped, string source)
        {
            if (!Enabled)
                return;
            if (string.IsNullOrWhiteSpace(fen) || boardSnapshot == null || boardSnapshot.Empty())
                return;

            string positionKey = BuildPositionKey(fen, boardFlipped);
            DateTime now = DateTime.UtcNow;
            if (positionKey == _lastScreenshotKey && now - _lastScreenshotUploadUtc < MinimumScreenshotInterval)
                return;

            _lastScreenshotKey = positionKey;
            _lastScreenshotUploadUtc = now;

            Mat snapshot;
            try
            {
                snapshot = boardSnapshot.Clone();
            }
            catch
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (snapshot)
                {
                    await UploadAsync(new TelemetryEvent
                    {
                        EventType = "position_detected",
                        Fen = fen,
                        BoardFlipped = boardFlipped,
                        Source = source,
                        CapturedAtUtc = now
                    }, snapshot).ConfigureAwait(false);
                }
            });
        }

        public static void QueueAnalysisResult(string fen, IReadOnlyList<MoveVariation> variations, int depth, bool boardFlipped, string source)
        {
            if (!Enabled)
                return;
            if (string.IsNullOrWhiteSpace(fen) || variations.Count == 0)
                return;

            var payload = variations.Take(BuildLimits.MaxLines).Select(v => new TelemetryVariation
            {
                Rank = v.Rank,
                Depth = v.Depth,
                Score = v.Score,
                ScoreType = v.ScoreType,
                MateIn = v.MateIn,
                Moves = v.Moves.Take(8).ToArray()
            }).ToArray();

            _ = Task.Run(() => UploadAsync(new TelemetryEvent
            {
                EventType = "analysis_result",
                Fen = fen,
                BoardFlipped = boardFlipped,
                Source = source,
                AnalysisDepth = depth,
                Variations = payload,
                CapturedAtUtc = DateTime.UtcNow
            }, null));
        }

        private static async Task UploadAsync(TelemetryEvent telemetryEvent, Mat? boardSnapshot)
        {
            if (!await UploadLock.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false))
            {
                DebugRuntime.WriteLine($"[Telemetry] Upload skipped after waiting: {telemetryEvent.EventType}");
                return;
            }

            try
            {
                telemetryEvent.Hwid = HardwareIdentity.GetHardwareId();
                telemetryEvent.AppVersion = GetAppVersion();
                SystemUsageSnapshot usage = SystemUsageTelemetry.Capture();
                telemetryEvent.ProcessCpuPercent = usage.ProcessCpuPercent;
                telemetryEvent.SystemCpuPercent = usage.SystemCpuPercent;
                telemetryEvent.GpuPercent = usage.GpuPercent;

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(JsonSerializer.Serialize(telemetryEvent, JsonOptions)), "metadata");

                if (boardSnapshot != null && !boardSnapshot.Empty())
                {
                    Cv2.ImEncode(".jpg", boardSnapshot, out byte[] jpegBytes, new ImageEncodingParam(ImwriteFlags.JpegQuality, 82));
                    using var imageContent = new ByteArrayContent(jpegBytes);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(imageContent, "screenshot", "board.jpg");
                }

                using HttpResponseMessage response = await Http.PostAsync(UploadEndpoint, form).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    DebugRuntime.WriteLine($"[Telemetry] Upload rejected: HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[Telemetry] Upload failed: {ex.Message}");
            }
            finally
            {
                UploadLock.Release();
            }
        }

        private static string BuildPositionKey(string fen, bool boardFlipped)
        {
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"{fen}|{boardFlipped}"));
            return Convert.ToHexString(digest).Substring(0, 16);
        }

        private static string GetAppVersion()
        {
            try
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private sealed class TelemetryEvent
        {
            public string EventType { get; set; } = "";
            public string Hwid { get; set; } = "";
            public string Fen { get; set; } = "";
            public bool BoardFlipped { get; set; }
            public string Source { get; set; } = "";
            public string AppVersion { get; set; } = "";
            public int AnalysisDepth { get; set; }
            public DateTime CapturedAtUtc { get; set; }
            public double? ProcessCpuPercent { get; set; }
            public double? SystemCpuPercent { get; set; }
            public double? GpuPercent { get; set; }
            public TelemetryVariation[]? Variations { get; set; }
        }

        private sealed class TelemetryVariation
        {
            public int Rank { get; set; }
            public int Depth { get; set; }
            public double Score { get; set; }
            public string ScoreType { get; set; } = "";
            public int? MateIn { get; set; }
            public string[] Moves { get; set; } = Array.Empty<string>();
        }
    }
}
