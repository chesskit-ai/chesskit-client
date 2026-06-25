using Chess;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace ChessKit
{
    public sealed class AnalysisBoardSnapshot
    {
        public bool Visible { get; init; }
        public bool BoardFlipped { get; init; }
        public bool HasTrackedHistory { get; init; }
        public Rectangle BoardScreenBounds { get; init; }
        public Rectangle WindowScreenBounds { get; init; }
        public string Fen { get; init; } = "";
    }

    public sealed class AnalysisBoardAnalysisSettings
    {
        public string EngineName { get; init; } = "Engine";
        public string EnginePath { get; init; } = "";
        public int Depth { get; init; } = 12;
        public bool Infinite { get; init; }
        public int LineCount { get; init; } = 3;
        public int Threads { get; init; } = 1;
        public int HashMb { get; init; } = 32;
    }

    public sealed class AnalysisBoardMatchSettings
    {
        public string WhiteEngineName { get; init; } = "White Engine";
        public string WhiteEnginePath { get; init; } = "";
        public string BlackEngineName { get; init; } = "Black Engine";
        public string BlackEnginePath { get; init; } = "";
        public string TimeControlKey { get; init; } = "3 min";
        public int BaseSeconds { get; init; } = 180;
        public int GameLimit { get; init; }
        public int Threads { get; init; } = 1;
        public int HashMb { get; init; } = 32;
    }

    public enum AnalysisBoardMatchCommandType
    {
        ToggleRunning,
        StopRunning,
        PauseResume,
        RestartGame,
        RestartMatch,
        ResetScore
    }

    public sealed class AnalysisBoardForm : Form
    {
        private sealed class HistoryEntry
        {
            public string Fen { get; init; } = "";
            public string MoveText { get; init; } = "";
            public Position? LastMoveFrom { get; init; }
            public Position? LastMoveTo { get; init; }
        }

        private sealed class VariationLine
        {
            public Guid Id { get; init; } = Guid.NewGuid();
            public int ParentIndex { get; init; }
            public List<HistoryEntry> Entries { get; init; } = new();
            public Dictionary<int, GameAnalysisMoveResult> AnalysisByOffset { get; init; } = new();
        }

        private sealed class ManualBoardArrow
        {
            public Position From { get; init; }
            public Position To { get; init; }
        }

        private sealed class ArchivedMatchGame
        {
            public string WhiteName { get; init; } = "White";
            public string BlackName { get; init; } = "Black";
            public string Result { get; init; } = "*";
            public string TimeControlKey { get; init; } = "3 min";
            public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
            public List<string> MoveTexts { get; init; } = new();
        }

        private sealed class EngineSelectorItem
        {
            public EngineInfo Engine { get; init; } = null!;
            public override string ToString() => Engine.Name;
        }

        private sealed class SelectorItem
        {
            public string Label { get; init; } = "";
            public int Value { get; init; }
            public override string ToString() => Label;
        }

        private const double DefaultWindowScreenRatio = 0.80;
        private const int InfiniteDepthSliderValue = 31;
        private const string PieceAssetResourceFolder = ".Assets.AnalysisBoardPieces.";

        private readonly Button _resetButton;
        private readonly Button _undoButton;
        private readonly Button _clipboardButton;
        private readonly ContextMenuStrip _clipboardMenu;
        private readonly Button _flipButton;
        private readonly Button _mirrorButton;
        private readonly Button _bookToggleButton;
        private readonly Button _restoreSizeButton;
        private readonly Button _analysisWhiteButton;
        private readonly Button _analysisBlackButton;
        private readonly Button _analysisBothButton;
        private readonly Label _analysisEngineLabel;
        private readonly ComboBox _analysisEngineComboBox;
        private readonly Label _analysisDepthLabel;
        private readonly Label _analysisDepthValueLabel;
        private readonly TrackBar _analysisDepthSlider;
        private readonly CheckBox _analysisInfiniteCheckBox;
        private readonly Label _analysisLinesLabel;
        private readonly ComboBox _analysisLinesComboBox;
        private readonly Label _analysisThreadsLabel;
        private readonly ComboBox _analysisThreadsComboBox;
        private readonly Label _analysisHashLabel;
        private readonly ComboBox _analysisHashComboBox;
        private readonly Button _matchToggleButton;
        private readonly Button _matchPauseButton;
        private readonly Button _matchRestartGameButton;
        private readonly Button _matchRestartMatchButton;
        private readonly Button _matchResetScoreButton;
        private readonly Button _matchCopyGamePgnButton;
        private readonly Button _matchCopyMatchPgnButton;
        private readonly Button _matchClearPgnButton;
        private readonly Button _matchAnalyzeGameButton;
        private readonly Label _matchWhiteEngineLabel;
        private readonly Button _matchWhiteEngineButton;
        private readonly ContextMenuStrip _matchWhiteEngineMenu;
        private readonly Label _matchBlackEngineLabel;
        private readonly Button _matchBlackEngineButton;
        private readonly ContextMenuStrip _matchBlackEngineMenu;
        private readonly Label _matchTimeLabel;
        private readonly Button _matchTimeButton;
        private readonly ContextMenuStrip _matchTimeMenu;
        private readonly Label _matchLengthLabel;
        private readonly Button _matchLengthButton;
        private readonly ContextMenuStrip _matchLengthMenu;
        private readonly ContextMenuStrip _matchGamePgnMenu;
        private readonly TextBox _fenTextBox;
        private readonly Label _turnLabel;
        private readonly Label _movesHeaderLabel;
        private readonly DataGridView _movesGrid;
        private readonly Button _jumpStartButton;
        private readonly Button _stepBackButton;
        private readonly Button _stepForwardButton;
        private readonly Button _jumpEndButton;
        private readonly Font _pieceFont;
        private readonly Font _coordFont;
        private readonly Dictionary<char, Image> _pieceImages = new();
        private readonly System.Windows.Forms.Timer _uiWatchdogTimer;
        private readonly Stopwatch _uiWatchdogStopwatch = new();

        private readonly Color _lightSquare = ColorTranslator.FromHtml("#f0d9b5");
        private readonly Color _darkSquare = ColorTranslator.FromHtml("#b58863");
        private readonly Color _highlightSquare = Color.FromArgb(110, 246, 246, 105);
        private readonly Color _targetHintColor = Color.FromArgb(140, 60, 60, 60);
        private readonly Color _dragOriginOverlay = Color.FromArgb(88, 22, 24, 28);
        private readonly Color _dragHoverOverlay = Color.FromArgb(54, 255, 255, 255);
        private readonly AppSettingsManager _appSettingsManager;
        private readonly EngineManager _analysisEngineManager;
        private readonly FileSystemWatcher _analysisEngineWatcher;
        private List<MoveArrow> _analysisArrows = new();
        private List<MoveVariation> _analysisVariations = new();
        // Server-driven Free analysis-board watermark state. The server governs the
        // Free limit and reports remaining moves + cooldown on each analysis
        // response; the board only displays it. Inert when Licensed (_freeArmed
        // stays false).
        private bool _freeAnalysisArmed;
        private int _freeAnalysisMovesRemaining;
        private int _freeAnalysisCooldownSeconds;
        private bool _freeAnalysisInCooldown;
        // Mirror-mode feedback: set when mirror is on but the source board can't be
        // read (covered or not yet detected), so the user knows why it froze.
        private bool _mirrorPaused;
        private string _mirrorPausedMessage = "";
        private bool _analysisIsBlackPerspective;
        private int _analysisDepth;
        private string _analysisStatusText = "Select W, B, or W+B to start analysis.";
        private EngineInfo? _selectedAnalysisEngine;
        private int _analysisTargetDepth = BuildLimits.ClampDepth(12);
        private bool _analysisInfinite;
        private int _analysisLineCount = BuildLimits.ClampLines(3);
        private int _analysisThreads = 1;
        private int _analysisHashMb = 32;
        private bool _suppressAnalysisSelectorEvents;
        private EngineInfo? _selectedMatchWhiteEngine;
        private EngineInfo? _selectedMatchBlackEngine;
        private string _matchTimeControlKey = "3 min";
        private int _matchBaseSeconds = 180;
        private int _matchGameLimit;
        private bool _matchRunning;
        private bool _matchPaused;
        private string _matchWhiteClockText = "03:00";
        private string _matchBlackClockText = "03:00";
        private string _matchStatusText = "Ready for engine match.";
        private int _matchWhiteWins;
        private int _matchBlackWins;
        // _matchWhiteWins/_matchBlackWins are the persistent per-COMPETITOR scores
        // (A = the engine that started the match as White, B = the other). This flag
        // says which competitor is playing White in the CURRENT game, so the bars map
        // each score to the right board side as the engines swap colours.
        private bool _matchWhiteSideIsCompetitorA = true;
        private int _matchDraws;
        private long _matchDisplayVersion;
        private string _matchScoreLeftLabel = "White";
        private string _matchScoreRightLabel = "Black";
        private readonly List<ArchivedMatchGame> _archivedMatchGames = new();
        private string _loadedGameWhiteName = "White";
        private string _loadedGameBlackName = "Black";
        private string _loadedGameResult = "*";
        private string _loadedGameTimeControlKey = "Analysis";
        private bool _loadedGameMetadataActive;

        private ChessBoard _board;
        private Rectangle _boardRect;
        private Rectangle _sidebarRect;
        private Rectangle _analysisPanelRect;
        private Rectangle _matchPanelRect;
        private Rectangle _bookPanelRect;
        private Rectangle _evalBarRect;
        private Rectangle _boardTopInfoRect;
        private Rectangle _boardBottomInfoRect;
        private Position? _selectedSquare;
        private Move[] _selectedMoves = Array.Empty<Move>();
        private bool _mouseDownOnBoard;
        private bool _mouseDownWasSelectedSquare;
        private bool _isDraggingPiece;
        private Point _mouseDownLocation;
        private Point _dragLocation;
        private Position? _dragOriginSquare;
        private Position? _dragHoverSquare;
        private Piece? _dragPiece;
        private readonly List<Position> _manualCircles = new();
        private readonly List<ManualBoardArrow> _manualArrows = new();
        private bool _rightMouseDownOnBoard;
        private bool _isRightDragging;
        private Position? _rightMouseDownSquare;
        private Position? _rightHoverSquare;
        private readonly List<HistoryEntry> _history = new();
        private readonly Dictionary<int, GameAnalysisMoveResult> _moveAnalysisByPly = new();
        private readonly Dictionary<int, List<VariationLine>> _variationsByParentIndex = new();
        private int _historyIndex = 0;
        private bool _suppressGridSelection;
        private bool _boardFlipped;
        private bool _mirrorModeEnabled;
        private bool _openingBookEnabled;
        private bool _applyingSavedWindowSize;
        private string _analysisMode = "OFF";
        private string _lastBookFen = "";
        private string _bookStatusText = "Book disabled.";
        private string _bookOpeningTitle = "";
        private List<OpeningBookMove> _bookMoves = new();
        private CancellationTokenSource? _bookLookupCts;
        private static readonly Dictionary<string, OpeningBookView> OpeningBookCache = new();
        private static readonly Lazy<EmbeddedOpeningBookData> EmbeddedOpeningBook = new(LoadEmbeddedOpeningBook);

        public event Action<AnalysisBoardSnapshot>? SnapshotChanged;
        public event Action<string>? AnalysisModeChanged;
        public event Action<bool>? MirrorModeChanged;
        public event Action<AnalysisBoardAnalysisSettings>? AnalysisSettingsChanged;
        public event Action<AnalysisBoardMatchSettings>? MatchSettingsChanged;
        public event Action<AnalysisBoardMatchCommandType>? MatchCommandRequested;
        public event Action<GameAnalysisRequest>? GameAnalysisRequested;

        private static readonly (string Label, int Seconds)[] MatchTimeControls =
        {
            ("15 sec", 15),
            ("30 sec", 30),
            ("1 min", 60),
            ("3 min", 180),
            ("5 min", 300),
            ("10 min", 600),
            ("15 min", 900),
            ("30 min", 1800),
            ("60 min", 3600)
        };

        private static readonly int[] MatchGameLimits =
        {
            0, 1, 2, 4, 6, 8, 10, 12, 20, 30, 50, 100, 250, 500, 1000
        };

        public AnalysisBoardForm()
        {
            Text = "Chess Kit Analysis Board";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(920, 760);
            Size = GetDefaultWindowSize(MinimumSize);
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 28, 30);
            ForeColor = Color.White;

            _appSettingsManager = new AppSettingsManager(Path.Combine(AppContext.BaseDirectory, "settings.ini"));
            // Analysis board uses LOCAL engines only - no remote entries.
            _analysisEngineManager = new EngineManager(Path.Combine(AppContext.BaseDirectory, "engines"), includeRemote: false);
            _analysisEngineManager.LoadSettings();
            _board = CreateNewBoard();

            _pieceFont = new Font("Segoe UI Symbol", 40, FontStyle.Regular, GraphicsUnit.Pixel);
            _coordFont = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            LoadPieceImages();
            ApplySavedWindowSize();
            _uiWatchdogTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _uiWatchdogTimer.Tick += (_, _) => MonitorUiResponsiveness();
            _uiWatchdogStopwatch.Start();
            _uiWatchdogTimer.Start();

            _resetButton = CreateButton("Reset", (_, _) =>
            {
                ClearLoadedGameMetadata();
                _board = CreateNewBoard();
                ClearSelection();
                ResetHistory();
                SyncStatus();
                Invalidate();
                EmitSnapshot();
            });

            _undoButton = CreateButton("Undo", (_, _) => StepHistory(-1));

            _clipboardButton = CreateButton("Clipboard", (_, _) =>
            {
                ToggleContextMenu(_clipboardMenu, _clipboardButton, RefreshClipboardMenu);
            });

            _flipButton = CreateButton("Flip", (_, _) =>
            {
                _boardFlipped = !_boardFlipped;
                Invalidate();
                EmitSnapshot();
            });

            _mirrorButton = CreateButton("Mirror", (_, _) => ToggleMirrorMode());

            _bookToggleButton = CreateButton("Book", (_, _) => ToggleOpeningBook());

            _restoreSizeButton = CreateButton("Restore Window", (_, _) =>
            {
                Size = GetDefaultWindowSize(MinimumSize);
                SaveWindowSize();
            });

            _analysisWhiteButton = CreateAnalysisModeButton("W", "WHITE");
            _analysisBlackButton = CreateAnalysisModeButton("B", "BLACK");
            _analysisBothButton = CreateAnalysisModeButton("W+B", "BOTH");
            _analysisEngineLabel = CreateConfigLabel("Engine");
            _analysisDepthLabel = CreateConfigLabel("Depth");
            _analysisDepthValueLabel = CreateConfigLabel("12");
            _analysisDepthValueLabel.TextAlign = ContentAlignment.MiddleRight;
            _analysisLinesLabel = CreateConfigLabel("Lines");
            _analysisThreadsLabel = CreateConfigLabel("Threads");
            _analysisHashLabel = CreateConfigLabel("Hash");

            _analysisEngineComboBox = CreateSelectorComboBox();
            _analysisEngineComboBox.DropDown += (_, _) => RefreshAnalysisEngineList(preserveSelection: true);
            _analysisEngineComboBox.SelectedIndexChanged += (_, _) => ApplyAnalysisEngineSelectionFromCombo();

            _analysisLinesComboBox = CreateSelectorComboBox();
            _analysisLinesComboBox.SelectedIndexChanged += (_, _) => ApplyAnalysisLineSelectionFromCombo();

            _analysisThreadsComboBox = CreateSelectorComboBox();
            _analysisThreadsComboBox.SelectedIndexChanged += (_, _) => ApplyAnalysisThreadsSelectionFromCombo();

            _analysisHashComboBox = CreateSelectorComboBox();
            _analysisHashComboBox.SelectedIndexChanged += (_, _) => ApplyAnalysisHashSelectionFromCombo();

            _matchToggleButton = CreateButton("Start Match", (_, _) => RequestToggleMatchRunning());
            _matchPauseButton = CreateButton("Pause", (_, _) => MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.PauseResume));
            _matchRestartGameButton = CreateButton("Restart Game", (_, _) =>
            {
                if (MessageBox.Show(this, "Restart the current engine game?", "Restart Game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.RestartGame);
            });
            _matchRestartMatchButton = CreateButton("Restart Match", (_, _) =>
            {
                if (MessageBox.Show(this, "Restart the whole match and clear the current game?", "Restart Match", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.RestartMatch);
            });
            _matchResetScoreButton = CreateButton("Reset Score", (_, _) =>
            {
                if (MessageBox.Show(this, "Reset the match score?", "Reset Score", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.ResetScore);
            });
            _matchCopyGamePgnButton = CreateButton("Game PGN", (_, _) =>
            {
                ToggleContextMenu(_matchGamePgnMenu, _matchCopyGamePgnButton, RefreshMatchGamePgnMenu);
            });
            _matchCopyMatchPgnButton = CreateButton("Match PGN", (_, _) => CopyMatchPgnToClipboard());
            _matchCopyMatchPgnButton.Enabled = BuildLimits.AllowMatchPgnExport;
            _matchClearPgnButton = CreateButton("Clear PGNs", (_, _) =>
            {
                if (_archivedMatchGames.Count == 0)
                {
                    SetMatchStatus("No saved games to clear.");
                    return;
                }

                if (MessageBox.Show(this, "Clear all saved match games from memory?", "Clear PGNs", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    ClearMatchPgnArchive();
                    SetMatchStatus("Saved games cleared.");
                }
            });
            _matchAnalyzeGameButton = CreateButton("Analyze Game", (_, _) =>
            {
                try
                {
                    GameAnalysisRequest request = BuildCurrentGameAnalysisRequest();
                    GameAnalysisRequested?.Invoke(request);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Analyze Game", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });

            _matchWhiteEngineLabel = CreateConfigLabel("White");
            _matchWhiteEngineButton = CreateButton("White Engine", (_, _) =>
            {
                ToggleContextMenu(_matchWhiteEngineMenu, _matchWhiteEngineButton, RefreshMatchEngineMenus);
            });
            _matchWhiteEngineButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _matchWhiteEngineButton.TextAlign = ContentAlignment.MiddleLeft;
            _matchWhiteEngineMenu = CreateDarkMenu();

            _matchBlackEngineLabel = CreateConfigLabel("Black");
            _matchBlackEngineButton = CreateButton("Black Engine", (_, _) =>
            {
                ToggleContextMenu(_matchBlackEngineMenu, _matchBlackEngineButton, RefreshMatchEngineMenus);
            });
            _matchBlackEngineButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _matchBlackEngineButton.TextAlign = ContentAlignment.MiddleLeft;
            _matchBlackEngineMenu = CreateDarkMenu();

            _matchTimeLabel = CreateConfigLabel("Time");
            _matchTimeLabel.TextAlign = ContentAlignment.MiddleRight;
            _matchTimeButton = CreateButton("3 min", (_, _) =>
            {
                ToggleContextMenu(_matchTimeMenu, _matchTimeButton, RefreshMatchTimeMenu);
            });
            _matchTimeButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _matchTimeMenu = CreateDarkMenu();
            _matchLengthLabel = CreateConfigLabel("Games");
            _matchLengthLabel.TextAlign = ContentAlignment.MiddleRight;
            _matchLengthButton = CreateButton("Unlimited", (_, _) =>
            {
                ToggleContextMenu(_matchLengthMenu, _matchLengthButton, RefreshMatchLengthMenu);
            });
            _matchLengthButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _matchLengthMenu = CreateDarkMenu();
            _matchGamePgnMenu = CreateDarkMenu();
            _clipboardMenu = CreateDarkMenu();
            _clipboardMenu.Opening += (_, _) => RefreshClipboardMenu();

            _analysisDepthSlider = new TrackBar
            {
                Minimum = 0,
                Maximum = GetAnalysisDepthSliderMaximum(),
                TickFrequency = 1,
                SmallChange = 1,
                LargeChange = 1,
                AutoSize = false,
                Height = 28,
                BackColor = Color.FromArgb(38, 38, 41)
            };
            _analysisDepthSlider.ValueChanged += (_, _) =>
            {
                int snapped = SnapDepth(_analysisDepthSlider.Value);
                if (_analysisDepthSlider.Value != snapped)
                {
                    _analysisDepthSlider.Value = snapped;
                    return;
                }

                _analysisInfinite = BuildLimits.AllowInfiniteAnalysis && snapped >= InfiniteDepthSliderValue;
                _analysisTargetDepth = _analysisInfinite ? BuildLimits.MaxDepth : BuildLimits.ClampDepth(snapped);
                SaveAnalysisSettings();
                RefreshAnalysisConfigButtons();
                AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
                Invalidate(_analysisPanelRect);
            };

            _analysisInfiniteCheckBox = new CheckBox
            {
                Text = "Infinite",
                AutoSize = false,
                Visible = false,
                Enabled = false,
                BackColor = Color.FromArgb(38, 38, 41),
                ForeColor = Color.FromArgb(225, 225, 225),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular)
            };

            _analysisEngineWatcher = new FileSystemWatcher(Path.Combine(AppContext.BaseDirectory, "engines"), "*.exe")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _analysisEngineWatcher.Created += (_, _) => QueueAnalysisEngineRefresh();
            _analysisEngineWatcher.Deleted += (_, _) => QueueAnalysisEngineRefresh();
            _analysisEngineWatcher.Renamed += (_, _) => QueueAnalysisEngineRefresh();
            _analysisEngineWatcher.Changed += (_, _) => QueueAnalysisEngineRefresh();

            _fenTextBox = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                Font = new Font("Consolas", 10f, FontStyle.Regular),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(36, 36, 38),
                ForeColor = Color.White
            };

            _turnLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(210, 210, 210),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _movesHeaderLabel = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(220, 220, 220),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                Text = "Moves",
                TextAlign = ContentAlignment.MiddleLeft
            };

            _movesGrid = new DataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeColumns = false,
                AllowUserToResizeRows = false,
                BackgroundColor = Color.FromArgb(32, 32, 34),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.None,
                ColumnHeadersVisible = false,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                ScrollBars = ScrollBars.Vertical,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                GridColor = Color.FromArgb(32, 32, 34),
                DefaultCellStyle = new DataGridViewCellStyle
                {
                    BackColor = Color.FromArgb(32, 32, 34),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(24, 120, 200),
                    SelectionForeColor = Color.White,
                    Font = new Font("Segoe UI", 10.5f, FontStyle.Regular),
                    Padding = new Padding(4, 2, 4, 2)
                }
            };
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", Width = 68, SortMode = DataGridViewColumnSortMode.NotSortable });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "White", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Black", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            _movesGrid.CellClick += (_, e) =>
            {
                if (_suppressGridSelection || e.RowIndex < 0)
                    return;

                if (_movesGrid.Rows[e.RowIndex].Tag is VariationLine variation)
                {
                    PromoteVariationToMainLine(variation);
                    return;
                }

                if (e.ColumnIndex < 1)
                    return;

                if (_movesGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Tag is int targetHistoryIndex &&
                    targetHistoryIndex >= 1 && targetHistoryIndex < _history.Count)
                {
                    JumpToHistory(targetHistoryIndex);
                }
            };
            _movesGrid.SelectionChanged += (_, _) => ApplyMoveGridSelectionToBoard();
            _movesGrid.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Right)
                {
                    StepHistory(1);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Left)
                {
                    StepHistory(-1);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Home)
                {
                    JumpToHistory(0);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.End)
                {
                    JumpToHistory(_history.Count - 1);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            _jumpStartButton = CreateNavButton("<<", (_, _) =>
            {
                if (_historyIndex == 0 && _history.Count > 1)
                    ClearMoveHistoryFromCurrentRoot();
                else
                    JumpToHistory(0);
            });
            _stepBackButton = CreateNavButton("<", (_, _) => StepHistory(-1));
            _stepForwardButton = CreateNavButton(">", (_, _) => StepHistory(1));
            _jumpEndButton = CreateNavButton(">>", (_, _) => JumpToHistory(_history.Count - 1));

            Controls.AddRange(new Control[]
            {
                _resetButton,
                _undoButton,
                _clipboardButton,
                _flipButton,
                _mirrorButton,
                _bookToggleButton,
                _restoreSizeButton,
                _analysisWhiteButton,
                _analysisBlackButton,
                _analysisBothButton,
                _analysisEngineLabel,
                _analysisEngineComboBox,
                _analysisDepthLabel,
                _analysisDepthValueLabel,
                _analysisDepthSlider,
                _analysisInfiniteCheckBox,
                _analysisLinesLabel,
                _analysisLinesComboBox,
                _analysisThreadsLabel,
                _analysisThreadsComboBox,
                _analysisHashLabel,
                _analysisHashComboBox,
                _matchToggleButton,
                _matchPauseButton,
                _matchRestartGameButton,
                _matchRestartMatchButton,
                _matchResetScoreButton,
                _matchCopyGamePgnButton,
                _matchCopyMatchPgnButton,
                _matchClearPgnButton,
                _matchAnalyzeGameButton,
                _matchWhiteEngineLabel,
                _matchWhiteEngineButton,
                _matchBlackEngineLabel,
                _matchBlackEngineButton,
                _matchTimeLabel,
                _matchTimeButton,
                _matchLengthLabel,
                _matchLengthButton,
                _turnLabel,
                _fenTextBox,
                _movesHeaderLabel,
                _movesGrid,
                _jumpStartButton,
                _stepBackButton,
                _stepForwardButton,
                _jumpEndButton
            });

            Resize += (_, _) =>
            {
                LayoutControls();
                EmitSnapshot();
                Invalidate();
            };
            ResizeEnd += (_, _) => SaveWindowSize();

            Move += (_, _) => EmitSnapshot();
            Shown += (_, _) =>
            {
                LayoutControls();
                SyncStatus();
                EmitSnapshot();
            };
            VisibleChanged += (_, _) => EmitSnapshot();
            FormClosing += AnalysisBoardForm_FormClosing;
            MouseDown += AnalysisBoardForm_MouseDown;
            MouseMove += AnalysisBoardForm_MouseMove;
            MouseUp += AnalysisBoardForm_MouseUp;

            LayoutControls();
            LoadAnalysisSettings();
            RefreshAnalysisEngineList(preserveSelection: true);
            RefreshMirrorButton();
            RefreshBookButton();
            RefreshAnalysisModeButtons();
            RefreshAnalysisConfigButtons();
            LoadMatchSettings();
            RefreshMatchConfigButtons();
            RefreshMatchEngineMenus();
            RefreshMatchTimeMenu();
            RefreshMatchLengthMenu();
            RefreshMatchGamePgnMenu();
            ResetHistory();
            SyncStatus();
        }

        public void ShowAnalysisBoard()
        {
            ResetInteractiveUiState();

            if (!Visible)
            {
                Show();
            }

            WindowState = FormWindowState.Normal;
            Enabled = true;
            UseWaitCursor = false;
            Cursor = Cursors.Default;
            Capture = false;
            ForceForegroundInteractiveShow();
            _uiWatchdogStopwatch.Restart();
            if (!_uiWatchdogTimer.Enabled)
            {
                _uiWatchdogTimer.Start();
            }

            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                Enabled = true;
                UseWaitCursor = false;
                Cursor = Cursors.Default;
                Capture = false;
                ForceForegroundInteractiveShow();
                Invalidate();
                Update();
                EmitSnapshot();
            }));

            EmitSnapshot();
        }

        public void RefreshExternalDetectionSnapshot()
        {
            if (IsDisposed)
                return;

            LayoutControls();
            Invalidate();
            Update();
            EmitSnapshot();
        }

        public Rectangle GetLiveBoardScreenBounds()
        {
            if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized)
                return Rectangle.Empty;

            return RectangleToScreen(_boardRect);
        }

        public Rectangle GetLiveWindowScreenBounds()
        {
            if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized)
                return Rectangle.Empty;

            return Bounds;
        }

        public bool TryTemporarilyMinimizeForExternalPriming()
        {
            if (IsDisposed || !Visible || WindowState == FormWindowState.Minimized)
                return false;

            ResetInteractiveUiState();
            WindowState = FormWindowState.Minimized;
            EmitSnapshot();
            return true;
        }

        public void RestoreAfterExternalPriming()
        {
            if (IsDisposed)
                return;

            WindowState = FormWindowState.Normal;
            Enabled = true;
            UseWaitCursor = false;
            Cursor = Cursors.Default;
            Capture = false;
            LayoutControls();
            Invalidate();
            Update();
            EmitSnapshot();
        }

        public bool MirrorModeEnabled => _mirrorModeEnabled;

        public void SetBoardFlipped(bool flipped)
        {
            if (_boardFlipped == flipped)
                return;

            _boardFlipped = flipped;
            Invalidate();
            EmitSnapshot();
        }

        public bool MirrorExternalFen(string fen, bool? boardFlipped = null)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return false;

            bool changed = false;
            if (boardFlipped.HasValue && _boardFlipped != boardFlipped.Value)
            {
                _boardFlipped = boardFlipped.Value;
                changed = true;
            }

            string currentFen = _board.ToFen();
            if (string.Equals(currentFen, fen, StringComparison.Ordinal))
            {
                if (changed)
                {
                    Invalidate();
                    EmitSnapshot();
                }

                return changed;
            }

            try
            {
                Move? mirroredMove = TryFindMoveForFenTransition(currentFen, fen);
                var mirroredBoard = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
                _board = mirroredBoard;
                ClearSelection();
                ResetDragState(clearSelection: false);
                if (mirroredMove != null && _history.Count > 0 && _historyIndex == _history.Count - 1)
                {
                    PushHistory(FormatMoveText(mirroredMove), mirroredMove);
                }
                else
                {
                    ResetHistory();
                }
                SyncStatus();
                ClearAnalysisArrows();
                ClearAnalysisVariations();
                Invalidate();
                EmitSnapshot();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void SetAnalysisArrows(IEnumerable<MoveArrow> arrows)
        {
            _analysisArrows = arrows.Take(BuildLimits.MaxLines).Select(a => new MoveArrow
            {
                FromFile = a.FromFile,
                FromRank = a.FromRank,
                ToFile = a.ToFile,
                ToRank = a.ToRank,
                Strength = a.Strength,
                IsFlipped = a.IsFlipped,
                PromotionPiece = a.PromotionPiece,
                MovingSide = a.MovingSide,
                Depth = a.Depth
            }).ToList();
            Invalidate(_boardRect);
        }

        public void SetFreeAnalysisLimitStatus(bool armed, int movesRemaining, int cooldownSeconds, bool inCooldown)
        {
            int nextMovesRemaining = Math.Max(0, movesRemaining);
            int nextCooldownSeconds = Math.Max(0, cooldownSeconds);
            bool nextInCooldown = armed && inCooldown;
            if (_freeAnalysisArmed == armed &&
                _freeAnalysisMovesRemaining == nextMovesRemaining &&
                _freeAnalysisCooldownSeconds == nextCooldownSeconds &&
                _freeAnalysisInCooldown == nextInCooldown)
            {
                return;
            }

            _freeAnalysisArmed = armed;
            _freeAnalysisMovesRemaining = nextMovesRemaining;
            _freeAnalysisCooldownSeconds = nextCooldownSeconds;
            _freeAnalysisInCooldown = nextInCooldown;
            Invalidate(_boardRect);
        }

        public void SetMirrorPausedHint(bool paused, string message)
        {
            string nextMessage = message ?? "";
            bool nextPaused = paused && !string.IsNullOrEmpty(nextMessage);
            if (_mirrorPaused == nextPaused &&
                string.Equals(_mirrorPausedMessage, nextMessage, StringComparison.Ordinal))
            {
                return;
            }

            _mirrorPaused = nextPaused;
            _mirrorPausedMessage = nextMessage;
            Invalidate(_boardRect);
        }

        public void SetAnalysisVariations(IEnumerable<MoveVariation>? variations, bool isBlackPerspective, int depth)
        {
            _analysisVariations = variations?.Take(BuildLimits.MaxLines).Select(v => new MoveVariation
            {
                Rank = v.Rank,
                Depth = v.Depth,
                Score = v.Score,
                ScoreType = v.ScoreType,
                MateIn = v.MateIn,
                Moves = v.Moves.ToList()
            }).ToList() ?? new List<MoveVariation>();
            _analysisIsBlackPerspective = isBlackPerspective;
            _analysisDepth = depth;
            Invalidate(_analysisPanelRect);
            Invalidate(_evalBarRect);
        }

        public void SetAnalysisStatus(string statusText)
        {
            string nextStatus = string.IsNullOrWhiteSpace(statusText)
                ? "Waiting for engine lines..."
                : statusText;

            if (string.Equals(_analysisStatusText, nextStatus, StringComparison.Ordinal))
                return;

            _analysisStatusText = nextStatus;
            Invalidate(_analysisPanelRect);
        }

        public AnalysisBoardAnalysisSettings GetAnalysisSettings()
        {
            return new AnalysisBoardAnalysisSettings
            {
                EngineName = _selectedAnalysisEngine?.Name ?? "Engine",
                EnginePath = _selectedAnalysisEngine?.ExecutablePath ?? "",
                Depth = BuildLimits.ClampDepth(_analysisTargetDepth),
                Infinite = BuildLimits.AllowInfiniteAnalysis && _analysisInfinite,
                LineCount = BuildLimits.ClampLines(_analysisLineCount),
                Threads = BuildLimits.ClampThreads(_analysisThreads),
                HashMb = BuildLimits.ClampHashMb(_analysisHashMb)
            };
        }

        public AnalysisBoardMatchSettings GetMatchSettings()
        {
            return new AnalysisBoardMatchSettings
            {
                WhiteEngineName = _selectedMatchWhiteEngine?.Name ?? "White Engine",
                WhiteEnginePath = _selectedMatchWhiteEngine?.ExecutablePath ?? "",
                BlackEngineName = _selectedMatchBlackEngine?.Name ?? "Black Engine",
                BlackEnginePath = _selectedMatchBlackEngine?.ExecutablePath ?? "",
                TimeControlKey = _matchTimeControlKey,
                BaseSeconds = BuildLimits.ClampMatchSeconds(_matchBaseSeconds),
                GameLimit = BuildLimits.ClampMatchGameLimit(_matchGameLimit),
                Threads = BuildLimits.ClampThreads(_analysisThreads),
                HashMb = BuildLimits.ClampHashMb(_analysisHashMb)
            };
        }

        public void SetMatchDisplay(long displayVersion, bool running, bool paused, string whiteClockText, string blackClockText, string leftScoreLabel, int whiteWins, string rightScoreLabel, int blackWins, int draws, string statusText, bool whiteSideIsCompetitorA = true)
        {
            if (displayVersion < _matchDisplayVersion)
                return;

            _matchDisplayVersion = displayVersion;
            _matchRunning = running;
            _matchPaused = paused;
            _matchWhiteClockText = whiteClockText;
            _matchBlackClockText = blackClockText;
            _matchScoreLeftLabel = string.IsNullOrWhiteSpace(leftScoreLabel) ? "White" : leftScoreLabel;
            _matchScoreRightLabel = string.IsNullOrWhiteSpace(rightScoreLabel) ? "Black" : rightScoreLabel;
            _matchWhiteWins = Math.Max(0, whiteWins);
            _matchBlackWins = Math.Max(0, blackWins);
            _matchWhiteSideIsCompetitorA = whiteSideIsCompetitorA;
            _matchDraws = Math.Max(0, draws);
            _matchStatusText = string.IsNullOrWhiteSpace(statusText) ? "Ready for engine match." : statusText;
            SaveMatchSettings();
            RefreshMatchConfigButtons();
            Invalidate(_sidebarRect);
        }

        public void ArchiveCurrentMatchGame(string whiteName, string blackName, string result, string timeControlKey)
        {
            var moveTexts = GetCurrentGameMoveTextsForPgn(includeResultSuffix: false);
            if (moveTexts.Count == 0)
                return;

            _archivedMatchGames.Add(new ArchivedMatchGame
            {
                WhiteName = string.IsNullOrWhiteSpace(whiteName) ? "White" : whiteName,
                BlackName = string.IsNullOrWhiteSpace(blackName) ? "Black" : blackName,
                Result = NormalizePgnResult(result),
                TimeControlKey = string.IsNullOrWhiteSpace(timeControlKey) ? "3 min" : timeControlKey,
                SavedAtUtc = DateTime.UtcNow,
                MoveTexts = moveTexts
            });

            RefreshMatchGamePgnMenu();
            Invalidate(_matchPanelRect);
        }

        public void ClearMatchPgnArchive()
        {
            _archivedMatchGames.Clear();
            RefreshMatchGamePgnMenu();
            Invalidate(_matchPanelRect);
        }

        public void ResetMatchScoreDisplay(long displayVersion, bool running, bool paused, string whiteClockText, string blackClockText, string leftScoreLabel, string rightScoreLabel, string statusText)
        {
            _matchDisplayVersion = displayVersion;
            _matchRunning = running;
            _matchPaused = paused;
            _matchWhiteClockText = whiteClockText;
            _matchBlackClockText = blackClockText;
            _matchScoreLeftLabel = string.IsNullOrWhiteSpace(leftScoreLabel) ? "White" : leftScoreLabel;
            _matchScoreRightLabel = string.IsNullOrWhiteSpace(rightScoreLabel) ? "Black" : rightScoreLabel;
            _matchWhiteWins = 0;
            _matchBlackWins = 0;
            _matchWhiteSideIsCompetitorA = true;
            _matchDraws = 0;
            _matchStatusText = string.IsNullOrWhiteSpace(statusText) ? "Ready for engine match." : statusText;
            SaveMatchSettings();
            RefreshMatchConfigButtons();
            Invalidate(_sidebarRect);
            Update();
            Refresh();
        }

        public void SwapMatchEngineSelections()
        {
            (_selectedMatchWhiteEngine, _selectedMatchBlackEngine) = (_selectedMatchBlackEngine, _selectedMatchWhiteEngine);
            SaveMatchSettings();
            RefreshMatchEngineMenus();
            RefreshMatchConfigButtons();
            MatchSettingsChanged?.Invoke(GetMatchSettings());
            Invalidate(_matchPanelRect);
        }

        public string? DetectCurrentMatchDrawStatus()
        {
            string currentFen = _board.ToFen();
            try
            {
                ChessBoard board = ChessBoard.LoadFromFen(currentFen, AutoEndgameRules.All);
                if (board.IsEndGame && board.EndGame?.WonSide == null)
                    return "½-½ Draw";
            }
            catch
            {
                // Ignore draw probing failures and continue with history-based checks.
            }

            string[] fenParts = currentFen.Split(' ');
            if (fenParts.Length >= 5 && int.TryParse(fenParts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int halfmoveClock) && halfmoveClock >= 100)
                return "½-½ 50-move";

            string currentKey = NormalizeFenForRepetition(currentFen);
            if (!string.IsNullOrWhiteSpace(currentKey))
            {
                int repetitionCount = _history.Count(entry => string.Equals(NormalizeFenForRepetition(entry.Fen), currentKey, StringComparison.Ordinal));
                if (repetitionCount >= 3)
                    return "½-½ Repetition";
            }

            return null;
        }

        public bool TryGetTerminalAnalysisStatus(out string statusText)
        {
            statusText = string.Empty;

            try
            {
                ChessBoard board = ChessBoard.LoadFromFen(_board.ToFen(), AutoEndgameRules.All);
                bool hasLegalMoves = board.Moves(false, true).Any();
                if (!hasLegalMoves || board.IsEndGame)
                {
                    PieceColor? wonSide = board.EndGame?.WonSide;
                    if (wonSide == PieceColor.White)
                    {
                        statusText = "1-0 Checkmate";
                        return true;
                    }

                    if (wonSide == PieceColor.Black)
                    {
                        statusText = "0-1 Checkmate";
                        return true;
                    }

                    string? drawStatus = DetectCurrentMatchDrawStatus();
                    statusText = string.IsNullOrWhiteSpace(drawStatus) ? "½-½ Draw" : drawStatus;
                    return true;
                }
            }
            catch
            {
                // If terminal probing fails, just let analysis continue normally.
            }

            return false;
        }

        public void AppendResultToLatestMove(string resultSuffix)
        {
            if (string.IsNullOrWhiteSpace(resultSuffix) || _history.Count <= 1)
                return;

            int lastIndex = _history.Count - 1;
            HistoryEntry lastEntry = _history[lastIndex];
            string moveText = lastEntry.MoveText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(moveText) || moveText.Contains(resultSuffix, StringComparison.Ordinal))
                return;

            _history[lastIndex] = new HistoryEntry
            {
                Fen = lastEntry.Fen,
                MoveText = moveText + " " + resultSuffix,
                LastMoveFrom = lastEntry.LastMoveFrom,
                LastMoveTo = lastEntry.LastMoveTo
            };
            if (_historyIndex > lastIndex)
                _historyIndex = lastIndex;
            try
            {
                RefreshMoveGrid();
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[MATCH] AppendResultToLatestMove failed: {ex}");
            }
            Invalidate();
        }

        public string? GetCurrentGamePgn()
        {
            var moveTexts = GetCurrentGameMoveTextsForPgn(includeResultSuffix: false);
            if (moveTexts.Count == 0)
                return null;

            return BuildPgn(
                _selectedMatchWhiteEngine?.Name ?? "White",
                _selectedMatchBlackEngine?.Name ?? "Black",
                _matchTimeControlKey,
                "*",
                DateTime.UtcNow,
                moveTexts);
        }

        public bool CanAnalyzeCurrentGame()
        {
            return !_matchRunning && _history.Count > 1;
        }

        public GameAnalysisRequest BuildCurrentGameAnalysisRequest()
        {
            if (!CanAnalyzeCurrentGame())
                throw new InvalidOperationException("Stop the match and play or load a game before analyzing it.");

            var moves = new List<GameAnalysisMoveInput>();
            for (int i = 1; i < _history.Count; i++)
            {
                string moveText = StripResultSuffix(_history[i].MoveText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(moveText))
                    continue;

                moves.Add(new GameAnalysisMoveInput
                {
                    PlyIndex = i,
                    MoveNumber = ((i - 1) / 2) + 1,
                    IsWhiteMove = i % 2 == 1,
                    MoveText = moveText,
                    FenBefore = _history[i - 1].Fen,
                    FenAfter = _history[i].Fen
                });
            }

            if (moves.Count == 0)
                throw new InvalidOperationException("There are no moves to analyze yet.");

            string result = "*";
            if (TryGetTerminalAnalysisStatus(out string terminalStatus))
                result = NormalizePgnResult(terminalStatus);

            return new GameAnalysisRequest
            {
                WhiteName = _loadedGameMetadataActive ? _loadedGameWhiteName : (_selectedMatchWhiteEngine?.Name ?? "White"),
                BlackName = _loadedGameMetadataActive ? _loadedGameBlackName : (_selectedMatchBlackEngine?.Name ?? "Black"),
                Result = _loadedGameMetadataActive && !string.IsNullOrWhiteSpace(_loadedGameResult) && _loadedGameResult != "*"
                    ? _loadedGameResult
                    : result,
                TimeControlKey = _loadedGameMetadataActive ? _loadedGameTimeControlKey : _matchTimeControlKey,
                Moves = moves
            };
        }

        public void ApplyGameAnalysisResults(IEnumerable<GameAnalysisMoveResult>? results)
        {
            _moveAnalysisByPly.Clear();

            if (results != null)
            {
                foreach (GameAnalysisMoveResult result in results)
                {
                    if (result.PlyIndex <= 0 || result.PlyIndex >= _history.Count)
                        continue;

                    HistoryEntry after = _history[result.PlyIndex];
                    HistoryEntry before = _history[result.PlyIndex - 1];
                    bool matchesCurrentHistory =
                        string.Equals(before.Fen, result.FenBefore, StringComparison.Ordinal) &&
                        string.Equals(after.Fen, result.FenAfter, StringComparison.Ordinal);

                    if (matchesCurrentHistory)
                        _moveAnalysisByPly[result.PlyIndex] = result;
                }
            }

            RefreshMoveGrid();
            ApplyStoredGameAnalysisPreviewForCurrentHistory();
            Invalidate();
        }

        public void PreviewGameAnalysisMove(GameAnalysisMoveResult analysis)
        {
            if (analysis.PlyIndex <= 0 || analysis.PlyIndex >= _history.Count)
                return;

            if (analysis.PlyIndex == _historyIndex)
            {
                ApplyStoredGameAnalysisPreviewForCurrentHistory();
                Invalidate();
                return;
            }

            JumpToHistory(analysis.PlyIndex);
        }

        public void ResetBoardToInitialPosition()
        {
            ClearLoadedGameMetadata();
            _board = CreateNewBoard();
            ClearSelection();
            ResetDragState(clearSelection: false);
            ResetHistory();
            SyncStatus();
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            Invalidate();
            EmitSnapshot();
        }

        public bool TryApplyEngineMove(string uciMove, out string moveText)
        {
            moveText = string.Empty;
            if (string.IsNullOrWhiteSpace(uciMove))
                return false;

            try
            {
                var legalMoves = _board.Moves(false, true);
                var selectedMove = legalMoves.FirstOrDefault(m => string.Equals(ToUciMove(m), uciMove, StringComparison.OrdinalIgnoreCase));
                if (selectedMove == null)
                    return false;

                moveText = FormatMoveText(selectedMove);
                _board.Move(selectedMove);
                PushHistory(moveText, selectedMove);
                ClearSelection();
                SyncStatus();
                Invalidate();
                EmitSnapshot();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ClearAnalysisArrows()
        {
            if (_analysisArrows.Count == 0)
                return;

            _analysisArrows.Clear();
            Invalidate(_boardRect);
        }

        public void ClearAnalysisVariations()
        {
            if (_analysisVariations.Count == 0 && _analysisDepth == 0)
                return;

            _analysisVariations.Clear();
            _analysisDepth = 0;
            Invalidate(_analysisPanelRect);
            Invalidate(_evalBarRect);
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            // The form does manual layout (LayoutControls) and custom GDI drawing
            // sized off DeviceDpi, so re-lay-out and repaint when the window moves
            // to a monitor at a different scaling.
            LayoutControls();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var panelBrush = new SolidBrush(Color.FromArgb(32, 32, 34));
            using var borderPen = new Pen(Color.FromArgb(70, 90, 90, 95), 1);
            using var boardBorderPen = new Pen(Color.FromArgb(110, 130, 130, 130), 1);

            Rectangle boardPanel = GetBoardPanelRect();
            e.Graphics.FillRectangle(panelBrush, boardPanel);
            e.Graphics.DrawRectangle(borderPen, boardPanel);

            DrawBoardInfoBars(e.Graphics);
            DrawBoard(e.Graphics);
            e.Graphics.DrawRectangle(boardBorderPen, _boardRect);
            DrawInlineEvalBar(e.Graphics, _evalBarRect);

            if (!_sidebarRect.IsEmpty)
            {
                e.Graphics.FillRectangle(panelBrush, _sidebarRect);
                e.Graphics.DrawRectangle(borderPen, _sidebarRect);
                DrawAnalysisPanel(e.Graphics);
                DrawMatchPanel(e.Graphics);
                DrawOpeningBookPanel(e.Graphics);
            }
        }

        private Rectangle GetBoardPanelRect()
        {
            if (_boardRect.IsEmpty)
                return Rectangle.Empty;

            Rectangle contentRect = _boardTopInfoRect.IsEmpty || _boardBottomInfoRect.IsEmpty
                ? _boardRect
                : Rectangle.FromLTRB(_boardRect.Left, _boardTopInfoRect.Top, _boardRect.Right, _boardBottomInfoRect.Bottom);

            return Rectangle.Inflate(contentRect, 10, 10);
        }

        private void DrawBoard(Graphics g)
        {
            var previousInterpolation = g.InterpolationMode;
            var previousPixelOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int squareSize = _boardRect.Width / 8;
            Position? checkedKingSquare = GetCheckedKingSquare(out bool isCheckmate);

            for (int displayRank = 0; displayRank < 8; displayRank++)
            {
                for (int displayFile = 0; displayFile < 8; displayFile++)
                {
                    int x = _boardRect.Left + displayFile * squareSize;
                    int y = _boardRect.Top + displayRank * squareSize;
                    Rectangle squareRect = new Rectangle(x, y, squareSize, squareSize);
                    bool isLight = ((displayFile + displayRank) % 2) == 0;

                    using (var squareBrush = new SolidBrush(isLight ? _lightSquare : _darkSquare))
                    {
                        g.FillRectangle(squareBrush, squareRect);
                    }

                    Position boardPosition = DisplayToBoardPosition(displayFile, displayRank);
                    bool isLastMoveSquare = TryGetLastMoveSquares(out var lastMoveFrom, out var lastMoveTo) &&
                        ((lastMoveFrom.HasValue && boardPosition == lastMoveFrom.Value) ||
                         (lastMoveTo.HasValue && boardPosition == lastMoveTo.Value));
                    bool isDragOrigin = _isDraggingPiece && _dragOriginSquare.HasValue && _dragOriginSquare.Value == boardPosition;
                    bool isLegalTarget = _selectedMoves.Any(m => m.NewPosition == boardPosition);
                    bool isDragHoverTarget = _isDraggingPiece && _dragHoverSquare.HasValue && _dragHoverSquare.Value == boardPosition && isLegalTarget;

                    if (checkedKingSquare.HasValue && boardPosition == checkedKingSquare.Value)
                    {
                        DrawKingDangerOverlay(g, squareRect, isCheckmate);
                    }

                    if (isLastMoveSquare)
                    {
                        using var lastMoveBrush = new SolidBrush(_highlightSquare);
                        g.FillRectangle(lastMoveBrush, squareRect);
                    }

                    if (isDragOrigin)
                    {
                        using var dragOriginBrush = new SolidBrush(_dragOriginOverlay);
                        g.FillRectangle(dragOriginBrush, squareRect);
                    }
                    else if (_selectedSquare.HasValue && _selectedSquare.Value == boardPosition)
                    {
                        using var highlightBrush = new SolidBrush(_highlightSquare);
                        g.FillRectangle(highlightBrush, squareRect);
                    }

                    if (isLegalTarget)
                    {
                        DrawMoveTargetHint(g, squareRect, squareSize, boardPosition, isDragHoverTarget);
                    }

                    var piece = _board[boardPosition];
                    if (piece != null)
                    {
                        if (isDragOrigin)
                        {
                            DrawGhostPiece(g, piece, squareRect);
                            continue;
                        }

                        DrawPiece(g, piece, squareRect);
                    }
                }
            }

            if (_analysisArrows.Count > 0)
            {
                foreach (var arrow in _analysisArrows.OrderByDescending(a => a.Strength))
                {
                    DrawAnalysisArrow(g, arrow, squareSize);
                    DrawAnalysisPromotionHint(g, arrow, squareSize);
                }
            }

            if (_mirrorPaused)
                DrawMirrorPausedHint(g);
            else if (_analysisArrows.Count > 0 || _freeAnalysisInCooldown)
                DrawFreeAnalysisWatermark(g);

            DrawManualBoardAnnotations(g, squareSize);

            if (_isDraggingPiece && _dragPiece != null)
            {
                DrawFloatingPiece(g, _dragPiece, _dragLocation, squareSize);
            }

            DrawBoardCoordinates(g, squareSize);
            DrawMoveClassificationBadge(g, squareSize);

            g.InterpolationMode = previousInterpolation;
            g.PixelOffsetMode = previousPixelOffset;
        }

        private void DrawFreeAnalysisWatermark(Graphics g)
        {
            if (!_freeAnalysisArmed || _boardRect.Width < 120 || _boardRect.Height < 120)
                return;

            // Server-driven copy: remaining moves while serving, a ticking M:SS
            // reset countdown during a cooldown.
            string text = _freeAnalysisInCooldown
                ? $"Free limit reached · resets in {FreeTierServerState.FormatCooldown(_freeAnalysisCooldownSeconds)}"
                : _freeAnalysisMovesRemaining == 1
                    ? "FREE · 1 move left"
                    : $"FREE · {_freeAnalysisMovesRemaining.ToString(CultureInfo.InvariantCulture)} moves left";

            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                float fontSize = Math.Clamp(_boardRect.Width * (_freeAnalysisInCooldown ? 0.043f : 0.038f), 16f, 34f);
                using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF textSize = g.MeasureString(text, font);
                var badge = new RectangleF(
                    _boardRect.Left + (_boardRect.Width - textSize.Width) / 2f,
                    _boardRect.Top + (_boardRect.Height - textSize.Height) / 2f,
                    textSize.Width,
                    textSize.Height);

                int fillAlpha = _freeAnalysisInCooldown ? 238 : 218;
                int edgeAlpha = _freeAnalysisInCooldown ? 42 : 32;
                using var edgeBrush = new SolidBrush(Color.FromArgb(edgeAlpha, 255, 255, 255));
                using var textBrush = new SolidBrush(Color.FromArgb(fillAlpha, 4, 5, 7));
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };

                g.DrawString(text, font, edgeBrush, new RectangleF(badge.X + 1f, badge.Y + 1f, badge.Width, badge.Height), format);
                g.DrawString(text, font, edgeBrush, new RectangleF(badge.X - 1f, badge.Y - 1f, badge.Width, badge.Height), format);
                g.DrawString(text, font, textBrush, badge, format);
            }
            finally
            {
                g.Restore(state);
            }
        }

        // Centered amber banner shown when mirror mode is on but the source board
        // can't be read (covered by another window, or none detected yet). The
        // screen capture only sees what is visually on top, so a covered source
        // silently stops feeding the mirror — this tells the user why.
        private void DrawMirrorPausedHint(Graphics g)
        {
            if (string.IsNullOrEmpty(_mirrorPausedMessage) || _boardRect.Width < 120 || _boardRect.Height < 120)
                return;

            string text = _mirrorPausedMessage;
            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                float fontSize = Math.Clamp(_boardRect.Width * 0.034f, 14f, 24f);
                using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };

                int maxTextWidth = (int)(_boardRect.Width * 0.82f);
                SizeF textSize = g.MeasureString(text, font, maxTextWidth, format);

                var badge = new RectangleF(
                    _boardRect.Left + (_boardRect.Width - textSize.Width) / 2f - 16f,
                    _boardRect.Top + (_boardRect.Height - textSize.Height) / 2f - 12f,
                    textSize.Width + 32f,
                    textSize.Height + 24f);

                using var bgBrush = new SolidBrush(Color.FromArgb(232, 18, 20, 24));
                using var borderPen = new Pen(Color.FromArgb(170, 240, 190, 90), 1.5f);
                g.FillRectangle(bgBrush, badge);
                g.DrawRectangle(borderPen, badge.X, badge.Y, badge.Width, badge.Height);

                using var textBrush = new SolidBrush(Color.FromArgb(245, 245, 205, 120));
                var textRect = new RectangleF(
                    _boardRect.Left + (_boardRect.Width - textSize.Width) / 2f,
                    _boardRect.Top + (_boardRect.Height - textSize.Height) / 2f,
                    textSize.Width,
                    textSize.Height);
                g.DrawString(text, font, textBrush, textRect, format);
            }
            finally
            {
                g.Restore(state);
            }
        }

        private void DrawBoardInfoBars(Graphics g)
        {
            if (_boardTopInfoRect.IsEmpty || _boardBottomInfoRect.IsEmpty)
                return;

            PieceColor topSide = _boardFlipped ? PieceColor.White : PieceColor.Black;
            PieceColor bottomSide = _boardFlipped ? PieceColor.Black : PieceColor.White;
            DrawBoardInfoBar(g, _boardTopInfoRect, topSide, isTop: true);
            DrawBoardInfoBar(g, _boardBottomInfoRect, bottomSide, isTop: false);
        }

        private void DrawBoardInfoBar(Graphics g, Rectangle rect, PieceColor side, bool isTop)
        {
            using var backBrush = new SolidBrush(Color.FromArgb(32, 32, 34));
            using var separatorPen = new Pen(Color.FromArgb(64, 70, 78), 1);
            using var nameBrush = new SolidBrush(Color.FromArgb(236, 236, 240));
            using var mutedBrush = new SolidBrush(Color.FromArgb(162, 166, 174));
            using var diskBorderPen = new Pen(Color.FromArgb(125, 130, 138), 1);
            using var nameFont = new Font("Segoe UI Semibold", 9.6f, FontStyle.Bold);
            using var metaFont = new Font("Segoe UI", 8.3f, FontStyle.Regular);
            using var clockFont = new Font("Segoe UI Semibold", 9.4f, FontStyle.Bold);
            using var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            using var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            g.FillRectangle(backBrush, rect);
            if (isTop)
                g.DrawLine(separatorPen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
            else
                g.DrawLine(separatorPen, rect.Left, rect.Top, rect.Right, rect.Top);

            string name = GetDisplayPlayerName(side);
            string clock = GetBoardClockText(side);
            string meta = BuildBoardInfoMeta(side, isTop);

            int pad = 10;
            int diskSize = Math.Min(18, rect.Height - 14);
            Rectangle diskRect = new Rectangle(rect.Left + pad, rect.Top + (rect.Height - diskSize) / 2, diskSize, diskSize);
            using (var diskBrush = new SolidBrush(side == PieceColor.White ? Color.FromArgb(238, 238, 230) : Color.FromArgb(20, 20, 22)))
            {
                g.FillEllipse(diskBrush, diskRect);
            }
            g.DrawEllipse(diskBorderPen, diskRect);

            Rectangle nameRect = new Rectangle(diskRect.Right + 8, rect.Top + 2, rect.Width - pad * 2 - diskSize - 124, rect.Height - 4);
            Rectangle clockRect = new Rectangle(rect.Right - pad - 104, rect.Top + 5, 104, rect.Height - 10);
            Rectangle metaRect = new Rectangle(rect.Left + pad + Math.Min(220, rect.Width / 2), rect.Top + 2, rect.Width - pad * 2 - Math.Min(220, rect.Width / 2) - 112, rect.Height - 4);

            g.DrawString(name, nameFont, nameBrush, nameRect, sfLeft);
            if (!string.IsNullOrWhiteSpace(meta))
                g.DrawString(meta, metaFont, mutedBrush, metaRect, sfRight);

            using var clockPath = CreateRoundedRect(clockRect, 7);
            using var clockBrush = new SolidBrush(side == PieceColor.White ? Color.FromArgb(238, 238, 230) : Color.FromArgb(45, 46, 52));
            using var clockTextBrush = new SolidBrush(side == PieceColor.White ? Color.FromArgb(28, 28, 30) : Color.FromArgb(238, 238, 240));
            g.FillPath(clockBrush, clockPath);
            g.DrawPath(separatorPen, clockPath);
            using var clockFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(clock, clockFont, clockTextBrush, clockRect, clockFormat);
        }

        private string GetBoardClockText(PieceColor side)
        {
            string text = side == PieceColor.White ? _matchWhiteClockText : _matchBlackClockText;
            return string.IsNullOrWhiteSpace(text) ? "00:00" : text;
        }

        private string GetDisplayPlayerName(PieceColor side)
        {
            if (_loadedGameMetadataActive)
                return side == PieceColor.White ? _loadedGameWhiteName : _loadedGameBlackName;

            return side == PieceColor.White
                ? (_selectedMatchWhiteEngine?.Name ?? "White")
                : (_selectedMatchBlackEngine?.Name ?? "Black");
        }

        private string BuildBoardInfoMeta(PieceColor side, bool isTop)
        {
            if (_matchRunning || _matchPaused || IsMatchGameContext())
            {
                int completedGames = _matchWhiteWins + _matchBlackWins + _matchDraws;
                // _matchWhiteWins/_matchBlackWins are Competitor A/B scores; map them to the
                // engine actually playing this side so a score follows its engine, not the side.
                int whiteSideWins = _matchWhiteSideIsCompetitorA ? _matchWhiteWins : _matchBlackWins;
                int blackSideWins = _matchWhiteSideIsCompetitorA ? _matchBlackWins : _matchWhiteWins;
                int wins = side == PieceColor.White ? whiteSideWins : blackSideWins;
                int pct = completedGames > 0 ? (int)Math.Round((wins * 100.0) / completedGames) : 0;
                string limitText = _matchGameLimit > 0
                    ? $"   {completedGames.ToString(CultureInfo.InvariantCulture)}/{_matchGameLimit.ToString(CultureInfo.InvariantCulture)}"
                    : "";
                string score = $"Score {wins} ({pct}%)   Draws {_matchDraws}{limitText}";
                if (_matchPaused && isTop)
                    return $"Paused   {score}";
                return score;
            }

            if (_loadedGameMetadataActive)
            {
                if (isTop)
                    return string.IsNullOrWhiteSpace(_loadedGameTimeControlKey) ? "Imported PGN" : _loadedGameTimeControlKey;
                return string.IsNullOrWhiteSpace(_loadedGameResult) ? "Result *" : $"Result {_loadedGameResult}";
            }

            return isTop ? "Analysis board" : (_board.Turn == side ? "To move" : "");
        }

        private bool IsMatchGameContext()
        {
            return !_loadedGameMetadataActive &&
                   (_history.Count > 1 || _matchWhiteWins > 0 || _matchBlackWins > 0 || _matchDraws > 0);
        }

        private Position? GetCheckedKingSquare(out bool isCheckmate)
        {
            isCheckmate = false;

            if (_board.WhiteKingChecked)
            {
                isCheckmate = _board.IsEndGame && _board.EndGame?.WonSide == PieceColor.Black;
                return _board.WhiteKing;
            }

            if (_board.BlackKingChecked)
            {
                isCheckmate = _board.IsEndGame && _board.EndGame?.WonSide == PieceColor.White;
                return _board.BlackKing;
            }

            return null;
        }

        private void DrawAnalysisPanel(Graphics g)
        {
            if (_analysisPanelRect.IsEmpty)
                return;

            using var panelBrush = new SolidBrush(Color.FromArgb(38, 38, 41));
            using var borderPen = new Pen(Color.FromArgb(65, 80, 80, 86), 1);
            using var titleBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
            using var mutedBrush = new SolidBrush(Color.FromArgb(155, 155, 160));
            using var lineBrush = new SolidBrush(Color.FromArgb(225, 225, 225));
            using var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            using var lineFont = new Font("Consolas", 9.5f, FontStyle.Regular);
            using var smallFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);

            g.FillRectangle(panelBrush, _analysisPanelRect);
            g.DrawRectangle(borderPen, _analysisPanelRect);

            int pad = 10;
            string scoreText = _analysisVariations.Count > 0
                ? FormatEvaluationText(_analysisVariations[0], _analysisIsBlackPerspective)
                : "0.00";
            string depthText = _analysisDepth > 0
                ? _analysisDepth.ToString(CultureInfo.InvariantCulture)
                : (_analysisInfinite ? "\u221E" : _analysisTargetDepth.ToString(CultureInfo.InvariantCulture));
            string title = _analysisVariations.Count > 0
                ? $"{scoreText}   Depth {depthText}"
                : $"Analysis   Depth {(_analysisInfinite ? "\u221E" : depthText)}";
            g.DrawString(title, titleFont, titleBrush, _analysisPanelRect.Left + pad, _analysisPanelRect.Top + 8);

            int textLeft = _analysisPanelRect.Left + pad;
            int controlBottom = Math.Max(
                _analysisEngineComboBox.Bottom,
                Math.Max(_analysisThreadsComboBox.Bottom, _analysisHashComboBox.Bottom));
            int y = controlBottom + 12;
            Rectangle textRect = new Rectangle(textLeft, y, _analysisPanelRect.Width - pad * 2, Math.Max(24, _analysisPanelRect.Bottom - y - pad));

            if (_analysisVariations.Count == 0)
            {
                g.DrawString(_analysisStatusText, smallFont, mutedBrush, textRect);
                return;
            }

            Region oldClip = g.Clip;
            g.SetClip(textRect);
            int lineHeight = Math.Max(22, TextHeight(lineFont) + 2);
            foreach (var variation in _analysisVariations.Take(_analysisLineCount))
            {
                string score = FormatEvaluationText(variation, _analysisIsBlackPerspective);
                string pv = variation.Moves.Count == 0
                    ? "-"
                    : string.Join(" ", variation.Moves.Take(4));
                string text = $"{variation.Rank}. {score,-6} {pv}";
                g.DrawString(text, lineFont, lineBrush, textLeft, y);
                y += lineHeight;
            }
            g.Clip = oldClip;
        }

        private void DrawOpeningBookPanel(Graphics g)
        {
            if (!_openingBookEnabled || _bookPanelRect.IsEmpty)
                return;

            SmoothingMode oldSmoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var panelPath = CreateRoundedRect(_bookPanelRect, 8);
            using var panelBrush = new SolidBrush(Color.FromArgb(34, 35, 39));
            using var borderPen = new Pen(Color.FromArgb(76, 82, 90), 1);
            using var titleBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
            using var mutedBrush = new SolidBrush(Color.FromArgb(165, 168, 176));
            using var lineBrush = new SolidBrush(Color.FromArgb(225, 225, 225));
            using var rowBrush = new SolidBrush(Color.FromArgb(42, 44, 50));
            using var altRowBrush = new SolidBrush(Color.FromArgb(38, 40, 46));
            using var movePillBrush = new SolidBrush(Color.FromArgb(42, 68, 92));
            using var accentBrush = new SolidBrush(Color.FromArgb(103, 169, 255));
            using var titleFont = new Font("Segoe UI Semibold", 11.2f, FontStyle.Bold);
            using var smallFont = new Font("Segoe UI", 8.8f, FontStyle.Regular);
            using var headerFont = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);
            using var moveFont = new Font("Segoe UI Semibold", 9.6f, FontStyle.Bold);
            using var valueFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);

            g.FillPath(panelBrush, panelPath);
            g.DrawPath(borderPen, panelPath);

            int pad = Math.Max(12, Math.Min(16, _bookPanelRect.Width / 32));
            int x = _bookPanelRect.Left + pad;
            int y = _bookPanelRect.Top + 12;
            int innerRight = _bookPanelRect.Right - pad;
            g.DrawString("Opening Book", titleFont, titleBrush, x, y);
            y += titleFont.Height + 6;

            if (!string.IsNullOrWhiteSpace(_bookOpeningTitle))
            {
                g.DrawString(TrimToWidth(g, _bookOpeningTitle, smallFont, _bookPanelRect.Width - pad * 2), smallFont, mutedBrush, x, y);
                y += smallFont.Height + 10;
            }
            else
            {
                y += 8;
            }

            if (_bookMoves.Count == 0)
            {
                g.DrawString(_bookStatusText, smallFont, mutedBrush, x, y);
                g.SmoothingMode = oldSmoothing;
                return;
            }

            int moveX = x;
            int rowHeight = Math.Max(24, moveFont.Height + 8);
            int rowGap = 5;
            using var rightAlign = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            using var leftAlign = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            const string popularityHeader = "Popularity";
            const string compactPopularityHeader = "Pop.";
            float popularityHeaderWidth = g.MeasureString(popularityHeader, headerFont).Width;
            float compactPopularityHeaderWidth = g.MeasureString(compactPopularityHeader, headerFont).Width;
            float linesHeaderWidth = g.MeasureString("Lines", headerFont).Width;
            float maxPopularityValueWidth = _bookMoves
                .Take(6)
                .Select(move => g.MeasureString(move.Popularity.ToString(CultureInfo.InvariantCulture), valueFont).Width)
                .DefaultIfEmpty(0f)
                .Max();
            float maxLinesValueWidth = _bookMoves
                .Take(6)
                .Select(move => g.MeasureString(move.LineCount.ToString(CultureInfo.InvariantCulture), valueFont).Width)
                .DefaultIfEmpty(0f)
                .Max();

            int minGap = Math.Max(12, _bookPanelRect.Width / 44);
            int linesWidth = (int)Math.Ceiling(Math.Max(linesHeaderWidth, maxLinesValueWidth)) + 8;
            int popularityWidth = (int)Math.Ceiling(Math.Max(popularityHeaderWidth, maxPopularityValueWidth)) + 8;
            int compactPopularityWidth = (int)Math.Ceiling(Math.Max(compactPopularityHeaderWidth, maxPopularityValueWidth)) + 8;
            int moveWidth = Math.Max(54, Math.Min(82, _bookPanelRect.Width / 6));
            int linesX = innerRight - linesWidth;
            int popularityX = linesX - minGap - popularityWidth;
            int popBarLeft = moveX + moveWidth + minGap;
            int popBarRight = popularityX - minGap;
            bool showBars = popBarRight - popBarLeft >= 44;
            bool useCompactPopularityHeader = popularityX - popBarLeft < 86;

            if (useCompactPopularityHeader)
            {
                popularityWidth = compactPopularityWidth;
                popularityX = linesX - minGap - popularityWidth;
                popBarRight = popularityX - minGap;
                showBars = popBarRight - popBarLeft >= 44;
            }

            if (!showBars)
            {
                moveWidth = Math.Max(46, Math.Min(64, _bookPanelRect.Width / 7));
                popBarLeft = moveX + moveWidth + 8;
                useCompactPopularityHeader = true;
                popularityWidth = compactPopularityWidth;
                popularityX = linesX - minGap - popularityWidth;
                popBarRight = popularityX - minGap;
                showBars = popBarRight - popBarLeft >= 40;
            }

            g.DrawString("Move", headerFont, mutedBrush, new RectangleF(moveX, y, moveWidth, 20), leftAlign);
            g.DrawString(useCompactPopularityHeader ? compactPopularityHeader : popularityHeader, headerFont, mutedBrush, new RectangleF(popularityX, y, popularityWidth, 20), rightAlign);
            g.DrawString("Lines", headerFont, mutedBrush, new RectangleF(linesX, y, linesWidth, 20), rightAlign);
            y += 23;

            int maxPopularity = Math.Max(1, _bookMoves.Max(move => move.Popularity));
            int maxRows = Math.Max(1, (_bookPanelRect.Bottom - y - pad + rowGap) / (rowHeight + rowGap));
            int rowIndex = 0;
            foreach (var move in _bookMoves.Take(maxRows))
            {
                var rowRect = new Rectangle(_bookPanelRect.Left + 10, y, _bookPanelRect.Width - 20, rowHeight);
                using (var rowPath = CreateRoundedRect(rowRect, 5))
                {
                    g.FillPath(rowIndex % 2 == 0 ? rowBrush : altRowBrush, rowPath);
                }

                var moveRect = new RectangleF(moveX, y + 4, moveWidth, rowHeight - 8);
                using (var movePath = CreateRoundedRect(moveRect, 4f))
                {
                    g.FillPath(movePillBrush, movePath);
                }

                string san = TrimToWidth(g, move.San, moveFont, moveWidth - 10);
                g.DrawString(san, moveFont, lineBrush, new RectangleF(moveRect.X + 7, y, moveWidth - 12, rowHeight), leftAlign);

                int barY = y + 11;
                if (showBars)
                {
                    int barWidth = popBarRight - popBarLeft;
                    using var trackPen = new Pen(Color.FromArgb(72, 76, 84), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    using var fillPen = new Pen(Color.FromArgb(103, 169, 255), 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(trackPen, popBarLeft, barY, popBarRight, barY);
                    int filledRight = popBarLeft + Math.Max(5, (int)Math.Round(barWidth * (move.Popularity / (double)maxPopularity)));
                    g.DrawLine(fillPen, popBarLeft, barY, filledRight, barY);
                }

                g.DrawString(move.Popularity.ToString(CultureInfo.InvariantCulture), valueFont, lineBrush, new RectangleF(popularityX, y + 3, popularityWidth, rowHeight), rightAlign);
                g.DrawString(move.LineCount.ToString(CultureInfo.InvariantCulture), valueFont, mutedBrush, new RectangleF(linesX, y + 3, linesWidth, rowHeight), rightAlign);
                y += rowHeight + rowGap;
                rowIndex++;
            }

            g.SmoothingMode = oldSmoothing;
        }

        private void DrawMatchPanel(Graphics g)
        {
            if (_matchPanelRect.IsEmpty)
                return;

            using var panelBrush = new SolidBrush(Color.FromArgb(36, 36, 39));
            using var borderPen = new Pen(Color.FromArgb(65, 80, 80, 86), 1);
            using var titleBrush = new SolidBrush(Color.FromArgb(230, 230, 230));
            using var mutedBrush = new SolidBrush(Color.FromArgb(160, 160, 166));
            using var titleFont = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
            using var smallFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);

            g.FillRectangle(panelBrush, _matchPanelRect);
            g.DrawRectangle(borderPen, _matchPanelRect);

            int pad = 10;
            int x = _matchPanelRect.Left + pad;
            int y = _matchPanelRect.Top + 8;
            g.DrawString("Engine Match", titleFont, titleBrush, x, y);

            int statusY = _matchPanelRect.Top + 48;
            using var statusFormat = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Near
            };
            g.DrawString(_matchStatusText, smallFont, mutedBrush, new RectangleF(x, statusY, _matchPanelRect.Width - pad * 2, 34), statusFormat);
        }

        private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DrawInlineEvalBar(Graphics g, Rectangle barRect)
        {
            if (barRect.IsEmpty)
                return;

            using var borderPen = new Pen(Color.FromArgb(90, 95, 95, 100), 1);
            using var whiteBrush = new SolidBrush(Color.FromArgb(238, 238, 230));
            using var blackBrush = new SolidBrush(Color.FromArgb(35, 35, 38));
            using var neutralPen = new Pen(Color.FromArgb(140, 120, 120, 125), 1);

            double displayEval = 0;
            var best = _analysisVariations.FirstOrDefault();
            if (best != null)
            {
                displayEval = best.ScoreType == "mate"
                    ? ((best.MateIn ?? 0) >= 0 ? 8 : -8)
                    : Math.Clamp(best.Score, -8, 8);

                if (_analysisIsBlackPerspective)
                    displayEval = -displayEval;
            }

            double whiteRatio = 0.5 + Math.Tanh(displayEval / 4.0) * 0.45;
            int whiteHeight = (int)Math.Round(barRect.Height * whiteRatio);
            // The side at the bottom of the board fills up for its advantage: not
            // flipped => White at the bottom; flipped => Black at the bottom.
            int topHeight = _boardFlipped ? whiteHeight : barRect.Height - whiteHeight;
            Rectangle topRect = new Rectangle(barRect.Left, barRect.Top, barRect.Width, topHeight);
            Rectangle bottomRect = new Rectangle(barRect.Left, topRect.Bottom, barRect.Width, barRect.Height - topHeight);

            g.FillRectangle(_boardFlipped ? whiteBrush : blackBrush, topRect);
            g.FillRectangle(_boardFlipped ? blackBrush : whiteBrush, bottomRect);
            g.DrawRectangle(borderPen, barRect);
            int centerY = barRect.Top + barRect.Height / 2;
            g.DrawLine(neutralPen, barRect.Left, centerY, barRect.Right, centerY);

            if (best != null)
            {
                using var textFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
                string evalText = FormatEvaluationText(best, _analysisIsBlackPerspective);
                SizeF textSize = g.MeasureString(evalText, textFont);
                float chipWidth = Math.Min(barRect.Width - 4, textSize.Width + 8);
                float chipHeight = Math.Min(22, textSize.Height + 4);
                float chipX = barRect.Left + (barRect.Width - chipWidth) / 2f;
                float chipY = barRect.Top + (barRect.Height - chipHeight) / 2f;
                var chipRect = new RectangleF(chipX, chipY, chipWidth, chipHeight);
                using (var chipPath = CreateRoundedRect(Rectangle.Round(chipRect), 5))
                using (var chipBrush = new SolidBrush(Color.FromArgb(190, 24, 25, 28)))
                using (var chipBorderPen = new Pen(Color.FromArgb(130, 238, 238, 230), 1))
                {
                    g.FillPath(chipBrush, chipPath);
                    g.DrawPath(chipBorderPen, chipPath);
                }

                float textX = chipRect.Left + (chipRect.Width - textSize.Width) / 2f;
                float textY = chipRect.Top + (chipRect.Height - textSize.Height) / 2f;
                using var textBrush = new SolidBrush(Color.FromArgb(248, 248, 244));
                g.DrawString(evalText, textFont, textBrush, textX, textY);
            }
        }

        private void DrawMoveTargetHint(Graphics g, Rectangle squareRect, int squareSize, Position boardPosition, bool isDragHoverTarget)
        {
            bool hasPiece = _board[boardPosition] != null;

            if (isDragHoverTarget)
            {
                using var hoverBrush = new SolidBrush(hasPiece ? Color.FromArgb(82, 42, 70, 36) : _dragHoverOverlay);
                g.FillRectangle(hoverBrush, squareRect);
            }

            if (hasPiece)
            {
                return;
            }

            int hintSize = Math.Max(12, squareSize / 4);

            Rectangle hintRect = new Rectangle(
                squareRect.Left + (squareSize - hintSize) / 2,
                squareRect.Top + (squareSize - hintSize) / 2,
                hintSize,
                hintSize);
            using var hintBrush = new SolidBrush(isDragHoverTarget ? Color.FromArgb(170, 45, 45, 45) : _targetHintColor);
            g.FillEllipse(hintBrush, hintRect);
        }

        private static void DrawKingDangerOverlay(Graphics g, Rectangle squareRect, bool isCheckmate)
        {
            int inset = Math.Max(2, squareRect.Width / 18);
            Rectangle glowRect = Rectangle.Inflate(squareRect, -inset, -inset);
            using var path = new GraphicsPath();
            path.AddEllipse(glowRect);

            using var glow = new PathGradientBrush(path)
            {
                CenterPoint = new PointF(squareRect.Left + squareRect.Width / 2f, squareRect.Top + squareRect.Height / 2f),
                CenterColor = isCheckmate ? Color.FromArgb(238, 220, 22, 22) : Color.FromArgb(222, 238, 34, 30),
                SurroundColors = new[] { Color.FromArgb(0, 236, 45, 38) },
                FocusScales = isCheckmate ? new PointF(0.40f, 0.40f) : new PointF(0.34f, 0.34f)
            };

            g.FillRectangle(glow, squareRect);
        }

        private static string FormatEvaluationText(MoveVariation variation, bool isBlackPerspective)
        {
            if (variation.ScoreType == "mate")
            {
                int mateIn = variation.MateIn ?? 0;
                if (isBlackPerspective)
                    mateIn = -mateIn;
                return $"M{Math.Abs(mateIn)}";
            }

            double score = variation.Score;
            if (isBlackPerspective)
                score = -score;

            if (Math.Abs(score) >= 10)
                return score > 0 ? "+inf" : "-inf";

            return score.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        private void DrawBoardCoordinates(Graphics g, int squareSize)
        {
            using var lightCoordBrush = new SolidBrush(Color.FromArgb(140, 90, 70));
            using var darkCoordBrush = new SolidBrush(Color.FromArgb(245, 235, 210));

            for (int displayFile = 0; displayFile < 8; displayFile++)
            {
                int x = _boardRect.Left + displayFile * squareSize;
                int y = _boardRect.Bottom - squareSize;
                bool isLight = ((displayFile + 7) % 2) == 0;
                Brush brush = isLight ? lightCoordBrush : darkCoordBrush;
                char fileLabel = _boardFlipped ? (char)('h' - displayFile) : (char)('a' + displayFile);
                g.DrawString(fileLabel.ToString(), _coordFont, brush, x + 7, y + squareSize - 38);
            }

            for (int displayRank = 0; displayRank < 8; displayRank++)
            {
                int x = _boardRect.Right - 26;
                int y = _boardRect.Top + displayRank * squareSize;
                bool isLight = ((7 + displayRank) % 2) == 0;
                Brush brush = isLight ? lightCoordBrush : darkCoordBrush;
                int rankLabel = _boardFlipped ? displayRank + 1 : 8 - displayRank;
                g.DrawString(rankLabel.ToString(), _coordFont, brush, x, y + 8);
            }
        }

        private void DrawAnalysisArrow(Graphics g, MoveArrow arrow, int squareSize)
        {
            float fromX, fromY, toX, toY;

            if (_boardFlipped)
            {
                fromX = _boardRect.X + (7 - arrow.FromFile + 0.5f) * squareSize;
                fromY = _boardRect.Y + (arrow.FromRank + 0.5f) * squareSize;
                toX = _boardRect.X + (7 - arrow.ToFile + 0.5f) * squareSize;
                toY = _boardRect.Y + (arrow.ToRank + 0.5f) * squareSize;
            }
            else
            {
                fromX = _boardRect.X + (arrow.FromFile + 0.5f) * squareSize;
                fromY = _boardRect.Y + (7 - arrow.FromRank + 0.5f) * squareSize;
                toX = _boardRect.X + (arrow.ToFile + 0.5f) * squareSize;
                toY = _boardRect.Y + (7 - arrow.ToRank + 0.5f) * squareSize;
            }

            float dx = toX - fromX;
            float dy = toY - fromY;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0f)
                return;

            float unitX = dx / length;
            float unitY = dy / length;
            float startOffset = squareSize * 0.16f;
            float endOffset = squareSize * 0.12f;

            fromX += unitX * startOffset;
            fromY += unitY * startOffset;
            toX -= unitX * endOffset;
            toY -= unitY * endOffset;

            float normalizedSquare = Math.Clamp(squareSize / 80f, 0.70f, 1.30f);
            float compressedScale = (float)Math.Pow(normalizedSquare, 0.55f);
            float widthScale = Math.Clamp(compressedScale, 0.66f, 1.12f);
            float lengthScale = Math.Clamp(compressedScale * 0.99f, 0.62f, 1.06f);

            Color arrowColor;
            float thickness;
            float headWidth;
            float headLength;

            switch (arrow.Strength)
            {
                case 1:
                    thickness = Math.Clamp(squareSize * 0.19f, 7.6f, 13.8f);
                    arrowColor = Color.FromArgb(118, 38, 84, 132);
                    headWidth = 4.25f * widthScale;
                    headLength = 3.9f * lengthScale;
                    break;
                case 2:
                    thickness = Math.Clamp(squareSize * 0.14f, 5.4f, 8.8f);
                    arrowColor = Color.FromArgb(102, 38, 84, 132);
                    headWidth = 3.35f * widthScale;
                    headLength = 2.95f * lengthScale;
                    break;
                case 3:
                    thickness = Math.Clamp(squareSize * 0.104f, 4.2f, 6.6f);
                    arrowColor = Color.FromArgb(88, 38, 84, 132);
                    headWidth = 2.85f * widthScale;
                    headLength = 2.5f * lengthScale;
                    break;
                default:
                    thickness = Math.Clamp(squareSize * 0.086f, 3.7f, 5.8f);
                    arrowColor = Color.FromArgb(78, 38, 84, 132);
                    headWidth = 2.5f * widthScale;
                    headLength = 2.25f * lengthScale;
                    break;
            }

            using var pen = new Pen(arrowColor, thickness);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Custom;
            using var cap = new AdjustableArrowCap(headWidth, headLength, true);
            cap.MiddleInset = 0;
            pen.CustomEndCap = cap;
            g.DrawLine(pen, fromX, fromY, toX, toY);

            DrawAnalysisDepthMarker(g, arrow, squareSize, fromX, fromY, toX, toY);
        }

        private void DrawAnalysisDepthMarker(Graphics g, MoveArrow arrow, int squareSize, float fromX, float fromY, float toX, float toY)
        {
            if (arrow.Strength != 1 || arrow.Depth <= 0)
                return;

            try
            {
                string text = arrow.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
                float fontSize = Math.Clamp(squareSize * 0.21f, 14f, 22f);
                using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF textSize = g.MeasureString(text, font);

                float dx = toX - fromX;
                float dy = toY - fromY;
                float length = (float)Math.Sqrt((dx * dx) + (dy * dy));
                if (length <= 0f)
                    return;

                float unitX = dx / length;
                float unitY = dy / length;
                float normalX = -unitY;
                float normalY = unitX;
                float side = normalX >= 0 ? 1f : -1f;
                float labelX = toX - unitX * (squareSize * 0.16f) + normalX * side * (squareSize * 0.18f);
                float labelY = toY - unitY * (squareSize * 0.16f) + normalY * side * (squareSize * 0.18f);
                labelX = Math.Clamp(labelX, _boardRect.Left + 2f, _boardRect.Right - textSize.Width - 2f);
                labelY = Math.Clamp(labelY, _boardRect.Top + 2f, _boardRect.Bottom - textSize.Height - 2f);

                using var edgeBrush = new SolidBrush(Color.FromArgb(120, 225, 225, 225));
                using var brush = new SolidBrush(Color.FromArgb(10, 10, 10));
                g.DrawString(text, font, edgeBrush, new PointF(labelX + 1.0f, labelY + 1.0f));
                g.DrawString(text, font, edgeBrush, new PointF(labelX - 0.8f, labelY - 0.8f));
                g.DrawString(text, font, brush, new PointF(labelX, labelY));
            }
            catch
            {
                // Diagnostic marker only; never let text drawing suppress arrows.
            }
        }

        private void DrawManualBoardAnnotations(Graphics g, int squareSize)
        {
            if (_manualCircles.Count == 0 && _manualArrows.Count == 0 && (!_isRightDragging || !_rightMouseDownSquare.HasValue || !_rightHoverSquare.HasValue))
                return;

            Color green = Color.FromArgb(160, 65, 135, 55);
            using var circlePen = new Pen(green, Math.Clamp(squareSize * 0.055f, 4.5f, 8.5f));
            foreach (Position square in _manualCircles)
            {
                Rectangle rect = GetBoardSquareRect(square, squareSize);
                float inset = Math.Clamp(squareSize * 0.08f, 5f, 10f);
                g.DrawEllipse(circlePen, rect.Left + inset, rect.Top + inset, rect.Width - (inset * 2f), rect.Height - (inset * 2f));
            }

            foreach (ManualBoardArrow arrow in _manualArrows)
            {
                DrawManualBoardArrow(g, arrow.From, arrow.To, squareSize, green);
            }

            if (_isRightDragging &&
                _rightMouseDownSquare.HasValue &&
                _rightHoverSquare.HasValue &&
                _rightMouseDownSquare.Value != _rightHoverSquare.Value)
            {
                DrawManualBoardArrow(g, _rightMouseDownSquare.Value, _rightHoverSquare.Value, squareSize, Color.FromArgb(115, green));
            }
        }

        private void DrawManualBoardArrow(Graphics g, Position from, Position to, int squareSize, Color color)
        {
            PointF fromCenter = GetBoardSquareCenter(from, squareSize);
            PointF toCenter = GetBoardSquareCenter(to, squareSize);

            float dx = toCenter.X - fromCenter.X;
            float dy = toCenter.Y - fromCenter.Y;
            float length = (float)Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= 0f)
                return;

            float unitX = dx / length;
            float unitY = dy / length;
            float startOffset = squareSize * 0.18f;
            float headLength = Math.Clamp(squareSize * 0.30f, 21f, 38f);
            float headWidth = Math.Clamp(squareSize * 0.42f, 30f, 54f);
            float endOffset = squareSize * 0.15f;
            fromCenter.X += unitX * startOffset;
            fromCenter.Y += unitY * startOffset;
            toCenter.X -= unitX * endOffset;
            toCenter.Y -= unitY * endOffset;

            PointF tip = toCenter;
            PointF neck = new PointF(tip.X - (unitX * headLength), tip.Y - (unitY * headLength));
            PointF normal = new PointF(-unitY, unitX);
            PointF left = new PointF(neck.X + (normal.X * headWidth / 2f), neck.Y + (normal.Y * headWidth / 2f));
            PointF right = new PointF(neck.X - (normal.X * headWidth / 2f), neck.Y - (normal.Y * headWidth / 2f));

            float thickness = Math.Clamp(squareSize * 0.13f, 9.0f, 18.0f);
            using var pen = new Pen(color, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var headBrush = new SolidBrush(color);
            g.DrawLine(pen, fromCenter, neck);
            g.FillPolygon(headBrush, new[] { tip, left, right });
        }

        private PointF GetBoardSquareCenter(Position boardPosition, int squareSize)
        {
            Rectangle rect = GetBoardSquareRect(boardPosition, squareSize);
            return new PointF(rect.Left + (rect.Width / 2f), rect.Top + (rect.Height / 2f));
        }

        private void DrawAnalysisPromotionHint(Graphics g, MoveArrow arrow, int squareSize)
        {
            if (arrow.PromotionPiece == '\0')
                return;

            string pieceText = GetPromotionGlyph(arrow.PromotionPiece, arrow.MovingSide);

            float centerX = _boardFlipped
                ? _boardRect.X + (7 - arrow.ToFile + 0.5f) * squareSize
                : _boardRect.X + (arrow.ToFile + 0.5f) * squareSize;
            float centerY = _boardFlipped
                ? _boardRect.Y + (arrow.ToRank + 0.5f) * squareSize
                : _boardRect.Y + (7 - arrow.ToRank + 0.5f) * squareSize;

            float boxSize = Math.Clamp(squareSize * 0.96f, 40f, 62f);
            float margin = Math.Clamp(squareSize * 0.12f, 8f, 14f);
            float boxX = Math.Clamp(centerX - (boxSize / 2f), _boardRect.Left + 4f, _boardRect.Right - boxSize - 4f);
            bool topHalf = centerY < _boardRect.Top + (_boardRect.Height / 2f);
            float boxY = topHalf
                ? _boardRect.Top - boxSize - margin
                : _boardRect.Bottom + margin;

            RectangleF rect = new RectangleF(boxX, boxY, boxSize, boxSize);
            using GraphicsPath path = CreateRoundedRect(rect, MathF.Min(12f, boxSize * 0.24f));
            using var fill = new SolidBrush(Color.FromArgb(242, 24, 28, 38));
            using var glow = new SolidBrush(Color.FromArgb(38, 110, 170, 255));
            using var border = new Pen(Color.FromArgb(235, 90, 146, 255), Math.Max(1.8f, boxSize * 0.05f));
            using var textBrush = new SolidBrush(Color.FromArgb(250, 245, 248, 252));
            using var font = new Font("Segoe UI Symbol", Math.Max(22f, boxSize * 0.60f), GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            g.FillEllipse(glow, rect.X + (boxSize * 0.12f), rect.Y + (boxSize * 0.12f), boxSize * 0.76f, boxSize * 0.76f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(pieceText, font, textBrush, rect, format);
        }

        private void DrawMoveClassificationBadge(Graphics g, int squareSize)
        {
            if (!string.Equals(_analysisMode, "OFF", StringComparison.OrdinalIgnoreCase))
                return;

            if (_historyIndex <= 0 || _historyIndex >= _history.Count)
                return;

            if (!_moveAnalysisByPly.TryGetValue(_historyIndex, out GameAnalysisMoveResult? analysis))
                return;

            if (!TryGetClassificationBadgeStyle(analysis.Classification, out string badgeText, out Color badgeFill, out Color badgeBorder, out Color badgeFore))
                return;

            Position? targetSquare = GetAnalyzedMoveTargetSquare(analysis);
            if (!targetSquare.HasValue)
                return;

            Rectangle squareRect = GetBoardSquareRect(targetSquare.Value, squareSize);
            float badgeSize = Math.Clamp(squareSize * 0.34f, 24f, 44f);
            float overlap = badgeSize * 0.28f;
            float badgeX = Math.Clamp(squareRect.Right - badgeSize + overlap, _boardRect.Left + 3f, _boardRect.Right - badgeSize - 3f);
            float badgeY = Math.Clamp(squareRect.Top - overlap, _boardRect.Top + 3f, _boardRect.Bottom - badgeSize - 3f);
            var badgeRect = new RectangleF(badgeX, badgeY, badgeSize, badgeSize);

            using (var shadowBrush = new SolidBrush(Color.FromArgb(95, 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, badgeRect.X + 2f, badgeRect.Y + 3f, badgeRect.Width, badgeRect.Height);
            }

            using (var fillBrush = new SolidBrush(badgeFill))
            using (var borderPen = new Pen(badgeBorder, Math.Max(1.4f, badgeSize * 0.055f)))
            {
                g.FillEllipse(fillBrush, badgeRect);
                g.DrawEllipse(borderPen, badgeRect);
            }

            float fontSize = badgeText.Length > 2
                ? Math.Clamp(badgeSize * 0.33f, 8.8f, 13.5f)
                : Math.Clamp(badgeSize * 0.43f, 11f, 18f);
            using (var badgeFont = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(badgeFore))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(badgeText, badgeFont, textBrush, badgeRect, format);
            }

            if (string.Equals(analysis.Classification, "Miss", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(analysis.BestMove) &&
                analysis.BestMove != "-")
            {
                DrawMissBestMovePill(g, squareRect, badgeRect, analysis.BestMove, badgeFill, badgeBorder);
            }
        }

        private Position? GetAnalyzedMoveTargetSquare(GameAnalysisMoveResult analysis)
        {
            HistoryEntry entry = _history[_historyIndex];
            if (entry.LastMoveTo.HasValue)
                return entry.LastMoveTo;

            Move? move = TryFindMoveForFenTransition(analysis.FenBefore, analysis.FenAfter);
            return move?.NewPosition;
        }

        private Rectangle GetBoardSquareRect(Position boardPosition, int squareSize)
        {
            int displayFile = _boardFlipped ? 7 - boardPosition.X : boardPosition.X;
            int displayRank = _boardFlipped ? boardPosition.Y : 7 - boardPosition.Y;
            return new Rectangle(
                _boardRect.Left + (displayFile * squareSize),
                _boardRect.Top + (displayRank * squareSize),
                squareSize,
                squareSize);
        }

        private void DrawMissBestMovePill(Graphics g, Rectangle squareRect, RectangleF badgeRect, string bestMove, Color accent, Color border)
        {
            string label = $"Best {bestMove}";
            float fontSize = Math.Clamp(squareRect.Height * 0.145f, 10f, 15f);
            using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            SizeF textSize = g.MeasureString(label, font);
            float pillWidth = Math.Min(_boardRect.Width - 10f, textSize.Width + 18f);
            float pillHeight = Math.Clamp(textSize.Height + 8f, 22f, 31f);

            float preferredX = badgeRect.Right - pillWidth;
            float pillX = Math.Clamp(preferredX, _boardRect.Left + 5f, _boardRect.Right - pillWidth - 5f);
            float pillY = badgeRect.Bottom + 5f;
            if (pillY + pillHeight > _boardRect.Bottom - 4f)
                pillY = badgeRect.Top - pillHeight - 5f;
            if (pillY < _boardRect.Top + 4f)
                pillY = Math.Clamp(squareRect.Bottom - pillHeight - 5f, _boardRect.Top + 4f, _boardRect.Bottom - pillHeight - 4f);

            var pillRect = new RectangleF(pillX, pillY, pillWidth, pillHeight);
            using GraphicsPath path = CreateRoundedRect(pillRect, Math.Min(9f, pillHeight * 0.34f));
            using var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            using var fill = new SolidBrush(Color.FromArgb(232, 24, 26, 31));
            using var glow = new SolidBrush(Color.FromArgb(45, accent));
            using var borderPen = new Pen(Color.FromArgb(210, border), 1.3f);
            using var textBrush = new SolidBrush(Color.FromArgb(245, 248, 252));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

            using GraphicsPath shadowPath = CreateRoundedRect(new RectangleF(pillRect.X + 2f, pillRect.Y + 3f, pillRect.Width, pillRect.Height), Math.Min(9f, pillHeight * 0.34f));
            g.FillPath(shadow, shadowPath);
            g.FillPath(fill, path);
            g.FillRectangle(glow, pillRect.Left, pillRect.Top, Math.Min(6f, pillRect.Width * 0.12f), pillRect.Height);
            g.DrawPath(borderPen, path);
            g.DrawString(label, font, textBrush, pillRect, format);
        }

        private static bool TryGetClassificationBadgeStyle(string classification, out string text, out Color fill, out Color border, out Color fore)
        {
            switch (classification)
            {
                case "Book":
                    text = "Book";
                    fill = Color.FromArgb(56, 112, 184);
                    border = Color.FromArgb(236, 190, 224, 255);
                    fore = Color.White;
                    return true;
                case "Best":
                    text = "BEST";
                    fill = Color.FromArgb(52, 138, 92);
                    border = Color.FromArgb(232, 162, 244, 195);
                    fore = Color.White;
                    return true;
                case "Good":
                    text = "Good";
                    fill = Color.FromArgb(55, 132, 176);
                    border = Color.FromArgb(232, 150, 220, 255);
                    fore = Color.White;
                    return true;
                case "Ok":
                    text = "OK";
                    fill = Color.FromArgb(92, 94, 102);
                    border = Color.FromArgb(225, 190, 194, 205);
                    fore = Color.White;
                    return true;
                case "Inaccuracy":
                    text = "?!";
                    fill = Color.FromArgb(62, 152, 214);
                    border = Color.FromArgb(238, 190, 230, 255);
                    fore = Color.White;
                    return true;
                case "Mistake":
                    text = "?";
                    fill = Color.FromArgb(218, 151, 44);
                    border = Color.FromArgb(242, 255, 217, 132);
                    fore = Color.FromArgb(28, 24, 18);
                    return true;
                case "Blunder":
                    text = "??";
                    fill = Color.FromArgb(222, 70, 82);
                    border = Color.FromArgb(245, 255, 190, 198);
                    fore = Color.White;
                    return true;
                case "Miss":
                    text = "MISS";
                    fill = Color.FromArgb(148, 96, 214);
                    border = Color.FromArgb(240, 224, 202, 255);
                    fore = Color.White;
                    return true;
                default:
                    text = string.Empty;
                    fill = Color.Empty;
                    border = Color.Empty;
                    fore = Color.Empty;
                    return false;
            }
        }

        private static string GetPromotionGlyph(char promotionPiece, char movingSide)
        {
            bool black = char.ToLowerInvariant(movingSide) == 'b';
            return char.ToLowerInvariant(promotionPiece) switch
            {
                'q' => black ? "♛" : "♕",
                'r' => black ? "♜" : "♖",
                'b' => black ? "♝" : "♗",
                'n' => black ? "♞" : "♘",
                _ => char.ToUpperInvariant(promotionPiece).ToString()
            };
        }

        private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
        {
            float diameter = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Position DisplayToBoardPosition(int displayFile, int displayRank)
        {
            int boardFile = _boardFlipped ? 7 - displayFile : displayFile;
            int boardRank = _boardFlipped ? displayRank : 7 - displayRank;
            return new Position((short)boardFile, (short)boardRank);
        }

        private bool TryGetLastMoveSquares(out Position? from, out Position? to)
        {
            from = null;
            to = null;
            if (_historyIndex <= 0 || _historyIndex >= _history.Count)
                return false;

            HistoryEntry entry = _history[_historyIndex];
            if (!entry.LastMoveFrom.HasValue || !entry.LastMoveTo.HasValue)
                return false;

            from = entry.LastMoveFrom;
            to = entry.LastMoveTo;
            return true;
        }

        private Position? TryResolveSquareFromPoint(Point location)
        {
            if (!_boardRect.Contains(location))
                return null;

            int squareSize = _boardRect.Width / 8;
            int displayFile = (location.X - _boardRect.Left) / squareSize;
            int displayRank = (location.Y - _boardRect.Top) / squareSize;

            if (displayFile < 0 || displayFile > 7 || displayRank < 0 || displayRank > 7)
                return null;

            return DisplayToBoardPosition(displayFile, displayRank);
        }

        private void AnalysisBoardForm_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var square = TryResolveSquareFromPoint(e.Location);
                if (!square.HasValue)
                    return;

                _rightMouseDownOnBoard = true;
                _rightMouseDownSquare = square;
                _rightHoverSquare = square;
                _isRightDragging = false;
                _mouseDownLocation = e.Location;
                Capture = true;
                return;
            }

            if (e.Button != MouseButtons.Left || !_boardRect.Contains(e.Location))
                return;

            ClearManualBoardAnnotations();

            var clickedSquare = TryResolveSquareFromPoint(e.Location);
            if (!clickedSquare.HasValue)
                return;

            _mouseDownOnBoard = true;
            _mouseDownWasSelectedSquare = _selectedSquare.HasValue && _selectedSquare.Value == clickedSquare.Value;
            _mouseDownLocation = e.Location;
            _dragLocation = e.Location;

            var clickedPiece = _board[clickedSquare.Value];
            if (clickedPiece != null && clickedPiece.Color == _board.Turn)
            {
                _dragOriginSquare = clickedSquare.Value;
                _dragPiece = clickedPiece;
                SelectSquare(clickedSquare.Value);
            }
        }

        private void AnalysisBoardForm_MouseMove(object? sender, MouseEventArgs e)
        {
            if (_rightMouseDownOnBoard && _rightMouseDownSquare.HasValue)
            {
                Position? rightHoverSquare = TryResolveSquareFromPoint(e.Location);
                _rightHoverSquare = rightHoverSquare;

                if (!_isRightDragging)
                {
                    int dx = e.Location.X - _mouseDownLocation.X;
                    int dy = e.Location.Y - _mouseDownLocation.Y;
                    if ((dx * dx) + (dy * dy) >= 25)
                        _isRightDragging = true;
                }

                Invalidate(_boardRect);
                return;
            }

            if (!_mouseDownOnBoard || !_dragOriginSquare.HasValue || _dragPiece == null)
                return;

            _dragLocation = e.Location;
            Position? hoverSquare = TryResolveSquareFromPoint(e.Location);
            _dragHoverSquare = hoverSquare.HasValue && _selectedMoves.Any(m => m.NewPosition == hoverSquare.Value)
                ? hoverSquare
                : null;

            if (!_isDraggingPiece)
            {
                int dx = e.Location.X - _mouseDownLocation.X;
                int dy = e.Location.Y - _mouseDownLocation.Y;
                if ((dx * dx) + (dy * dy) >= 25)
                {
                    _isDraggingPiece = true;
                }
            }

            Invalidate();
        }

        private void AnalysisBoardForm_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && _rightMouseDownOnBoard)
            {
                Position? rightReleasedSquare = TryResolveSquareFromPoint(e.Location);
                if (_rightMouseDownSquare.HasValue && rightReleasedSquare.HasValue)
                {
                    if (_rightMouseDownSquare.Value == rightReleasedSquare.Value)
                        ToggleManualCircle(rightReleasedSquare.Value);
                    else
                        ToggleManualArrow(_rightMouseDownSquare.Value, rightReleasedSquare.Value);
                }

                ResetRightAnnotationState();
                Invalidate(_boardRect);
                return;
            }

            if (e.Button != MouseButtons.Left || !_mouseDownOnBoard)
                return;

            var releasedSquare = TryResolveSquareFromPoint(e.Location);
            var releasedPiece = releasedSquare.HasValue ? _board[releasedSquare.Value] : null;

            if (_isDraggingPiece && releasedSquare.HasValue && TryPerformMoveTo(releasedSquare.Value))
            {
                ResetDragState();
                return;
            }

            if (!_isDraggingPiece && releasedSquare.HasValue)
            {
                if (_mouseDownWasSelectedSquare &&
                    _selectedSquare.HasValue &&
                    releasedSquare.Value == _selectedSquare.Value)
                {
                    ClearSelection();
                    Invalidate();
                    ResetDragState(clearSelection: false);
                    return;
                }

                if (_selectedSquare.HasValue && TryPerformMoveTo(releasedSquare.Value))
                {
                    ResetDragState();
                    return;
                }

                if (releasedPiece != null && releasedPiece.Color == _board.Turn)
                {
                    SelectSquare(releasedSquare.Value);
                }
                else if (_dragOriginSquare.HasValue && releasedSquare.Value == _dragOriginSquare.Value)
                {
                    SelectSquare(releasedSquare.Value);
                }
                else
                {
                    ClearSelection();
                    Invalidate();
                }
            }
            else
            {
                ClearSelection();
                Invalidate();
            }

            ResetDragState(clearSelection: false);
        }

        private void SelectSquare(Position square)
        {
            var piece = _board[square];
            if (piece == null || piece.Color != _board.Turn)
            {
                ClearSelection();
                Invalidate();
                return;
            }

            try
            {
                _selectedSquare = square;
                _selectedMoves = _board.Moves(square, false, false);
            }
            catch (ChessPieceNotFoundException)
            {
                ClearSelection();
            }

            Invalidate();
        }

        private void ClearSelection()
        {
            _selectedSquare = null;
            _selectedMoves = Array.Empty<Move>();
        }

        private void ToggleManualCircle(Position square)
        {
            int existingIndex = _manualCircles.FindIndex(existing => existing == square);
            if (existingIndex >= 0)
                _manualCircles.RemoveAt(existingIndex);
            else
                _manualCircles.Add(square);
        }

        private void ToggleManualArrow(Position from, Position to)
        {
            int existingIndex = _manualArrows.FindIndex(existing => existing.From == from && existing.To == to);
            if (existingIndex >= 0)
                _manualArrows.RemoveAt(existingIndex);
            else
                _manualArrows.Add(new ManualBoardArrow { From = from, To = to });
        }

        private void ClearManualBoardAnnotations()
        {
            if (_manualCircles.Count == 0 && _manualArrows.Count == 0)
                return;

            _manualCircles.Clear();
            _manualArrows.Clear();
            Invalidate(_boardRect);
        }

        private void ResetRightAnnotationState()
        {
            _rightMouseDownOnBoard = false;
            _isRightDragging = false;
            _rightMouseDownSquare = null;
            _rightHoverSquare = null;
            if (Capture)
                Capture = false;
        }

        private void SyncStatus()
        {
            _fenTextBox.Text = _board.ToFen();
            _turnLabel.Text = _board.Turn == PieceColor.White ? "Turn: White" : "Turn: Black";
            QueueOpeningBookLookup();
        }

        private void ResetHistory()
        {
            _moveAnalysisByPly.Clear();
            _variationsByParentIndex.Clear();
            _history.Clear();
            _history.Add(new HistoryEntry
            {
                Fen = _board.ToFen(),
                MoveText = ""
            });
            _historyIndex = 0;
            RefreshMoveGrid();
            RefreshMatchConfigButtons();
        }

        private void PushHistory(string moveText, Move? move = null)
        {
            bool truncated = _historyIndex < _history.Count - 1;
            if (truncated)
            {
                PreserveFutureAsVariation(_historyIndex);
                RemoveVariationsAfter(_historyIndex);
                _history.RemoveRange(_historyIndex + 1, _history.Count - (_historyIndex + 1));
            }

            RemoveAnalysisFrom(_historyIndex + 1);
            _history.Add(new HistoryEntry
            {
                Fen = _board.ToFen(),
                MoveText = moveText,
                LastMoveFrom = move?.OriginalPosition,
                LastMoveTo = move?.NewPosition
            });
            _historyIndex = _history.Count - 1;

            // Clean forward append with no variations: O(1) incremental grid update
            // instead of the full O(n) rebuild (which made self-play lag grow O(n^2)
            // with move count). Any truncation/variation falls back to the rebuild.
            if (!truncated && _variationsByParentIndex.Count == 0)
                AppendMoveToGrid(_historyIndex);
            else
                RefreshMoveGrid();

            RefreshMatchConfigButtons();
        }

        private void PreserveFutureAsVariation(int parentIndex)
        {
            if (parentIndex < 0 || parentIndex >= _history.Count - 1)
                return;

            var entries = _history
                .Skip(parentIndex + 1)
                .Select(CloneHistoryEntry)
                .ToList();
            if (entries.Count == 0)
                return;

            var analysisByOffset = new Dictionary<int, GameAnalysisMoveResult>();
            for (int offset = 0; offset < entries.Count; offset++)
            {
                int sourcePly = parentIndex + 1 + offset;
                if (_moveAnalysisByPly.TryGetValue(sourcePly, out GameAnalysisMoveResult? analysis))
                    analysisByOffset[offset] = CloneAnalysisForVariation(analysis);
            }

            string signature = BuildVariationSignature(entries);
            if (!_variationsByParentIndex.TryGetValue(parentIndex, out var lines))
            {
                lines = new List<VariationLine>();
                _variationsByParentIndex[parentIndex] = lines;
            }

            if (lines.Any(line => string.Equals(BuildVariationSignature(line.Entries), signature, StringComparison.Ordinal)))
                return;

            lines.Add(new VariationLine
            {
                ParentIndex = parentIndex,
                Entries = entries,
                AnalysisByOffset = analysisByOffset
            });
        }

        private void PromoteVariationToMainLine(VariationLine variation)
        {
            if (variation.ParentIndex < 0 || variation.ParentIndex >= _history.Count || variation.Entries.Count == 0)
                return;

            PreserveFutureAsVariation(variation.ParentIndex);
            RemoveVariationsAfter(variation.ParentIndex);
            RemoveAnalysisFrom(variation.ParentIndex + 1);
            _history.RemoveRange(variation.ParentIndex + 1, _history.Count - (variation.ParentIndex + 1));
            _history.AddRange(variation.Entries.Select(CloneHistoryEntry));
            RestoreVariationAnalysis(variation);

            if (_variationsByParentIndex.TryGetValue(variation.ParentIndex, out var lines))
            {
                lines.RemoveAll(line => line.Id == variation.Id);
                if (lines.Count == 0)
                    _variationsByParentIndex.Remove(variation.ParentIndex);
            }

            _historyIndex = Math.Min(variation.ParentIndex + 1, _history.Count - 1);
            _board = ChessBoard.LoadFromFen(_history[_historyIndex].Fen, AutoEndgameRules.All);
            ClearSelection();
            ResetDragState(clearSelection: false);
            SyncStatus();
            RefreshMoveGrid();
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            Invalidate();
            EmitSnapshot();
        }

        private static HistoryEntry CloneHistoryEntry(HistoryEntry entry)
        {
            return new HistoryEntry
            {
                Fen = entry.Fen,
                MoveText = entry.MoveText,
                LastMoveFrom = entry.LastMoveFrom,
                LastMoveTo = entry.LastMoveTo
            };
        }

        private static GameAnalysisMoveResult CloneAnalysisForVariation(GameAnalysisMoveResult source)
        {
            return new GameAnalysisMoveResult
            {
                PlyIndex = source.PlyIndex,
                MoveNumber = source.MoveNumber,
                IsWhiteMove = source.IsWhiteMove,
                MoveText = source.MoveText,
                FenBefore = source.FenBefore,
                FenAfter = source.FenAfter,
                BestMove = source.BestMove,
                EvalBefore = source.EvalBefore,
                EvalAfterForMover = source.EvalAfterForMover,
                Loss = source.Loss,
                Classification = source.Classification,
                Depth = source.Depth,
                IsMateScore = source.IsMateScore,
                MateIn = source.MateIn
            };
        }

        private void RestoreVariationAnalysis(VariationLine variation)
        {
            foreach (var pair in variation.AnalysisByOffset)
            {
                int offset = pair.Key;
                int plyIndex = variation.ParentIndex + 1 + offset;
                if (plyIndex <= 0 || plyIndex >= _history.Count || offset < 0 || offset >= variation.Entries.Count)
                    continue;

                HistoryEntry before = _history[plyIndex - 1];
                HistoryEntry after = _history[plyIndex];
                _moveAnalysisByPly[plyIndex] = CloneAnalysisForPly(pair.Value, plyIndex, before, after);
            }
        }

        private static GameAnalysisMoveResult CloneAnalysisForPly(GameAnalysisMoveResult source, int plyIndex, HistoryEntry before, HistoryEntry after)
        {
            return new GameAnalysisMoveResult
            {
                PlyIndex = plyIndex,
                MoveNumber = ((plyIndex - 1) / 2) + 1,
                IsWhiteMove = plyIndex % 2 == 1,
                MoveText = StripResultSuffix(after.MoveText ?? string.Empty),
                FenBefore = before.Fen,
                FenAfter = after.Fen,
                BestMove = source.BestMove,
                EvalBefore = source.EvalBefore,
                EvalAfterForMover = source.EvalAfterForMover,
                Loss = source.Loss,
                Classification = source.Classification,
                Depth = source.Depth,
                IsMateScore = source.IsMateScore,
                MateIn = source.MateIn
            };
        }

        private static string BuildVariationSignature(IEnumerable<HistoryEntry> entries)
        {
            return string.Join("|", entries.Select(entry => entry.Fen));
        }

        private void RemoveAnalysisFrom(int plyIndex)
        {
            foreach (int key in _moveAnalysisByPly.Keys.Where(key => key >= plyIndex).ToList())
                _moveAnalysisByPly.Remove(key);
        }

        private void RemoveVariationsAfter(int parentIndex)
        {
            foreach (int key in _variationsByParentIndex.Keys.Where(key => key > parentIndex).ToList())
                _variationsByParentIndex.Remove(key);
        }

        private static Move? TryFindMoveForFenTransition(string beforeFen, string afterFen)
        {
            try
            {
                string targetKey = NormalizeFenForRepetition(afterFen);
                if (string.IsNullOrWhiteSpace(targetKey))
                    return null;

                var beforeBoard = ChessBoard.LoadFromFen(beforeFen, AutoEndgameRules.All);
                foreach (Move move in beforeBoard.Moves(false, true))
                {
                    var candidateBoard = ChessBoard.LoadFromFen(beforeFen, AutoEndgameRules.All);
                    Move? candidateMove = candidateBoard.Moves(false, true)
                        .FirstOrDefault(m => string.Equals(ToUciMove(m), ToUciMove(move), StringComparison.OrdinalIgnoreCase));
                    if (candidateMove == null)
                        continue;

                    candidateBoard.Move(candidateMove);
                    if (string.Equals(NormalizeFenForRepetition(candidateBoard.ToFen()), targetKey, StringComparison.Ordinal))
                        return move;
                }
            }
            catch
            {
            }

            return null;
        }

        private List<string> GetCurrentGameMoveTextsForPgn(bool includeResultSuffix)
        {
            var moves = new List<string>();
            for (int i = 1; i < _history.Count; i++)
            {
                string text = _history[i].MoveText ?? string.Empty;
                text = includeResultSuffix ? text.Trim() : StripResultSuffix(text);
                if (!string.IsNullOrWhiteSpace(text))
                    moves.Add(text);
            }

            return moves;
        }

        private void CopyCurrentGamePgnToClipboard()
        {
            string? pgn = GetCurrentGamePgn();
            if (string.IsNullOrWhiteSpace(pgn))
            {
                SetMatchStatus("No current game PGN to copy.");
                return;
            }

            try
            {
                Clipboard.SetText(pgn);
                SetMatchStatus("Current game PGN copied.");
            }
            catch
            {
                SetMatchStatus("Could not copy current game PGN.");
            }
        }

        private void CopyArchivedGamePgnToClipboard(int archivedIndex)
        {
            if (archivedIndex < 0 || archivedIndex >= _archivedMatchGames.Count)
            {
                SetMatchStatus("Saved game not found.");
                return;
            }

            string pgn = BuildPgn(_archivedMatchGames[archivedIndex]);
            try
            {
                Clipboard.SetText(pgn);
                SetMatchStatus($"Saved game {archivedIndex + 1} PGN copied.");
            }
            catch
            {
                SetMatchStatus("Could not copy saved game PGN.");
            }
        }

        private void CopyMatchPgnToClipboard()
        {
            if (BuildLimits.IsFreeEdition)
            {
                SetMatchStatus("Match PGN export is available in the full version.");
                return;
            }

            var sections = new List<string>();
            sections.AddRange(_archivedMatchGames.Select(BuildPgn));

            string? currentPgn = GetCurrentGamePgn();
            if (!string.IsNullOrWhiteSpace(currentPgn))
                sections.Add(currentPgn);

            if (sections.Count == 0)
            {
                SetMatchStatus("No match PGN to copy.");
                return;
            }

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine + Environment.NewLine, sections));
                SetMatchStatus("Match PGN copied.");
            }
            catch
            {
                SetMatchStatus("Could not copy match PGN.");
            }
        }

        private void RefreshClipboardMenu()
        {
            _clipboardMenu.Items.Clear();

            var copyFenItem = new ToolStripMenuItem("Copy FEN");
            copyFenItem.Click += (_, _) => CopyFenToClipboard();
            _clipboardMenu.Items.Add(copyFenItem);

            var loadFenItem = new ToolStripMenuItem("Load FEN from Clipboard")
            {
                Enabled = !_matchRunning
            };
            loadFenItem.Click += (_, _) => LoadFenFromClipboard();
            _clipboardMenu.Items.Add(loadFenItem);

            _clipboardMenu.Items.Add(new ToolStripSeparator());

            var copyPgnItem = new ToolStripMenuItem("Copy Game PGN")
            {
                Enabled = _history.Count > 1
            };
            copyPgnItem.Click += (_, _) => CopyCurrentGamePgnToClipboard();
            _clipboardMenu.Items.Add(copyPgnItem);

            var loadPgnItem = new ToolStripMenuItem("Load PGN from Clipboard")
            {
                Enabled = !_matchRunning
            };
            loadPgnItem.Click += (_, _) => LoadPgnFromClipboard();
            _clipboardMenu.Items.Add(loadPgnItem);
        }

        private void CopyFenToClipboard()
        {
            try
            {
                Clipboard.SetText(_board.ToFen());
            }
            catch
            {
                SetMatchStatus("Could not copy FEN.");
            }
        }

        private void LoadFenFromClipboard()
        {
            try
            {
                LoadFenText(Clipboard.GetText());
                SetMatchStatus("FEN loaded from clipboard.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid FEN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadPgnFromClipboard()
        {
            try
            {
                string text = Clipboard.GetText();
                LoadPgnText(text);
                SetMatchStatus("PGN loaded from clipboard.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid PGN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadFenText(string fen)
        {
            if (_matchRunning)
                throw new InvalidOperationException("Stop the engine match before loading a FEN.");

            ClearLoadedGameMetadata();
            _board = ChessBoard.LoadFromFen((fen ?? string.Empty).Trim(), AutoEndgameRules.All);
            ClearSelection();
            ResetDragState(clearSelection: false);
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            ResetHistory();
            SyncStatus();
            Invalidate();
            EmitSnapshot();
        }

        private void LoadPgnText(string pgnText)
        {
            if (_matchRunning)
                throw new InvalidOperationException("Stop the engine match before loading a PGN.");

            Dictionary<string, string> headers = ParsePgnHeaders(pgnText);
            string normalized = NormalizeClipboardPgn(pgnText);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("Clipboard does not contain any PGN moves.");

            _loadedGameWhiteName = GetPgnHeaderValue(headers, "White", "White");
            _loadedGameBlackName = GetPgnHeaderValue(headers, "Black", "Black");
            _loadedGameResult = NormalizePgnResult(GetPgnHeaderValue(headers, "Result", "*"));
            _loadedGameTimeControlKey = GetPgnHeaderValue(headers, "TimeControl", "Analysis");
            _loadedGameMetadataActive = true;

            _board = CreateNewBoard();
            ClearSelection();
            ResetDragState(clearSelection: false);
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            ResetHistory();

            foreach (string token in TokenizePgnMoves(normalized))
            {
                if (IsPgnHeaderOrResult(token))
                    continue;

                Move move = ResolvePgnMoveToken(token);
                string moveText = FormatMoveText(move);
                _board.Move(move);
                PushHistory(moveText, move);
            }

            SyncStatus();
            JumpToHistory(0);
            RefreshMatchConfigButtons();
            Invalidate();
            EmitSnapshot();
        }

        private void ClearLoadedGameMetadata()
        {
            _loadedGameWhiteName = "White";
            _loadedGameBlackName = "Black";
            _loadedGameResult = "*";
            _loadedGameTimeControlKey = "Analysis";
            _loadedGameMetadataActive = false;
        }

        private Move ResolvePgnMoveToken(string token)
        {
            return ResolveMoveTokenForBoard(_board, token)
                ?? throw new InvalidOperationException($"Could not apply PGN move '{token}'.");
        }

        private static Move? ResolveMoveTokenForBoard(ChessBoard board, string token)
        {
            string normalizedToken = NormalizeMoveToken(token);
            var legalMoves = board.Moves(false, true);

            Move? sanMatch = legalMoves.FirstOrDefault(m =>
                string.Equals(NormalizeMoveToken(m.San ?? string.Empty), normalizedToken, StringComparison.OrdinalIgnoreCase));
            if (sanMatch != null)
                return sanMatch;

            return legalMoves.FirstOrDefault(m =>
                string.Equals(ToUciMove(m), normalizedToken, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeClipboardPgn(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string normalized = text.Replace("\r", " ").Replace("\n", " ");
            normalized = Regex.Replace(normalized, @"\{[^}]*\}", " ");
            normalized = Regex.Replace(normalized, @";[^\n\r]*", " ");
            normalized = Regex.Replace(normalized, @"\([^)]*\)", " ");
            normalized = Regex.Replace(normalized, @"\[[^\]]*\]", " ");
            normalized = Regex.Replace(normalized, @"\$\d+", " ");
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }

        private static Dictionary<string, string> ParsePgnHeaders(string text)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return headers;

            foreach (Match match in Regex.Matches(text, @"\[(?<key>[A-Za-z0-9_]+)\s+""(?<value>(?:\\.|[^""])*)""\]"))
            {
                string key = match.Groups["key"].Value.Trim();
                string value = match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
                if (!string.IsNullOrWhiteSpace(key))
                    headers[key] = value;
            }

            return headers;
        }

        private static string GetPgnHeaderValue(Dictionary<string, string> headers, string key, string fallback)
        {
            return headers.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;
        }

        private static IEnumerable<string> TokenizePgnMoves(string normalizedPgn)
        {
            foreach (string rawToken in normalizedPgn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string token = rawToken.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                if (Regex.IsMatch(token, @"^\d+\.(\.\.)?$"))
                    continue;

                token = Regex.Replace(token, @"^\d+\.(\.\.)?", string.Empty);
                if (!string.IsNullOrWhiteSpace(token))
                    yield return token;
            }
        }

        private static bool IsPgnHeaderOrResult(string token)
        {
            return token is "1-0" or "0-1" or "1/2-1/2" or "*";
        }

        private static string NormalizeMoveToken(string token)
        {
            return (token ?? string.Empty)
                .Trim()
                .Replace("0-0-0", "O-O-O", StringComparison.OrdinalIgnoreCase)
                .Replace("0-0", "O-O", StringComparison.OrdinalIgnoreCase)
                .Replace("!", string.Empty, StringComparison.Ordinal)
                .Replace("?", string.Empty, StringComparison.Ordinal)
                .Replace("ep", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("e.p.", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private void RefreshMatchGamePgnMenu()
        {
            _matchGamePgnMenu.Items.Clear();

            var currentItem = new ToolStripMenuItem("Current game")
            {
                Enabled = _history.Count > 1
            };
            currentItem.Click += (_, _) => CopyCurrentGamePgnToClipboard();
            _matchGamePgnMenu.Items.Add(currentItem);

            if (_archivedMatchGames.Count > 0)
            {
                _matchGamePgnMenu.Items.Add(new ToolStripSeparator());

                for (int i = 0; i < _archivedMatchGames.Count; i++)
                {
                    int archivedIndex = i;
                    var game = _archivedMatchGames[i];
                    string label = $"Game {archivedIndex + 1}: {TrimToControlText(game.WhiteName, 12)} vs {TrimToControlText(game.BlackName, 12)} {game.Result}";
                    var item = new ToolStripMenuItem(label);
                    item.Click += (_, _) => CopyArchivedGamePgnToClipboard(archivedIndex);
                    _matchGamePgnMenu.Items.Add(item);
                }
            }
        }

        private void SetMatchStatus(string statusText)
        {
            _matchStatusText = string.IsNullOrWhiteSpace(statusText) ? "Ready for engine match." : statusText;
            Invalidate(_matchPanelRect);
        }

        private static string BuildPgn(ArchivedMatchGame game)
        {
            return BuildPgn(game.WhiteName, game.BlackName, game.TimeControlKey, game.Result, game.SavedAtUtc, game.MoveTexts);
        }

        private static string BuildPgn(string whiteName, string blackName, string timeControlKey, string result, DateTime savedAtUtc, IReadOnlyList<string> moveTexts)
        {
            var builder = new StringBuilder();
            string normalizedResult = NormalizePgnResult(result);
            string dateText = savedAtUtc.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);

            builder.AppendLine("[Event \"Chess Kit Engine Match\"]");
            builder.AppendLine("[Site \"Chess Kit Analysis Board\"]");
            builder.AppendLine($"[Date \"{dateText}\"]");
            builder.AppendLine("[Round \"-\"]");
            builder.AppendLine($"[White \"{EscapePgnHeader(whiteName)}\"]");
            builder.AppendLine($"[Black \"{EscapePgnHeader(blackName)}\"]");
            builder.AppendLine($"[Result \"{normalizedResult}\"]");
            builder.AppendLine($"[TimeControl \"{EscapePgnHeader(timeControlKey)}\"]");
            builder.AppendLine();

            for (int i = 0; i < moveTexts.Count; i += 2)
            {
                int moveNumber = (i / 2) + 1;
                builder.Append(moveNumber.ToString(CultureInfo.InvariantCulture));
                builder.Append(".");
                builder.Append(moveTexts[i]);

                if (i + 1 < moveTexts.Count)
                {
                    builder.Append(' ');
                    builder.Append(moveTexts[i + 1]);
                }

                builder.Append(' ');
            }

            builder.Append(normalizedResult);
            return builder.ToString().TrimEnd();
        }

        private static string EscapePgnHeader(string text)
        {
            return (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string NormalizePgnResult(string result)
        {
            string normalized = (result ?? string.Empty).Trim();
            return normalized switch
            {
                "1-0" => "1-0",
                "0-1" => "0-1",
                "1/2-1/2" => "1/2-1/2",
                "½-½" => "1/2-1/2",
                "*" => "*",
                _ when normalized.StartsWith("1-0", StringComparison.Ordinal) => "1-0",
                _ when normalized.StartsWith("0-1", StringComparison.Ordinal) => "0-1",
                _ when normalized.Contains("½-½", StringComparison.Ordinal) => "1/2-1/2",
                _ when normalized.Contains("1/2-1/2", StringComparison.Ordinal) => "1/2-1/2",
                _ => "*"
            };
        }

        private static string StripResultSuffix(string moveText)
        {
            string text = (moveText ?? string.Empty).Trim();
            foreach (string suffix in new[] { " 1-0", " 0-1", " 1/2-1/2", " ½-½" })
            {
                if (text.EndsWith(suffix, StringComparison.Ordinal))
                    return text[..^suffix.Length].TrimEnd();
            }

            return text;
        }

        private void RefreshMoveGrid()
        {
            _suppressGridSelection = true;
            _movesGrid.CurrentCell = null;
            _movesGrid.Rows.Clear();

            AddVariationRowsForParent(0);
            for (int pairStart = 1; pairStart < _history.Count; pairStart += 2)
            {
                int moveNumber = ((pairStart - 1) / 2) + 1;
                string whiteMove = FormatMoveGridText(pairStart);
                string blackMove = pairStart + 1 < _history.Count ? FormatMoveGridText(pairStart + 1) : string.Empty;
                int rowIndex = _movesGrid.Rows.Add(moveNumber.ToString() + ".", whiteMove, blackMove);
                _movesGrid.Rows[rowIndex].Cells[1].Tag = pairStart;
                if (pairStart + 1 < _history.Count)
                    _movesGrid.Rows[rowIndex].Cells[2].Tag = pairStart + 1;
                ApplyMoveCellAnalysisStyle(_movesGrid.Rows[rowIndex].Cells[1], pairStart);
                if (pairStart + 1 < _history.Count)
                    ApplyMoveCellAnalysisStyle(_movesGrid.Rows[rowIndex].Cells[2], pairStart + 1);

                AddVariationRowsForParent(pairStart);
                if (pairStart + 1 < _history.Count)
                    AddVariationRowsForParent(pairStart + 1);
            }

            _movesGrid.ClearSelection();
            if (_historyIndex > 0)
            {
                int colIndex = ((_historyIndex - 1) % 2 == 0) ? 1 : 2;
                DataGridViewRow? row = _movesGrid.Rows
                    .Cast<DataGridViewRow>()
                    .FirstOrDefault(candidate => candidate.Cells[colIndex].Tag is int ply && ply == _historyIndex);
                if (row != null)
                {
                    if (colIndex >= 0 && colIndex < row.Cells.Count)
                    {
                        _movesGrid.CurrentCell = row.Cells[colIndex];
                        row.Cells[colIndex].Selected = true;
                    }
                }
            }

            UpdateMoveNavButtons();
            _suppressGridSelection = false;
        }

        private void UpdateMoveNavButtons()
        {
            _jumpStartButton.Enabled = _historyIndex > 0 || _history.Count > 1;
            _stepBackButton.Enabled = _historyIndex > 0;
            _stepForwardButton.Enabled = _historyIndex < _history.Count - 1;
            _jumpEndButton.Enabled = _historyIndex < _history.Count - 1;
        }

        // Fast path for a clean forward move append (no truncation, no variations):
        // append just the new move's cell instead of rebuilding the whole grid. The
        // full RefreshMoveGrid is O(n) per move -> O(n^2) over a game, which is what
        // made analysis-board self-play lag worse with every move.
        private void AppendMoveToGrid(int ply)
        {
            _suppressGridSelection = true;
            DataGridViewCell currentCell;
            if ((ply % 2) == 1)
            {
                // White move: start a new move-number row.
                int moveNumber = ((ply - 1) / 2) + 1;
                int rowIndex = _movesGrid.Rows.Add(moveNumber.ToString() + ".", FormatMoveGridText(ply), string.Empty);
                currentCell = _movesGrid.Rows[rowIndex].Cells[1];
                currentCell.Tag = ply;
                ApplyMoveCellAnalysisStyle(currentCell, ply);
            }
            else
            {
                // Black move: fill the black cell of the last (white-move) row.
                int lastRow = _movesGrid.Rows.Count - 1;
                currentCell = _movesGrid.Rows[lastRow].Cells[2];
                currentCell.Value = FormatMoveGridText(ply);
                currentCell.Tag = ply;
                ApplyMoveCellAnalysisStyle(currentCell, ply);
            }

            _movesGrid.ClearSelection();
            currentCell.Selected = true;
            _movesGrid.CurrentCell = currentCell;

            UpdateMoveNavButtons();
            _suppressGridSelection = false;
        }

        private void ApplyMoveGridSelectionToBoard()
        {
            if (_suppressGridSelection || _movesGrid.CurrentCell == null)
                return;

            DataGridViewCell cell = _movesGrid.CurrentCell;
            if (cell.RowIndex < 0 || cell.ColumnIndex < 1)
                return;

            if (_movesGrid.Rows[cell.RowIndex].Tag is VariationLine)
                return;

            if (cell.Tag is not int targetHistoryIndex)
                return;

            if (targetHistoryIndex < 1 || targetHistoryIndex >= _history.Count || targetHistoryIndex == _historyIndex)
                return;

            JumpToHistory(targetHistoryIndex);
        }

        private void AddVariationRowsForParent(int parentIndex)
        {
            if (!_variationsByParentIndex.TryGetValue(parentIndex, out var lines) || lines.Count == 0)
                return;

            foreach (VariationLine line in lines.Where(line => line.Entries.Count > 0))
            {
                string indicator = parentIndex == _historyIndex ? "v" : ">";
                int rowIndex = _movesGrid.Rows.Add(indicator, BuildVariationPreview(line), string.Empty);
                DataGridViewRow row = _movesGrid.Rows[rowIndex];
                row.Tag = line;
                row.Height = Math.Max(row.Height, 30);
                GameAnalysisMoveResult? rowAnalysis = GetWorstVariationAnalysis(line);
                foreach (DataGridViewCell cell in row.Cells)
                {
                    if (rowAnalysis != null)
                    {
                        ApplyMoveCellAnalysisStyle(cell, rowAnalysis);
                        cell.Style.BackColor = Blend(cell.Style.BackColor, Color.FromArgb(38, 36, 44), 0.45f);
                    }
                    else
                    {
                        cell.Style.BackColor = Color.FromArgb(38, 36, 44);
                        cell.Style.ForeColor = Color.FromArgb(205, 186, 235);
                        cell.Style.SelectionBackColor = Color.FromArgb(76, 58, 110);
                        cell.Style.SelectionForeColor = Color.White;
                    }

                    cell.ToolTipText = "Click to make this variation the main line.";
                }
            }
        }

        private string BuildVariationPreview(VariationLine line)
        {
            var parts = new List<string>();
            int parentIndex = line.ParentIndex;
            for (int i = 0; i < Math.Min(8, line.Entries.Count); i++)
            {
                int ply = parentIndex + 1 + i;
                string moveText = FormatVariationMoveText(line, i);
                if (string.IsNullOrWhiteSpace(moveText))
                    continue;

                int moveNumber = ((ply - 1) / 2) + 1;
                if (ply % 2 == 1)
                    parts.Add($"{moveNumber.ToString(CultureInfo.InvariantCulture)}.{moveText}");
                else if (i == 0)
                    parts.Add($"{moveNumber.ToString(CultureInfo.InvariantCulture)}...{moveText}");
                else
                    parts.Add(moveText);
            }

            string preview = string.Join(" ", parts);
            if (line.Entries.Count > 8)
                preview += " ...";
            return preview;
        }

        private string FormatVariationMoveText(VariationLine line, int offset)
        {
            if (offset < 0 || offset >= line.Entries.Count)
                return string.Empty;

            string moveText = StripResultSuffix(line.Entries[offset].MoveText ?? string.Empty);
            if (!line.AnalysisByOffset.TryGetValue(offset, out GameAnalysisMoveResult? analysis))
                return moveText;

            string evalText = FormatCompactAnalysisEval(analysis);
            string classText = FormatCompactAnalysisClass(analysis.Classification);
            if (string.IsNullOrWhiteSpace(evalText))
                return $"{moveText} {classText}";

            return $"{moveText} {evalText} {classText}";
        }

        private static GameAnalysisMoveResult? GetWorstVariationAnalysis(VariationLine line)
        {
            return line.AnalysisByOffset.Values
                .OrderByDescending(analysis => AnalysisSeverity(analysis.Classification))
                .FirstOrDefault();
        }

        private static int AnalysisSeverity(string classification)
        {
            return classification switch
            {
                "Blunder" => 7,
                "Miss" => 6,
                "Mistake" => 5,
                "Inaccuracy" => 4,
                "Ok" => 3,
                "Good" => 2,
                "Best" => 1,
                "Book" => 1,
                _ => 0
            };
        }

        private string FormatMoveGridText(int plyIndex)
        {
            if (plyIndex <= 0 || plyIndex >= _history.Count)
                return string.Empty;

            string moveText = _history[plyIndex].MoveText ?? string.Empty;
            if (!_moveAnalysisByPly.TryGetValue(plyIndex, out GameAnalysisMoveResult? analysis))
                return moveText;

            string evalText = FormatCompactAnalysisEval(analysis);
            string classText = FormatCompactAnalysisClass(analysis.Classification);
            if (string.IsNullOrWhiteSpace(evalText))
                return $"{moveText}  {classText}";

            return $"{moveText}  {evalText}  {classText}";
        }

        private static string FormatCompactAnalysisEval(GameAnalysisMoveResult analysis)
        {
            if (analysis.IsMateScore && analysis.MateIn.HasValue)
                return $"M{Math.Abs(analysis.MateIn.Value).ToString(CultureInfo.InvariantCulture)}";

            return analysis.EvalBefore.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        }

        private static string FormatCompactAnalysisClass(string classification)
        {
            return classification switch
            {
                "Inaccuracy" => "Inacc",
                _ => string.IsNullOrWhiteSpace(classification) ? "" : classification
            };
        }

        private void ApplyMoveCellAnalysisStyle(DataGridViewCell cell, int plyIndex)
        {
            if (!_moveAnalysisByPly.TryGetValue(plyIndex, out GameAnalysisMoveResult? analysis))
                return;

            ApplyMoveCellAnalysisStyle(cell, analysis);
        }

        private static void ApplyMoveCellAnalysisStyle(DataGridViewCell cell, GameAnalysisMoveResult analysis)
        {
            Color back;
            Color fore;
            switch (analysis.Classification)
            {
                case "Book":
                    back = Color.FromArgb(28, 42, 64);
                    fore = Color.FromArgb(148, 190, 255);
                    break;
                case "Best":
                    back = Color.FromArgb(26, 58, 40);
                    fore = Color.FromArgb(142, 236, 178);
                    break;
                case "Good":
                    back = Color.FromArgb(28, 52, 68);
                    fore = Color.FromArgb(128, 210, 255);
                    break;
                case "Ok":
                    back = Color.FromArgb(48, 48, 42);
                    fore = Color.FromArgb(225, 225, 205);
                    break;
                case "Miss":
                    back = Color.FromArgb(50, 34, 68);
                    fore = Color.FromArgb(213, 174, 255);
                    break;
                case "Inaccuracy":
                    back = Color.FromArgb(52, 42, 24);
                    fore = Color.FromArgb(255, 201, 94);
                    break;
                case "Mistake":
                    back = Color.FromArgb(62, 36, 26);
                    fore = Color.FromArgb(255, 159, 96);
                    break;
                case "Blunder":
                    back = Color.FromArgb(66, 28, 34);
                    fore = Color.FromArgb(255, 120, 136);
                    break;
                default:
                    back = Color.FromArgb(40, 40, 44);
                    fore = Color.FromArgb(210, 210, 215);
                    break;
            }

            cell.Style.BackColor = back;
            cell.Style.ForeColor = fore;
            cell.Style.SelectionBackColor = Color.FromArgb(
                Math.Min(255, back.R + 32),
                Math.Min(255, back.G + 48),
                Math.Min(255, back.B + 64));
            cell.Style.SelectionForeColor = Color.White;
            cell.ToolTipText = BuildMoveAnalysisTooltip(analysis);
        }

        private static Color Blend(Color foreground, Color background, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            int r = (int)Math.Round(foreground.R + ((background.R - foreground.R) * amount));
            int g = (int)Math.Round(foreground.G + ((background.G - foreground.G) * amount));
            int b = (int)Math.Round(foreground.B + ((background.B - foreground.B) * amount));
            return Color.FromArgb(r, g, b);
        }

        private static string BuildMoveAnalysisTooltip(GameAnalysisMoveResult analysis)
        {
            string eval = FormatCompactAnalysisEval(analysis);
            string loss = analysis.Loss <= 0
                ? "0"
                : analysis.Loss.ToString("0", CultureInfo.InvariantCulture);
            string best = string.IsNullOrWhiteSpace(analysis.BestMove) ? "-" : analysis.BestMove;
            return $"{analysis.Classification} | Eval {eval} | Best {best} | Loss {loss} cp | Depth {analysis.Depth.ToString(CultureInfo.InvariantCulture)}";
        }

        private void JumpToHistory(int index)
        {
            if (index < 0 || index >= _history.Count || index == _historyIndex)
                return;

            _board = ChessBoard.LoadFromFen(_history[index].Fen, AutoEndgameRules.All);
            _historyIndex = index;
            ClearSelection();
            ResetDragState(clearSelection: false);
            SyncStatus();
            RefreshMoveGrid();
            ApplyStoredGameAnalysisPreviewForCurrentHistory();
            Invalidate();
            EmitSnapshot();
        }

        private void ApplyStoredGameAnalysisPreviewForCurrentHistory()
        {
            if (!string.Equals(_analysisMode, "OFF", StringComparison.OrdinalIgnoreCase))
                return;

            if (_historyIndex <= 0 || !_moveAnalysisByPly.TryGetValue(_historyIndex, out GameAnalysisMoveResult? analysis))
            {
                ClearAnalysisVariations();
                ClearAnalysisArrows();
                return;
            }

            double whitePerspectiveAfter = analysis.IsWhiteMove
                ? analysis.EvalAfterForMover
                : -analysis.EvalAfterForMover;

            var variation = new MoveVariation
            {
                Rank = 1,
                Depth = analysis.Depth,
                Score = whitePerspectiveAfter,
                ScoreType = analysis.IsMateScore ? "mate" : "cp",
                MateIn = analysis.MateIn,
                Moves = string.IsNullOrWhiteSpace(analysis.BestMove) || analysis.BestMove == "-"
                    ? new List<string>()
                    : new List<string> { analysis.BestMove }
            };

            _analysisVariations = new List<MoveVariation> { variation };
            _analysisIsBlackPerspective = false;
            _analysisDepth = analysis.Depth;
            _analysisStatusText = string.IsNullOrWhiteSpace(analysis.BestMove) || analysis.BestMove == "-"
                ? $"{analysis.Classification}: no engine best move stored."
                : $"{analysis.Classification}: best was {analysis.BestMove}.";

            if (string.Equals(analysis.Classification, "Miss", StringComparison.OrdinalIgnoreCase) &&
                TryCreateAnalysisBestMoveArrow(analysis, out MoveArrow? missedMoveArrow))
            {
                _analysisArrows = new List<MoveArrow> { missedMoveArrow! };
                Invalidate(_boardRect);
            }
            else
            {
                ClearAnalysisArrows();
            }

            Invalidate(_analysisPanelRect);
            Invalidate(_evalBarRect);
        }

        private static bool TryCreateAnalysisBestMoveArrow(GameAnalysisMoveResult analysis, out MoveArrow? arrow)
        {
            arrow = null;
            if (string.IsNullOrWhiteSpace(analysis.BestMove) || analysis.BestMove == "-" || string.IsNullOrWhiteSpace(analysis.FenBefore))
                return false;

            try
            {
                ChessBoard board = ChessBoard.LoadFromFen(analysis.FenBefore, AutoEndgameRules.All);
                Move? bestMove = ResolveMoveTokenForBoard(board, analysis.BestMove);
                if (bestMove == null)
                    return false;

                arrow = new MoveArrow
                {
                    FromFile = bestMove.OriginalPosition.X,
                    FromRank = bestMove.OriginalPosition.Y,
                    ToFile = bestMove.NewPosition.X,
                    ToRank = bestMove.NewPosition.Y,
                    Strength = 1,
                    IsFlipped = false,
                    PromotionPiece = bestMove.IsPromotion && bestMove.Promotion != null
                        ? char.ToLowerInvariant(bestMove.Promotion.Type.AsChar)
                        : '\0',
                    MovingSide = board.Turn == PieceColor.White ? 'w' : 'b',
                    Depth = analysis.Depth
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StepHistory(int delta)
        {
            if (_history.Count == 0 || delta == 0)
                return;

            int targetIndex = Math.Clamp(_historyIndex + delta, 0, _history.Count - 1);
            JumpToHistory(targetIndex);
        }

        private void ClearMoveHistoryFromCurrentRoot()
        {
            if (_historyIndex != 0 || _history.Count <= 1)
                return;

            DialogResult result = MessageBox.Show(
                this,
                "Clear the move list and all saved variations?",
                "Clear analysis board moves",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
                return;

            ClearLoadedGameMetadata();
            _moveAnalysisByPly.Clear();
            _variationsByParentIndex.Clear();
            _history.RemoveRange(1, _history.Count - 1);
            ClearSelection();
            ResetDragState(clearSelection: false);
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            SyncStatus();
            RefreshMoveGrid();
            QueueOpeningBookLookup();
            Invalidate();
            EmitSnapshot();
        }

        private void EmitSnapshot()
        {
            bool isShown = Visible && WindowState != FormWindowState.Minimized;
            bool isEffectivelyVisible = isShown && IsEffectivelyVisibleOnScreen();
            SnapshotChanged?.Invoke(new AnalysisBoardSnapshot
            {
                Visible = isShown,
                BoardFlipped = _boardFlipped,
                HasTrackedHistory = _history.Count > 1,
                Fen = _board.ToFen(),
                BoardScreenBounds = isShown ? RectangleToScreen(_boardRect) : Rectangle.Empty,
                WindowScreenBounds = isShown ? Bounds : Rectangle.Empty
            });
        }

        private void MonitorUiResponsiveness()
        {
            long elapsedMs = _uiWatchdogStopwatch.ElapsedMilliseconds;
            _uiWatchdogStopwatch.Restart();

            if (elapsedMs >= 750)
            {
                DebugRuntime.WriteLine($"[UI] Analysis board UI stall detected: {elapsedMs}ms");
            }
        }

        private bool IsEffectivelyVisibleOnScreen()
        {
            if (!Visible || WindowState == FormWindowState.Minimized)
                return false;

            Rectangle boardScreenBounds = RectangleToScreen(_boardRect);
            if (boardScreenBounds.Width < 120 || boardScreenBounds.Height < 120)
                return false;

            const double minimumVisibleAreaRatio = 0.45;
            const int minimumVisibleSampleHits = 3;

            foreach (var screen in Screen.AllScreens)
            {
                Rectangle visibleBounds = Rectangle.Intersect(boardScreenBounds, screen.Bounds);
                if (visibleBounds.Width < 120 || visibleBounds.Height < 120)
                    continue;

                double visibleAreaRatio = (double)(visibleBounds.Width * visibleBounds.Height) /
                                          (boardScreenBounds.Width * boardScreenBounds.Height);
                if (visibleAreaRatio < minimumVisibleAreaRatio)
                    continue;

                int inset = 24;
                var samplePoints = new[]
                {
                    new Point(visibleBounds.Left + visibleBounds.Width / 2, visibleBounds.Top + visibleBounds.Height / 2),
                    new Point(Math.Min(visibleBounds.Right - inset, visibleBounds.Left + inset), Math.Min(visibleBounds.Bottom - inset, visibleBounds.Top + inset)),
                    new Point(Math.Max(visibleBounds.Left + inset, visibleBounds.Right - inset), Math.Min(visibleBounds.Bottom - inset, visibleBounds.Top + inset)),
                    new Point(Math.Min(visibleBounds.Right - inset, visibleBounds.Left + inset), Math.Max(visibleBounds.Top + inset, visibleBounds.Bottom - inset)),
                    new Point(Math.Max(visibleBounds.Left + inset, visibleBounds.Right - inset), Math.Max(visibleBounds.Top + inset, visibleBounds.Bottom - inset))
                };

                int visibleHits = 0;
                foreach (var sample in samplePoints)
                {
                    IntPtr hitHandle = WindowFromPoint(sample);
                    if (hitHandle == IntPtr.Zero)
                        continue;

                    IntPtr rootHandle = GetAncestor(hitHandle, GA_ROOT);
                    if (rootHandle == Handle)
                        visibleHits++;
                }

                if (visibleHits >= minimumVisibleSampleHits)
                {
                    return true;
                }
            }

            return false;
        }

        private void LayoutControls()
        {
            int margin = Dpi.Scale(this, 16);
            int top = Dpi.Scale(this, 14);
            int buttonHeight = ControlHeight(_resetButton.Font, 8, 32);
            int labelHeight = ControlHeight(_analysisEngineLabel.Font, 6, 26);
            int compactButtonHeight = ControlHeight(_analysisEngineComboBox.Font, 7, 30);
            int modeButtonHeight = ControlHeight(_analysisBothButton.Font, 8, 32);
            int matchButtonHeight = ControlHeight(_matchToggleButton.Font, 9, 36);
            int navHeight = ControlHeight(_jumpStartButton.Font, 9, 38);
            int movesHeaderHeight = ControlHeight(_movesHeaderLabel.Font, 6, 32);
            int gridRowHeight = ControlHeight(_movesGrid.DefaultCellStyle.Font, 6, 28);
            int spacing = Dpi.Scale(this, 8);
            int sidebarWidth = Dpi.Scale(this, 270);
            int fenHeight = Dpi.Scale(this, 34);
            int turnHeight = ControlHeight(_turnLabel.Font, 6, 26);
            int bottomMargin = Dpi.Scale(this, 18);
            _movesGrid.RowTemplate.Height = gridRowHeight;
            foreach (DataGridViewRow row in _movesGrid.Rows)
                row.Height = gridRowHeight;

            int commandX = margin;
            int commandY = top;
            void PlaceCommand(Button button, int width)
            {
                if (commandX > margin && commandX + width > ClientSize.Width - margin)
                {
                    commandX = margin;
                    commandY += buttonHeight + spacing;
                }

                button.SetBounds(commandX, commandY, width, buttonHeight);
                commandX = button.Right + spacing;
            }

            PlaceCommand(_resetButton, Dpi.Scale(this, 90));
            PlaceCommand(_undoButton, Dpi.Scale(this, 90));
            PlaceCommand(_clipboardButton, Dpi.Scale(this, 116));
            PlaceCommand(_matchAnalyzeGameButton, Dpi.Scale(this, 204));
            PlaceCommand(_flipButton, Dpi.Scale(this, 90));
            PlaceCommand(_mirrorButton, Dpi.Scale(this, 124));
            PlaceCommand(_bookToggleButton, Dpi.Scale(this, 116));
            PlaceCommand(_restoreSizeButton, Dpi.Scale(this, 186));

            int fenTop = ClientSize.Height - bottomMargin - fenHeight;
            _fenTextBox.SetBounds(margin, fenTop, ClientSize.Width - margin * 2, fenHeight);
            _turnLabel.SetBounds(margin, fenTop - turnHeight - Dpi.Scale(this, 8), Dpi.Scale(this, 240), turnHeight);

            int boardTop = Math.Max(_resetButton.Bottom, _restoreSizeButton.Bottom) + Dpi.Scale(this, 18);
            int boardInfoHeight = Dpi.Scale(this, 34);
            int boardInfoGap = Dpi.Scale(this, 4);
            int availableHeight = Math.Max(Dpi.Scale(this, 320), _turnLabel.Top - boardTop - Dpi.Scale(this, 10) - boardInfoHeight * 2 - boardInfoGap * 2);
            int inlineEvalWidth = Dpi.Scale(this, 50);
            int evalGap = Dpi.Scale(this, 8);
            int availableWidth = Math.Max(Dpi.Scale(this, 320), ClientSize.Width - margin * 3 - sidebarWidth - inlineEvalWidth - evalGap);
            int boardSize = Math.Min(availableWidth, availableHeight);
            _boardTopInfoRect = new Rectangle(margin, boardTop, boardSize, boardInfoHeight);
            _boardRect = new Rectangle(margin, _boardTopInfoRect.Bottom + boardInfoGap, boardSize, boardSize);
            _boardBottomInfoRect = new Rectangle(margin, _boardRect.Bottom + boardInfoGap, boardSize, boardInfoHeight);

            Rectangle boardPanel = GetBoardPanelRect();
            _evalBarRect = new Rectangle(boardPanel.Right + evalGap, boardPanel.Top, inlineEvalWidth, boardPanel.Height);
            int sidebarLeft = _evalBarRect.Right + Dpi.Scale(this, 10);
            int sidebarWidthActual = Math.Max(Dpi.Scale(this, 220), ClientSize.Width - sidebarLeft - margin);
            _sidebarRect = new Rectangle(sidebarLeft, boardPanel.Top, sidebarWidthActual, boardPanel.Height);

            int visibleAnalysisRows = Math.Max(_analysisLineCount, 3);
            int analysisPanelMinHeight = Dpi.Scale(this, 262) + (visibleAnalysisRows * Math.Max(24, TextHeight(_analysisEngineLabel.Font) + 4));
            int analysisPanelHeight = Math.Min(Dpi.Scale(this, 430), Math.Max(analysisPanelMinHeight, _sidebarRect.Height / 4));
            _analysisPanelRect = new Rectangle(_sidebarRect.Left + Dpi.Scale(this, 12), _sidebarRect.Top + Dpi.Scale(this, 12), _sidebarRect.Width - Dpi.Scale(this, 24), analysisPanelHeight);

            int modeTop = _analysisPanelRect.Top + Dpi.Scale(this, 48);
            int modeGap = Dpi.Scale(this, 8);
            int modeButtonWidth = (_analysisPanelRect.Width - Dpi.Scale(this, 20) - modeGap * 2) / 3;
            _analysisWhiteButton.SetBounds(_analysisPanelRect.Left + Dpi.Scale(this, 10), modeTop, modeButtonWidth, modeButtonHeight);
            _analysisBlackButton.SetBounds(_analysisWhiteButton.Right + modeGap, modeTop, modeButtonWidth, modeButtonHeight);
            _analysisBothButton.SetBounds(_analysisBlackButton.Right + modeGap, modeTop, modeButtonWidth, modeButtonHeight);

            int configTop = _analysisWhiteButton.Bottom + Dpi.Scale(this, 10);
            int controlsLeft = _analysisPanelRect.Left + Dpi.Scale(this, 10);
            int controlsWidth = _analysisPanelRect.Width - Dpi.Scale(this, 20);
            int analysisLabelWidth = Dpi.Scale(this, 88);
            int depthTop = configTop;
            _analysisDepthLabel.SetBounds(controlsLeft, depthTop, analysisLabelWidth, labelHeight);
            _analysisLinesComboBox.SetBounds(_analysisPanelRect.Right - Dpi.Scale(this, 64), depthTop - 1, Dpi.Scale(this, 54), compactButtonHeight);
            _analysisLinesLabel.SetBounds(_analysisLinesComboBox.Left - Dpi.Scale(this, 50), depthTop, Dpi.Scale(this, 44), labelHeight);
            _analysisDepthValueLabel.SetBounds(_analysisLinesLabel.Left - Dpi.Scale(this, 58), depthTop, Dpi.Scale(this, 52), labelHeight);
            _analysisDepthSlider.SetBounds(_analysisDepthLabel.Right + Dpi.Scale(this, 8), depthTop - 2, _analysisDepthValueLabel.Left - (_analysisDepthLabel.Right + Dpi.Scale(this, 14)), compactButtonHeight);
            _analysisInfiniteCheckBox.SetBounds(controlsLeft, depthTop, 1, 1);

            int tuningTop = _analysisDepthSlider.Bottom + Dpi.Scale(this, 10);
            int tuningGap = Dpi.Scale(this, 10);
            if (controlsWidth >= Dpi.Scale(this, 560))
            {
                int engineLabelWidth = Dpi.Scale(this, 52);
                int threadsLabelWidth = Dpi.Scale(this, 58);
                int threadsComboWidth = Dpi.Scale(this, 72);
                int hashLabelWidth = Dpi.Scale(this, 42);
                int hashComboWidth = Dpi.Scale(this, 98);
                int engineComboLeft = controlsLeft + engineLabelWidth + Dpi.Scale(this, 8);
                int trailingWidth =
                    tuningGap + threadsLabelWidth + Dpi.Scale(this, 6) + threadsComboWidth +
                    tuningGap + hashLabelWidth + Dpi.Scale(this, 6) + hashComboWidth;
                int engineComboWidth = Math.Max(Dpi.Scale(this, 150), controlsWidth - engineLabelWidth - Dpi.Scale(this, 8) - trailingWidth);

                _analysisEngineLabel.SetBounds(controlsLeft, tuningTop, engineLabelWidth, labelHeight);
                _analysisEngineComboBox.SetBounds(engineComboLeft, tuningTop - 1, engineComboWidth, compactButtonHeight);

                int threadsLeft = _analysisEngineComboBox.Right + tuningGap;
                _analysisThreadsLabel.SetBounds(threadsLeft, tuningTop, threadsLabelWidth, labelHeight);
                _analysisThreadsComboBox.SetBounds(_analysisThreadsLabel.Right + Dpi.Scale(this, 6), tuningTop - 1, threadsComboWidth, compactButtonHeight);

                int hashLeft = _analysisThreadsComboBox.Right + tuningGap;
                _analysisHashLabel.SetBounds(hashLeft, tuningTop, hashLabelWidth, labelHeight);
                _analysisHashComboBox.SetBounds(_analysisHashLabel.Right + Dpi.Scale(this, 6), tuningTop - 1, Math.Max(Dpi.Scale(this, 74), controlsLeft + controlsWidth - (_analysisHashLabel.Right + Dpi.Scale(this, 6))), compactButtonHeight);
            }
            else
            {
                _analysisEngineLabel.SetBounds(controlsLeft, tuningTop, analysisLabelWidth, labelHeight);
                _analysisEngineComboBox.SetBounds(_analysisEngineLabel.Right + Dpi.Scale(this, 8), tuningTop - 1, controlsWidth - _analysisEngineLabel.Width - Dpi.Scale(this, 8), compactButtonHeight);

                int compactTop = _analysisEngineComboBox.Bottom + Dpi.Scale(this, 8);
                int tuneColumnWidth = (controlsWidth - tuningGap) / 2;
                int tuneLabelWidth = Dpi.Scale(this, 68);
                _analysisThreadsLabel.SetBounds(controlsLeft, compactTop, tuneLabelWidth, labelHeight);
                _analysisThreadsComboBox.SetBounds(_analysisThreadsLabel.Right + Dpi.Scale(this, 8), compactTop - 1, Math.Max(Dpi.Scale(this, 58), tuneColumnWidth - tuneLabelWidth - Dpi.Scale(this, 8)), compactButtonHeight);
                int hashLeft = controlsLeft + tuneColumnWidth + tuningGap;
                _analysisHashLabel.SetBounds(hashLeft, compactTop, Dpi.Scale(this, 50), labelHeight);
                _analysisHashComboBox.SetBounds(_analysisHashLabel.Right + Dpi.Scale(this, 8), compactTop - 1, controlsLeft + controlsWidth - (_analysisHashLabel.Right + Dpi.Scale(this, 8)), compactButtonHeight);
            }
            _analysisEngineComboBox.DropDownWidth = Math.Max(_analysisEngineComboBox.Width, Dpi.Scale(this, 260));

            int requiredMatchHeight = Dpi.Scale(this, 86) + matchButtonHeight * 3 + Dpi.Scale(this, 16) + Dpi.Scale(this, 10) + compactButtonHeight * 2 + Dpi.Scale(this, 8) + Dpi.Scale(this, 10) + buttonHeight * 2 + Dpi.Scale(this, 8) + Dpi.Scale(this, 20);
            int matchPanelHeight = Math.Max(Dpi.Scale(this, 372), requiredMatchHeight);
            _matchPanelRect = new Rectangle(_sidebarRect.Left + Dpi.Scale(this, 12), _analysisPanelRect.Bottom + Dpi.Scale(this, 12), _sidebarRect.Width - Dpi.Scale(this, 24), matchPanelHeight);

            int matchPad = Dpi.Scale(this, 10);
            int matchLeft = _matchPanelRect.Left + matchPad;
            int matchTop = _matchPanelRect.Top + Dpi.Scale(this, 86);
            int matchInnerWidth = _matchPanelRect.Width - matchPad * 2;
            int matchGap = Dpi.Scale(this, 8);

            int actionWidth = (matchInnerWidth - matchGap) / 2;
            _matchToggleButton.SetBounds(matchLeft, matchTop, actionWidth, matchButtonHeight);
            _matchPauseButton.SetBounds(_matchToggleButton.Right + matchGap, matchTop, actionWidth, matchButtonHeight);
            _matchRestartGameButton.SetBounds(matchLeft, _matchToggleButton.Bottom + Dpi.Scale(this, 8), actionWidth, matchButtonHeight);
            _matchRestartMatchButton.SetBounds(_matchRestartGameButton.Right + matchGap, _matchPauseButton.Bottom + Dpi.Scale(this, 8), actionWidth, matchButtonHeight);
            _matchResetScoreButton.SetBounds(matchLeft, _matchRestartGameButton.Bottom + Dpi.Scale(this, 8), matchInnerWidth, matchButtonHeight);

            int matchEngineTop = _matchResetScoreButton.Bottom + Dpi.Scale(this, 10);
            int matchLabelWidth = Dpi.Scale(this, 86);
            _matchWhiteEngineLabel.SetBounds(matchLeft, matchEngineTop, matchLabelWidth, labelHeight);
            _matchWhiteEngineButton.SetBounds(matchLeft + matchLabelWidth + Dpi.Scale(this, 10), matchEngineTop - 1, matchInnerWidth - matchLabelWidth - Dpi.Scale(this, 10), compactButtonHeight);

            int blackTop = _matchWhiteEngineButton.Bottom + Dpi.Scale(this, 8);
            _matchBlackEngineLabel.SetBounds(matchLeft, blackTop, matchLabelWidth, labelHeight);
            _matchBlackEngineButton.SetBounds(matchLeft + matchLabelWidth + Dpi.Scale(this, 10), blackTop - 1, matchInnerWidth - matchLabelWidth - Dpi.Scale(this, 10), compactButtonHeight);

            int timeSelectorWidth = Math.Min(Dpi.Scale(this, 132), Math.Max(Dpi.Scale(this, 104), matchInnerWidth / 4));
            _matchTimeButton.SetBounds(_matchPanelRect.Right - matchPad - timeSelectorWidth, _matchPanelRect.Top + Dpi.Scale(this, 8), timeSelectorWidth, compactButtonHeight);
            int matchHeaderLabelGap = Dpi.Scale(this, 6);
            int timeLabelWidth = Dpi.Scale(this, 76);
            int lengthLabelWidth = Dpi.Scale(this, 90);
            _matchTimeLabel.SetBounds(_matchTimeButton.Left - matchHeaderLabelGap - timeLabelWidth, _matchPanelRect.Top + Dpi.Scale(this, 10), timeLabelWidth, labelHeight);
            int lengthWidth = Math.Min(Dpi.Scale(this, 112), Math.Max(Dpi.Scale(this, 92), matchInnerWidth / 5));
            _matchLengthButton.SetBounds(_matchTimeLabel.Left - Dpi.Scale(this, 10) - lengthWidth, _matchPanelRect.Top + Dpi.Scale(this, 8), lengthWidth, compactButtonHeight);
            _matchLengthLabel.SetBounds(_matchLengthButton.Left - matchHeaderLabelGap - lengthLabelWidth, _matchPanelRect.Top + Dpi.Scale(this, 10), lengthLabelWidth, labelHeight);

            int pgnTop = _matchBlackEngineButton.Bottom + Dpi.Scale(this, 10);
            int pgnButtonWidth = (matchInnerWidth - matchGap) / 2;
            _matchCopyGamePgnButton.SetBounds(matchLeft, pgnTop, pgnButtonWidth, buttonHeight);
            _matchCopyMatchPgnButton.SetBounds(_matchCopyGamePgnButton.Right + matchGap, pgnTop, pgnButtonWidth, buttonHeight);
            _matchClearPgnButton.SetBounds(matchLeft, _matchCopyGamePgnButton.Bottom + Dpi.Scale(this, 8), matchInnerWidth, buttonHeight);

            int movesTop = _matchPanelRect.Bottom + Dpi.Scale(this, 12);
            if (_openingBookEnabled)
            {
                int bookHeight = Math.Min(Dpi.Scale(this, 182), Math.Max(Dpi.Scale(this, 158), _sidebarRect.Height / 5));
                _bookPanelRect = new Rectangle(_sidebarRect.Left + Dpi.Scale(this, 12), movesTop, _sidebarRect.Width - Dpi.Scale(this, 24), bookHeight);
                movesTop = _bookPanelRect.Bottom + Dpi.Scale(this, 14);
            }
            else
            {
                _bookPanelRect = Rectangle.Empty;
            }

            _movesHeaderLabel.SetBounds(_sidebarRect.Left + Dpi.Scale(this, 12), movesTop, _sidebarRect.Width - Dpi.Scale(this, 24), movesHeaderHeight);

            int navGap = Dpi.Scale(this, 8);
            int navTop = _sidebarRect.Bottom - navHeight - Dpi.Scale(this, 12);
            int navButtonWidth = (_sidebarRect.Width - Dpi.Scale(this, 24) - navGap * 3) / 4;

            _movesGrid.SetBounds(_sidebarRect.Left + Dpi.Scale(this, 12), _movesHeaderLabel.Bottom + Dpi.Scale(this, 8), _sidebarRect.Width - Dpi.Scale(this, 24), navTop - (_movesHeaderLabel.Bottom + Dpi.Scale(this, 8)) - Dpi.Scale(this, 10));
            _jumpStartButton.SetBounds(_sidebarRect.Left + Dpi.Scale(this, 12), navTop, navButtonWidth, navHeight);
            _stepBackButton.SetBounds(_jumpStartButton.Right + navGap, navTop, navButtonWidth, navHeight);
            _stepForwardButton.SetBounds(_stepBackButton.Right + navGap, navTop, navButtonWidth, navHeight);
            _jumpEndButton.SetBounds(_stepForwardButton.Right + navGap, navTop, navButtonWidth, navHeight);
        }

        private static ChessBoard CreateNewBoard()
        {
            return new ChessBoard
            {
                AutoEndgameRules = AutoEndgameRules.All
            };
        }

        private void ApplySavedWindowSize()
        {
            try
            {
                var settings = _appSettingsManager.Load();
                if (settings.AnalysisBoardDefaultSizeVersion < 2 &&
                    settings.AnalysisBoardWindowWidth <= 1200 &&
                    settings.AnalysisBoardWindowHeight <= 940)
                {
                    settings.AnalysisBoardWindowWidth = 0;
                    settings.AnalysisBoardWindowHeight = 0;
                    settings.AnalysisBoardDefaultSizeVersion = 2;
                    _appSettingsManager.Save(settings);
                }

                if (settings.AnalysisBoardWindowWidth < MinimumSize.Width || settings.AnalysisBoardWindowHeight < MinimumSize.Height)
                {
                    Size = GetDefaultWindowSize(MinimumSize);
                    return;
                }

                _applyingSavedWindowSize = true;
                Size = new Size(settings.AnalysisBoardWindowWidth, settings.AnalysisBoardWindowHeight);
            }
            finally
            {
                _applyingSavedWindowSize = false;
            }
        }

        private static Size GetDefaultWindowSize(Size minimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int width = Math.Max(minimum.Width, (int)Math.Round(workArea.Width * DefaultWindowScreenRatio));
            int height = Math.Max(minimum.Height, (int)Math.Round(workArea.Height * DefaultWindowScreenRatio));
            return new Size(width, height);
        }

        private void SaveWindowSize()
        {
            if (_applyingSavedWindowSize || WindowState != FormWindowState.Normal)
                return;

            var settings = _appSettingsManager.Load();
            settings.AnalysisBoardWindowWidth = Width;
            settings.AnalysisBoardWindowHeight = Height;
            _appSettingsManager.Save(settings);
        }

        private void QueueOpeningBookLookup()
        {
            if (!_openingBookEnabled || !IsHandleCreated)
                return;

            string fen = _board.ToFen();
            if (string.Equals(fen, _lastBookFen, StringComparison.Ordinal))
                return;

            _lastBookFen = fen;
            OpeningBookView? cached;
            lock (OpeningBookCache)
            {
                OpeningBookCache.TryGetValue(fen, out cached);
            }

            if (cached != null)
            {
                ApplyOpeningBookView(fen, cached);
                return;
            }

            _bookLookupCts?.Cancel();
            _bookLookupCts = new CancellationTokenSource();
            var token = _bookLookupCts.Token;

            _bookMoves.Clear();
            _bookOpeningTitle = "";
            _bookStatusText = "Reading embedded opening book...";
            Invalidate(_bookPanelRect);

            try
            {
                var view = BuildEmbeddedOpeningBookView();
                lock (OpeningBookCache)
                {
                    OpeningBookCache[fen] = view;
                }

                if (!token.IsCancellationRequested)
                    ApplyOpeningBookView(fen, view);
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    _bookMoves.Clear();
                    _bookOpeningTitle = "";
                    _bookStatusText = $"Book unavailable: {ex.Message}";
                    Invalidate(_bookPanelRect);
                }
            }
        }

        private OpeningBookView BuildEmbeddedOpeningBookView()
        {
            var playedMoves = GetBookComparablePlayedMoves();

            var candidates = new Dictionary<string, OpeningBookMove>(StringComparer.OrdinalIgnoreCase);
            string openingName = "";
            int openingDepth = -1;

            foreach (var line in EmbeddedOpeningBook.Value.Lines)
            {
                int compareCount = Math.Min(playedMoves.Count, line.Moves.Count);
                bool prefixMatches = true;
                for (int i = 0; i < compareCount; i++)
                {
                    if (!string.Equals(line.Moves[i], playedMoves[i], StringComparison.OrdinalIgnoreCase))
                    {
                        prefixMatches = false;
                        break;
                    }
                }

                if (!prefixMatches)
                    continue;

                if (line.Moves.Count <= playedMoves.Count)
                {
                    if (line.Moves.Count > openingDepth)
                    {
                        openingDepth = line.Moves.Count;
                        openingName = line.Name;
                    }

                    continue;
                }

                if (playedMoves.Count > openingDepth)
                {
                    openingDepth = playedMoves.Count;
                    openingName = line.Name;
                }

                string nextMove = line.Moves[playedMoves.Count];
                if (candidates.TryGetValue(nextMove, out var existing))
                {
                    candidates[nextMove] = new OpeningBookMove
                    {
                        San = existing.San,
                        Popularity = existing.Popularity + line.Weight,
                        LineCount = existing.LineCount + 1,
                        ExampleLine = PreferShorterExample(existing.ExampleLine, line.Name)
                    };
                }
                else
                {
                    candidates[nextMove] = new OpeningBookMove
                    {
                        San = nextMove,
                        Popularity = line.Weight,
                        LineCount = 1,
                        ExampleLine = line.Name
                    };
                }
            }

            return new OpeningBookView
            {
                OpeningName = playedMoves.Count == 0 ? "" : openingName,
                Moves = candidates.Values
                    .OrderByDescending(move => move.Popularity)
                    .ThenBy(move => move.San, StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        private List<string> GetBookComparablePlayedMoves()
        {
            var moves = new List<string>();
            int count = Math.Clamp(_historyIndex + 1, 1, _history.Count);
            if (count <= 1)
                return moves;

            var board = CreateNewBoard();
            for (int i = 1; i < count; i++)
            {
                string rawMove = StripResultSuffix(_history[i].MoveText ?? string.Empty);
                Move? move = ResolveMoveTokenForBoard(board, rawMove);
                if (move == null)
                {
                    string fallback = NormalizeMoveToken(rawMove);
                    if (!string.IsNullOrWhiteSpace(fallback))
                        moves.Add(fallback);
                    break;
                }

                moves.Add(NormalizeMoveToken(move.San ?? rawMove));
                board.Move(move);
            }

            return moves;
        }

        private static EmbeddedOpeningBookData LoadEmbeddedOpeningBook()
        {
            Assembly assembly = typeof(AnalysisBoardForm).Assembly;
            string[] resourceNames = assembly.GetManifestResourceNames()
                .Where(name => Regex.IsMatch(name, @"Assets\.lichess_openings\.[a-e]\.tsv$", RegexOptions.IgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (resourceNames.Length == 0)
                return new EmbeddedOpeningBookData();

            var openings = new List<EmbeddedOpeningLine>();
            foreach (string resourceName in resourceNames)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    continue;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? header = reader.ReadLine();
                if (header == null)
                    continue;

                while (reader.ReadLine() is { } line)
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 3)
                        continue;

                    string eco = parts[0].Trim();
                    string name = parts[1].Trim();
                    string pgn = parts[2].Trim();
                    var moves = SplitOpeningMoves(pgn);
                    if (moves.Count == 0)
                        continue;

                    var opening = new EmbeddedOpeningLine
                    {
                        Eco = eco,
                        Name = string.IsNullOrWhiteSpace(eco) ? name : $"{eco} {name}",
                        Moves = moves,
                        Weight = 1
                    };
                    openings.Add(opening);
                }
            }

            return new EmbeddedOpeningBookData
            {
                Lines = openings
            };
        }

        private static List<string> SplitOpeningMoves(string moves)
        {
            return Regex.Split(moves, @"\s+")
                .SelectMany(SplitJoinedMoveNumber)
                .Select(NormalizeMoveToken)
                .Where(move => !string.IsNullOrWhiteSpace(move))
                .Where(move => !Regex.IsMatch(move, @"^\d+\.(\.\.)?$"))
                .Where(move => !IsPgnHeaderOrResult(move))
                .ToList();
        }

        private static IEnumerable<string> SplitJoinedMoveNumber(string token)
        {
            token = token.Trim();
            if (string.IsNullOrWhiteSpace(token))
                yield break;

            var match = Regex.Match(token, @"^\d+\.(\.\.)?(.+)$");
            if (match.Success)
            {
                string move = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(move))
                    yield return move;
                yield break;
            }

            yield return token;
        }

        private static string PreferShorterExample(string current, string candidate)
        {
            if (string.IsNullOrWhiteSpace(current))
                return candidate;
            if (string.IsNullOrWhiteSpace(candidate))
                return current;
            return candidate.Length < current.Length ? candidate : current;
        }

        private void ApplyOpeningBookView(string fen, OpeningBookView view)
        {
            if (!_openingBookEnabled || !string.Equals(fen, _lastBookFen, StringComparison.Ordinal))
                return;

            _bookOpeningTitle = view.OpeningName;
            _bookMoves = view.Moves.Take(BuildLimits.OpeningBookMoveLimit).ToList();
            _bookStatusText = _bookMoves.Count == 0
                ? "No embedded-book moves found."
                : "";
            Invalidate(_bookPanelRect);
        }

        private static string FormatCompactCount(int value)
        {
            if (value >= 1_000_000)
                return (value / 1_000_000.0).ToString("0.0M", CultureInfo.InvariantCulture);
            if (value >= 1_000)
                return (value / 1_000.0).ToString("0.0K", CultureInfo.InvariantCulture);
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string TrimToWidth(Graphics g, string text, Font font, int maxWidth)
        {
            if (string.IsNullOrWhiteSpace(text) || g.MeasureString(text, font).Width <= maxWidth)
                return text;

            const string ellipsis = "...";
            string trimmed = text;
            while (trimmed.Length > 4 && g.MeasureString(trimmed + ellipsis, font).Width > maxWidth)
            {
                trimmed = trimmed[..^1];
            }

            return trimmed + ellipsis;
        }

        private static Button CreateButton(string text, EventHandler onClick)
        {
            var button = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                FlatAppearance =
                {
                    BorderColor = Color.FromArgb(78, 78, 82),
                    MouseOverBackColor = Color.FromArgb(60, 60, 64),
                    MouseDownBackColor = Color.FromArgb(72, 72, 76)
                }
            };
            button.Click += onClick;
            return button;
        }

        private static ComboBox CreateSelectorComboBox()
        {
            var comboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22,
                IntegralHeight = false,
                DropDownWidth = 220
            };
            comboBox.DrawItem += DrawSelectorComboItem;
            return comboBox;
        }

        private static void DrawSelectorComboItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox comboBox || e.Index < 0)
                return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using var backBrush = new SolidBrush(selected ? Color.FromArgb(60, 60, 64) : Color.FromArgb(45, 45, 48));
            using var textBrush = new SolidBrush(Color.White);
            e.Graphics.FillRectangle(backBrush, e.Bounds);
            string text = comboBox.Items[e.Index]?.ToString() ?? "";
            var textRect = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                text,
                comboBox.Font,
                textRect,
                Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private Button CreateAnalysisModeButton(string text, string mode)
        {
            var button = CreateButton(text, (_, _) => ToggleAnalysisMode(mode));
            button.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            return button;
        }

        private void ToggleAnalysisMode(string mode)
        {
            _analysisMode = _analysisMode == mode ? "OFF" : mode;
            RefreshAnalysisModeButtons();
            AnalysisModeChanged?.Invoke(_analysisMode);
        }

        private void StopAnalysisBoardWorkForHide()
        {
            _analysisMode = "OFF";
            RefreshAnalysisModeButtons();
            ClearAnalysisArrows();
            ClearAnalysisVariations();
            SetAnalysisStatus("Select W, B, or W+B to start analysis.");
            AnalysisModeChanged?.Invoke(_analysisMode);
            MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.StopRunning);
        }

        private void LoadAnalysisSettings()
        {
            var settings = _appSettingsManager.Load();
            _analysisTargetDepth = BuildLimits.ClampDepth(settings.AnalysisBoardDepth <= 0 ? 12 : settings.AnalysisBoardDepth);
            _analysisInfinite = BuildLimits.AllowInfiniteAnalysis && settings.AnalysisBoardInfinite;
            _analysisLineCount = BuildLimits.ClampLines(settings.AnalysisBoardLineCount);
            _analysisThreads = BuildLimits.ClampThreads(settings.AnalysisBoardThreads);
            _analysisHashMb = BuildLimits.ClampHashMb(settings.AnalysisBoardHashMb);

            if (!string.IsNullOrWhiteSpace(settings.AnalysisBoardEnginePath))
            {
                _selectedAnalysisEngine = _analysisEngineManager.AvailableEngines
                    .FirstOrDefault(e => string.Equals(e.ExecutablePath, settings.AnalysisBoardEnginePath, StringComparison.OrdinalIgnoreCase));
            }

            if (_selectedAnalysisEngine == null && !string.IsNullOrWhiteSpace(settings.AnalysisBoardEngineFileName))
            {
                _selectedAnalysisEngine = _analysisEngineManager.AvailableEngines
                    .FirstOrDefault(e => string.Equals(e.FileName, settings.AnalysisBoardEngineFileName, StringComparison.OrdinalIgnoreCase));
            }

            _selectedAnalysisEngine ??= _analysisEngineManager.CurrentEngine ?? _analysisEngineManager.AvailableEngines.FirstOrDefault();
        }

        private void LoadMatchSettings()
        {
            var settings = _appSettingsManager.Load();
            _matchTimeControlKey = string.IsNullOrWhiteSpace(settings.AnalysisBoardMatchTimeControlKey)
                ? "3 min"
                : settings.AnalysisBoardMatchTimeControlKey;
            _matchBaseSeconds = MatchTimeControls.FirstOrDefault(p => string.Equals(p.Label, _matchTimeControlKey, StringComparison.OrdinalIgnoreCase)).Seconds;
            if (_matchBaseSeconds <= 0)
            {
                _matchTimeControlKey = "3 min";
                _matchBaseSeconds = 180;
            }
            if (_matchBaseSeconds > BuildLimits.MatchMaxSeconds)
            {
                var fallback = MatchTimeControls.LastOrDefault(p => p.Seconds <= BuildLimits.MatchMaxSeconds && p.Seconds > 0);
                _matchTimeControlKey = fallback.Label ?? "1 min";
                _matchBaseSeconds = fallback.Seconds > 0 ? fallback.Seconds : BuildLimits.MatchMaxSeconds;
            }
            _matchBaseSeconds = BuildLimits.ClampMatchSeconds(_matchBaseSeconds);
            _matchGameLimit = BuildLimits.ClampMatchGameLimit(settings.AnalysisBoardMatchGameLimit);

            if (!string.IsNullOrWhiteSpace(settings.AnalysisBoardMatchWhiteEnginePath))
            {
                _selectedMatchWhiteEngine = _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    string.Equals(e.ExecutablePath, settings.AnalysisBoardMatchWhiteEnginePath, StringComparison.OrdinalIgnoreCase));
            }

            if (_selectedMatchWhiteEngine == null && !string.IsNullOrWhiteSpace(settings.AnalysisBoardMatchWhiteEngineFileName))
            {
                _selectedMatchWhiteEngine = _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    string.Equals(e.FileName, settings.AnalysisBoardMatchWhiteEngineFileName, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(settings.AnalysisBoardMatchBlackEnginePath))
            {
                _selectedMatchBlackEngine = _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    string.Equals(e.ExecutablePath, settings.AnalysisBoardMatchBlackEnginePath, StringComparison.OrdinalIgnoreCase));
            }

            if (_selectedMatchBlackEngine == null && !string.IsNullOrWhiteSpace(settings.AnalysisBoardMatchBlackEngineFileName))
            {
                _selectedMatchBlackEngine = _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    string.Equals(e.FileName, settings.AnalysisBoardMatchBlackEngineFileName, StringComparison.OrdinalIgnoreCase));
            }

            _selectedMatchWhiteEngine ??= _analysisEngineManager.CurrentEngine ?? _analysisEngineManager.AvailableEngines.FirstOrDefault();
            _selectedMatchBlackEngine ??= _selectedMatchWhiteEngine ?? _analysisEngineManager.AvailableEngines.FirstOrDefault();
            _matchWhiteWins = Math.Max(0, settings.AnalysisBoardMatchWhiteWins);
            _matchBlackWins = Math.Max(0, settings.AnalysisBoardMatchBlackWins);
            _matchDraws = Math.Max(0, settings.AnalysisBoardMatchDraws);
            _matchWhiteClockText = FormatClock(TimeSpan.FromSeconds(_matchBaseSeconds));
            _matchBlackClockText = FormatClock(TimeSpan.FromSeconds(_matchBaseSeconds));
        }

        private void SaveAnalysisSettings()
        {
            var settings = _appSettingsManager.Load();
            settings.AnalysisBoardEngineFileName = _selectedAnalysisEngine?.FileName ?? "";
            settings.AnalysisBoardEnginePath = _selectedAnalysisEngine?.ExecutablePath ?? "";
            settings.AnalysisBoardDepth = BuildLimits.ClampDepth(_analysisTargetDepth);
            settings.AnalysisBoardInfinite = BuildLimits.AllowInfiniteAnalysis && _analysisInfinite;
            settings.AnalysisBoardLineCount = BuildLimits.ClampLines(_analysisLineCount);
            settings.AnalysisBoardThreads = BuildLimits.ClampThreads(_analysisThreads);
            settings.AnalysisBoardHashMb = BuildLimits.ClampHashMb(_analysisHashMb);
            _appSettingsManager.Save(settings);
        }

        private void SaveMatchSettings()
        {
            var settings = _appSettingsManager.Load();
            settings.AnalysisBoardMatchWhiteEngineFileName = _selectedMatchWhiteEngine?.FileName ?? "";
            settings.AnalysisBoardMatchBlackEngineFileName = _selectedMatchBlackEngine?.FileName ?? "";
            settings.AnalysisBoardMatchWhiteEnginePath = _selectedMatchWhiteEngine?.ExecutablePath ?? "";
            settings.AnalysisBoardMatchBlackEnginePath = _selectedMatchBlackEngine?.ExecutablePath ?? "";
            settings.AnalysisBoardMatchTimeControlKey = _matchTimeControlKey;
            settings.AnalysisBoardMatchGameLimit = BuildLimits.ClampMatchGameLimit(_matchGameLimit);
            settings.AnalysisBoardMatchWhiteWins = _matchWhiteWins;
            settings.AnalysisBoardMatchBlackWins = _matchBlackWins;
            settings.AnalysisBoardMatchDraws = _matchDraws;
            _appSettingsManager.Save(settings);
        }

        private void ToggleMirrorMode()
        {
            if (_matchRunning)
            {
                SetMatchStatus("Stop the engine match before using mirror mode.");
                return;
            }

            _mirrorModeEnabled = !_mirrorModeEnabled;
            RefreshMirrorButton();
            MirrorModeChanged?.Invoke(_mirrorModeEnabled);
        }

        private void RequestToggleMatchRunning()
        {
            if (!_matchRunning && _mirrorModeEnabled)
            {
                SetMatchStatus("Turn mirror mode off before starting an engine match.");
                return;
            }

            MatchCommandRequested?.Invoke(AnalysisBoardMatchCommandType.ToggleRunning);
        }

        private void ToggleOpeningBook()
        {
            _openingBookEnabled = !_openingBookEnabled;
            RefreshBookButton();
            LayoutControls();

            if (_openingBookEnabled)
            {
                _lastBookFen = "";
                QueueOpeningBookLookup();
            }
            else
            {
                _bookLookupCts?.Cancel();
                _bookMoves.Clear();
                _bookOpeningTitle = "";
                _bookStatusText = "Book disabled.";
            }

            Invalidate();
        }

        private void RefreshMirrorButton()
        {
            UpdateAnalysisModeButton(_mirrorButton, _mirrorModeEnabled);
            _mirrorButton.Text = _mirrorModeEnabled ? "Mirror On" : "Mirror";
            _mirrorButton.Enabled = !_matchRunning;
        }

        private void RefreshBookButton()
        {
            UpdateAnalysisModeButton(_bookToggleButton, _openingBookEnabled);
            _bookToggleButton.Text = _openingBookEnabled ? "Book On" : "Book";
        }

        private void RefreshAnalysisModeButtons()
        {
            UpdateAnalysisModeButton(_analysisWhiteButton, _analysisMode == "WHITE");
            UpdateAnalysisModeButton(_analysisBlackButton, _analysisMode == "BLACK");
            UpdateAnalysisModeButton(_analysisBothButton, _analysisMode == "BOTH");
        }

        private void RefreshAnalysisConfigButtons()
        {
            _analysisDepthSlider.Enabled = true;
            _analysisInfinite = BuildLimits.AllowInfiniteAnalysis && _analysisInfinite;
            _analysisTargetDepth = BuildLimits.ClampDepth(_analysisTargetDepth);
            _analysisLineCount = BuildLimits.ClampLines(_analysisLineCount);
            _analysisThreads = BuildLimits.ClampThreads(_analysisThreads);
            _analysisHashMb = BuildLimits.ClampHashMb(_analysisHashMb);
            if (_analysisDepthSlider.Maximum != GetAnalysisDepthSliderMaximum())
                _analysisDepthSlider.Maximum = GetAnalysisDepthSliderMaximum();

            if (_analysisInfiniteCheckBox.Checked != _analysisInfinite)
                _analysisInfiniteCheckBox.Checked = _analysisInfinite;

            int snapped = _analysisInfinite ? InfiniteDepthSliderValue : SnapDepth(_analysisTargetDepth);
            if (_analysisDepthSlider.Value != snapped)
                _analysisDepthSlider.Value = snapped;

            _analysisDepthValueLabel.Text = _analysisInfinite ? "\u221E" : _analysisTargetDepth.ToString(CultureInfo.InvariantCulture);
            RefreshAnalysisSelectorValues();
        }

        private void RefreshMatchConfigButtons()
        {
            _matchToggleButton.Text = _matchRunning ? "Stop Match" : "Start Match";
            _matchToggleButton.Enabled = _matchRunning || !_mirrorModeEnabled;
            _matchPauseButton.Text = _matchPaused ? "Resume" : "Pause";
            _matchPauseButton.Enabled = _matchRunning;
            _matchWhiteEngineButton.Text = TrimToControlText(_selectedMatchWhiteEngine?.Name ?? "No engine", 18);
            _matchBlackEngineButton.Text = TrimToControlText(_selectedMatchBlackEngine?.Name ?? "No engine", 18);
            _matchTimeButton.Text = _matchTimeControlKey;
            _matchLengthButton.Text = _matchGameLimit <= 0 ? "Unlimited" : _matchGameLimit.ToString(CultureInfo.InvariantCulture);
            _matchCopyMatchPgnButton.Enabled = BuildLimits.AllowMatchPgnExport && _archivedMatchGames.Count > 0;
            _matchAnalyzeGameButton.Enabled = CanAnalyzeCurrentGame();
            RefreshMirrorButton();
            Invalidate(_matchPanelRect);
        }

        private void RefreshAnalysisEngineList(bool preserveSelection)
        {
            string? currentPath = preserveSelection ? _selectedAnalysisEngine?.ExecutablePath : null;
            string? currentFile = preserveSelection ? _selectedAnalysisEngine?.FileName : null;
            string? currentWhitePath = preserveSelection ? _selectedMatchWhiteEngine?.ExecutablePath : null;
            string? currentWhiteFile = preserveSelection ? _selectedMatchWhiteEngine?.FileName : null;
            string? currentBlackPath = preserveSelection ? _selectedMatchBlackEngine?.ExecutablePath : null;
            string? currentBlackFile = preserveSelection ? _selectedMatchBlackEngine?.FileName : null;

            _analysisEngineManager.ScanForEngines();
            _analysisEngineManager.LoadSettings();

            _selectedAnalysisEngine =
                _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(currentPath) &&
                    string.Equals(e.ExecutablePath, currentPath, StringComparison.OrdinalIgnoreCase))
                ?? _analysisEngineManager.AvailableEngines.FirstOrDefault(e =>
                    !string.IsNullOrWhiteSpace(currentFile) &&
                    string.Equals(e.FileName, currentFile, StringComparison.OrdinalIgnoreCase))
                ?? _analysisEngineManager.CurrentEngine
                ?? _analysisEngineManager.AvailableEngines.FirstOrDefault();

            RefreshAnalysisSelectorValues();
            SaveAnalysisSettings();
            RefreshAnalysisConfigButtons();
            _selectedMatchWhiteEngine = _analysisEngineManager.AvailableEngines
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(currentWhitePath) && string.Equals(e.ExecutablePath, currentWhitePath, StringComparison.OrdinalIgnoreCase))
                ?? (!string.IsNullOrWhiteSpace(currentWhiteFile)
                    ? _analysisEngineManager.AvailableEngines.FirstOrDefault(e => string.Equals(e.FileName, currentWhiteFile, StringComparison.OrdinalIgnoreCase))
                    : _analysisEngineManager.AvailableEngines.FirstOrDefault());

            _selectedMatchBlackEngine = _analysisEngineManager.AvailableEngines
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(currentBlackPath) && string.Equals(e.ExecutablePath, currentBlackPath, StringComparison.OrdinalIgnoreCase))
                ?? (!string.IsNullOrWhiteSpace(currentBlackFile)
                    ? _analysisEngineManager.AvailableEngines.FirstOrDefault(e => string.Equals(e.FileName, currentBlackFile, StringComparison.OrdinalIgnoreCase))
                    : _selectedMatchWhiteEngine ?? _analysisEngineManager.AvailableEngines.FirstOrDefault());
            RefreshMatchEngineMenus();
            RefreshMatchConfigButtons();
            SaveMatchSettings();
        }

        private void QueueAnalysisEngineRefresh()
        {
            if (!IsHandleCreated)
                return;

            void Refresh()
            {
                RefreshAnalysisEngineList(preserveSelection: true);
                AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
                Invalidate(_analysisPanelRect);
            }

            if (InvokeRequired)
                BeginInvoke(new Action(Refresh));
            else
                Refresh();
        }

        private static int SnapDepth(int value)
        {
            return Math.Clamp(value, 0, GetAnalysisDepthSliderMaximum());
        }

        private static int GetAnalysisDepthSliderMaximum() => BuildLimits.AllowInfiniteAnalysis ? InfiniteDepthSliderValue : BuildLimits.MaxDepth;

        private void RefreshAnalysisSelectorValues()
        {
            _suppressAnalysisSelectorEvents = true;
            try
            {
                RefreshAnalysisEngineSelector();
                RefreshNumericSelector(
                    _analysisLinesComboBox,
                    Enumerable.Range(1, BuildLimits.MaxLines)
                        .Select(v => new SelectorItem { Label = v.ToString(CultureInfo.InvariantCulture), Value = v }),
                    _analysisLineCount);
                RefreshNumericSelector(
                    _analysisThreadsComboBox,
                    new[] { 1, 2, 4, 6, 8, 12, 16 }
                        .Where(v => v <= BuildLimits.MaxThreads)
                        .Select(v => new SelectorItem { Label = v.ToString(CultureInfo.InvariantCulture), Value = v }),
                    _analysisThreads);
                RefreshNumericSelector(
                    _analysisHashComboBox,
                    new[] { 16, 32, 64, 128, 256, 512, 1024 }
                        .Where(v => v <= BuildLimits.MaxHashMb)
                        .Select(v => new SelectorItem { Label = $"{v.ToString(CultureInfo.InvariantCulture)} MB", Value = v }),
                    _analysisHashMb);
            }
            finally
            {
                _suppressAnalysisSelectorEvents = false;
            }
        }

        private void RefreshAnalysisEngineSelector()
        {
            string selectedPath = _selectedAnalysisEngine?.ExecutablePath ?? "";
            _analysisEngineComboBox.BeginUpdate();
            try
            {
                _analysisEngineComboBox.Items.Clear();
                int selectedIndex = -1;
                foreach (var engine in _analysisEngineManager.AvailableEngines)
                {
                    int index = _analysisEngineComboBox.Items.Add(new EngineSelectorItem { Engine = engine });
                    if (selectedIndex < 0 && string.Equals(engine.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase))
                        selectedIndex = index;
                }

                if (selectedIndex < 0 && _analysisEngineComboBox.Items.Count > 0)
                    selectedIndex = 0;

                _analysisEngineComboBox.SelectedIndex = selectedIndex;
            }
            finally
            {
                _analysisEngineComboBox.EndUpdate();
            }
        }

        private static void RefreshNumericSelector(ComboBox comboBox, IEnumerable<SelectorItem> items, int selectedValue)
        {
            comboBox.BeginUpdate();
            try
            {
                comboBox.Items.Clear();
                int selectedIndex = -1;
                foreach (SelectorItem item in items)
                {
                    int index = comboBox.Items.Add(item);
                    if (selectedIndex < 0 && item.Value == selectedValue)
                        selectedIndex = index;
                }

                if (selectedIndex < 0 && comboBox.Items.Count > 0)
                    selectedIndex = 0;

                comboBox.SelectedIndex = selectedIndex;
            }
            finally
            {
                comboBox.EndUpdate();
            }
        }

        private void ApplyAnalysisEngineSelectionFromCombo()
        {
            if (_suppressAnalysisSelectorEvents || _analysisEngineComboBox.SelectedItem is not EngineSelectorItem item)
                return;

            if (_selectedAnalysisEngine != null &&
                string.Equals(_selectedAnalysisEngine.ExecutablePath, item.Engine.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedAnalysisEngine = item.Engine;
            SaveAnalysisSettings();
            AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
            Invalidate(_analysisPanelRect);
        }

        private void ApplyAnalysisLineSelectionFromCombo()
        {
            if (_suppressAnalysisSelectorEvents || _analysisLinesComboBox.SelectedItem is not SelectorItem item)
                return;

            int next = BuildLimits.ClampLines(item.Value);
            if (_analysisLineCount == next)
                return;

            _analysisLineCount = next;
            SaveAnalysisSettings();
            AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
            Invalidate(_analysisPanelRect);
        }

        private void ApplyAnalysisThreadsSelectionFromCombo()
        {
            if (_suppressAnalysisSelectorEvents || _analysisThreadsComboBox.SelectedItem is not SelectorItem item)
                return;

            int next = BuildLimits.ClampThreads(item.Value);
            if (_analysisThreads == next)
                return;

            _analysisThreads = next;
            SaveAnalysisSettings();
            AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
            MatchSettingsChanged?.Invoke(GetMatchSettings());
            Invalidate(_analysisPanelRect);
            Invalidate(_matchPanelRect);
        }

        private void ApplyAnalysisHashSelectionFromCombo()
        {
            if (_suppressAnalysisSelectorEvents || _analysisHashComboBox.SelectedItem is not SelectorItem item)
                return;

            int next = BuildLimits.ClampHashMb(item.Value);
            if (_analysisHashMb == next)
                return;

            _analysisHashMb = next;
            SaveAnalysisSettings();
            AnalysisSettingsChanged?.Invoke(GetAnalysisSettings());
            MatchSettingsChanged?.Invoke(GetMatchSettings());
            Invalidate(_analysisPanelRect);
            Invalidate(_matchPanelRect);
        }

        private void RefreshMatchEngineMenus()
        {
            void Populate(ContextMenuStrip menu, bool isWhiteMenu)
            {
                menu.Items.Clear();
                foreach (var engine in _analysisEngineManager.AvailableEngines)
                {
                    bool isSelected = isWhiteMenu
                        ? _selectedMatchWhiteEngine != null && string.Equals(engine.ExecutablePath, _selectedMatchWhiteEngine.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        : _selectedMatchBlackEngine != null && string.Equals(engine.ExecutablePath, _selectedMatchBlackEngine.ExecutablePath, StringComparison.OrdinalIgnoreCase);

                    var item = new ToolStripMenuItem(engine.Name)
                    {
                        Checked = isSelected,
                        BackColor = Color.FromArgb(38, 38, 41),
                        ForeColor = Color.White
                    };

                    item.Click += (_, _) =>
                    {
                        if (isWhiteMenu)
                            _selectedMatchWhiteEngine = engine;
                        else
                            _selectedMatchBlackEngine = engine;

                        SaveMatchSettings();
                        RefreshMatchConfigButtons();
                        MatchSettingsChanged?.Invoke(GetMatchSettings());
                        Invalidate(_matchPanelRect);
                    };

                    menu.Items.Add(item);
                }
            }

            Populate(_matchWhiteEngineMenu, isWhiteMenu: true);
            Populate(_matchBlackEngineMenu, isWhiteMenu: false);
        }

        private void RefreshMatchTimeMenu()
        {
            _matchTimeMenu.Items.Clear();
            foreach (var preset in MatchTimeControls.Where(p => p.Seconds <= BuildLimits.MatchMaxSeconds))
            {
                string label = preset.Label;
                int seconds = preset.Seconds;
                var item = new ToolStripMenuItem(label)
                {
                    Checked = string.Equals(_matchTimeControlKey, label, StringComparison.OrdinalIgnoreCase),
                    BackColor = Color.FromArgb(38, 38, 41),
                    ForeColor = Color.White
                };
                item.Click += (_, _) =>
                {
                    _matchTimeControlKey = label;
                    _matchBaseSeconds = seconds;
                    _matchWhiteClockText = FormatClock(TimeSpan.FromSeconds(_matchBaseSeconds));
                    _matchBlackClockText = FormatClock(TimeSpan.FromSeconds(_matchBaseSeconds));
                    SaveMatchSettings();
                    RefreshMatchConfigButtons();
                    MatchSettingsChanged?.Invoke(GetMatchSettings());
                    Invalidate(_matchPanelRect);
                };
                _matchTimeMenu.Items.Add(item);
            }
        }

        private void RefreshMatchLengthMenu()
        {
            _matchLengthMenu.Items.Clear();
            foreach (int limit in MatchGameLimits.Where(limit => BuildLimits.MatchGameLimit == int.MaxValue || (limit > 0 && limit <= BuildLimits.MatchGameLimit)))
            {
                int selectedLimit = limit;
                string label = selectedLimit <= 0 ? "Unlimited" : selectedLimit.ToString(CultureInfo.InvariantCulture);
                var item = new ToolStripMenuItem(label)
                {
                    Checked = _matchGameLimit == selectedLimit,
                    BackColor = Color.FromArgb(38, 38, 41),
                    ForeColor = Color.White
                };
                item.Click += (_, _) =>
                {
                    _matchGameLimit = selectedLimit;
                    SaveMatchSettings();
                    RefreshMatchConfigButtons();
                    MatchSettingsChanged?.Invoke(GetMatchSettings());
                    Invalidate(_matchPanelRect);
                };
                _matchLengthMenu.Items.Add(item);
            }
        }

        private static ContextMenuStrip CreateDarkMenu()
        {
            var menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = Color.FromArgb(38, 38, 41),
                ForeColor = Color.White,
                Renderer = new ToolStripProfessionalRenderer(new DarkColorTable())
            };
            return menu;
        }

        private static string TrimToControlText(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
                return text;
            return text[..Math.Max(1, maxChars - 3)] + "...";
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuBorder => Color.FromArgb(78, 78, 82);
            public override Color MenuItemBorder => Color.FromArgb(100, 130, 130, 130);
            public override Color MenuItemSelected => Color.FromArgb(60, 60, 64);
            public override Color ToolStripDropDownBackground => Color.FromArgb(38, 38, 41);
            public override Color ImageMarginGradientBegin => Color.FromArgb(38, 38, 41);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(38, 38, 41);
            public override Color ImageMarginGradientEnd => Color.FromArgb(38, 38, 41);
        }

        private static Label CreateConfigLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(225, 225, 225),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private static int TextHeight(Font? font)
        {
            return TextRenderer.MeasureText("Hg", font ?? SystemFonts.MessageBoxFont).Height;
        }

        private static int ControlHeight(Font? font, int verticalPadding, int minimum)
        {
            return Math.Max(minimum, TextHeight(font) + verticalPadding);
        }

        private static void UpdateAnalysisModeButton(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(75, 115, 68) : Color.FromArgb(47, 47, 50);
            button.ForeColor = selected ? Color.White : Color.FromArgb(225, 225, 225);
            button.FlatAppearance.BorderColor = selected ? Color.FromArgb(112, 170, 94) : Color.FromArgb(82, 82, 86);
        }

        private static Button CreateNavButton(string text, EventHandler onClick)
        {
            var button = CreateButton(text, onClick);
            button.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
            return button;
        }

        private void LoadPieceImages()
        {
            _pieceImages.Clear();

            var expectedFiles = new Dictionary<char, string>
            {
                ['K'] = "wK.png",
                ['Q'] = "wQ.png",
                ['R'] = "wR.png",
                ['B'] = "wB.png",
                ['N'] = "wN.png",
                ['P'] = "wP.png",
                ['k'] = "bK.png",
                ['q'] = "bQ.png",
                ['r'] = "bR.png",
                ['b'] = "bB.png",
                ['n'] = "bN.png",
                ['p'] = "bP.png"
            };

            foreach (var entry in expectedFiles)
            {
                using Stream? stream = OpenEmbeddedPieceStream(entry.Value);
                if (stream == null)
                    continue;
                using var sourceImage = Image.FromStream(stream);
                _pieceImages[entry.Key] = new Bitmap(sourceImage);
            }
        }

        private static Stream? OpenEmbeddedPieceStream(string fileName)
        {
            Assembly assembly = typeof(AnalysisBoardForm).Assembly;
            string suffix = PieceAssetResourceFolder + fileName;
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(resourceName)
                ? null
                : assembly.GetManifestResourceStream(resourceName);
        }

        private void DrawPiece(Graphics g, Piece piece, Rectangle squareRect)
        {
            char fenChar = piece.ToFenChar();
            if (_pieceImages.TryGetValue(fenChar, out var image))
            {
                int inset = Math.Max(2, squareRect.Width / 18);
                Rectangle imageRect = Rectangle.Inflate(squareRect, -inset, -inset);
                g.DrawImage(image, imageRect);
                return;
            }

            string glyph = GetPieceGlyph(piece);
            using var pieceBrush = new SolidBrush(piece.Color == PieceColor.White ? Color.White : Color.Black);
            using var outlineBrush = new SolidBrush(Color.FromArgb(80, 0, 0, 0));
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            RectangleF pieceRect = new RectangleF(squareRect.X, squareRect.Y - 2, squareRect.Width, squareRect.Height);
            RectangleF shadowRect = new RectangleF(pieceRect.X + 1, pieceRect.Y + 1, pieceRect.Width, pieceRect.Height);
            g.DrawString(glyph, _pieceFont, outlineBrush, shadowRect, centered);
            g.DrawString(glyph, _pieceFont, pieceBrush, pieceRect, centered);
        }

        private void DrawGhostPiece(Graphics g, Piece piece, Rectangle squareRect)
        {
            char fenChar = piece.ToFenChar();
            if (_pieceImages.TryGetValue(fenChar, out var image))
            {
                int inset = Math.Max(2, squareRect.Width / 18);
                Rectangle imageRect = Rectangle.Inflate(squareRect, -inset, -inset);
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                var matrix = new System.Drawing.Imaging.ColorMatrix
                {
                    Matrix33 = 0.34f
                };
                attributes.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
                g.DrawImage(image, imageRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                return;
            }

            string glyph = GetPieceGlyph(piece);
            using var pieceBrush = new SolidBrush(piece.Color == PieceColor.White
                ? Color.FromArgb(105, 255, 255, 255)
                : Color.FromArgb(118, 0, 0, 0));
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            RectangleF pieceRect = new RectangleF(squareRect.X, squareRect.Y - 2, squareRect.Width, squareRect.Height);
            g.DrawString(glyph, _pieceFont, pieceBrush, pieceRect, centered);
        }

        private static string GetPieceGlyph(Piece piece)
        {
            return piece.ToFenChar() switch
            {
                'K' => "♔",
                'Q' => "♕",
                'R' => "♖",
                'B' => "♗",
                'N' => "♘",
                'P' => "♙",
                'k' => "♚",
                'q' => "♛",
                'r' => "♜",
                'b' => "♝",
                'n' => "♞",
                'p' => "♟",
                _ => "?"
            };
        }

        private bool TryPerformMoveTo(Position targetSquare)
        {
            if (_matchRunning)
                return false;

            var selectedMove = _selectedMoves.FirstOrDefault(m => m.NewPosition == targetSquare);
            if (selectedMove == null)
                return false;

            try
            {
                string moveText = FormatMoveText(selectedMove);
                _board.Move(selectedMove);
                PushHistory(moveText, selectedMove);
                ClearSelection();
                SyncStatus();
                Invalidate();
                EmitSnapshot();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Illegal move", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private static string FormatMoveText(Move move)
        {
            if (!string.IsNullOrWhiteSpace(move.San))
                return move.San;

            string text = PositionToSquare(move.OriginalPosition) + PositionToSquare(move.NewPosition);
            if (move.IsMate)
                return text + "#";
            if (move.IsCheck)
                return text + "+";
            return text;
        }

        private static string ToUciMove(Move move)
        {
            string text = PositionToSquare(move.OriginalPosition) + PositionToSquare(move.NewPosition);
            if (move.IsPromotion && move.Promotion != null)
                text += char.ToLowerInvariant(move.Promotion.Type.AsChar);
            return text;
        }

        private static string PositionToSquare(Position position)
        {
            char file = (char)('a' + position.X);
            char rank = (char)('1' + position.Y);
            return $"{file}{rank}";
        }

        private static string FormatClock(TimeSpan remaining)
        {
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;

            if (remaining.TotalMinutes >= 1)
                return $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";

            return $"{remaining.Seconds:00}.{remaining.Milliseconds / 100:0}";
        }

        private static string NormalizeFenForRepetition(string fen)
        {
            if (string.IsNullOrWhiteSpace(fen))
                return string.Empty;

            string[] parts = fen.Split(' ');
            if (parts.Length < 4)
                return string.Empty;

            return string.Join(" ", parts.Take(4));
        }

        private void ResetDragState(bool clearSelection = true)
        {
            _mouseDownOnBoard = false;
            _mouseDownWasSelectedSquare = false;
            _isDraggingPiece = false;
            _dragOriginSquare = null;
            _dragHoverSquare = null;
            _dragPiece = null;
            if (clearSelection)
            {
                ClearSelection();
            }
        }

        private void ResetInteractiveUiState()
        {
            ResetDragState(clearSelection: false);
            CloseTransientMenus();
            ActiveControl = null;
        }

        private void CloseTransientMenus()
        {
            CloseMenuIfVisible(_clipboardMenu);
            CloseMenuIfVisible(_matchWhiteEngineMenu);
            CloseMenuIfVisible(_matchBlackEngineMenu);
            CloseMenuIfVisible(_matchTimeMenu);
            CloseMenuIfVisible(_matchLengthMenu);
            CloseMenuIfVisible(_matchGamePgnMenu);
        }

        private static void ToggleContextMenu(ContextMenuStrip? menu, Control? owner, Action refresh)
        {
            if (menu == null || menu.IsDisposed || owner == null || owner.IsDisposed)
                return;

            if (menu.Visible)
            {
                menu.Close(ToolStripDropDownCloseReason.CloseCalled);
                return;
            }

            refresh();
            menu.Show(owner, new Point(0, owner.Height));
        }

        private static void CloseMenuIfVisible(ContextMenuStrip? menu)
        {
            if (menu == null || menu.IsDisposed || !menu.Visible)
                return;

            menu.Close(ToolStripDropDownCloseReason.CloseCalled);
        }

        private void ForceForegroundInteractiveShow()
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            TopMost = true;
            ShowWindow(Handle, SW_SHOWNORMAL);
            BringWindowToTop(Handle);
            SetActiveWindow(Handle);
            SetForegroundWindow(Handle);
            BringToFront();
            Activate();
            Focus();
            TopMost = false;
        }

        private void DrawFloatingPiece(Graphics g, Piece piece, Point location, int squareSize)
        {
            char fenChar = piece.ToFenChar();
            Rectangle floatingRect = new Rectangle(location.X - squareSize / 2, location.Y - squareSize / 2, squareSize, squareSize);
            if (_pieceImages.TryGetValue(fenChar, out var image))
            {
                Rectangle imageRect = Rectangle.Inflate(floatingRect, -Math.Max(2, squareSize / 18), -Math.Max(2, squareSize / 18));
                g.DrawImage(image, imageRect);
                return;
            }

            string glyph = GetPieceGlyph(piece);
            using var pieceBrush = new SolidBrush(piece.Color == PieceColor.White ? Color.White : Color.Black);
            using var outlineBrush = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            RectangleF pieceRect = new RectangleF(location.X - squareSize / 2f, location.Y - squareSize / 2f - 2, squareSize, squareSize);
            RectangleF shadowRect = new RectangleF(pieceRect.X + 2, pieceRect.Y + 2, pieceRect.Width, pieceRect.Height);
            g.DrawString(glyph, _pieceFont, outlineBrush, shadowRect, centered);
            g.DrawString(glyph, _pieceFont, pieceBrush, pieceRect, centered);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bookLookupCts?.Cancel();
                _bookLookupCts?.Dispose();

                foreach (var image in _pieceImages.Values)
                {
                    image.Dispose();
                }

                _pieceImages.Clear();
                _pieceFont.Dispose();
                _coordFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private void AnalysisBoardForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                SaveWindowSize();
                e.Cancel = true;
                StopAnalysisBoardWorkForHide();
                ResetInteractiveUiState();
                Hide();
                EmitSnapshot();
                return;
            }

            _uiWatchdogTimer.Stop();
            SaveWindowSize();
        }

        private const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_SHOWNORMAL = 1;

        private sealed class OpeningBookMove
        {
            public string San { get; init; } = "";
            public int Popularity { get; init; }
            public int LineCount { get; init; }
            public string ExampleLine { get; init; } = "";
        }

        private sealed class OpeningBookView
        {
            public string OpeningName { get; init; } = "";
            public List<OpeningBookMove> Moves { get; init; } = new();
        }

        private sealed class EmbeddedOpeningBookData
        {
            public List<EmbeddedOpeningLine> Lines { get; init; } = new();
        }

        private sealed class EmbeddedOpeningLine
        {
            public string Eco { get; init; } = "";
            public string Name { get; init; } = "";
            public List<string> Moves { get; init; } = new();
            public int Weight { get; init; } = 1;
        }
    }
}


