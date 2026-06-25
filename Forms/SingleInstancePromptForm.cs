using System.Drawing.Drawing2D;

namespace ChessKit
{
    public sealed class SingleInstancePromptForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _bodyLabel;
        private readonly Button _useExistingButton;
        private readonly Button _closeButton;
        private readonly Button _dismissButton;

        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(72, 150, 255);
        private readonly int _cornerRadius = 18;

        public SingleInstancePromptForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = _surfaceColor;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            ClientSize = Dpi.Scale(this, new Size(456, 192));
            MinimumSize = Dpi.Scale(this, new Size(420, 186));
            DoubleBuffered = true;
            Padding = Dpi.Scale(this, new Padding(18, 16, 18, 18));
            Text = "ChessKit";
            Opacity = 0.985;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(140, 170, 255),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = "INSTANCE ALREADY RUNNING",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "ChessKit is already open"
            };

            _bodyLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(190, 196, 214),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                TextAlign = ContentAlignment.TopLeft,
                Text = "To avoid crashes, duplicate overlays, and log conflicts, only one instance can run at a time."
            };

            _useExistingButton = CreateActionButton("Use running app", _accentColor, () =>
            {
                DialogResult = DialogResult.OK;
                Close();
            });

            _dismissButton = CreateGhostButton("Close this copy", () =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            });

            _closeButton = CreateCloseButton();

            Controls.Add(_eyebrowLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_bodyLabel);
            Controls.Add(_useExistingButton);
            Controls.Add(_dismissButton);
            Controls.Add(_closeButton);

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) =>
            {
                ApplyRoundedRegion();
                ApplyLayout();
            };

            ApplyRoundedRegion();
            ApplyLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            // AutoScaleMode.None: WinForms does not resize this owner-drawn form,
            // so re-derive every scaled metric from the new DeviceDpi, then
            // re-lay-out, re-round the region, and repaint.
            MinimumSize = Dpi.Scale(this, new Size(420, 186));
            ClientSize = Dpi.Scale(this, new Size(456, 192));
            Padding = Dpi.Scale(this, new Padding(18, 16, 18, 18));
            _closeButton.Size = Dpi.Scale(this, new Size(28, 28));

            ApplyRoundedRegion();
            ApplyLayout();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle bounds = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using GraphicsPath path = CreateRoundedRectanglePath(bounds, Dpi.Scale(this, _cornerRadius));
            using SolidBrush backgroundBrush = new SolidBrush(_surfaceColor);
            using Pen edgePen = new Pen(_surfaceEdgeColor, Dpi.Scale(this, 1.2f));
            using SolidBrush accentBrush = new SolidBrush(_accentColor);
            using Pen glowPen = new Pen(Color.FromArgb(42, 112, 194), Dpi.Scale(this, 1f));

            e.Graphics.FillPath(backgroundBrush, path);
            e.Graphics.DrawPath(edgePen, path);

            Rectangle accentRect = new Rectangle(Padding.Left, Padding.Top - Dpi.Scale(this, 4), Dpi.Scale(this, 122), Dpi.Scale(this, 4));
            using GraphicsPath accentPath = CreateRoundedRectanglePath(accentRect, Dpi.Scale(this, 2));
            e.Graphics.FillPath(accentBrush, accentPath);

            Rectangle innerGlow = Rectangle.Inflate(bounds, -Dpi.Scale(this, 2), -Dpi.Scale(this, 2));
            using GraphicsPath glowPath = CreateRoundedRectanglePath(innerGlow, Math.Max(Dpi.Scale(this, 8), Dpi.Scale(this, _cornerRadius) - Dpi.Scale(this, 2)));
            e.Graphics.DrawPath(glowPen, glowPath);
        }

        private void ApplyLayout()
        {
            int contentLeft = Padding.Left;
            int contentTop = Padding.Top;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - Dpi.Scale(this, 2));

            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, contentWidth - _closeButton.Width - Dpi.Scale(this, 10), Dpi.Scale(this, 18));
            _titleLabel.Bounds = new Rectangle(contentLeft, contentTop + Dpi.Scale(this, 24), contentWidth, Dpi.Scale(this, 34));
            _bodyLabel.Bounds = new Rectangle(contentLeft, contentTop + Dpi.Scale(this, 62), contentWidth, Dpi.Scale(this, 42));

            int buttonsTop = _bodyLabel.Bottom + Dpi.Scale(this, 18);
            int buttonWidth = (contentWidth - Dpi.Scale(this, 12)) / 2;

            _useExistingButton.Bounds = new Rectangle(contentLeft, buttonsTop, buttonWidth, Dpi.Scale(this, 40));
            _dismissButton.Bounds = new Rectangle(_useExistingButton.Right + Dpi.Scale(this, 12), buttonsTop, buttonWidth, Dpi.Scale(this, 40));
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
                DialogResult = DialogResult.Cancel;
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
    }
}
