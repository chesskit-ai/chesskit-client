using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    public enum NoticeDialogKind
    {
        Info,
        Warning,
        Error
    }

    public sealed class NoticeDialogForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _bodyLabel;
        private readonly Label _hardwareIdLabel;
        private readonly Label _statusLabel;
        private readonly Button _copyButton;
        private readonly Button _purchaseButton;
        private readonly Button _okButton;
        private readonly Button _closeButton;
        private readonly Func<string, bool>? _copyAction;
        private readonly string _hardwareId;
        private readonly bool _hasHardwareId;
        private readonly string _purchaseUrl;
        private bool _hardwareIdRevealLocked;
        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor;
        private readonly Color _hardwarePanelColor = Color.FromArgb(16, 18, 24);
        private readonly int _cornerRadius = 18;
        private float _uiScale;
        private readonly WinFormsTimer _centerTimer;
        private int _centerPassesRemaining = 6;

        public NoticeDialogForm(
            string eyebrow,
            string title,
            string body,
            string hardwareId,
            NoticeDialogKind kind,
            bool copiedToClipboard,
            Func<string, bool>? copyAction,
            string purchaseUrl = "")
        {
            _hardwareId = hardwareId;
            _hasHardwareId = !string.IsNullOrWhiteSpace(hardwareId);
            _copyAction = copyAction;
            _purchaseUrl = purchaseUrl;
            _accentColor = kind switch
            {
                NoticeDialogKind.Error => Color.FromArgb(232, 92, 92),
                NoticeDialogKind.Warning => Color.FromArgb(244, 166, 78),
                _ => Color.FromArgb(72, 150, 255)
            };

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            // Derive the UI scale from the monitor's TRUE per-monitor DPI
            // (the app runs PerMonitorV2-aware, so DeviceDpi is correct). No
            // artificial ceiling: the dialog must look right up to 200%+.
            _uiScale = Dpi.Factor(this);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _surfaceColor;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            ClientSize = ScaleSize(new Size(520, 308));
            MinimumSize = ScaleSize(new Size(480, 292));
            DoubleBuffered = true;
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            Text = title;
            Opacity = 0.99;
            KeyPreview = true;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(155, 178, 255),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = eyebrow.ToUpperInvariant(),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _bodyLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(194, 200, 218),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                Text = body,
                TextAlign = ContentAlignment.TopLeft
            };

            _hardwareIdLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(238, 242, 250),
                BackColor = _hardwarePanelColor,
                Font = new Font("Cascadia Mono", 12f, FontStyle.Bold),
                Text = MaskHardwareId(hardwareId),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };

            _statusLabel = new Label
            {
                AutoSize = false,
                ForeColor = copiedToClipboard ? Color.FromArgb(132, 218, 166) : Color.FromArgb(220, 190, 125),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Text = copiedToClipboard ? "Copied to clipboard" : "Clipboard was busy. Use Copy HWID below.",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _copyButton = CreateGhostButton("Copy HWID", CopyHardwareId);
            _copyButton.Visible = _hasHardwareId;
            _purchaseButton = CreateActionButton(_hasHardwareId ? "Purchase" : "Upgrade", _accentColor, OpenPurchasePage);
            _purchaseButton.Visible = !string.IsNullOrWhiteSpace(_purchaseUrl);
            _okButton = CreateGhostButton("OK", () =>
            {
                DialogResult = DialogResult.OK;
                Close();
            });
            _closeButton = CreateCloseButton();
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
            Controls.Add(_bodyLabel);
            Controls.Add(_hardwareIdLabel);
            Controls.Add(_statusLabel);
            Controls.Add(_copyButton);
            Controls.Add(_purchaseButton);
            Controls.Add(_okButton);
            Controls.Add(_closeButton);

            // Upsell / generic-notice mode: with no hardware ID there's nothing to
            // copy, so hide the HWID panel, its status line and the Copy button.
            // ApplyLayout collapses the freed vertical space.
            _hardwareIdLabel.Visible = _hasHardwareId;
            _statusLabel.Visible = _hasHardwareId;

            _hardwareIdLabel.MouseEnter += (_, _) => SetHardwareIdRevealed(true);
            _hardwareIdLabel.MouseLeave += (_, _) =>
            {
                if (!_hardwareIdRevealLocked)
                    SetHardwareIdRevealed(false);
            };
            _hardwareIdLabel.Click += (_, _) =>
            {
                _hardwareIdRevealLocked = !_hardwareIdRevealLocked;
                SetHardwareIdRevealed(_hardwareIdRevealLocked);
            };

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) =>
            {
                ApplyRoundedRegion();
                ApplyLayout();
            };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            ApplyRoundedRegion();
            ApplyLayout();
            ForceCenterTopMost();
            if (!string.IsNullOrWhiteSpace(_hardwareId) && !copiedToClipboard)
                _statusLabel.Text = "HWID hidden. Hover or click the HWID field to reveal it.";
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ForceCenterTopMost();
            _centerPassesRemaining = 6;
            _centerTimer.Start();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            // The dialog is borderless with AutoScaleMode.None and lays itself
            // out from _uiScale, so when it is dragged onto a monitor with a
            // different DPI we must recompute the scale, resize the surface,
            // re-clip the rounded region, re-run the manual layout and repaint.
            _uiScale = Dpi.Factor(this);
            MinimumSize = ScaleSize(new Size(480, 292));
            ClientSize = ScaleSize(new Size(520, 308));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            _closeButton.Size = ScaleSize(new Size(32, 32));
            ApplyRoundedRegion();
            ApplyLayout();
            Invalidate();
        }

        public static void ShowNotice(
            IWin32Window? owner,
            string eyebrow,
            string title,
            string body,
            string hardwareId,
            NoticeDialogKind kind,
            bool copiedToClipboard,
            Func<string, bool>? copyAction,
            string purchaseUrl = "")
        {
            using var dialog = new NoticeDialogForm(eyebrow, title, body, hardwareId, kind, copiedToClipboard, copyAction, purchaseUrl);
            AppIcon.ApplyTo(dialog);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.TopMost = true;
            dialog.Shown += (_, _) =>
            {
                dialog.ForceCenterTopMost();
                dialog.BeginInvoke(new Action(dialog.ForceCenterTopMost));
            };

            if (owner != null)
            {
                dialog.ShowDialog(owner);
                return;
            }

            using var topMostOwner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            topMostOwner.Show();
            topMostOwner.Hide();
            dialog.ShowDialog(topMostOwner);
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
            {
                SetWindowPos(Handle, HWND_TOPMOST, centered.X, centered.Y, size.Width, size.Height, SWP_SHOWWINDOW);
            }
            BringToFront();
            Activate();
        }

        public void BeginCopyHardwareIdAsync()
        {
            if (string.IsNullOrWhiteSpace(_hardwareId) || _copyAction == null)
                return;

            _statusLabel.ForeColor = Color.FromArgb(150, 160, 184);
            _statusLabel.Text = "Copying HWID...";

            _ = Task.Run(() =>
            {
                bool copied = _copyAction(_hardwareId);
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _statusLabel.ForeColor = copied
                                ? Color.FromArgb(132, 218, 166)
                                : Color.FromArgb(220, 190, 125);
                            _statusLabel.Text = copied
                                ? "Copied to clipboard"
                                : "Clipboard was busy. Use Copy HWID below.";
                        }));
                    }
                }
                catch
                {
                }
            });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using GraphicsPath path = CreateRoundedRectanglePath(bounds, ScaleValue(_cornerRadius));
            using SolidBrush backgroundBrush = new SolidBrush(_surfaceColor);
            using Pen edgePen = new Pen(_surfaceEdgeColor, 1.2f);
            using SolidBrush accentBrush = new SolidBrush(_accentColor);
            using Pen glowPen = new Pen(Color.FromArgb(42, _accentColor.R, _accentColor.G, _accentColor.B), 1f);

            e.Graphics.FillPath(backgroundBrush, path);
            e.Graphics.DrawPath(edgePen, path);

            Rectangle accentRect = new Rectangle(Padding.Left, Padding.Top - ScaleValue(5), ScaleValue(132), ScaleValue(4));
            using GraphicsPath accentPath = CreateRoundedRectanglePath(accentRect, ScaleValue(2));
            e.Graphics.FillPath(accentBrush, accentPath);

            if (_hasHardwareId)
            {
                Rectangle hardwareBounds = _hardwareIdLabel.Bounds;
                using GraphicsPath hardwarePath = CreateRoundedRectanglePath(hardwareBounds, ScaleValue(8));
                using SolidBrush hardwareBrush = new SolidBrush(_hardwarePanelColor);
                using Pen hardwarePen = new Pen(Color.FromArgb(54, 62, 80), 1f);
                e.Graphics.FillPath(hardwareBrush, hardwarePath);
                e.Graphics.DrawPath(hardwarePen, hardwarePath);
            }

            Rectangle innerGlow = Rectangle.Inflate(bounds, -2, -2);
            using GraphicsPath glowPath = CreateRoundedRectanglePath(innerGlow, ScaleValue(Math.Max(8, _cornerRadius - 2)));
            e.Graphics.DrawPath(glowPen, glowPath);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_surfaceColor);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _centerTimer.Stop();
                _centerTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ApplyLayout()
        {
            int contentLeft = Padding.Left;
            int contentTop = Padding.Top;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;
            int closeGap = ScaleValue(12);
            int eyebrowHeight = ScaleValue(20);
            int titleGap = ScaleValue(8);
            int titleWidth = Math.Max(ScaleValue(240), contentWidth - _closeButton.Width - closeGap);
            int bodyWidth = Math.Max(ScaleValue(280), contentWidth);

            Size titleMeasured = TextRenderer.MeasureText(
                _titleLabel.Text,
                _titleLabel.Font,
                new Size(titleWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int titleHeight = Math.Max(ScaleValue(42), titleMeasured.Height + ScaleValue(4));

            Size bodyMeasured = TextRenderer.MeasureText(
                _bodyLabel.Text,
                _bodyLabel.Font,
                new Size(bodyWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int bodyHeight = Math.Max(ScaleValue(52), bodyMeasured.Height + ScaleValue(6));

            // The HWID panel + its status line collapse entirely in upsell mode.
            int hardwareHeight = _hasHardwareId ? ScaleValue(48) : 0;
            int statusHeight = _hasHardwareId ? ScaleValue(24) : 0;
            int sectionGap = ScaleValue(12);
            int buttonGap = ScaleValue(14);
            int buttonHeight = ScaleValue(38);
            int copyButtonWidth = ScaleValue(132);
            int purchaseButtonWidth = ScaleValue(122);
            int okButtonWidth = ScaleValue(118);

            int hardwareBlock = _hasHardwareId
                ? sectionGap + hardwareHeight + ScaleValue(8) + statusHeight
                : sectionGap;

            int requiredHeight =
                contentTop +
                eyebrowHeight +
                titleGap +
                titleHeight +
                ScaleValue(8) +
                bodyHeight +
                hardwareBlock +
                buttonGap +
                buttonHeight +
                Padding.Bottom;

            if (ClientSize.Height < requiredHeight)
            {
                ClientSize = new Size(ClientSize.Width, requiredHeight);
                return;
            }

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - ScaleValue(2));
            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, titleWidth, eyebrowHeight);
            _titleLabel.Bounds = new Rectangle(contentLeft, _eyebrowLabel.Bottom + titleGap, titleWidth, titleHeight);
            _bodyLabel.Bounds = new Rectangle(contentLeft, _titleLabel.Bottom + ScaleValue(8), bodyWidth, bodyHeight);
            _hardwareIdLabel.Bounds = new Rectangle(contentLeft, _bodyLabel.Bottom + sectionGap, contentWidth, hardwareHeight);
            _statusLabel.Bounds = new Rectangle(contentLeft, _hardwareIdLabel.Bottom + ScaleValue(8), contentWidth, statusHeight);

            int buttonTop = ClientSize.Height - Padding.Bottom - buttonHeight;
            _copyButton.Bounds = new Rectangle(contentLeft, buttonTop, copyButtonWidth, buttonHeight);
            _purchaseButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - okButtonWidth - ScaleValue(12) - purchaseButtonWidth, buttonTop, purchaseButtonWidth, buttonHeight);
            _okButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - okButtonWidth, buttonTop, okButtonWidth, buttonHeight);
        }

        private void ApplyRoundedRegion()
        {
            using GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), ScaleValue(_cornerRadius));
            Region = new Region(path);
        }

        private void CopyHardwareId()
        {
            bool copied = _copyAction?.Invoke(_hardwareId) ?? false;
            _statusLabel.Text = copied ? "Copied to clipboard" : "Clipboard is busy. Try again.";
            _statusLabel.ForeColor = copied ? Color.FromArgb(132, 218, 166) : Color.FromArgb(232, 130, 130);
        }

        private void SetHardwareIdRevealed(bool revealed)
        {
            _hardwareIdLabel.Text = revealed
                ? (string.IsNullOrWhiteSpace(_hardwareId) ? "Unavailable" : _hardwareId)
                : MaskHardwareId(_hardwareId);

            if (!string.IsNullOrWhiteSpace(_hardwareId))
            {
                _statusLabel.Text = revealed
                    ? (_hardwareIdRevealLocked ? "HWID visible. Click the field again to hide it." : "HWID visible while hovered.")
                    : "HWID hidden. Hover or click the HWID field to reveal it.";
                _statusLabel.ForeColor = Color.FromArgb(150, 160, 184);
            }
        }

        private static string MaskHardwareId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Unavailable";

            string trimmed = value.Trim();
            if (trimmed.Length <= 4)
                return new string('*', trimmed.Length);

            return new string('*', Math.Max(0, trimmed.Length - 4)) + trimmed[^4..];
        }

        private void OpenPurchasePage()
        {
            if (string.IsNullOrWhiteSpace(_purchaseUrl))
                return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _purchaseUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                _statusLabel.Text = "Could not open purchase page.";
                _statusLabel.ForeColor = Color.FromArgb(232, 130, 130);
            }
        }

        private Button CreateActionButton(string text, Color backColor, Action onClick)
        {
            Button button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                TabStop = true,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.08f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.08f);
            button.Click += (_, _) => onClick();
            return button;
        }

        private Button CreateGhostButton(string text, Action onClick)
        {
            Button button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 38, 49),
                ForeColor = Color.FromArgb(218, 223, 238),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                TabStop = true,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(74, 82, 102);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 47, 61);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 36, 46);
            button.Click += (_, _) => onClick();
            return button;
        }

        private Button CreateCloseButton()
        {
            Button button = new Button
            {
                Text = "x",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(34, 38, 49),
                ForeColor = Color.FromArgb(220, 226, 240),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Size = ScaleSize(new Size(32, 32)),
                TabStop = false,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 64, 64);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(150, 48, 48);
            button.Click += (_, _) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            return button;
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            Rectangle arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private int ScaleValue(int value)
        {
            return Math.Max(1, (int)Math.Round(value * _uiScale));
        }

        private Size ScaleSize(Size size)
        {
            return new Size(ScaleValue(size.Width), ScaleValue(size.Height));
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
