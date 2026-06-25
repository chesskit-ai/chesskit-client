using System.Drawing.Drawing2D;

namespace ChessKit
{
    internal sealed class FreeTermsForm : Form
    {
        private readonly CheckBox _acceptCheck;
        private readonly Button _acceptButton;

        public FreeTermsForm()
        {
            bool isFreeEdition = BuildLimits.IsFreeEdition;
            Text = isFreeEdition ? "Chess Kit (Free Edition)" : "Chess Kit";
            ShowIcon = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScroll = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = FreeUi.CreateFont(10f);
            ClientSize = FreeUi.GetStartupClientSize(720, 620);
            MinimumSize = new Size(500, 420);
            BackColor = FreeUi.Colors.Window;
            ForeColor = FreeUi.Colors.Text;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(28, 22, 28, 24),
                BackColor = FreeUi.Colors.Window,
                ColumnCount = 1,
                RowCount = 5
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Text = isFreeEdition ? "Chess Kit (Free Edition)" : "Chess Kit",
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = FreeUi.CreateFont(22f, FontStyle.Bold, semibold: true),
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 0, 12)
            }, 0, 0);

            var terms = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = FreeUi.Colors.Panel,
                ForeColor = FreeUi.Colors.Text,
                Font = FreeUi.CreateFont(11.25f),
                Text = BuildTermsText(),
                Margin = new Padding(0, 8, 0, 16),
                DetectUrls = false,
                TabStop = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            root.Controls.Add(FreeUi.WrapPanel(terms, new Padding(18)), 0, 1);

            _acceptCheck = new CheckBox
            {
                Text = isFreeEdition
                    ? "I understand and accept these Free Edition terms."
                    : "I understand and accept these terms.",
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = FreeUi.ControlHeight(11.25f, 2, 10),
                Font = FreeUi.CreateFont(11.25f),
                ForeColor = FreeUi.Colors.Text,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 14)
            };
            root.Controls.Add(_acceptCheck, 0, 2);

            var note = new Label
            {
                Text = "Acceptance is saved locally on this machine.",
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = FreeUi.ControlHeight(9.5f, 1, 12),
                Font = FreeUi.CreateFont(9.5f),
                ForeColor = FreeUi.Colors.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 18)
            };
            root.Controls.Add(note, 0, 3);

            var buttons = FreeUi.CreateButtonBar(2);

            _acceptButton = FreeUi.CreateButton("Accept and continue", primary: true);
            _acceptButton.Dock = DockStyle.Fill;
            _acceptButton.Enabled = false;
            _acceptCheck.CheckedChanged += (_, _) => _acceptButton.Enabled = _acceptCheck.Checked;
            _acceptButton.Click += (_, _) =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            var closeButton = FreeUi.CreateButton(isFreeEdition ? "Close" : "Close", primary: false);
            closeButton.Dock = DockStyle.Fill;
            closeButton.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            buttons.Controls.Add(closeButton, 0, 0);
            buttons.Controls.Add(_acceptButton, 1, 0);
            root.Controls.Add(buttons, 0, 4);

            Shown += (_, _) =>
            {
                terms.SelectionLength = 0;
                _acceptCheck.Focus();
            };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult != DialogResult.OK)
            {
                DialogResult = DialogResult.Cancel;
            }

            base.OnFormClosing(e);
        }

        private static string BuildTermsText()
        {
            if (!BuildLimits.IsFreeEdition)
            {
                return
                    "Chess Kit terms\n\n" +
                    "Chess Kit provides chessboard detection, engine analysis, an analysis board, game review, and related tools for studying chess positions and games.\n\n" +
                    "You are responsible for following the rules of any chess website, tournament, server, workplace, or platform where you run this software. Do not use Chess Kit where assistance is prohibited.\n\n" +
                    "No warranty is provided. Computer vision can misread boards, engines can fail to start, and analysis can be wrong. Verify important positions yourself.";
            }

            return
                "Free Edition terms\n\n" +
                "The Chess Kit Free Edition is limited on purpose, including capped external-board assistance, capped analysis-board assistance, reduced engine settings, and restricted export/coaching features.\n\n" +
                "You are responsible for following the rules of any chess website, tournament, server, workplace, or platform where you run this software. Do not use Chess Kit where assistance is prohibited.\n\n" +
                "The Free Edition may display a transparent watermark over detected external boards. This makes the edition limits clear.\n\n" +
                "No warranty is provided. Computer vision can misread boards, engines can fail to start, and analysis can be wrong. Verify important positions yourself.";
        }
    }

    internal sealed class FreeWelcomeForm : Form
    {
        private readonly Label _title;
        private readonly Label _body;
        private readonly Label _counter;
        private readonly Panel _dotPanel;
        private readonly Button _backButton;
        private readonly Button _nextButton;
        private int _pageIndex;

        private readonly (string Title, string Body)[] _pages = BuildPages();

        public FreeWelcomeForm()
        {
            Text = BuildLimits.IsFreeEdition ? "Chess Kit (Free Edition) Welcome" : "Chess Kit Welcome";
            ShowIcon = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScroll = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = FreeUi.CreateFont(10f);
            ClientSize = FreeUi.GetStartupClientSize(760, 560);
            MinimumSize = new Size(500, 400);
            BackColor = FreeUi.Colors.Window;
            ForeColor = FreeUi.Colors.Text;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(28, 24, 28, 24),
                BackColor = FreeUi.Colors.Window,
                ColumnCount = 1,
                RowCount = 4
            };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = FreeUi.Colors.Panel,
                Padding = new Padding(26)
            };
            card.Paint += FreeUi.PaintCardBorder;
            root.Controls.Add(card, 0, 0);

            var cardLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = FreeUi.Colors.Panel,
                ColumnCount = 1,
                RowCount = 3
            };
            cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(cardLayout);

            _title = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = FreeUi.CreateFont(22f, FontStyle.Bold, semibold: true),
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 0, 18)
            };
            cardLayout.Controls.Add(_title, 0, 0);

            _body = new Label
            {
                Dock = DockStyle.Fill,
                Font = FreeUi.CreateFont(12.25f),
                ForeColor = FreeUi.Colors.Text,
                Margin = new Padding(0),
                AutoEllipsis = false
            };
            cardLayout.Controls.Add(_body, 0, 1);

            _counter = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Font = FreeUi.CreateFont(10f),
                ForeColor = FreeUi.Colors.Muted,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 20, 0, 0)
            };
            cardLayout.Controls.Add(_counter, 0, 2);

            _dotPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 26,
                BackColor = FreeUi.Colors.Window,
                Margin = new Padding(0, 16, 0, 18)
            };
            _dotPanel.Paint += PaintDots;
            root.Controls.Add(_dotPanel, 0, 1);

            var buttons = FreeUi.CreateButtonBar(2);
            _nextButton = FreeUi.CreateButton("Next", primary: true);
            _nextButton.Dock = DockStyle.Fill;
            _nextButton.Click += (_, _) => Advance();
            _backButton = FreeUi.CreateButton("Back", primary: false);
            _backButton.Dock = DockStyle.Fill;
            _backButton.Click += (_, _) => Back();
            buttons.Controls.Add(_backButton, 0, 0);
            buttons.Controls.Add(_nextButton, 1, 0);
            root.Controls.Add(buttons, 0, 2);

            RenderPage();
        }

        private void Advance()
        {
            if (_pageIndex >= _pages.Length - 1)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            _pageIndex++;
            RenderPage();
        }

        private void Back()
        {
            if (_pageIndex <= 0)
                return;

            _pageIndex--;
            RenderPage();
        }

        private void RenderPage()
        {
            _title.Text = _pages[_pageIndex].Title;
            _body.Text = _pages[_pageIndex].Body;
            _counter.Text = $"{_pageIndex + 1} of {_pages.Length}";
            _backButton.Enabled = _pageIndex > 0;
            _nextButton.Text = _pageIndex == _pages.Length - 1
                ? "Start"
                : "Next";
            _dotPanel.Invalidate();
        }

        private static (string Title, string Body)[] BuildPages()
        {
            var pages = new List<(string Title, string Body)>
            {
                (BuildLimits.IsFreeEdition ? "Welcome to Chess Kit (Free Edition)" : "Welcome to Chess Kit",
                    "Chess Kit starts as a small floating toolbar. The default hotkey is F1: press F1 to show or hide the toolbar whenever you lose it, hide it, or want it back on top.\n\nYou can change the function-key bindings later from the toolbar menu."),
                ("Choosing What To Analyze",
                    "Use W when you want suggestions for White, B when you want suggestions for Black, and W+B when you want the best engine lines for whichever side is to move.\n\nIf Chess Kit cannot confidently infer board orientation from a position, it will ask which way the pawns move. Answer once and it will remember that orientation during the session."),
                ("External Board Assistance",
                    BuildLimits.IsFreeEdition
                        ? "When a chessboard is visible in another app or browser, Chess Kit detects it and draws arrows directly over the board.\n\nIn the Free Edition the assisted-move allowance is metered: the toolbar shows how many moves are left, and once the limit is reached it briefly pauses and counts down before assistance resumes. A watermark over detected boards shows the current state."
                        : "When a chessboard is visible in another app or browser, Chess Kit detects it and draws arrows directly over the board.\n\nThe toolbar can attach to the target window, follow it as it moves, and return to the top-center screen position when no chessboard is visible."),
                ("Speed And Depth",
                    "Lower depth gives near-instant arrows. Higher depth lets the engine think longer and update the arrows as better lines arrive. The final slider stop is infinite analysis.\n\nThe toolbar menu lets you change depth, arrows/lines, threads, hash, engine, FPS display, key bindings, and taskbar behavior."),
                ("Analysis Board",
                    BuildLimits.IsFreeEdition
                        ? "The built-in analysis board is for testing positions, loading PGN/FEN, engine matches, opening book checks, and game review.\n\nIn the Free Edition, live assistance on the analysis board uses the same metered move allowance as external boards: it pauses briefly and counts down once the limit is reached, then resumes."
                        : "The built-in analysis board is for testing positions, loading PGN/FEN, engine matches, opening book checks, and game review.\n\nIt uses its own engine instance and can run separate analysis without depending on external board detection."),
                ("Game Review",
                    BuildLimits.IsFreeEdition
                        ? $"Game analysis is capped to {BuildLimits.GameAnalysisPlyLimit / 2} full moves in the Free Edition. You can still see the review layout, classifications, chart, and move table.\n\nAnnotated PGN export, match PGN export, and coach review are reserved for the paid version."
                        : "Game analysis reviews a PGN with engine evaluations, classifications, an interactive chart, move table, annotated PGN export, and coach-style summaries."),
                ("Ready",
                    BuildLimits.IsFreeEdition
                        ? "Press F1 if the toolbar is hidden. Pick W, B, or W+B, then bring a chessboard into view.\n\nThe full version removes the Free Edition move caps and unlocks the restricted review/export features."
                        : "Press F1 if the toolbar is hidden. Pick W, B, or W+B, then bring a chessboard into view.\n\nYou can open the analysis board from the toolbar whenever you want a controlled position or game-review workspace.")
            };

            return pages.ToArray();
        }

        private void PaintDots(object? sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int count = _pages.Length;
            int dotSize = Dpi.Scale(_dotPanel, 8);
            int gap = Dpi.Scale(_dotPanel, 12);
            int totalWidth = (count * dotSize) + ((count - 1) * gap);
            int x = Math.Max(0, (_dotPanel.Width - totalWidth) / 2);
            int y = Math.Max(0, (_dotPanel.Height - dotSize) / 2);

            for (int i = 0; i < count; i++)
            {
                using var brush = new SolidBrush(i == _pageIndex ? FreeUi.Colors.Accent : FreeUi.Colors.Border);
                e.Graphics.FillEllipse(brush, x + (i * (dotSize + gap)), y, dotSize, dotSize);
            }
        }
    }

    internal static class FreeUi
    {
        internal static class Colors
        {
            public static readonly Color Window = Color.FromArgb(18, 19, 24);
            public static readonly Color Panel = Color.FromArgb(28, 30, 37);
            public static readonly Color Button = Color.FromArgb(43, 45, 54);
            public static readonly Color ButtonHot = Color.FromArgb(59, 64, 78);
            public static readonly Color Accent = Color.FromArgb(77, 148, 255);
            public static readonly Color AccentDark = Color.FromArgb(49, 103, 190);
            public static readonly Color Text = Color.FromArgb(235, 239, 248);
            public static readonly Color Muted = Color.FromArgb(165, 173, 188);
            public static readonly Color Border = Color.FromArgb(69, 74, 88);
        }

        public static Button CreateButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Width = primary ? 190 : 150,
                Height = ControlHeight(10.5f, 1, 18),
                MinimumSize = new Size(120, ControlHeight(10.5f, 1, 18)),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Colors.Accent : Colors.Button,
                ForeColor = Color.White,
                Font = CreateFont(10.5f, semibold: true),
                Margin = new Padding(8, 0, 0, 0),
                UseVisualStyleBackColor = false
            };
            button.FlatAppearance.BorderColor = primary ? Colors.Accent : Colors.Border;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(92, 161, 255) : Colors.ButtonHot;
            button.FlatAppearance.MouseDownBackColor = primary ? Colors.AccentDark : Color.FromArgb(34, 36, 44);
            return button;
        }

        public static TableLayoutPanel CreateButtonBar(int buttonCount)
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = ControlHeight(10.5f, 1, 22),
                ColumnCount = Math.Max(1, buttonCount),
                RowCount = 1,
                BackColor = Colors.Window,
                Margin = new Padding(0)
            };

            for (int i = 0; i < bar.ColumnCount; i++)
                bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / bar.ColumnCount));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            return bar;
        }

        public static Font CreateFont(float size, FontStyle style = FontStyle.Regular, bool semibold = false)
        {
            return new Font(semibold ? "Segoe UI Semibold" : "Segoe UI", size, style, GraphicsUnit.Point);
        }

        public static int ControlHeight(float fontSize, int lines, int padding)
        {
            using var font = CreateFont(fontSize);
            int lineHeight = TextRenderer.MeasureText("Ag", font).Height;
            return Math.Max(1, (lineHeight * Math.Max(1, lines)) + padding);
        }

        public static Size GetStartupClientSize(int desiredWidth, int desiredHeight)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, desiredWidth, desiredHeight);
            float scale = GetPrimaryScreenScale();
            int logicalWorkWidth = Math.Max(1, (int)Math.Floor(workArea.Width / scale));
            int logicalWorkHeight = Math.Max(1, (int)Math.Floor(workArea.Height / scale));
            int maxWidth = Math.Max(420, (int)(logicalWorkWidth * 0.9));
            int maxHeight = Math.Max(360, (int)(logicalWorkHeight * 0.9));
            return new Size(Math.Min(desiredWidth, maxWidth), Math.Min(desiredHeight, maxHeight));
        }

        private static float GetPrimaryScreenScale()
        {
            try
            {
                using var graphics = Graphics.FromHwnd(IntPtr.Zero);
                return Math.Max(1f, graphics.DpiX / 96f);
            }
            catch
            {
                return 1f;
            }
        }

        public static Panel WrapPanel(Control child, Padding padding)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = padding,
                BackColor = Colors.Panel
            };
            panel.Paint += PaintCardBorder;
            panel.Controls.Add(child);
            return panel;
        }

        public static void PaintCardBorder(object? sender, PaintEventArgs e)
        {
            if (sender is not Control control)
                return;

            using var pen = new Pen(Colors.Border, Dpi.Scale(control, 1f));
            e.Graphics.DrawRectangle(pen, 0, 0, control.Width - 1, control.Height - 1);
        }
    }
}
