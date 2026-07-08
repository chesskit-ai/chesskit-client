using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    public sealed class EvalBarForm : Form
    {
        private static readonly Color TransparentBackColor = Color.FromArgb(255, 1, 2, 3);
        private readonly object _lock = new();
        private Rectangle _barRect;
        private Rectangle _lastBoardRect = Rectangle.Empty;
        private double _evaluation = 0.0;
        private bool _isMate = false;
        private int _mateIn = 0;
        private bool _enabled = false;
        private bool _shouldBeVisible = false;
        private EvalDisplayMode _displayMode = EvalDisplayMode.Bar;
        private bool _boardFlipped = false;
        private readonly WinFormsTimer _updateTimer;

        private double _displayedEvaluation = 0.0;
        private double _targetEvaluation = 0.0;

        public EvalBarForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = Dpi.Scale(this, new Size(18, 400));
            BackColor = TransparentBackColor;
            TransparencyKey = TransparentBackColor;
            DoubleBuffered = true;

            _updateTimer = new WinFormsTimer { Interval = 16 };
            _updateTimer.Tick += (s, e) =>
            {
                bool shouldShow = _shouldBeVisible && _enabled;

                if (shouldShow && !Visible)
                {
                    Show();
                    TopMost = true;
                }
                else if (!shouldShow && Visible)
                {
                    Hide();
                }

                if (Visible)
                {
                    AnimateEvaluation();
                    Invalidate();
                }
            };
            _updateTimer.Start();

            // Hide the eval bar from screen capture (stream-safety + no vision
            // feedback); re-applied per-HWND. See CaptureExclusion.
            CaptureExclusion.Register(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            int ex = (int)GetWindowLong(Handle, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(Handle, GWL_EXSTYLE, (IntPtr)ex);
            SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(TransparentBackColor), 244, LWA_COLORKEY | LWA_ALPHA);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Re-lay-out the bar/notch bounds against the last anchored board rect
            // so dragging the overlay between mixed-DPI monitors rescales the bar
            // width, notch size, and inset margins. Then repaint for the new DPI.
            if (!_lastBoardRect.IsEmpty)
                UpdatePosition(_lastBoardRect);
            Invalidate();
        }

        private void AnimateEvaluation()
        {
            if (Math.Abs(_displayedEvaluation - _targetEvaluation) > 0.01)
            {
                double diff = _targetEvaluation - _displayedEvaluation;
                _displayedEvaluation += diff * 0.18;
            }
            else
            {
                _displayedEvaluation = _targetEvaluation;
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetEnabled(enabled)));
                return;
            }

            lock (_lock)
            {
                _enabled = enabled;
                if (!enabled)
                {
                    _shouldBeVisible = false;
                }
            }
        }

        public void SetBoardVisible(bool visible)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetBoardVisible(visible)));
                return;
            }

            lock (_lock)
            {
                _shouldBeVisible = visible;
            }

            // Hide inline instead of waiting for the reconcile timer's next
            // tick: on a minimize the bar must vanish with the window, and
            // the extra ~16ms was visible as a lingering sliver.
            if (!visible && Visible)
            {
                try { Hide(); } catch { }
            }
        }

        /// <summary>Mirror the displayed board's orientation: when flipped (viewing from
        /// Black), Black sits at the bottom of the bar and fills upward for its advantage.</summary>
        public void SetBoardFlipped(bool flipped)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetBoardFlipped(flipped)));
                return;
            }

            if (_boardFlipped == flipped)
                return;
            _boardFlipped = flipped;
            Invalidate();
        }

        public void SetDisplayMode(EvalDisplayMode mode)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetDisplayMode(mode)));
                return;
            }

            lock (_lock)
            {
                _displayMode = mode;
            }

            if (!_lastBoardRect.IsEmpty)
                UpdatePosition(_lastBoardRect);
            Invalidate();
        }

        public void UpdatePosition(Rectangle boardRect)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdatePosition(boardRect)));
                return;
            }

            lock (_lock)
            {
                _lastBoardRect = boardRect;
                if (_displayMode == EvalDisplayMode.Notch)
                {
                    int notchWidth = Dpi.Scale(this, 112);
                    int notchHeight = Dpi.Scale(this, 20);
                    _barRect = new Rectangle(
                        boardRect.X + (boardRect.Width - notchWidth) / 2,
                        boardRect.Y + Dpi.Scale(this, 2),
                        notchWidth,
                        notchHeight);
                    Bounds = _barRect;
                    return;
                }

                int barWidth = Dpi.Scale(this, 18);
                int barMargin = Dpi.Scale(this, 5);
                _barRect = new Rectangle(
                    boardRect.X - barWidth - barMargin,
                    boardRect.Y,
                    barWidth,
                    boardRect.Height);

                Bounds = _barRect;
            }
        }

        public void UpdateWindowPosition(Rectangle windowRect, Rectangle boardRect)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateWindowPosition(windowRect, boardRect)));
                return;
            }

            _ = windowRect;
            UpdatePosition(boardRect);
        }

        public void UpdateEvaluation(double eval, bool isMate = false, int mateIn = 0)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateEvaluation(eval, isMate, mateIn)));
                return;
            }

            lock (_lock)
            {
                _evaluation = eval;
                _targetEvaluation = eval;
                _isMate = isMate;
                _mateIn = mateIn;
            }
        }

        public void ForceHide()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ForceHide()));
                return;
            }

            _enabled = false;
            _shouldBeVisible = false;
            Hide();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTTRANSPARENT = -1;

            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            double evaluation;
            bool isMate;
            int mateIn;
            EvalDisplayMode displayMode;

            lock (_lock)
            {
                evaluation = _displayedEvaluation;
                isMate = _isMate;
                mateIn = _mateIn;
                displayMode = _displayMode;
            }

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            Rectangle rect = ClientRectangle;
            if (displayMode == EvalDisplayMode.Notch)
            {
                DrawNotch(this, g, rect, evaluation, isMate, mateIn);
                return;
            }

            if (rect.Width < Dpi.Scale(this, 8) || rect.Height < Dpi.Scale(this, 40))
                return;

            int railInset = Dpi.Scale(this, 2);
            Rectangle railRect = new Rectangle(rect.Left + railInset, rect.Top + railInset, rect.Width - railInset * 2, rect.Height - railInset * 2);

            double scaleMax = GetScaleMaxAbs(evaluation, isMate);
            double whitePercent;
            if (isMate)
            {
                whitePercent = mateIn > 0 ? 1.0 : 0.0;
            }
            else
            {
                double clampedEval = Math.Max(-scaleMax, Math.Min(scaleMax, evaluation));
                whitePercent = 0.5 + (clampedEval / scaleMax) * 0.5;
            }

            int whiteHeight = (int)Math.Round(railRect.Height * whitePercent);
            int blackHeight = railRect.Height - whiteHeight;
            // The side at the BOTTOM of the board sits at the bottom of the bar and
            // fills UP for its advantage. Not flipped => White at the bottom; flipped
            // (viewing from Black) => Black at the bottom.
            bool whiteAtBottom = !_boardFlipped;
            int topHeight = whiteAtBottom ? blackHeight : whiteHeight;
            int bottomHeight = railRect.Height - topHeight;
            int splitY = railRect.Top + topHeight;

            using (SolidBrush shellBrush = new SolidBrush(Color.FromArgb(12, 13, 16)))
            {
                g.FillRectangle(shellBrush, rect);
            }

            if (topHeight > 0)
            {
                using SolidBrush topBrush = new SolidBrush(whiteAtBottom ? Color.FromArgb(16, 18, 22) : Color.FromArgb(185, 188, 192));
                g.FillRectangle(topBrush, new Rectangle(railRect.X, railRect.Top, railRect.Width, topHeight));
            }

            if (bottomHeight > 0)
            {
                using SolidBrush bottomBrush = new SolidBrush(whiteAtBottom ? Color.FromArgb(185, 188, 192) : Color.FromArgb(16, 18, 22));
                g.FillRectangle(bottomBrush, new Rectangle(railRect.X, splitY, railRect.Width, bottomHeight));
            }

            int centerY = railRect.Top + railRect.Height / 2;
            using (Pen centerPen = new Pen(Color.FromArgb(210, 176, 182, 192), Dpi.Scale(this, 2f)))
            {
                g.DrawLine(centerPen, railRect.Left, centerY, railRect.Right - 1, centerY);
            }

            // The white/black split IS the eval indicator (more light up top = White
            // is better). A thin line marks it — no numeric scale, just a slim bar.
            int splitMarkerY = Math.Max(railRect.Top + 1, Math.Min(railRect.Bottom - 1, splitY));
            using (Pen markerPen = new Pen(Color.FromArgb(225, 232, 234, 238), Dpi.Scale(this, 1.5f)))
            {
                g.DrawLine(markerPen, railRect.Left, splitMarkerY, railRect.Right - 1, splitMarkerY);
            }

            using Pen borderPen = new Pen(Color.FromArgb(150, 58, 64, 74), Dpi.Scale(this, 1f));
            g.DrawRectangle(borderPen, railRect.X, railRect.Y, railRect.Width - 1, railRect.Height - 1);
        }

        private static void DrawScaleMarker(
            Graphics g,
            Rectangle railRect,
            Pen tickPen,
            Font font,
            Brush textBrush,
            StringFormat format,
            double normalizedY,
            string label)
        {
            int y = railRect.Top + (int)Math.Round(railRect.Height * normalizedY);
            const int labelEdgePadding = 9;
            y = Math.Max(railRect.Top + labelEdgePadding, Math.Min(railRect.Bottom - labelEdgePadding, y));

            int tickWidth = label == "0" ? railRect.Width : Math.Max(5, railRect.Width - 5);
            g.DrawLine(tickPen, railRect.Right - tickWidth, y, railRect.Right - 1, y);

            // Use the full form width (the rail is inset 2px each side) and draw a
            // dark outline so the labels stay legible over BOTH the bright
            // (white-advantage) and dark (black-advantage) bar fills.
            RectangleF labelRect = new RectangleF(railRect.Left - 2, y - 8, railRect.Width + 4, 16);
            using (SolidBrush outline = new SolidBrush(Color.FromArgb(220, 5, 6, 9)))
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;
                        RectangleF shadow = labelRect;
                        shadow.Offset(dx, dy);
                        g.DrawString(label, font, outline, shadow, format);
                    }
                }
            }
            g.DrawString(label, font, textBrush, labelRect, format);
        }

        private static void DrawNotch(Control owner, Graphics g, Rectangle rect, double evaluation, bool isMate, int mateIn)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            string text = isMate
                ? $"M{Math.Abs(mateIn)}"
                : evaluation.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);

            using Font font = new Font("Segoe UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);
            using StringFormat format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.None
            };

            Color textColor;
            if (isMate)
            {
                textColor = mateIn >= 0
                    ? Color.FromArgb(252, 88, 92, 100)
                    : Color.FromArgb(252, 10, 11, 13);
            }
            else if (evaluation > 0.05)
            {
                textColor = Color.FromArgb(252, 88, 92, 100);
            }
            else if (evaluation < -0.05)
            {
                textColor = Color.FromArgb(252, 10, 11, 13);
            }
            else
            {
                textColor = Color.FromArgb(250, 42, 45, 50);
            }

            using SolidBrush textBrush = new SolidBrush(textColor);
            int notchTextPad = Dpi.Scale(owner, 6);
            Rectangle textRect = new Rectangle(rect.X + notchTextPad, rect.Y, Math.Max(1, rect.Width - notchTextPad * 2), rect.Height);
            g.DrawString(text, font, textBrush, textRect, format);
        }

        private static double GetScaleMaxAbs(double evaluation, bool isMate)
        {
            if (isMate)
                return 10.0;

            double absEval = Math.Abs(evaluation);
            if (absEval <= 4.0)
                return 4.0;
            if (absEval <= 8.0)
                return 8.0;
            if (absEval <= 12.0)
                return 12.0;
            if (absEval <= 20.0)
                return 20.0;
            if (absEval <= 50.0)
                return 50.0;
            if (absEval <= 100.0)
                return 100.0;

            return Math.Ceiling(absEval / 50.0) * 50.0;
        }

        private static double EvalToNormalizedY(double eval, double scaleMax)
        {
            double clamped = Math.Max(-scaleMax, Math.Min(scaleMax, eval));
            return 0.5 - (clamped / scaleMax) * 0.5;
        }

        private static string FormatScaleLabel(double value)
        {
            double abs = Math.Abs(value);
            string sign = value > 0 ? "+" : "-";

            if (abs >= 10 || Math.Abs(abs - Math.Round(abs)) < 0.001)
                return sign + Math.Round(abs).ToString(System.Globalization.CultureInfo.InvariantCulture);

            return sign + abs.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void DrawMateBadge(Graphics g, Rectangle railRect, int mateIn)
        {
            string mateText = $"M{Math.Abs(mateIn)}";
            Rectangle mateRect = new Rectangle(
                railRect.Left - 1,
                mateIn > 0 ? railRect.Top + 3 : railRect.Bottom - 20,
                railRect.Width + 2,
                16);

            using Font font = new Font("Segoe UI Semibold", 6.2f, FontStyle.Bold);
            using SolidBrush textBrush = new SolidBrush(Color.FromArgb(232, 234, 238));
            using SolidBrush backBrush = new SolidBrush(Color.FromArgb(225, 20, 22, 27));
            using StringFormat format = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.None
            };

            g.FillRectangle(backBrush, mateRect);
            g.DrawString(mateText, font, textBrush, mateRect, format);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int LWA_COLORKEY = 0x1;
        private const int LWA_ALPHA = 0x2;
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
