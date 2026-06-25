using System.Drawing.Drawing2D;

namespace ChessKit
{
    /// <summary>
    /// Owns the Free Edition taskbar access window: a minimized helper form that
    /// lives in the Windows taskbar so Free users always have a way back to the
    /// app's core actions even when the floating toolbar is hidden. It is
    /// DPI-aware (AutoScaleMode.Dpi) and styled to match the rest of the app,
    /// with a prominent Upgrade call-to-action plus a grid of quick actions.
    /// It talks to the rest of the app only through the delegates injected at
    /// construction time.
    /// </summary>
    public sealed class FreeEditionWindow : IDisposable
    {
        private readonly Action _onShowToolbar;
        private readonly Action _onOpenAnalysisBoard;
        private readonly Action _onToggleOverlay;
        private readonly Action _onShowHardwareId;
        private readonly Action _onShowAbout;
        private readonly Action _onExit;
        private readonly Action _onUpgrade;
        private readonly Action _onQuickStart;
        private readonly Action _onOpenSettings;
        private readonly Action _onCheckForUpdates;

        private Form? _window;

        private static readonly Color BgColor      = Color.FromArgb(28, 28, 31);
        private static readonly Color ButtonColor  = Color.FromArgb(48, 48, 53);
        private static readonly Color ButtonHover  = Color.FromArgb(64, 64, 70);
        private static readonly Color BorderColor  = Color.FromArgb(74, 74, 82);
        private static readonly Color AccentColor  = Color.FromArgb(56, 132, 255);
        private static readonly Color AccentHover  = Color.FromArgb(82, 151, 255);
        private static readonly Color AmberColor   = Color.FromArgb(232, 168, 30);
        private static readonly Color TextColor    = Color.White;
        private static readonly Color SubtleColor  = Color.FromArgb(166, 166, 174);

        public FreeEditionWindow(
            Action onShowToolbar,
            Action onOpenAnalysisBoard,
            Action onToggleOverlay,
            Action onShowHardwareId,
            Action onShowAbout,
            Action onExit,
            Action onUpgrade,
            Action onQuickStart,
            Action onOpenSettings,
            Action onCheckForUpdates)
        {
            _onShowToolbar = onShowToolbar;
            _onOpenAnalysisBoard = onOpenAnalysisBoard;
            _onToggleOverlay = onToggleOverlay;
            _onShowHardwareId = onShowHardwareId;
            _onShowAbout = onShowAbout;
            _onExit = onExit;
            _onUpgrade = onUpgrade;
            _onQuickStart = onQuickStart;
            _onOpenSettings = onOpenSettings;
            _onCheckForUpdates = onCheckForUpdates;
        }

