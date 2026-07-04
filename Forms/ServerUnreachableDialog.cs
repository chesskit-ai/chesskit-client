using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ChessKit
{
    /// <summary>
    /// The user's choice when the ChessKit servers can't be reached during the
    /// startup license check.
    /// </summary>
    internal enum LicenseUnreachableChoice
    {
        Retry,
        ContinueFree,
        Exit
    }

    /// <summary>
    /// A professional, DPI-aware modal shown ONLY when the license/vision servers
    /// are unreachable (a connection failure), never when the server actually
    /// responds "not licensed". It exists so a paying user is never silently and
    /// scarily dropped to Free over a transient outage: a network failure is not a
    /// license revocation. Offers Try again / Continue in Free (offline) / Exit.
    ///
    /// Visual language deliberately mirrors <see cref="NoticeDialogForm"/> so the
    /// app's dialogs stay consistent; kept a separate form because the notice is
    /// sealed with a fixed OK/Purchase layout and auto-confirms on Escape, which is
    /// the wrong behaviour for a three-way choice.
    /// </summary>
    internal sealed class ServerUnreachableDialog : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _bodyLabel;
        private readonly Label _hardwareIdLabel;
        private readonly Button _retryButton;
        private readonly Button _freeButton;
        private readonly Button _exitButton;
        private readonly Func<string, bool>? _copyAction;
        private readonly string _hardwareId;
        private readonly bool _hasHardwareId;

        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(244, 166, 78); // warning amber
        private readonly Color _hardwarePanelColor = Color.FromArgb(16, 18, 24);
        private readonly int _cornerRadius = 18;
        private float _uiScale;

        public LicenseUnreachableChoice Choice { get; private set; } = LicenseUnreachableChoice.Retry;

        private ServerUnreachableDialog(string hardwareId, string detail, Func<string, bool>? copyAction)
        {
            _hardwareId = hardwareId ?? "";
            _hasHardwareId = !string.IsNullOrWhiteSpace(_hardwareId);
            _copyAction = copyAction;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            _uiScale = Dpi.Factor(this);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;   // this is a top-level, app-blocking decision; let it appear in the taskbar
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _surfaceColor;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            ClientSize = ScaleSize(new Size(540, 320));
            MinimumSize = ScaleSize(new Size(500, 300));
            DoubleBuffered = true;
            Padding = new Padding(ScaleValue(24), ScaleValue(22), ScaleValue(24), ScaleValue(20));
            Text = "ChessKit - Connection problem";
            KeyPreview = true;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(244, 196, 140),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = "CONNECTION",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                Text = "Can't reach the ChessKit servers",
                TextAlign = ContentAlignment.MiddleLeft
            };

            string body =
                "ChessKit couldn't reach its servers to verify your license right now. " +
                "This is a connection problem, not a change to your license - if you own a " +
                "license, it is safe and will reconnect automatically once the servers are " +
                "reachable again.\n\n" +
                "You can try again, continue in Free mode (the offline analysis board still " +
                "works), or exit.";
            if (!string.IsNullOrWhiteSpace(detail))
                body += "\n\nDetails: " + detail.Trim();

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
                ForeColor = Color.FromArgb(210, 216, 232),
                BackColor = _hardwarePanelColor,
                Font = new Font("Cascadia Mono", 10.5f, FontStyle.Bold),
                Text = _hasHardwareId ? ("ID  " + _hardwareId + "   (click to copy)") : "",
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Visible = _hasHardwareId
            };
            _hardwareIdLabel.Click += (_, _) => CopyHardwareId();

            _retryButton = CreateActionButton("Try again", _accentColor, () => Finish(LicenseUnreachableChoice.Retry));
            _freeButton = CreateGhostButton("Continue in Free mode", () => Finish(LicenseUnreachableChoice.ContinueFree));
            _exitButton = CreateGhostButton("Exit", () => Finish(LicenseUnreachableChoice.Exit));

            Controls.Add(_eyebrowLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_bodyLabel);
            Controls.Add(_hardwareIdLabel);
            Controls.Add(_retryButton);
            Controls.Add(_freeButton);
            Controls.Add(_exitButton);

            AcceptButton = _retryButton; // Enter = the safe, non-destructive default (re-check)

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) => { ApplyRoundedRegion(); ApplyLayout(); };
            KeyDown += (_, e) =>
            {
                // Escape is the safe default: re-check, never silently pick Free or Exit.
                if (e.KeyCode == Keys.Escape)
                {
                    e.Handled = true;
                    Finish(LicenseUnreachableChoice.Retry);
                }
            };

            ApplyRoundedRegion();
            ApplyLayout();
            ForceCenterTopMost();
        }

        /// <summary>
        /// Shows the dialog modally and returns the user's choice. Must be called on
        /// a UI thread with a message pump (marshal via the overlay's Invoke).
        /// </summary>
        public static LicenseUnreachableChoice Show(string hardwareId, string detail, Func<string, bool>? copyAction)
        {
            using var dialog = new ServerUnreachableDialog(hardwareId, detail, copyAction);
            AppIcon.ApplyTo(dialog);
            dialog.Shown += (_, _) =>
            {
                dialog.ForceCenterTopMost();
                try { dialog.BeginInvoke(new Action(dialog.ForceCenterTopMost)); } catch { }
            };

            // Own the dialog with a hidden top-most window so it can never end up
            // behind the transparent overlay / splash.
            using var owner = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1),
                Opacity = 0,
                TopMost = true
            };
            owner.Show();
            owner.Hide();
            dialog.ShowDialog(owner);
            return dialog.Choice;
        }

        private void Finish(LicenseUnreachableChoice choice)
        {
            Choice = choice;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // Control.DeviceDpi is only reliable once the handle exists. The
            // constructor samples it before the handle is created (returning the
            // system/primary DPI), so on a mixed-DPI multi-monitor setup where this
            // dialog opens on a non-primary monitor it would otherwise mis-scale.
            // Re-seed the factor and re-apply the DPI-scaled size + layout here,
            // before the first paint, so it is always correct.
            _uiScale = Dpi.Factor(this);
            MinimumSize = ScaleSize(new Size(500, 300));
            ClientSize = ScaleSize(new Size(540, 320));
            Padding = new Padding(ScaleValue(24), ScaleValue(22), ScaleValue(24), ScaleValue(20));
            ApplyRoundedRegion();
            ApplyLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ForceCenterTopMost();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _uiScale = Dpi.Factor(this);
            MinimumSize = ScaleSize(new Size(500, 300));
            ClientSize = ScaleSize(new Size(540, 320));
            Padding = new Padding(ScaleValue(24), ScaleValue(22), ScaleValue(24), ScaleValue(20));
            ApplyRoundedRegion();
            ApplyLayout();
            Invalidate();
        }

        private void CopyHardwareId()
        {
            if (!_hasHardwareId || _copyAction == null)
                return;
            bool copied = _copyAction(_hardwareId);
            _hardwareIdLabel.Text = copied
                ? ("ID  " + _hardwareId + "   (copied)")
                : ("ID  " + _hardwareId + "   (clipboard busy)");
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
            Activate();
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
                Rectangle hwBounds = _hardwareIdLabel.Bounds;
                using GraphicsPath hwPath = CreateRoundedRectanglePath(hwBounds, ScaleValue(8));
                using SolidBrush hwBrush = new SolidBrush(_hardwarePanelColor);
                using Pen hwPen = new Pen(Color.FromArgb(54, 62, 80), 1f);
                e.Graphics.FillPath(hwBrush, hwPath);
                e.Graphics.DrawPath(hwPen, hwPath);
            }

            Rectangle innerGlow = Rectangle.Inflate(bounds, -2, -2);
            using GraphicsPath glowPath = CreateRoundedRectanglePath(innerGlow, ScaleValue(Math.Max(8, _cornerRadius - 2)));
            e.Graphics.DrawPath(glowPen, glowPath);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_surfaceColor);
        }

        private void ApplyLayout()
        {
            int contentLeft = Padding.Left;
            int contentTop = Padding.Top;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;

            int eyebrowHeight = ScaleValue(20);
            int titleGap = ScaleValue(8);

            Size titleMeasured = TextRenderer.MeasureText(
                _titleLabel.Text, _titleLabel.Font, new Size(contentWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int titleHeight = Math.Max(ScaleValue(30), titleMeasured.Height + ScaleValue(4));

            Size bodyMeasured = TextRenderer.MeasureText(
                _bodyLabel.Text, _bodyLabel.Font, new Size(contentWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int bodyHeight = Math.Max(ScaleValue(96), bodyMeasured.Height + ScaleValue(6));

            int sectionGap = ScaleValue(14);
            int hardwareHeight = _hasHardwareId ? ScaleValue(40) : 0;
            int buttonGap = ScaleValue(16);
            int buttonHeight = ScaleValue(40);

            int hardwareBlock = _hasHardwareId ? sectionGap + hardwareHeight : 0;

            int requiredHeight =
                contentTop + eyebrowHeight + titleGap + titleHeight + ScaleValue(8) + bodyHeight +
                hardwareBlock + buttonGap + buttonHeight + Padding.Bottom;

            if (ClientSize.Height < requiredHeight)
            {
                ClientSize = new Size(ClientSize.Width, requiredHeight);
                return;
            }

            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, contentWidth, eyebrowHeight);
            _titleLabel.Bounds = new Rectangle(contentLeft, _eyebrowLabel.Bottom + titleGap, contentWidth, titleHeight);
            _bodyLabel.Bounds = new Rectangle(contentLeft, _titleLabel.Bottom + ScaleValue(8), contentWidth, bodyHeight);
            if (_hasHardwareId)
                _hardwareIdLabel.Bounds = new Rectangle(contentLeft, _bodyLabel.Bottom + sectionGap, contentWidth, hardwareHeight);

            int buttonTop = ClientSize.Height - Padding.Bottom - buttonHeight;
            int exitWidth = ScaleValue(96);
            int freeWidth = ScaleValue(196);
            int retryWidth = ScaleValue(132);
            int gap = ScaleValue(12);

            // Exit on the left; Continue-in-Free and Try-again grouped on the right
            // (Try-again is the accented primary, furthest right).
            _exitButton.Bounds = new Rectangle(contentLeft, buttonTop, exitWidth, buttonHeight);
            _retryButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - retryWidth, buttonTop, retryWidth, buttonHeight);
            _freeButton.Bounds = new Rectangle(_retryButton.Left - gap - freeWidth, buttonTop, freeWidth, buttonHeight);
        }

        private void ApplyRoundedRegion()
        {
            using GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), ScaleValue(_cornerRadius));
            Region = new Region(path);
        }

        private Button CreateActionButton(string text, Color backColor, Action onClick)
        {
            Button button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.FromArgb(26, 20, 10),
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
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
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

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Max(2, radius * 2);
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

        private int ScaleValue(int value) => Math.Max(1, (int)Math.Round(value * _uiScale));
        private Size ScaleSize(Size size) => new Size(ScaleValue(size.Width), ScaleValue(size.Height));

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
