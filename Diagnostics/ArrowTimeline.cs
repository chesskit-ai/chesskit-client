using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace ChessKit
{
    /// <summary>
    /// Structured JSONL timeline of the arrow pipeline: board detection,
    /// engine requests/responses, and overlay paint/hide events. Every line
    /// carries a monotonic timestamp so request->response->painted latencies
    /// and visible-gap windows can be reconstructed offline (see
    /// tools/analyze_arrow_timeline.py).
    ///
    /// Enabled by default in DEBUG builds; in release builds opt in with
    /// CHESSKIT_ARROW_TIMELINE=1. CHESSKIT_ARROW_TIMELINE=0 disables it
    /// everywhere. Writes debug/arrow-timeline.jsonl next to session.log.
    ///
    /// Writes are ASYNCHRONOUS: Log() formats the line on the calling thread
    /// (cheap) and hands it to a background writer, so NO disk I/O ever lands
    /// on the hot path. This keeps the instrumentation from distorting the very
    /// latencies it measures (a synchronous flush per event added real ms in
    /// debug builds).
    /// </summary>
    internal static class ArrowTimeline
    {
        private static readonly object _sync = new();
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static StreamWriter? _writer;
        private static bool _initialized;
        private static bool _enabled;
        private static BlockingCollection<string>? _queue;
        private static Thread? _writerThread;

        public static bool Enabled
        {
            get
            {
                if (!_initialized)
                    Initialize();
                return _enabled;
            }
        }

        private static void Initialize()
        {
            lock (_sync)
            {
                if (_initialized)
                    return;
                _initialized = true;

                string? env = Environment.GetEnvironmentVariable("CHESSKIT_ARROW_TIMELINE");
#if DEBUG
                _enabled = !string.Equals(env?.Trim(), "0", StringComparison.Ordinal);
#else
                string normalized = env?.Trim().ToLowerInvariant() ?? "";
                _enabled = normalized is "1" or "true" or "yes" or "on";
#endif
                if (!_enabled)
                    return;

                try
                {
                    string logDir = Path.Combine(AppContext.BaseDirectory, "debug");
                    Directory.CreateDirectory(logDir);
                    _writer = new StreamWriter(
                        Path.Combine(logDir, "arrow-timeline.jsonl"),
                        append: false,
                        Encoding.UTF8)
                    {
                        // No per-line AutoFlush: the background thread flushes
                        // in batches, keeping disk I/O off the hot path.
                        AutoFlush = false
                    };
                    _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
                    _writerThread = new Thread(WriterLoop)
                    {
                        IsBackground = true,
                        Name = "ArrowTimelineWriter",
                        Priority = ThreadPriority.BelowNormal,
                    };
                    _writerThread.Start();
                }
                catch
                {
                    _enabled = false;
                    _writer = null;
                    _queue = null;
                }
            }
        }

        private static void WriterLoop()
        {
            var writer = _writer;
            var queue = _queue;
            if (writer == null || queue == null)
                return;

            try
            {
                // GetConsumingEnumerable blocks until an item is available and
                // ends when CompleteAdding() is called on shutdown. Flush once
                // the queue drains (no pending items) so the file stays current
                // for a live tail without flushing every single line.
                foreach (string line in queue.GetConsumingEnumerable())
                {
                    writer.WriteLine(line);
                    if (queue.Count == 0)
                    {
                        try { writer.Flush(); } catch { }
                    }
                }
            }
            catch
            {
                // Writer/queue disposed during shutdown - nothing to do.
            }
            finally
            {
                try { writer.Flush(); } catch { }
            }
        }

        /// <summary>
        /// Append one event. Optional fields are omitted from the JSON when
        /// left at their defaults, so call sites stay terse.
        /// </summary>
        public static void Log(
            string evt,
            string? fen = null,
            string? reason = null,
            string? stage = null,
            int depth = int.MinValue,
            int count = int.MinValue,
            string? reqId = null,
            double ms = double.MinValue,
            string? extra = null)
        {
            if (!Enabled)
                return;

            var sb = new StringBuilder(192);
            sb.Append("{\"t\":").Append(_clock.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"wall\":\"").Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append('"');
            Append(sb, "evt", evt);
            Append(sb, "fen", fen);
            Append(sb, "reason", reason);
            Append(sb, "stage", stage);
            if (depth != int.MinValue)
                sb.Append(",\"depth\":").Append(depth);
            if (count != int.MinValue)
                sb.Append(",\"count\":").Append(count);
            Append(sb, "reqId", reqId);
            if (ms != double.MinValue)
                sb.Append(",\"ms\":").Append(ms.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            Append(sb, "extra", extra);
            sb.Append('}');

            // Hand off to the background writer - never touches the disk here.
            try { _queue?.Add(sb.ToString()); } catch { }
        }

        public static void Shutdown()
        {
            BlockingCollection<string>? queue;
            Thread? thread;
            lock (_sync)
            {
                _enabled = false;
                queue = _queue;
                thread = _writerThread;
            }

            // Signal the writer to drain remaining events, then wait briefly
            // for it to finish so the final lines reach disk.
            try { queue?.CompleteAdding(); } catch { }
            try { thread?.Join(1000); } catch { }

            lock (_sync)
            {
                try { _writer?.Flush(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _queue?.Dispose(); } catch { }
                _writer = null;
                _queue = null;
                _writerThread = null;
            }
        }

        private static void Append(StringBuilder sb, string key, string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            sb.Append(",\"").Append(key).Append("\":\"");
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                            sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
