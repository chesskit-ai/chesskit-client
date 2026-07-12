using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    public sealed class SettingsToolbarForm : Form
    {
        private readonly object _lock = new();
        private Rectangle _toolbarRect;
        private Rectangle _currentDisplayRect;
        private bool _enabled = false;
        private bool _shouldBeVisible = false;
        private readonly WinFormsTimer _updateTimer;
        private bool _isExpanded = false;
        // True when the toolbar is currently positioned attached to a tracked
        // window (top edge flush with the window's top edge). False when in
        // fallback position (top-of-screen) or anchored to a board rect. When
        // docked, the paint code uses flat top corners and rounded bottom
        // corners so the toolbar reads as merged with the window.
        private bool _isDockedToWindow = false;
        // When docked, the form's bounds are wider than the visible toolbar
        // to make room for the concave corners that overflow the visible
        // area. All button positions and hit-tests need to be shifted right
        // by this amount so the buttons sit inside the visible area, not
        // the overflow zones. Set to 0 when not docked.
        private int _dockedContentOffsetX = 0;
        // The toolbar is owner-drawn, so WinForms cannot scale it for us.
        // Keep all layout rectangles in 96-DPI logical units and scale the
        // form bounds / paint surface / mouse coordinates at the edge.
        private float _uiScale = 1f;
        private bool _regionSignatureValid = false;
        private Size _lastRegionClientSize = Size.Empty;
        private bool _lastRegionDocked = false;
        private int _lastRegionContentOffsetX = 0;
        private float _lastRegionUiScale = 0f;

        // Engine management fields
        private EngineManager? _engineManager;
        private string _selectedEngineName = "Stockfish";
        private AppSettingsManager? _appSettingsManager;

        // UI Controls
        private readonly List<ToolbarButton> _buttons = new();
        private readonly Dictionary<string, object> _settings = new();
        private readonly ToolTip _buttonToolTip;
        private ContextMenuStrip? _engineDropdownMenu;
        private DateTime _engineDropdownClosedAtUtc = DateTime.MinValue;

        // Fonts and brushes
        private Font _buttonFont;
        private Font _labelFont;
        private Font _metricsFont;
        // Bottom-strip free-cluster fonts. Held as fields (not created per-paint)
        // so the layout MEASUREMENT and the actual DRAW use byte-identical fonts -
        // that exact-match is what keeps the engine-name slot sized correctly.
        private Font _freeChipFont;
        private Font _freeLinkFont;
        private Brush _textBrush;
        private Brush _metricsBrush;
        private Brush _backgroundBrush;
        private Brush _hoverBrush;
        private Pen _borderPen;

        // Settings values - match main program defaults
        private bool _boardFlipped = false;
        private int _initialDepth = 6;
        private int _maxDepth = 12;
        private bool _infiniteAnalysis = false;
        private int _engineThreads = 8;
        private int _arrowCount = 3;
        private bool _coachModeEnabled = false;
        private int _coachLevel = 5;
        private int _coachMarkCount = 1;
        private bool _coachCardEnabled = true;
        private int _hashSize = 128;
        private bool _showEvalBar = false;
        private bool _showEngineLines = false;
        private bool _analysisWhite = false;
        private bool _analysisBlack = false;
        private bool _analysisBoth = false;
        private bool _eloLimitEnabled = false;
        private int _maxEloRating = 2000;
        private bool _speculativeAnalysisEnabled = true;
        private SpeculativeAnalysisMode _speculativeAnalysisMode = SpeculativeAnalysisMode.Balanced;
        private BlitzModeSetting _blitzMode = BlitzModeSetting.On;
        private bool _bulletProfileEnabled = false;
        private bool _humanAdaptiveEnabled = true;
        private HumanPlayProfile _humanPlayProfile = HumanPlayProfile.Balanced;
        private EvalDisplayMode _evalDisplayMode = EvalDisplayMode.Bar;
        private bool _showTaskbarIcon = true;
        private bool _showTaskbarWindow = true;
        private bool _settingsToolbarHidden = false;
        private bool _toolbarNetworkStatsEnabled = false;
        private bool _excludeOverlaysFromCapture = true;
        private HotkeyBindings _hotkeys = new();

        private const int ExpandedHeaderTop = 52;
        private const int ExpandedRowHeight = 40;
        private const int ExpandedSectionGap = 16;
        private const int ExpandedCardPadding = 18;
        private const int ExpandedBottomToolbarHeight = 34;
        private const int ExpandedScreenMargin = 8;
        private const int ExpandedMaxWindowHeight = 720;
        private const int ExpandedScrollStep = 80;
        private const int ExpandedScrollbarWidth = 7;
        private const int ExpandedScrollbarRightPad = 7;
        private const int SliderLabelWidth = 150;
        private const int SliderTrackWidth = 190;
        private const int SliderValueWidth = 54;
        private const int InfiniteDepthSliderValue = 31;
        private const int HumanSectionHeight = 156;
        private const int SpeculativeSectionHeight = 208;
        private const int AppSectionHeight = 416;

        // Lower-half row grid. Every control inside a row vertically
        // centers against the row's midline so toggling state never
        // shifts labels around.
        private const int LowerCheckboxSize = 18;
        private const int LowerButtonHeight = 28;
        private const int LowerButtonWidth = 150;
        private const int LowerCardHeaderHeight = 38;
        private const int LowerCardLeftPad = 16;
        private const int LowerCardRightPad = 16;
        // Inside Human Engine and Speculative cards, content rows (checkboxes,
        // labels, buttons) are INDENTED from the section title's left edge so
        // they read as items belonging to the section rather than sitting
        // flush against the card border.
        private const int HumanContentIndent = 16;
        private const int SpeculativeContentIndent = 16;

        // Rendered widths of labels that we need to position controls AFTER.
        // These are measured once at construction using the actual labelFont
        // so the gap is correct regardless of system DPI / font scaling.
        // (Eyeballed pixel values broke at non-default DPIs.)
        private int _maxEloLabelWidth = 70;        // safe defaults until measured
        private int _playProfileLabelWidth = 90;
        private int _modeLabelWidth = 50;
        private int _blitzModeLabelWidth = 82;
        // Gap inserted between the last slider (Hash) and the Max ELO row,
        // creating a small visual section break. Must match between the
        // draw flow and the layout/hit-test formulas.
        private const int SliderToEloGap = 10;
        private const int AnalysisSliderRowCount = 6;

        // Performance metrics
        private double _currentFps = 0;
        private double _currentFenPerSec = 0;
        private BoardVisionDetector.NetworkMetricsSnapshot _networkMetrics = BoardVisionDetector.NetworkMetricsSnapshot.Empty;
        private string _transientStatusText = "";
        private DateTime _transientStatusUntilUtc = DateTime.MinValue;
        // A PERSISTENT analysis-status hint (e.g. "Connecting to engine…",
        // "Engine unavailable — retrying", "Engine rejected this device") shown in
        // the bottom-strip FPS slot whenever live analysis can't currently produce
        // arrows, so the overlay is never silently blank. Set from Program's
        // per-second metrics tick off the real engine/analysis state; "" means
        // nothing to say (arrows are flowing or analysis is off) and the slot
        // shows FPS as usual. A transient status (ShowTransientStatus) still wins
        // briefly when one is active.
        private string _analysisStatusHint = "";
        private BoardVisionConnectionState _visionConnectionState = BoardVisionConnectionState.Disconnected;

        // Free / connection-quality state, driven from the latest vision + engine
        // responses (see UpdatePerformanceMetrics). _isFreeLimited reflects the build OR a
        // server free tag; _freeReason is the server's rate_capped/busy code (if
        // any) that unlocks the "Free limit · Read more" affordance. _signalBars is
        // derived purely from round-trip latency and is shown for EVERYONE -- it is
        // connection health, never the free cap.
        private bool _isFreeLimited = BuildLimits.IsFreeEdition;
        private string? _freeReason;
        private int _signalBars = -1;       // 0..4, -1 = no measurement yet
        private long _lastLatencyMs;
        // Server-driven Free remaining-moves count, pushed from the latest analysis
        // response via UpdatePerformanceMetrics. The cooldown countdown itself is
        // read LIVE from FreeTierServerState at paint time so it ticks down on the
        // 16ms repaint timer and stays visible for the whole cooldown.
        private int _freeMovesRemaining;
        // The last layout-affecting bottom-strip signature we sized the collapsed
        // toolbar for: the free-limited flag + the amber chip label + the engine
        // label. When any of these change (e.g. the chip appears/changes from
        // "FREE" to "SUSPENDED", or the selected engine name changes) the reserved
        // engine slot / cluster width can change, so the collapsed width must be
        // recomputed and re-laid-out - otherwise the engine label and the chip
        // briefly overlap until some unrelated event happens to re-layout. Seeded
        // empty so the first metrics push always re-measures.
        private string _lastCollapsedLayoutSignature = "";
        // Hit regions (content-rect coords) of the bottom-strip free/connection
        // cluster: the signal bars (hover -> connection-quality tooltip), the amber
        // FREE chip (hover -> free explainer), and the "Free limit · Read more"
        // link (hover + click -> upsell dialog).
        private Rectangle _freeReadMoreHitRect = Rectangle.Empty;
        private Rectangle _freeChipHitRect = Rectangle.Empty;
        private Rectangle _signalBarsHitRect = Rectangle.Empty;
        private bool _hoveredFreeReadMore;

        // Hover tracking
        private ToolbarButton? _hoveredButton = null;
        private string? _hoveredAppAction = null;
        private Point _lastMousePos = Point.Empty;
        private string _visibleTooltipText = "";
        // Hover region of the vision/server connection dot in the metrics strip
        // (content-rect coords), used to show an explanatory tooltip over it.
        private Rectangle _dotHitRect = Rectangle.Empty;

        // Slider dragging
        private string? _draggingSlider = null;
        private bool _isDragging = false;
        private bool _engineSettingsDirty = false;
        private int _expandedScrollOffset = 0;
        private bool _scrollbarDragging = false;
        private int _scrollbarDragStartY = 0;
        private int _scrollbarDragStartOffset = 0;

        public event Action<string, object>? SettingChanged;

        public SettingsToolbarForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            Size = ScaleToDevice(new Size(GetCollapsedToolbarWidth(), 32));
            // Plain opaque BackColor. The form's shape is set via Region
            // (see UpdateFormRegion), so we don't need transparency tricks
            // - pixels outside the region simply aren't drawn or composited.
            BackColor = Color.FromArgb(28, 28, 30);
            DoubleBuffered = true;

            // Initialize drawing resources
            _buttonFont = new Font("Segoe UI", 10f, FontStyle.Regular);
            _labelFont = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _metricsFont = new Font("Consolas", 10f, FontStyle.Bold);
            _freeChipFont = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
            _freeLinkFont = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _textBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
            _metricsBrush = new SolidBrush(Color.FromArgb(100, 255, 100));
            _backgroundBrush = new SolidBrush(Color.FromArgb(255, 35, 35, 38));
            _hoverBrush = new SolidBrush(Color.FromArgb(60, 100, 100, 100));
            _borderPen = new Pen(Color.FromArgb(80, 100, 100, 100), 1);
            _buttonToolTip = new ToolTip
            {
                ShowAlways = true,
                InitialDelay = 150,
                ReshowDelay = 50,
                AutoPopDelay = 4000
            };

            // Initialize engine manager
            InitializeEngineManager();
            InitializeAppSettings();

            InitializeButtons();
            RefreshHotkeyTooltips();
            InitializeLabelMeasurements();
            Size = ScaleToDevice(new Size(GetCollapsedToolbarWidth(), 32));

            // Timer for visibility control
            _updateTimer = new WinFormsTimer { Interval = 16 };
            _updateTimer.Tick += (s, e) =>
            {
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
                    EnsureTopMostZOrder();
                    Invalidate();
                }
            };
            _updateTimer.Start();
        }

        // Initialize engine manager
        private void InitializeEngineManager()
        {
            try
            {
                var enginesPath = Path.Combine(AppContext.BaseDirectory, "engines");
                _engineManager = new EngineManager(enginesPath);
                _engineManager.ScanForEngines();
                _engineManager.LoadSettings();

                if (_engineManager.CurrentEngine != null)
                {
                    _selectedEngineName = _engineManager.CurrentEngine.DisplayName;
                    DebugRuntime.WriteLine($"[Settings] Loaded engine: {_selectedEngineName}");
                }
                else
                {
                    DebugRuntime.WriteLine("[Settings] No engines found");
                }

                _engineManager.EngineChanged += (engine) =>
                {
                    _selectedEngineName = engine.Name;
                    DebugRuntime.WriteLine($"[Settings] Engine changed to: {_selectedEngineName}");
                };
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[Settings] Failed to initialize engine manager: {ex.Message}");
                _engineManager = null;
            }
        }

        private void InitializeAppSettings()
        {
            try
            {
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.ini");
                _appSettingsManager = new AppSettingsManager(settingsPath);
                var settings = _appSettingsManager.Load();
                _speculativeAnalysisEnabled = settings.SpeculativeAnalysisEnabled;
                _speculativeAnalysisMode = settings.SpeculativeAnalysisMode;
                _blitzMode = settings.BlitzMode;
                _bulletProfileEnabled = settings.ToolbarBulletProfileEnabled;
                _humanAdaptiveEnabled = settings.HumanAdaptiveEnabled;
                _humanPlayProfile = settings.HumanPlayProfile;
                _evalDisplayMode = settings.EvalDisplayMode;
                _maxDepth = BuildLimits.ClampDepth(settings.ToolbarDepth);
                _infiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && settings.ToolbarInfiniteAnalysis;
                _initialDepth = GetEffectiveInitialDepth();
                _engineThreads = BuildLimits.ClampThreads(settings.ToolbarThreads);
                _arrowCount = BuildLimits.ClampLines(settings.ToolbarArrowCount);
                _coachModeEnabled = settings.ToolbarCoachModeEnabled;
                _coachLevel = Math.Clamp(settings.ToolbarCoachLevel, 1, 10);
                _coachMarkCount = Math.Clamp(settings.ToolbarCoachMarkCount, 1, 3);
                _coachCardEnabled = settings.ToolbarCoachCardEnabled;
                // Older free builds capped this at 1. In the Free Edition, lift that
                // old saved value to the intended default while still allowing users
                // to manually choose fewer lines later.
                if (BuildLimits.IsFreeEdition && settings.ToolbarArrowCount <= 1)
                    _arrowCount = BuildLimits.ClampLines(3);
                _hashSize = ClampHashSize(settings.ToolbarHashMb);
                _eloLimitEnabled = settings.ToolbarEloLimitEnabled;
                _maxEloRating = Math.Clamp(settings.ToolbarMaxEloRating, 800, 3000);
                _showTaskbarIcon = settings.ShowTaskbarIcon;
                _showTaskbarWindow = settings.ShowTaskbarWindow;
                _settingsToolbarHidden = settings.SettingsToolbarHidden;
                _toolbarNetworkStatsEnabled = settings.ToolbarNetworkStatsEnabled;
                _excludeOverlaysFromCapture = settings.ExcludeOverlaysFromCapture;
                _hotkeys = settings.Hotkeys?.Clone() ?? new HotkeyBindings();
                _hotkeys.Normalize();
            }
            catch (Exception ex)
            {
                DebugRuntime.WriteLine($"[Settings] Failed to initialize app settings: {ex.Message}");
            }
        }

        private void SaveAppSettings()
        {
            if (_appSettingsManager == null)
            {
                return;
            }

            var settings = _appSettingsManager.Load();
            settings.SpeculativeAnalysisEnabled = _speculativeAnalysisEnabled;
            settings.SpeculativeAnalysisMode = _speculativeAnalysisMode;
            settings.BlitzMode = _blitzMode;
            settings.ToolbarBulletProfileEnabled = _bulletProfileEnabled;
            settings.HumanAdaptiveEnabled = _humanAdaptiveEnabled;
            settings.HumanPlayProfile = _humanPlayProfile;
            settings.EvalDisplayMode = _evalDisplayMode;
            settings.ToolbarInitialDepth = GetEffectiveInitialDepth();
            settings.ToolbarDepth = _maxDepth;
            settings.ToolbarInfiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && _infiniteAnalysis;
            settings.ToolbarThreads = BuildLimits.ClampThreads(_engineThreads);
            settings.ToolbarArrowCount = BuildLimits.ClampLines(_arrowCount);
            settings.ToolbarCoachModeEnabled = _coachModeEnabled;
            settings.ToolbarCoachLevel = Math.Clamp(_coachLevel, 1, 10);
            settings.ToolbarCoachMarkCount = Math.Clamp(_coachMarkCount, 1, 3);
            settings.ToolbarCoachCardEnabled = _coachCardEnabled;
            settings.ToolbarHashMb = ClampHashSize(_hashSize);
            settings.ToolbarEloLimitEnabled = _eloLimitEnabled;
            settings.ToolbarMaxEloRating = _maxEloRating;
            settings.ShowTaskbarIcon = _showTaskbarIcon;
            settings.ShowTaskbarWindow = _showTaskbarWindow;
            settings.SettingsToolbarHidden = _settingsToolbarHidden;
            settings.ToolbarNetworkStatsEnabled = _toolbarNetworkStatsEnabled;
            settings.ExcludeOverlaysFromCapture = _excludeOverlaysFromCapture;
            settings.Hotkeys = _hotkeys.Clone();
            _appSettingsManager.Save(settings);
        }

        // Push live metrics + free/connection state from the capture loop. Internal
        // (not public) because RemoteEngineClient.FreeStateSnapshot is an internal
        // type; the only caller is Program, in this same assembly.
        internal void UpdatePerformanceMetrics(
            double fps,
            double fenPerSec,
            BoardVisionDetector.NetworkMetricsSnapshot? networkMetrics = null,
            RemoteEngineClient.FreeStateSnapshot engineFree = default)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdatePerformanceMetrics(fps, fenPerSec, networkMetrics, engineFree)));
                return;
            }

            _currentFps = fps;
            _currentFenPerSec = fenPerSec;
            _networkMetrics = networkMetrics ?? BoardVisionDetector.NetworkMetricsSnapshot.Empty;

            // Free flag: this build is a free OR the server tagged either the
            // vision or the engine response free:true. A paid build only flips
            // here if the server itself says so.
            bool serverFreeLimited = _networkMetrics.IsFreeLimited || engineFree.IsFreeLimited;
            _isFreeLimited = BuildLimits.IsFreeEdition || serverFreeLimited;

            // Free-limit reason that unlocks "Read more". The engine's explicit
            // rate_capped/busy code wins (it is the cap the upsell describes);
            // otherwise fall back to the vision response's FreeReason. Cleared
            // for paid users so the affordance never shows for them.
            string? reason = engineFree.ErrorCode;
            if (string.IsNullOrEmpty(reason))
                reason = _networkMetrics.FreeReason;
            _freeReason = _isFreeLimited ? NormalizeFreeReason(reason) : null;

            // Server-reported moves left in the current Free window (the cooldown
            // countdown is read live from FreeTierServerState at paint). Only the
            // server can set this; a Licensed session reports 0 and shows nothing.
            _freeMovesRemaining = _isFreeLimited ? Math.Max(0, engineFree.FreeMovesRemaining) : 0;

            // Connection quality (signal bars) from round-trip latency. Vision
            // runs every frame so its latency is the steadiest health signal;
            // fall back to the engine round-trip when no vision sample exists.
            long latency = _networkMetrics.LatencyMs > 0
                ? _networkMetrics.LatencyMs
                : (engineFree.HasResult ? engineFree.RoundTripMs : 0);
            _lastLatencyMs = latency;
            _signalBars = LatencyToSignalBars(latency);

            // If a layout-affecting bottom-strip element changed (the chip
            // appeared/changed, or the engine label changed) re-measure and
            // re-lay-out the COLLAPSED bar so the engine label and the free
            // cluster don't overlap in steady state. The draw-time hard-clip
            // already prevents the one-frame transient overlap; this fixes the
            // steady state so the label gets its full width back once the bar is
            // resized. Skip while expanded (the expanded bounds aren't driven by
            // the collapsed width) - it re-measures on the next collapse.
            string layoutSignature = BuildCollapsedLayoutSignature();
            if (!string.Equals(layoutSignature, _lastCollapsedLayoutSignature, StringComparison.Ordinal))
            {
                _lastCollapsedLayoutSignature = layoutSignature;
                if (!_isExpanded && _toolbarRect != Rectangle.Empty)
                {
                    ResizeToolbarForCurrentDpi();
                    return; // ResizeToolbarForCurrentDpi already invalidated.
                }
            }

            Invalidate();
        }

        // The layout-affecting fingerprint of the bottom strip: whether the free
        // cluster is showing, the exact amber chip label (its width varies -
        // "FREE" vs "SUSPENDED"), and the engine indicator label. Changing any of
        // these can change the reserved engine slot / cluster width, so the
        // collapsed toolbar must be re-measured (see UpdatePerformanceMetrics).
        private string BuildCollapsedLayoutSignature()
        {
            string chip = _isFreeLimited ? GetFreeChipLabel() : "";
            (string engineLabel, _) = GetEngineIndicatorLabel();
            return $"{(_isFreeLimited ? 1 : 0)}|{chip}|{engineLabel}";
        }

        // Map round-trip latency (ms) to a 0..5 cellphone-style signal level.
        // -1 means "no measurement yet" (drawn as dim/empty bars, no claim made).
        // This is the FULL cloud round-trip — network PLUS server-side board
        // detection and engine inference — not a raw network ping. Even on a great
        // line (gigabit, fast PC) the floor is ~200 ms because the server has to
        // think, so the thresholds are deliberately generous: ~250 ms is excellent
        // here, and only a genuinely struggling link climbs past half a second.
        private static int LatencyToSignalBars(long latencyMs)
        {
            if (latencyMs <= 0) return -1;
            if (latencyMs < 260) return 5;    // excellent (at/near the inference floor)
            if (latencyMs < 380) return 4;    // very good
            if (latencyMs < 550) return 3;    // good
            if (latencyMs < 850) return 2;    // fair
            if (latencyMs < 1300) return 1;   // weak
            return 0;                          // very poor
        }

        private static string? NormalizeFreeReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return null;
            return reason.Trim().ToLowerInvariant() switch
            {
                "rate_capped" => "rate_capped",
                "busy" => "busy",
                _ => null
            };
        }

        public void ShowTransientStatus(string message, int durationMs = 3500)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowTransientStatus(message, durationMs)));
                return;
            }

            _transientStatusText = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
            _transientStatusUntilUtc = string.IsNullOrEmpty(_transientStatusText)
                ? DateTime.MinValue
                : DateTime.UtcNow.AddMilliseconds(Math.Max(750, durationMs));
            Invalidate();
        }

        // Sets (or clears, with null/empty) the persistent analysis-status hint
        // shown in the FPS slot while live analysis can't produce arrows. Cheap:
        // only repaints when the text actually changes, so the per-second tick can
        // call it unconditionally. The hint is purely informational - it never
        // gates analysis - and is overridden briefly by an active transient status.
        public void SetAnalysisStatusHint(string? hint)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetAnalysisStatusHint(hint)));
                return;
            }

            string next = string.IsNullOrWhiteSpace(hint) ? "" : hint.Trim();
            if (string.Equals(next, _analysisStatusHint, StringComparison.Ordinal))
                return;
            _analysisStatusHint = next;
            Invalidate();
        }

        public void SyncVisionConnectionState(BoardVisionConnectionState state)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncVisionConnectionState(state)));
                return;
            }

            _visionConnectionState = state;
            Invalidate();
        }

        public void SyncAnalysisState(string mode)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncAnalysisState(mode)));
                return;
            }

            _analysisWhite = string.Equals(mode, "WHITE", StringComparison.OrdinalIgnoreCase);
            _analysisBlack = string.Equals(mode, "BLACK", StringComparison.OrdinalIgnoreCase);
            _analysisBoth = string.Equals(mode, "BOTH", StringComparison.OrdinalIgnoreCase);

            var whiteButton = _buttons.FirstOrDefault(b => b.IsWhiteButton);
            var blackButton = _buttons.FirstOrDefault(b => b.IsBlackButton);
            var bothButton = _buttons.FirstOrDefault(b => b.Text == "W+B");

            if (whiteButton != null) whiteButton.IsActive = _analysisWhite;
            if (blackButton != null) blackButton.IsActive = _analysisBlack;
            if (bothButton != null) bothButton.IsActive = _analysisBoth;

            Invalidate();
        }

        public void SyncEvalBarState(bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncEvalBarState(enabled)));
                return;
            }

            _showEvalBar = enabled;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEvalBar");
            if (button != null)
            {
                button.IsActive = enabled;
            }
            Invalidate();
        }

        public void SyncEngineLinesState(bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncEngineLinesState(enabled)));
                return;
            }

            _showEngineLines = enabled;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEngineLines");
            if (button != null)
            {
                button.IsActive = enabled;
            }
            Invalidate();
        }

        public void SyncBoardFlippedState(bool flipped)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncBoardFlippedState(flipped)));
                return;
            }

            _boardFlipped = flipped;
            var button = _buttons.FirstOrDefault(b => b.Tooltip == "Flip Board");
            if (button != null)
            {
                button.IsActive = flipped;
            }
            Invalidate();
        }

        public void SyncCoachModeState(bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncCoachModeState(enabled)));
                return;
            }

            _coachModeEnabled = enabled;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "CoachMode");
            if (button != null)
            {
                button.IsActive = enabled;
            }
            Invalidate();
        }

        public void SyncTaskbarIconState(bool enabled)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncTaskbarIconState(enabled)));
                return;
            }

            _showTaskbarIcon = enabled;
            SaveAppSettings();
            Invalidate();
        }

        public void SyncSettingsToolbarHiddenState(bool hidden)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncSettingsToolbarHiddenState(hidden)));
                return;
            }

            _settingsToolbarHidden = hidden;
            SaveAppSettings();
            Invalidate();
        }

        private int GetEffectiveInitialDepth() => _maxDepth <= 0 ? 0 : Math.Min(BuildLimits.ClampDepth(_maxDepth), 6);
        private static int GetDepthSliderMaximum() => BuildLimits.AllowInfiniteAnalysis ? InfiniteDepthSliderValue : BuildLimits.MaxDepth;

        public int GetInitialDepth() => GetEffectiveInitialDepth();
        public int GetMaxDepth() => BuildLimits.ClampDepth(_maxDepth);
        public bool GetInfiniteAnalysis() => BuildLimits.AllowInfiniteAnalysis && _infiniteAnalysis;
        public int GetEngineThreads() => BuildLimits.ClampThreads(_engineThreads);
        public int GetArrowCount() => BuildLimits.ClampLines(_arrowCount);
        public bool GetCoachModeEnabled() => _coachModeEnabled;
        public int GetCoachLevel() => Math.Clamp(_coachLevel, 1, 10);
        public int GetCoachMarkCount() => Math.Clamp(_coachMarkCount, 1, 3);
        public bool GetCoachCardEnabled() => _coachCardEnabled;
        public int GetHashSize() => ClampHashSize(_hashSize);
        public int GetMaxEloRating() => _maxEloRating;
        public bool GetEloLimitEnabled() => _eloLimitEnabled;
        public bool GetSpeculativeAnalysisEnabled() => _speculativeAnalysisEnabled;
        public SpeculativeAnalysisMode GetSpeculativeAnalysisMode() => _speculativeAnalysisMode;
        public BlitzModeSetting GetBlitzMode() => _blitzMode;
        public bool GetBulletProfileEnabled() => _bulletProfileEnabled;
        public bool GetHumanAdaptiveEnabled() => _humanAdaptiveEnabled;
        public HumanPlayProfile GetHumanPlayProfile() => _humanPlayProfile;
        public EvalDisplayMode GetEvalDisplayMode() => _evalDisplayMode;
        public bool GetShowTaskbarIcon() => _showTaskbarIcon;
        public bool GetShowTaskbarWindowEnabled() => _showTaskbarWindow;

        public bool GetExcludeOverlaysFromCaptureEnabled() => _excludeOverlaysFromCapture;
        public bool GetSettingsToolbarHidden() => _settingsToolbarHidden;
        public bool GetToolbarNetworkStatsEnabled() => _toolbarNetworkStatsEnabled;
        internal HotkeyBindings GetHotkeyBindings() => _hotkeys.Clone();
        public bool IsHumanEngineSelected() => IsHumanEngineName(_selectedEngineName);
        public int GetCurrentToolbarHeight() => _isExpanded
            ? Math.Max(ScaleToDevice(32), Height)
            : ScaleToDevice(32);

        private void RefreshDpiScale()
        {
            int dpi = 96;
            try
            {
                dpi = Math.Max(96, DeviceDpi);
            }
            catch
            {
                dpi = 96;
            }

            // Keep the toolbar layout in real pixels. The expanded settings
            // panel has a carefully tuned fixed layout; scaling the whole paint
            // surface at high DPI creates large dead zones and missing rows.
            _uiScale = 1f;
        }

        private int ScaleToDevice(int logical) => (int)Math.Round(logical * _uiScale, MidpointRounding.AwayFromZero);
        private int ScaleToLogical(int device) => (int)Math.Round(device / _uiScale, MidpointRounding.AwayFromZero);

        // The toolbar deliberately keeps its layout in real pixels (_uiScale == 1)
        // so the expanded panel's tuned layout survives high DPI. But GDI+ renders
        // point-size fonts larger as DPI rises, so any slot that must hold *text*
        // (the FPS slot, the engine indicator, the free chip / read-more link) has
        // to grow with DPI or the text overflows its fixed slot and gets ellipsized
        // (e.g. "Stockfish…" at 175% scaling). Scale text-slot widths by the DPI.
        private float GetTextDpiScale()
        {
            int dpi = 96;
            try { dpi = Math.Max(96, DeviceDpi); } catch { }
            return dpi / 96f;
        }
        private int ScaleTextWidth(int logical) => (int)Math.Ceiling(logical * GetTextDpiScale());

        private Size ScaleToDevice(Size logical) => new(
            Math.Max(1, ScaleToDevice(logical.Width)),
            Math.Max(1, ScaleToDevice(logical.Height)));

        private Point ScaleToDevice(Point logical) => new(ScaleToDevice(logical.X), ScaleToDevice(logical.Y));
        private Point ScaleToLogical(Point device) => new(ScaleToLogical(device.X), ScaleToLogical(device.Y));

        private Rectangle GetLogicalClientRectangle() => new(
            0,
            0,
            Math.Max(1, ScaleToLogical(ClientSize.Width)),
            Math.Max(1, ScaleToLogical(ClientSize.Height)));

        private int GetExpandedContentHeight()
        {
            return GetAppSectionRect().Bottom + 14;
        }

        private int GetExpandedViewportHeight()
        {
            return Math.Max(0, GetLogicalClientRectangle().Height - ExpandedBottomToolbarHeight);
        }

        private int GetMaxExpandedScrollOffset()
        {
            return Math.Max(0, GetExpandedContentHeight() - GetExpandedViewportHeight());
        }

        private bool IsExpandedScrollNeeded()
        {
            return _isExpanded && GetMaxExpandedScrollOffset() > 0;
        }

        private void ClampExpandedScrollOffset()
        {
            if (!_isExpanded)
            {
                _expandedScrollOffset = 0;
                _scrollbarDragging = false;
                return;
            }

            int maxOffset = GetMaxExpandedScrollOffset();
            _expandedScrollOffset = Math.Clamp(_expandedScrollOffset, 0, maxOffset);
            if (maxOffset == 0)
            {
                _scrollbarDragging = false;
            }
        }

        private bool ScrollExpandedContent(int logicalDelta)
        {
            if (!_isExpanded)
                return false;

            int oldOffset = _expandedScrollOffset;
            _expandedScrollOffset = Math.Clamp(_expandedScrollOffset + logicalDelta, 0, GetMaxExpandedScrollOffset());
            if (_expandedScrollOffset == oldOffset)
                return false;

            Invalidate();
            return true;
        }

        private Point GetToolbarHitTestPoint(Point rawClientPoint)
        {
            Point logicalPoint = ScaleToLogical(rawClientPoint);
            return new Point(logicalPoint.X - _dockedContentOffsetX, logicalPoint.Y);
        }

        private Point GetExpandedContentHitTestPoint(Point rawClientPoint)
        {
            Point hitTestPoint = GetToolbarHitTestPoint(rawClientPoint);
            if (_isExpanded && hitTestPoint.Y >= 0 && hitTestPoint.Y < GetExpandedViewportHeight())
            {
                hitTestPoint.Y += _expandedScrollOffset;
            }
            return hitTestPoint;
        }

        private Rectangle ConstrainToolbarTargetToWorkingArea(Rectangle target, Rectangle anchorRect)
        {
            Rectangle workingArea;
            try
            {
                workingArea = Screen.FromRectangle(anchorRect).WorkingArea;
            }
            catch
            {
                workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            }

            int margin = ScaleToDevice(ExpandedScreenMargin);
            int minX = workingArea.Left + margin;
            int maxWidth = Math.Max(ScaleToDevice(260), workingArea.Width - margin * 2);
            if (target.Width > maxWidth)
                target.Width = maxWidth;

            int maxX = Math.Max(minX, workingArea.Right - margin - target.Width);
            target.X = Math.Clamp(target.X, minX, maxX);

            int desiredHeight = target.Height;
            if (_isExpanded)
            {
                int screenMaxHeight = Math.Max(ScaleToDevice(160), workingArea.Height - margin * 2);
                int preferredMaxHeight = Math.Min(
                    ScaleToDevice(ExpandedMaxWindowHeight),
                    (int)Math.Round(workingArea.Height * 0.78, MidpointRounding.AwayFromZero));
                int maxHeight = Math.Min(screenMaxHeight, Math.Max(ScaleToDevice(320), preferredMaxHeight));
                desiredHeight = Math.Min(target.Height, maxHeight);
                target.Height = desiredHeight;
            }

            // Keep the toolbar fully on-screen vertically in BOTH the collapsed and
            // expanded states. This clamp used to run only when expanded, so the
            // collapsed bar could be pushed above the work area (e.g. when it
            // followed a maximized window up to the top edge of the screen) and have
            // its top half clipped off-screen.
            int minY = workingArea.Top + margin;
            int maxY = Math.Max(minY, workingArea.Bottom - margin - desiredHeight);
            target.Y = Math.Clamp(target.Y, minY, maxY);

            return target;
        }

        private void ResizeToolbarForCurrentDpi()
        {
            var topLeft = Location;
            int logicalWidth = _isExpanded
                ? Math.Max(GetCollapsedToolbarWidth(), ScaleToLogical(Math.Max(Width, 1)))
                : GetCollapsedToolbarWidth();
            int logicalHeight = _isExpanded ? GetExpandedToolbarHeight() : 32;
            var target = new Rectangle(topLeft, ScaleToDevice(new Size(logicalWidth, logicalHeight)));
            target = ConstrainToolbarTargetToWorkingArea(target, Bounds == Rectangle.Empty ? target : Bounds);
            Bounds = target;
            _toolbarRect = target;
            _currentDisplayRect = target;
            ClampExpandedScrollOffset();
            UpdateFormRegion();
            Invalidate();
        }

        private static int ClampHashSize(int hashSize)
        {
            int[] supported = { 32, 64, 128, 256, 512, 1024 };
            supported = supported.Where(v => v <= BuildLimits.MaxHashMb).ToArray();
            if (supported.Length == 0)
                return BuildLimits.MaxHashMb;
            return supported.OrderBy(v => Math.Abs(v - hashSize)).First();
        }

        private static bool IsHumanEngineName(string? engineName)
        {
            if (string.IsNullOrWhiteSpace(engineName))
            {
                return false;
            }

            return engineName.Contains("human", StringComparison.OrdinalIgnoreCase);
        }

        private int GetExpandedToolbarHeight()
        {
            return GetExpandedContentHeight() + ExpandedBottomToolbarHeight;
        }

        private void InitializeLabelMeasurements()
        {
            // Measure rendered text widths once so layout code can position
            // checkboxes/buttons immediately after labels without overlapping.
            // We use a temporary Graphics with the same TextRenderingHint that
            // OnPaint uses, so the measurements match the actual draw.
            try
            {
                using var bmp = new Bitmap(1, 1);
                using var g = Graphics.FromImage(bmp);
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                _maxEloLabelWidth = (int)Math.Ceiling(g.MeasureString("Max ELO", _labelFont).Width);
                _playProfileLabelWidth = (int)Math.Ceiling(g.MeasureString("Play Profile", _labelFont).Width);
                _modeLabelWidth = (int)Math.Ceiling(g.MeasureString("Mode", _labelFont).Width);
                _blitzModeLabelWidth = (int)Math.Ceiling(g.MeasureString("Blitz Mode", _labelFont).Width);
            }
            catch
            {
                // Keep the conservative defaults if measurement fails for any reason.
            }
        }

        private void InitializeButtons()
        {
            int x = 10;
            int y = 4;

            // Main menu button
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.Menu,
                IconActivePaths = IconRenderer.Paths.ChevronUp,
                Tooltip = "Settings Menu",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleExpanded(),
                Type = ButtonType.Toggle
            });
            x += 33;

            // Exit button
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.Close,
                Tooltip = "Exit Application",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ExitApplication(),
                Type = ButtonType.Button
            });
            x += 33;

            // Separator
            x += 5;

            // Analyze as White (F2)
            _buttons.Add(new ToolbarButton
            {
                Text = "W",
                Tooltip = "Analyze as White (F2)",
                Bounds = new Rectangle(x, y, 26, 24),
                Action = () => ToggleAnalysisWhite(),
                Type = ButtonType.Toggle,
                IsActive = _analysisWhite,
                IsWhiteButton = true
            });
            x += 31;

            // Analyze as Black (F4)
            _buttons.Add(new ToolbarButton
            {
                Text = "B",
                Tooltip = "Analyze as Black (F4)",
                Bounds = new Rectangle(x, y, 26, 24),
                Action = () => ToggleAnalysisBlack(),
                Type = ButtonType.Toggle,
                IsActive = _analysisBlack,
                IsBlackButton = true
            });
            x += 31;

            // W+B button width is auto-measured. Hardcoding it produced
            // wrong widths at different DPIs - too narrow at 200% (text
            // clipped) or too wide at 100% (visually unbalanced). The font
            // size already scales with DPI, so measuring the rendered text
            // gives a width that's correct at any scaling.
            int wPlusBWidth;
            using (var measureBitmap = new Bitmap(1, 1))
            using (var tmpG = Graphics.FromImage(measureBitmap))
            using (var boldFont = new Font(_buttonFont.FontFamily, _buttonFont.Size + 1, FontStyle.Bold))
            {
                // The active-state W+B is drawn bold one size up, so measure
                // that worst case - it's the widest the text ever gets.
                SizeF measured = tmpG.MeasureString("W+B", boldFont);
                // 14px horizontal padding (7 each side) gives the text
                // breathing room without being noticeably wider than W or B.
                wPlusBWidth = Math.Max(48, (int)Math.Ceiling(measured.Width) + 14);
            }
            _buttons.Add(new ToolbarButton
            {
                Text = "W+B",
                Tooltip = $"Analyze Both Sides ({_hotkeys.AnalyzeBoth})",
                Bounds = new Rectangle(x, y, wPlusBWidth, 24),
                Action = () => ToggleAnalysisBoth(),
                Type = ButtonType.Toggle,
                IsActive = _analysisBoth
            });
            x += wPlusBWidth + 5;

            // Separator
            x += 5;

            // Flip board button
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.ArrowUpDown,
                Tooltip = "Flip Board",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleFlipBoard(),
                Type = ButtonType.Toggle,
                SettingKey = "BoardFlipped",
                IsActive = _boardFlipped
            });
            x += 33;

            // Separator
            x += 5;

            // Eval bar toggle
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.BarChart,
                Tooltip = "Eval Bar (F9)",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleEvalBar(),
                Type = ButtonType.Toggle,
                SettingKey = "ShowEvalBar",
                IsActive = _showEvalBar
            });
            x += 33;

            // Engine lines toggle
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.List,
                Tooltip = "Engine Lines (F8)",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleEngineLines(),
                Type = ButtonType.Toggle,
                SettingKey = "ShowEngineLines",
                IsActive = _showEngineLines
            });
            x += 33;

            // Coach overlay toggle
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.Coach,
                Tooltip = "Coach Mode",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleCoachMode(),
                Type = ButtonType.Toggle,
                SettingKey = "CoachMode",
                IsActive = _coachModeEnabled
            });
            x += 33;

            // Separator
            x += 5;

            // Engine selector button
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.Cpu,
                Tooltip = "Select Engine",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ShowEngineDropdownMenu(new Rectangle(x, y + 24, 150, 200)),
                Type = ButtonType.Dropdown
            });
            x += 33;

            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.SquarePen,
                Tooltip = "Open Analysis Board",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => OpenAnalysisBoardFromToolbar(),
                Type = ButtonType.Button
            });
            x += 33;

            // Debug HUD toggle - small floating window with FPS, capture mode,
            // engine, exec mode, tracking state. Useful for diagnosing what
            // the app is currently doing without the full debug console.
            _buttons.Add(new ToolbarButton
            {
                IconPaths = IconRenderer.Paths.Gauge,
                Tooltip = "Toggle Debug HUD",
                Bounds = new Rectangle(x, y, 28, 24),
                Action = () => ToggleDebugHud(),
                Type = ButtonType.Toggle,
                IsActive = _debugHudVisible
            });
        }

        private void DismissTransientUi()
        {
            _buttonToolTip.Hide(this);
            _visibleTooltipText = "";
            _hoveredButton = null;
            _hoveredAppAction = null;
            _scrollbarDragging = false;
            if (!_isExpanded)
            {
                _expandedScrollOffset = 0;
            }
            _isDragging = false;
            _draggingSlider = null;
            _scrollbarDragging = false;
            _expandedScrollOffset = 0;

            ReleaseCapture();
            EndMenu();

            if (_engineDropdownMenu != null)
            {
                if (_engineDropdownMenu.IsDisposed)
                {
                    _engineDropdownMenu = null;
                }
                else if (_engineDropdownMenu.Visible)
                {
                    _engineDropdownMenu.Close();
                }
            }

            if (_isExpanded)
            {
                _isExpanded = false;

                if (_toolbarRect != Rectangle.Empty)
                {
                    var topLeft = new Point(_toolbarRect.X, _toolbarRect.Y);
                    int newW = GetCollapsedToolbarWidth();
                    Size = new Size(newW, 32);
                    Location = topLeft;
                    _toolbarRect = Bounds;
                    _currentDisplayRect = Bounds;
                    UpdateFormRegion();
                }

                SettingChanged?.Invoke("MenuExpanded", false);
            }

            SettingChanged?.Invoke("ObstructingUiActive", false);
            Invalidate();
        }

        private void OpenAnalysisBoardFromToolbar()
        {
            DismissTransientUi();
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed)
                    return;

                ReleaseCapture();
                EndMenu();
                SettingChanged?.Invoke("OpenAnalysisBoard", true);
            }));
        }

        private bool _debugHudVisible = false;
        private void ToggleDebugHud()
        {
            _debugHudVisible = !_debugHudVisible;
            var button = _buttons.FirstOrDefault(b => b.Tooltip == "Toggle Debug HUD");
            if (button != null) button.IsActive = _debugHudVisible;
            ResizeToolbarForCurrentDpi();
            SettingChanged?.Invoke("DebugHud", _debugHudVisible);
            Invalidate();
        }

        public void SyncDebugHudState(bool visible)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SyncDebugHudState(visible)));
                return;
            }
            _debugHudVisible = visible;
            var button = _buttons.FirstOrDefault(b => b.Tooltip == "Toggle Debug HUD");
            if (button != null) button.IsActive = _debugHudVisible;
            ResizeToolbarForCurrentDpi();
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            int ex = (int)GetWindowLong(Handle, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(Handle, GWL_EXSTYLE, (IntPtr)ex);

            // Form-wide 92% opacity - gives the toolbar a slight see-through
            // effect over the window content beneath. The toolbar's actual
            // shape (concave-corner tab when docked) is established by
            // SetFormRegion, NOT by a transparency colorkey, so we can
            // avoid the colorkey-anti-aliasing artifacts that produced
            // visible colored edges.
            SetLayeredWindowAttributes(Handle, 0, 235, LWA_ALPHA);

            RefreshDpiScale();
            InitializeLabelMeasurements();
            _regionSignatureValid = false;
            ResizeToolbarForCurrentDpi();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            RefreshDpiScale();
            InitializeLabelMeasurements();
            _regionSignatureValid = false;
            ResizeToolbarForCurrentDpi();
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
                    _isExpanded = false;
                    _expandedScrollOffset = 0;
                    _scrollbarDragging = false;
                    SettingChanged?.Invoke("MenuExpanded", false);
                    _buttonToolTip.Hide(this);
                    _visibleTooltipText = "";
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
                if (!visible && _isExpanded)
                {
                    _isExpanded = false;
                    _expandedScrollOffset = 0;
                    _scrollbarDragging = false;
                    SettingChanged?.Invoke("MenuExpanded", false);
                }

                if (!visible)
                {
                    _buttonToolTip.Hide(this);
                    _visibleTooltipText = "";
                }
            }
        }

        public void UpdatePosition(Rectangle boardRect)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdatePosition(boardRect))); return; }

            lock (_lock)
            {
                int collapsedHeight = ScaleToDevice(32);
                int expandedHeight = ScaleToDevice(GetExpandedToolbarHeight());
                const int toolbarMargin = 6;
                int boardWidthLogical = ScaleToLogical(boardRect.Width);

                int toolbarWidthLogical = _isExpanded
                    ? Math.Max(720, Math.Min(boardWidthLogical + 120, 820))
                    : Math.Min(GetCollapsedToolbarWidth(), Math.Max(420, boardWidthLogical + 40));
                int toolbarWidth = ScaleToDevice(toolbarWidthLogical);
                int xOffset = (boardRect.Width - toolbarWidth) / 2;

                int topY = boardRect.Y + ScaleToDevice(toolbarMargin) - ScaleToDevice(40);

                int h = _isExpanded ? expandedHeight : collapsedHeight;

                var newTarget = new Rectangle(
                    boardRect.X + xOffset,
                    topY,
                    toolbarWidth,
                    h
                );

                _isDockedToWindow = false;
                _dockedContentOffsetX = 0;
                newTarget = ConstrainToolbarTargetToWorkingArea(newTarget, boardRect);
                SetSmoothPositionTarget(newTarget);
            }
        }

        /// <summary>
        /// Position the toolbar attached to the top of the window - centered
        /// horizontally on the window, with the top edge of the toolbar
        /// flush against the window's top edge. The toolbar's rounded
        /// bottom corners keep it visually distinct from the window's
        /// content area, while the flush top edge makes it read as part
        /// of the window itself rather than floating above it.
        /// </summary>
        public void UpdateWindowPosition(Rectangle windowRect)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => UpdateWindowPosition(windowRect))); return; }

            lock (_lock)
            {
                int collapsedHeight = ScaleToDevice(32);
                int expandedHeight = ScaleToDevice(GetExpandedToolbarHeight());
                // Concave wings extend outside the visual toolbar area to
                // bridge into the window edge. The form's bounds need to
                // include those wings or they'll be clipped.
                const int topConcaveRadius = 8;
                int windowWidthLogical = ScaleToLogical(windowRect.Width);

                int h = _isExpanded ? expandedHeight : collapsedHeight;
                int toolbarVisualWidthLogical = _isExpanded
                    ? Math.Max(720, Math.Min(windowWidthLogical, 860))
                    : Math.Min(GetCollapsedToolbarWidth(), Math.Max(420, windowWidthLogical - 24));
                int toolbarBoundsWidth = ScaleToDevice(toolbarVisualWidthLogical + topConcaveRadius * 2);
                int xOffset = (windowRect.Width - toolbarBoundsWidth) / 2;

                // Flush against the window's top edge - no gap. Combined
                // with concave-wing top corners on the toolbar, this creates
                // the "merged tab" look.
                int topY = windowRect.Y;

                var newTarget = new Rectangle(
                    windowRect.X + xOffset,
                    topY,
                    toolbarBoundsWidth,
                    h
                );

                _isDockedToWindow = true;
                _dockedContentOffsetX = topConcaveRadius;
                newTarget = ConstrainToolbarTargetToWorkingArea(newTarget, windowRect);
                SetSmoothPositionTarget(newTarget);
            }
        }

        /// <summary>
        /// Sets a new position for the toolbar. We track the target directly
        /// without animation - when a user drags a window, the per-frame
        /// position delta is small enough that 1:1 tracking looks fluid, and
        /// any animation introduces visible lag. The flicker-vs-real-movement
        /// distinction is handled upstream in Program.cs (see toolbar
        /// hysteresis there).
        /// </summary>
        private void SetSmoothPositionTarget(Rectangle newTarget)
        {
            _toolbarRect = newTarget;
            _currentDisplayRect = newTarget;
            if (Bounds != newTarget)
            {
                Bounds = newTarget;
            }
            ClampExpandedScrollOffset();
            UpdateFormRegion();
            EnsureTopMostZOrder();
        }

        // Re-assert the toolbar at the top of the always-on-top band WITHOUT
        // moving, resizing, or activating it. The WinForms TopMost flag is set
        // once at init and never re-applied, so when the tracked window jumps
        // above the toolbar (gaining focus, maximizing, or going fullscreen-
        // topmost) the bar can fall behind and stay there. Re-asserting
        // HWND_TOPMOST on the 16ms visibility timer and on each reposition keeps
        // it reliably on top. Throttled to a few calls/second; SWP_NOACTIVATE
        // means it never steals focus from the game window.
        private DateTime _lastTopmostReassertUtc = DateTime.MinValue;
        private void EnsureTopMostZOrder()
        {
            if (!IsHandleCreated || !Visible) return;
            DateTime now = DateTime.UtcNow;
            if (now < _lastTopmostReassertUtc.AddMilliseconds(150)) return;
            _lastTopmostReassertUtc = now;
            try
            {
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { /* never break the toolbar on a z-order call */ }
        }

        /// <summary>
        /// Set the form's Region to match the visible shape. When docked,
        /// region = tab path so concave corners are actual window-shape cuts
        /// (no transparency math, no colorkey artifacts). When floating,
        /// region = full client rect (rectangular form, OnPaint draws the
        /// rounded corners with anti-aliasing on top of the form's own
        /// background - no color artifacts there either since both are dark).
        /// </summary>
        private void UpdateFormRegion()
        {
            Size clientSize = ClientSize;
            if (_regionSignatureValid &&
                clientSize == _lastRegionClientSize &&
                _isDockedToWindow == _lastRegionDocked &&
                _dockedContentOffsetX == _lastRegionContentOffsetX &&
                Math.Abs(_uiScale - _lastRegionUiScale) < 0.0001f)
            {
                return;
            }

            try
            {
                Region? replacement = null;
                if (_isDockedToWindow)
                {
                    int tcr = _dockedContentOffsetX;
                    var logicalRect = GetLogicalClientRectangle();
                    var visualRect = new Rectangle(
                        tcr, 0,
                        logicalRect.Width - tcr * 2, logicalRect.Height);
                    if (visualRect.Width <= 0 || visualRect.Height <= 0) return;
                    using var path = GetTabRectangle(visualRect, 8, tcr);
                    using var scaledPath = (GraphicsPath)path.Clone();
                    using var matrix = new Matrix();
                    matrix.Scale(_uiScale, _uiScale);
                    scaledPath.Transform(matrix);
                    replacement = new Region(scaledPath);
                }

                Region? oldRegion = Region;
                Region = replacement;
                if (!ReferenceEquals(oldRegion, replacement))
                {
                    oldRegion?.Dispose();
                }

                _lastRegionClientSize = clientSize;
                _lastRegionDocked = _isDockedToWindow;
                _lastRegionContentOffsetX = _dockedContentOffsetX;
                _lastRegionUiScale = _uiScale;
                _regionSignatureValid = true;
            }
            catch { /* defensive - never break the form on a region issue */ }
        }

        // Settings change handlers
        private void ExitApplication()
        {
            SettingChanged?.Invoke("Exit", true);
        }

        private void ToggleAnalysisWhite()
        {
            _analysisWhite = !_analysisWhite;
            if (_analysisWhite)
            {
                _analysisBlack = false;
                _analysisBoth = false;
                var blackButton = _buttons.FirstOrDefault(b => b.IsBlackButton);
                var bothButton = _buttons.FirstOrDefault(b => b.Text == "W+B");
                if (blackButton != null) blackButton.IsActive = false;
                if (bothButton != null) bothButton.IsActive = false;
            }

            var whiteButton = _buttons.FirstOrDefault(b => b.IsWhiteButton);
            if (whiteButton != null) whiteButton.IsActive = _analysisWhite;

            Invalidate();
            PostSettingChanged("ResetDepth", true);
            PostSettingChanged("AnalysisWhite", _analysisWhite);
        }

        private void ToggleAnalysisBlack()
        {
            _analysisBlack = !_analysisBlack;
            if (_analysisBlack)
            {
                _analysisWhite = false;
                _analysisBoth = false;
                var whiteButton = _buttons.FirstOrDefault(b => b.IsWhiteButton);
                var bothButton = _buttons.FirstOrDefault(b => b.Text == "W+B");
                if (whiteButton != null) whiteButton.IsActive = false;
                if (bothButton != null) bothButton.IsActive = false;
            }

            var blackButton = _buttons.FirstOrDefault(b => b.IsBlackButton);
            if (blackButton != null) blackButton.IsActive = _analysisBlack;

            Invalidate();
            PostSettingChanged("ResetDepth", true);
            PostSettingChanged("AnalysisBlack", _analysisBlack);
        }

        private void ToggleAnalysisBoth()
        {
            _analysisBoth = !_analysisBoth;
            if (_analysisBoth)
            {
                _analysisWhite = false;
                _analysisBlack = false;
                var whiteButton = _buttons.FirstOrDefault(b => b.IsWhiteButton);
                var blackButton = _buttons.FirstOrDefault(b => b.IsBlackButton);
                if (whiteButton != null) whiteButton.IsActive = false;
                if (blackButton != null) blackButton.IsActive = false;
            }

            var bothButton = _buttons.FirstOrDefault(b => b.Text == "W+B");
            if (bothButton != null) bothButton.IsActive = _analysisBoth;

            Invalidate();
            PostSettingChanged("ResetDepth", true);
            PostSettingChanged("AnalysisBoth", _analysisBoth);
        }

        private void PostSettingChanged(string setting, object value)
        {
            if (IsDisposed || Disposing)
                return;

            void Raise()
            {
                if (!IsDisposed && !Disposing)
                    SettingChanged?.Invoke(setting, value);
            }

            if (IsHandleCreated)
                BeginInvoke(new Action(Raise));
            else
                Raise();
        }

        private void ToggleExpanded()
        {
            _isExpanded = !_isExpanded;
            _buttonToolTip.Hide(this);
            _visibleTooltipText = "";
            _hoveredButton = null;

            if (_toolbarRect != Rectangle.Empty)
            {
                var topLeft = new Point(_toolbarRect.X, _toolbarRect.Y);
                int newH = _isExpanded ? GetExpandedToolbarHeight() : 32;
                int newW = _isExpanded ? ScaleToLogical(_toolbarRect.Width) : GetCollapsedToolbarWidth();

                var target = new Rectangle(topLeft, ScaleToDevice(new Size(newW, newH)));
                target = ConstrainToolbarTargetToWorkingArea(target, _toolbarRect);
                Bounds = target;
                _toolbarRect = target;
                // Expand/collapse changes size synchronously; sync the
                // animation's display rect so the next position update
                // doesn't try to interpolate from a stale rect.
                _currentDisplayRect = target;
                ClampExpandedScrollOffset();
                UpdateFormRegion();   // size changed - region must follow
            }

            SettingChanged?.Invoke("MenuExpanded", _isExpanded);
        }

        /// <summary>
        /// Opens the toolbar's expanded settings panel (used by the free
        /// window's "Settings" action). No-op if it is already expanded.
        /// </summary>
        public void ShowExpandedSettings()
        {
            if (!_isExpanded)
                ToggleExpanded();
        }

        private void ToggleFlipBoard()
        {
            _boardFlipped = !_boardFlipped;
            var button = _buttons.FirstOrDefault(b => b.Tooltip == "Flip Board");
            if (button != null)
            {
                button.IsActive = _boardFlipped;
            }
            SettingChanged?.Invoke("BoardFlipped", _boardFlipped);
        }

        private void ToggleEvalBar()
        {
            _showEvalBar = !_showEvalBar;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEvalBar");
            if (button != null)
            {
                button.IsActive = _showEvalBar;
            }

            SettingChanged?.Invoke("ShowEvalBar", _showEvalBar);
        }

        private void ToggleEngineLines()
        {
            _showEngineLines = !_showEngineLines;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEngineLines");
            if (button != null)
            {
                button.IsActive = _showEngineLines;
            }

            SettingChanged?.Invoke("ShowEngineLines", _showEngineLines);
        }

        private void ToggleCoachMode()
        {
            _coachModeEnabled = !_coachModeEnabled;
            var button = _buttons.FirstOrDefault(b => b.SettingKey == "CoachMode");
            if (button != null)
            {
                button.IsActive = _coachModeEnabled;
            }

            SaveAppSettings();
            SettingChanged?.Invoke("CoachModeEnabled", _coachModeEnabled);
            Invalidate();
        }

        private void ToggleCoachCard()
        {
            if (!_coachModeEnabled)
                return;

            _coachCardEnabled = !_coachCardEnabled;
            SaveAppSettings();
            SettingChanged?.Invoke("CoachCardEnabled", _coachCardEnabled);
        }

        private void RefreshHotkeyTooltips()
        {
            var whiteButton = _buttons.FirstOrDefault(b => b.IsWhiteButton);
            if (whiteButton != null)
                whiteButton.Tooltip = $"Analyze as White ({_hotkeys.AnalyzeWhite})";

            var blackButton = _buttons.FirstOrDefault(b => b.IsBlackButton);
            if (blackButton != null)
                blackButton.Tooltip = $"Analyze as Black ({_hotkeys.AnalyzeBlack})";

            var bothButton = _buttons.FirstOrDefault(b => b.Text == "W+B");
            if (bothButton != null)
                bothButton.Tooltip = $"Analyze Both Sides ({_hotkeys.AnalyzeBoth})";

            var evalButton = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEvalBar");
            if (evalButton != null)
                evalButton.Tooltip = $"Eval Bar ({_hotkeys.ToggleEvalBar})";

            var linesButton = _buttons.FirstOrDefault(b => b.SettingKey == "ShowEngineLines");
            if (linesButton != null)
                linesButton.Tooltip = $"Engine Lines ({_hotkeys.ToggleEngineLines})";
        }

        private void ToggleSpeculativeAnalysis()
        {
            _speculativeAnalysisEnabled = !_speculativeAnalysisEnabled;
            SaveAppSettings();
            SettingChanged?.Invoke("SpeculativeAnalysisEnabled", _speculativeAnalysisEnabled);
        }

        private void CycleSpeculativeMode()
        {
            _speculativeAnalysisMode = _speculativeAnalysisMode switch
            {
                SpeculativeAnalysisMode.Conservative => SpeculativeAnalysisMode.Balanced,
                SpeculativeAnalysisMode.Balanced => SpeculativeAnalysisMode.Aggressive,
                _ => SpeculativeAnalysisMode.Conservative
            };

            SaveAppSettings();
            SettingChanged?.Invoke("SpeculativeAnalysisMode", _speculativeAnalysisMode);
        }

        private void CycleBlitzMode()
        {
            _blitzMode = _blitzMode switch
            {
                BlitzModeSetting.Auto => BlitzModeSetting.On,
                BlitzModeSetting.On => BlitzModeSetting.Off,
                _ => BlitzModeSetting.Auto
            };

            SaveAppSettings();
            SettingChanged?.Invoke("BlitzMode", _blitzMode);
        }

        private void ToggleBulletProfile()
        {
            _bulletProfileEnabled = !_bulletProfileEnabled;
            SaveAppSettings();
            SettingChanged?.Invoke("BulletProfile", _bulletProfileEnabled);
        }

        private void ToggleHumanAdaptive()
        {
            _humanAdaptiveEnabled = !_humanAdaptiveEnabled;
            SaveAppSettings();
            SettingChanged?.Invoke("HumanAdaptiveEnabled", _humanAdaptiveEnabled);
        }

        private void CycleHumanPlayProfile()
        {
            _humanPlayProfile = _humanPlayProfile switch
            {
                HumanPlayProfile.Human => HumanPlayProfile.Balanced,
                HumanPlayProfile.Balanced => HumanPlayProfile.Hard,
                _ => HumanPlayProfile.Human
            };

            SaveAppSettings();
            SettingChanged?.Invoke("HumanPlayProfile", _humanPlayProfile);
        }

        private void CycleEvalDisplayMode()
        {
            _evalDisplayMode = _evalDisplayMode == EvalDisplayMode.Bar
                ? EvalDisplayMode.Notch
                : EvalDisplayMode.Bar;

            SaveAppSettings();
            SettingChanged?.Invoke("EvalDisplayMode", _evalDisplayMode);
        }

        private void ToggleTaskbarIconSetting()
        {
            _showTaskbarIcon = !_showTaskbarIcon;
            SaveAppSettings();
            SettingChanged?.Invoke("ShowTaskbarIcon", _showTaskbarIcon);
        }

        private void ToggleShowTaskbarWindowSetting()
        {
            _showTaskbarWindow = !_showTaskbarWindow;
            SaveAppSettings();
            SettingChanged?.Invoke("ShowTaskbarWindow", _showTaskbarWindow);
        }

        private void ToggleSettingsToolbarVisibilitySetting()
        {
            _settingsToolbarHidden = !_settingsToolbarHidden;
            SaveAppSettings();
            SettingChanged?.Invoke("SettingsToolbarHidden", _settingsToolbarHidden);
        }

        private void ToggleExcludeOverlaysFromCaptureSetting()
        {
            _excludeOverlaysFromCapture = !_excludeOverlaysFromCapture;
            SaveAppSettings();
            SettingChanged?.Invoke("ExcludeOverlaysFromCapture", _excludeOverlaysFromCapture);
        }

        private void ToggleToolbarNetworkStatsSetting()
        {
            _toolbarNetworkStatsEnabled = !_toolbarNetworkStatsEnabled;
            SaveAppSettings();
            ResizeToolbarForCurrentDpi();
            SettingChanged?.Invoke("ToolbarNetworkStatsEnabled", _toolbarNetworkStatsEnabled);
        }

        private void OpenKeyBindingsDialog()
        {
            SettingChanged?.Invoke("ObstructingUiActive", true);
            try
            {
                using var dialog = new KeyBindingsForm(_hotkeys);
                dialog.Shown += (_, _) =>
                {
                    dialog.Activate();
                    dialog.BringToFront();
                };

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _hotkeys = dialog.Result;
                    _hotkeys.Normalize();
                    SaveAppSettings();
                    RefreshHotkeyTooltips();
                    SettingChanged?.Invoke("KeyBindingsChanged", _hotkeys.Clone());
                    Invalidate();
                }
            }
            finally
            {
                SettingChanged?.Invoke("ObstructingUiActive", false);
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int WM_MOUSEMOVE = 0x0200;
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_LBUTTONUP = 0x0202;
            const int WM_MOUSEWHEEL = 0x020A;
            const int HTTRANSPARENT = -1;
            const int HTCLIENT = 1;

            if (m.Msg == WM_MOUSEMOVE)
            {
                _lastMousePos = GetSignedPointFromLParam(m.LParam);
                Point hitTestPos = GetToolbarHitTestPoint(_lastMousePos);
                Point contentHitTestPos = GetExpandedContentHitTestPoint(_lastMousePos);
                int x = contentHitTestPos.X;

                if (_scrollbarDragging)
                {
                    UpdateExpandedScrollFromScrollbarDrag(hitTestPos.Y);
                    m.Result = IntPtr.Zero;
                    return;
                }

                if (_isDragging && _draggingSlider != null)
                {
                    UpdateSliderValue(_draggingSlider, x);
                    Invalidate();
                    return;
                }

                ToolbarButton? newHover = null;
                foreach (var button in _buttons)
                {
                    if (button.Bounds.Contains(hitTestPos))
                    {
                        newHover = button;
                        break;
                    }
                }

                string? newAppHover = null;
                if (_isExpanded)
                {
                    if (GetKeyBindingsButtonRect().Contains(contentHitTestPos)) newAppHover = "keys";
                    else if (GetEvalDisplayModeButtonRect().Contains(contentHitTestPos)) newAppHover = "evalmode";
                    else if (GetHardwareIdButtonRect().Contains(contentHitTestPos)) newAppHover = "hwid";
                    else if (GetLicenseStatusButtonRect().Contains(contentHitTestPos)) newAppHover = "license";
                    else if (GetAboutButtonRect().Contains(contentHitTestPos)) newAppHover = "about";
                    else if (GetWebsiteButtonRect().Contains(contentHitTestPos)) newAppHover = "website";
                }

                bool newDotHover = newHover == null && _dotHitRect.Contains(hitTestPos);
                // Bottom-strip free/connection cluster hovers (only meaningful when
                // not over a button). Each region is in content-rect coords.
                bool overBars = newHover == null && _signalBarsHitRect.Contains(hitTestPos);
                bool overChip = newHover == null && !overBars && _freeChipHitRect.Contains(hitTestPos);
                bool overReadMore = newHover == null && !overBars && !overChip && _freeReadMoreHitRect.Contains(hitTestPos);

                if (newHover != _hoveredButton || newAppHover != _hoveredAppAction || overReadMore != _hoveredFreeReadMore)
                {
                    _hoveredButton = newHover;
                    _hoveredAppAction = newAppHover;
                    _hoveredFreeReadMore = overReadMore;
                    Invalidate();
                }

                // Tooltips are idempotent (each call no-ops if the same text is
                // already showing). A hovered button wins; otherwise the free
                // chip / signal bars / connection dot show their explainer;
                // otherwise hide any tooltip.
                if (newHover != null)
                    UpdateHoverTooltip(newHover, _lastMousePos);
                else if (overChip)
                    ShowSimpleTooltip(GetFreeChipTooltip());
                else if (overReadMore)
                    ShowSimpleTooltip(FreeReadMoreTooltip);
                else if (overBars)
                    ShowSimpleTooltip(GetSignalBarsTooltip());
                else if (newDotHover)
                    ShowDotTooltip();
                else if (_visibleTooltipText.Length > 0)
                    UpdateHoverTooltip(null, _lastMousePos);
            }
            else if (m.Msg == WM_LBUTTONDOWN)
            {
                Point rawClickPos = GetSignedPointFromLParam(m.LParam);
                Point clickPos = GetToolbarHitTestPoint(rawClickPos);
                Point contentClickPos = GetExpandedContentHitTestPoint(rawClickPos);
                int x = contentClickPos.X;

                foreach (var button in _buttons)
                {
                    if (button.Bounds.Contains(clickPos))
                    {
                        if (button.Type == ButtonType.Dropdown)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (!IsDisposed && !Disposing)
                                    button.Action?.Invoke();
                            }));
                        }
                        else
                        {
                            button.Action?.Invoke();
                        }
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }
                }

                // Bottom-strip free upsell: the "Free limit · Read more" link OR the
                // amber FREE chip itself. The chip is always the click target so the
                // upsell still works when the secondary text is dropped under tight
                // width (Task: chip alone carries the upsell). Same SettingChanged
                // channel the expanded app-action buttons use.
                if ((!_freeReadMoreHitRect.IsEmpty && _freeReadMoreHitRect.Contains(clickPos)) ||
                    (!_freeChipHitRect.IsEmpty && _freeChipHitRect.Contains(clickPos)))
                {
                    SettingChanged?.Invoke("ShowFreeUpsell", true);
                    m.Result = IntPtr.Zero;
                    return;
                }

                if (_isExpanded)
                {
                    if (HandleExpandedScrollbarMouseDown(clickPos))
                    {
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var coachCardRect = GetCoachCardCheckboxRect();
                    if (coachCardRect.Contains(contentClickPos))
                    {
                        ToggleCoachCard();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    // Check for ELO limit checkbox click
                    var checkboxRect = GetEloCheckboxRect();
                    if (checkboxRect.Contains(contentClickPos))
                    {
                        _eloLimitEnabled = !_eloLimitEnabled;
                        SaveAppSettings();
                        SettingChanged?.Invoke("EloLimitEnabled", _eloLimitEnabled);

                        if (_eloLimitEnabled)
                        {
                            SettingChanged?.Invoke("MaxEloRating", _maxEloRating);
                        }

                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var speculativeRect = GetSpeculativeSectionRect();
                    int speculativeSectionX = speculativeRect.X + 18;
                    int speculativeSectionY = speculativeRect.Y + 50;

                    var speculativeCheckboxRect = GetSpeculativeCheckboxRect(speculativeSectionX, speculativeSectionY);
                    if (speculativeCheckboxRect.Contains(contentClickPos))
                    {
                        ToggleSpeculativeAnalysis();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var speculativeModeRect = GetSpeculativeModeButtonRect(speculativeSectionX, speculativeSectionY);
                    if (speculativeModeRect.Contains(contentClickPos))
                    {
                        CycleSpeculativeMode();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var blitzModeRect = GetBlitzModeButtonRect(speculativeSectionX, speculativeSectionY);
                    if (blitzModeRect.Contains(contentClickPos))
                    {
                        CycleBlitzMode();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    if (ShowBulletProfileRow)
                    {
                        var bulletProfileRect = GetBulletProfileCheckboxRect(speculativeSectionX, speculativeSectionY);
                        if (bulletProfileRect.Contains(contentClickPos))
                        {
                            ToggleBulletProfile();
                            Invalidate();
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }

                    var taskbarIconRect = GetTaskbarIconCheckboxRect();
                    if (taskbarIconRect.Contains(contentClickPos))
                    {
                        ToggleTaskbarIconSetting();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    if (ShowTaskbarWindowRow)
                    {
                        var taskbarWindowRect = GetShowTaskbarWindowCheckboxRect();
                        if (taskbarWindowRect.Contains(contentClickPos))
                        {
                            ToggleShowTaskbarWindowSetting();
                            Invalidate();
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }

                    var toolbarVisibleRect = GetSettingsToolbarVisibilityCheckboxRect();
                    if (toolbarVisibleRect.Contains(contentClickPos))
                    {
                        ToggleSettingsToolbarVisibilitySetting();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var networkStatsRect = GetToolbarNetworkStatsCheckboxRect();
                    if (networkStatsRect.Contains(contentClickPos))
                    {
                        ToggleToolbarNetworkStatsSetting();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var captureExclusionRect = GetExcludeOverlaysFromCaptureCheckboxRect();
                    if (captureExclusionRect.Contains(contentClickPos))
                    {
                        ToggleExcludeOverlaysFromCaptureSetting();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var keyBindingsRect = GetKeyBindingsButtonRect();
                    if (keyBindingsRect.Contains(contentClickPos))
                    {
                        OpenKeyBindingsDialog();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var evalDisplayModeRect = GetEvalDisplayModeButtonRect();
                    if (evalDisplayModeRect.Contains(contentClickPos))
                    {
                        CycleEvalDisplayMode();
                        Invalidate();
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var hardwareIdRect = GetHardwareIdButtonRect();
                    if (hardwareIdRect.Contains(contentClickPos))
                    {
                        SettingChanged?.Invoke("ShowHardwareId", true);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var licenseStatusRect = GetLicenseStatusButtonRect();
                    if (licenseStatusRect.Contains(contentClickPos))
                    {
                        SettingChanged?.Invoke("ShowLicenseStatus", true);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var aboutRect = GetAboutButtonRect();
                    if (aboutRect.Contains(contentClickPos))
                    {
                        SettingChanged?.Invoke("ShowAbout", true);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    var websiteRect = GetWebsiteButtonRect();
                    if (websiteRect.Contains(contentClickPos))
                    {
                        SettingChanged?.Invoke("VisitWebsite", true);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                    if (IsHumanEngineSelected())
                    {
                        var humanAdaptiveRect = GetHumanAdaptiveCheckboxRect();
                        if (humanAdaptiveRect.Contains(contentClickPos))
                        {
                            ToggleHumanAdaptive();
                            Invalidate();
                            m.Result = IntPtr.Zero;
                            return;
                        }

                        var humanProfileRect = GetHumanProfileButtonRect();
                        if (humanProfileRect.Contains(contentClickPos))
                        {
                            CycleHumanPlayProfile();
                            Invalidate();
                            m.Result = IntPtr.Zero;
                            return;
                        }
                    }

                    string? slider = GetSliderAt(contentClickPos);
                    if (slider != null)
                    {
                        _isDragging = true;
                        _draggingSlider = slider;
                        UpdateSliderValue(slider, x);
                        Invalidate();
                    }
                }

                m.Result = IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_LBUTTONUP)
            {
                if (_scrollbarDragging)
                {
                    _scrollbarDragging = false;
                    Capture = false;
                    Invalidate();
                    m.Result = IntPtr.Zero;
                    return;
                }

                if (_isDragging)
                {
                    _isDragging = false;
                    _draggingSlider = null;
                    if (_engineSettingsDirty)
                    {
                        SaveAppSettings();
                        _engineSettingsDirty = false;
                    }
                    Invalidate();
                }
                m.Result = IntPtr.Zero;
                return;
            }
            else if (m.Msg == WM_MOUSEWHEEL)
            {
                if (_isExpanded && IsExpandedScrollNeeded())
                {
                    int wheelDelta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
                    ScrollExpandedContent(wheelDelta < 0 ? ExpandedScrollStep : -ExpandedScrollStep);
                    m.Result = IntPtr.Zero;
                    return;
                }
            }
            else if (m.Msg == WM_NCHITTEST)
            {
                Point screenPoint = GetSignedPointFromLParam(m.LParam);
                Point clientPoint = PointToClient(screenPoint);
                Point logicalClientPoint = ScaleToLogical(clientPoint);
                Point hitTestPoint = GetToolbarHitTestPoint(clientPoint);

                foreach (var button in _buttons)
                {
                    if (button.Bounds.Contains(hitTestPoint))
                    {
                        m.Result = (IntPtr)HTCLIENT;
                        return;
                    }
                }

                // Make the bottom-strip free/connection cluster live in collapsed
                // mode too: the Read-more link needs to be clickable, and the
                // bars/chip/link need mouse-move to drive their hover tooltips.
                // (WM_MOUSEMOVE only fires over HTCLIENT regions.)
                if ((!_freeReadMoreHitRect.IsEmpty && _freeReadMoreHitRect.Contains(hitTestPoint)) ||
                    (!_freeChipHitRect.IsEmpty && _freeChipHitRect.Contains(hitTestPoint)) ||
                    (!_signalBarsHitRect.IsEmpty && _signalBarsHitRect.Contains(hitTestPoint)))
                {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }

                if (_isExpanded && logicalClientPoint.Y > 35)
                {
                    m.Result = (IntPtr)HTCLIENT;
                    return;
                }

                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            base.WndProc(ref m);
        }

        private static Point GetSignedPointFromLParam(IntPtr lParam)
        {
            long raw = lParam.ToInt64();
            int x = unchecked((short)(raw & 0xFFFF));
            int y = unchecked((short)((raw >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        private Rectangle GetExpandedScrollbarTrackRect()
        {
            if (!IsExpandedScrollNeeded())
                return Rectangle.Empty;

            var logicalRect = GetLogicalClientRectangle();
            int viewportHeight = GetExpandedViewportHeight();
            if (viewportHeight <= 24)
                return Rectangle.Empty;

            int rightInset = _dockedContentOffsetX + ExpandedScrollbarRightPad;
            int x = Math.Max(
                _dockedContentOffsetX,
                logicalRect.Width - rightInset - ExpandedScrollbarWidth);
            return new Rectangle(
                x,
                8,
                ExpandedScrollbarWidth,
                Math.Max(1, viewportHeight - 16));
        }

        private Rectangle GetExpandedScrollbarThumbRect()
        {
            var track = GetExpandedScrollbarTrackRect();
            if (track == Rectangle.Empty)
                return Rectangle.Empty;

            int contentHeight = Math.Max(1, GetExpandedContentHeight());
            int viewportHeight = Math.Max(1, GetExpandedViewportHeight());
            int maxOffset = GetMaxExpandedScrollOffset();
            int thumbHeight = Math.Max(28, (int)Math.Round(track.Height * (viewportHeight / (double)contentHeight)));
            thumbHeight = Math.Min(track.Height, thumbHeight);
            int travel = Math.Max(0, track.Height - thumbHeight);
            int thumbY = track.Y + (maxOffset <= 0 ? 0 : (int)Math.Round(travel * (_expandedScrollOffset / (double)maxOffset)));

            return new Rectangle(track.X, thumbY, track.Width, thumbHeight);
        }

        private bool HandleExpandedScrollbarMouseDown(Point hitTestPoint)
        {
            if (!IsExpandedScrollNeeded())
                return false;

            var track = GetExpandedScrollbarTrackRect();
            if (track == Rectangle.Empty || !track.Contains(hitTestPoint))
                return false;

            var thumb = GetExpandedScrollbarThumbRect();
            if (thumb.Contains(hitTestPoint))
            {
                _scrollbarDragging = true;
                _scrollbarDragStartY = hitTestPoint.Y;
                _scrollbarDragStartOffset = _expandedScrollOffset;
                Capture = true;
            }
            else
            {
                int direction = hitTestPoint.Y < thumb.Y ? -1 : 1;
                ScrollExpandedContent(direction * Math.Max(ExpandedScrollStep, GetExpandedViewportHeight() - 60));
            }

            return true;
        }

        private void UpdateExpandedScrollFromScrollbarDrag(int currentY)
        {
            var track = GetExpandedScrollbarTrackRect();
            var thumb = GetExpandedScrollbarThumbRect();
            if (track == Rectangle.Empty || thumb == Rectangle.Empty)
            {
                _scrollbarDragging = false;
                Capture = false;
                return;
            }

            int maxOffset = GetMaxExpandedScrollOffset();
            int travel = Math.Max(1, track.Height - thumb.Height);
            int deltaY = currentY - _scrollbarDragStartY;
            int newOffset = _scrollbarDragStartOffset + (int)Math.Round(deltaY * (maxOffset / (double)travel));
            _expandedScrollOffset = Math.Clamp(newOffset, 0, maxOffset);
            Invalidate();
        }

        private string? GetSliderAt(Point pos)
        {
            if (!_isExpanded) return null;

            for (int i = 0; i < AnalysisSliderRowCount; i++)
            {
                if (GetSliderTrackRect(i).Contains(pos))
                {
                    return i switch
                    {
                        0 => "MaxDepth",
                        1 => "Threads",
                        2 => "Arrows",
                        3 => "CoachLevel",
                        4 => "CoachMarks",
                        5 => "Hash",
                        _ => null
                    };
                }
            }

            if (_eloLimitEnabled && GetEloSliderTrackRect().Contains(pos))
            {
                return "MaxElo";
            }

            return null;
        }

        private void UpdateHoverTooltip(ToolbarButton? button, Point mousePos)
        {
            if (button == null || string.IsNullOrWhiteSpace(button.Tooltip))
            {
                _buttonToolTip.Hide(this);
                _visibleTooltipText = "";
                return;
            }

            if (_visibleTooltipText == button.Tooltip)
                return;

            _buttonToolTip.Hide(this);
            _buttonToolTip.Show(button.Tooltip, this, mousePos.X + 12, mousePos.Y + 22, 4000);
            _visibleTooltipText = button.Tooltip;
        }

        // Explains the vision/server connection dot, anchored just below it.
        // Reuses the shared tooltip; idempotent so it can be called every move.
        private void ShowDotTooltip()
        {
            const string text =
                "Server connection status — used only for the cloud AI model.\n\n" +
                "Green = connected  ·  Amber = connecting  ·  Red = offline.\n\n" +
                "Local engines run entirely on your PC, so they keep working\n" +
                "even when this is red. It only reflects the AI model link.";
            if (_visibleTooltipText == text)
                return;

            _buttonToolTip.Hide(this);
            int x = _dotHitRect.Left + _dockedContentOffsetX;
            int y = _dotHitRect.Bottom + 6;
            _buttonToolTip.Show(text, this, x, y, 8000);
            _visibleTooltipText = text;
        }

        // Exact upsell copy for the amber FREE chip (ordinary Free user).
        private const string FreeChipTooltip =
            "Free Edition · ~15 moves, then a brief cooldown · Upgrade for unlimited moves + the human engine.";

        // Chip tooltip: for an ordinary Free user it's the upsell copy above; when
        // the license is INACTIVE it leads with the reason so hovering the chip
        // explains why the app dropped to Free.
        private static string GetFreeChipTooltip()
        {
            string lead = LicenseStatusInfo.WatermarkLead(LicenseStatusInfo.Reason);
            if (string.IsNullOrEmpty(lead))
                return FreeChipTooltip;
            return $"{lead}. Running with Free Edition limits. Renew or contact support to restore full access.";
        }
        private const string FreeReadMoreTooltip =
            "You've hit the Free Edition limit. Click to learn what Upgrade unlocks.";

        // Connection-quality explainer for the signal bars. Reflects link health
        // only — never the free cap — so the text never mentions free.
        private string GetSignalBarsTooltip()
        {
            string strength = _signalBars switch
            {
                5 => "Excellent",
                4 => "Very good",
                3 => "Good",
                2 => "Fair",
                1 => "Weak",
                0 => "Very poor",
                _ => "Measuring"
            };
            string latency = _lastLatencyMs > 0 ? $"~{_lastLatencyMs} ms round-trip" : "no sample yet";
            string rate = _currentFps > 0 ? $"\nVision updates: {_currentFps:F1}/s" : "";
            return
                $"Server connection quality: {strength} ({latency}).{rate}\n\n" +
                "Reflects the round-trip to the cloud server for board\n" +
                "detection and the AI engine. Local engines are unaffected.";
        }

        // Shared tooltip channel for the bottom-strip free/connection cluster,
        // anchored just below the mouse. Idempotent, like the other tooltips.
        private void ShowSimpleTooltip(string text)
        {
            if (string.IsNullOrEmpty(text) || _visibleTooltipText == text)
                return;

            _buttonToolTip.Hide(this);
            _buttonToolTip.Show(text, this, _lastMousePos.X + 12, _lastMousePos.Y + 22, 8000);
            _visibleTooltipText = text;
        }

        private void UpdateSliderValue(string slider, int mouseX)
        {
            Rectangle trackRect = slider switch
            {
                "MaxDepth" => GetSliderTrackRect(0),
                "Threads" => GetSliderTrackRect(1),
                "Arrows" => GetSliderTrackRect(2),
                "CoachLevel" => GetSliderTrackRect(3),
                "CoachMarks" => GetSliderTrackRect(4),
                "Hash" => GetSliderTrackRect(5),
                "MaxElo" => GetEloSliderTrackRect(),
                _ => Rectangle.Empty
            };

            if (trackRect == Rectangle.Empty)
            {
                return;
            }

            int trackX = trackRect.X + 12;
            int trackWidth = trackRect.Width - 24;

            float position = Math.Max(0, Math.Min(1, (float)(mouseX - trackX) / trackWidth));

            switch (slider)
            {
                case "MaxDepth":
                    int oldMax = _maxDepth;
                    bool oldInfinite = _infiniteAnalysis;
                    int depthSliderMax = GetDepthSliderMaximum();
                    int sliderValue = (int)Math.Round(position * depthSliderMax, MidpointRounding.AwayFromZero);
                    _infiniteAnalysis = BuildLimits.AllowInfiniteAnalysis && sliderValue >= InfiniteDepthSliderValue;
                    _maxDepth = _infiniteAnalysis ? BuildLimits.MaxDepth : BuildLimits.ClampDepth(sliderValue);
                    _initialDepth = GetEffectiveInitialDepth();
                    if (oldMax != _maxDepth || oldInfinite != _infiniteAnalysis)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("InitialDepth", GetEffectiveInitialDepth());
                        SettingChanged?.Invoke("MaxDepth", _maxDepth);
                        SettingChanged?.Invoke("InfiniteAnalysis", _infiniteAnalysis);
                    }
                    break;

                case "Threads":
                    int oldThreads = _engineThreads;
                    _engineThreads = BuildLimits.ClampThreads((int)Math.Round(1 + position * (BuildLimits.MaxThreads - 1), MidpointRounding.AwayFromZero));
                    if (oldThreads != _engineThreads)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("EngineThreads", _engineThreads);
                    }
                    break;

                case "Arrows":
                    if (_coachModeEnabled)
                    {
                        return;
                    }

                    int oldArrows = _arrowCount;
                    _arrowCount = BuildLimits.ClampLines(BuildLimits.MaxLines <= 1
                        ? 1
                        : (int)Math.Round(1 + position * (BuildLimits.MaxLines - 1), MidpointRounding.AwayFromZero));
                    if (oldArrows != _arrowCount)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("ArrowCount", _arrowCount);
                    }
                    break;

                case "CoachLevel":
                    if (!_coachModeEnabled)
                    {
                        return;
                    }

                    int oldCoachLevel = _coachLevel;
                    _coachLevel = (int)Math.Round(1 + position * 9, MidpointRounding.AwayFromZero);
                    _coachLevel = Math.Clamp(_coachLevel, 1, 10);
                    if (oldCoachLevel != _coachLevel)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("CoachLevel", _coachLevel);
                    }
                    break;

                case "CoachMarks":
                    if (!_coachModeEnabled)
                    {
                        return;
                    }

                    int oldCoachMarks = _coachMarkCount;
                    _coachMarkCount = (int)Math.Round(1 + position * 2, MidpointRounding.AwayFromZero);
                    _coachMarkCount = Math.Clamp(_coachMarkCount, 1, 3);
                    if (oldCoachMarks != _coachMarkCount)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("CoachMarkCount", _coachMarkCount);
                    }
                    break;

                case "Hash":
                    int oldHash = _hashSize;
                    float hashPos = position * 6;
                    if (hashPos < 1) _hashSize = 32;
                    else if (hashPos < 2) _hashSize = 64;
                    else if (hashPos < 3) _hashSize = 128;
                    else if (hashPos < 4) _hashSize = 256;
                    else if (hashPos < 5) _hashSize = 512;
                    else _hashSize = 1024;
                    _hashSize = ClampHashSize(_hashSize);

                    if (oldHash != _hashSize)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("HashSize", _hashSize);
                    }
                    break;

                case "MaxElo":
                    int oldElo = _maxEloRating;
                    _maxEloRating = (int)Math.Round(800 + position * 2200, MidpointRounding.AwayFromZero); // 800-3000 range
                    _maxEloRating = (_maxEloRating / 50) * 50; // Round to nearest 50
                    if (oldElo != _maxEloRating)
                    {
                        _engineSettingsDirty = true;
                        SettingChanged?.Invoke("MaxEloRating", _maxEloRating);
                    }
                    break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_backgroundBrush == null || _borderPen == null || _textBrush == null || _hoverBrush == null)
            {
                e.Graphics.Clear(Color.FromArgb(35, 35, 38));
                return;
            }

            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.ScaleTransform(_uiScale, _uiScale);

            var rect = GetLogicalClientRectangle();

            // Draw background. Two looks:
            //   - Docked to window: flat top edge + rounded bottom corners
            //     (tab shape) so the toolbar reads as merged with the window.
            //   - Floating (fallback / board-anchored): rounded all four
            //     corners as before.
            if (_isDockedToWindow)
            {
                // The form is wider than the visible toolbar by tcr on each
                // side. Pass the INSET rect to GetTabRectangle - the path's
                // wings will then extend OUT into the overflow zones to
                // bridge into the window's top edge.
                int tcr = _dockedContentOffsetX;
                var visualRect = new Rectangle(
                    rect.X + tcr, rect.Y,
                    rect.Width - tcr * 2, rect.Height);
                using (var path = GetTabRectangle(visualRect, 8, tcr))
                {
                    g.FillPath(_backgroundBrush, path);
                }
            }
            else
            {
                using (var path = GetRoundedRectangle(rect, 4))
                {
                    g.FillPath(_backgroundBrush, path);
                    g.DrawPath(_borderPen, path);
                }
            }

            // Draw expanded content first so the bottom toolbar row always stays on top.
            if (_isExpanded)
            {
                var contentState = g.Save();
                try
                {
                    int viewportHeight = Math.Max(0, rect.Height - ExpandedBottomToolbarHeight);
                    g.SetClip(new Rectangle(0, 0, rect.Width, viewportHeight));
                    if (_expandedScrollOffset != 0)
                    {
                        g.TranslateTransform(0, -_expandedScrollOffset);
                    }
                    DrawExpandedContent(g);
                }
                finally
                {
                    g.Restore(contentState);
                }

                DrawExpandedScrollbar(g);
            }

            // When docked, the form is wider than its visible area to make
            // room for concave corner overflow. Translate the graphics so
            // all subsequent drawing happens within the visible area, then
            // restore. Hit-testing handles the same shift in WndProc by
            // subtracting _dockedContentOffsetX from mouse coordinates.
            using var savedTransform = g.Transform;
            if (_dockedContentOffsetX != 0)
            {
                g.TranslateTransform(_dockedContentOffsetX, 0);
            }
            try
            {
                // Always draw buttons at the bottom of the form
                int buttonY = rect.Height - 28;

                // Update button positions before drawing
                foreach (var button in _buttons)
                {
                    var oldBounds = button.Bounds;
                    button.Bounds = new Rectangle(oldBounds.X, buttonY, oldBounds.Width, oldBounds.Height);
                }

                // Draw buttons
                foreach (var button in _buttons)
                {
                    DrawButton(g, button);
                }

                // Draw performance metrics only inside the bottom toolbar strip.
                // Inset the rect width by the offset so the metrics don't
                // overflow into the right-side concave area.
                var contentRect = _dockedContentOffsetX != 0
                    ? new Rectangle(0, 0, rect.Width - _dockedContentOffsetX * 2, rect.Height)
                    : rect;
                DrawPerformanceMetrics(g, contentRect);
            }
            finally
            {
                g.Transform = savedTransform;
            }
        }

        private void DrawPerformanceMetrics(Graphics g, Rectangle rect)
        {
            // Find the rightmost button edge to avoid overlap.
            int rightmostButtonX = 0;
            foreach (var button in _buttons)
            {
                if (button.Bounds.Right > rightmostButtonX)
                    rightmostButtonX = button.Bounds.Right;
            }

            bool showTransientStatus =
                !string.IsNullOrWhiteSpace(_transientStatusText) &&
                DateTime.UtcNow <= _transientStatusUntilUtc;
            // A persistent analysis hint takes the FPS slot whenever there's
            // something to explain (engine connecting/unavailable/rejected, no
            // engine selected) and no transient status is currently winning. Both
            // render in the same blue "alert" style and suppress the dot + free
            // cluster so the message owns the right area cleanly.
            bool showAlert = showTransientStatus || !string.IsNullOrWhiteSpace(_analysisStatusHint);
            string alertText = showTransientStatus ? _transientStatusText : _analysisStatusHint;
            string fpsText = showAlert ? alertText : BuildMetricsText();
            string dotText = "\u25CF";
            int dotGap = ScaleToDevice(8);

            // Measure the actual rendered width of the text at the current
            // DPI / font size. Without this, the fixed MetricsReservedWidth
            // (which is in logical/DIP coordinates) gets visually narrower
            // than the rendered text at high DPI scaling - at 200% DPI the
            // text "FPS: 30.0" gets clipped to "FPS: 3" or "FPS: 30".
            SizeF textSize = g.MeasureString(fpsText, _metricsFont);
            SizeF dotTextSize = g.MeasureString(dotText, _metricsFont);
            int textWidth = (int)Math.Ceiling(textSize.Width) + ScaleToDevice(4);
            int dotWidth = (int)Math.Ceiling(dotTextSize.Width) + ScaleToDevice(4);
            int metricsStartX = rightmostButtonX + 14;

            int stripTop = rect.Height - 30;
            int stripHeight = 26;
            int rightEdge = rect.Width - 12;

            // Clear the whole bottom strip from the FPS slot to the right edge
            // (covers FPS, dot and the engine indicator) without erasing any
            // expanded-panel cards drawn earlier above the strip.
            using (var clearBrush = new SolidBrush(Color.FromArgb(245, 35, 35, 38)))
            {
                g.FillRectangle(clearBrush, metricsStartX - 5, stripTop,
                    rect.Width - metricsStartX + 5, stripHeight);
            }

            if (showAlert)
            {
                // An alert (transient "Select an engine first", or the persistent
                // analysis hint "Connecting to engine…" / "Engine unavailable —
                // retrying" / "Engine rejected this device") takes over the whole
                // right area; hide the dot + engine + free cluster while it shows
                // so no stale hit targets remain.
                _dotHitRect = Rectangle.Empty;
                _freeChipHitRect = Rectangle.Empty;
                _freeReadMoreHitRect = Rectangle.Empty;
                var msgRect = new Rectangle(metricsStartX, stripTop, Math.Max(1, rightEdge - metricsStartX), stripHeight);
                using var msgFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                using var msgBrush = new SolidBrush(Color.FromArgb(92, 164, 255));
                g.DrawString(fpsText, _metricsFont, msgBrush, msgRect, msgFormat);
            }
            else
            {
                // Latency text, right after the buttons - colored by the same
                // tier as the signal bars (gaming-ping style), dim while no
                // sample has been measured yet.
                var textRect = new Rectangle(metricsStartX, stripTop, Math.Max(1, textWidth), stripHeight);
                using (var metricsFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center
                })
                using (var pingBrush = new SolidBrush(
                    _lastLatencyMs > 0 ? SignalBarColor(_signalBars) : Color.FromArgb(150, 150, 155)))
                {
                    g.DrawString(fpsText, _metricsFont, pingBrush, textRect, metricsFormat);
                }

                // Connection-status dot, tight against the FPS text.
                var dotRect = new Rectangle(textRect.Right + dotGap, stripTop, Math.Max(1, dotWidth), stripHeight);
                using (var dotFormat = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.None,
                    FormatFlags = StringFormatFlags.NoClip
                })
                using (var dotBrush = new SolidBrush(GetVisionConnectionColor()))
                {
                    g.DrawString(dotText, _metricsFont, dotBrush, dotRect, dotFormat);
                }
                // Remember the dot's location (inflated for easier hover) so the
                // mouse handler can show an explanatory tooltip over it.
                _dotHitRect = Rectangle.Inflate(dotRect, ScaleToDevice(6), ScaleToDevice(3));

                // Current-engine indicator: grouped right after the FPS/dot. The
                // engine label has PRIORITY over the free cluster - measure what it
                // actually needs (real font), and tell the cluster not to draw its
                // (droppable) secondary text past that point so the label is never
                // ellipsized to "St…".
                (string engineLabel, Color engineColor) = GetEngineIndicatorLabel();
                int engineLeft = Math.Max(dotRect.Right + ScaleToDevice(16),
                                          metricsStartX + ScaleTextWidth(GetMetricsReservedWidth()) + ScaleToDevice(14));
                int engineLabelWidth = (int)Math.Ceiling(g.MeasureString(engineLabel, _metricsFont).Width) + ScaleToDevice(4);
                int engineNeededRight = engineLeft + engineLabelWidth + ScaleToDevice(ClusterGapPx);

                // Right-anchored free / connection cluster: [secondary text]
                // [FREE chip] [signal bars], laid out right-to-left from the strip's
                // right edge. Returns its leftmost x so the engine slot stops there.
                // Anchor the cluster just to the right of the engine label
                // (consistent, scale-aware spacing) rather than pinning it to the
                // strip's far edge — the latter left a big, window-width-dependent
                // gap between the engine label and the FREE/status chip.
                int clusterContentW = MeasureActiveFreeClusterWidth(g);
                int clusterAnchor = Math.Min(rightEdge, engineNeededRight + ScaleToDevice(ClusterGapPx) + clusterContentW);
                int clusterRight = DrawFreeConnectionCluster(g, stripTop, stripHeight, clusterAnchor, engineNeededRight);

                // Subtle separator between the FPS/dot and the engine label.
                using (var sepPen = new Pen(Color.FromArgb(60, 100, 100, 100), 1))
                {
                    g.DrawLine(sepPen, engineLeft - 8, stripTop + 2, engineLeft - 8, stripTop + stripHeight - 2);
                }
                // The engine label must NEVER paint under/over the free cluster,
                // even for the one frame after the chip appears or its label
                // changes (e.g. "FREE" -> "SUSPENDED") before the collapsed width
                // is recomputed. HARD-CLIP its right edge to just left of the
                // cluster: in steady state the collapsed width is sized so the
                // full label fits before the cluster (see GetCollapsedToolbarWidth),
                // so this clamp is a no-op then; only on the transient overlap frame
                // does it kick in and ellipsize ("Stockfish (serv…") instead of
                // bleeding into the chip ("Stockfish (serv]FREE"). The label keeps
                // priority - the cluster already drops its secondary text first.
                int engineMaxRight = clusterRight - ScaleToDevice(10);
                int engineRight = Math.Min(engineLeft + engineLabelWidth, engineMaxRight);
                // When the bar is width-clamped so narrow that not even a sliver of the
                // engine label fits before the cluster, DROP the label for this frame
                // rather than paint it out to the strip's right edge ON TOP of the
                // signal bars / FREE chip. The old fallback clamped to `rightEdge`,
                // which produced exactly that overlap (garbled engine name over the
                // status cluster) whenever the FREE chip widened the cluster on a
                // small board. The natural collapsed width still reserves room for the
                // full label (GetCollapsedToolbarWidth), so this only bites when the
                // user shrinks the bar below that — and the cluster already conveys the
                // live state.
                if (engineRight - engineLeft >= ScaleToDevice(6))
                {
                    var engineRect = new Rectangle(engineLeft, stripTop, engineRight - engineLeft, stripHeight);
                    using (var engineFormat = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    })
                    using (var engineBrush = new SolidBrush(engineColor))
                    {
                        g.DrawString(engineLabel, _metricsFont, engineBrush, engineRect, engineFormat);
                    }
                }
            }

            // Separator line between buttons and the FPS slot.
            using (var separatorPen = new Pen(Color.FromArgb(60, 100, 100, 100), 1))
            {
                g.DrawLine(separatorPen, metricsStartX - 10, stripTop + 2,
                    metricsStartX - 10, stripTop + stripHeight - 2);
            }
        }

        private const int ClusterGapPx = 10;          // gap between cluster items (logical)
        private const int SignalBarsContentWidth = 23; // 5 bars * 3 + 4 gaps * 2 (logical)

        // The amber chip label. Ordinarily "FREE"; when the license is INACTIVE
        // (suspended/expired/revoked/unknown) it names the reason ("SUSPENDED" /
        // "EXPIRED" / "REVOKED" / "INACTIVE") so the user can tell why this is Free.
        // Only meaningful for a Free session; the chip is drawn only when
        // _isFreeLimited, so the reason is correctly gated at the draw site.
        private static string GetFreeChipLabel() =>
            LicenseStatusInfo.ChipLabel(LicenseStatusInfo.Reason);

        // The amber chip's drawn width (device px) at the current DPI. The label can
        // be "FREE" or a (wider) inactive-reason word, so measure the CURRENT label
        // with the real font; the reservation then matches the draw exactly.
        private int MeasureFreeChipWidth(Graphics g)
        {
            SizeF chipTextSize = g.MeasureString(GetFreeChipLabel(), _freeChipFont);
            int chipPadX = ScaleToDevice(7);
            return (int)Math.Ceiling(chipTextSize.Width) + chipPadX * 2;
        }

        // The server-driven secondary cluster text (device-px width via the real
        // link font). Empty when there's nothing to say. Priority of meaning:
        //   - in cooldown      -> "resets in M:SS"   (the live countdown)
        //   - serving + count  -> "N moves left"
        //   - serving + reason -> "Free limit · Read more" (rate_capped/busy upsell)
        // This is the element that DROPS first when horizontal space is tight; the
        // FREE chip stays and carries the upsell click target.
        private string GetFreeSecondaryText()
        {
            if (!_isFreeLimited)
                return "";

            int cooldown = FreeTierServerState.CooldownRemainingSeconds;
            if (cooldown > 0)
                return $"resets in {FreeTierServerState.FormatCooldown(cooldown)}";

            if (_freeMovesRemaining > 0)
                return _freeMovesRemaining == 1
                    ? "1 move left"
                    : $"{_freeMovesRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture)} moves left";

            if (!string.IsNullOrEmpty(_freeReason))
                return "Free limit · Read more";

            return "";
        }

        private int MeasureFreeSecondaryWidth(Graphics g, string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            return (int)Math.Ceiling(g.MeasureString(text, _freeLinkFont).Width) + ScaleToDevice(4);
        }

        // Width (device px) to RESERVE for the right-anchored free/connection
        // cluster: signal bars (everyone) + FREE chip (free) + the WIDEST secondary
        // text it can show (free). Using the worst-case secondary string keeps the
        // collapsed width stable as the live text changes (countdown ticking,
        // moves-left dropping) instead of jittering, while still guaranteeing the
        // engine label fits beside the cluster at its widest. Measured with the real
        // fonts at device DPI so it matches the draw. Non-free pays only for bars.
        private int MeasureFreeClusterWidth(Graphics g)
        {
            int gap = ScaleToDevice(ClusterGapPx);
            int width = ScaleToDevice(SignalBarsContentWidth);
            if (_isFreeLimited)
            {
                width += gap + MeasureFreeChipWidth(g);
                // Widest secondary string the cluster ever renders.
                int secondary = MeasureFreeSecondaryWidth(g, "Free limit · Read more");
                if (secondary > 0)
                    width += gap + secondary;
            }
            return width;
        }

        // Draws the right-anchored connection signal bars, the amber FREE chip
        // (free only) and the server-driven secondary text (moves-left / cooldown
        // countdown / upsell). The secondary text is DROPPED if drawing it would
        // push the cluster left of engineNeededRight - the engine label has
        // priority and must never be truncated to fit the cluster. Returns the
        // leftmost device-x the cluster occupies so the engine label is capped just
        // left of it. Hit rectangles are stored in content-rect coords.
        // Like MeasureFreeClusterWidth, but measures the CURRENT secondary text
        // (usually empty) instead of the worst-case string, so the caller can
        // anchor the cluster tightly to the right of the engine label rather than
        // reserving the maximum width (which would re-open the big gap).
        private int MeasureActiveFreeClusterWidth(Graphics g)
        {
            int gap = ScaleToDevice(ClusterGapPx);
            int width = ScaleToDevice(SignalBarsContentWidth);
            if (_isFreeLimited)
                width += gap + MeasureFreeChipWidth(g);
            int secondaryW = MeasureFreeSecondaryWidth(g, GetFreeSecondaryText());
            if (secondaryW > 0)
                width += gap + secondaryW;
            return width;
        }

        private int DrawFreeConnectionCluster(Graphics g, int stripTop, int stripHeight, int rightEdge, int engineNeededRight)
        {
            int cursorX = rightEdge;
            int midY = stripTop + stripHeight / 2;
            int gap = ScaleToDevice(ClusterGapPx);

            // ---- Signal bars (rightmost, shown for everyone) ----
            int barCount = 5;
            int barWidth = ScaleToDevice(3);
            int barGap = ScaleToDevice(2);
            int barsWidth = barCount * barWidth + (barCount - 1) * barGap;
            int barsRight = cursorX;
            int barsLeft = barsRight - barsWidth;
            int maxBarHeight = Math.Min(ScaleToDevice(14), stripHeight - ScaleToDevice(6));
            int minBarHeight = Math.Max(ScaleToDevice(3), maxBarHeight / 4);
            int barsBottom = midY + maxBarHeight / 2;

            Color activeColor = SignalBarColor(_signalBars);
            using (var activeBrush = new SolidBrush(activeColor))
            using (var dimBrush = new SolidBrush(Color.FromArgb(70, 150, 150, 155)))
            {
                for (int i = 0; i < barCount; i++)
                {
                    int h = minBarHeight + (int)Math.Round((maxBarHeight - minBarHeight) * (i / (double)(barCount - 1)));
                    int x = barsLeft + i * (barWidth + barGap);
                    var barRect = new Rectangle(x, barsBottom - h, barWidth, h);
                    bool lit = _signalBars > 0 && i < _signalBars;
                    g.FillRectangle(lit ? activeBrush : dimBrush, barRect);
                }
            }
            // Hover region over the bars reuses the dot tooltip channel via its own
            // hit rect (handled in WndProc) — inflate for an easy target.
            _signalBarsHitRect = Rectangle.Inflate(
                new Rectangle(barsLeft, barsBottom - maxBarHeight, barsWidth, maxBarHeight),
                ScaleToDevice(5), ScaleToDevice(4));
            cursorX = barsLeft - gap;

            // ---- Amber chip (Free / free-tagged session only) ----
            // "FREE" normally; the inactive-license reason word otherwise.
            if (_isFreeLimited)
            {
                string chipText = GetFreeChipLabel();
                int chipW = MeasureFreeChipWidth(g);
                int chipH = Math.Min(ScaleToDevice(16), stripHeight - ScaleToDevice(6));
                int chipRight = cursorX;
                int chipLeft = chipRight - chipW;
                var chipRect = new Rectangle(chipLeft, midY - chipH / 2, chipW, chipH);

                Color amberFill = Color.FromArgb(46, 244, 166, 78);   // soft amber wash
                Color amberEdge = Color.FromArgb(170, 244, 166, 78);
                Color amberText = Color.FromArgb(245, 198, 120);
                using (var path = GetRoundedRectangle(chipRect, ScaleToDevice(4)))
                using (var fill = new SolidBrush(amberFill))
                using (var edge = new Pen(amberEdge, 1f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
                using (var chipBrush = new SolidBrush(amberText))
                using (var chipFmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    g.DrawString(chipText, _freeChipFont, chipBrush, chipRect, chipFmt);
                }
                _freeChipHitRect = Rectangle.Inflate(chipRect, ScaleToDevice(3), ScaleToDevice(3));
                cursorX = chipLeft - gap;
            }
            else
            {
                _freeChipHitRect = Rectangle.Empty;
            }

            // ---- Server-driven secondary text (drops first when space is tight) ----
            string secondaryText = GetFreeSecondaryText();
            int secondaryW = MeasureFreeSecondaryWidth(g, secondaryText);
            // Only draw it if it fits without intruding on the engine label's needed
            // extent. When it doesn't fit we silently drop it (chip still upsells).
            bool fitsSecondary = secondaryW > 0 && (cursorX - secondaryW) >= engineNeededRight;
            if (fitsSecondary)
            {
                int linkRight = cursorX;
                int linkLeft = linkRight - secondaryW;
                var linkRect = new Rectangle(linkLeft, stripTop, secondaryW, stripHeight);

                // Quiet by default, brighter + underlined on hover.
                Color linkColor = _hoveredFreeReadMore
                    ? Color.FromArgb(150, 196, 255)
                    : Color.FromArgb(120, 150, 190);
                using (var linkBrush = new SolidBrush(linkColor))
                using (var linkFmt = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    g.DrawString(secondaryText, _freeLinkFont, linkBrush, linkRect, linkFmt);
                    if (_hoveredFreeReadMore)
                    {
                        SizeF measured = g.MeasureString(secondaryText, _freeLinkFont);
                        int underlineY = midY + (int)Math.Ceiling(measured.Height / 2) - ScaleToDevice(1);
                        using var underline = new Pen(linkColor, 1f);
                        g.DrawLine(underline, linkLeft, underlineY, linkLeft + (int)measured.Width, underlineY);
                    }
                }
                // The "Read more" hover/click target only applies when there is an
                // upsell reason; the countdown / moves text is informational.
                _freeReadMoreHitRect = string.IsNullOrEmpty(_freeReason)
                    ? Rectangle.Empty
                    : Rectangle.Inflate(linkRect, ScaleToDevice(4), ScaleToDevice(2));
                cursorX = linkLeft - gap;
            }
            else
            {
                _freeReadMoreHitRect = Rectangle.Empty;
            }

            return cursorX;
        }

        // 0..4 (or -1 = unknown) signal level -> bar colour. Green when strong,
        // amber mid, red weak; dim grey when we have no measurement yet.
        private static Color SignalBarColor(int bars)
        {
            return bars switch
            {
                >= 4 => Color.FromArgb(64, 220, 112),    // strong (matches connected dot)
                3 => Color.FromArgb(240, 200, 90),       // good
                2 => Color.FromArgb(255, 142, 66),       // fair
                1 => Color.FromArgb(250, 108, 64),       // weak
                0 => Color.FromArgb(235, 82, 82),        // none / very poor
                _ => Color.FromArgb(120, 150, 150, 155)  // unknown (dim)
            };
        }

        // Current engine label + colour for the toolbar indicator: amber when
        // nothing is selected, blue for a server engine, green for a local one.
        // Drawn in its own slot at the far right of the bottom strip.
        private (string label, Color color) GetEngineIndicatorLabel()
        {
            var cur = _engineManager?.CurrentEngine;
            if (cur == null)
                return ("No engine selected", Color.FromArgb(255, 170, 70));        // amber warning
            if (cur.Source == EngineSource.Remote)
                return (cur.DisplayName, Color.FromArgb(92, 164, 255));             // blue = server ("... (server)")
            return (cur.DisplayName + "  · local", Color.FromArgb(150, 210, 160));  // green = local
        }

        private Color GetVisionConnectionColor()
        {
            return _visionConnectionState switch
            {
                BoardVisionConnectionState.Connected => Color.FromArgb(64, 220, 112),
                BoardVisionConnectionState.Connecting => Color.FromArgb(255, 205, 72),
                BoardVisionConnectionState.HttpFallback => Color.FromArgb(92, 164, 255),
                BoardVisionConnectionState.Cooldown => Color.FromArgb(255, 142, 66),
                _ => Color.FromArgb(235, 82, 82)
            };
        }

        // The user-facing metric in the bottom strip is the server round-trip
        // latency (gaming-style ping), colored by the same tier as the signal
        // bars. FPS moved to the debug HUD - a capture/upload rate means little
        // to users, while "how fast do my arrows come back" is exactly this ms.
        private string BuildMetricsText()
        {
            string ping = _lastLatencyMs > 0 ? $"{_lastLatencyMs} ms" : "-- ms";
            if (!_toolbarNetworkStatsEnabled)
                return ping;

            string transport = string.IsNullOrWhiteSpace(_networkMetrics.Transport)
                ? "none"
                : _networkMetrics.Transport;
            return $"{ping} | {transport} | {_networkMetrics.KilobytesPerSecond:F2} KB/s | avg {_networkMetrics.AveragePacketKilobytes:F2} KB";
        }

        // Width reserved for the FPS slot in the bottom toolbar strip. This
        // must match between the layout calculation (GetCollapsedToolbarWidth)
        // and the actual draw call so the slot doesn't get clamped over the
        // buttons in collapsed mode.
        //
        // Bumped from 110 to 160 because at 200% DPI scaling on high-res
        // monitors, "FPS: 30.0" was getting clipped to "FPS: 3". The actual
        // draw path also auto-measures the text width as a safety net, but
        // this larger reservation ensures the toolbar's collapsed width is
        // wide enough to hold the slot in the first place.
        // Trimmed from 260: the FPS text ("FPS: 36.7") is short, and the engine
        // indicator now follows it. The draw path auto-measures the text and
        // draws it unclamped, so this only sizes the FPS slot before the engine
        // separator; a too-large value just opens a gap between FPS and engine.
        private const int MetricsReservedWidth = 130;
        private const int DebugMetricsReservedWidth = 560;

        private int GetMetricsReservedWidth() => _toolbarNetworkStatsEnabled ? DebugMetricsReservedWidth : MetricsReservedWidth;

        // Creates an offscreen Graphics whose DPI MATCHES the live paint surface, so
        // text measured here (for layout reservation) equals what OnPaint actually
        // renders. A default Bitmap Graphics is 96 DPI and would under-measure
        // point-size fonts at high DPI - the old root cause of the engine name being
        // clamped to "St…" at 175/200%. Caller disposes both.
        private Graphics CreateMeasureGraphics(out Bitmap bmp)
        {
            int dpi = 96;
            try { dpi = Math.Max(96, DeviceDpi); } catch { }
            bmp = new Bitmap(1, 1);
            bmp.SetResolution(dpi, dpi);
            var g = Graphics.FromImage(bmp);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            return g;
        }

        // The collapsed toolbar width is MEASUREMENT-DRIVEN with the engine name as a
        // first-class citizen: buttons + FPS slot + the actual rendered engine label
        // + the free/connection cluster, all measured with their real fonts at the
        // device DPI. Because _uiScale == 1, ScaleToDevice is identity and these
        // device-px measurements feed Size = ScaleToDevice(GetCollapsedToolbarWidth())
        // without double-scaling. Reserving the engine label's true width here is
        // what guarantees it never has to ellipsize in the collapsed bar at any DPI;
        // when the bar is later width-clamped to a small board the DRAW drops the
        // cluster's secondary text first (engine label keeps priority).
        private int GetCollapsedToolbarWidth()
        {
            int rightmostButtonX = 0;
            foreach (var button in _buttons)
            {
                if (button.Bounds.Right > rightmostButtonX)
                    rightmostButtonX = button.Bounds.Right;
            }

            const int separatorPad = 14;
            const int trailingPad = 12;
            const int engineGap = 14;
            const int enginePad = 8;     // breathing room after the engine label
            int clusterGap = ScaleToDevice(ClusterGapPx);

            int fpsSlot = ScaleTextWidth(GetMetricsReservedWidth());

            int engineLabelWidth;
            int clusterWidth;
            Bitmap? bmp = null;
            Graphics? g = null;
            try
            {
                g = CreateMeasureGraphics(out bmp);
                (string engineLabel, _) = GetEngineIndicatorLabel();
                engineLabelWidth = (int)Math.Ceiling(g.MeasureString(engineLabel, _metricsFont).Width) + ScaleToDevice(4);
                clusterWidth = MeasureActiveFreeClusterWidth(g);
            }
            catch
            {
                // Conservative fallbacks (device px) if measurement fails.
                engineLabelWidth = ScaleTextWidth(210);
                clusterWidth = ScaleToDevice(SignalBarsContentWidth) + (_isFreeLimited ? ScaleTextWidth(190) : 0);
            }
            finally
            {
                g?.Dispose();
                bmp?.Dispose();
            }

            // Scale the gap/pad constants by the text DPI factor (identity at 100%,
            // ~1.75x at 175%). They were raw logical px while the text widths beside
            // them are DPI-scaled, so at high DPI the gaps were too small and the
            // right-side cluster overlapped the engine label.
            return rightmostButtonX + ScaleTextWidth(separatorPad) + fpsSlot
                + ScaleTextWidth(engineGap) + engineLabelWidth + ScaleTextWidth(enginePad)
                + clusterGap + clusterWidth + ScaleTextWidth(trailingPad);
        }

        private void DrawButton(Graphics g, ToolbarButton button)
        {
            var rect = button.Bounds;

            if (_hoveredButton == button)
            {
                using (var path = GetRoundedRectangle(rect, 3))
                {
                    g.FillPath(_hoverBrush, path);
                }
            }

            // Draw active state for toggle buttons
            if (button.Type == ButtonType.Toggle && button.IsActive)
            {
                using (var activeBrush = new SolidBrush(Color.FromArgb(60, 100, 200, 100)))
                using (var path = GetRoundedRectangle(rect, 3))
                {
                    g.FillPath(activeBrush, path);
                }
            }

            using var textFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            // Simplified styling for W/B/W+B buttons
            if (button.IsWhiteButton || button.IsBlackButton || string.Equals(button.Text, "W+B", StringComparison.Ordinal))
            {
                if (button.IsActive)
                {
                    // Active state - green background with white text
                    using (var bgBrush = new SolidBrush(Color.FromArgb(50, 200, 50)))
                    using (var path = GetRoundedRectangle(new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), 3))
                    {
                        g.FillPath(bgBrush, path);
                    }

                    using (var textBrush = new SolidBrush(Color.White))
                    using (var boldFont = new Font(_buttonFont.FontFamily, _buttonFont.Size + 1, FontStyle.Bold))
                    {
                        g.DrawString(button.Text, boldFont, textBrush, rect, textFormat);
                    }
                }
                else
                {
                    // Inactive state - normal appearance
                    using (var textBrush = new SolidBrush(Color.FromArgb(200, 200, 200)))
                    using (var normalFont = new Font(_buttonFont.FontFamily, _buttonFont.Size, FontStyle.Regular))
                    {
                        g.DrawString(button.Text, normalFont, textBrush, rect, textFormat);
                    }
                }
            }
            else if (button.IconPaths != null)
            {
                // Icon button: render the SVG path. Active toggles get a
                // brighter color; inactive uses the standard label grey.
                string[] paths = (button.IsActive && button.IconActivePaths != null)
                    ? button.IconActivePaths
                    : button.IconPaths;
                Color iconColor = button.IsActive
                    ? Color.FromArgb(255, 255, 255)
                    : Color.FromArgb(200, 200, 200);
                try
                {
                    IconRenderer.Draw(g, paths, rect, iconColor, Dpi.Factor(this));
                }
                catch
                {
                    // Belt-and-suspenders: never let an icon issue break
                    // the toolbar paint. Empty button is preferable to
                    // a black rectangle covering the whole toolbar.
                }
            }
            else
            {
                g.DrawString(button.Text, _buttonFont, _textBrush, rect, textFormat);
            }
        }

        private void DrawExpandedContent(Graphics g)
        {
            var analysisRect = GetAnalysisSectionRect();
            var speculativeRect = GetSpeculativeSectionRect();
            var appRect = GetAppSectionRect();

            using (var panelBrush = new SolidBrush(Color.FromArgb(255, 24, 24, 27)))
            {
                // When docked, the form is wider than the visible toolbar
                // by 2*_dockedContentOffsetX (concave-overflow zones). The
                // expanded panel must respect the visible area, not paint
                // across the overflow which is supposed to be transparent.
                int panelLeft = _dockedContentOffsetX;
                var logicalRect = GetLogicalClientRectangle();
                int panelWidth = logicalRect.Width - _dockedContentOffsetX * 2;
                g.FillRectangle(panelBrush, new Rectangle(panelLeft, 0, panelWidth, GetExpandedContentHeight()));
            }

            DrawSectionCard(g, analysisRect, "Engine Analysis");
            DrawSectionCard(g, speculativeRect, "Speculative Analysis");
            DrawSectionCard(g, appRect, "App Settings");

            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop;
            int lineHeight = ExpandedRowHeight;

            DrawSlider(g, x, y, "Depth", GetInfiniteAnalysis() ? InfiniteDepthSliderValue : GetMaxDepth(), 0, GetDepthSliderMaximum());
            y += lineHeight;

            DrawSlider(g, x, y, "Threads", GetEngineThreads(), 1, BuildLimits.MaxThreads);
            y += lineHeight;

            DrawSlider(g, x, y, "Arrows / Lines", GetArrowCount(), 1, BuildLimits.MaxLines, !_coachModeEnabled);
            y += lineHeight;

            DrawSlider(g, x, y, "Coach Level", GetCoachLevel(), 1, 10, _coachModeEnabled);
            y += lineHeight;

            DrawSlider(g, x, y, "Coach Marks", GetCoachMarkCount(), 1, 3, _coachModeEnabled);
            DrawCoachCardToggle(g, x, y, _coachModeEnabled);
            y += lineHeight;

            DrawSlider(g, x, y, "Hash (MB)", GetHashSize(), 32, BuildLimits.MaxHashMb);
            y += lineHeight;

            // === Max ELO row (40px row) ===
            // Section break before the Max ELO row.
            y += SliderToEloGap;

            // Layout intent: "Max ELO" label is in the SAME column as the slider
            // labels above (Depth, Hash, etc.) so the rows visually
            // align. The checkbox is placed AFTER the label text, not before
            // it. This keeps the label column tidy and the checkbox is still
            // immediately associated with the row's setting.
            using (var rowFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            })
            using (var checkboxPen = new Pen(Color.FromArgb(160, 160, 160), 1))
            using (var mutedBrush = new SolidBrush(Color.FromArgb(140, 140, 140)))
            {
                // 1. Label drawn rect-centered so its visual center is on the
                // row midline, and at the same X as the slider labels above.
                var eloLabelRect = new Rectangle(x, y, SliderLabelWidth, lineHeight);
                g.DrawString("Max ELO", _labelFont, _textBrush, eloLabelRect, rowFmt);

                // 2. Checkbox sits right after the label text. We compute its
                // position from the rendered label width so any future label
                // change (translation, rename) doesn't break alignment.
                var checkboxRect = GetEloCheckboxRect();
                g.DrawRectangle(checkboxPen, checkboxRect);

                if (_eloLimitEnabled)
                {
                    DrawCheckboxCheckmark(g, checkboxRect, Color.FromArgb(100, 200, 100));

                    // Slider track + thumb + value drawn inline so we keep full
                    // control over vertical positioning of every element in the row.
                    int trackX = x + SliderLabelWidth;
                    int trackWidth = SliderTrackWidth;
                    int trackY = y + 20;

                    using (var trackPen = new Pen(Color.FromArgb(80, 150, 150, 150), 2))
                    {
                        g.DrawLine(trackPen, trackX, trackY, trackX + trackWidth, trackY);
                    }

                    float position = (float)(_maxEloRating - 800) / 2200f;
                    int thumbX = trackX + (int)(position * trackWidth);

                    Color thumbColor = (_isDragging && _draggingSlider == "MaxElo")
                        ? Color.FromArgb(250, 150, 200, 250)
                        : Color.FromArgb(200, 100, 150, 250);

                    using (var thumbBrush = new SolidBrush(thumbColor))
                    {
                        g.FillEllipse(thumbBrush, thumbX - 7, trackY - 7, 14, 14);
                    }

                    var valueRect = new Rectangle(
                        trackX + trackWidth + 10, y, SliderValueWidth, lineHeight);
                    using var valueFmt = new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(_maxEloRating.ToString(), _labelFont, _textBrush, valueRect, valueFmt);
                }
                else
                {
                    // "Disabled" placeholder sits IMMEDIATELY AFTER the checkbox
                    // (with a small gap), not in the slider's value column.
                    // The previous placement was too far right - and because the
                    // value column is only SliderValueWidth (54px) wide while
                    // "Disabled" renders ~60-65px, GDI+ was wrapping per-char and
                    // only the first wrapped line ("Dis") fit vertically.
                    var checkboxRectLocal = GetEloCheckboxRect();
                    int disabledX = checkboxRectLocal.Right + 10;
                    var disabledRect = new Rectangle(
                        disabledX, y, 200, lineHeight);
                    using var disabledFmt = new StringFormat
                    {
                        Alignment = StringAlignment.Near,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap,
                    };
                    g.DrawString("Disabled", _labelFont, mutedBrush, disabledRect, disabledFmt);
                }
            }

            y += lineHeight;     // advance one full row, no extra gap

            // === Engine row (40px) ===
            // Label and value share the same row vertical midline.
            if (_engineManager != null && _engineManager.CurrentEngine != null)
            {
                using var valueBrush = new SolidBrush(Color.FromArgb(240, 240, 240));
                using var rowFmt = new StringFormat
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };

                var labelRect = new Rectangle(x, y, SliderLabelWidth, lineHeight);
                g.DrawString("Engine", _labelFont, _textBrush, labelRect, rowFmt);

                // Engine name occupies the slider+value width so long names get
                // ellipsized cleanly instead of trampling the next column.
                var engineValueRect = new Rectangle(
                    x + SliderLabelWidth, y,
                    SliderTrackWidth + SliderValueWidth + 10, lineHeight);
                g.DrawString(_engineManager.CurrentEngine.DisplayName, _labelFont, valueBrush, engineValueRect, rowFmt);
            }

            y += lineHeight + 12;   // gap before the Human Engine sub-card

            if (IsHumanEngineSelected())
            {
                DrawHumanEngineSection(g);
            }

            DrawSpeculativeSection(g, speculativeRect.X + 18, speculativeRect.Y + 50, speculativeRect.Width - 36);
            DrawAppSettingsSection(g, appRect.X + 18, appRect.Y + 50, appRect.Width - 36);
        }

        private void DrawExpandedScrollbar(Graphics g)
        {
            if (!IsExpandedScrollNeeded())
                return;

            var track = GetExpandedScrollbarTrackRect();
            var thumb = GetExpandedScrollbarThumbRect();
            if (track == Rectangle.Empty || thumb == Rectangle.Empty)
                return;

            using var trackBrush = new SolidBrush(Color.FromArgb(45, 255, 255, 255));
            using var thumbBrush = new SolidBrush(_scrollbarDragging
                ? Color.FromArgb(190, 220, 220, 220)
                : Color.FromArgb(145, 190, 190, 190));

            using (var path = GetRoundedRectangle(track, 4))
            {
                g.FillPath(trackBrush, path);
            }

            using (var path = GetRoundedRectangle(thumb, 4))
            {
                g.FillPath(thumbBrush, path);
            }
        }

        private void DrawHumanEngineSection(Graphics g)
        {
            using var mutedBrush = new SolidBrush(Color.FromArgb(140, 140, 140));
            using var checkboxPen = new Pen(Color.FromArgb(160, 160, 160), 1);
            using var buttonBrush = new SolidBrush(Color.FromArgb(55, 55, 58));
            using var buttonBorderPen = new Pen(Color.FromArgb(95, 95, 100), 1);
            using var titleBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
            using var titleFont = new Font(_buttonFont.FontFamily, _buttonFont.Size, FontStyle.Bold);
            using var sectionFill = new SolidBrush(Color.FromArgb(110, 31, 31, 34));
            using var sectionBorder = new Pen(Color.FromArgb(85, 88, 90, 96), 1);
            using var rowFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            using var centerFmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            var humanRect = GetHumanSectionRect();
            using (var path = GetRoundedRectangle(humanRect, 8))
            {
                g.FillPath(sectionFill, path);
                g.DrawPath(sectionBorder, path);
            }

            // Title sits flush with card padding so it reads as the section header.
            int titleX = humanRect.X + LowerCardLeftPad;
            int contentRight = humanRect.Right - LowerCardRightPad;
            int titleWidth = contentRight - titleX;
            var titleRect = new Rectangle(titleX, humanRect.Y, titleWidth, LowerCardHeaderHeight);
            g.DrawString("Human Engine", titleFont, titleBrush, titleRect, rowFmt);

            // Content rows are INDENTED inside the card so they read as items
            // belonging to the section, not stuck to the section's left edge.
            int contentX = titleX + HumanContentIndent;
            int contentWidth = contentRight - contentX;

            // --- Adaptive row (40px) ---
            int adaptiveRowTop = humanRect.Y + LowerCardHeaderHeight;
            var adaptiveRect = GetHumanAdaptiveCheckboxRect();
            g.DrawRectangle(checkboxPen, adaptiveRect);
            if (_humanAdaptiveEnabled)
            {
                DrawCheckboxCheckmark(g, adaptiveRect, Color.FromArgb(100, 200, 100));
            }
            var adaptiveLabelRect = new Rectangle(
                adaptiveRect.Right + 10, adaptiveRowTop,
                contentRight - (adaptiveRect.Right + 10), 40);
            g.DrawString("Adaptive human mode", _labelFont, _textBrush, adaptiveLabelRect, rowFmt);

            // --- Profile row (40px): label + button immediately after ---
            int profileRowTop = adaptiveRowTop + 40;
            var profileLabelRect = new Rectangle(contentX, profileRowTop, contentWidth, 40);
            g.DrawString("Play Profile", _labelFont, _textBrush, profileLabelRect, rowFmt);

            var buttonRect = GetHumanProfileButtonRect();
            using (var path = GetRoundedRectangle(buttonRect, 4))
            {
                g.FillPath(buttonBrush, path);
                g.DrawPath(buttonBorderPen, path);
            }
            g.DrawString(_humanPlayProfile.ToString(), _labelFont, _textBrush, buttonRect, centerFmt);

            // --- Note row: small text, vertically centered in remaining space ---
            using var smallFont = new Font(_labelFont.FontFamily,
                Math.Max(8.5f, _labelFont.Size - 0.7f), FontStyle.Regular);
            int noteTop = profileRowTop + 40;
            int noteHeight = humanRect.Bottom - noteTop - 8;   // 8px breathing room
            var noteRect = new Rectangle(contentX, noteTop, contentWidth, noteHeight);
            using var noteFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("Only applies to HumanUciEngine", smallFont, mutedBrush, noteRect, noteFmt);
        }

        private void DrawSpeculativeSection(Graphics g, int x, int y, int width)
        {
            using var mutedBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
            using var outlinePen = new Pen(Color.FromArgb(160, 160, 160), 1);
            using var buttonBrush = new SolidBrush(Color.FromArgb(55, 55, 58));
            using var buttonBorderPen = new Pen(Color.FromArgb(95, 95, 100), 1);
            using var rowFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            using var centerFmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            // Content rows are INDENTED inside the card (relative to the section
            // title, which was drawn at x by the parent DrawSectionCard call) so
            // they read as items belonging to the section rather than sitting
            // flush against the section's left edge.
            int contentX = x + SpeculativeContentIndent;
            int contentWidth = width - SpeculativeContentIndent;

            // --- Enable row (40px) ---
            var checkboxRect = GetSpeculativeCheckboxRect(x, y);
            g.DrawRectangle(outlinePen, checkboxRect);
            if (_speculativeAnalysisEnabled)
            {
                DrawCheckboxCheckmark(g, checkboxRect, Color.FromArgb(100, 200, 100));
            }
            var enableLabelRect = new Rectangle(
                checkboxRect.Right + 10, y,
                contentWidth - (checkboxRect.Right - contentX) - 10, 40);
            g.DrawString("Enable background pre-cache during opponent turn",
                _labelFont, _textBrush, enableLabelRect, rowFmt);

            // --- Mode row (40px): label + button immediately after ---
            int modeRowTop = y + 40;
            var modeLabelRect = new Rectangle(contentX, modeRowTop, contentWidth, 40);
            g.DrawString("Mode", _labelFont, _textBrush, modeLabelRect, rowFmt);

            var modeButtonRect = GetSpeculativeModeButtonRect(x, y);
            using (var path = GetRoundedRectangle(modeButtonRect, 4))
            {
                g.FillPath(buttonBrush, path);
                g.DrawPath(buttonBorderPen, path);
            }
            g.DrawString(_speculativeAnalysisMode.ToString(),
                _labelFont, _textBrush, modeButtonRect, centerFmt);

            // --- Blitz row (40px): auto low-latency mode for rapid live boards ---
            int blitzRowTop = modeRowTop + 40;
            var blitzLabelRect = new Rectangle(contentX, blitzRowTop, contentWidth, 40);
            g.DrawString("Blitz Mode", _labelFont, _textBrush, blitzLabelRect, rowFmt);

            var blitzButtonRect = GetBlitzModeButtonRect(x, y);
            using (var path = GetRoundedRectangle(blitzButtonRect, 4))
            {
                g.FillPath(buttonBrush, path);
                g.DrawPath(buttonBorderPen, path);
            }
            g.DrawString(_blitzMode.ToString(),
                _labelFont, _textBrush, blitzButtonRect, centerFmt);

            // --- Bullet profile row (40px, Licensed only): trades depth for
            // MultiPV breadth so the reply cache covers ~10 opponent moves and
            // arrows serve instantly on cache hits during bullet games. Hidden
            // for Free (its prefetch cache is force-off, so the profile would
            // only lower depth). ---
            int noteTop = blitzRowTop + 40;
            if (ShowBulletProfileRow)
            {
                var bulletCheckboxRect = GetBulletProfileCheckboxRect(x, y);
                g.DrawRectangle(outlinePen, bulletCheckboxRect);
                if (_bulletProfileEnabled)
                {
                    DrawCheckboxCheckmark(g, bulletCheckboxRect, Color.FromArgb(100, 200, 100));
                }
                var bulletLabelRect = new Rectangle(
                    bulletCheckboxRect.Right + 10, blitzRowTop + 40,
                    contentWidth - (bulletCheckboxRect.Right - contentX) - 10, 40);
                g.DrawString("Bullet profile (fast arrows)",
                    _labelFont, _textBrush, bulletLabelRect, rowFmt);
                noteTop += 40;
            }

            // --- Note row: small text, vertically centered in remaining space ---
            string noteText = _speculativeAnalysisMode switch
            {
                SpeculativeAnalysisMode.Conservative => "Top 1 branch, minimal background work",
                SpeculativeAnalysisMode.Balanced => "Top 2 branches, recommended default",
                _ => "Top 3 branches, more aggressive pre-caching"
            };
            string blitzNoteText = _blitzMode switch
            {
                BlitzModeSetting.Auto => "Auto enables low-latency live-board handling during rapid move bursts",
                BlitzModeSetting.On => "Always use low-latency live-board confirmation and arrow refresh",
                _ => "Use conservative live-board confirmation and arrow refresh"
            };
            using var smallFont = new Font(_labelFont.FontFamily,
                Math.Max(8.5f, _labelFont.Size - 0.7f), FontStyle.Regular);
            var speculativeRect = GetSpeculativeSectionRect();
            int noteHeight = speculativeRect.Bottom - noteTop - 8;
            var noteRect = new Rectangle(contentX, noteTop, contentWidth, noteHeight);
            using var noteFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.Word,
            };
            g.DrawString($"{noteText}\n{blitzNoteText}", smallFont, mutedBrush, noteRect, noteFmt);
        }

        private void DrawAppSettingsSection(Graphics g, int x, int y, int width)
        {
            using var mutedBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
            using var outlinePen = new Pen(Color.FromArgb(160, 160, 160), 1);
            using var buttonBorderPen = new Pen(Color.FromArgb(95, 95, 100), 1);
            using var rowFmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            using var centerFmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };

            int contentX = x + SpeculativeContentIndent;
            int contentWidth = width - SpeculativeContentIndent;

            var taskbarRect = GetTaskbarIconCheckboxRect();
            g.DrawRectangle(outlinePen, taskbarRect);
            if (_showTaskbarIcon)
            {
                DrawCheckboxCheckmark(g, taskbarRect, Color.FromArgb(100, 200, 100));
            }

            var taskbarLabelRect = new Rectangle(taskbarRect.Right + 10, y, contentWidth - 10, 40);
            g.DrawString("Show system tray icon on launch", _labelFont, _textBrush, taskbarLabelRect, rowFmt);

            // --- Taskbar window row (40px, Licensed only): the minimized
            // taskbar access window. Hidden for Free, where the window is a
            // mandatory affordance; every row below shifts down with it. ---
            if (ShowTaskbarWindowRow)
            {
                var taskbarWindowRect = GetShowTaskbarWindowCheckboxRect();
                g.DrawRectangle(outlinePen, taskbarWindowRect);
                if (_showTaskbarWindow)
                {
                    DrawCheckboxCheckmark(g, taskbarWindowRect, Color.FromArgb(100, 200, 100));
                }

                var taskbarWindowLabelRect = new Rectangle(taskbarWindowRect.Right + 10, y + 40, contentWidth - 10, 40);
                g.DrawString("Show taskbar window", _labelFont, _textBrush, taskbarWindowLabelRect, rowFmt);
            }

            var toolbarVisibleRect = GetSettingsToolbarVisibilityCheckboxRect();
            g.DrawRectangle(outlinePen, toolbarVisibleRect);
            if (!_settingsToolbarHidden)
            {
                DrawCheckboxCheckmark(g, toolbarVisibleRect, Color.FromArgb(100, 200, 100));
            }

            var toolbarVisibleLabelRect = new Rectangle(toolbarVisibleRect.Right + 10, y + 40 + TaskbarWindowRowShift, contentWidth - 10, 40);
            g.DrawString("Keep settings bar visible", _labelFont, _textBrush, toolbarVisibleLabelRect, rowFmt);

            var networkStatsRect = GetToolbarNetworkStatsCheckboxRect();
            g.DrawRectangle(outlinePen, networkStatsRect);
            if (_toolbarNetworkStatsEnabled)
            {
                DrawCheckboxCheckmark(g, networkStatsRect, Color.FromArgb(100, 200, 100));
            }

            var networkStatsLabelRect = new Rectangle(networkStatsRect.Right + 10, y + 80 + TaskbarWindowRowShift, contentWidth - 10, 40);
            g.DrawString("Show network stats beside ping", _labelFont, _textBrush, networkStatsLabelRect, rowFmt);

            // --- Capture-exclusion row (40px, both editions): hides the arrow/
            // eval/engine-lines overlays from screen capture. Every button row
            // below shifts down by CaptureExclusionRowShift (always on). ---
            var captureExclusionRect = GetExcludeOverlaysFromCaptureCheckboxRect();
            g.DrawRectangle(outlinePen, captureExclusionRect);
            if (_excludeOverlaysFromCapture)
            {
                DrawCheckboxCheckmark(g, captureExclusionRect, Color.FromArgb(100, 200, 100));
            }

            var captureExclusionLabelRect = new Rectangle(captureExclusionRect.Right + 10, y + 120 + TaskbarWindowRowShift, contentWidth - 10, 40);
            g.DrawString("Hide overlays from screen capture", _labelFont, _textBrush, captureExclusionLabelRect, rowFmt);

            int buttonRowTop = y + 124 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            var keyButtonRect = GetKeyBindingsButtonRect();
            DrawAppActionButton(g, keyButtonRect, buttonBorderPen, _hoveredAppAction == "keys");
            g.DrawString("Key Bindings", _labelFont, _textBrush, keyButtonRect, centerFmt);

            var summaryRect = new Rectangle(keyButtonRect.Right + 14, buttonRowTop, Math.Max(80, contentX + contentWidth - keyButtonRect.Right - 14), LowerButtonHeight);
            string summary = $"Overlay {_hotkeys.ToggleOverlay}  W {_hotkeys.AnalyzeWhite}  B {_hotkeys.AnalyzeBlack}  W+B {_hotkeys.AnalyzeBoth}";
            using var smallFont = new Font(_labelFont.FontFamily, Math.Max(8.5f, _labelFont.Size - 0.7f), FontStyle.Regular);
            g.DrawString(summary, smallFont, mutedBrush, summaryRect, rowFmt);

            var evalModeButtonRect = GetEvalDisplayModeButtonRect();
            DrawAppActionButton(g, evalModeButtonRect, buttonBorderPen, _hoveredAppAction == "evalmode");
            g.DrawString("Eval Mode", _labelFont, _textBrush, evalModeButtonRect, centerFmt);

            var evalModeNoteRect = new Rectangle(evalModeButtonRect.Right + 14, evalModeButtonRect.Y, Math.Max(80, contentX + contentWidth - evalModeButtonRect.Right - 14), LowerButtonHeight);
            g.DrawString(_evalDisplayMode == EvalDisplayMode.Bar ? "Bar with scale markers" : "Notch score above board", smallFont, mutedBrush, evalModeNoteRect, rowFmt);

            var hardwareIdButtonRect = GetHardwareIdButtonRect();
            DrawAppActionButton(g, hardwareIdButtonRect, buttonBorderPen, _hoveredAppAction == "hwid");
                    g.DrawString("Show HWID", _labelFont, _textBrush, hardwareIdButtonRect, centerFmt);

            var hardwareIdNoteRect = new Rectangle(hardwareIdButtonRect.Right + 14, hardwareIdButtonRect.Y, Math.Max(80, contentX + contentWidth - hardwareIdButtonRect.Right - 14), LowerButtonHeight);
            g.DrawString("Copies the license HWID", smallFont, mutedBrush, hardwareIdNoteRect, rowFmt);

            var licenseStatusButtonRect = GetLicenseStatusButtonRect();
            DrawAppActionButton(g, licenseStatusButtonRect, buttonBorderPen, _hoveredAppAction == "license");
            g.DrawString("License Status", _labelFont, _textBrush, licenseStatusButtonRect, centerFmt);

            var licenseStatusNoteRect = new Rectangle(licenseStatusButtonRect.Right + 14, licenseStatusButtonRect.Y, Math.Max(80, contentX + contentWidth - licenseStatusButtonRect.Right - 14), LowerButtonHeight);
            g.DrawString("Checks app and engine access", smallFont, mutedBrush, licenseStatusNoteRect, rowFmt);

            var aboutButtonRect = GetAboutButtonRect();
            DrawAppActionButton(g, aboutButtonRect, buttonBorderPen, _hoveredAppAction == "about");
            g.DrawString("About", _labelFont, _textBrush, aboutButtonRect, centerFmt);

            var aboutNoteRect = new Rectangle(aboutButtonRect.Right + 14, aboutButtonRect.Y, Math.Max(80, contentX + contentWidth - aboutButtonRect.Right - 14), LowerButtonHeight);
            g.DrawString("Version and updates", smallFont, mutedBrush, aboutNoteRect, rowFmt);

            var websiteButtonRect = GetWebsiteButtonRect();
            DrawAppActionButton(g, websiteButtonRect, buttonBorderPen, _hoveredAppAction == "website");
            g.DrawString("Visit Website", _labelFont, _textBrush, websiteButtonRect, centerFmt);

            var websiteNoteRect = new Rectangle(websiteButtonRect.Right + 14, websiteButtonRect.Y, Math.Max(80, contentX + contentWidth - websiteButtonRect.Right - 14), LowerButtonHeight);
            g.DrawString("Opens chesskit.ai", smallFont, mutedBrush, websiteNoteRect, rowFmt);
        }

        private void DrawAppActionButton(Graphics g, Rectangle rect, Pen borderPen, bool hovered)
        {
            using var buttonBrush = new SolidBrush(hovered ? Color.FromArgb(74, 78, 90) : Color.FromArgb(55, 55, 58));
            using var path = GetRoundedRectangle(rect, 4);
            g.FillPath(buttonBrush, path);
            g.DrawPath(borderPen, path);
        }

        private void DrawSectionCard(Graphics g, Rectangle rect, string title)
        {
            using var fillBrush = new SolidBrush(Color.FromArgb(240, 31, 31, 34));
            using var borderPen = new Pen(Color.FromArgb(130, 95, 95, 100), 1);
            using var titleBrush = new SolidBrush(Color.FromArgb(245, 245, 245));
            using var titleFont = new Font(_buttonFont.FontFamily, _buttonFont.Size, FontStyle.Bold);

            using var path = GetRoundedRectangle(rect, 8);
            g.FillPath(fillBrush, path);
            g.DrawPath(borderPen, path);

            g.DrawString(title, titleFont, titleBrush, rect.X + 16, rect.Y + 14);
        }

        private void DrawTrimmedText(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(text, font, brush, rect, format);
        }

        private void DrawCheckboxCheckmark(Graphics g, Rectangle rect, Color color)
        {
            using var pen = new Pen(color, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            Point p1 = new Point(rect.X + 4, rect.Y + rect.Height / 2 + 1);
            Point p2 = new Point(rect.X + 8, rect.Bottom - 5);
            Point p3 = new Point(rect.Right - 4, rect.Y + 4);
            g.DrawLines(pen, new[] { p1, p2, p3 });
        }

        private Rectangle GetInfiniteAnalysisCheckboxRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop;
            int checkboxX = x + SliderLabelWidth + SliderTrackWidth + SliderValueWidth + 46;
            return new Rectangle(checkboxX, y + 11, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetCoachCardCheckboxRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop + ExpandedRowHeight * 4;
            int checkboxX = x + SliderLabelWidth + SliderTrackWidth + SliderValueWidth + 46;
            return new Rectangle(checkboxX, y + 11, LowerCheckboxSize, LowerCheckboxSize);
        }

        private void DrawInfiniteAnalysisToggle(Graphics g, int x, int y)
        {
            var checkboxRect = GetInfiniteAnalysisCheckboxRect();
            using var checkboxPen = new Pen(Color.FromArgb(160, 160, 160), 1);
            using var mutedBrush = new SolidBrush(Color.FromArgb(180, 185, 195));
            using var activeBrush = new SolidBrush(Color.FromArgb(235, 240, 250));
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            g.DrawRectangle(checkboxPen, checkboxRect);
            if (_infiniteAnalysis)
                DrawCheckboxCheckmark(g, checkboxRect, Color.FromArgb(100, 200, 100));

            var labelRect = new Rectangle(checkboxRect.Right + 8, y, 130, ExpandedRowHeight);
            g.DrawString("Infinite", _labelFont, _infiniteAnalysis ? activeBrush : mutedBrush, labelRect, fmt);
        }

        private void DrawCoachCardToggle(Graphics g, int x, int y, bool enabled)
        {
            var checkboxRect = GetCoachCardCheckboxRect();
            using var checkboxPen = new Pen(enabled ? Color.FromArgb(160, 160, 160) : Color.FromArgb(80, 120, 120, 125), 1);
            using var mutedBrush = new SolidBrush(enabled ? Color.FromArgb(180, 185, 195) : Color.FromArgb(105, 145, 145, 150));
            using var activeBrush = new SolidBrush(Color.FromArgb(235, 240, 250));
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };

            g.DrawRectangle(checkboxPen, checkboxRect);
            if (enabled && _coachCardEnabled)
                DrawCheckboxCheckmark(g, checkboxRect, Color.FromArgb(100, 200, 100));

            var labelRect = new Rectangle(checkboxRect.Right + 8, y, 130, ExpandedRowHeight);
            g.DrawString("Coach Card", _labelFont, enabled && _coachCardEnabled ? activeBrush : mutedBrush, labelRect, fmt);
        }

        private void DrawSlider(Graphics g, int x, int y, string label, int value, int min, int max, bool enabled = true)
        {
            int labelX = string.Equals(label, "Max ELO", StringComparison.Ordinal) ? x + 34 : x;
            using var disabledBrush = new SolidBrush(Color.FromArgb(105, 145, 145, 150));
            Brush labelBrush = enabled ? _textBrush : disabledBrush;
            g.DrawString(label, _labelFont, labelBrush, labelX, y + 8);

            int trackX = x + SliderLabelWidth;
            int trackWidth = SliderTrackWidth;
            int trackY = y + 20;

            using (var trackPen = new Pen(enabled ? Color.FromArgb(80, 150, 150, 150) : Color.FromArgb(45, 120, 120, 125), 2))
            {
                g.DrawLine(trackPen, trackX, trackY, trackX + trackWidth, trackY);
            }

            float position;
            if (label.Contains("Hash"))
            {
                // Special handling for hash sizes
                if (value <= 32) position = 0;
                else if (value <= 64) position = 0.167f;
                else if (value <= 128) position = 0.333f;
                else if (value <= 256) position = 0.5f;
                else if (value <= 512) position = 0.667f;
                else position = 1.0f;
            }
            else
            {
                position = max <= min ? 0f : (float)(value - min) / (max - min);
            }

            int thumbX = trackX + (int)(position * trackWidth);

            // Highlight the currently dragged slider
            Color thumbColor = !enabled
                ? Color.FromArgb(95, 120, 120, 126)
                : (_isDragging && GetSliderNameFromLabel(label) == _draggingSlider)
                ? Color.FromArgb(250, 150, 200, 250)
                : Color.FromArgb(200, 100, 150, 250);

            using (var thumbBrush = new SolidBrush(thumbColor))
            {
                g.FillEllipse(thumbBrush, thumbX - 7, trackY - 7, 14, 14);
            }

            var valueRect = new Rectangle(trackX + trackWidth + 10, y + 2, SliderValueWidth, 24);
            using var valueFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            string valueText = string.Equals(label, "Depth", StringComparison.Ordinal) && value >= InfiniteDepthSliderValue
                ? "\u221E"
                : value.ToString();
            g.DrawString(valueText, _labelFont, labelBrush, valueRect, valueFormat);
        }

        private void ShowEngineDropdownMenu(Rectangle dropdownBounds)
        {
            if (_engineManager == null) return;
            if ((DateTime.UtcNow - _engineDropdownClosedAtUtc).TotalMilliseconds < 250)
                return;

            if (_engineDropdownMenu != null)
            {
                if (_engineDropdownMenu.IsDisposed)
                {
                    _engineDropdownMenu = null;
                }
                else if (_engineDropdownMenu.Visible)
                {
                    _engineDropdownMenu.Close();
                    return;
                }
                else
                {
                    _engineDropdownMenu = null;
                }
            }

            var contextMenu = new ContextMenuStrip();
            _engineDropdownMenu = contextMenu;
            contextMenu.BackColor = Color.FromArgb(40, 40, 42);
            contextMenu.ForeColor = Color.FromArgb(220, 220, 220);
            contextMenu.ShowImageMargin = false;

            var localEngines = _engineManager.LocalEngines.ToList();
            var remoteEngines = _engineManager.RemoteEngines.ToList();
            string? currentKey = _engineManager.CurrentEngine?.Key;

            ToolStripMenuItem Dark(string text) => new(text)
            {
                BackColor = Color.FromArgb(40, 40, 42),
                ForeColor = Color.FromArgb(220, 220, 220),
            };

            void AddCategoryHeader(string text)
            {
                var header = Dark(text);
                header.Enabled = false;
                header.ForeColor = Color.FromArgb(150, 150, 155);
                header.Font = new Font(header.Font, FontStyle.Bold);
                contextMenu.Items.Add(header);
            }

            void SelectEngine(EngineInfo engine)
            {
                _selectedEngineName = engine.DisplayName;
                _engineManager.SetCurrentEngine(engine);
                SettingChanged?.Invoke("EngineChanged", engine.ExecutablePath);
                Invalidate();
            }

            void StyleCurrent(ToolStripMenuItem item, bool isCurrent)
            {
                if (isCurrent)
                {
                    item.Text = "●  " + item.Text;
                    item.ForeColor = Color.FromArgb(120, 230, 150);   // bright green = active
                    item.Font = new Font(item.Font, FontStyle.Bold);
                }
                else
                {
                    item.Text = "     " + item.Text;                   // align under the ● marker
                }
            }

            // Local engine: its own submenu carries the per-engine options
            // (select / rename / remove) - no separate management menu.
            void AddLocalEngine(EngineInfo engine)
            {
                var item = Dark(engine.DisplayName);
                StyleCurrent(item, engine.Key == currentKey);

                var use = Dark(engine.Key == currentKey ? "Use this engine (current)" : "Use this engine");
                use.Click += (s, e) => SelectEngine(engine);
                var rename = Dark("Rename…");
                rename.Click += (s, e) => RenameEngine(engine);
                var remove = Dark("Remove from list");
                remove.Click += (s, e) => { _engineManager.RemoveEngine(engine); Invalidate(); };

                item.DropDownItems.Add(use);
                item.DropDownItems.Add(new ToolStripSeparator());
                item.DropDownItems.Add(rename);
                item.DropDownItems.Add(remove);
                contextMenu.Items.Add(item);
            }

            // Remote engine: read-only, server-defined. One-click select only -
            // no rename/remove.
            void AddRemoteEngine(EngineInfo engine)
            {
                var item = Dark(engine.DisplayName);
                StyleCurrent(item, engine.Key == currentKey);
                item.Click += (s, e) => SelectEngine(engine);
                contextMenu.Items.Add(item);
            }

            if (localEngines.Count > 0)
            {
                AddCategoryHeader("Local engines");
                foreach (var engine in localEngines)
                    AddLocalEngine(engine);
            }

            if (remoteEngines.Count > 0)
            {
                if (localEngines.Count > 0)
                    contextMenu.Items.Add(new ToolStripSeparator());
                AddCategoryHeader("Remote engines (server)");
                foreach (var engine in remoteEngines)
                    AddRemoteEngine(engine);
            }

            contextMenu.Items.Add(new ToolStripSeparator());

            // List-level actions.
            var installItem = Dark("Install engine…");
            installItem.Click += (s, e) => BrowseForEngine();
            contextMenu.Items.Add(installItem);

            var resetItem = Dark("Reset / restore all local engines");
            resetItem.Click += (s, e) => { _engineManager.ResetEngines(); Invalidate(); };
            contextMenu.Items.Add(resetItem);

            // Notify Program.cs to pause board detection while the dropdown
            // is open - it overlaps the chess board area and YOLO produces
            // garbage FENs from the occluded image.
            contextMenu.Opening += (s, e) => SettingChanged?.Invoke("SoftObstructingUiActive", true);
            contextMenu.Closed += (s, e) =>
            {
                _engineDropdownClosedAtUtc = DateTime.UtcNow;
                SettingChanged?.Invoke("SoftObstructingUiActive", false);
                if (ReferenceEquals(_engineDropdownMenu, contextMenu))
                {
                    _engineDropdownMenu = null;
                }
            };

            // Convert client coordinates to screen coordinates. Use .Y (the top
            // of dropdownBounds, which is already the button's bottom edge: the
            // caller passes Rectangle(x, y + buttonHeight, ...)). Using .Bottom
            // here added the rectangle's dummy 200px height as a downward offset,
            // which (after DPI scaling) dropped the menu ~1/4 screen below.
            var screenPoint = PointToScreen(ScaleToDevice(new Point(dropdownBounds.X, dropdownBounds.Y)));
            contextMenu.Show(screenPoint);
        }

        private void RenameEngine(EngineInfo engine)
        {
            if (_engineManager == null || engine == null)
                return;

            // Modal dialog overlaps the board / steals focus - pause detection.
            SettingChanged?.Invoke("ObstructingUiActive", true);
            try
            {
                string? nickname = PromptForText(
                    "Rename engine",
                    $"Nickname for \"{engine.Name}\" (blank to clear):",
                    engine.Nickname ?? "");
                if (nickname != null) // null = cancelled
                {
                    _engineManager.SetNickname(engine, nickname);
                    Invalidate();
                }
            }
            finally
            {
                SettingChanged?.Invoke("ObstructingUiActive", false);
            }
        }

        /// <summary>Minimal modal text prompt. Returns null if cancelled.</summary>
        private string? PromptForText(string title, string label, string initial)
        {
            using var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false,
                MaximizeBox = false,
                ClientSize = new Size(360, 120),
                BackColor = Color.FromArgb(40, 40, 42),
                ForeColor = Color.FromArgb(220, 220, 220),
                TopMost = true,
            };
            var lbl = new Label { Text = label, AutoSize = false, Bounds = new Rectangle(12, 12, 336, 20), ForeColor = Color.FromArgb(220, 220, 220) };
            var box = new TextBox { Text = initial, Bounds = new Rectangle(12, 38, 336, 24), BackColor = Color.FromArgb(28, 28, 30), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Bounds = new Rectangle(180, 78, 78, 28), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(268, 78, 78, 28), FlatStyle = FlatStyle.Flat, ForeColor = Color.White };
            form.Controls.Add(lbl);
            form.Controls.Add(box);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            box.SelectAll();
            return form.ShowDialog() == DialogResult.OK ? box.Text.Trim() : null;
        }

        private void BrowseForEngine()
        {
            // Pause board detection while the file dialog (and any follow-up
            // error MessageBox) is up - these can overlap the chess board
            // and definitely steal focus / trigger the board repaints.
            SettingChanged?.Invoke("ObstructingUiActive", true);
            try
            {
                using (var openFileDialog = new OpenFileDialog())
                {
                    openFileDialog.Title = "Select UCI Chess Engine";
                    openFileDialog.Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*";
                    openFileDialog.InitialDirectory = Path.Combine(AppContext.BaseDirectory, "engines");

                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        if (_engineManager != null && _engineManager.AddCustomEngine(openFileDialog.FileName))
                        {
                            // Engine added and selected
                            if (_engineManager.CurrentEngine != null)
                            {
                                _selectedEngineName = _engineManager.CurrentEngine.Name;
                                SettingChanged?.Invoke("EngineChanged", _engineManager.CurrentEngine.ExecutablePath);
                                Invalidate();
                            }
                        }
                        else
                        {
                            MessageBox.Show("Failed to add engine. Make sure it's a valid UCI engine.",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
            }
            finally
            {
                SettingChanged?.Invoke("ObstructingUiActive", false);
            }
        }

        // Helper method to map label to slider name
        private string? GetSliderNameFromLabel(string label)
        {
            if (label.Contains("Depth")) return "MaxDepth";
            if (label.Contains("Threads")) return "Threads";
            if (label.Contains("Arrows")) return "Arrows";
            if (label.Contains("Coach Level")) return "CoachLevel";
            if (label.Contains("Coach Marks")) return "CoachMarks";
            if (label.Contains("Hash")) return "Hash";
            if (label.Contains("Max ELO")) return "MaxElo";
            return null;
        }

        private Rectangle GetSpeculativeCheckboxRect(int x, int y)
        {
            // Vertically center an 18px checkbox in the 40px enable row.
            // Checkbox X is the indented content column, not the section's edge.
            int cy = y + (40 - LowerCheckboxSize) / 2;   // = y + 11
            int cx = x + SpeculativeContentIndent;
            return new Rectangle(cx, cy, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetSpeculativeModeButtonRect(int x, int y)
        {
            // Mode row sits one full 40px row below the enable row. Button is
            // placed IMMEDIATELY AFTER the "Mode" label using the measured
            // label width so it stays correct at any DPI / font scaling.
            int rowTop = y + 40;
            int btnY = rowTop + (40 - LowerButtonHeight) / 2;
            int labelX = x + SpeculativeContentIndent;
            int btnX = labelX + _modeLabelWidth + 16;   // 16px gap after label
            return new Rectangle(btnX, btnY, LowerButtonWidth, LowerButtonHeight);
        }

        private Rectangle GetBlitzModeButtonRect(int x, int y)
        {
            int rowTop = y + 80;
            int btnY = rowTop + (40 - LowerButtonHeight) / 2;
            int labelX = x + SpeculativeContentIndent;
            int btnX = labelX + _blitzModeLabelWidth + 16;
            return new Rectangle(btnX, btnY, LowerButtonWidth, LowerButtonHeight);
        }

        // The Bullet profile row exists only for Licensed sessions: the
        // speculative prefetch/PV cache it widens is force-off for Free, so
        // the profile would only lower depth there. Runtime property (not a
        // cached bool) because the edition can flip mid-session when the
        // license verifies; every layout/draw/hit-test call re-evaluates.
        private static bool ShowBulletProfileRow => !BuildLimits.IsFreeEdition;

        private Rectangle GetBulletProfileCheckboxRect(int x, int y)
        {
            // One full 40px row below the Blitz row; same checkbox geometry
            // as the speculative enable row.
            int cy = y + 120 + (40 - LowerCheckboxSize) / 2;
            int cx = x + SpeculativeContentIndent;
            return new Rectangle(cx, cy, LowerCheckboxSize, LowerCheckboxSize);
        }

        // The "Show taskbar window" row exists only for Licensed sessions: the
        // Free Edition's taskbar window is a mandatory affordance (upsell +
        // quick actions), so Free never gets to hide it. Runtime property (not
        // a cached bool) for the same reason as ShowBulletProfileRow: the
        // edition can flip mid-session when the license verifies.
        private static bool ShowTaskbarWindowRow => !BuildLimits.IsFreeEdition;

        // Extra Y consumed by the taskbar-window row: every app-section row
        // below the tray-icon row shifts down by one 40px row when visible.
        private static int TaskbarWindowRowShift => ShowTaskbarWindowRow ? ExpandedRowHeight : 0;

        // The capture-exclusion row is shown in BOTH editions (stream-safety +
        // feedback-prevention benefit everyone), so it is an always-on 40px
        // shift applied to the button rows and the section height. It sits one
        // row below the network-stats row.
        private const int CaptureExclusionRowShift = ExpandedRowHeight;

        private Rectangle GetExcludeOverlaysFromCaptureCheckboxRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 120 + TaskbarWindowRowShift + (40 - LowerCheckboxSize) / 2;
            return new Rectangle(x, y, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetTaskbarIconCheckboxRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + (40 - LowerCheckboxSize) / 2;
            return new Rectangle(x, y, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetShowTaskbarWindowCheckboxRect()
        {
            // One full 40px row below the tray-icon row (Licensed only).
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 40 + (40 - LowerCheckboxSize) / 2;
            return new Rectangle(x, y, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetSettingsToolbarVisibilityCheckboxRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 40 + TaskbarWindowRowShift + (40 - LowerCheckboxSize) / 2;
            return new Rectangle(x, y, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetToolbarNetworkStatsCheckboxRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 80 + TaskbarWindowRowShift + (40 - LowerCheckboxSize) / 2;
            return new Rectangle(x, y, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetKeyBindingsButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 124 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private Rectangle GetEvalDisplayModeButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 164 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private Rectangle GetHardwareIdButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 204 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private Rectangle GetWebsiteButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 324 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private Rectangle GetLicenseStatusButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 244 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private Rectangle GetAboutButtonRect()
        {
            var appRect = GetAppSectionRect();
            int x = appRect.X + 18 + SpeculativeContentIndent;
            int y = appRect.Y + 50 + 284 + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(x, y, 150, LowerButtonHeight);
        }

        private int GetSpeculativeSectionStartY()
        {
            return GetSpeculativeSectionRect().Y + 50;
        }

        private Rectangle GetAnalysisSectionRect()
        {
            int panelX = 14;
            int panelY = 14;
            int panelWidth = GetLogicalClientRectangle().Width - 28;
            // Layout below the header:
            //   slider rows (Depth..Hash, with Coach rows) = AnalysisSliderRowCount * ExpandedRowHeight
            //   10px section break before Max ELO          = SliderToEloGap
            //   Max ELO row + Engine row                   = 2 * ExpandedRowHeight
            //   (if HumanEngine) 12px gap + Human sub-card
            //   18px bottom pad inside the parent card
            int analysisSectionHeight = ExpandedHeaderTop
                + (ExpandedRowHeight * AnalysisSliderRowCount)
                + SliderToEloGap
                + (ExpandedRowHeight * 2)
                + (IsHumanEngineSelected() ? (12 + HumanSectionHeight) : 0)
                + 18;
            return new Rectangle(panelX, panelY, panelWidth, analysisSectionHeight);
        }

        private Rectangle GetSpeculativeSectionRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            // The Bullet profile row adds one 40px row when visible; the note
            // block keeps its height because everything below shifts with it.
            int height = SpeculativeSectionHeight + (ShowBulletProfileRow ? ExpandedRowHeight : 0);
            return new Rectangle(analysisRect.X, analysisRect.Bottom + ExpandedSectionGap, analysisRect.Width, height);
        }

        private Rectangle GetAppSectionRect()
        {
            var speculativeRect = GetSpeculativeSectionRect();
            // The taskbar-window row adds one 40px row when visible (Licensed
            // only); the capture-exclusion row adds one always (both editions).
            // Everything below each shifts down by the same amount.
            int height = AppSectionHeight + TaskbarWindowRowShift + CaptureExclusionRowShift;
            return new Rectangle(speculativeRect.X, speculativeRect.Bottom + ExpandedSectionGap, speculativeRect.Width, height);
        }

        private Rectangle GetSliderTrackRect(int sliderIndex)
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop + ExpandedRowHeight * sliderIndex;
            int trackX = x + SliderLabelWidth;
            int trackY = y + 20;
            return new Rectangle(trackX - 12, trackY - 12, SliderTrackWidth + 24, 24);
        }

        private Rectangle GetEloCheckboxRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop + ExpandedRowHeight * AnalysisSliderRowCount + SliderToEloGap;
            // Place the checkbox AFTER the rendered "Max ELO" text, with a
            // 14px gap. We use the measured width of the label so this works
            // correctly at any DPI / font scaling, instead of guessing.
            int checkboxX = x + _maxEloLabelWidth + 14;
            // 18px checkbox vertically centered on row midline (y+20) ? top y+11.
            return new Rectangle(checkboxX, y + 11, 18, 18);
        }

        private Rectangle GetEloSliderTrackRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + ExpandedCardPadding;
            int y = analysisRect.Y + ExpandedHeaderTop + ExpandedRowHeight * AnalysisSliderRowCount + SliderToEloGap;
            int trackX = x + SliderLabelWidth;
            int trackY = y + 20;
            return new Rectangle(trackX - 12, trackY - 12, SliderTrackWidth + 24, 24);
        }

        private Rectangle GetHumanAdaptiveCheckboxRect()
        {
            var humanRect = GetHumanSectionRect();
            // Adaptive row sits directly under the title, with content INDENTED
            // from the card's left edge.
            int rowTop = humanRect.Y + LowerCardHeaderHeight;
            int cy = rowTop + (40 - LowerCheckboxSize) / 2;   // = rowTop + 11
            int cx = humanRect.X + LowerCardLeftPad + HumanContentIndent;
            return new Rectangle(cx, cy, LowerCheckboxSize, LowerCheckboxSize);
        }

        private Rectangle GetHumanProfileButtonRect()
        {
            var humanRect = GetHumanSectionRect();
            // Profile row is one full 40px row below the adaptive row.
            int rowTop = humanRect.Y + LowerCardHeaderHeight + 40;
            int btnY = rowTop + (40 - LowerButtonHeight) / 2;
            // Button sits IMMEDIATELY AFTER the "Play Profile" label rather
            // than at the far right of the card. We use the measured width of
            // the label so the gap stays correct at any DPI / font scaling.
            int labelX = humanRect.X + LowerCardLeftPad + HumanContentIndent;
            int btnX = labelX + _playProfileLabelWidth + 16;   // 16px gap after label
            return new Rectangle(btnX, btnY, LowerButtonWidth, LowerButtonHeight);
        }

        private Rectangle GetHumanSectionRect()
        {
            var analysisRect = GetAnalysisSectionRect();
            int x = analysisRect.X + 14;
            // Slider rows + section break + ELO row + Engine row + 12px gap.
            // Matches the layout used by GetAnalysisSectionRect.
            int y = analysisRect.Y
                + ExpandedHeaderTop
                + ExpandedRowHeight * AnalysisSliderRowCount
                + SliderToEloGap
                + ExpandedRowHeight * 2
                + 12;
            int width = analysisRect.Width - 28;
            int height = HumanSectionHeight;
            return new Rectangle(x, y, width, height);
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

        /// <summary>
        /// Returns a tab-shaped path with concave curves at the top corners
        /// that bridge OUTWARD into the window's top edge (matching the
        /// macOS Dynamic Island look). The path extends `topConcaveRadius`
        /// pixels outside the rect on each side at the top - those "wings"
        /// are the bridge to the window edge. Caller is responsible for
        /// making the form bounds wide enough to include the wings, OR
        /// using this path with the form's Region (which handles clipping
        /// to whatever shape we draw, including outside the visible rect).
        /// </summary>
        private GraphicsPath GetTabRectangle(Rectangle rect, int bottomRadius, int topConcaveRadius)
        {
            int tcr = Math.Min(topConcaveRadius, Math.Min(rect.Width / 4, rect.Height / 2));
            int br = Math.Min(bottomRadius, Math.Min(rect.Width / 2, rect.Height / 2));

            GraphicsPath path = new GraphicsPath();
            path.StartFigure();

            // Path goes clockwise. The "wings" at the top extend tcr pixels
            // outside the visual rect to bridge into the window's edge.
            //
            //   (X-tcr, Y) ?????????????????????? (Right+tcr, Y)
            //              ?                    ?     ? top edge (longer than rect)
            //               ?                  ?
            //                ? ? concave curves (bulge toward (X-tcr,Y) and (Right+tcr,Y))
            //               (X, Y+tcr)  (Right, Y+tcr)
            //                -                -
            //                - toolbar body   -
            //                -                -
            //                ?-(X+br, Bottom) (Right-br, Bottom)-?
            //                    (convex bottom corners)

            int leftOuter = rect.X - tcr;
            int rightOuter = rect.Right + tcr;

            // Top edge: from (leftOuter, Y) to (rightOuter, Y) - flush with
            // the window's top edge, longer than the rect by 2*tcr
            path.AddLine(leftOuter, rect.Y, rightOuter, rect.Y);

            // Top-right concave: from (rightOuter, Y) curving DOWN-LEFT to
            // (Right, Y+tcr). The concave bulge is toward (Right+tcr, Y) =
            // toward the upper-right corner of the wing area.
            path.AddBezier(
                rightOuter, rect.Y,
                rightOuter - tcr / 2, rect.Y,            // ctrl 1: along top edge toward inner
                rect.Right, rect.Y + tcr / 2,            // ctrl 2: along the right edge
                rect.Right, rect.Y + tcr);

            // Right edge
            path.AddLine(rect.Right, rect.Y + tcr, rect.Right, rect.Bottom - br);

            // Bottom-right convex
            path.AddArc(rect.Right - br * 2, rect.Bottom - br * 2, br * 2, br * 2, 0, 90);

            // Bottom edge
            path.AddLine(rect.Right - br, rect.Bottom, rect.X + br, rect.Bottom);

            // Bottom-left convex
            path.AddArc(rect.X, rect.Bottom - br * 2, br * 2, br * 2, 90, 90);

            // Left edge
            path.AddLine(rect.X, rect.Bottom - br, rect.X, rect.Y + tcr);

            // Top-left concave: from (X, Y+tcr) curving UP-LEFT to (leftOuter, Y).
            // Symmetric to top-right.
            path.AddBezier(
                rect.X, rect.Y + tcr,
                rect.X, rect.Y + tcr / 2,
                leftOuter + tcr / 2, rect.Y,
                leftOuter, rect.Y);

            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buttonToolTip?.Dispose();
                _updateTimer?.Stop();
                _updateTimer?.Dispose();
                _buttonFont?.Dispose();
                _labelFont?.Dispose();
                _metricsFont?.Dispose();
                _freeChipFont?.Dispose();
                _freeLinkFont?.Dispose();
                _textBrush?.Dispose();
                _metricsBrush?.Dispose();
                _backgroundBrush?.Dispose();
                _hoverBrush?.Dispose();
                _borderPen?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Win32 constants
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern bool EndMenu();

        const int WS_EX_NOACTIVATE = 0x08000000;
        const int LWA_COLORKEY = 0x1;
        const int LWA_ALPHA = 0x2;

        [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        // Inner classes
        private class ToolbarButton
        {
            public string Text { get; set; } = "";
            public string Tooltip { get; set; } = "";
            public Rectangle Bounds { get; set; }
            public Action? Action { get; set; }
            public ButtonType Type { get; set; } = ButtonType.Button;
            public bool IsActive { get; set; }
            public string? SettingKey { get; set; }
            public bool IsWhiteButton { get; set; } = false;
            public bool IsBlackButton { get; set; } = false;
            // SVG path data (Lucide format, 24x24 viewBox). When non-null,
            // the icon is drawn instead of Text. IconActivePaths is used when
            // the toggle is active and a different icon represents the
            // active state (e.g., chevron-up for the open menu vs hamburger
            // for the closed menu); falls back to IconPaths when null.
            public string[]? IconPaths { get; set; }
            public string[]? IconActivePaths { get; set; }
        }

        private enum ButtonType
        {
            Button,
            Toggle,
            Dropdown
        }
    }
}



