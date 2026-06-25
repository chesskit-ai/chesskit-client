using System.Text;

namespace ChessKit
{
    internal sealed class DebugConsoleSnapshot
    {
        public bool IsTracking { get; init; }
        public bool AnalysisEnabled { get; init; }
        public string AnalysisSide { get; init; } = "OFF";
        public bool WaitingForOpponent { get; init; }
        public bool BoardTracked { get; init; }
        public bool ShowingArrows { get; init; }
        public int ArrowCount { get; init; }
        public double Fps { get; init; }
        public double FenPerSecond { get; init; }
        public string CurrentFen { get; init; } = "";
        public string LastUserMoveFen { get; init; } = "";
        public string ExecutionMode { get; init; } = "";
        public string CurrentEngine { get; init; } = "";
        public string CaptureMode { get; init; } = "";
    }

    internal static class DebugRuntime
    {
#if DEBUG
        private static readonly object _sync = new();
        private static readonly Queue<string> _recentEvents = new();
        private static readonly StringBuilder _lineBuffer = new();
        private static TextWriter? _originalOut;
        private static TextWriter? _originalError;
        private static StreamWriter? _sessionWriter;
        private static bool _initialized;
        private static DateTime _lastRenderUtc = DateTime.MinValue;
        private static DebugConsoleSnapshot _snapshot = new();
        private static string _lastEvent = "Waiting for startup";

        public static string SessionLogPath { get; private set; } = "";

        public static void Initialize()
        {
            lock (_sync)
            {
                if (_initialized)
                    return;

                _originalOut = Console.Out;
                _originalError = Console.Error;

                string logDir = Path.Combine(AppContext.BaseDirectory, "debug");
                Directory.CreateDirectory(logDir);

                SessionLogPath = Path.Combine(logDir, "session.log");
                _sessionWriter = new StreamWriter(SessionLogPath, append: false, Encoding.UTF8)
                {
                    AutoFlush = true
                };

                var interceptingWriter = new InterceptingTextWriter(HandleConsoleText);
                Console.SetOut(interceptingWriter);
                Console.SetError(interceptingWriter);

                _initialized = true;
                _lastEvent = $"Debug log: {SessionLogPath}";
                WriteSessionLine("[DEBUG] Session started");
                Render(force: true);
            }
        }

        public static void WriteLine(string message)
        {
            if (!_initialized)
                Initialize();

            // Must lock here — HandleConsoleLine mutates _recentEvents and
            // _lastEvent, which UpdateStatus's Render path iterates. Without
            // this lock, concurrent WriteLine + UpdateStatus calls throw
            // InvalidOperationException ("Collection was modified") inside
            // BuildLines' foreach over _recentEvents.
            lock (_sync)
            {
                HandleConsoleLine(message);
            }
        }

        public static void UpdateStatus(DebugConsoleSnapshot snapshot, string? lastEvent = null)
        {
            if (!_initialized)
                return;

            lock (_sync)
            {
                _snapshot = snapshot;
                if (!string.IsNullOrWhiteSpace(lastEvent))
                {
                    _lastEvent = lastEvent!;
                }

                Render();
            }
        }

        public static void Shutdown()
        {
            lock (_sync)
            {
                if (!_initialized)
                    return;

                WriteSessionLine("[DEBUG] Session ended");
                _sessionWriter?.Dispose();
                _sessionWriter = null;

                if (_originalOut != null)
                    Console.SetOut(_originalOut);

                if (_originalError != null)
                    Console.SetError(_originalError);

                _initialized = false;
            }
        }

        private static void HandleConsoleText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            lock (_sync)
            {
                foreach (char ch in text)
                {
                    if (ch == '\r')
                        continue;

                    if (ch == '\n')
                    {
                        if (_lineBuffer.Length > 0)
                        {
                            HandleConsoleLine(_lineBuffer.ToString());
                            _lineBuffer.Clear();
                        }
                    }
                    else
                    {
                        _lineBuffer.Append(ch);
                    }
                }
            }
        }

        private static void HandleConsoleLine(string line)
        {
            string trimmed = line?.TrimEnd() ?? "";
            if (trimmed.Length == 0)
                return;

            WriteSessionLine(trimmed);

            if (ShouldShowInRecentEvents(trimmed))
            {
                _recentEvents.Enqueue(trimmed);
                while (_recentEvents.Count > 8)
                {
                    _recentEvents.Dequeue();
                }
            }

            if (ShouldUpdateLastEvent(trimmed))
            {
                _lastEvent = trimmed;
            }

            Render();
        }

