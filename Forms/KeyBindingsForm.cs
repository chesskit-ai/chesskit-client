namespace ChessKit
{
    internal sealed class KeyBindingsForm : Form
    {
        private readonly Dictionary<HotkeyCommand, ComboBox> _comboBoxes = new();
        private readonly Label _statusLabel;
        private readonly Button _saveButton;
        private readonly HotkeyBindings _bindings;

        public HotkeyBindings Result => _bindings.Clone();

        public KeyBindingsForm(HotkeyBindings current)
        {
            _bindings = current.Clone();
            _bindings.Normalize();

            Text = "Key Bindings";
            ShowIcon = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(440, 536);
            MinimumSize = new Size(456, 576);
            BackColor = Color.FromArgb(24, 24, 27);
            ForeColor = Color.FromArgb(238, 238, 242);
            Font = new Font("Segoe UI", 10f);

            var title = new Label
            {
                Text = "Function Key Bindings",
                AutoSize = false,
                Left = 20,
                Top = 18,
                Width = 380,
                Height = 32,
                Font = new Font(Font.FontFamily, 13f, FontStyle.Bold),
                ForeColor = Color.White
            };
            Controls.Add(title);

            int y = 64;
            foreach (HotkeyCommand command in Enum.GetValues<HotkeyCommand>())
            {
                var label = new Label
                {
                    Text = GetCommandLabel(command),
                    Left = 24,
                    Top = y + 4,
                    Width = 210,
                    Height = 28,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                Controls.Add(label);

                var combo = new ComboBox
                {
                    Left = 250,
                    Top = y,
                    Width = 130,
                    Height = 30,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(42, 42, 46),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                for (Keys key = Keys.F1; key <= Keys.F12; key++)
                    combo.Items.Add(key);
                combo.SelectedItem = _bindings.GetKey(command);
                combo.SelectedIndexChanged += (_, _) => ValidateBindings();
                Controls.Add(combo);
                _comboBoxes[command] = combo;
                y += 38;
            }

            _statusLabel = new Label
            {
                Left = 24,
                Top = y + 4,
                Width = 360,
                Height = 34,
                ForeColor = Color.FromArgb(180, 185, 195)
            };
            Controls.Add(_statusLabel);

            int buttonTop = ClientSize.Height - 78;

            var resetButton = CreateButton("Defaults", 24, buttonTop, 110);
            resetButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            resetButton.Click += (_, _) =>
            {
                var defaults = new HotkeyBindings();
                foreach (HotkeyCommand command in Enum.GetValues<HotkeyCommand>())
                    _comboBoxes[command].SelectedItem = defaults.GetKey(command);
                ValidateBindings();
            };
            Controls.Add(resetButton);

            var cancelButton = CreateButton("Cancel", 180, buttonTop, 90);
            cancelButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            cancelButton.Click += (_, _) => DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);

            _saveButton = CreateButton("Save", 290, buttonTop, 90, primary: true);
            _saveButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _saveButton.Click += (_, _) =>
            {
                if (!ValidateBindings())
                    return;

                foreach (var pair in _comboBoxes)
                    _bindings.SetKey(pair.Key, (Keys)pair.Value.SelectedItem!);
                DialogResult = DialogResult.OK;
            };
            Controls.Add(_saveButton);

            Layout += (_, _) =>
            {
                // The Layout handler runs at runtime against the live (already
                // DPI-scaled) ClientSize, so the bottom inset literal must itself
                // be scaled to keep the buttons flush with the bottom at high DPI.
                // (Identity at 100% — Dpi.Scale(this, 78) == 78 at 96 DPI.)
                int top = ClientSize.Height - Dpi.Scale(this, 78);
                resetButton.Top = top;
                cancelButton.Top = top;
                _saveButton.Top = top;
            };

            ValidateBindings();
        }

        private bool ValidateBindings()
        {
            var selected = _comboBoxes.ToDictionary(p => p.Key, p => (Keys)p.Value.SelectedItem!);
            var duplicate = selected
                .GroupBy(p => p.Value)
                .FirstOrDefault(g => g.Count() > 1);

            if (duplicate != null)
            {
                _statusLabel.ForeColor = Color.FromArgb(255, 120, 120);
                _statusLabel.Text = $"{duplicate.Key} is assigned more than once.";
                _saveButton.Enabled = false;
                return false;
            }

            if (selected.Values.Any(k => !HotkeyBindings.IsValidFunctionKey(k)))
            {
                _statusLabel.ForeColor = Color.FromArgb(255, 120, 120);
                _statusLabel.Text = "Only F1-F12 keys are supported.";
                _saveButton.Enabled = false;
                return false;
            }

            _statusLabel.ForeColor = Color.FromArgb(130, 220, 160);
            _statusLabel.Text = "Bindings look good.";
            _saveButton.Enabled = true;
            return true;
        }

        private Button CreateButton(string text, int x, int y, int width, bool primary = false)
        {
            return new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = width,
                Height = 42,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(66, 144, 245) : Color.FromArgb(44, 44, 48),
                ForeColor = Color.White,
                UseVisualStyleBackColor = false,
                TextAlign = ContentAlignment.MiddleCenter
            };
        }

        private static string GetCommandLabel(HotkeyCommand command) => command switch
        {
            HotkeyCommand.ToggleOverlay => "Enable / disable overlay",
            HotkeyCommand.AnalyzeWhite => "Analyze as White",
            HotkeyCommand.AnalyzeBlack => "Analyze as Black",
            HotkeyCommand.AnalyzeBoth => "Analyze both sides",
            HotkeyCommand.CopyFen => "Copy FEN",
            HotkeyCommand.ToggleEngineLines => "Engine lines",
            HotkeyCommand.ToggleEvalBar => "Eval bar",
            _ => command.ToString()
        };
    }
}