        public void Initialize()
        {
            if (_window != null && !_window.IsDisposed)
                return;

            var form = new Form
            {
                Text = "Chess Kit",
                ShowInTaskbar = true,
                StartPosition = FormStartPosition.CenterScreen,
                AutoScaleMode = AutoScaleMode.Dpi,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = true,
                ClientSize = new Size(460, 466),
                BackColor = BgColor,
                ForeColor = TextColor,
                Font = new Font("Segoe UI", 9.75f, FontStyle.Regular)
            };
            AppIcon.ApplyTo(form);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                BackColor = BgColor,
                Padding = new Padding(26, 18, 26, 14)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));   // 0 header
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 1 hint (content-sized so it never clips at high DPI)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50f));   // 2 upgrade
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 3 subtitle (content-sized so it never clips at high DPI)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 192f));  // 4 grid
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 5 spacer
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));   // 6 footer

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildHint(), 0, 1);

            var upgrade = CreateButton("Upgrade to Full", _onUpgrade, primary: true);
            upgrade.Margin = new Padding(2, 2, 2, 2);
            root.Controls.Add(upgrade, 0, 2);

            root.Controls.Add(BuildSubtitle(), 0, 3);
            root.Controls.Add(BuildActionGrid(), 0, 4);
            root.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = BgColor, Margin = new Padding(0) }, 0, 5);
            root.Controls.Add(BuildFooter(), 0, 6);

            form.Controls.Add(root);

            form.FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    form.WindowState = FormWindowState.Minimized;
                }
            };

            _window = form;
            form.WindowState = FormWindowState.Minimized;
            form.Show();
        }

        private static Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Fill, BackColor = BgColor, Margin = new Padding(0) };
            header.Paint += (_, e) =>
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var titleFont = new Font("Segoe UI Semibold", 18f, FontStyle.Bold);
                using var chipFont = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
                const string title = "ChessKit";
                const string chip = "FREE";
                SizeF titleSize = g.MeasureString(title, titleFont);
                SizeF chipTextSize = g.MeasureString(chip, chipFont);
                int gap = Dpi.Scale(header, 11);
                int chipW = (int)Math.Ceiling(chipTextSize.Width) + Dpi.Scale(header, 18);
                int chipH = (int)Math.Ceiling(chipTextSize.Height) + Dpi.Scale(header, 5);
                int totalW = (int)Math.Ceiling(titleSize.Width) + gap + chipW;
                int startX = Math.Max(0, (header.Width - totalW) / 2);
                int midY = header.Height / 2;
                using (var titleBrush = new SolidBrush(TextColor))
                    g.DrawString(title, titleFont, titleBrush, startX, midY - titleSize.Height / 2f);
                var chipRect = new Rectangle(startX + (int)Math.Ceiling(titleSize.Width) + gap, midY - chipH / 2, chipW, chipH);
                using (var chipPath = RoundedRect(chipRect, chipH / 2))
                using (var chipBrush = new SolidBrush(AmberColor))
                    g.FillPath(chipBrush, chipPath);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using (var chipTextBrush = new SolidBrush(Color.FromArgb(30, 24, 6)))
                    g.DrawString(chip, chipFont, chipTextBrush, chipRect, fmt);
            };
            return header;
        }

        private static Control BuildHint()
        {
            return new Label
            {
                Text = "Press F1 anytime to show the toolbar",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = SubtleColor,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
                Margin = new Padding(0)
            };
        }

        private static Control BuildSubtitle()
        {
            return new Label
            {
                Text = "Unlimited moves · human-play engine · no watermark",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = SubtleColor,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 8)
            };
        }

        private Control BuildActionGrid()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                BackColor = BgColor,
                Margin = new Padding(0)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            for (int i = 0; i < 4; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

            grid.Controls.Add(CreateButton("Show toolbar", _onShowToolbar, primary: false), 0, 0);
            grid.Controls.Add(CreateButton("Analysis board", _onOpenAnalysisBoard, primary: false), 1, 0);
            grid.Controls.Add(CreateButton("Toggle overlay", _onToggleOverlay, primary: false), 0, 1);
            grid.Controls.Add(CreateButton("Settings", _onOpenSettings, primary: false), 1, 1);
            grid.Controls.Add(CreateButton("Quick start", _onQuickStart, primary: false), 0, 2);
            grid.Controls.Add(CreateButton("Check for updates", _onCheckForUpdates, primary: false), 1, 2);
            grid.Controls.Add(CreateButton("Show HWID", _onShowHardwareId, primary: false), 0, 3);
            grid.Controls.Add(CreateButton("About", _onShowAbout, primary: false), 1, 3);
            return grid;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = BgColor,
                Margin = new Padding(0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var version = new Label
            {
                Text = "v" + GetVersionText(),
                AutoSize = true,
                ForeColor = SubtleColor,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Anchor = AnchorStyles.Left,
                Margin = new Padding(2, 0, 0, 0)
            };
            footer.Controls.Add(version, 0, 0);

            var exit = CreateButton("Exit", _onExit, primary: false);
            exit.Dock = DockStyle.None;
            exit.AutoSize = false;
            exit.Size = new Size(112, 36);
            exit.Anchor = AnchorStyles.Right;
            exit.Margin = new Padding(2, 4, 2, 4);
            footer.Controls.Add(exit, 1, 0);
            return footer;
        }

        private Button CreateButton(string text, Action onClick, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                ForeColor = TextColor,
                BackColor = primary ? AccentColor : ButtonColor,
                Font = new Font("Segoe UI", primary ? 10.5f : 9.75f, primary ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(4),
                UseVisualStyleBackColor = false,
                AutoEllipsis = true,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? AccentColor : BorderColor;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.MouseOverBackColor = primary ? AccentHover : ButtonHover;
            button.FlatAppearance.MouseDownBackColor = primary ? AccentHover : ButtonHover;
            button.Click += (_, _) =>
            {
                try { onClick(); } catch { }
            };
            return button;
        }

        private static string GetVersionText()
        {
            try { return UpdateChecker.CurrentVersion; }
            catch { return "1.0.0"; }
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            if (d >= rect.Width || d >= rect.Height)
            {
                path.AddEllipse(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (_window != null)
                {
                    try { _window.Dispose(); } catch { }
                    _window = null;
                }
            }
            catch { }
        }
    }
}
