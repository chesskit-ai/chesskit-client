using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    public sealed class EngineLinesForm : Form
    {
        private readonly object _lock = new();
        private Rectangle _linesRect;
        private List<MoveVariation> _variations = new();
        private bool _enabled = false;
        private bool _shouldBeVisible = false;
        private readonly WinFormsTimer _updateTimer;
        private Font _moveFont = null!;
        private Font _evalFont = null!;
        private Font _smallFont = null!;
        private Brush _textBrush;
        private Brush _evalBrush;
        private Brush _backgroundBrush;
        private Pen _borderPen;
        private bool _isBlackPerspective = false;

        public EngineLinesForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            // Initial size - will be adjusted based on board
            Size = Dpi.Scale(this, new Size(600, 65));

            BackColor = Color.FromArgb(25, 25, 25);
            DoubleBuffered = true;

            // Initialize drawing resources. The control is custom-drawn with
            // AutoScaleMode.None, so the point-size fonts must track the monitor
            // DPI ourselves (rebuilt on DPI change via BuildFonts) - otherwise
            // text stays 96-DPI sized while the layout grows.
            BuildFonts();
            _textBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
            _evalBrush = new SolidBrush(Color.FromArgb(255, 255, 255));
            _backgroundBrush = new SolidBrush(Color.FromArgb(240, 30, 30, 30));
            _borderPen = new Pen(Color.FromArgb(100, 100, 100, 100), Dpi.Scale(this, 1f));

            // Timer for visibility control
            _updateTimer = new WinFormsTimer { Interval = 16 }; // ~60 FPS
            _updateTimer.Tick += (s, e) =>
            {
                // Determine if we should be visible
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
                    Invalidate();
                }
            };
            _updateTimer.Start();
        }

        // (Re)build the point-size fonts scaled to the current monitor DPI. The
        // base point sizes match the original 96-DPI design; Dpi.Scale is an
        // identity no-op at 100%, so this changes nothing at 96 DPI.
        private void BuildFonts()
        {
            _moveFont?.Dispose();
            _evalFont?.Dispose();
            _smallFont?.Dispose();
            _moveFont = new Font("Consolas", Dpi.Scale(this, 8.5f), FontStyle.Regular);
            _evalFont = new Font("Consolas", Dpi.Scale(this, 9f), FontStyle.Bold);
            _smallFont = new Font("Consolas", Dpi.Scale(this, 8f), FontStyle.Regular);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // Dragging between mixed-DPI monitors: rebuild the DPI-scaled fonts
            // and repaint. The bounds themselves are driven by UpdatePosition
            // off the (already DPI-correct) board rect, so layout follows.
            BuildFonts();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // DeviceDpi is only accurate once the handle exists. If the overlay
            // was constructed before its monitor's DPI was known (or it opens
            // directly on a high-DPI monitor, where OnDpiChanged never fires),
            // rebuild the fonts now at the true DPI. No-op at 96 DPI.
            BuildFonts();

            // Make the window layered and set opacity
            int ex = (int)GetWindowLong(Handle, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(Handle, GWL_EXSTYLE, (IntPtr)ex);

            // Set to 90% opacity for readability
            SetLayeredWindowAttributes(Handle, 0, 230, LWA_ALPHA);
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
        }

        public void SetPerspective(bool isBlackPerspective)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetPerspective(isBlackPerspective)));
                return;
            }

            lock (_lock)
            {
                _isBlackPerspective = isBlackPerspective;
            }
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
                int linesHeight = Dpi.Scale(this, 65);
                int linesMargin = Dpi.Scale(this, 8);

                // NEVER exceed board width - exactly match board width
                int linesWidth = boardRect.Width;

                _linesRect = new Rectangle(
                    boardRect.X,  // Align perfectly with board left edge
                    boardRect.Y + boardRect.Height + linesMargin,
                    linesWidth,
                    linesHeight
                );

                Bounds = _linesRect;
            }
        }

        public void UpdateVariations(List<MoveVariation> variations)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateVariations(variations)));
                return;
            }

            lock (_lock)
            {
                _variations = variations?.Take(3).ToList() ?? new List<MoveVariation>();
            }
        }

        public void UpdateVariations(List<MoveVariation> variations, bool isBlackPerspective)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateVariations(variations, isBlackPerspective)));
                return;
            }

            lock (_lock)
            {
                _variations = variations?.Take(3).ToList() ?? new List<MoveVariation>();
                _isBlackPerspective = isBlackPerspective;
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

            List<MoveVariation> variations;
            bool isBlackPerspective;

            lock (_lock)
            {
                variations = new List<MoveVariation>(_variations);
                isBlackPerspective = _isBlackPerspective;
            }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var rect = ClientRectangle;

            // Draw background with rounded corners
            using (var path = GetRoundedRectangle(rect, Dpi.Scale(this, 6)))
            {
                g.FillPath(_backgroundBrush, path);
                g.DrawPath(_borderPen, path);
            }

            // Draw the variations
            if (variations.Count == 0)
            {
                // Show "Calculating..." when no variations
                string waitingText = "Analyzing position...";
                var textSize = g.MeasureString(waitingText, _moveFont);
                float centerX = (rect.Width - textSize.Width) / 2;
                float centerY = (rect.Height - textSize.Height) / 2;
                g.DrawString(waitingText, _moveFont, _textBrush, centerX, centerY);
            }
            else
            {
                int lineHeight = Dpi.Scale(this, 18);
                int yOffset = Dpi.Scale(this, 5);

                for (int i = 0; i < Math.Min(variations.Count, 3); i++)
                {
                    var variation = variations[i];
                    int y = yOffset + (i * lineHeight);

                    // Draw rank indicator with different colors
                    Color rankColor = i switch
                    {
                        0 => Color.FromArgb(100, 200, 100), // Green for best
                        1 => Color.FromArgb(200, 200, 100), // Yellow for second
                        _ => Color.FromArgb(150, 150, 150)  // Gray for third
                    };

                    using (var rankBrush = new SolidBrush(rankColor))
                    {
                        string rankText = $"{i + 1}.";
                        g.DrawString(rankText, _evalFont, rankBrush, Dpi.Scale(this, 8), y);
                    }

                    // Draw depth information - leave enough room so the line
                    // does not visually collide with board coordinates.
                    using (var depthBrush = new SolidBrush(Color.FromArgb(160, 160, 160)))
                    {
                        string depthText = $"[{variation.Depth}]";
                        g.DrawString(depthText, _smallFont, depthBrush, Dpi.Scale(this, 42), y + Dpi.Scale(this, 1));
                    }

                    // Draw evaluation (inverted for black perspective)
                    string evalText = GetEvaluationText(variation, isBlackPerspective);
                    Color evalColor = GetEvaluationColor(variation, isBlackPerspective);
                    using (var evalBrush = new SolidBrush(evalColor))
                    {
                        // Fixed width for eval to align moves
                        var evalRect = new RectangleF(Dpi.Scale(this, 92), y, Dpi.Scale(this, 68), lineHeight);
                        var format = new StringFormat() { Alignment = StringAlignment.Near };
                        g.DrawString(evalText, _evalFont, evalBrush, evalRect, format);
                    }

                    // Draw moves from the variation - 16 moves
                    string movesText = FormatMoves(variation.Moves, rect.Width);

                    // Start moves at fixed position for alignment
                    var movesRect = new RectangleF(Dpi.Scale(this, 178), y, rect.Width - Dpi.Scale(this, 183), lineHeight);
                    g.DrawString(movesText, _moveFont, _textBrush, movesRect);
                }
            }
        }

        private string FormatMoves(List<string> moves, int formWidth)
        {
            if (moves == null || moves.Count == 0)
                return "";

            // Show 16 moves
            var displayMoves = moves.Take(16).ToList();
            string movesText = string.Join(" ", displayMoves);

            // If there are more moves, add ellipsis
            if (moves.Count > 16)
                movesText += "...";

            // Dynamic truncation based on form width
            // Approximately 5 pixels per character at this font size. Both the
            // left offset (where moves start) and the per-character width scale
            // with DPI so the truncation point matches the actual rendered text.
            int maxChars = (formWidth - Dpi.Scale(this, 188)) / Dpi.Scale(this, 5);
            if (movesText.Length > maxChars && maxChars > 10)
            {
                movesText = movesText.Substring(0, maxChars - 3) + "...";
            }

            return movesText;
        }

        private GraphicsPath GetRoundedRectangle(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            path.StartFigure();
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180, 90);
            path.AddLine(rect.X + radius, rect.Y, rect.Right - radius * 2, rect.Y);
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270, 90);
            path.AddLine(rect.Right, rect.Y + radius, rect.Right, rect.Bottom - radius * 2);
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddLine(rect.Right - radius * 2, rect.Bottom, rect.X + radius, rect.Bottom);
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        private string GetEvaluationText(MoveVariation variation, bool isBlackPerspective)
        {
            if (variation.ScoreType == "mate")
            {
                int mateIn = variation.MateIn ?? 0;
                // Invert mate for black perspective
                if (isBlackPerspective)
                    mateIn = -mateIn;
                return $"M{Math.Abs(mateIn)}";
            }
            else
            {
                double score = variation.Score;
                // Invert score for black perspective
                if (isBlackPerspective)
                    score = -score;

                if (Math.Abs(score) >= 10)
                    return score > 0 ? "+∞" : "-∞";
                else
                    return score.ToString("+0.00;-0.00");
            }
        }

        private Color GetEvaluationColor(MoveVariation variation, bool isBlackPerspective)
        {
            double score = variation.Score;
            int? mateIn = variation.MateIn;

            // Invert for black perspective
            if (isBlackPerspective)
            {
                score = -score;
                if (mateIn.HasValue)
                    mateIn = -mateIn.Value;
            }

            if (variation.ScoreType == "mate")
            {
                return (mateIn ?? 0) > 0 ? Color.LightGreen : Color.LightCoral;
            }
            else
            {
                if (score > 1.5)
                    return Color.LightGreen;
                else if (score < -1.5)
                    return Color.LightCoral;
                else
                    return Color.FromArgb(220, 220, 220);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _updateTimer?.Stop();
                _updateTimer?.Dispose();
                _moveFont?.Dispose();
                _evalFont?.Dispose();
                _smallFont?.Dispose();
                _textBrush?.Dispose();
                _evalBrush?.Dispose();
                _backgroundBrush?.Dispose();
                _borderPen?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Win32 constants
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int LWA_ALPHA = 0x2;

        [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }
}
