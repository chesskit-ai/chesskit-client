using System.Globalization;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Text;

namespace ChessKit
{
    public sealed class GameAnalysisRequest
    {
        public string WhiteName { get; init; } = "White";
        public string BlackName { get; init; } = "Black";
        public string Result { get; init; } = "*";
        public string TimeControlKey { get; init; } = "Analysis";
        public List<GameAnalysisMoveInput> Moves { get; init; } = new();
    }

    public sealed class GameAnalysisMoveInput
    {
        public int PlyIndex { get; init; }
        public int MoveNumber { get; init; }
        public bool IsWhiteMove { get; init; }
        public string MoveText { get; init; } = "";
        public string FenBefore { get; init; } = "";
        public string FenAfter { get; init; } = "";
    }

    public sealed class GameAnalysisMoveResult
    {
        public int PlyIndex { get; init; }
        public int MoveNumber { get; init; }
        public bool IsWhiteMove { get; init; }
        public string MoveText { get; init; } = "";
        public string FenBefore { get; init; } = "";
        public string FenAfter { get; init; } = "";
        public string BestMove { get; set; } = "";
        public double EvalBefore { get; set; }
        public double EvalAfterForMover { get; set; }
        public double Loss { get; set; }
        public string Classification { get; set; } = "Book";
        public int Depth { get; set; }
        public bool IsMateScore { get; set; }
        public int? MateIn { get; set; }
    }

    public sealed class GameAnalysisSummary
    {
        public string SideName { get; init; } = "";
        public int BookMoves { get; set; }
        public int BestMoves { get; set; }
        public int GoodMoves { get; set; }
        public int OkMoves { get; set; }
        public int Misses { get; set; }
        public int Inaccuracies { get; set; }
        public int Mistakes { get; set; }
        public int Blunders { get; set; }
        public double AverageCentipawnLoss { get; set; }
        public double Accuracy { get; set; }
    }

    public sealed class GameAnalysisForm : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _subtitleLabel;
        private readonly Label _statusLabel;
        private readonly SurfacePanel _controlsPanel;
        private readonly Button _copyPgnButton;
        private readonly Button _coachButton;
        private readonly Button _analysisToggleButton;
        private readonly Label _depthCaptionLabel;
        private readonly TextBox _depthValueLabel;
        private readonly DepthScaleControl _depthSlider;
        private readonly ToolTip _depthTip = new() { AutoPopDelay = 15000, InitialDelay = 250, ReshowDelay = 120 };
        private readonly Panel _chartPanel;
        private readonly Panel _progressPanel;
        private readonly SmoothDataGridView _movesGrid;
        private readonly SummaryCard _whiteSummaryCard;
        private readonly SummaryCard _blackSummaryCard;
        private readonly Font _sectionFont;
        private readonly Font _bodyFont;
        private readonly Font _monoFont;
        private readonly AppSettingsManager _settingsManager =
            new(Path.Combine(AppContext.BaseDirectory, "settings.ini"));

        private GameAnalysisRequest? _request;
        private string _engineName = "Engine";
        private int _depth;
        private int _engineThreads;
        private int _engineHashMb;
        private string _annotatedPgn = "";
        private List<GameAnalysisMoveResult> _results = new();
        private bool _analysisRunning;
        private int _openingBoundaryIndex = -1;
        private int _endgameBoundaryIndex = -1;
        private int _progressPercent;
        private int _requestedDepth;
        private int _analysisRunVersion;
        private int _hoveredChartIndex = -1;
        private Rectangle _lastChartPlotBounds = Rectangle.Empty;
        private bool _syncingDepthInput;
        private bool _populatingMoveGrid;
        private static int _freeAnalysisRunsUsed;
        private static int _freeCoachReportsUsed;

        public event Func<GameAnalysisRequest, int, IProgress<GameAnalysisProgress>, Task<GameAnalysisWindowData>>? AnalyzeRequested;
        public event Action<GameAnalysisWindowData>? AnalysisCompleted;
        public event Action<GameAnalysisMoveResult>? MoveSelected;

