using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    internal sealed class StartupStatusForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _statusLabel;
        private readonly ProgressBar _progressBar;
        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(72, 150, 255);
        private float _uiScale;
        private readonly WinFormsTimer _centerTimer;
        private int _centerPassesRemaining = 6;

        public StartupStatusForm()
        {
            _uiScale = ComputeUiScale();

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _surfaceColor;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            ClientSize = ScaleSize(new Size(420, 172));
            Padding = new Padding(ScaleValue(24), ScaleValue(22), ScaleValue(24), ScaleValue(22));
            Text = "ChessKit starting";
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(155, 178, 255),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold),
                Text = "CHESSKIT",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 15.5f, FontStyle.Bold),
                Text = "Starting ChessKit",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _statusLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(196, 206, 230),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.4f, FontStyle.Regular),
                Text = "Preparing application...",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 24
            };

            _centerTimer = new WinFormsTimer { Interval = 80 };
            _centerTimer.Tick += (_, _) =>
            {
                ForceCenterTopMost();
                _centerPassesRemaining--;
                if (_centerPassesRemaining <= 0)
                    _centerTimer.Stop();
            };

            Controls.Add(_eyebrowLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_statusLabel);
            Controls.Add(_progressBar);

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) =>
            {
                ApplyRoundedRegion();
                ApplyLayout();
            };

            ApplyRoundedRegion();
            ApplyLayout();
            ForceCenterTopMost();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ForceCenterTopMost();
            _centerPassesRemaining = 6;
            _centerTimer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ForceCenterTopMost();
        }

        public void SetStatus(string status, int? progressPercent = null, bool indeterminate = false)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetStatus(status, progressPercent, indeterminate)));
                return;
            }

            _statusLabel.Text = string.IsNullOrWhiteSpace(status)
                ? "Preparing application..."
                : status.Trim();

            if (indeterminate || !progressPercent.HasValue)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 24;
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Value = Math.Clamp(progressPercent.Value, _progressBar.Minimum, _progressBar.Maximum);
            }

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using GraphicsPath path = CreateRoundedRectanglePath(bounds, ScaleValue(18));
            using SolidBrush backgroundBrush = new SolidBrush(_surfaceColor);
            using Pen edgePen = new Pen(_surfaceEdgeColor, 1.2f);
            using SolidBrush accentBrush = new SolidBrush(_accentColor);

            e.Graphics.FillPath(backgroundBrush, path);
            e.Graphics.DrawPath(edgePen, path);

            Rectangle accentRect = new Rectangle(Padding.Left, Padding.Top - ScaleValue(5), ScaleValue(124), ScaleValue(4));
            using GraphicsPath accentPath = CreateRoundedRectanglePath(accentRect, ScaleValue(2));
            e.Graphics.FillPath(accentBrush, accentPath);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_surfaceColor);
        }

        private void ApplyLayout()
        {
            int left = Padding.Left;
            int width = ClientSize.Width - Padding.Left - Padding.Right;
            int top = Padding.Top + ScaleValue(6);

            _eyebrowLabel.Bounds = new Rectangle(left, top, width, ScaleValue(22));
            _titleLabel.Bounds = new Rectangle(left, _eyebrowLabel.Bottom + ScaleValue(8), width, ScaleValue(34));
            _statusLabel.Bounds = new Rectangle(left, _titleLabel.Bottom + ScaleValue(8), width, ScaleValue(28));
            _progressBar.Bounds = new Rectangle(left, _statusLabel.Bottom + ScaleValue(16), width, ScaleValue(8));
        }

        private void ApplyRoundedRegion()
        {
            try
            {
                using GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), ScaleValue(18));
                Region = new Region(path);
            }
            catch
            {
            }
        }

        private void ForceCenterTopMost()
        {
            if (IsDisposed)
                return;

            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (area.Width <= 0 || area.Height <= 0)
                area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);

            Size size = Size;
            var centered = new Point(
                area.Left + Math.Max(0, (area.Width - size.Width) / 2),
                area.Top + Math.Max(0, (area.Height - size.Height) / 2));

            TopMost = true;
            WindowState = FormWindowState.Normal;
            Location = centered;
            if (IsHandleCreated)
                SetWindowPos(Handle, HWND_TOPMOST, centered.X, centered.Y, size.Width, size.Height, SWP_SHOWWINDOW);
            BringToFront();
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            int diameter = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private float ComputeUiScale()
        {
            // PerMonitorV2-aware: DeviceDpi is the true DPI of the monitor this
            // form is on (96 = 100% .. 192 = 200%). No artificial ceiling, so the
            // splash scales correctly at 175%/200% instead of being capped.
            int dpi = 96;
            try { dpi = Math.Max(96, DeviceDpi); } catch { }
            return dpi / 96f;
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Dragging the splash between mixed-DPI monitors: recompute the scale
            // and re-apply the manual layout / rounded region / paint at the new DPI.
            _uiScale = ComputeUiScale();
            ApplyRoundedRegion();
            ApplyLayout();
            Invalidate();
        }

        private int ScaleValue(int value) => (int)Math.Round(value * _uiScale);

        private Size ScaleSize(Size size) => new Size(ScaleValue(size.Width), ScaleValue(size.Height));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _centerTimer.Stop();
                _centerTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);
    }
}
