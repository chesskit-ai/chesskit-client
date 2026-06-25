using System.Drawing.Drawing2D;

namespace ChessKit
{
    public sealed class OrientationPromptForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _bodyLabel;
        private readonly Button _upButton;
        private readonly Button _downButton;
        private readonly Button _dismissButton;
        private readonly Button _closeButton;

        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(72, 150, 255);
        private readonly int _cornerRadius = 18;

        public event Action<bool>? DirectionChosen;
        public event Action? Dismissed;

        public OrientationPromptForm()
        {
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
            ClientSize = Dpi.Scale(this, new Size(428, 178));
            MinimumSize = Dpi.Scale(this, new Size(390, 170));
            DoubleBuffered = true;
            Padding = Dpi.Scale(this, new Padding(18, 16, 18, 18));
            Text = "Board Orientation";
            Opacity = 1.0;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(140, 170, 255),
                BackColor = _surfaceColor,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = "ORIENTATION CHECK",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = _surfaceColor,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _bodyLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(190, 196, 214),
                BackColor = _surfaceColor,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                TextAlign = ContentAlignment.TopLeft
            };

            _upButton = CreateActionButton("Move Up", _accentColor, () => ChooseDirection(true));
            _downButton = CreateActionButton("Move Down", Color.FromArgb(68, 78, 108), () => ChooseDirection(false));
            _dismissButton = CreateGhostButton("Not now", () => Dismissed?.Invoke());
            _closeButton = CreateCloseButton();

