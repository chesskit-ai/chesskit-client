using System.Drawing.Drawing2D;

namespace ChessKit
{
    internal enum UpdateDialogChoice
    {
        Later,
        Ignore,
        Download
    }

    internal sealed class UpdateAvailableDialogForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _bodyLabel;
        private readonly Label _notesLabel;
        private readonly Button _downloadButton;
        private readonly Button _laterButton;
        private readonly Button _ignoreButton;
        private readonly Button _closeButton;
        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(72, 150, 255);
        private float _uiScale;

        public UpdateDialogChoice Choice { get; private set; } = UpdateDialogChoice.Later;

        public UpdateAvailableDialogForm(UpdateCheckResult update)
        {
            // Derive the UI scale from the TRUE per-monitor DPI (PerMonitorV2),
            // with no artificial ceiling so the dialog stays crisp and correctly
            // proportioned past 200%. At 96 DPI this is exactly 1.0 (identity).
            _uiScale = Dpi.Factor(this);

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            UpdateStyles();

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _surfaceColor;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            ClientSize = ScaleSize(new Size(560, 292));
            MinimumSize = ScaleSize(new Size(520, 276));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            Text = "Chess Kit update";
            KeyPreview = true;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(155, 178, 255),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = update.IsRequired ? "UPDATE REQUIRED" : "UPDATE AVAILABLE",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                Text = $"Chess Kit {update.LatestVersion}",
                TextAlign = ContentAlignment.MiddleLeft
            };

            string requiredText = update.IsRequired
                ? "This update is required for the current release channel."
                : "You can install it now, leave it for later, or ignore this version.";
            _bodyLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(194, 200, 218),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                Text = $"Installed version: {update.CurrentVersion}\nLatest version: {update.LatestVersion}\n\n{requiredText}",
                TextAlign = ContentAlignment.TopLeft
            };

            _notesLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(218, 223, 238),
                BackColor = Color.FromArgb(16, 18, 24),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Text = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "No release notes were provided." : update.ReleaseNotes,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(ScaleValue(12), ScaleValue(10), ScaleValue(12), ScaleValue(10))
            };

            _downloadButton = CreateActionButton("Download", _accentColor, () => CloseWith(UpdateDialogChoice.Download));
            _laterButton = CreateGhostButton("Later", () => CloseWith(UpdateDialogChoice.Later));
            _ignoreButton = CreateGhostButton("Ignore version", () => CloseWith(UpdateDialogChoice.Ignore));
            _ignoreButton.Visible = !update.IsRequired;
            _closeButton = CreateCloseButton();

            Controls.Add(_eyebrowLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_bodyLabel);
            Controls.Add(_notesLabel);
            Controls.Add(_downloadButton);
            Controls.Add(_laterButton);
            Controls.Add(_ignoreButton);
            Controls.Add(_closeButton);

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) =>
            {
                ApplyRoundedRegion();
                ApplyLayout();
            };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
                    CloseWith(UpdateDialogChoice.Later);
            };

            ApplyRoundedRegion();
            ApplyLayout();
        }

        public static UpdateDialogChoice ShowUpdate(IWin32Window? owner, UpdateCheckResult update)
        {
            using var dialog = new UpdateAvailableDialogForm(update);
            AppIcon.ApplyTo(dialog);
            if (owner == null)
                dialog.ShowDialog();
            else
                dialog.ShowDialog(owner);
            return dialog.Choice;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Once the handle exists DeviceDpi reports the true monitor DPI, which
            // can differ from the construction-time estimate. Re-derive the scale and
            // re-lay-out so the dialog is correctly sized on its actual monitor.
            float scale = Dpi.Factor(this);
            if (Math.Abs(scale - _uiScale) > 0.001f)
            {
                _uiScale = scale;
                RebuildScaledLayout();
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Window dragged to a monitor at a different DPI: recompute the scale,
            // re-apply the rounded region and the hand-laid-out bounds, repaint.
            _uiScale = Dpi.Factor(this);
            RebuildScaledLayout();
        }

        private void RebuildScaledLayout()
        {
            ClientSize = ScaleSize(new Size(560, 292));
            MinimumSize = ScaleSize(new Size(520, 276));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            _closeButton.Size = ScaleSize(new Size(32, 32));
            _notesLabel.Padding = new Padding(ScaleValue(12), ScaleValue(10), ScaleValue(12), ScaleValue(10));
            ApplyRoundedRegion();
            ApplyLayout();
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

            Rectangle accentRect = new Rectangle(Padding.Left, Padding.Top - ScaleValue(5), ScaleValue(132), ScaleValue(4));
            using GraphicsPath accentPath = CreateRoundedRectanglePath(accentRect, ScaleValue(2));
            e.Graphics.FillPath(accentBrush, accentPath);

            using GraphicsPath notesPath = CreateRoundedRectanglePath(_notesLabel.Bounds, ScaleValue(8));
            using Pen notesPen = new Pen(Color.FromArgb(54, 62, 80), 1f);
            e.Graphics.DrawPath(notesPen, notesPath);
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
            int closeGap = ScaleValue(12);
            int titleWidth = Math.Max(ScaleValue(260), contentWidth - _closeButton.Width - closeGap);
            int eyebrowHeight = ScaleValue(20);
            int titleHeight = ScaleValue(42);
            int bodyHeight = ScaleValue(86);
            int notesHeight = ScaleValue(74);
            int buttonHeight = ScaleValue(38);
            int buttonGap = ScaleValue(12);
            int downloadWidth = ScaleValue(122);
            int laterWidth = ScaleValue(102);
            int ignoreWidth = ScaleValue(132);

            int requiredHeight = contentTop + eyebrowHeight + ScaleValue(8) + titleHeight + ScaleValue(8) + bodyHeight + ScaleValue(12) + notesHeight + ScaleValue(14) + buttonHeight + Padding.Bottom;
            if (ClientSize.Height < requiredHeight)
            {
                ClientSize = new Size(ClientSize.Width, requiredHeight);
                return;
            }

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - ScaleValue(2));
            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, titleWidth, eyebrowHeight);
            _titleLabel.Bounds = new Rectangle(contentLeft, _eyebrowLabel.Bottom + ScaleValue(8), titleWidth, titleHeight);
            _bodyLabel.Bounds = new Rectangle(contentLeft, _titleLabel.Bottom + ScaleValue(8), contentWidth, bodyHeight);
            _notesLabel.Bounds = new Rectangle(contentLeft, _bodyLabel.Bottom + ScaleValue(12), contentWidth, notesHeight);

            int buttonTop = ClientSize.Height - Padding.Bottom - buttonHeight;
            _downloadButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - downloadWidth, buttonTop, downloadWidth, buttonHeight);
            _laterButton.Bounds = new Rectangle(_downloadButton.Left - buttonGap - laterWidth, buttonTop, laterWidth, buttonHeight);
            _ignoreButton.Bounds = new Rectangle(contentLeft, buttonTop, ignoreWidth, buttonHeight);
        }

        private void CloseWith(UpdateDialogChoice choice)
        {
            Choice = choice;
            DialogResult = DialogResult.OK;
            Close();
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
            button.Click += (_, _) => CloseWith(UpdateDialogChoice.Later);
            return button;
        }

        private void ApplyRoundedRegion()
        {
            using GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), ScaleValue(18));
            Region = new Region(path);
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

        private int ScaleValue(int value) => Math.Max(1, (int)Math.Round(value * _uiScale));
        private Size ScaleSize(Size size) => new Size(ScaleValue(size.Width), ScaleValue(size.Height));
    }
}