        public bool HasSameRequest(GameAnalysisRequest request)
        {
            if (_request == null)
                return false;

            if (!string.Equals(_request.WhiteName, request.WhiteName, StringComparison.Ordinal) ||
                !string.Equals(_request.BlackName, request.BlackName, StringComparison.Ordinal) ||
                !string.Equals(_request.Result, request.Result, StringComparison.Ordinal) ||
                !string.Equals(_request.TimeControlKey, request.TimeControlKey, StringComparison.Ordinal) ||
                _request.Moves.Count != request.Moves.Count)
            {
                return false;
            }

            for (int i = 0; i < _request.Moves.Count; i++)
            {
                GameAnalysisMoveInput left = _request.Moves[i];
                GameAnalysisMoveInput right = request.Moves[i];
                if (left.PlyIndex != right.PlyIndex ||
                    left.MoveNumber != right.MoveNumber ||
                    left.IsWhiteMove != right.IsWhiteMove ||
                    !string.Equals(left.MoveText, right.MoveText, StringComparison.Ordinal) ||
                    !string.Equals(left.FenBefore, right.FenBefore, StringComparison.Ordinal) ||
                    !string.Equals(left.FenAfter, right.FenAfter, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        public GameAnalysisForm()
        {
            Text = "Chess Kit Game Analysis";
            ShowIcon = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = GetScreenAwareMinimumSize(new Size(1280, 820), new Size(960, 680));
            ApplySavedOrDefaultWindowSize();
            BackColor = Color.FromArgb(22, 22, 24);
            ForeColor = Color.White;

            _sectionFont = new Font("Segoe UI Semibold", 16f, FontStyle.Bold);
            _bodyFont = new Font("Segoe UI", 10f, FontStyle.Regular);
            _monoFont = new Font("Consolas", 10f, FontStyle.Regular);

            _titleLabel = new Label
            {
                AutoSize = false,
                Font = _sectionFont,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Game Analysis"
            };

            _subtitleLabel = new Label
            {
                AutoSize = false,
                Font = _bodyFont,
                ForeColor = Color.FromArgb(190, 190, 196),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _statusLabel = new Label
            {
                AutoSize = false,
                Font = _bodyFont,
                ForeColor = Color.FromArgb(160, 214, 255),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Ready."
            };

            _controlsPanel = new SurfacePanel();

            _copyPgnButton = CreateTopButton("Copy Annotated");
            if (_copyPgnButton is ModernActionButton copyButton)
                copyButton.SetPalette(Color.FromArgb(58, 60, 66), Color.FromArgb(74, 76, 84), Color.FromArgb(88, 90, 98), Color.FromArgb(90, 92, 98));
            _copyPgnButton.Click += (_, _) => CopyAnnotatedPgn();

            _coachButton = CreateTopButton("Coach");
            if (_coachButton is ModernActionButton coachButton)
                coachButton.SetPalette(Color.FromArgb(74, 84, 120), Color.FromArgb(92, 104, 146), Color.FromArgb(58, 68, 104), Color.FromArgb(100, 112, 154));
            _coachButton.Enabled = false;
            _coachButton.Visible = BuildLimits.AllowGameAnalysisCoach;
            _coachButton.Click += (_, _) => ShowCoachReport();

            _analysisToggleButton = CreateTopButton("Start");
            if (_analysisToggleButton is ModernActionButton analysisButton)
                analysisButton.SetPalette(Color.FromArgb(58, 122, 228), Color.FromArgb(78, 140, 244), Color.FromArgb(48, 106, 204), Color.FromArgb(74, 132, 230));
            _analysisToggleButton.Click += (_, _) =>
            {
                if (_analysisRunning)
                    StopCurrentAnalysis();
                else
                    StartCurrentAnalysis();
            };

            _depthCaptionLabel = new Label
            {
                AutoSize = false,
                Font = _bodyFont,
                ForeColor = Color.FromArgb(190, 190, 196),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Depth"
            };

            _depthValueLabel = new TextBox
            {
                AutoSize = false,
                Font = _bodyFont,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(34, 35, 39),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 2,
                Text = "18"
            };
            _depthValueLabel.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
                    e.Handled = true;
            };
            _depthValueLabel.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    CommitDepthText();
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    SyncDepthControls(_requestedDepth, updateSlider: true, updateText: true);
                    e.SuppressKeyPress = true;
                }
            };
            _depthValueLabel.Leave += (_, _) => CommitDepthText();

            _depthSlider = new DepthScaleControl
            {
                Minimum = 0,
                Maximum = BuildLimits.MaxDepth,
                Height = 62,
                BackColor = Color.FromArgb(27, 28, 32)
            };
            _depthSlider.ValueChanged += (_, _) =>
            {
                if (_syncingDepthInput)
                    return;

                SyncDepthControls(_depthSlider.Value, updateSlider: false, updateText: true);
            };

            string depthTipText = "Each move is searched to this depth. Higher depth is more accurate but much slower — past depth ~20 a full-game review can take a long time.";
            _depthTip.SetToolTip(_depthSlider, depthTipText);
            _depthTip.SetToolTip(_depthValueLabel, depthTipText);

            _chartPanel = new BufferedPanel
            {
                BackColor = Color.FromArgb(18, 18, 19)
            };
            _chartPanel.Paint += (_, e) => DrawChart(e.Graphics, _chartPanel.ClientRectangle);
            _chartPanel.MouseMove += (_, e) => UpdateChartHover(e.Location);
            _chartPanel.MouseLeave += (_, _) =>
            {
                if (_hoveredChartIndex < 0)
                    return;

                _hoveredChartIndex = -1;
                _chartPanel.Invalidate();
            };

            _progressPanel = new Panel
            {
                BackColor = Color.FromArgb(30, 30, 33)
            };
            _progressPanel.Paint += (_, e) => DrawProgressBar(e.Graphics, _progressPanel.ClientRectangle);

            _whiteSummaryCard = new SummaryCard(
                Color.FromArgb(86, 214, 204),
                Color.FromArgb(172, 190, 214),
                Color.FromArgb(76, 132, 255),
                Color.FromArgb(255, 197, 64),
                Color.FromArgb(255, 92, 92));
            _blackSummaryCard = new SummaryCard(
                Color.FromArgb(86, 214, 204),
                Color.FromArgb(172, 190, 214),
                Color.FromArgb(76, 132, 255),
                Color.FromArgb(255, 197, 64),
                Color.FromArgb(255, 92, 92));

            _movesGrid = new SmoothDataGridView
            {
                BackgroundColor = Color.FromArgb(22, 22, 24),
                BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(52, 52, 56),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 38,
                ColumnHeadersDefaultCellStyle =
                {
                    BackColor = Color.FromArgb(36, 36, 39),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                    SelectionBackColor = Color.FromArgb(36, 36, 39),
                    SelectionForeColor = Color.White
                },
                DefaultCellStyle =
                {
                    BackColor = Color.FromArgb(26, 26, 28),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(36, 92, 156),
                    SelectionForeColor = Color.White,
                    Font = _bodyFont
                },
                AlternatingRowsDefaultCellStyle =
                {
                    BackColor = Color.FromArgb(30, 30, 33),
                    ForeColor = Color.White,
                    SelectionBackColor = Color.FromArgb(36, 92, 156),
                    SelectionForeColor = Color.White,
                    Font = _bodyFont
                },
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToOrderColumns = false,
                MultiSelect = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ScrollBars = ScrollBars.Vertical
            };
            _movesGrid.RowTemplate.Height = 34;
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "#", Width = 52, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Side", Width = 56, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Move", Width = 150, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Eval", Width = 92, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Best", Width = 150, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Loss", Width = 78, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Class", Width = 150, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Depth", Width = 82, SortMode = DataGridViewColumnSortMode.Automatic });
            _movesGrid.SortCompare += MovesGrid_SortCompare;
            _movesGrid.SelectionChanged += (_, _) => NotifySelectedMoveFromGrid();

            Controls.AddRange(new Control[]
            {
                _titleLabel,
                _subtitleLabel,
                _statusLabel,
                _chartPanel,
                _progressPanel,
                _whiteSummaryCard.Container,
                _blackSummaryCard.Container,
                _movesGrid
            });

            _controlsPanel.Controls.AddRange(new Control[]
            {
                _analysisToggleButton,
                _depthCaptionLabel,
                _depthSlider,
                _depthValueLabel,
                _copyPgnButton,
                _coachButton
            });
            Controls.Add(_controlsPanel);

            Resize += (_, _) => LayoutControls();
            LayoutControls();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveWindowSize();
            base.OnFormClosing(e);
        }

        private void ApplySavedOrDefaultWindowSize()
        {
            AppSettings settings = _settingsManager.Load();
            if (settings.GameAnalysisDefaultSizeVersion < 2 &&
                settings.GameAnalysisWindowWidth <= 1340 &&
                settings.GameAnalysisWindowHeight <= 900)
            {
                settings.GameAnalysisWindowWidth = 0;
                settings.GameAnalysisWindowHeight = 0;
                settings.GameAnalysisDefaultSizeVersion = 2;
                _settingsManager.Save(settings);
            }

            Size preferred = settings.GameAnalysisWindowWidth > 0 && settings.GameAnalysisWindowHeight > 0
                ? new Size(settings.GameAnalysisWindowWidth, settings.GameAnalysisWindowHeight)
                : GetFirstRunWindowSize(MinimumSize);

            Size = ClampWindowSizeToCurrentScreen(preferred, MinimumSize);
        }

        private void SaveWindowSize()
        {
            if (WindowState == FormWindowState.Minimized)
                return;

            Size size = WindowState == FormWindowState.Normal
                ? Size
                : RestoreBounds.Size;

            if (size.Width <= 0 || size.Height <= 0)
                return;

            AppSettings settings = _settingsManager.Load();
            settings.GameAnalysisWindowWidth = size.Width;
            settings.GameAnalysisWindowHeight = size.Height;
            _settingsManager.Save(settings);
        }

        private static Size GetFirstRunWindowSize(Size minimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int width = Math.Max(minimum.Width, (int)Math.Round(workArea.Width * 0.80));
            int height = Math.Max(minimum.Height, (int)Math.Round(workArea.Height * 0.80));
            return new Size(width, height);
        }

        private static Size ClampWindowSizeToCurrentScreen(Size preferred, Size minimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int maxWidth = Math.Max(1, (int)Math.Round(workArea.Width * 0.96));
            int maxHeight = Math.Max(1, (int)Math.Round(workArea.Height * 0.96));
            minimum = GetScreenAwareMinimumSize(minimum, new Size(960, 680));
            maxWidth = Math.Max(maxWidth, minimum.Width);
            maxHeight = Math.Max(maxHeight, minimum.Height);
            int width = Math.Clamp(preferred.Width, minimum.Width, maxWidth);
            int height = Math.Clamp(preferred.Height, minimum.Height, maxHeight);
            return new Size(width, height);
        }

        private static Size GetScreenAwareMinimumSize(Size preferredMinimum, Size absoluteMinimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int screenMaxWidth = Math.Max(absoluteMinimum.Width, (int)Math.Round(workArea.Width * 0.96));
            int screenMaxHeight = Math.Max(absoluteMinimum.Height, (int)Math.Round(workArea.Height * 0.96));
            return new Size(
                Math.Min(preferredMinimum.Width, screenMaxWidth),
                Math.Min(preferredMinimum.Height, screenMaxHeight));
        }

        public void LoadAnalysis(GameAnalysisRequest request, string engineName, int depth, int engineThreads, int engineHashMb)
        {
            _request = request;
            _engineName = string.IsNullOrWhiteSpace(engineName) ? "Engine" : engineName;
            _depth = BuildLimits.ClampDepth(depth);
            _engineThreads = BuildLimits.ClampThreads(engineThreads);
            _engineHashMb = BuildLimits.ClampHashMb(engineHashMb);
            _requestedDepth = BuildLimits.ClampDepth(depth);
            _annotatedPgn = string.Empty;
            _results.Clear();
            _openingBoundaryIndex = -1;
            _endgameBoundaryIndex = -1;
            _progressPercent = 0;
            _movesGrid.Rows.Clear();
            _copyPgnButton.Enabled = false;
            _coachButton.Enabled = false;
            _analysisRunning = false;
            _titleLabel.Text = "Game Analysis";
            _subtitleLabel.Text = BuildSubtitle(request, includeDepth: false);
            _statusLabel.Text = "Ready to analyze.";
            _whiteSummaryCard.Apply(new GameAnalysisSummary { SideName = request.WhiteName }, true);
            _blackSummaryCard.Apply(new GameAnalysisSummary { SideName = request.BlackName }, true);
            SyncDepthControls(_requestedDepth, updateSlider: true, updateText: true);
            SetAnalysisToggleState(false);
            _progressPanel.Invalidate();
            _chartPanel.Invalidate();
        }

        private void StartCurrentAnalysis()
        {
            if (_request == null || AnalyzeRequested == null || _analysisRunning)
                return;

            if (BuildLimits.IsFreeEdition && _freeAnalysisRunsUsed >= BuildLimits.GameAnalysisRunLimitPerLaunch)
            {
                _statusLabel.Text = "Free Edition limit reached: restart Chess Kit to run one more game analysis, or upgrade for unlimited reviews.";
                return;
            }

            if (BuildLimits.IsFreeEdition)
                _freeAnalysisRunsUsed++;

            CommitDepthText();
            _analysisRunning = true;
            _analysisRunVersion++;
            _annotatedPgn = string.Empty;
            _copyPgnButton.Enabled = false;
            _coachButton.Enabled = false;
            SetAnalysisToggleState(true);
            _statusLabel.Text = $"Analyzing game at depth {_requestedDepth.ToString(CultureInfo.InvariantCulture)}...";
            _subtitleLabel.Text = BuildSubtitle(_request, includeDepth: true);
            _ = RunAnalysisAsync(_analysisRunVersion, _requestedDepth);
        }

        private string BuildSubtitle(GameAnalysisRequest request, bool includeDepth)
        {
            string resourceText = $"T{_engineThreads.ToString(CultureInfo.InvariantCulture)} H{_engineHashMb.ToString(CultureInfo.InvariantCulture)}";
            string depthText = includeDepth
                ? $"  |  Depth {_requestedDepth.ToString(CultureInfo.InvariantCulture)}"
                : string.Empty;
            return $"{request.WhiteName} vs {request.BlackName}  |  {_engineName}{depthText}  |  {resourceText}";
        }

        private void StopCurrentAnalysis()
        {
            if (!_analysisRunning)
                return;

            _analysisRunVersion++;
            _analysisRunning = false;
            SetAnalysisToggleState(false);
            _statusLabel.Text = "Analysis stopped.";
            _progressPanel.Invalidate();
        }

        private async Task RunAnalysisAsync(int runVersion, int depth)
        {
            try
            {
                if (_request == null || AnalyzeRequested == null)
                    return;

                var progress = new Progress<GameAnalysisProgress>(p => ApplyProgressUpdate(runVersion, p));
                GameAnalysisWindowData data = await AnalyzeRequested(_request, depth, progress);
                if (IsDisposed)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (runVersion != _analysisRunVersion)
                        return;

                    _analysisRunning = false;
                    _results = data.MoveResults;
                    _annotatedPgn = data.AnnotatedPgn;
                    _openingBoundaryIndex = data.OpeningBoundaryIndex;
                    _endgameBoundaryIndex = data.EndgameBoundaryIndex;
                    _progressPercent = 100;
                    _copyPgnButton.Enabled = BuildLimits.AllowAnnotatedPgnExport && !string.IsNullOrWhiteSpace(_annotatedPgn);
                    _coachButton.Enabled = BuildLimits.AllowGameAnalysisCoach && _results.Count > 0;
                    _statusLabel.Text = data.StatusText;
                    SetAnalysisToggleState(false);
                    _whiteSummaryCard.Apply(data.WhiteSummary);
                    _blackSummaryCard.Apply(data.BlackSummary);
                    PopulateMoveGrid();
                    _progressPanel.Invalidate();
                    _chartPanel.Invalidate();
                    AnalysisCompleted?.Invoke(data);
                }));
            }
            catch (Exception ex)
            {
                if (IsDisposed)
                    return;

                BeginInvoke(new Action(() =>
                {
                    if (runVersion != _analysisRunVersion)
                        return;

                    _analysisRunning = false;
                    _progressPercent = 0;
                    SetAnalysisToggleState(false);
                    _statusLabel.Text = $"Analysis failed: {ex.Message}";
                    _progressPanel.Invalidate();
                }));
            }
        }

        private void ApplyProgressUpdate(int runVersion, GameAnalysisProgress progress)
        {
            if (IsDisposed || runVersion != _analysisRunVersion)
                return;

            _results = progress.MoveResults;
            _openingBoundaryIndex = progress.OpeningBoundaryIndex;
            _endgameBoundaryIndex = progress.EndgameBoundaryIndex;
            _progressPercent = progress.ProgressPercent;
            _statusLabel.Text = progress.StatusText;
            _whiteSummaryCard.Apply(progress.WhiteSummary, waiting: progress.ProgressPercent <= 0 && progress.MoveResults.Count == 0);
            _blackSummaryCard.Apply(progress.BlackSummary, waiting: progress.ProgressPercent <= 0 && progress.MoveResults.Count == 0);
            PopulateMoveGrid();
            _progressPanel.Invalidate();
            _chartPanel.Invalidate();
        }

        private void PopulateMoveGrid()
        {
            _populatingMoveGrid = true;
            try
            {
                _movesGrid.Rows.Clear();
                foreach (GameAnalysisMoveResult move in _results)
                {
                    int rowIndex = _movesGrid.Rows.Add(
                        move.IsWhiteMove ? move.MoveNumber.ToString(CultureInfo.InvariantCulture) + "." : string.Empty,
                        move.IsWhiteMove ? "W" : "B",
                        move.MoveText,
                        FormatEval(move),
                        move.BestMove,
                        move.Loss <= 0 ? "0" : move.Loss.ToString("0", CultureInfo.InvariantCulture),
                        move.Classification,
                        move.Depth.ToString(CultureInfo.InvariantCulture));

                    DataGridViewRow row = _movesGrid.Rows[rowIndex];
                    row.Tag = move;
                    row.Cells[1].Style.ForeColor = move.IsWhiteMove
                        ? Color.FromArgb(230, 230, 235)
                        : Color.FromArgb(160, 204, 255);
                    row.Cells[1].Style.Font = new Font(_bodyFont, FontStyle.Bold);
                    row.Cells[1].Style.BackColor = move.IsWhiteMove
                        ? Color.FromArgb(32, 44, 60)
                        : Color.FromArgb(31, 36, 50);
                    row.Cells[1].Style.SelectionBackColor = move.IsWhiteMove
                        ? Color.FromArgb(52, 104, 168)
                        : Color.FromArgb(48, 95, 156);

                    row.Cells[3].Style.ForeColor = move.EvalBefore >= 0
                        ? Color.FromArgb(126, 214, 255)
                        : Color.FromArgb(255, 186, 102);

                    ApplyClassificationStyle(row.Cells[6], move.Classification);
                }
            }
            finally
            {
                _populatingMoveGrid = false;
            }
        }

        private void NotifySelectedMoveFromGrid()
        {
            if (_populatingMoveGrid || _analysisRunning)
                return;

            if (_movesGrid.CurrentRow?.Tag is GameAnalysisMoveResult move)
                MoveSelected?.Invoke(move);
        }

        private void MovesGrid_SortCompare(object? sender, DataGridViewSortCompareEventArgs e)
        {
            if (e.RowIndex1 < 0 || e.RowIndex2 < 0)
                return;

            GameAnalysisMoveResult? left = _movesGrid.Rows[e.RowIndex1].Tag as GameAnalysisMoveResult;
            GameAnalysisMoveResult? right = _movesGrid.Rows[e.RowIndex2].Tag as GameAnalysisMoveResult;
            if (left == null || right == null)
                return;

            e.SortResult = e.Column.Name switch
            {
                "#" => left.MoveNumber.CompareTo(right.MoveNumber),
                "Side" => string.Compare(left.IsWhiteMove ? "W" : "B", right.IsWhiteMove ? "W" : "B", StringComparison.Ordinal),
                "Move" => string.Compare(left.MoveText, right.MoveText, StringComparison.OrdinalIgnoreCase),
                "Eval" => left.EvalBefore.CompareTo(right.EvalBefore),
                "Best" => string.Compare(left.BestMove, right.BestMove, StringComparison.OrdinalIgnoreCase),
                "Loss" => left.Loss.CompareTo(right.Loss),
                "Class" => CompareClassification(left.Classification, right.Classification),
                "Depth" => left.Depth.CompareTo(right.Depth),
                _ => 0
            };
            e.Handled = true;
        }

        private static int CompareClassification(string left, string right)
        {
            static int Rank(string value) => value switch
            {
                "Book" => 0,
                "Best" => 1,
                "Good" => 2,
                "Ok" => 3,
                "Miss" => 4,
                "Inaccuracy" => 5,
                "Mistake" => 6,
                "Blunder" => 7,
                _ => 6
            };

            return Rank(left).CompareTo(Rank(right));
        }

        private static void ApplyClassificationStyle(DataGridViewCell cell, string classification)
        {
            Color fore;
            Color back;

            switch (classification)
            {
                case "Book":
                    fore = Color.FromArgb(148, 190, 255);
                    back = Color.FromArgb(28, 42, 64);
                    break;
                case "Best":
                    fore = Color.FromArgb(117, 224, 145);
                    back = Color.FromArgb(32, 56, 40);
                    break;
                case "Good":
                    fore = Color.FromArgb(86, 214, 204);
                    back = Color.FromArgb(28, 58, 60);
                    break;
                case "Ok":
                    fore = Color.FromArgb(172, 190, 214);
                    back = Color.FromArgb(39, 47, 60);
                    break;
                case "Miss":
                    fore = Color.FromArgb(199, 155, 255);
                    back = Color.FromArgb(52, 36, 72);
                    break;
                case "Inaccuracy":
                    fore = Color.FromArgb(102, 182, 255);
                    back = Color.FromArgb(30, 49, 68);
                    break;
                case "Mistake":
                    fore = Color.FromArgb(255, 197, 80);
                    back = Color.FromArgb(63, 50, 24);
                    break;
                case "Blunder":
                    fore = Color.FromArgb(255, 116, 116);
                    back = Color.FromArgb(68, 28, 30);
                    break;
                default:
                    fore = Color.White;
                    back = Color.FromArgb(26, 26, 28);
                    break;
            }

            cell.Style.ForeColor = fore;
            cell.Style.BackColor = back;
            cell.Style.SelectionForeColor = Color.White;
            cell.Style.SelectionBackColor = Color.FromArgb(58, 84, 116);
            cell.Style.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        }

        private void CopyAnnotatedPgn()
        {
            if (BuildLimits.IsFreeEdition)
            {
                _statusLabel.Text = "Annotated PGN export is available in the full version.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_annotatedPgn))
                return;

            try
            {
                Clipboard.SetText(_annotatedPgn);
                _statusLabel.Text = "Annotated PGN copied.";
            }
            catch
            {
                _statusLabel.Text = "Could not copy annotated PGN.";
            }
        }

        private void ShowCoachReport()
        {
            if (BuildLimits.IsFreeEdition && _freeCoachReportsUsed >= BuildLimits.GameAnalysisCoachLimitPerLaunch)
            {
                _statusLabel.Text = "Free Edition limit reached: the AI coach can be opened once per Chess Kit run.";
                return;
            }

            if (_request == null || _results.Count == 0)
                return;

            if (BuildLimits.IsFreeEdition)
                _freeCoachReportsUsed++;

            var report = GameCoachReportBuilder.Build(_request, _results, _engineName, _requestedDepth);
            var form = new GameCoachReportForm(report, plyIndex =>
            {
                GameAnalysisMoveResult? move = _results.FirstOrDefault(m => m.PlyIndex == plyIndex);
                if (move != null)
                    MoveSelected?.Invoke(move);
            });
            form.Show(this);
        }

        private void DrawChart(Graphics g, Rectangle rect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_chartPanel.BackColor);

            using var borderPen = new Pen(Color.FromArgb(68, 68, 74));
            using var midPen = new Pen(Color.FromArgb(82, 82, 88));
            using var gridPen = new Pen(Color.FromArgb(42, 42, 47));
            using var evalPen = new Pen(Color.FromArgb(255, 120, 0), 2f);
            using var whiteBrush = new SolidBrush(Color.FromArgb(232, 232, 232));
            using var textBrush = new SolidBrush(Color.FromArgb(210, 210, 215));
            using var subtleBrush = new SolidBrush(Color.FromArgb(128, 128, 136));
            using var axisBrush = new SolidBrush(Color.FromArgb(154, 156, 164));

            Rectangle inner = new Rectangle(rect.Left, rect.Top, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1));
            g.DrawRectangle(borderPen, inner);

            int plotLeftPad = Dpi.Scale(_chartPanel, 58);
            int plotTopPad = Dpi.Scale(_chartPanel, 16);
            int plotWidthInset = Dpi.Scale(_chartPanel, 70);
            int plotHeightInset = Dpi.Scale(_chartPanel, 34);
            Rectangle plot = new Rectangle(inner.Left + plotLeftPad, inner.Top + plotTopPad, Math.Max(10, inner.Width - plotWidthInset), Math.Max(10, inner.Height - plotHeightInset));
            _lastChartPlotBounds = plot;
            int midY = plot.Top + plot.Height / 2;

            if (_results.Count == 0)
            {
                g.DrawString(_analysisRunning ? "Running analysis..." : "No game analysis yet.", _bodyFont, subtleBrush, inner.Left + Dpi.Scale(_chartPanel, 12), inner.Top + Dpi.Scale(_chartPanel, 12));
                return;
            }

            List<double> whitePerspective = _results.Select(ToWhitePerspectiveEval).ToList();
            double maxAbs = GetChartMaxAbs(whitePerspective);

            DrawChartAxis(g, plot, maxAbs, axisBrush, gridPen, midPen);

            int phaseLabelGap = Dpi.Scale(_chartPanel, 8);
            int phaseLabelTop = inner.Top + Dpi.Scale(_chartPanel, 8);
            int openingEnd = Math.Clamp(_openingBoundaryIndex, 1, Math.Max(1, _results.Count - 1));
            int midgameX = plot.Left + (int)Math.Round(plot.Width * (openingEnd / (double)_results.Count));
            g.DrawLine(midPen, midgameX, plot.Top, midgameX, plot.Bottom);
            g.DrawString("Opening", _bodyFont, subtleBrush, plot.Left + phaseLabelGap, phaseLabelTop);
            g.DrawString("Middlegame", _bodyFont, subtleBrush, midgameX + phaseLabelGap, phaseLabelTop);
            if (_endgameBoundaryIndex > openingEnd && _endgameBoundaryIndex < _results.Count)
            {
                int endgameX = plot.Left + (int)Math.Round(plot.Width * (_endgameBoundaryIndex / (double)_results.Count));
                g.DrawLine(midPen, endgameX, plot.Top, endgameX, plot.Bottom);
                g.DrawString("Endgame", _bodyFont, subtleBrush, endgameX + phaseLabelGap, phaseLabelTop);
            }

            var points = new List<PointF>();
            for (int i = 0; i < whitePerspective.Count; i++)
            {
                double clamped = Math.Max(-maxAbs, Math.Min(maxAbs, whitePerspective[i]));
                float x = plot.Left + (whitePerspective.Count == 1 ? plot.Width / 2f : (float)(i * plot.Width / (double)(whitePerspective.Count - 1)));
                float y = (float)(midY - (clamped / maxAbs) * (plot.Height * 0.46));
                points.Add(new PointF(x, y));
            }

            if (points.Count > 1)
                g.DrawLines(evalPen, points.ToArray());
            else if (points.Count == 1)
            {
                float dotR = Dpi.Scale(_chartPanel, 3f);
                g.FillEllipse(new SolidBrush(evalPen.Color), points[0].X - dotR, points[0].Y - dotR, dotR * 2, dotR * 2);
            }

            DrawChartHover(g, inner, plot, points, whitePerspective, whiteBrush, textBrush);
        }

        private void DrawChartAxis(Graphics g, Rectangle plot, double maxAbs, Brush axisBrush, Pen gridPen, Pen midPen)
        {
            using var axisFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            double[] values = { maxAbs, maxAbs / 2.0, 0, -maxAbs / 2.0, -maxAbs };

            foreach (double value in values)
            {
                float y = EvalToChartY(value, plot, maxAbs);
                Pen pen = Math.Abs(value) < 0.001 ? midPen : gridPen;
                g.DrawLine(pen, plot.Left, y, plot.Right, y);

                string label = FormatAxisEval(value);
                RectangleF labelRect = new RectangleF(plot.Left - Dpi.Scale(_chartPanel, 54), y - Dpi.Scale(_chartPanel, 10), Dpi.Scale(_chartPanel, 46), Dpi.Scale(_chartPanel, 20));
                g.DrawString(label, _bodyFont, axisBrush, labelRect, axisFormat);
            }
        }

        private void DrawChartHover(Graphics g, Rectangle inner, Rectangle plot, List<PointF> points, List<double> whitePerspective, Brush whiteBrush, Brush textBrush)
        {
            if (_hoveredChartIndex < 0 || _hoveredChartIndex >= points.Count || _hoveredChartIndex >= _results.Count)
                return;

            PointF point = points[_hoveredChartIndex];
            GameAnalysisMoveResult move = _results[_hoveredChartIndex];
            double eval = whitePerspective[_hoveredChartIndex];

            using var crossPen = new Pen(Color.FromArgb(165, 180, 190, 205));
            crossPen.DashStyle = DashStyle.Dash;
            using var markerFill = new SolidBrush(Color.FromArgb(255, 214, 122));
            using var markerBorder = new Pen(Color.FromArgb(18, 18, 19), 2f);

            float markerR = Dpi.Scale(_chartPanel, 5f);
            g.DrawLine(crossPen, point.X, plot.Top, point.X, plot.Bottom);
            g.DrawLine(crossPen, plot.Left, point.Y, plot.Right, point.Y);
            g.FillEllipse(markerFill, point.X - markerR, point.Y - markerR, markerR * 2, markerR * 2);
            g.DrawEllipse(markerBorder, point.X - markerR, point.Y - markerR, markerR * 2, markerR * 2);

            string moveNo = move.IsWhiteMove
                ? $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}."
                : $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}...";
            string line1 = $"{moveNo} {move.MoveText}  {FormatChartEval(move, eval)}";
            string line2 = $"{move.Classification}  Loss {Math.Max(0, move.Loss).ToString("0", CultureInfo.InvariantCulture)}";
            string line3 = string.IsNullOrWhiteSpace(move.BestMove) ? "Best -" : $"Best {move.BestMove}";

            SizeF size1 = g.MeasureString(line1, _bodyFont);
            SizeF size2 = g.MeasureString(line2, _bodyFont);
            SizeF size3 = g.MeasureString(line3, _bodyFont);
            int bubblePad = Dpi.Scale(_chartPanel, 22);
            int bubbleEdge = Dpi.Scale(_chartPanel, 14);
            int bubbleClamp = Dpi.Scale(_chartPanel, 8);
            int textInset = Dpi.Scale(_chartPanel, 12);
            int accentWidth = Dpi.Scale(_chartPanel, 4);
            int bubbleWidth = (int)Math.Ceiling(Math.Max(size1.Width, Math.Max(size2.Width, size3.Width))) + bubblePad;
            int bubbleHeight = Dpi.Scale(_chartPanel, 72);
            int bubbleX = (int)Math.Round(point.X + bubbleEdge);
            int bubbleY = (int)Math.Round(point.Y - bubbleHeight - bubbleEdge);

            if (bubbleX + bubbleWidth > inner.Right - bubbleClamp)
                bubbleX = (int)Math.Round(point.X - bubbleWidth - bubbleEdge);
            bubbleX = Math.Max(inner.Left + bubbleClamp, Math.Min(inner.Right - bubbleWidth - bubbleClamp, bubbleX));
            bubbleY = Math.Max(inner.Top + bubbleClamp, Math.Min(inner.Bottom - bubbleHeight - bubbleClamp, bubbleY));

            Rectangle bubble = new Rectangle(bubbleX, bubbleY, bubbleWidth, bubbleHeight);
            using GraphicsPath path = RoundedRect(bubble, Dpi.Scale(_chartPanel, 8));
            using var fill = new SolidBrush(Color.FromArgb(238, 26, 27, 31));
            using var border = new Pen(Color.FromArgb(96, 100, 110));
            using var accent = new SolidBrush(GetClassificationColor(move.Classification));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.FillRectangle(accent, bubble.Left, bubble.Top, accentWidth, bubble.Height);

            g.DrawString(line1, _bodyFont, whiteBrush, bubble.Left + textInset, bubble.Top + Dpi.Scale(_chartPanel, 8));
            g.DrawString(line2, _bodyFont, accent, bubble.Left + textInset, bubble.Top + Dpi.Scale(_chartPanel, 30));
            g.DrawString(line3, _bodyFont, textBrush, bubble.Left + textInset, bubble.Top + Dpi.Scale(_chartPanel, 50));
        }

        private void UpdateChartHover(Point location)
        {
            if (_results.Count == 0 || _lastChartPlotBounds.Width <= 0)
            {
                if (_hoveredChartIndex >= 0)
                {
                    _hoveredChartIndex = -1;
                    _chartPanel.Invalidate();
                }
                return;
            }

            Rectangle hoverBounds = Rectangle.Inflate(_lastChartPlotBounds, Dpi.Scale(_chartPanel, 6), Dpi.Scale(_chartPanel, 18));
            if (!hoverBounds.Contains(location))
            {
                if (_hoveredChartIndex >= 0)
                {
                    _hoveredChartIndex = -1;
                    _chartPanel.Invalidate();
                }
                return;
            }

            int next = _results.Count == 1
                ? 0
                : (int)Math.Round((location.X - _lastChartPlotBounds.Left) / (double)Math.Max(1, _lastChartPlotBounds.Width) * (_results.Count - 1));
            next = Math.Clamp(next, 0, _results.Count - 1);
            if (next == _hoveredChartIndex)
                return;

            _hoveredChartIndex = next;
            _chartPanel.Invalidate();
        }

        private static double ToWhitePerspectiveEval(GameAnalysisMoveResult result)
        {
            return result.IsWhiteMove ? result.EvalBefore : -result.EvalBefore;
        }

        private static double GetChartMaxAbs(List<double> values)
        {
            if (values.Count == 0)
                return 1.0;

            double maxAbs = Math.Max(8.0, values.Max(v => Math.Abs(v)));
            maxAbs = Math.Min(maxAbs, 50.0);

            if (maxAbs <= 8.0)
                return 8.0;
            if (maxAbs <= 12.0)
                return 12.0;
            if (maxAbs <= 16.0)
                return 16.0;
            if (maxAbs <= 20.0)
                return 20.0;
            if (maxAbs <= 30.0)
                return 30.0;
            return 50.0;
        }

        private static float EvalToChartY(double eval, Rectangle plot, double maxAbs)
        {
            int midY = plot.Top + plot.Height / 2;
            double clamped = Math.Max(-maxAbs, Math.Min(maxAbs, eval));
            return (float)(midY - (clamped / maxAbs) * (plot.Height * 0.46));
        }

        private static string FormatAxisEval(double value)
        {
            if (Math.Abs(value) < 0.001)
                return "0";
            return value.ToString("+0.#;-0.#", CultureInfo.InvariantCulture);
        }

        private static string FormatChartEval(GameAnalysisMoveResult move, double whitePerspectiveEval)
        {
            if (move.IsMateScore && move.MateIn.HasValue)
                return $"M{Math.Abs(move.MateIn.Value).ToString(CultureInfo.InvariantCulture)}";
            return whitePerspectiveEval.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        private static Color GetClassificationColor(string classification)
        {
            return classification switch
            {
                "Book" => Color.FromArgb(148, 190, 255),
                "Best" => Color.FromArgb(117, 224, 145),
                "Good" => Color.FromArgb(86, 214, 204),
                "Ok" => Color.FromArgb(172, 190, 214),
                "Miss" => Color.FromArgb(199, 155, 255),
                "Inaccuracy" => Color.FromArgb(102, 182, 255),
                "Mistake" => Color.FromArgb(255, 197, 80),
                "Blunder" => Color.FromArgb(255, 116, 116),
                _ => Color.FromArgb(210, 210, 215)
            };
        }

        private void DrawProgressBar(Graphics g, Rectangle rect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_progressPanel.BackColor);
            Rectangle inner = Rectangle.Inflate(rect, -1, -1);
            using var borderPen = new Pen(Color.FromArgb(66, 66, 70));
            using var fillBrush = new SolidBrush(Color.FromArgb(46, 132, 255));
            using var backgroundBrush = new SolidBrush(Color.FromArgb(40, 40, 44));
            using var textBrush = new SolidBrush(Color.White);
            g.FillRectangle(backgroundBrush, inner);
            int fillWidth = (int)Math.Round(inner.Width * (_progressPercent / 100.0));
            if (fillWidth > 0)
            {
                Rectangle fillRect = new Rectangle(inner.Left, inner.Top, Math.Min(inner.Width, fillWidth), inner.Height);
                g.FillRectangle(fillBrush, fillRect);
            }
            g.DrawRectangle(borderPen, inner);
            string text = _analysisRunning ? $"Analyzing... {_progressPercent.ToString(CultureInfo.InvariantCulture)}%" : "Analysis completed";
            SizeF size = g.MeasureString(text, _bodyFont);
            g.DrawString(text, _bodyFont, textBrush, inner.Left + (inner.Width - size.Width) / 2f, inner.Top + (inner.Height - size.Height) / 2f);
        }

        private void LayoutControls()
        {
            int margin = 18;
            int top = 16;
            int titleHeight = ControlHeight(_titleLabel.Font, 8, 42);
            int labelHeight = ControlHeight(_bodyFont, 6, 28);
            int buttonHeight = ControlHeight(_analysisToggleButton.Font, 10, 38);
            int inputHeight = ControlHeight(_depthValueLabel.Font, 8, 34);
            int sliderHeight = ControlHeight(_depthSlider.Font, 44, 72);
            int controlsHeight = Math.Max(buttonHeight + 28, sliderHeight + 16);

            int startWidth = ButtonWidth(_analysisToggleButton, 96);
            int depthCaptionWidth = Math.Max(88, TextRenderer.MeasureText("Depth", _depthCaptionLabel.Font).Width + 28);
            int depthValueWidth = Math.Max(52, TextRenderer.MeasureText("30", _depthValueLabel.Font).Width + 28);
            int copyWidth = ButtonWidth(_copyPgnButton, 206);
            int coachWidth = ButtonWidth(_coachButton, 124);
            int gap = 14;
            int preferredSliderWidth = 300;
            int controlsWidth = startWidth + depthCaptionWidth + preferredSliderWidth + depthValueWidth + copyWidth + coachWidth + gap * 5 + 28;
            int maxControlsWidth = Math.Max(520, ClientSize.Width - margin * 2);
            controlsWidth = Math.Min(controlsWidth, maxControlsWidth);
            int availableForSlider = controlsWidth - (startWidth + depthCaptionWidth + depthValueWidth + copyWidth + coachWidth + gap * 5 + 28);
            int sliderWidth = Math.Max(230, availableForSlider);

            _controlsPanel.SetBounds(ClientSize.Width - margin - controlsWidth, top - 2, controlsWidth, controlsHeight);

            int controlY = (_controlsPanel.Height - buttonHeight) / 2;
            int inputY = (_controlsPanel.Height - inputHeight) / 2;
            int sliderY = (_controlsPanel.Height - sliderHeight) / 2;
            int x = 14;
            _analysisToggleButton.SetBounds(x, controlY, startWidth, buttonHeight);
            x = _analysisToggleButton.Right + gap;
            _depthCaptionLabel.SetBounds(x, inputY, depthCaptionWidth, inputHeight);
            x = _depthCaptionLabel.Right + gap;
            _depthSlider.SetBounds(x, sliderY, sliderWidth, sliderHeight);
            x = _depthSlider.Right + gap;
            _depthValueLabel.SetBounds(x, inputY, depthValueWidth, inputHeight);
            x = _depthValueLabel.Right + gap;
            _copyPgnButton.SetBounds(x, controlY, copyWidth, buttonHeight);
            x = _copyPgnButton.Right + gap;
            _coachButton.SetBounds(x, controlY, Math.Max(coachWidth, _controlsPanel.Width - x - 14), buttonHeight);

            int leftLaneRight = _controlsPanel.Left - 18;
            int leftLaneWidth = Math.Max(220, leftLaneRight - margin);
            _titleLabel.SetBounds(margin, top, leftLaneWidth, titleHeight);
            _subtitleLabel.SetBounds(margin, _titleLabel.Bottom + 2, leftLaneWidth, labelHeight);
            _statusLabel.SetBounds(margin, _subtitleLabel.Bottom + 2, leftLaneWidth, labelHeight);
            int headerBottom = Math.Max(_statusLabel.Bottom, _controlsPanel.Bottom);
            _progressPanel.SetBounds(margin, headerBottom + 10, ClientSize.Width - margin * 2, ControlHeight(_bodyFont, 2, 18));

            int chartTop = _progressPanel.Bottom + 12;
            int chartHeight = Dpi.Scale(this, 250);
            _chartPanel.SetBounds(margin, chartTop, ClientSize.Width - margin * 2, chartHeight);

            int summaryTop = _chartPanel.Bottom + 12;
            int summaryGap = 10;
            int summaryWidth = (ClientSize.Width - margin * 2 - summaryGap) / 2;
            int summaryCardHeight = Math.Max(_whiteSummaryCard.PreferredHeight, _blackSummaryCard.PreferredHeight);
            _whiteSummaryCard.Container.SetBounds(margin, summaryTop, summaryWidth, summaryCardHeight);
            _blackSummaryCard.Container.SetBounds(_whiteSummaryCard.Container.Right + summaryGap, summaryTop, summaryWidth, summaryCardHeight);
            _whiteSummaryCard.LayoutCard();
            _blackSummaryCard.LayoutCard();

            int gridTop = _whiteSummaryCard.Container.Bottom + 12;
            _movesGrid.SetBounds(margin, gridTop, ClientSize.Width - margin * 2, ClientSize.Height - gridTop - margin);
            LayoutMoveGridColumns();
        }

        private static int TextHeight(Font? font)
        {
            return TextRenderer.MeasureText("Hg", font ?? SystemFonts.MessageBoxFont).Height;
        }

        private static int ControlHeight(Font? font, int verticalPadding, int minimum)
        {
            return Math.Max(minimum, TextHeight(font) + verticalPadding);
        }

        private static int ButtonWidth(Button button, int minimum)
        {
            int measured = TextRenderer.MeasureText(button.Text, button.Font).Width + 34;
            return Math.Max(minimum, measured);
        }

        private void SetAnalysisToggleState(bool running)
        {
            _analysisToggleButton.Enabled = true;
            _analysisToggleButton.Text = running ? "Stop" : "Start";
            if (_analysisToggleButton is ModernActionButton button)
            {
                if (running)
                    button.SetPalette(Color.FromArgb(166, 116, 38), Color.FromArgb(186, 132, 52), Color.FromArgb(144, 96, 28), Color.FromArgb(188, 136, 60));
                else
                    button.SetPalette(Color.FromArgb(58, 122, 228), Color.FromArgb(78, 140, 244), Color.FromArgb(48, 106, 204), Color.FromArgb(74, 132, 230));
            }
        }

        private void LayoutMoveGridColumns()
        {
            if (_movesGrid.Columns.Count < 8)
                return;

            int availableWidth = _movesGrid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 16;
            if (availableWidth <= 0)
                return;

            const int noWidth = 52;
            const int sideWidth = 74;
            const int evalWidth = 92;
            const int lossWidth = 78;
            const int classWidth = 150;
            const int depthWidth = 96;

            int flexibleWidth = Math.Max(320, availableWidth - noWidth - sideWidth - evalWidth - lossWidth - classWidth - depthWidth);
            int moveWidth = Math.Max(130, (int)Math.Round(flexibleWidth * 0.45));
            int bestWidth = Math.Max(130, flexibleWidth - moveWidth);

            _movesGrid.Columns[0].Width = noWidth;
            _movesGrid.Columns[1].Width = sideWidth;
            _movesGrid.Columns[2].Width = moveWidth;
            _movesGrid.Columns[3].Width = evalWidth;
            _movesGrid.Columns[4].Width = bestWidth;
            _movesGrid.Columns[5].Width = lossWidth;
            _movesGrid.Columns[6].Width = classWidth;
            _movesGrid.Columns[7].Width = depthWidth;
        }

        private void CommitDepthText()
        {
            if (_syncingDepthInput)
                return;

            string text = _depthValueLabel.Text.Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth))
            {
                SyncDepthControls(_requestedDepth, updateSlider: true, updateText: true);
                return;
            }

            SyncDepthControls(depth, updateSlider: true, updateText: true);
        }

        private void SyncDepthControls(int depth, bool updateSlider, bool updateText)
        {
            int next = BuildLimits.ClampDepth(Math.Clamp(depth, _depthSlider.Minimum, _depthSlider.Maximum));
            _syncingDepthInput = true;
            try
            {
                _requestedDepth = next;
                if (updateSlider && _depthSlider.Value != next)
                    _depthSlider.Value = next;
                if (updateText)
                    _depthValueLabel.Text = next.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _syncingDepthInput = false;
            }
        }

        private string FormatEval(GameAnalysisMoveResult move)
        {
            if (move.IsMateScore && move.MateIn.HasValue)
                return $"M{Math.Abs(move.MateIn.Value)}";
            return move.EvalBefore.ToString("+0.00;-0.00", CultureInfo.InvariantCulture);
        }

        private sealed class SummaryCard
        {
            public Panel Container { get; }
            private readonly Label _nameLabel;
            private readonly Label _accuracyLabel;
            private readonly Label _bookChip;
            private readonly Label _bestChip;
            private readonly Label _goodChip;
            private readonly Label _okChip;
        private readonly Label _missChip;
        private readonly Label _inaccuracyChip;
        private readonly Label _mistakeChip;
        private readonly Label _blunderChip;

            public SummaryCard(Color goodColor, Color okColor, Color inaccuracyColor, Color mistakeColor, Color blunderColor)
            {
                Container = new Panel
                {
                    BackColor = Color.FromArgb(28, 28, 31),
                    BorderStyle = BorderStyle.FixedSingle
                };

                _nameLabel = CreateLabel(12f, FontStyle.Bold, Color.White);
                _accuracyLabel = CreateLabel(18f, FontStyle.Bold, Color.White, ContentAlignment.MiddleRight);
                _bookChip = CreateChip(Color.FromArgb(148, 190, 255));
                _bestChip = CreateChip(Color.FromArgb(117, 224, 145));
                _goodChip = CreateChip(goodColor);
                _okChip = CreateChip(okColor);
                _missChip = CreateChip(Color.FromArgb(199, 155, 255));
                _inaccuracyChip = CreateChip(inaccuracyColor);
                _mistakeChip = CreateChip(mistakeColor);
                _blunderChip = CreateChip(blunderColor);

                Container.Controls.AddRange(new Control[]
                {
                    _nameLabel, _accuracyLabel, _bookChip, _bestChip, _goodChip, _okChip, _missChip, _inaccuracyChip, _mistakeChip, _blunderChip
                });
            }

            public int PreferredHeight
            {
                get
                {
                    int pad = 16;
                    int headerHeight = Math.Max(TextHeight(_nameLabel.Font) + 10, TextHeight(_accuracyLabel.Font) + 8);
                    int chipHeight = Math.Max(28, TextHeight(_bookChip.Font) + 8);
                    return pad + headerHeight + 8 + chipHeight + 8 + chipHeight + pad;
                }
            }

            public void Apply(GameAnalysisSummary summary, bool waiting = false)
            {
                _nameLabel.Text = summary.SideName;
                _accuracyLabel.Text = waiting ? "--" : $"{summary.Accuracy:0}%";
                _bookChip.Text = waiting ? "Book -" : $"{summary.BookMoves} Book";
                _bestChip.Text = waiting ? "Best -" : $"{summary.BestMoves} Best";
                _goodChip.Text = waiting ? "Good -" : $"{summary.GoodMoves} Good";
                _okChip.Text = waiting ? "Ok -" : $"{summary.OkMoves} Ok";
                _missChip.Text = waiting ? "Miss -" : $"{summary.Misses} Miss";
                _inaccuracyChip.Text = waiting ? "Inaccuracies -" : $"{summary.Inaccuracies} Inaccuracies";
                _mistakeChip.Text = waiting ? "Mistakes -" : $"{summary.Mistakes} Mistakes";
                _blunderChip.Text = waiting ? "Blunders -" : $"{summary.Blunders} Blunders";
            }

            public void LayoutCard()
            {
                int pad = 16;
                int headerHeight = Math.Max(TextHeight(_nameLabel.Font) + 10, TextHeight(_accuracyLabel.Font) + 8);
                int nameTop = pad - 2;
                int accuracyWidth = Math.Max(118, TextRenderer.MeasureText("100%", _accuracyLabel.Font).Width + 28);
                _nameLabel.SetBounds(pad, nameTop, Math.Max(80, Container.Width - accuracyWidth - pad * 3), headerHeight);
                _accuracyLabel.SetBounds(Container.Width - accuracyWidth - pad, nameTop, accuracyWidth, headerHeight);

                int chipGap = 10;
                int chipWidth = (Container.Width - pad * 2 - chipGap * 3) / 4;
                int bottomChipWidth = (Container.Width - pad * 2 - chipGap * 3) / 4;
                int chipHeight = Math.Max(28, TextHeight(_bookChip.Font) + 8);
                int topRowY = pad + headerHeight + 8;
                int bottomRowY = topRowY + chipHeight + 8;

                _bookChip.SetBounds(pad, topRowY, chipWidth, chipHeight);
                _bestChip.SetBounds(_bookChip.Right + chipGap, topRowY, chipWidth, chipHeight);
                _goodChip.SetBounds(_bestChip.Right + chipGap, topRowY, chipWidth, chipHeight);
                _okChip.SetBounds(_goodChip.Right + chipGap, topRowY, chipWidth, chipHeight);

                _missChip.SetBounds(pad, bottomRowY, bottomChipWidth, chipHeight);
                _inaccuracyChip.SetBounds(_missChip.Right + chipGap, bottomRowY, bottomChipWidth, chipHeight);
                _mistakeChip.SetBounds(_inaccuracyChip.Right + chipGap, bottomRowY, bottomChipWidth, chipHeight);
                _blunderChip.SetBounds(_mistakeChip.Right + chipGap, bottomRowY, bottomChipWidth, chipHeight);
            }

            private static Label CreateLabel(float size, FontStyle style, Color color, ContentAlignment align = ContentAlignment.MiddleLeft)
            {
                return new Label
                {
                    AutoSize = false,
                    Font = new Font("Segoe UI", size, style),
                    ForeColor = color,
                    BackColor = Color.Transparent,
                    TextAlign = align
                };
            }

            private static Label CreateChip(Color color)
            {
                return new Label
                {
                    AutoSize = false,
                    BackColor = Color.FromArgb(44, color),
                    ForeColor = color,
                    Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    BorderStyle = BorderStyle.FixedSingle
                };
            }
        }

        private sealed class SmoothDataGridView : DataGridView
        {
            public SmoothDataGridView()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                UpdateStyles();
            }
        }

        private sealed class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                UpdateStyles();
            }
        }

        private sealed class DepthScaleControl : Control
        {
            private int _minimum;
            private int _maximum = 30;
            private int _value = 18;
            private bool _dragging;

            public event EventHandler? ValueChanged;

            [Browsable(false)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public int Minimum
            {
                get => _minimum;
                set
                {
                    _minimum = value;
                    if (_maximum < _minimum)
                        _maximum = _minimum;
                    Value = Math.Clamp(_value, _minimum, _maximum);
                    Invalidate();
                }
            }

            [Browsable(false)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public int Maximum
            {
                get => _maximum;
                set
                {
                    _maximum = Math.Max(value, _minimum);
                    Value = Math.Clamp(_value, _minimum, _maximum);
                    Invalidate();
                }
            }

            [Browsable(false)]
            [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
            public int Value
            {
                get => _value;
                set
                {
                    int next = Math.Clamp(value, _minimum, _maximum);
                    if (next == _value)
                        return;

                    _value = next;
                    Invalidate();
                    ValueChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public DepthScaleControl()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                Cursor = Cursors.Hand;
            }

            protected override void OnDpiChangedAfterParent(EventArgs e)
            {
                base.OnDpiChangedAfterParent(e);
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                _dragging = true;
                Capture = true;
                UpdateValueFromX(e.X);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (_dragging)
                    UpdateValueFromX(e.X);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _dragging = false;
                Capture = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);

                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(BackColor);

                int labelHeight = TextRenderer.MeasureText("30", Font).Height;
                int trackHeight = Math.Max(Dpi.Scale(this, 7), Math.Min(Dpi.Scale(this, 10), Height / 8));
                int sidePad = Math.Max(Dpi.Scale(this, 12), labelHeight / 2 + Dpi.Scale(this, 4));
                int trackTop = Math.Max(Dpi.Scale(this, 8), (Height - labelHeight - Dpi.Scale(this, 24)) / 2);
                Rectangle trackRect = new Rectangle(sidePad, trackTop, Math.Max(Dpi.Scale(this, 40), Width - sidePad * 2), trackHeight);
                int tickTopBase = trackRect.Bottom + Dpi.Scale(this, 9);
                int labelTop = tickTopBase + Dpi.Scale(this, 9);

                using var trackBrush = new SolidBrush(Color.FromArgb(48, 50, 56));
                using var activeBrush = new SolidBrush(Color.FromArgb(86, 154, 255));
                using var tickPen = new Pen(Color.FromArgb(92, 94, 102));
                using var majorTickPen = new Pen(Color.FromArgb(126, 128, 136));
                using var textBrush = new SolidBrush(Color.FromArgb(176, 178, 186));
                using var thumbBrush = new SolidBrush(Color.FromArgb(235, 240, 246));
                using var thumbBorderPen = new Pen(Color.FromArgb(78, 118, 176));

                int trackRadius = Dpi.Scale(this, 4);
                using (GraphicsPath trackPath = RoundedRect(trackRect, trackRadius))
                {
                    e.Graphics.FillPath(trackBrush, trackPath);
                }

                float ratio = _maximum == _minimum ? 0f : (Value - _minimum) / (float)(_maximum - _minimum);
                int activeWidth = (int)Math.Round(trackRect.Width * ratio);
                if (activeWidth > 0)
                {
                    Rectangle activeRect = new Rectangle(trackRect.Left, trackRect.Top, activeWidth, trackRect.Height);
                    using GraphicsPath activePath = RoundedRect(activeRect, trackRadius);
                    e.Graphics.FillPath(activeBrush, activePath);
                }

                float thumbX = trackRect.Left + trackRect.Width * ratio;
                float thumbWidth = Math.Max(Dpi.Scale(this, 14), trackHeight + Dpi.Scale(this, 6));
                float thumbHeight = Math.Max(Dpi.Scale(this, 16), trackHeight + Dpi.Scale(this, 8));
                RectangleF thumbRect = new RectangleF(thumbX - thumbWidth / 2f, trackRect.Top - (thumbHeight - trackRect.Height) / 2f, thumbWidth, thumbHeight);
                e.Graphics.FillEllipse(thumbBrush, thumbRect);
                e.Graphics.DrawEllipse(thumbBorderPen, thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height);

                for (int depth = _minimum; depth <= _maximum; depth += 5)
                {
                    float tickX = trackRect.Left + trackRect.Width * ((depth - _minimum) / (float)Math.Max(1, _maximum - _minimum));
                    bool isMajor = depth % 10 == 0 || depth == _minimum || depth == _maximum;
                    int tickTop = tickTopBase + (isMajor ? 0 : Dpi.Scale(this, 4));
                    int tickBottom = tickTopBase + (isMajor ? Dpi.Scale(this, 8) : Dpi.Scale(this, 6));
                    e.Graphics.DrawLine(isMajor ? majorTickPen : tickPen, tickX, tickTop, tickX, tickBottom);

                    string label = depth.ToString(CultureInfo.InvariantCulture);
                    SizeF size = e.Graphics.MeasureString(label, Font);
                    float labelX = tickX - size.Width / 2f;
                    labelX = Math.Max(0, Math.Min(Width - size.Width - Dpi.Scale(this, 2), labelX));
                    e.Graphics.DrawString(label, Font, textBrush, labelX, labelTop);
                }
            }

            private void UpdateValueFromX(int x)
            {
                int labelHeight = TextRenderer.MeasureText("30", Font).Height;
                int trackLeft = Math.Max(Dpi.Scale(this, 12), labelHeight / 2 + Dpi.Scale(this, 4));
                int trackWidth = Math.Max(Dpi.Scale(this, 40), Width - trackLeft * 2);
                float ratio = Math.Clamp((x - trackLeft) / (float)trackWidth, 0f, 1f);
                int depth = (int)Math.Round(_minimum + ((_maximum - _minimum) * ratio));
                Value = depth;
            }

            private static GraphicsPath RoundedRect(Rectangle rect, int radius)
            {
                int diameter = radius * 2;
                var path = new GraphicsPath();
                if (rect.Width <= 0 || rect.Height <= 0)
                    return path;

                path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
                path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
                path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }
        }

        private Button CreateTopButton(string text)
        {
            var button = new ModernActionButton
            {
                Text = text,
                ForeColor = Color.White
            };
            return button;
        }

        private sealed class SurfacePanel : Panel
        {
            public SurfacePanel()
            {
                DoubleBuffered = true;
                BackColor = Color.Transparent;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = RoundedRect(rect, Dpi.Scale(this, 10));
                using var fill = new SolidBrush(Color.FromArgb(27, 28, 32));
                using var border = new Pen(Color.FromArgb(60, 62, 68));
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(border, path);
            }
        }

        private sealed class ModernActionButton : Button
        {
            private Color _fill = Color.FromArgb(58, 60, 66);
            private Color _hover = Color.FromArgb(74, 76, 84);
            private Color _pressed = Color.FromArgb(88, 90, 98);
            private Color _border = Color.FromArgb(90, 92, 98);
            private bool _hovered;
            private bool _pressedState;

            public ModernActionButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                BackColor = Color.Transparent;
                TabStop = false;
            }

            public void SetPalette(Color fill, Color hover, Color pressed, Color border)
            {
                _fill = fill;
                _hover = hover;
                _pressed = pressed;
                _border = border;
                Invalidate();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hovered = false;
                _pressedState = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs mevent)
            {
                base.OnMouseDown(mevent);
                _pressedState = true;
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs mevent)
            {
                base.OnMouseUp(mevent);
                _pressedState = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs pevent)
            {
                pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                pevent.Graphics.Clear(Parent?.BackColor ?? BackColor);

                Rectangle rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using var path = RoundedRect(rect, Dpi.Scale(this, 8));
                Color fill = !Enabled ? Color.FromArgb(46, 48, 54) : _pressedState ? _pressed : _hovered ? _hover : _fill;
                using var fillBrush = new SolidBrush(fill);
                using var borderPen = new Pen(_border);
                using var textBrush = new SolidBrush(Enabled ? ForeColor : Color.FromArgb(150, 152, 160));
                pevent.Graphics.FillPath(fillBrush, path);
                pevent.Graphics.DrawPath(borderPen, path);

                RectangleF textRect = new RectangleF(rect.Left + Dpi.Scale(this, 12), rect.Top + Dpi.Scale(this, 2), rect.Width - Dpi.Scale(this, 24), rect.Height - Dpi.Scale(this, 4));
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                pevent.Graphics.DrawString(Text, Font, textBrush, textRect, format);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
                return path;

            path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public sealed class GameAnalysisWindowData
    {
        public List<GameAnalysisMoveResult> MoveResults { get; init; } = new();
        public GameAnalysisSummary WhiteSummary { get; init; } = new() { SideName = "White" };
        public GameAnalysisSummary BlackSummary { get; init; } = new() { SideName = "Black" };
        public string AnnotatedPgn { get; init; } = "";
        public string StatusText { get; init; } = "Analysis complete.";
        public int OpeningBoundaryIndex { get; init; } = 12;
        public int EndgameBoundaryIndex { get; init; } = -1;
    }

    public sealed class GameCoachReport
    {
        public string Title { get; init; } = "Coach Report";
        public string Subtitle { get; init; } = "";
        public string Verdict { get; init; } = "";
        public string Narrative { get; init; } = "";
        public string WhiteFocus { get; init; } = "";
        public string BlackFocus { get; init; } = "";
        public List<GameCoachMoment> Moments { get; init; } = new();
    }

    public sealed class GameCoachMoment
    {
        public string MoveLabel { get; init; } = "";
        public string Side { get; init; } = "";
        public string Played { get; init; } = "";
        public string Best { get; init; } = "";
        public string Motif { get; init; } = "";
        public string Classification { get; init; } = "";
        public int Loss { get; init; }
        public string CoachNote { get; init; } = "";
        public int PlyIndex { get; init; }
    }

    public sealed class GameCoachReportForm : Form
    {
        private readonly GameCoachReport _report;
        private readonly Label _titleLabel;
        private readonly Label _subtitleLabel;
        private readonly RichTextBox _narrativeBox;
        private readonly DataGridView _momentsGrid;
        private readonly Action<int>? _onMomentActivated;
        private readonly AppSettingsManager _settingsManager =
            new(Path.Combine(AppContext.BaseDirectory, "settings.ini"));

        public GameCoachReportForm(GameCoachReport report, Action<int>? onMomentActivated = null)
        {
            _report = report;
            _onMomentActivated = onMomentActivated;
            Text = "Chess Kit AI Coach";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f, FontStyle.Regular);
            MinimumSize = GetScreenAwareMinimumSize(new Size(1320, 820), new Size(980, 680));
            ApplySavedOrDefaultWindowSize();
            BackColor = Color.FromArgb(20, 20, 23);
            ForeColor = Color.White;

            _titleLabel = new Label
            {
                AutoSize = false,
                Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = report.Title
            };

            _subtitleLabel = new Label
            {
                AutoSize = false,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                ForeColor = Color.FromArgb(178, 180, 188),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = report.Subtitle
            };

            _narrativeBox = new RichTextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(28, 28, 32),
                ForeColor = Color.FromArgb(232, 232, 236),
                Font = new Font("Segoe UI", 10.4f, FontStyle.Regular),
                ReadOnly = true,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            FillNarrative();

            _momentsGrid = new DataGridView
            {
                BackgroundColor = Color.FromArgb(24, 24, 27),
                BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(52, 52, 58),
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                EnableHeadersVisualStyles = false,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                ColumnHeadersHeight = 36,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ScrollBars = ScrollBars.Vertical,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders
            };
            _momentsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(36, 36, 40);
            _momentsGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _momentsGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            _momentsGrid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            _momentsGrid.DefaultCellStyle.BackColor = Color.FromArgb(27, 27, 30);
            _momentsGrid.DefaultCellStyle.ForeColor = Color.White;
            _momentsGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(48, 86, 132);
            _momentsGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            _momentsGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _momentsGrid.DefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
            _momentsGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(31, 31, 35);
            _momentsGrid.AlternatingRowsDefaultCellStyle.ForeColor = Color.White;
            _momentsGrid.AlternatingRowsDefaultCellStyle.WrapMode = DataGridViewTriState.True;
            _momentsGrid.AlternatingRowsDefaultCellStyle.Padding = new Padding(4, 3, 4, 3);
            _momentsGrid.RowTemplate.Height = 38;
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Move", Width = 96 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Side", Width = 92 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Played", Width = 132 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Best", Width = 132 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Motif", Width = 190 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Class", Width = 150 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Loss", Width = 96 });
            _momentsGrid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Coach Note",
                MinimumWidth = 520,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            });
            PopulateMoments();
            // Click a coach moment to jump the analysis board to that position, the
            // same as clicking a row in the Game Analysis move table.
            _momentsGrid.CellClick += (_, e) =>
            {
                if (e.RowIndex < 0)
                    return;
                if (_momentsGrid.Rows[e.RowIndex].Tag is GameCoachMoment clicked)
                    _onMomentActivated?.Invoke(clicked.PlyIndex);
            };

            Controls.AddRange(new Control[] { _titleLabel, _subtitleLabel, _narrativeBox, _momentsGrid });
            Resize += (_, _) => LayoutControls();
            LayoutControls();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveWindowSize();
            base.OnFormClosing(e);
        }

        private void ApplySavedOrDefaultWindowSize()
        {
            AppSettings settings = _settingsManager.Load();
            if (settings.GameCoachDefaultSizeVersion < 2 &&
                settings.GameCoachWindowWidth <= 1380 &&
                settings.GameCoachWindowHeight <= 900)
            {
                settings.GameCoachWindowWidth = 0;
                settings.GameCoachWindowHeight = 0;
                settings.GameCoachDefaultSizeVersion = 2;
                _settingsManager.Save(settings);
            }

            Size preferred = settings.GameCoachWindowWidth > 0 && settings.GameCoachWindowHeight > 0
                ? new Size(settings.GameCoachWindowWidth, settings.GameCoachWindowHeight)
                : GetFirstRunWindowSize(MinimumSize);

            Size = ClampWindowSizeToCurrentScreen(preferred, MinimumSize);
        }

        private void SaveWindowSize()
        {
            if (WindowState == FormWindowState.Minimized)
                return;

            Size size = WindowState == FormWindowState.Normal
                ? Size
                : RestoreBounds.Size;

            if (size.Width <= 0 || size.Height <= 0)
                return;

            AppSettings settings = _settingsManager.Load();
            settings.GameCoachWindowWidth = size.Width;
            settings.GameCoachWindowHeight = size.Height;
            _settingsManager.Save(settings);
        }

        private static Size GetFirstRunWindowSize(Size minimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int width = Math.Max(minimum.Width, (int)Math.Round(workArea.Width * 0.80));
            int height = Math.Max(minimum.Height, (int)Math.Round(workArea.Height * 0.80));
            return new Size(width, height);
        }

        private static Size ClampWindowSizeToCurrentScreen(Size preferred, Size minimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int maxWidth = Math.Max(1, (int)Math.Round(workArea.Width * 0.96));
            int maxHeight = Math.Max(1, (int)Math.Round(workArea.Height * 0.96));
            minimum = GetScreenAwareMinimumSize(minimum, new Size(980, 680));
            maxWidth = Math.Max(maxWidth, minimum.Width);
            maxHeight = Math.Max(maxHeight, minimum.Height);
            int width = Math.Clamp(preferred.Width, minimum.Width, maxWidth);
            int height = Math.Clamp(preferred.Height, minimum.Height, maxHeight);
            return new Size(width, height);
        }

        private static Size GetScreenAwareMinimumSize(Size preferredMinimum, Size absoluteMinimum)
        {
            Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            int screenMaxWidth = Math.Max(absoluteMinimum.Width, (int)Math.Round(workArea.Width * 0.96));
            int screenMaxHeight = Math.Max(absoluteMinimum.Height, (int)Math.Round(workArea.Height * 0.96));
            return new Size(
                Math.Min(preferredMinimum.Width, screenMaxWidth),
                Math.Min(preferredMinimum.Height, screenMaxHeight));
        }

        private void FillNarrative()
        {
            _narrativeBox.Clear();
            AppendHeader("Game Verdict");
            AppendBody(_report.Verdict);
            AppendHeader("Coach Walkthrough");
            AppendBody(_report.Narrative);
            AppendHeader("White Training Focus");
            AppendBody(_report.WhiteFocus);
            AppendHeader("Black Training Focus");
            AppendBody(_report.BlackFocus);
            _narrativeBox.SelectionStart = 0;
            _narrativeBox.SelectionLength = 0;
        }

        private void AppendHeader(string text)
        {
            _narrativeBox.SelectionColor = Color.FromArgb(126, 214, 255);
            _narrativeBox.SelectionFont = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold);
            _narrativeBox.AppendText(text + Environment.NewLine);
        }

        private void AppendBody(string text)
        {
            _narrativeBox.SelectionColor = Color.FromArgb(226, 226, 232);
            _narrativeBox.SelectionFont = new Font("Segoe UI", 10.4f, FontStyle.Regular);
            _narrativeBox.AppendText(text.Trim() + Environment.NewLine + Environment.NewLine);
        }

        private void PopulateMoments()
        {
            foreach (GameCoachMoment moment in _report.Moments)
            {
                int rowIndex = _momentsGrid.Rows.Add(
                    moment.MoveLabel,
                    moment.Side,
                    moment.Played,
                    moment.Best,
                    moment.Motif,
                    moment.Classification,
                    moment.Loss.ToString(CultureInfo.InvariantCulture),
                    moment.CoachNote);
                DataGridViewRow row = _momentsGrid.Rows[rowIndex];
                row.Tag = moment;
                Color accent = moment.Classification switch
                {
                    "Miss" => Color.FromArgb(199, 155, 255),
                    "Blunder" => Color.FromArgb(255, 116, 116),
                    "Mistake" => Color.FromArgb(255, 197, 80),
                    "Inaccuracy" => Color.FromArgb(102, 182, 255),
                    _ => Color.FromArgb(172, 190, 214)
                };
                row.Cells[4].Style.ForeColor = Color.FromArgb(160, 210, 255);
                row.Cells[4].Style.Font = new Font("Segoe UI Semibold", 9.3f, FontStyle.Bold);
                row.Cells[5].Style.ForeColor = accent;
                row.Cells[5].Style.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            }
        }

        private void LayoutControls()
        {
            int margin = 18;
            int titleHeight = ControlHeight(_titleLabel.Font, 10, 44);
            int subtitleHeight = ControlHeight(_subtitleLabel.Font, 8, 32);
            _titleLabel.SetBounds(margin, 16, ClientSize.Width - margin * 2, titleHeight);
            _subtitleLabel.SetBounds(margin, _titleLabel.Bottom + 2, ClientSize.Width - margin * 2, subtitleHeight);

            int contentTop = _subtitleLabel.Bottom + 14;
            int availableHeight = Math.Max(260, ClientSize.Height - contentTop - margin);
            int gridMinHeight = Math.Max(280, ControlHeight(_momentsGrid.Font, 12, 44) * 5);
            int narrativeHeight = Math.Max(250, Math.Min((int)(ClientSize.Height * 0.46), availableHeight - gridMinHeight - 14));
            if (availableHeight - narrativeHeight - 14 < gridMinHeight)
                narrativeHeight = Math.Max(180, availableHeight - gridMinHeight - 14);

            _narrativeBox.SetBounds(margin, contentTop, ClientSize.Width - margin * 2, narrativeHeight);
            _momentsGrid.SetBounds(margin, _narrativeBox.Bottom + 14, ClientSize.Width - margin * 2, Math.Max(120, ClientSize.Height - _narrativeBox.Bottom - margin - 14));
        }

        private static int TextHeight(Font? font)
        {
            return TextRenderer.MeasureText("Hg", font ?? SystemFonts.MessageBoxFont).Height;
        }

        private static int ControlHeight(Font? font, int verticalPadding, int minimum)
        {
            return Math.Max(minimum, TextHeight(font) + verticalPadding);
        }
    }

    public static class GameCoachReportBuilder
    {
        public static GameCoachReport Build(GameAnalysisRequest request, List<GameAnalysisMoveResult> results, string engineName, int depth)
        {
            var whiteMoves = results.Where(m => m.IsWhiteMove).ToList();
            var blackMoves = results.Where(m => !m.IsWhiteMove).ToList();
            var critical = results
                .Where(IsCritical)
                .OrderByDescending(m => m.Loss)
                .Take(10)
                .Select(ToMoment)
                .ToList();

            if (critical.Count == 0)
            {
                critical = results
                    .Where(m => !string.Equals(m.Classification, "Book", StringComparison.Ordinal))
                    .OrderByDescending(m => m.Loss)
                    .Take(6)
                    .Select(ToMoment)
                    .ToList();
            }

            string title = "AI Coach Review";
            string subtitle = $"{request.WhiteName} vs {request.BlackName}  |  {engineName} depth {depth.ToString(CultureInfo.InvariantCulture)}";
            string verdict = BuildVerdict(request, results, whiteMoves, blackMoves);
            string narrative = BuildNarrative(request, results, critical);
            string whiteFocus = BuildFocus(request.WhiteName, whiteMoves);
            string blackFocus = BuildFocus(request.BlackName, blackMoves);

            return new GameCoachReport
            {
                Title = title,
                Subtitle = subtitle,
                Verdict = verdict,
                Narrative = narrative,
                WhiteFocus = whiteFocus,
                BlackFocus = blackFocus,
                Moments = critical
            };
        }

        private static bool IsCritical(GameAnalysisMoveResult move)
        {
            return move.Classification is "Miss" or "Blunder" or "Mistake" || move.Loss >= 90;
        }

        private static GameCoachMoment ToMoment(GameAnalysisMoveResult move)
        {
            string moveLabel = move.IsWhiteMove
                ? $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}."
                : $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}...";
            MoveInsight insight = MoveInsightExtractor.Extract(move);
            return new GameCoachMoment
            {
                MoveLabel = moveLabel,
                Side = move.IsWhiteMove ? "White" : "Black",
                Played = move.MoveText,
                Best = string.IsNullOrWhiteSpace(move.BestMove) ? "-" : move.BestMove,
                Motif = insight.PrimaryMotif,
                Classification = move.Classification,
                Loss = (int)Math.Round(move.Loss),
                CoachNote = BuildMomentNote(move, insight),
                PlyIndex = move.PlyIndex
            };
        }

        private static string BuildMomentNote(GameAnalysisMoveResult move, MoveInsight insight)
        {
            string best = string.IsNullOrWhiteSpace(move.BestMove) || move.BestMove == "-"
                ? "the engine's preferred move"
                : move.BestMove;
            string theme = insight.CoachPhrase;
            string lineHint = !string.IsNullOrWhiteSpace(insight.LineHint)
                ? $" The concrete line starts with {insight.LineHint}."
                : "";

            int variant = Math.Abs(HashCode.Combine(move.PlyIndex, move.MoveText, move.BestMove, move.Classification)) % 3;
            return move.Classification switch
            {
                "Miss" => variant switch
                {
                    0 => $"This was a missed chance around {theme}. The important comparison is not just {move.MoveText} versus {best}, but what forcing resource became available before the chance disappeared.{lineHint}",
                    1 => $"The position offered something more direct than the move played. {best} is the move to calculate first because it addresses {theme} before normal improving moves matter.{lineHint}",
                    _ => $"Treat this as a candidate-move failure: the board was asking for {theme}, and {best} was the cleanest way to ask the hard question.{lineHint}"
                },
                "Blunder" => variant switch
                {
                    0 => $"This move changed the position too violently in the wrong direction. The warning sign was {theme}; after {move.MoveText}, the opponent's reply becomes easier to find than yours.{lineHint}",
                    1 => $"Before committing to {move.MoveText}, the final scan should have been checks, captures, threats, and loose pieces. That scan points back toward {best}.{lineHint}",
                    _ => $"This is the kind of error where the move's idea may be understandable, but the tactical bill arrives immediately. Anchor the review on {theme} and then compare with {best}.{lineHint}"
                },
                "Mistake" => variant switch
                {
                    0 => $"The move is plausible, but it solves the wrong problem. {best} keeps the position closer to its main demand: {theme}.{lineHint}",
                    1 => $"This was a practical drift rather than a single-move collapse. The useful exercise is to name why {best} is more urgent than {move.MoveText}.{lineHint}",
                    _ => $"The evaluation swing says the position had a concrete requirement here. Use {best} as the model move and connect it to {theme}.{lineHint}"
                },
                "Inaccuracy" => variant switch
                {
                    0 => $"A small concession, mostly about precision. {best} improves the position with less compromise and keeps control of {theme}.{lineHint}",
                    1 => $"Nothing catastrophic, but the move lets the opponent breathe. The cleaner version was {best}, mainly because of {theme}.{lineHint}",
                    _ => $"This is a refinement moment: the played move is playable-looking, while {best} carries the same plan with a sharper detail around {theme}.{lineHint}"
                },
                _ => $"Review why {best} was preferred and whether the move changed {theme}. Good coaching starts from the position's demand, not from memorizing the engine line.{lineHint}"
            };
        }

        private static string BuildVerdict(GameAnalysisRequest request, List<GameAnalysisMoveResult> results, List<GameAnalysisMoveResult> whiteMoves, List<GameAnalysisMoveResult> blackMoves)
        {
            int whiteHeavy = CountHeavyErrors(whiteMoves);
            int blackHeavy = CountHeavyErrors(blackMoves);
            double whiteLoss = AverageLoss(whiteMoves);
            double blackLoss = AverageLoss(blackMoves);
            string cleaner = Math.Abs(whiteLoss - blackLoss) < 8
                ? "The game was roughly balanced in practical quality."
                : whiteLoss < blackLoss
                    ? $"{request.WhiteName} played the cleaner game overall."
                    : $"{request.BlackName} played the cleaner game overall.";
            string decisive = whiteHeavy == blackHeavy
                ? "The decisive story is less about one single collapse and more about repeated small decisions accumulating."
                : whiteHeavy > blackHeavy
                    ? $"{request.WhiteName}'s heavier errors shaped the result more than the normal inaccuracies."
                    : $"{request.BlackName}'s heavier errors shaped the result more than the normal inaccuracies.";
            string result = string.IsNullOrWhiteSpace(request.Result) ? "*" : request.Result;
            return $"{cleaner} {decisive} Result: {result}. Average loss: {request.WhiteName} {whiteLoss:0} cp, {request.BlackName} {blackLoss:0} cp.";
        }

        private static string BuildNarrative(GameAnalysisRequest request, List<GameAnalysisMoveResult> results, List<GameCoachMoment> critical)
        {
            if (results.Count == 0)
                return "No analyzed moves are available yet.";

            var sb = new StringBuilder();
            int bookMoves = results.Count(m => string.Equals(m.Classification, "Book", StringComparison.Ordinal));
            if (bookMoves > 0)
                sb.Append($"The opening stayed in known territory for {bookMoves.ToString(CultureInfo.InvariantCulture)} ply, so the early phase should be judged more by when the players left book than by the book moves themselves. ");
            else
                sb.Append("The game left theory immediately, so the first meaningful test was independent development and king safety rather than memorization. ");

            GameAnalysisMoveResult? firstSwing = results.FirstOrDefault(IsCritical);
            if (firstSwing != null)
            {
                string side = firstSwing.IsWhiteMove ? request.WhiteName : request.BlackName;
                sb.Append($"The first major teaching moment came on move {FormatMoveNumber(firstSwing)}, when {side} played {firstSwing.MoveText}. ");
                if (!string.IsNullOrWhiteSpace(firstSwing.BestMove) && firstSwing.BestMove != "-")
                    sb.Append($"The engine preferred {firstSwing.BestMove}, which is the move to analyze slowly before looking at anything else. ");
            }

            if (critical.Count > 0)
            {
                GameCoachMoment top = critical[0];
                sb.Append($"The biggest swing was {top.Side}'s {top.Played} on move {top.MoveLabel}, classified as {top.Classification} with a {top.Loss.ToString(CultureInfo.InvariantCulture)} cp loss. Its main theme was {top.Motif.ToLowerInvariant()}. ");
            }

            var themes = critical
                .Select(m => m.Motif)
                .Where(m => !string.IsNullOrWhiteSpace(m) && !string.Equals(m, "General decision", StringComparison.Ordinal))
                .GroupBy(m => m)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => g.Key.ToLowerInvariant())
                .ToList();
            if (themes.Count > 0)
                sb.Append($"Repeated themes: {string.Join(", ", themes)}. ");

            sb.Append("For improvement, do not memorize the engine line first. Start by naming the position's priority, then compare that human priority against the best move.");
            return sb.ToString();
        }

        private static string BuildFocus(string name, List<GameAnalysisMoveResult> moves)
        {
            if (moves.Count == 0)
                return $"{name}: no moves to review.";

            var counts = moves.GroupBy(m => m.Classification).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            int misses = counts.GetValueOrDefault("Miss");
            int blunders = counts.GetValueOrDefault("Blunder");
            int mistakes = counts.GetValueOrDefault("Mistake");
            int inaccuracies = counts.GetValueOrDefault("Inaccuracy");
            double avgLoss = AverageLoss(moves);

            var advice = new List<string>();
            if (misses > 0)
                advice.Add("spend time on candidate forcing moves before settling for a safe move");
            if (blunders > 0)
                advice.Add("add a final blunder-check for checks, captures, and direct threats");
            if (mistakes > inaccuracies && mistakes > 0)
                advice.Add("slow down when the position changes character, especially after captures or checks");
            if (advice.Count == 0)
                advice.Add("keep sharpening move ordering and conversion technique");

            return $"{name}: {avgLoss:0} cp average loss, {misses} missed chances, {mistakes} mistakes, {blunders} blunders. Main focus: {string.Join("; ", advice)}.";
        }

        private static int CountHeavyErrors(List<GameAnalysisMoveResult> moves)
        {
            return moves.Count(m => m.Classification is "Miss" or "Mistake" or "Blunder");
        }

        private static double AverageLoss(List<GameAnalysisMoveResult> moves)
        {
            var judged = moves.Where(m => !string.Equals(m.Classification, "Book", StringComparison.Ordinal)).ToList();
            if (judged.Count == 0)
                return 0;
            return judged.Average(m => Math.Max(0, m.Loss));
        }

        private static string FormatMoveNumber(GameAnalysisMoveResult move)
        {
            return move.IsWhiteMove
                ? $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}."
                : $"{move.MoveNumber.ToString(CultureInfo.InvariantCulture)}...";
        }
    }

    public sealed class MoveInsight
    {
        public string PrimaryMotif { get; init; } = "General decision";
        public string CoachPhrase { get; init; } = "the position's main demand";
        public string LineHint { get; init; } = "";
    }

    public static class MoveInsightExtractor
    {
        private static readonly Dictionary<char, int> PieceValues = new()
        {
            ['p'] = 100,
            ['n'] = 320,
            ['b'] = 330,
            ['r'] = 500,
            ['q'] = 900
        };

        public static MoveInsight Extract(GameAnalysisMoveResult move)
        {
            string played = move.MoveText.Trim();
            string best = move.BestMove.Trim();
            string bestLower = best.ToLowerInvariant();
            string playedLower = played.ToLowerInvariant();
            double materialDrop = EstimateMaterialDropForMover(move);
            bool playedCheck = played.Contains('+') || played.Contains('#');
            bool bestCheck = best.Contains('+') || best.Contains('#');
            bool playedCapture = played.Contains('x');
            bool bestCapture = best.Contains('x');

            if (string.Equals(move.Classification, "Book", StringComparison.Ordinal))
                return Create("Opening book", "known opening structure and development priorities", best);

            if (move.IsMateScore || move.MateIn.HasValue || best.Contains('#'))
                return Create("Mate pattern", "forcing checks and mating geometry", best);

            if (string.Equals(move.Classification, "Miss", StringComparison.Ordinal) && bestCheck && !playedCheck)
                return Create("Missed forcing move", "checks before quiet moves", best);

            if (string.Equals(move.Classification, "Miss", StringComparison.Ordinal) && bestCapture && !playedCapture)
                return Create("Missed capture", "a concrete capture or tactical pickup", best);

            if (materialDrop >= 300 || (move.Loss >= 180 && LooksQuiet(played) && bestCapture))
                return Create("Hanging material", "loose pieces and undefended material", best);

            if (bestCheck && !playedCheck)
                return Create("Forcing check", "forcing moves that restrict the opponent first", best);

            if (IsCastle(played) || IsCastle(best))
                return Create("King safety", "king safety and rook coordination", best);

            if (played.Contains('=') || best.Contains('=') || HasAdvancedPawn(move.FenBefore, move.IsWhiteMove))
                return Create("Promotion race", "passed pawns and promotion timing", best);

            if (TouchesQueen(played) || TouchesQueen(best))
                return Create("Queen activity", "queen activity and tactical exposure", best);

            if (TouchesKnight(played) || TouchesKnight(best))
                return Create("Knight tactic", "fork squares and knight tempo", best);

            if (TouchesBishop(played) || TouchesBishop(best))
                return Create("Diagonal control", "diagonals, pins, and long-range pressure", best);

            if (TouchesRook(played) || TouchesRook(best))
                return Create("Open file", "rook activity, files, and rank pressure", best);

            if (move.Loss >= 90)
                return Create("Critical tempo", "move order and tempo", best);

            if (move.Loss >= 35)
                return Create("Small concession", "precision and keeping useful tension", best);

            return Create("General decision", "the position's main demand", best);
        }

        private static MoveInsight Create(string motif, string phrase, string best)
        {
            return new MoveInsight
            {
                PrimaryMotif = motif,
                CoachPhrase = phrase,
                LineHint = string.IsNullOrWhiteSpace(best) || best == "-" ? "" : best
            };
        }

        private static bool LooksQuiet(string move)
        {
            return !move.Contains('x') && !move.Contains('+') && !move.Contains('#');
        }

        private static bool IsCastle(string move)
        {
            return move.StartsWith("O-O", StringComparison.Ordinal) || move.StartsWith("0-0", StringComparison.Ordinal);
        }

        private static bool TouchesQueen(string move) => move.StartsWith("Q", StringComparison.Ordinal);
        private static bool TouchesRook(string move) => move.StartsWith("R", StringComparison.Ordinal);
        private static bool TouchesBishop(string move) => move.StartsWith("B", StringComparison.Ordinal);
        private static bool TouchesKnight(string move) => move.StartsWith("N", StringComparison.Ordinal);

        private static double EstimateMaterialDropForMover(GameAnalysisMoveResult move)
        {
            if (string.IsNullOrWhiteSpace(move.FenBefore) || string.IsNullOrWhiteSpace(move.FenAfter))
                return 0;

            int before = MaterialBalance(move.FenBefore);
            int after = MaterialBalance(move.FenAfter);
            int deltaForWhite = after - before;
            int deltaForMover = move.IsWhiteMove ? deltaForWhite : -deltaForWhite;
            return Math.Max(0, -deltaForMover);
        }

        private static int MaterialBalance(string fen)
        {
            string board = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            int white = 0;
            int black = 0;
            foreach (char ch in board)
            {
                char lower = char.ToLowerInvariant(ch);
                if (!PieceValues.TryGetValue(lower, out int value))
                    continue;

                if (char.IsUpper(ch))
                    white += value;
                else
                    black += value;
            }

            return white - black;
        }

        private static bool HasAdvancedPawn(string fen, bool white)
        {
            string board = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            string[] ranks = board.Split('/');
            if (ranks.Length != 8)
                return false;

            for (int row = 0; row < ranks.Length; row++)
            {
                int rank = 8 - row;
                foreach (char ch in ranks[row])
                {
                    if (white && ch == 'P' && rank >= 6)
                        return true;
                    if (!white && ch == 'p' && rank <= 3)
                        return true;
                }
            }

            return false;
        }
    }

    public sealed class GameAnalysisProgress
    {
        public List<GameAnalysisMoveResult> MoveResults { get; init; } = new();
        public GameAnalysisSummary WhiteSummary { get; init; } = new() { SideName = "White" };
        public GameAnalysisSummary BlackSummary { get; init; } = new() { SideName = "Black" };
        public string StatusText { get; init; } = "Analyzing...";
        public int ProgressPercent { get; init; }
        public int OpeningBoundaryIndex { get; init; } = 12;
        public int EndgameBoundaryIndex { get; init; } = -1;
    }
}