            Controls.Add(_eyebrowLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_bodyLabel);
            Controls.Add(_upButton);
            Controls.Add(_downButton);
            Controls.Add(_dismissButton);
            Controls.Add(_closeButton);

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) =>
            {
                ApplyRoundedRegion();
                ApplyLayout();
            };

            FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                    Dismissed?.Invoke();
                }
            };

            ApplyRoundedRegion();
            ApplyLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            // Owner-drawn + AutoScaleMode.None, so WinForms will not re-scale us
            // when the form is dragged onto a monitor with a different DPI. Rebuild
            // the DPI-derived layout/region and repaint at the new scale.
            ApplyRoundedRegion();
            ApplyLayout();
            Invalidate(true);
        }

        private void ChooseDirection(bool pawnsMoveUp)
        {
            Hide();
            DirectionChosen?.Invoke(pawnsMoveUp);
        }

        public void ShowPrompt(char referenceColor, Rectangle? anchorRect = null)
        {
            Enabled = true;
            UseWaitCursor = false;
            Cursor = Cursors.Default;
            string colorName = referenceColor == 'b' ? "black" : "white";
            _titleLabel.Text = $"Which way do {colorName} pawns move?";
            _bodyLabel.Text = $"We cannot tell the board orientation confidently from this position alone. Pick the direction once and we will remember it for this session.";

            Size preferred = GetPreferredPromptSize();
            ClientSize = preferred;

            Rectangle workArea = anchorRect.HasValue
                ? Screen.FromRectangle(anchorRect.Value).WorkingArea
                : (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080));

            SuspendLayout();
            ApplyRoundedRegion();
            ApplyLayout();

            Point location = ComputePromptLocation(anchorRect, workArea, preferred);
            Location = location;

            if (!Visible)
                Show();

            ResumeLayout(true);
            Invalidate(true);
            Refresh();
            TopMost = false;
            TopMost = true;
            ForceTopMost();
            BringToFront();
            Activate();
            SetForegroundWindow(Handle);
            _upButton.Focus();

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                TopMost = true;
                ForceTopMost();
                BringToFront();
                Activate();
                SetForegroundWindow(Handle);
            }));
        }

        private void ForceTopMost()
        {
            if (!IsHandleCreated)
                return;

            SetWindowPos(
                Handle,
                HWND_TOPMOST,
                Left,
                Top,
                Width,
                Height,
                SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using GraphicsPath path = CreateRoundedRectanglePath(bounds, Dpi.Scale(this, _cornerRadius));
            using SolidBrush backgroundBrush = new SolidBrush(_surfaceColor);
            using Pen edgePen = new Pen(_surfaceEdgeColor, Dpi.Scale(this, 1.2f));
            using SolidBrush accentBrush = new SolidBrush(_accentColor);
            using Pen glowPen = new Pen(Color.FromArgb(42, 112, 194), Dpi.Scale(this, 1f));

            e.Graphics.FillPath(backgroundBrush, path);
            e.Graphics.DrawPath(edgePen, path);

            Rectangle accentRect = new Rectangle(Padding.Left, Padding.Top - Dpi.Scale(this, 4), Dpi.Scale(this, 92), Dpi.Scale(this, 4));
            using GraphicsPath accentPath = CreateRoundedRectanglePath(accentRect, Dpi.Scale(this, 2));
            e.Graphics.FillPath(accentBrush, accentPath);

            Rectangle innerGlow = Rectangle.Inflate(bounds, -Dpi.Scale(this, 2), -Dpi.Scale(this, 2));
            using GraphicsPath glowPath = CreateRoundedRectanglePath(innerGlow, Math.Max(Dpi.Scale(this, 8), Dpi.Scale(this, _cornerRadius) - Dpi.Scale(this, 2)));
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
            int titleWidth = contentWidth - _closeButton.Width - Dpi.Scale(this, 10);

            Size titleMeasured = TextRenderer.MeasureText(
                _titleLabel.Text,
                _titleLabel.Font,
                new Size(titleWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            Size bodyMeasured = TextRenderer.MeasureText(
                _bodyLabel.Text,
                _bodyLabel.Font,
                new Size(contentWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - Dpi.Scale(this, 2));

            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, titleWidth, Dpi.Scale(this, 23));
            _titleLabel.Bounds = new Rectangle(contentLeft, contentTop + Dpi.Scale(this, 29), titleWidth, Math.Max(Dpi.Scale(this, 38), titleMeasured.Height));
            _bodyLabel.Bounds = new Rectangle(contentLeft, _titleLabel.Bottom + Dpi.Scale(this, 8), contentWidth, Math.Max(Dpi.Scale(this, 42), bodyMeasured.Height));

            int buttonTopGap = Dpi.Scale(this, 12);
            int buttonsTop = _bodyLabel.Bottom + buttonTopGap;
            int primaryWidth = Math.Max(Dpi.Scale(this, 112), (contentWidth - Dpi.Scale(this, 12)) / 2);

            _upButton.Bounds = new Rectangle(contentLeft, buttonsTop, primaryWidth, Dpi.Scale(this, 40));
            _downButton.Bounds = new Rectangle(_upButton.Right + Dpi.Scale(this, 12), buttonsTop, primaryWidth, Dpi.Scale(this, 40));
            _dismissButton.Bounds = new Rectangle(contentLeft, buttonsTop + Dpi.Scale(this, 48), contentWidth, Dpi.Scale(this, 34));
        }

        private Size GetPreferredPromptSize()
        {
            int maxWidth = Dpi.Scale(this, 470);
            int bodyWidth = maxWidth - Padding.Left - Padding.Right;
            int titleWidth = bodyWidth - Dpi.Scale(this, 28) - Dpi.Scale(this, 10);

            Size titleMeasured = TextRenderer.MeasureText(
                _titleLabel.Text,
                _titleLabel.Font,
                new Size(titleWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            Size bodyMeasured = TextRenderer.MeasureText(
                _bodyLabel.Text,
                _bodyLabel.Font,
                new Size(bodyWidth, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);

            int height =
                Padding.Top + Dpi.Scale(this, 23) +
                Dpi.Scale(this, 6) +
                Math.Max(Dpi.Scale(this, 38), titleMeasured.Height) +
                Dpi.Scale(this, 8) +
                Math.Max(Dpi.Scale(this, 42), bodyMeasured.Height) +
                Dpi.Scale(this, 12) +
                Dpi.Scale(this, 40) +
                Dpi.Scale(this, 8) +
                Dpi.Scale(this, 34) +
                Padding.Bottom;

            return new Size(maxWidth, Math.Max(Dpi.Scale(this, 188), height));
        }

        private Point ComputePromptLocation(Rectangle? anchorRect, Rectangle workArea, Size promptSize)
        {
            int edgeMargin = Dpi.Scale(this, 12);

            if (!anchorRect.HasValue)
            {
                int defaultCenteredX = workArea.Left + Math.Max(edgeMargin, (workArea.Width - promptSize.Width) / 2);
                return new Point(defaultCenteredX, workArea.Top + Dpi.Scale(this, 56));
            }

            Rectangle anchor = anchorRect.Value;
            int centeredX = anchor.Left + (anchor.Width - promptSize.Width) / 2;
            int x = Math.Max(workArea.Left + edgeMargin, Math.Min(centeredX, workArea.Right - promptSize.Width - edgeMargin));

            int centeredY = anchor.Top + (anchor.Height - promptSize.Height) / 2;
            int y = Math.Max(workArea.Top + edgeMargin, Math.Min(centeredY, workArea.Bottom - promptSize.Height - edgeMargin));

            return new Point(x, y);
        }

        private void ApplyRoundedRegion()
        {
            using GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(0, 0, Width, Height), Dpi.Scale(this, _cornerRadius));
            Region = new Region(path);
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
                ForeColor = Color.FromArgb(210, 214, 226),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                TabStop = true,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(74, 82, 102);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 47, 61);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 36, 46);
            button.Click += (_, _) =>
            {
                Hide();
                onClick();
            };
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
                Size = Dpi.Scale(this, new Size(28, 28)),
                TabStop = false,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 64, 64);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(150, 48, 48);
            button.Click += (_, _) =>
            {
                Hide();
                Dismissed?.Invoke();
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

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_SHOWWINDOW = 0x0040;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);
    }
}