        private static void WriteSessionLine(string line)
        {
            _sessionWriter?.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}");
        }

        private static bool ShouldShowInRecentEvents(string line)
        {
            if (line.StartsWith("[FEN DETECTED]") ||
                line.StartsWith("[METRICS]") ||
                line.StartsWith("[ParseSimplified]") ||
                line.StartsWith("[Infinite]") ||
                line.Contains("RAW OUTPUT START") ||
                line.Contains("RAW OUTPUT END") ||
                line.Contains("TIMEOUT OUTPUT START") ||
                line.Contains("TIMEOUT OUTPUT END") ||
                line.StartsWith("  Move:"))
            {
                return false;
            }

            return true;
        }

        private static bool ShouldUpdateLastEvent(string line)
        {
            return !line.StartsWith("[FEN DETECTED]") &&
                   !line.StartsWith("[METRICS]");
        }

        private static void Render(bool force = false)
        {
            if (_originalOut == null)
                return;

            DateTime now = DateTime.UtcNow;
            if (!force && (now - _lastRenderUtc).TotalMilliseconds < 125)
                return;

            _lastRenderUtc = now;

            int width = Math.Max(80, Console.WindowWidth > 0 ? Console.WindowWidth : 120);
            var lines = BuildLines(width);

            try
            {
                Console.CursorVisible = false;
                Console.SetCursorPosition(0, 0);

                foreach (string line in lines)
                {
                    _originalOut.Write(Fit(line, width));
                    _originalOut.Write(Environment.NewLine);
                }
            }
            catch
            {
                // Ignore console rendering issues and keep file logging working.
            }
        }

        private static List<string> BuildLines(int width)
        {
            var lines = new List<string>
            {
                Border("Chess Kit Debug", width),
                Row($"Tracking: {OnOff(_snapshot.IsTracking)}", $"Analysis: {(_snapshot.AnalysisEnabled ? _snapshot.AnalysisSide : "OFF")}", width),
                Row($"Waiting: {YesNo(_snapshot.WaitingForOpponent)}", $"Board: {YesNo(_snapshot.BoardTracked)}", width),
                Row($"Arrows: {(_snapshot.ShowingArrows ? _snapshot.ArrowCount.ToString() : "0")}", $"Exec: {Safe(_snapshot.ExecutionMode, 16)}", width),
                Body("Engine", Safe(_snapshot.CurrentEngine, 64), width),
                Row($"FPS: {_snapshot.Fps:F1}", $"Capture: {Safe(_snapshot.CaptureMode, 32)}", width),
                Divider(width),
                Body("Current FEN", _snapshot.CurrentFen, width),
                Body("Last User FEN", _snapshot.LastUserMoveFen, width),
                Body("Last Event", _lastEvent, width),
                Divider(width),
                "Recent Events:"
            };

            // Snapshot _recentEvents before iterating. This iteration runs
            // under _sync from all current callers, but snapshotting protects
            // against any future caller that forgets the lock — and it's
            // essentially free (small queue).
            string[] recentSnapshot = _recentEvents.ToArray();
            for (int i = recentSnapshot.Length - 1; i >= 0; i--)
            {
                lines.Add($"  {recentSnapshot[i]}");
            }

            while (lines.Count < 18)
            {
                lines.Add("");
            }

            lines.Add(Divider(width));
            lines.Add($"Log file: {SessionLogPath}");
            lines.Add("Keys: F1 overlay | F2 white | F3 W+B | F4 black | F7 FEN | F8 lines | F9 eval");
            return lines;
        }

        private static string Border(string title, int width)
        {
            string inner = $" {title} ";
            int remaining = Math.Max(0, width - inner.Length - 2);
            return $"┌{inner}{new string('─', remaining)}┐";
        }

        private static string Divider(int width)
        {
            return new string('─', Math.Max(0, width - 1));
        }

        private static string Row(string left, string right, int width)
        {
            int available = Math.Max(10, width - 4);
            int leftWidth = available / 2;
            int rightWidth = available - leftWidth;
            return $"{Safe(left, leftWidth).PadRight(leftWidth)}  {Safe(right, rightWidth)}";
        }

        private static string Body(string label, string value, int width)
        {
            string prefix = $"{label}: ";
            return prefix + Safe(value, Math.Max(0, width - prefix.Length - 1));
        }

        private static string Fit(string value, int width)
        {
            return Safe(value, Math.Max(0, width - 1)).PadRight(Math.Max(0, width - 1));
        }

        private static string Safe(string? value, int maxLength)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace(Environment.NewLine, " ");
            if (text.Length <= maxLength)
                return text;

            return maxLength <= 3 ? text[..maxLength] : text[..(maxLength - 3)] + "...";
        }

        private static string OnOff(bool value) => value ? "ON" : "OFF";
        private static string YesNo(bool value) => value ? "YES" : "NO";

        private sealed class InterceptingTextWriter : TextWriter
        {
            private readonly Action<string> _writeAction;

            public InterceptingTextWriter(Action<string> writeAction)
            {
                _writeAction = writeAction;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value) => _writeAction(value.ToString());
            public override void Write(string? value)
            {
                if (value != null)
                    _writeAction(value);
            }

            public override void WriteLine(string? value)
            {
                if (value != null)
                    _writeAction(value + Environment.NewLine);
            }
        }
#else
        public static string SessionLogPath => "";
        public static void Initialize() { }
        public static void WriteLine(string message) { }
        public static void UpdateStatus(DebugConsoleSnapshot snapshot, string? lastEvent = null) { }
        public static void Shutdown() { }
#endif
    }
}
