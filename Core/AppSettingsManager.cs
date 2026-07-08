using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ChessKit
{
    public enum SpeculativeAnalysisMode
    {
        Conservative,
        Balanced,
        Aggressive
    }

    public enum BlitzModeSetting
    {
        Auto,
        On,
        Off
    }

    public enum HumanPlayProfile
    {
        Human,
        Balanced,
        Hard
    }

    public enum EvalDisplayMode
    {
        Bar,
        Notch
    }

    public enum HotkeyCommand
    {
        ToggleOverlay,
        AnalyzeWhite,
        AnalyzeBlack,
        AnalyzeBoth,
        CopyFen,
        ToggleEngineLines,
        ToggleEvalBar
    }

    internal sealed class HotkeyBindings
    {
        public Keys ToggleOverlay { get; set; } = Keys.F1;
        public Keys AnalyzeWhite { get; set; } = Keys.F2;
        public Keys AnalyzeBlack { get; set; } = Keys.F4;
        public Keys AnalyzeBoth { get; set; } = Keys.F3;
        public Keys CopyFen { get; set; } = Keys.F7;
        public Keys ToggleEngineLines { get; set; } = Keys.F8;
        public Keys ToggleEvalBar { get; set; } = Keys.F9;

        public Keys GetKey(HotkeyCommand command) => command switch
        {
            HotkeyCommand.ToggleOverlay => ToggleOverlay,
            HotkeyCommand.AnalyzeWhite => AnalyzeWhite,
            HotkeyCommand.AnalyzeBlack => AnalyzeBlack,
            HotkeyCommand.AnalyzeBoth => AnalyzeBoth,
            HotkeyCommand.CopyFen => CopyFen,
            HotkeyCommand.ToggleEngineLines => ToggleEngineLines,
            HotkeyCommand.ToggleEvalBar => ToggleEvalBar,
            _ => Keys.None
        };

        public void SetKey(HotkeyCommand command, Keys key)
        {
            switch (command)
            {
                case HotkeyCommand.ToggleOverlay: ToggleOverlay = key; break;
                case HotkeyCommand.AnalyzeWhite: AnalyzeWhite = key; break;
                case HotkeyCommand.AnalyzeBlack: AnalyzeBlack = key; break;
                case HotkeyCommand.AnalyzeBoth: AnalyzeBoth = key; break;
                case HotkeyCommand.CopyFen: CopyFen = key; break;
                case HotkeyCommand.ToggleEngineLines: ToggleEngineLines = key; break;
                case HotkeyCommand.ToggleEvalBar: ToggleEvalBar = key; break;
            }
        }

        public HotkeyBindings Clone() => new()
        {
            ToggleOverlay = ToggleOverlay,
            AnalyzeWhite = AnalyzeWhite,
            AnalyzeBlack = AnalyzeBlack,
            AnalyzeBoth = AnalyzeBoth,
            CopyFen = CopyFen,
            ToggleEngineLines = ToggleEngineLines,
            ToggleEvalBar = ToggleEvalBar
        };

        public void Normalize()
        {
            if (AnalyzeBoth == Keys.F4 && AnalyzeBlack == Keys.F3)
            {
                AnalyzeBoth = Keys.F3;
                AnalyzeBlack = Keys.F4;
            }

            var defaults = new HotkeyBindings();
            var used = new HashSet<Keys>();
            foreach (HotkeyCommand command in Enum.GetValues<HotkeyCommand>())
            {
                Keys key = GetKey(command);
                if (!IsValidFunctionKey(key) || !used.Add(key))
                {
                    key = defaults.GetKey(command);
                    while (used.Contains(key) && key < Keys.F12)
                        key++;
                    SetKey(command, key);
                    used.Add(key);
                }
            }
        }

        public static bool IsValidFunctionKey(Keys key) => key >= Keys.F1 && key <= Keys.F12;
    }

    internal sealed class AppSettings
    {
        public bool ShowTaskbarIcon { get; set; } = true;
        // The minimized taskbar access window (TaskbarWindow), NOT the tray icon
        // (that is ShowTaskbarIcon above, which predates this naming). Licensed
        // only: the Free Edition always shows its window regardless.
        public bool ShowTaskbarWindow { get; set; } = true;
        public bool SystemTrayHideConfirmed { get; set; } = false;
        public bool SettingsToolbarHidden { get; set; } = false;
        public bool ToolbarNetworkStatsEnabled { get; set; } = false;
        // Exclude the arrow/eval/engine-lines overlays from screen capture
        // (WDA_EXCLUDEFROMCAPTURE). Default ON: prevents our arrows from feeding
        // back into our own DXGI vision capture and keeps the overlay hidden
        // from OBS/screen-share. Turn OFF when debugging in a VM viewed via
        // capture-based remote view (the arrows would otherwise be invisible
        // there). See CaptureExclusion.
        public bool ExcludeOverlaysFromCapture { get; set; } = true;
        public HotkeyBindings Hotkeys { get; set; } = new();
        public bool SpeculativeAnalysisEnabled { get; set; } = true;
        public SpeculativeAnalysisMode SpeculativeAnalysisMode { get; set; } = SpeculativeAnalysisMode.Balanced;
        public BlitzModeSetting BlitzMode { get; set; } = BlitzModeSetting.On;
        public bool HumanAdaptiveEnabled { get; set; } = true;
        public HumanPlayProfile HumanPlayProfile { get; set; } = HumanPlayProfile.Balanced;
        public int ToolbarInitialDepth { get; set; } = 6;
        public int ToolbarDepth { get; set; } = 12;
        public bool ToolbarInfiniteAnalysis { get; set; } = false;
        public int ToolbarThreads { get; set; } = 8;
        public int ToolbarArrowCount { get; set; } = 3;
        public bool ToolbarBulletProfileEnabled { get; set; } = false;
        public bool ToolbarCoachModeEnabled { get; set; } = false;
        public int ToolbarCoachLevel { get; set; } = 5;
        public int ToolbarCoachMarkCount { get; set; } = 1;
        public bool ToolbarCoachCardEnabled { get; set; } = true;
        public int ToolbarHashMb { get; set; } = 128;
        public bool ToolbarEloLimitEnabled { get; set; } = false;
        public int ToolbarMaxEloRating { get; set; } = 2000;
        public EvalDisplayMode EvalDisplayMode { get; set; } = EvalDisplayMode.Bar;
        public string AnalysisBoardEngineFileName { get; set; } = "";
        public string AnalysisBoardEnginePath { get; set; } = "";
        public int AnalysisBoardDepth { get; set; } = 12;
        public bool AnalysisBoardInfinite { get; set; } = false;
        public int AnalysisBoardLineCount { get; set; } = 3;
        public int AnalysisBoardThreads { get; set; } = 1;
        public int AnalysisBoardHashMb { get; set; } = 32;
        public string AnalysisBoardMatchWhiteEngineFileName { get; set; } = "";
        public string AnalysisBoardMatchBlackEngineFileName { get; set; } = "";
        public string AnalysisBoardMatchWhiteEnginePath { get; set; } = "";
        public string AnalysisBoardMatchBlackEnginePath { get; set; } = "";
        public string AnalysisBoardMatchTimeControlKey { get; set; } = "3 min";
        public int AnalysisBoardMatchGameLimit { get; set; } = 0;
        public int AnalysisBoardMatchWhiteWins { get; set; } = 0;
        public int AnalysisBoardMatchBlackWins { get; set; } = 0;
        public int AnalysisBoardMatchDraws { get; set; } = 0;
        public int AnalysisBoardWindowWidth { get; set; } = 0;
        public int AnalysisBoardWindowHeight { get; set; } = 0;
        public int GameAnalysisWindowWidth { get; set; } = 0;
        public int GameAnalysisWindowHeight { get; set; } = 0;
        public int GameCoachWindowWidth { get; set; } = 0;
        public int GameCoachWindowHeight { get; set; } = 0;
        public int AnalysisBoardDefaultSizeVersion { get; set; } = 0;
        public int GameAnalysisDefaultSizeVersion { get; set; } = 0;
        public int GameCoachDefaultSizeVersion { get; set; } = 0;
        public bool StartupTermsAccepted { get; set; } = false;
        public string StartupTermsVersion { get; set; } = "";
        public bool StartupWelcomeCompleted { get; set; } = false;
        public bool LegacyFreeTermsAccepted { get; set; } = false;
        public string LegacyFreeTermsVersion { get; set; } = "";
        public bool LegacyFreeWelcomeCompleted { get; set; } = false;
        public bool DiagnosticsLatencyLogEnabled { get; set; } = false;
        public bool DiagnosticsBoardTraceEnabled { get; set; } = false;
        public bool RemoteEngineEnabled { get; set; } = true;
        public string RemoteEngineHost { get; set; } = "";
        public int RemoteEnginePort { get; set; } = 8091;
        public int RemoteEngineTimeoutMs { get; set; } = 15000;
        public string IgnoredUpdateVersion { get; set; } = "";
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    internal sealed class AppSettingsManager
    {
        private const string SettingsFileName = "settings.ini";
        private const string LegacySettingsFileName = "app_settings.json";
        private readonly string _settingsPath;

        public AppSettingsManager(string settingsPath)
        {
            _settingsPath = ResolveSettingsPath(settingsPath);
        }

        public static string GetDefaultSettingsPath()
        {
            return Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        }

        private static string ResolveSettingsPath(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                return GetDefaultSettingsPath();

            string fileName = Path.GetFileName(requestedPath);
            if (string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase))
                return requestedPath;

            if (string.Equals(fileName, LegacySettingsFileName, StringComparison.OrdinalIgnoreCase))
            {
                string? directory = Path.GetDirectoryName(requestedPath);
                return Path.Combine(string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory, SettingsFileName);
            }

            return requestedPath;
        }

        private static string GetLegacyPortableSettingsPath()
        {
            return Path.Combine(AppContext.BaseDirectory, LegacySettingsFileName);
        }

        private void TryMigrateLegacyJsonSettings()
        {
            try
            {
                string legacyPath = GetLegacyPortableSettingsPath();
                if (File.Exists(_settingsPath) || !File.Exists(legacyPath))
                    return;

                string json = File.ReadAllText(legacyPath);
                AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.Hotkeys ??= new HotkeyBindings();
                settings.Hotkeys.Normalize();
                Save(settings);
            }
            catch
            {
                // Migration is best effort. A missing settings.ini should behave like a fresh portable install.
            }
        }

        public AppSettings Load()
        {
            try
            {
                TryMigrateLegacyJsonSettings();

                if (!File.Exists(_settingsPath))
                {
                    return new AppSettings();
                }

                var text = File.ReadAllText(_settingsPath);
                var settings = text.TrimStart().StartsWith("{", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings()
                    : DeserializeIni(text);
                settings.Hotkeys ??= new HotkeyBindings();
                settings.Hotkeys.Normalize();
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            try
            {
                settings.LastUpdatedUtc = DateTime.UtcNow;
                var text = SerializeIni(settings);
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                var tempPath = _settingsPath + ".tmp";
                File.WriteAllText(tempPath, text, Encoding.UTF8);

                if (!File.Exists(_settingsPath))
                {
                    File.Move(tempPath, _settingsPath);
                    return;
                }

                try
                {
                    File.Replace(tempPath, _settingsPath, null);
                }
                catch
                {
                    File.Copy(tempPath, _settingsPath, overwrite: true);
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        private static AppSettings DeserializeIni(string text)
        {
            AppSettings settings = new();
            settings.Hotkeys ??= new HotkeyBindings();

            string section = "Settings";
            foreach (string rawLine in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                string key = line[..separator].Trim();
                string value = line[(separator + 1)..].Trim();

                if (section.Equals("Hotkeys", StringComparison.OrdinalIgnoreCase))
                {
                    SetHotkeyValue(settings.Hotkeys, key, value);
                    continue;
                }

                if (section.Equals("Diagnostics", StringComparison.OrdinalIgnoreCase))
                {
                    SetDiagnosticsValue(settings, key, value);
                    continue;
                }

                SetSettingValue(settings, key, value);
            }

            return settings;
        }

        private static string SerializeIni(AppSettings settings)
        {
            settings.Hotkeys ??= new HotkeyBindings();
            StringBuilder builder = new();
            builder.AppendLine("; ChessKit portable settings");
            builder.AppendLine("; Delete this file to reset accepted terms, welcome tour, and local preferences.");
            builder.AppendLine("[Settings]");

            foreach (PropertyInfo property in typeof(AppSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.Name == nameof(AppSettings.Hotkeys) || IsDiagnosticsProperty(property.Name))
                    continue;

                object? value = property.GetValue(settings);
                builder.Append(property.Name).Append('=').AppendLine(FormatValue(value));
            }

            builder.AppendLine();
            builder.AppendLine("[Diagnostics]");
            builder.Append(nameof(AppSettings.DiagnosticsLatencyLogEnabled)).Append('=').AppendLine(FormatValue(settings.DiagnosticsLatencyLogEnabled));
            builder.Append("LatencyLog=").AppendLine(FormatValue(settings.DiagnosticsLatencyLogEnabled));
            builder.Append(nameof(AppSettings.DiagnosticsBoardTraceEnabled)).Append('=').AppendLine(FormatValue(settings.DiagnosticsBoardTraceEnabled));
            builder.Append("BoardTrace=").AppendLine(FormatValue(settings.DiagnosticsBoardTraceEnabled));

            builder.AppendLine();
            builder.AppendLine("[Hotkeys]");
            foreach (PropertyInfo property in typeof(HotkeyBindings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                object? value = property.GetValue(settings.Hotkeys);
                builder.Append(property.Name).Append('=').AppendLine(FormatValue(value));
            }

            return builder.ToString();
        }

        private static bool IsDiagnosticsProperty(string propertyName)
        {
            return propertyName.Equals(nameof(AppSettings.DiagnosticsLatencyLogEnabled), StringComparison.OrdinalIgnoreCase) ||
                   propertyName.Equals(nameof(AppSettings.DiagnosticsBoardTraceEnabled), StringComparison.OrdinalIgnoreCase);
        }

        private static void SetDiagnosticsValue(AppSettings settings, string key, string value)
        {
            object? converted = ConvertValue(value, typeof(bool));
            if (converted is not bool boolValue)
                return;

            if (key.Equals(nameof(AppSettings.DiagnosticsLatencyLogEnabled), StringComparison.OrdinalIgnoreCase) ||
                key.Equals("LatencyLog", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Latency", StringComparison.OrdinalIgnoreCase))
            {
                settings.DiagnosticsLatencyLogEnabled = boolValue;
            }
            else if (key.Equals(nameof(AppSettings.DiagnosticsBoardTraceEnabled), StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("BoardTrace", StringComparison.OrdinalIgnoreCase))
            {
                settings.DiagnosticsBoardTraceEnabled = boolValue;
            }
        }

        private static void SetSettingValue(AppSettings settings, string key, string value)
        {
            PropertyInfo? property = typeof(AppSettings).GetProperty(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite || property.Name == nameof(AppSettings.Hotkeys))
                return;

            object? converted = ConvertValue(value, property.PropertyType);
            if (converted != null)
                property.SetValue(settings, converted);
        }

        private static void SetHotkeyValue(HotkeyBindings hotkeys, string key, string value)
        {
            PropertyInfo? property = typeof(HotkeyBindings).GetProperty(key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite)
                return;

            object? converted = ConvertValue(value, property.PropertyType);
            if (converted != null)
                property.SetValue(hotkeys, converted);
        }

        private static object? ConvertValue(string value, Type targetType)
        {
            try
            {
                if (targetType == typeof(string))
                    return value;
                if (targetType == typeof(bool) && bool.TryParse(value, out bool boolValue))
                    return boolValue;
                if (targetType == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                    return intValue;
                if (targetType == typeof(DateTime) && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime dateValue))
                    return dateValue;
                if (targetType.IsEnum && Enum.TryParse(targetType, value, ignoreCase: true, out object? enumValue))
                    return enumValue;
            }
            catch
            {
            }

            return null;
        }

        private static string FormatValue(object? value)
        {
            return value switch
            {
                null => "",
                DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? "",
                _ => value.ToString() ?? ""
            };
        }
    }
}
