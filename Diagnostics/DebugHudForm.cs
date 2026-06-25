using System.Drawing.Drawing2D;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    /// <summary>
    /// A small always-on-top floating HUD that shows runtime diagnostics: FPS,
    /// capture mode, engine, execution mode, tracking state, etc. Toggleable
    /// from the settings toolbar. Unlike DebugRuntime (which intercepts the
    /// console), this works in Release builds and is meant to be visible
    /// alongside normal gameplay.
    ///
    /// The form invalidates at 5 Hz which is plenty for a stats panel and
    /// avoids fighting the main capture loop for compositor time.
    /// </summary>
    public sealed partial class DebugHudForm : Form
    {
        // Fields the main app updates on every metrics tick.
        private readonly object _stateLock = new();
        private double _fps = 0;
        private double _fenPerSec = 0;
        private string _captureMode = "?";
        private string _engineName = "?";
        private string _executionMode = "?";
        private bool _tracking = false;
        private bool _boardTracked = false;
        private bool _analysisOn = false;
        private string _analysisSide = "OFF";
        private int _arrowCount = 0;
        private bool _waitingForOpponent = false;
        private double _lastMoveLatencyMs = -1;
        private string _lastEvent = "";
        private string _lastEngineFen = "";
        private string _streamTransport = "none";
        private bool _mirrorActive = false;

        private bool _enabled = false;
        private readonly WinFormsTimer _updateTimer;

        private static readonly Font HeaderFont = new("Segoe UI", 9f, FontStyle.Bold);
        private static readonly Font BodyFont = new("Consolas", 9f);
        private static readonly Brush HeaderBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
        private static readonly Brush LabelBrush = new SolidBrush(Color.FromArgb(160, 160, 160));
        private static readonly Brush ValueBrush = new SolidBrush(Color.FromArgb(220, 240, 200));
        private static readonly Brush BgBrush = new SolidBrush(Color.FromArgb(235, 28, 28, 30));
        private static readonly Pen BorderPen = new(Color.FromArgb(80, 80, 80), 1);

        public DebugHudForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(28, 28, 30);
            DoubleBuffered = true;

            // Compute the DPI-aware layout (size, column positions, line height)
            // from actually-rendered text metrics plus DPI-scaled pixel literals.
            // Recomputed on OnDpiChanged so dragging the HUD between mixed-DPI
            // monitors re-lays-out instead of clipping.
            RecomputeLayout();

            _updateTimer = new WinFormsTimer { Interval = 200 };  // 5 Hz
            _updateTimer.Tick += (s, e) =>
            {
                if (_enabled && !Visible)
                {
                    Show();
                    TopMost = true;
                }
                else if (!_enabled && Visible)
                {
                    Hide();
                }
                if (Visible)
                {
                    Invalidate();
                }
            };
            _updateTimer.Start();
        }

        // Layout values computed from actual rendered text metrics plus
        // DPI-scaled pixel literals. Recomputed on OnDpiChanged so the HUD
        // re-lays-out when moved between mixed-DPI monitors. Avoids hardcoded
        // pixel positions that don't scale with DPI.
        private int _rowLabelX = 14;
        private int _rowValueX = 110;
        private int _rowLineHeight = 18;
        private int _rowPaddingY = 14;

        // Recompute size + column layout from current DeviceDpi. Text-metric
        // values (via a DeviceDpi-matched measure Graphics) already reflect the
        // monitor's DPI; the pixel literals (padding, column gap, line spacing)
        // are routed through Dpi.Scale so they grow with it too. At 96 DPI every
        // Dpi.Scale call is an identity no-op, so 100% rendering is unchanged.
        private void RecomputeLayout()
        {
            using var g = Dpi.CreateMeasureGraphics(this, out var bmp);
            try
            {
                var headerSize = g.MeasureString("Chess Kit Debug HUD", HeaderFont);
                // Widest label we'll ever draw (Capture / Tracking / Analysis / Last move)
                var labelSize = g.MeasureString("Last move", BodyFont);
                // Widest value we'll likely render. Engine names can be long
                // (e.g. "stockfish-windows-x86-64-avx2"); budget for ~30 chars at
                // the body font size so they fit without ellipsizing.
                var valueSize = g.MeasureString(new string('M', 30), BodyFont);

                int padding = Dpi.Scale(this, 14);
                int columnGap = Dpi.Scale(this, 16);
                int formWidth = (int)Math.Ceiling(
                    Math.Max(headerSize.Width,
                             labelSize.Width + columnGap + valueSize.Width)
                    + padding * 2);

                int lineHeight = (int)Math.Ceiling(Math.Max(headerSize.Height, valueSize.Height)) + Dpi.Scale(this, 4);
                // 1 header + 12 data lines + 1 event line + 3 FEN lines (+ gaps/padding).
                // The event line ("Move at …") was previously unbudgeted, which clipped
                // the wrapped FEN's last line off the bottom edge.
                int formHeight = padding + lineHeight + Dpi.Scale(this, 4) + lineHeight * 12 + Dpi.Scale(this, 4) + lineHeight + lineHeight * 3 + padding;

                Size = new Size(formWidth, formHeight);
                _rowLabelX = padding;
                _rowValueX = padding + (int)Math.Ceiling(labelSize.Width) + columnGap;
                _rowLineHeight = lineHeight;
                _rowPaddingY = padding;
            }
            finally
            {
                bmp.Dispose();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            RecomputeLayout();
            Invalidate();
        }

        public void SetEnabled(bool on)
        {
            _enabled = on;
        }

        public bool IsEnabled => _enabled;

        public void UpdateMetrics(double fps, double fenPerSec, string captureMode,
                                  string engineName, string executionMode,
                                  bool tracking, bool boardTracked, bool analysisOn,
                                  string analysisSide, int arrowCount,
                                  bool waitingForOpponent, double lastMoveLatencyMs,
                                  string lastEvent, string lastEngineFen,
                                  string streamTransport, bool mirrorActive)
        {
            lock (_stateLock)
            {
                _fps = fps;
                _fenPerSec = fenPerSec;
                _captureMode = captureMode ?? "?";
                _engineName = engineName ?? "?";
                _executionMode = executionMode ?? "?";
                _tracking = tracking;
                _boardTracked = boardTracked;
                _analysisOn = analysisOn;
                _analysisSide = analysisSide ?? "OFF";
                _arrowCount = arrowCount;
                _waitingForOpponent = waitingForOpponent;
                _lastMoveLatencyMs = lastMoveLatencyMs;
                _lastEvent = lastEvent ?? "";
                _lastEngineFen = lastEngineFen ?? "";
                _streamTransport = streamTransport ?? "none";
                _mirrorActive = mirrorActive;
            }
        }

        // Position the HUD next to the toolbar so it's visible but not in the
        // way. Caller (Program.cs / SettingsToolbar) decides where to put it.
        public void UpdatePosition(System.Drawing.Point topLeft)
        {
            if (Location != topLeft)
                Location = topLeft;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_NOACTIVATE: don't steal focus when shown.
                // WS_EX_TOOLWINDOW: don't show in alt-tab.
                // WS_EX_TOPMOST: stay on top.
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;
            g.FillRectangle(BgBrush, rect);
            g.DrawRectangle(BorderPen, 0, 0, rect.Width - 1, rect.Height - 1);

            int x = _rowLabelX;
            int y = _rowPaddingY;
            int valueX = _rowValueX;
            int lineHeight = _rowLineHeight;

            // How wide can the value column be without spilling out the right
            // edge? Used to clip / truncate strings to fit.
            int valueColWidth = rect.Width - valueX - _rowLabelX;

            // Header
            g.DrawString("Chess Kit Debug HUD", HeaderFont, HeaderBrush, x, y);
            y += lineHeight + Dpi.Scale(this, 4);

            // Snapshot under lock to avoid torn reads
            double fps, fenPerSec, lastMoveLatencyMs;
            string captureMode, engineName, executionMode, analysisSide, lastEvent, lastEngineFen, streamTransport;
            bool tracking, boardTracked, analysisOn, waitingForOpponent, mirrorActive;
            int arrowCount;
            lock (_stateLock)
            {
                fps = _fps;
                fenPerSec = _fenPerSec;
                captureMode = _captureMode;
                engineName = _engineName;
                executionMode = _executionMode;
                tracking = _tracking;
                boardTracked = _boardTracked;
                analysisOn = _analysisOn;
                analysisSide = _analysisSide;
                arrowCount = _arrowCount;
                waitingForOpponent = _waitingForOpponent;
                lastMoveLatencyMs = _lastMoveLatencyMs;
                lastEvent = _lastEvent;
                lastEngineFen = _lastEngineFen;
                streamTransport = _streamTransport;
                mirrorActive = _mirrorActive;
            }

            DrawRow(g, x, y, valueX, valueColWidth, "FPS", $"{fps:F1}");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "FEN/s", $"{fenPerSec:F1}");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Capture", captureMode);
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Engine", engineName);
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Exec", executionMode);
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Stream", TransportLabel(streamTransport));
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Tracking", tracking ? "ON" : "OFF");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Board", boardTracked ? "YES" : "NO");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Mirror", mirrorActive ? "ON" : "OFF");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Analysis", analysisOn ? analysisSide : "OFF");
            y += lineHeight;
            DrawRow(g, x, y, valueX, valueColWidth, "Arrows", $"{arrowCount}{(waitingForOpponent ? " (waiting)" : "")}");
            y += lineHeight;

            string latencyText = lastMoveLatencyMs >= 0
                ? $"{lastMoveLatencyMs:F0} ms"
                : "—";
            DrawRow(g, x, y, valueX, valueColWidth, "Last move", latencyText);
            y += lineHeight + Dpi.Scale(this, 4);

            if (!string.IsNullOrWhiteSpace(lastEvent))
            {
                using var eventFont = new Font(BodyFont.FontFamily, 8.5f, FontStyle.Italic);
                // Clip the event line to the form width so it never spills.
                var eventRect = new RectangleF(x, y, rect.Width - x * 2, lineHeight);
                using var noWrap = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                g.DrawString(lastEvent, eventFont, LabelBrush, eventRect, noWrap);
                y += lineHeight;
            }

            // Show the FEN being sent to the engine. Wraps to a second line if
            // needed. This is the ground truth for diagnosing arrow-direction
            // bugs: if the rendered board doesn't match this string, the
            // detector and engine disagree.
            if (!string.IsNullOrWhiteSpace(lastEngineFen))
            {
                using var fenFont = new Font(BodyFont.FontFamily, 8f);
                var fenRect = new RectangleF(x, y, rect.Width - x * 2, lineHeight * 3);
                using var wrap = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = 0  // wrapping enabled
                };
                g.DrawString($"FEN: {lastEngineFen}", fenFont, LabelBrush, fenRect, wrap);
            }
        }

        // Friendly label for the vision stream transport reported by the detector.
        private static string TransportLabel(string transport) => transport switch
        {
            "ws" => "WebSocket",
            "http" => "HTTP (fallback)",
            _ => "—"
        };

        private static void DrawRow(Graphics g, int x, int y, int valueX, int valueColWidth, string label, string value)
        {
            g.DrawString(label, BodyFont, LabelBrush, x, y);
            // Clip / ellipsize the value if it's too wide for the column.
            var valueRect = new RectangleF(valueX, y, valueColWidth, BodyFont.Height + 4);
            using var noWrap = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(value, BodyFont, ValueBrush, valueRect, noWrap);
        }

        // Note: don't override Dispose(bool) — the auto-generated Designer
        // partial already does that. Stop the timer when the form is closed
        // instead; the Designer's Dispose handles the rest.
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _updateTimer?.Stop(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
