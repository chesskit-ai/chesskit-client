using System.Drawing.Drawing2D;
using System.Reflection;

namespace ChessKit
{
    internal sealed class AboutDialogForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _versionLabel;
        private readonly Label _statusLabel;
        private readonly Button _checkButton;
        private readonly Button _websiteButton;
        private readonly Button _okButton;
        private readonly Button _closeButton;
        private readonly Func<Task<UpdateCheckResult>> _checkForUpdateAsync;
        private readonly Action<UpdateCheckResult, UpdateDialogChoice> _handleUpdateChoice;
        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _accentColor = Color.FromArgb(72, 150, 255);
        private float _uiScale;

        private AboutDialogForm(
            Func<Task<UpdateCheckResult>> checkForUpdateAsync,
            Action<UpdateCheckResult, UpdateDialogChoice> handleUpdateChoice)
        {
            _checkForUpdateAsync = checkForUpdateAsync;
            _handleUpdateChoice = handleUpdateChoice;

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
            ClientSize = ScaleSize(new Size(540, 238));
            MinimumSize = ScaleSize(new Size(500, 224));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            Text = "About Chess Kit";
            KeyPreview = true;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = CreateLabel("ABOUT CHESS KIT", Color.FromArgb(155, 178, 255), new Font("Segoe UI Semibold", 8f, FontStyle.Bold));
            _titleLabel = CreateLabel("Chess Kit", Color.White, new Font("Segoe UI Semibold", 18f, FontStyle.Bold));
            _versionLabel = CreateLabel(GetVersionText(), Color.FromArgb(218, 223, 238), new Font("Segoe UI", 10f, FontStyle.Regular));
            _statusLabel = CreateLabel("Ready to check for updates.", Color.FromArgb(155, 164, 188), new Font("Segoe UI", 9f, FontStyle.Regular));

            _checkButton = CreateActionButton("Check for updates", _accentColor, async () => await CheckForUpdateFromDialogAsync());
            _websiteButton = CreateGhostButton("Visit website", () => UpdateChecker.OpenDownloadPage("https://chesskit.ai"));
            _okButton = CreateGhostButton("OK", Close);
            _closeButton = CreateCloseButton();

            Controls.AddRange(new Control[] { _eyebrowLabel, _titleLabel, _versionLabel, _statusLabel, _checkButton, _websiteButton, _okButton, _closeButton });

            Layout += (_, _) => ApplyLayout();
            SizeChanged += (_, _) => { ApplyRoundedRegion(); ApplyLayout(); };
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
                    Close();
            };

