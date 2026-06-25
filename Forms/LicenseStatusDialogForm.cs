using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    internal sealed class LicenseStatusItem
    {
        public string Name { get; init; } = "";
        public string StateText { get; init; } = "";
        public string DetailText { get; init; } = "";
        public LicenseStatusVisualState VisualState { get; init; }
    }

    internal enum LicenseStatusVisualState
    {
        Licensed,
        Warning,
        Error,
        Info
    }

    internal sealed class LicenseStatusDialogForm : Form
    {
        private readonly Label _eyebrowLabel;
        private readonly Label _titleLabel;
        private readonly Label _hwidCaptionLabel;
        private readonly Label _hwidLabel;
        private readonly Label _statusLabel;
        private readonly Button _copyButton;
        private readonly Button _purchaseButton;
        private readonly Button _okButton;
        private readonly Button _closeButton;
        private List<LicenseStatusItem> _items;
        private readonly Func<string, bool>? _copyAction;
        private readonly string _hwid;
        private readonly string _purchaseUrl;
        private bool _hwidRevealLocked;
        private readonly Color _surfaceColor = Color.FromArgb(22, 24, 31);
        private readonly Color _surfaceEdgeColor = Color.FromArgb(56, 64, 86);
        private readonly Color _panelColor = Color.FromArgb(16, 18, 24);
        private Color _accentColor;
        private float _uiScale;
        private readonly WinFormsTimer _centerTimer;
        private int _centerPassesRemaining = 6;

        public LicenseStatusDialogForm(
            IEnumerable<LicenseStatusItem> items,
            string hwid,
            Func<string, bool>? copyAction,
            string purchaseUrl)
        {
            _items = items.ToList();
            _hwid = hwid;
            _copyAction = copyAction;
            _purchaseUrl = purchaseUrl;
            _accentColor = ResolveAccentColor(_items);

            // True per-monitor DPI factor (PerMonitorV2). No artificial ceiling so
            // the dialog scales correctly at 175/200%+; floored at 1.0 so 100% is
            // an exact identity no-op.
            _uiScale = Dpi.Factor(this);

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
            ClientSize = ScaleSize(new Size(560, 360));
            MinimumSize = ScaleSize(new Size(520, 338));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            Text = "License status";
            KeyPreview = true;
            BorderlessFormDrag.Enable(this);

            _eyebrowLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(155, 178, 255),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold),
                Text = "LICENSE STATUS",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _titleLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 16f, FontStyle.Bold),
                Text = "License status",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _hwidCaptionLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(150, 160, 184),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                Text = "HWID",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _hwidLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(238, 242, 250),
                BackColor = _panelColor,
                Font = new Font("Cascadia Mono", 11f, FontStyle.Bold),
                Text = MaskHwid(hwid),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand
            };

            _statusLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(150, 160, 184),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.8f, FontStyle.Regular),
                Text = "",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _copyButton = CreateGhostButton("Copy HWID", CopyHwid);
            _purchaseButton = CreateActionButton("Purchase", _accentColor, OpenPurchasePage);
            _purchaseButton.Visible = !string.IsNullOrWhiteSpace(_purchaseUrl);
            _okButton = CreateGhostButton("OK", () => CloseWithOk());
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
            Controls.Add(_hwidCaptionLabel);
            Controls.Add(_hwidLabel);
            Controls.Add(_statusLabel);
            Controls.Add(_copyButton);
            Controls.Add(_purchaseButton);
            Controls.Add(_okButton);
            Controls.Add(_closeButton);

            _hwidLabel.MouseEnter += (_, _) => SetHwidRevealed(true);
            _hwidLabel.MouseLeave += (_, _) =>
            {
                if (!_hwidRevealLocked)
                    SetHwidRevealed(false);
            };
            _hwidLabel.Click += (_, _) =>
            {
                _hwidRevealLocked = !_hwidRevealLocked;
                SetHwidRevealed(_hwidRevealLocked);
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
                    CloseWithOk();
            };

            ApplyRoundedRegion();
            ApplyLayout();
            ForceCenterTopMost();
            _statusLabel.Text = string.IsNullOrWhiteSpace(_hwid)
                ? ""
                : "HWID hidden. Hover or click the HWID field to reveal it.";
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

            // The dialog is owner-drawn with AutoScaleMode.None, so WinForms does not
            // re-scale it for us when it moves to a monitor at a different DPI. Recompute
            // the scale factor from the new DeviceDpi and rebuild the size, padding,
            // rounded region, child layout, and repaint so it stays crisp across mixed-DPI
            // monitors.
            _uiScale = Dpi.Factor(this);
            MinimumSize = ScaleSize(new Size(520, 338));
            Padding = new Padding(ScaleValue(22), ScaleValue(20), ScaleValue(22), ScaleValue(20));
            ApplyRoundedRegion();
            ApplyLayout();
            ForceCenterTopMost();
            Invalidate();
        }

        public static void ShowStatus(
            IWin32Window? owner,
            IEnumerable<LicenseStatusItem> items,
            string hwid,
            Func<string, bool>? copyAction,
            string purchaseUrl)
        {
            using var dialog = new LicenseStatusDialogForm(items, hwid, copyAction, purchaseUrl);
            AppIcon.ApplyTo(dialog);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.TopMost = true;
            dialog.Shown += (_, _) =>
            {
                dialog.ForceCenterTopMost();
                dialog.BeginInvoke(new Action(dialog.ForceCenterTopMost));
            };

            if (owner == null)
            {
                dialog.ShowDialog();
            }
            else
            {
                dialog.ShowDialog(owner);
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
            {
                SetWindowPos(Handle, HWND_TOPMOST, centered.X, centered.Y, size.Width, size.Height, SWP_SHOWWINDOW);
            }
            BringToFront();
            Activate();
        }

        public void UpdateStatusItems(IEnumerable<LicenseStatusItem> items)
        {
            _items = items.ToList();
            _accentColor = ResolveAccentColor(_items);
            _purchaseButton.BackColor = _accentColor;
            _purchaseButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_accentColor, 0.08f);
            _purchaseButton.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_accentColor, 0.08f);
            _statusLabel.Text = string.IsNullOrWhiteSpace(_hwid)
                ? ""
                : "HWID hidden. Hover or click the HWID field to reveal it.";
            ApplyLayout();
            Invalidate();
        }

        private static Color ResolveAccentColor(IReadOnlyCollection<LicenseStatusItem> items) =>
            items.Any(i => i.VisualState == LicenseStatusVisualState.Error)
                ? Color.FromArgb(232, 92, 92)
                : items.Any(i => i.VisualState == LicenseStatusVisualState.Warning)
                    ? Color.FromArgb(244, 166, 78)
                    : items.Any(i => i.VisualState == LicenseStatusVisualState.Info)
                        ? Color.FromArgb(72, 150, 255)
                        : Color.FromArgb(92, 204, 142);

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

            int contentLeft = Padding.Left;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;
            int rowTop = _titleLabel.Bottom + ScaleValue(12);
            int rowHeight = ScaleValue(66);
            int rowGap = ScaleValue(10);

            using var nameFont = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
            using var stateFont = new Font("Segoe UI Semibold", 9.3f, FontStyle.Bold);
            using var detailFont = new Font("Segoe UI", 8.8f, FontStyle.Regular);

            for (int i = 0; i < _items.Count; i++)
            {
                Rectangle row = new Rectangle(contentLeft, rowTop + i * (rowHeight + rowGap), contentWidth, rowHeight);
                DrawStatusRow(e.Graphics, row, _items[i], nameFont, stateFont, detailFont);
            }

            Rectangle hwidBounds = _hwidLabel.Bounds;
            using GraphicsPath hwidPath = CreateRoundedRectanglePath(hwidBounds, ScaleValue(8));
            using SolidBrush hwidBrush = new SolidBrush(_panelColor);
            using Pen hwidPen = new Pen(Color.FromArgb(54, 62, 80), 1f);
            e.Graphics.FillPath(hwidBrush, hwidPath);
            e.Graphics.DrawPath(hwidPen, hwidPath);
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

        private void DrawStatusRow(Graphics g, Rectangle row, LicenseStatusItem item, Font nameFont, Font stateFont, Font detailFont)
        {
            Color statusColor = item.VisualState switch
            {
                LicenseStatusVisualState.Licensed => Color.FromArgb(92, 204, 142),
                LicenseStatusVisualState.Warning => Color.FromArgb(244, 166, 78),
                LicenseStatusVisualState.Error => Color.FromArgb(232, 92, 92),
                _ => Color.FromArgb(72, 150, 255)
            };

            using GraphicsPath rowPath = CreateRoundedRectanglePath(row, ScaleValue(8));
            using SolidBrush rowBrush = new SolidBrush(Color.FromArgb(27, 30, 39));
            using Pen rowPen = new Pen(Color.FromArgb(56, 64, 86), 1f);
            g.FillPath(rowBrush, rowPath);
            g.DrawPath(rowPen, rowPath);

            Rectangle stripe = new Rectangle(row.Left, row.Top, ScaleValue(5), row.Height);
            using GraphicsPath stripePath = CreateRoundedRectanglePath(stripe, ScaleValue(3));
            using SolidBrush stripeBrush = new SolidBrush(statusColor);
            g.FillPath(stripeBrush, stripePath);

            int left = row.Left + ScaleValue(18);
            int top = row.Top + ScaleValue(10);
            int rightStatusWidth = ScaleValue(130);
            Rectangle nameRect = new Rectangle(left, top, row.Width - ScaleValue(34) - rightStatusWidth, ScaleValue(22));
            Rectangle stateRect = new Rectangle(row.Right - ScaleValue(16) - rightStatusWidth, top, rightStatusWidth, ScaleValue(22));
            Rectangle detailRect = new Rectangle(left, row.Top + ScaleValue(34), row.Width - ScaleValue(34), ScaleValue(24));

            TextRenderer.DrawText(g, item.Name, nameFont, nameRect, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, item.StateText, stateFont, stateRect, statusColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, item.DetailText, detailFont, detailRect, Color.FromArgb(194, 200, 218), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void ApplyLayout()
        {
            int contentLeft = Padding.Left;
            int contentTop = Padding.Top;
            int contentWidth = ClientSize.Width - Padding.Left - Padding.Right;
            int closeGap = ScaleValue(12);
            int eyebrowHeight = ScaleValue(20);
            int titleHeight = ScaleValue(42);
            int rowHeight = ScaleValue(66);
            int rowGap = ScaleValue(10);
            int rowAreaHeight = _items.Count * rowHeight + Math.Max(0, _items.Count - 1) * rowGap;
            int hwidCaptionHeight = ScaleValue(20);
            int hwidHeight = ScaleValue(42);
            int statusHeight = ScaleValue(22);
            int buttonHeight = ScaleValue(38);
            int buttonGap = ScaleValue(12);
            int copyButtonWidth = ScaleValue(132);
            int purchaseButtonWidth = ScaleValue(122);
            int okButtonWidth = ScaleValue(110);

            int requiredHeight =
                contentTop + eyebrowHeight + ScaleValue(8) + titleHeight + ScaleValue(12) +
                rowAreaHeight + ScaleValue(16) + hwidCaptionHeight + ScaleValue(4) +
                hwidHeight + ScaleValue(6) + statusHeight + ScaleValue(14) +
                buttonHeight + Padding.Bottom;

            if (ClientSize.Height < requiredHeight)
            {
                ClientSize = new Size(ClientSize.Width, requiredHeight);
                return;
            }

            _closeButton.Location = new Point(ClientSize.Width - Padding.Right - _closeButton.Width, Padding.Top - ScaleValue(2));
            _eyebrowLabel.Bounds = new Rectangle(contentLeft, contentTop, contentWidth - _closeButton.Width - closeGap, eyebrowHeight);
            _titleLabel.Bounds = new Rectangle(contentLeft, _eyebrowLabel.Bottom + ScaleValue(8), contentWidth - _closeButton.Width - closeGap, titleHeight);

            int rowAreaBottom = _titleLabel.Bottom + ScaleValue(12) + rowAreaHeight;
            _hwidCaptionLabel.Bounds = new Rectangle(contentLeft, rowAreaBottom + ScaleValue(16), contentWidth, hwidCaptionHeight);
            _hwidLabel.Bounds = new Rectangle(contentLeft, _hwidCaptionLabel.Bottom + ScaleValue(4), contentWidth, hwidHeight);
            _statusLabel.Bounds = new Rectangle(contentLeft, _hwidLabel.Bottom + ScaleValue(6), contentWidth, statusHeight);

            int buttonTop = ClientSize.Height - Padding.Bottom - buttonHeight;
            _copyButton.Bounds = new Rectangle(contentLeft, buttonTop, copyButtonWidth, buttonHeight);
            _okButton.Bounds = new Rectangle(ClientSize.Width - Padding.Right - okButtonWidth, buttonTop, okButtonWidth, buttonHeight);
            _purchaseButton.Bounds = new Rectangle(_okButton.Left - buttonGap - purchaseButtonWidth, buttonTop, purchaseButtonWidth, buttonHeight);
        }

        private void CopyHwid()
        {
            bool copied = _copyAction?.Invoke(_hwid) ?? false;
            _statusLabel.Text = copied ? "HWID copied to clipboard." : "Clipboard is busy. Try again.";
            _statusLabel.ForeColor = copied ? Color.FromArgb(132, 218, 166) : Color.FromArgb(232, 130, 130);
        }

        private void SetHwidRevealed(bool revealed)
        {
            _hwidLabel.Text = revealed ? (string.IsNullOrWhiteSpace(_hwid) ? "Unavailable" : _hwid) : MaskHwid(_hwid);
            if (!string.IsNullOrWhiteSpace(_hwid))
            {
                _statusLabel.Text = revealed
                    ? (_hwidRevealLocked ? "HWID visible. Click the field again to hide it." : "HWID visible while hovered.")
                    : "HWID hidden. Hover or click the HWID field to reveal it.";
                _statusLabel.ForeColor = Color.FromArgb(150, 160, 184);
            }
        }

        private static string MaskHwid(string value)
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

        private void CloseWithOk()
        {
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
            button.Click += (_, _) => CloseWithOk();
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