            ApplyRoundedRegion();
            ApplyLayout();
        }

        public static void ShowAbout(
            IWin32Window? owner,
            Func<Task<UpdateCheckResult>> checkForUpdateAsync,
            Action<UpdateCheckResult, UpdateDialogChoice> handleUpdateChoice)
        {
            using var dialog = new AboutDialogForm(checkForUpdateAsync, handleUpdateChoice);
            AppIcon.ApplyTo(dialog);
            if (owner == null)
                dialog.ShowDialog();
            else
                dialog.ShowDialog(owner);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            // AutoScaleMode.None + manual layout: WinForms will not re-scale this
            // form when it moves between monitors of different DPI, so recompute the
            // DeviceDpi-derived scale and re-apply every scaled literal (size,
            // padding, close-button size, rounded region, child bounds) ourselves.
            _uiScale = Dpi.Factor(this);

            _closeButton.Size = ScaleSize(new Size(32, 32));
            ClientSize = ScaleSize(new Size(540, 238));
            MinimumSize = ScaleSize(new Size(500, 224));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));

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
        }

        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(_surfaceColor);

        private async Task CheckForUpdateFromDialogAsync()
        {
            _checkButton.Enabled = false;
            _statusLabel.ForeColor = Color.FromArgb(155, 164, 188);
            _statusLabel.Text = "Checking chesskit.ai...";

            try
            {
                UpdateCheckResult result = await _checkForUpdateAsync();
                if (result.IsUpdateAvailable)
                {
                    _statusLabel.Text = $"Update available: {result.LatestVersion}";
                    UpdateDialogChoice choice = UpdateAvailableDialogForm.ShowUpdate(this, result);
                    _handleUpdateChoice(result, choice);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(result.LatestVersion) || string.IsNullOrWhiteSpace(result.Message))
                {
                    _statusLabel.ForeColor = Color.FromArgb(104, 220, 150);
                    _statusLabel.Text = "Chess Kit is up to date.";
                }
                else
                {
                    _statusLabel.ForeColor = Color.FromArgb(255, 210, 120);
                    _statusLabel.Text = result.Message;
                }
            }
            catch (Exception ex)
            {
                _statusLabel.ForeColor = Color.FromArgb(255, 150, 150);
                _statusLabel.Text = $"Update check failed: {ex.Message}";
            }
            finally
            {
                _checkButton.Enabled = true;
            }
        }

        private void ApplyLayout()
        {
            int contentLeft = Padding.Left;
            int contentTop = Padding.Top;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;
            int closeGap = ScaleValue(12);
            int titleWidth = Math.Max(ScaleValue(260), contentWidth - _closeButton.Width - closeGap);
            int buttonHeight = ScaleValue(38);
            int buttonGap = ScaleValue(12);
            int okWidth = ScaleValue(96);
            int websiteWidth = ScaleValue(126);
            int checkWidth = ScaleValue(164);

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - ScaleValue(2));
            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, titleWidth, ScaleValue(20));
            _titleLabel.Bounds = new Rectangle(contentLeft, _eyebrowLabel.Bottom + ScaleValue(8), titleWidth, ScaleValue(44));
            _versionLabel.Bounds = new Rectangle(contentLeft, _titleLabel.Bottom + ScaleValue(8), contentWidth, ScaleValue(52));
            _statusLabel.Bounds = new Rectangle(contentLeft, _versionLabel.Bottom + ScaleValue(8), contentWidth, ScaleValue(26));

            int buttonTop = ClientSize.Height - Padding.Bottom - buttonHeight;
            _okButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - okWidth, buttonTop, okWidth, buttonHeight);
            _websiteButton.Bounds = new Rectangle(_okButton.Left - buttonGap - websiteWidth, buttonTop, websiteWidth, buttonHeight);
            _checkButton.Bounds = new Rectangle(contentLeft, buttonTop, checkWidth, buttonHeight);
        }

        private static string GetVersionText()
        {
            string version = UpdateChecker.CurrentVersion;
            string fileVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyFileVersionAttribute>()
                ?.Version ?? version;
#if DEBUG
            string build = BuildLimits.IsDebugFreeEditionOverride ? "Debug (Free)" : "Debug";
#else
            string build = BuildLimits.IsFreeEdition ? "Free Edition" : "Full release";
#endif
            return $"Version {version}\nBuild: {build} | File version {fileVersion}";
        }

        private static Label CreateLabel(string text, Color color, Font font) =>
            new()
            {
                AutoSize = false,
                Text = text,
                ForeColor = color,
                BackColor = Color.Transparent,
                Font = font,
                TextAlign = ContentAlignment.MiddleLeft
            };

        private Button CreateActionButton(string text, Color backColor, Func<Task> onClick)
        {
            Button button = CreateBaseButton(text, backColor, Color.White, new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold));
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(backColor, 0.08f);
            button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.08f);
            button.Click += async (_, _) => await onClick();
            return button;
        }

        private Button CreateGhostButton(string text, Action onClick)
        {
            Button button = CreateBaseButton(text, Color.FromArgb(34, 38, 49), Color.FromArgb(218, 223, 238), new Font("Segoe UI", 9f, FontStyle.Regular));
            button.FlatAppearance.BorderColor = Color.FromArgb(74, 82, 102);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 47, 61);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(32, 36, 46);
            button.Click += (_, _) => onClick();
            return button;
        }

        private Button CreateCloseButton()
        {
            Button button = CreateBaseButton("x", Color.FromArgb(34, 38, 49), Color.FromArgb(220, 226, 240), new Font("Segoe UI Semibold", 9f, FontStyle.Bold));
            button.Size = ScaleSize(new Size(32, 32));
            button.TabStop = false;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(180, 64, 64);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(150, 48, 48);
            button.Click += (_, _) => Close();
            return button;
        }

        private Button CreateBaseButton(string text, Color backColor, Color foreColor, Font font) =>
            new()
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = foreColor,
                Font = font,
                TabStop = true,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false
            };

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
