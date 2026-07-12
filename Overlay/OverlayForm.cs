using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace ChessKit
{
    public sealed class OverlayForm : Form
    {
        private readonly object _lock = new();
        private readonly List<Rectangle> _rects = new();
        private readonly List<MoveArrow> _arrows = new();
        private CoachOverlayData? _coachOverlayData = null;
        private string _lastCoachSignature = "";
        private DateTime _hideAt = DateTime.MinValue;
        private readonly WinFormsTimer _timer;
        private Rectangle? _boardRect = null;
        private string _lastArrowSignature = "";
        private string _lastArrowGeometrySignature = "";
        private int _arrowGeneration = 0;
        // Count of move-arrow shows dropped because a newer generation already
        // owns the surface. Surfaced to the app so a stuck-arrow session is
        // diagnosable from runtime.log alone (arrow-timeline flag not required).
        private static long _droppedStaleGenerationShows = 0;
        public static long DroppedStaleGenerationShows => System.Threading.Interlocked.Read(ref _droppedStaleGenerationShows);
        private DateTime _lastDepthOnlyInvalidateUtc = DateTime.MinValue;
        private bool _depthOnlyInvalidatePending = false;
        private const int DepthOnlyInvalidateIntervalMs = 110;
        // Server-driven Free watermark state. The server governs the Free limit
        // (move-count window + cooldown) and reports it; the client only displays
        // it. _freeWatermarkArmed is the "this is a Free session" guard (true while
        // the server tags the session free), replacing the old move-limit>0 guard.
        // _freeMovesRemaining is the server's remaining-moves count. _freeInCooldown
        // + _freeCooldownSeconds drive the "resets in M:SS" text and keep the
        // watermark pinned visible for the whole cooldown. A Licensed session never
        // arms any of this.
        private bool _freeWatermarkVisible = false;
        private bool _freeWatermarkArmed = false;
        private int _freeMovesRemaining = 0;
        private bool _freeInCooldown = false;
        private int _freeCooldownSeconds = 0;
        private string _lastFreeWatermarkSignature = "";

        // Pixels of padding around the board on each side. Arrow heads
        // and glow effects can extend slightly past the board edge —
        // padding gives them room to render without being clipped.
        private const int OverlayPadding = 40;

        // Flash effect fields
        private bool _isFlashing = false;
        private int _flashCount = 0;
        private int _flashMaxCount = 0;
        private Color _flashColor = Color.LawnGreen;
        private int _flashThickness = 4;
        private bool _flashVisible = true;
        private DateTime _flashToggleAt = DateTime.MinValue;

        public OverlayForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            // Start tiny and offscreen. Once arrows are shown we switch
            // to a stable screen-sized surface. That avoids layered-window
            // surface recreation while a chess window is being resized.
            Bounds = new Rectangle(-1, -1, 1, 1);

            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;

            DoubleBuffered = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);

            _timer = new WinFormsTimer { Interval = 50 };
            _timer.Tick += (s, e) =>
            {
                bool needsInvalidate = false;

                // Handle flash effect
                if (_isFlashing && DateTime.UtcNow >= _flashToggleAt)
                {
                    _flashVisible = !_flashVisible;
                    _flashToggleAt = DateTime.UtcNow.AddMilliseconds(100);
                    needsInvalidate = true;

                    if (!_flashVisible)
                    {
                        _flashCount++;
                        if (_flashCount >= _flashMaxCount)
                        {
                            _isFlashing = false;
                            lock (_lock) { _rects.Clear(); }
                            Hide();
                        }
                    }
                }

                // Handle regular hide timing
                if (!_isFlashing && _hideAt != DateTime.MinValue && DateTime.UtcNow >= _hideAt)
                {
                    bool keepFreeLimitWatermark;
                    lock (_lock)
                    {
                        keepFreeLimitWatermark = ShouldKeepFreeLimitWatermarkLocked();
                        if (_arrows.Count > 0)
                        {
                            ArrowTimeline.Log("ARROW_HIDDEN", count: _arrows.Count, reason: "display duration expired");
                        }
                        _rects.Clear();
                        _arrows.Clear();
                        _coachOverlayData = null;
                        _lastArrowSignature = "";
                        _lastArrowGeometrySignature = "";
                        _lastCoachSignature = "";
                        _freeWatermarkVisible = keepFreeLimitWatermark;
                        _hideAt = DateTime.MinValue;
                    }
                    needsInvalidate = true;
                    if (!keepFreeLimitWatermark)
                        Hide();
                }

                lock (_lock)
                {
                    if (_depthOnlyInvalidatePending &&
                        DateTime.UtcNow >= _lastDepthOnlyInvalidateUtc.AddMilliseconds(DepthOnlyInvalidateIntervalMs))
                    {
                        _depthOnlyInvalidatePending = false;
                        _lastDepthOnlyInvalidateUtc = DateTime.UtcNow;
                        needsInvalidate = true;
                    }
                }

                if (needsInvalidate) Invalidate();
            };
            _timer.Start();

            // Exclude the arrow/coach/watermark surface from screen capture:
            // prevents our own arrows from feeding back into our DXGI vision
            // capture AND keeps the overlay hidden from OBS/screen-share.
            CaptureExclusion.Register(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int ex = (int)GetWindowLong(Handle, GWL_EXSTYLE);
            ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
            SetWindowLong(Handle, GWL_EXSTYLE, (IntPtr)ex);
            uint key = (uint)ColorTranslator.ToWin32(BackColor);
            SetLayeredWindowAttributes(Handle, key, 255, LWA_COLORKEY | LWA_ALPHA);
        }

        public void ShowBoxes(IEnumerable<Rectangle> rects, int durationMs = 800)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => ShowBoxes(rects, durationMs))); return; }

            lock (_lock)
            {
                _rects.Clear();
                _rects.AddRange(rects);
                if (rects.Any())
                {
                    _boardRect = rects.First();
                }
                _hideAt = DateTime.UtcNow.AddMilliseconds(Math.Max(durationMs, 200));
                _isFlashing = false;
            }

            this.Opacity = 1.0;  // Full opacity for rectangles
            if (!Visible) Show();
            TopMost = true;
            Invalidate();
        }

        /// <summary>
        /// Updates the server-driven Free watermark state without taking over the
        /// overlay surface. While serving, the watermark rides alongside the arrows
        /// ("FREE · N moves left"); during a cooldown it stays pinned visible on its
        /// own ("Free limit reached · resets in M:SS"). Pass armed=false (the
        /// Licensed case) to clear it entirely.
        /// </summary>
        public void SetFreeWatermarkStatus(bool armed, int movesRemaining, int cooldownSeconds, bool inCooldown)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetFreeWatermarkStatus(armed, movesRemaining, cooldownSeconds, inCooldown)));
                return;
            }

            bool changed;
            lock (_lock)
            {
                _freeWatermarkArmed = armed;
                _freeMovesRemaining = Math.Max(0, movesRemaining);
                _freeInCooldown = armed && inCooldown;
                _freeCooldownSeconds = Math.Max(0, cooldownSeconds);

                bool nextVisible = ShouldShowFreeWatermarkLocked();
                string nextSignature = BuildFreeWatermarkSignature(_boardRect, nextVisible);
                changed = !string.Equals(_lastFreeWatermarkSignature, nextSignature, StringComparison.Ordinal);

                _freeWatermarkVisible = nextVisible;
                _lastFreeWatermarkSignature = nextSignature;
                if (_freeWatermarkVisible)
                    _hideAt = DateTime.MinValue;
            }

            if (_freeWatermarkVisible && !Visible)
            {
                Show();
                changed = true;
            }

            if (Visible && changed)
                Invalidate();
        }

        /// <summary>
        /// Pins the Free cooldown watermark over the given board even when no arrows
        /// are present (the cooldown pauses analysis, so there may be none). Used
        /// when the server reports an active cooldown so the notice stays put for
        /// the whole countdown.
        /// </summary>
        public void ShowFreeWatermark(Rectangle boardRect, int movesRemaining, int cooldownSeconds, bool inCooldown)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowFreeWatermark(boardRect, movesRemaining, cooldownSeconds, inCooldown)));
                return;
            }

            var transition = ApplyBoardScreenRect(boardRect);
            bool changed;
            lock (_lock)
            {
                _rects.Clear();
                _arrows.Clear();
                _coachOverlayData = null;
                _lastArrowSignature = "";
                _lastArrowGeometrySignature = "";
                _lastCoachSignature = "";
                _freeWatermarkArmed = true;
                _freeMovesRemaining = Math.Max(0, movesRemaining);
                _freeInCooldown = inCooldown;
                _freeCooldownSeconds = Math.Max(0, cooldownSeconds);
                _freeWatermarkVisible = ShouldShowFreeWatermarkLocked();

                string nextSignature = BuildFreeWatermarkSignature(_boardRect, _freeWatermarkVisible);
                changed = !string.Equals(_lastFreeWatermarkSignature, nextSignature, StringComparison.Ordinal) ||
                    transition.SurfaceChanged ||
                    transition.OldBoard != transition.NewBoard;
                _lastFreeWatermarkSignature = nextSignature;
                _hideAt = DateTime.MinValue;
                _isFlashing = false;
            }

            Opacity = 1.0;
            if (!Visible) Show();
            TopMost = true;
            if (changed)
                InvalidateBoardTransition(transition.OldBoard, transition.NewBoard, transition.SurfaceChanged);
        }

        /// <summary>
        /// Keeps the overlay's screen surface stable and updates _boardRect
        /// to the corresponding form-local rect. Resizing layered windows
        /// every frame flickers badly on Windows; changing only the local
        /// board coordinates is much calmer during browser resizes.
        /// </summary>
        private (Rectangle? OldBoard, Rectangle NewBoard, bool SurfaceChanged) ApplyBoardScreenRect(Rectangle boardScreen)
        {
            boardScreen = NormalizeBoardScreenRect(boardScreen);

            var formBounds = GetOverlayScreenBounds();
            bool surfaceChanged = Bounds != formBounds;

            if (surfaceChanged)
            {
                Bounds = formBounds;
            }

            var localBoardRect = new Rectangle(
                boardScreen.X - formBounds.X,
                boardScreen.Y - formBounds.Y,
                boardScreen.Width,
                boardScreen.Height);
            Rectangle? oldBoard;
            lock (_lock)
            {
                oldBoard = _boardRect;
                _boardRect = localBoardRect;
            }

            return (oldBoard, localBoardRect, surfaceChanged);
        }

        private static Rectangle NormalizeBoardScreenRect(Rectangle rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return rect;

            int side = Math.Min(rect.Width, rect.Height);
            int x = rect.X + ((rect.Width - side) / 2);
            int y = rect.Y + ((rect.Height - side) / 2);
            return new Rectangle(x, y, side, side);
        }

        private static Rectangle GetOverlayScreenBounds()
        {
            if (Screen.AllScreens.Length == 0)
            {
                return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1, 1);
            }

            Rectangle bounds = Screen.AllScreens[0].Bounds;
            foreach (var screen in Screen.AllScreens.Skip(1))
            {
                bounds = Rectangle.Union(bounds, screen.Bounds);
            }

            return bounds;
        }

        /// <summary>
        /// Per-frame position update from the main loop. Synchronously
        /// moves the overlay window to track the board — same mechanism
        /// the toolbar uses, instant via Win32 SetWindowPos. No repaint.
        /// Safe to call at the main loop's frame rate; cheap enough that
        /// it's fine even when arrows aren't currently displayed.
        /// </summary>
        public void SetBoardScreenPosition(Rectangle boardScreen)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetBoardScreenPosition(boardScreen)));
                return;
            }

            // Don't move the form when nothing's on it — avoids waking
            // up a hidden window's paint pipeline unnecessarily.
            if (!Visible) return;

            var transition = ApplyBoardScreenRect(boardScreen);

            // Static board + unchanged surface = the pixels are identical, so this
            // per-frame position update has nothing to repaint. Skipping the
            // Invalidate here removes a full ~60Hz GDI+ arrow repaint of the board
            // region whenever a board is being tracked but hasn't moved. Arrow/
            // watermark CONTENT changes invalidate through their own paths
            // (ShowMoveArrows / ShowFreeWatermark), so they are unaffected.
            if (!transition.SurfaceChanged && transition.OldBoard == transition.NewBoard)
                return;

            InvalidateBoardTransition(transition.OldBoard, transition.NewBoard, transition.SurfaceChanged);
        }

        /// <summary>
        /// Shows move arrows on the board with different strengths
        /// </summary>
        public void ShowMoveArrows(Rectangle boardRect, List<MoveArrow> arrows, int generation, int durationMs = 5000)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowMoveArrows(boardRect, arrows, generation, durationMs)));
                return;
            }

            string newSignature = BuildArrowSignature(arrows);
            string newGeometrySignature = BuildArrowGeometrySignature(arrows);
            bool arrowContentChanged;
            bool arrowGeometryChanged;
            bool shouldInvalidateContent = false;
            lock (_lock)
            {
                if (generation < _arrowGeneration)
                {
                    // A newer generation already owns the surface, so this show is
                    // dropped WITHOUT repainting - the exact mechanism that can
                    // freeze the previous position's arrows on screen. Count it so
                    // the app can surface drops in runtime.log even without the
                    // arrow-timeline flag; the reconciler corrects the pixels.
                    System.Threading.Interlocked.Increment(ref _droppedStaleGenerationShows);
                    ArrowTimeline.Log("ARROW_DRAW_STALE_GEN", count: arrows.Count, extra: $"gen={generation} current={_arrowGeneration}");
                    return;
                }

                _arrowGeneration = generation;
                if (_freeWatermarkArmed && _freeInCooldown)
                {
                    bool wasWatermarkVisible = _freeWatermarkVisible;
                    _rects.Clear();
                    _arrows.Clear();
                    _coachOverlayData = null;
                    _lastArrowSignature = "";
                    _lastArrowGeometrySignature = "";
                    _lastCoachSignature = "";
                    _freeWatermarkVisible = ShouldKeepFreeLimitWatermarkLocked();
                    string nextFreeSignature = BuildFreeWatermarkSignature(_boardRect, _freeWatermarkVisible);
                    arrowContentChanged = !wasWatermarkVisible ||
                        !string.Equals(_lastFreeWatermarkSignature, nextFreeSignature, StringComparison.Ordinal);
                    _lastFreeWatermarkSignature = nextFreeSignature;
                    _hideAt = DateTime.MinValue;
                    _isFlashing = false;
                    arrowGeometryChanged = false;
                    _depthOnlyInvalidatePending = false;
                }
                else
                {
                    arrowContentChanged = !string.Equals(_lastArrowSignature, newSignature, StringComparison.Ordinal);
                    arrowGeometryChanged = !string.Equals(_lastArrowGeometrySignature, newGeometrySignature, StringComparison.Ordinal);
                    if (arrowContentChanged)
                    {
                        if (arrowGeometryChanged)
                            _rects.Clear();
                        _arrows.Clear();
                        _coachOverlayData = null;
                        _arrows.AddRange(arrows);
                        _lastArrowSignature = newSignature;
                        _lastArrowGeometrySignature = newGeometrySignature;
                        _lastCoachSignature = "";

                        if (arrowGeometryChanged)
                        {
                            _depthOnlyInvalidatePending = false;
                            _lastDepthOnlyInvalidateUtc = DateTime.UtcNow;
                        }
                        else
                        {
                            DateTime now = DateTime.UtcNow;
                            if (now >= _lastDepthOnlyInvalidateUtc.AddMilliseconds(DepthOnlyInvalidateIntervalMs))
                            {
                                _lastDepthOnlyInvalidateUtc = now;
                                shouldInvalidateContent = true;
                            }
                            else
                            {
                                _depthOnlyInvalidatePending = true;
                            }
                        }
                    }
                    _freeWatermarkVisible = ShouldShowFreeWatermarkLocked();
                    _hideAt = DateTime.UtcNow.AddMilliseconds(Math.Max(durationMs, 1000));
                    _isFlashing = false;
                }
            }

            // Position the form to enclose the board (sets _boardRect to
            // form-local coords as a side effect).
            var transition = ApplyBoardScreenRect(boardRect);
            lock (_lock)
            {
                if (_freeWatermarkArmed && _freeInCooldown)
                {
                    _freeWatermarkVisible = ShouldKeepFreeLimitWatermarkLocked();
                    _lastFreeWatermarkSignature = BuildFreeWatermarkSignature(_boardRect, _freeWatermarkVisible);
                    arrowContentChanged = arrowContentChanged || transition.SurfaceChanged || transition.OldBoard != transition.NewBoard;
                }
            }

            double targetOpacity = _freeWatermarkArmed && _freeInCooldown ? 1.0 : 0.30;
            if (Math.Abs(Opacity - targetOpacity) > 0.001)
                Opacity = targetOpacity;
            if (!Visible) Show();
            if (!TopMost)
                TopMost = true;
            if (arrowGeometryChanged || transition.SurfaceChanged || transition.OldBoard != transition.NewBoard)
            {
                InvalidateBoardTransition(transition.OldBoard, transition.NewBoard, transition.SurfaceChanged);
            }
            else if (arrowContentChanged)
            {
                if (shouldInvalidateContent || (_freeWatermarkArmed && _freeInCooldown))
                    Invalidate();
            }

            // Log only real visual changes, not the 60fps same-content refresh.
            if (arrowContentChanged)
            {
                ArrowTimeline.Log(
                    "ARROW_PAINTED",
                    count: arrows.Count,
                    extra: $"gen={generation} geomChanged={arrowGeometryChanged} sig={newGeometrySignature}");
            }
        }

        public void ShowCoachOverlay(Rectangle boardRect, CoachOverlayData data, int generation, int durationMs = 5000)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowCoachOverlay(boardRect, data, generation, durationMs)));
                return;
            }

            string newSignature = BuildCoachSignature(data);
            bool contentChanged;
            lock (_lock)
            {
                if (generation < _arrowGeneration)
                {
                    // Same freeze mechanism as ShowMoveArrows: a superseded show
                    // is dropped without repainting. Count it so the coach
                    // reconciler / diagnostics see it.
                    System.Threading.Interlocked.Increment(ref _droppedStaleGenerationShows);
                    return;
                }

                _arrowGeneration = generation;
                _rects.Clear();
                _arrows.Clear();
                _lastArrowSignature = "";
                _lastArrowGeometrySignature = "";

                contentChanged = !string.Equals(_lastCoachSignature, newSignature, StringComparison.Ordinal);
                _coachOverlayData = data;
                _lastCoachSignature = newSignature;
                _freeWatermarkVisible = ShouldShowFreeWatermarkLocked();
                _hideAt = DateTime.UtcNow.AddMilliseconds(Math.Max(durationMs, 1000));
                _isFlashing = false;
                _depthOnlyInvalidatePending = false;
            }

            var transition = ApplyBoardScreenRect(boardRect);
            bool wasVisible = Visible;
            double targetOpacity = _freeWatermarkArmed && _freeInCooldown ? 1.0 : 0.78;
            if (Math.Abs(Opacity - targetOpacity) > 0.001)
                Opacity = targetOpacity;
            if (!Visible) Show();
            if (!TopMost)
                TopMost = true;

            if (!wasVisible || contentChanged || transition.SurfaceChanged || transition.OldBoard != transition.NewBoard)
                InvalidateBoardTransition(transition.OldBoard, transition.NewBoard, transition.SurfaceChanged);
        }

        public void ShowFlash(Rectangle rect, Color color, int flashCount = 2, int thickness = 6)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowFlash(rect, color, flashCount, thickness)));
                return;
            }

            // Position the form to enclose the flash target rect with
            // padding (also covers thick pen overflow). The flash rect
            // becomes form-local at (padding, padding).
            var transition = ApplyBoardScreenRect(rect);

            lock (_lock)
            {
                _rects.Clear();
                _rects.Add(new Rectangle(OverlayPadding, OverlayPadding, rect.Width, rect.Height));
                _isFlashing = true;
                _flashCount = 0;
                _flashMaxCount = flashCount;
                _flashColor = color;
                _flashThickness = thickness;
                _flashVisible = true;
                _flashToggleAt = DateTime.UtcNow.AddMilliseconds(100);
                _hideAt = DateTime.MinValue;
            }

            this.Opacity = 1.0;  // Full opacity for flash
            if (!Visible) Show();
            TopMost = true;
            InvalidateBoardTransition(transition.OldBoard, transition.NewBoard, transition.SurfaceChanged);
        }

        public void HideOverlay()
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => HideOverlay())); return; }

            lock (_lock)
            {
                _rects.Clear();
                _arrows.Clear();
                _coachOverlayData = null;
                _lastArrowSignature = "";
                _lastArrowGeometrySignature = "";
                _lastCoachSignature = "";
                _freeWatermarkVisible = false;
                _lastFreeWatermarkSignature = "";
                _isFlashing = false;
                _hideAt = DateTime.MinValue;
            }
            Hide();
        }

        public void HideArrows(int generation, bool preserveFreeLimitWatermark = true)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => HideArrows(generation, preserveFreeLimitWatermark))); return; }

            lock (_lock)
            {
                if (generation < _arrowGeneration)
                {
                    return;
                }

                if (_arrows.Count > 0)
                {
                    ArrowTimeline.Log("ARROW_HIDDEN", count: _arrows.Count, extra: $"gen={generation}");
                }

                _arrowGeneration = generation;
                _arrows.Clear();
                _coachOverlayData = null;
                _lastArrowSignature = "";
                _lastArrowGeometrySignature = "";
                _lastCoachSignature = "";
                _freeWatermarkVisible = preserveFreeLimitWatermark && ShouldKeepFreeLimitWatermarkLocked();
                _lastFreeWatermarkSignature = BuildFreeWatermarkSignature(_boardRect, _freeWatermarkVisible);
                // Only hide if we're not showing rectangles
                if (_rects.Count == 0 && !_freeWatermarkVisible)
                {
                    _hideAt = DateTime.MinValue;
                    Hide();
                }
                else
                {
                    Invalidate();
                }
            }
        }

        // Show the watermark whenever this is a Free session AND we either have
        // something to ride alongside (arrows / coach marks) OR we're in cooldown
        // (which pins it on its own). The cooldown branch is the "keep it visible
        // the whole time" case.
        private bool ShouldShowFreeWatermarkLocked()
        {
            return _freeWatermarkArmed &&
                (_freeInCooldown || _arrows.Count > 0 || _coachOverlayData != null) &&
                _boardRect.HasValue;
        }

        // The strong-visibility state: a Free session in cooldown keeps the notice
        // pinned even after arrows are cleared.
        private bool ShouldKeepFreeLimitWatermarkLocked()
        {
            return _freeWatermarkArmed &&
                _freeInCooldown &&
                _boardRect.HasValue;
        }

        private string BuildFreeWatermarkSignature(Rectangle? boardRect, bool visible)
        {
            string board = boardRect.HasValue
                ? $"{boardRect.Value.X},{boardRect.Value.Y},{boardRect.Value.Width},{boardRect.Value.Height}"
                : "none";
            return $"{visible}|{_freeInCooldown}|{_freeMovesRemaining}|{_freeCooldownSeconds}|{board}";
        }

        protected override bool ShowWithoutActivation => true;

        private static string BuildArrowSignature(IEnumerable<MoveArrow> arrows)
        {
            return string.Join("|", arrows.Select(a =>
                $"{a.FromFile},{a.FromRank}>{a.ToFile},{a.ToRank}:{a.Strength}:{a.Depth}:{a.IsFlipped}:{a.PromotionPiece}:{a.MovingSide}"));
        }

        private static string BuildArrowGeometrySignature(IEnumerable<MoveArrow> arrows)
        {
            return string.Join("|", arrows.Select(a =>
                $"{a.FromFile},{a.FromRank}>{a.ToFile},{a.ToRank}:{a.Strength}:{a.IsFlipped}:{a.PromotionPiece}:{a.MovingSide}"));
        }

        private static string BuildCoachSignature(CoachOverlayData data)
        {
            string marks = string.Join("|", data.Marks.Select(m =>
                $"{m.File},{m.Rank}:{m.Strength}:{m.Label}:{m.IsFlipped}"));
            return $"{data.ComplexityScore}:{data.Depth}:{data.TargetDepth}:{data.IsLoading}:{data.ShowPanel}:{data.Title}:{data.Detail}:{marks}";
        }

        private void InvalidateBoardTransition(Rectangle? oldBoard, Rectangle newBoard, bool surfaceChanged)
        {
            if (surfaceChanged || oldBoard == null)
            {
                Invalidate();
                return;
            }

            Rectangle dirty = Rectangle.Union(oldBoard.Value, newBoard);
            int pad = Math.Max(72, Math.Max(newBoard.Width, newBoard.Height) / 6);
            dirty.Inflate(pad, pad);
            dirty.Intersect(ClientRectangle);
            if (dirty.Width <= 0 || dirty.Height <= 0)
            {
                Invalidate();
                return;
            }

            Invalidate(dirty);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
            const int HTTRANSPARENT = -1;

            // Make the window click-through
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

            List<Rectangle> rects;
            List<MoveArrow> arrows;
            CoachOverlayData? coachOverlayData;
            bool isFlashing;
            bool flashVisible;
            Color flashColor;
            int flashThickness;
            bool freeWatermarkVisible;
            int freeMovesRemaining;
            int freeCooldownSeconds;
            bool freeInCooldown;
            bool freeWatermarkArmed;

            lock (_lock)
            {
                rects = new List<Rectangle>(_rects);
                arrows = new List<MoveArrow>(_arrows);
                coachOverlayData = _coachOverlayData;
                isFlashing = _isFlashing;
                flashVisible = _flashVisible;
                flashColor = _flashColor;
                flashThickness = _flashThickness;
                freeWatermarkVisible = _freeWatermarkVisible;
                freeMovesRemaining = _freeMovesRemaining;
                freeCooldownSeconds = _freeCooldownSeconds;
                freeInCooldown = _freeInCooldown;
                freeWatermarkArmed = _freeWatermarkArmed;
            }

            bool shouldDrawFreeWatermark =
                freeWatermarkArmed && freeWatermarkVisible && (freeInCooldown || arrows.Count > 0 || coachOverlayData != null) && _boardRect.HasValue;

            if (rects.Count == 0 && arrows.Count == 0 && coachOverlayData == null && !shouldDrawFreeWatermark) return;

            // Don't draw if we're in the "off" phase of flashing
            if (isFlashing && !flashVisible) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Draw board rectangles
            if (rects.Count > 0)
            {
                Color borderColor = isFlashing ? flashColor : Color.LawnGreen;
                // The detection-box border is a fixed pixel width unrelated to the
                // board size (unlike the arrows/coach marks, which scale from
                // squareSize). Scale it by DeviceDpi so it keeps the same physical
                // weight at 125/150/175/200%. Identity at 96 DPI.
                int thickness = isFlashing ? flashThickness : Dpi.Scale(this, 4);

                using var penShadow = new Pen(Color.FromArgb(140, 0, 0, 0), thickness + Dpi.Scale(this, 4));
                using var pen = new Pen(borderColor, thickness);

                foreach (var r in rects)
                {
                    e.Graphics.DrawRectangle(penShadow, r);
                    e.Graphics.DrawRectangle(pen, r);
                }
            }

            if (_boardRect.HasValue)
            {
                var board = _boardRect.Value;
                float squareSize = board.Width / 8f;

                // Enable high-quality rendering for arrows
                e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
                e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                if (shouldDrawFreeWatermark)
                    DrawFreeWatermark(e.Graphics, board, freeMovesRemaining, freeCooldownSeconds, freeInCooldown);

                if (coachOverlayData != null)
                {
                    DrawCoachOverlay(e.Graphics, board, squareSize, coachOverlayData);
                }

                // Draw in reverse order so best move is on top
                foreach (var arrow in arrows.OrderByDescending(a => a.Strength))
                {
                    DrawLichessStyleArrow(e.Graphics, arrow, arrows.Count, board, squareSize);
                    DrawPromotionHint(e.Graphics, arrow, board, squareSize);
                    TryDrawDepthBadge(e.Graphics, arrow, board, squareSize);
                }
            }
        }

        private static void DrawFreeWatermark(Graphics g, Rectangle board, int movesRemaining, int cooldownSeconds, bool inCooldown)
        {
            if (board.Width < 120 || board.Height < 120)
                return;

            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                // This overlay uses a magenta TransparencyKey. Antialiased translucent
                // text blends with that hidden surface and produces pink fringes.
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

                // Server-driven detail: while serving show how many moves are left in
                // the Free window; during a cooldown show the locally-ticking M:SS
                // reset countdown (it stays pinned for the whole cooldown).
                string detail = inCooldown
                    ? $"resets in {FreeTierServerState.FormatCooldown(cooldownSeconds)}"
                    : movesRemaining == 1
                        ? "1 move left"
                        : $"{movesRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture)} moves left";

                // Lead-in: an INACTIVE license (suspended/expired/revoked/unknown)
                // says WHY this is Free, distinct from an ordinary Free user who just
                // leads with "FREE". This method only runs for an armed Free session,
                // so reading the reason here is already correctly gated.
                string lead = LicenseStatusInfo.WatermarkLead(LicenseStatusInfo.Reason);
                if (string.IsNullOrEmpty(lead))
                    lead = inCooldown ? "Free limit reached" : "FREE";
                string text = $"{lead} · {detail}";
                float fontSize = Math.Clamp(board.Width * (inCooldown ? 0.043f : 0.038f), 16f, 34f);
                using var font = new Font("Segoe UI Semibold", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF textSize = g.MeasureString(text, font);

                float centerX = board.Left + (board.Width / 2f);
                float centerY = board.Top + (board.Height / 2f);
                var badge = new RectangleF(
                    centerX - (textSize.Width / 2f),
                    centerY - (textSize.Height / 2f),
                    textSize.Width,
                    textSize.Height);

                using var textBrush = new SolidBrush(Color.FromArgb(255, 4, 5, 7));
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };

                g.DrawString(text, font, textBrush, badge, format);
            }
            finally
            {
                g.Restore(state);
            }
        }

        private void DrawCoachOverlay(Graphics g, Rectangle board, float squareSize, CoachOverlayData data)
        {
            DrawCoachMarks(g, board, squareSize, data.Marks);
            if (data.ShowPanel)
                DrawCoachPanel(g, board, data);
        }

        private void DrawCoachMarks(Graphics g, Rectangle board, float squareSize, IReadOnlyList<CoachSquareMark> marks)
        {
            if (marks.Count == 0)
                return;

            foreach (var mark in marks.OrderByDescending(m => m.Strength))
            {
                float x = mark.IsFlipped
                    ? board.X + (7 - mark.File) * squareSize
                    : board.X + mark.File * squareSize;
                float y = mark.IsFlipped
                    ? board.Y + mark.Rank * squareSize
                    : board.Y + (7 - mark.Rank) * squareSize;

                float inset = Math.Clamp(squareSize * 0.13f, 6f, 14f);
                var rect = new RectangleF(x + inset, y + inset, squareSize - inset * 2f, squareSize - inset * 2f);
                Color accent = GetCoachMarkColor(mark.Strength);
                int fillAlpha = mark.Strength switch
                {
                    1 => 28,
                    2 => 22,
                    _ => 18
                };
                int borderAlpha = mark.Strength switch
                {
                    1 => 235,
                    2 => 205,
                    _ => 175
                };

                using var fill = new SolidBrush(Color.FromArgb(fillAlpha, accent));
                using var border = new Pen(Color.FromArgb(borderAlpha, accent), Math.Clamp(squareSize * 0.038f, 2.0f, 4.2f));
                using var path = CreateRoundedRect(rect, Math.Clamp(squareSize * 0.08f, 4f, 9f));
                g.FillPath(fill, path);
                g.DrawPath(border, path);

                string label = string.IsNullOrWhiteSpace(mark.Label) ? mark.Strength.ToString(System.Globalization.CultureInfo.InvariantCulture) : mark.Label;
                float badgeSize = Math.Clamp(squareSize * 0.26f, 17f, 28f);
                var badgeRect = new RectangleF(rect.Right - badgeSize * 0.84f, rect.Top - badgeSize * 0.16f, badgeSize, badgeSize);
                using var badgeFill = new SolidBrush(Color.FromArgb(230, 18, 22, 30));
                using var badgeBorder = new Pen(Color.FromArgb(220, accent), Math.Max(1.4f, squareSize * 0.018f));
                using var textBrush = new SolidBrush(Color.FromArgb(245, 248, 250));
                using var font = new Font("Segoe UI Semibold", Math.Clamp(squareSize * 0.12f, 8.5f, 13.5f), FontStyle.Bold, GraphicsUnit.Pixel);
                using var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap
                };

                g.FillEllipse(badgeFill, badgeRect);
                g.DrawEllipse(badgeBorder, badgeRect);
                g.DrawString(label, font, textBrush, badgeRect, fmt);
            }
        }

        private void DrawCoachPanel(Graphics g, Rectangle board, CoachOverlayData data)
        {
            if (board.Width < 160 || board.Height < 160)
                return;

            float panelWidth = Math.Clamp(board.Width * 0.42f, 300f, 460f);
            float panelHeight = Math.Clamp(board.Height * 0.14f, 100f, 132f);
            float margin = Math.Clamp(board.Width * 0.018f, 10f, 18f);
            var panel = GetCoachPanelRect(board, panelWidth, panelHeight, margin);

            using var shadowPath = CreateRoundedRect(new RectangleF(panel.X + 2f, panel.Y + 2f, panel.Width, panel.Height), 8f);
            using var shadowFill = new SolidBrush(Color.FromArgb(72, 0, 0, 0));
            g.FillPath(shadowFill, shadowPath);

            using var panelPath = CreateRoundedRect(panel, 8f);
            using var panelFill = new SolidBrush(Color.FromArgb(232, 20, 24, 32));
            using var panelBorder = new Pen(Color.FromArgb(145, 210, 220, 232), 1f);
            g.FillPath(panelFill, panelPath);
            g.DrawPath(panelBorder, panelPath);

            string title = string.IsNullOrWhiteSpace(data.Title)
                ? (data.IsLoading ? "Coach Thinking" : "Coach")
                : data.Title;
            string detail = string.IsNullOrWhiteSpace(data.Detail) ? "Position focus" : data.Detail;
            if (data.IsLoading && string.IsNullOrWhiteSpace(data.Detail))
                detail = "Waiting for stable depth";
            if (data.TargetDepth > 0)
                detail = $"{detail}  depth {Math.Max(0, data.Depth)}/{data.TargetDepth}";
            else if (data.Depth > 0)
                detail = $"{detail}  depth {data.Depth}";
            using var titleFont = new Font("Segoe UI Semibold", Math.Clamp(board.Width * 0.019f, 13f, 18f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var detailFont = new Font("Segoe UI", Math.Clamp(board.Width * 0.0155f, 11f, 15f), FontStyle.Regular, GraphicsUnit.Pixel);
            using var titleBrush = new SolidBrush(Color.FromArgb(245, 250, 252));
            using var mutedBrush = new SolidBrush(Color.FromArgb(190, 214, 222, 232));
            using var valueBrush = new SolidBrush(Color.FromArgb(255, 255, 210, 112));
            using var noWrap = new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };

            float pad = Math.Clamp(panel.Width * 0.065f, 16f, 24f);
            float titleHeight = Math.Clamp(panel.Height * 0.28f, 28f, 36f);
            float detailHeight = Math.Clamp(panel.Height * 0.24f, 22f, 30f);
            var titleRect = new RectangleF(panel.X + pad, panel.Y + 10f, panel.Width - pad * 2f, titleHeight);
            g.DrawString(title, titleFont, titleBrush, titleRect, noWrap);

            string value = data.IsLoading && data.TargetDepth > 0
                ? $"{Math.Clamp(data.Depth, 0, data.TargetDepth)}/{data.TargetDepth}"
                : data.ComplexityScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
            SizeF valueSize = g.MeasureString(value, titleFont);
            var valueRect = new RectangleF(panel.Right - pad - valueSize.Width - 6f, panel.Y + 10f, valueSize.Width + 6f, titleHeight);
            g.DrawString(value, titleFont, valueBrush, valueRect, noWrap);

            var detailRect = new RectangleF(panel.X + pad, titleRect.Bottom + 2f, panel.Width - pad * 2f, detailHeight);
            g.DrawString(detail, detailFont, mutedBrush, detailRect, noWrap);

            float trackX = panel.X + pad;
            float trackY = panel.Bottom - Math.Clamp(panel.Height * 0.24f, 24f, 32f);
            float trackW = panel.Width - pad * 2f;
            float trackH = Math.Clamp(panel.Height * 0.105f, 8f, 12f);
            using var trackBack = new SolidBrush(Color.FromArgb(150, 48, 54, 66));
            using var trackPath = CreateRoundedRect(new RectangleF(trackX, trackY, trackW, trackH), trackH / 2f);
            g.FillPath(trackBack, trackPath);

            float fillRatio = data.IsLoading && data.TargetDepth > 0
                ? Math.Clamp(Math.Max(0, data.Depth) / (float)data.TargetDepth, 0f, 1f)
                : Math.Clamp(data.ComplexityScore, 0, 100) / 100f;
            float fillW = Math.Clamp(trackW * fillRatio, 0f, trackW);
            if (fillW > 0.5f)
            {
                using var fillBrush = new LinearGradientBrush(
                    new RectangleF(trackX, trackY, Math.Max(fillW, 1f), trackH),
                    Color.FromArgb(255, 71, 194, 152),
                    Color.FromArgb(255, 255, 196, 87),
                    LinearGradientMode.Horizontal);
                using var fillPath = CreateRoundedRect(new RectangleF(trackX, trackY, fillW, trackH), trackH / 2f);
                g.FillPath(fillBrush, fillPath);
            }
        }

        private static Color GetCoachMarkColor(int strength)
        {
            return strength switch
            {
                1 => Color.FromArgb(255, 236, 177, 73),
                2 => Color.FromArgb(255, 72, 190, 196),
                _ => Color.FromArgb(255, 171, 137, 255)
            };
        }

        private RectangleF GetCoachPanelRect(Rectangle board, float panelWidth, float panelHeight, float margin)
        {
            Rectangle client = ClientRectangle;

            if (board.Bottom + margin + panelHeight <= client.Bottom)
            {
                return new RectangleF(board.Left + margin, board.Bottom + margin, panelWidth, panelHeight);
            }

            if (board.Top - margin - panelHeight >= client.Top)
            {
                return new RectangleF(board.Left + margin, board.Top - margin - panelHeight, panelWidth, panelHeight);
            }

            if (board.Right + margin + panelWidth <= client.Right)
            {
                return new RectangleF(board.Right + margin, board.Top + margin, panelWidth, panelHeight);
            }

            if (board.Left - margin - panelWidth >= client.Left)
            {
                return new RectangleF(board.Left - margin - panelWidth, board.Top + margin, panelWidth, panelHeight);
            }

            return new RectangleF(board.Left + margin, board.Top + margin, panelWidth, panelHeight);
        }

        private void DrawLichessStyleArrow(
            Graphics g,
            MoveArrow arrow,
            int totalArrowCount,
            Rectangle board,
            float squareSize)
        {
            float fromX, fromY, toX, toY;

            if (arrow.IsFlipped)
            {
                fromX = board.X + (7 - arrow.FromFile + 0.5f) * squareSize;
                fromY = board.Y + (arrow.FromRank + 0.5f) * squareSize;
                toX = board.X + (7 - arrow.ToFile + 0.5f) * squareSize;
                toY = board.Y + (arrow.ToRank + 0.5f) * squareSize;
            }
            else
            {
                fromX = board.X + (arrow.FromFile + 0.5f) * squareSize;
                fromY = board.Y + (7 - arrow.FromRank + 0.5f) * squareSize;
                toX = board.X + (arrow.ToFile + 0.5f) * squareSize;
                toY = board.Y + (7 - arrow.ToRank + 0.5f) * squareSize;
            }

            float dx = toX - fromX;
            float dy = toY - fromY;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) return;

            float unitX = dx / length;
            float unitY = dy / length;

            // Keep the shaft starting/ending inside the source/target squares.
            float startOffset = squareSize * 0.16f;
            float endOffset = squareSize * 0.12f;

            fromX += unitX * startOffset;
            fromY += unitY * startOffset;
            toX -= unitX * endOffset;
            toY -= unitY * endOffset;

            // Scale directly from the current square size so arrows stay visually
            // consistent as the board is resized.
            // Use a compressed growth curve so large boards do not blow up the arrows.
            float normalizedSquare = Math.Clamp(squareSize / 80f, 0.70f, 1.30f);
            float compressedScale = (float)Math.Pow(normalizedSquare, 0.55f);
            float widthScale = Math.Clamp(compressedScale, 0.66f, 1.12f);
            float lengthScale = Math.Clamp(compressedScale * 0.99f, 0.62f, 1.06f);
            float smallBoardScale = Math.Clamp((squareSize - 44f) / 44f, 0.0f, 1.0f);
            float primaryHeadDamping = 0.74f + (smallBoardScale * 0.26f);
            float primaryThicknessDamping = 0.84f + (smallBoardScale * 0.16f);

            // Rank 1 uses the strongest style and the final displayed rank uses
            // the weakest. Every rank between them is evenly interpolated. The old
            // hard-coded 1/2/3/default buckets made ranks 2 and 3 cluster together
            // visually, especially once shaft and arrow-head sizes compounded.
            float prominence = ArrowRankScale.GetProminence(arrow.Strength, totalArrowCount);
            float strongestThickness = Math.Clamp(
                squareSize * 0.218f * primaryThicknessDamping,
                3.8f,
                15.6f);
            float weakestThickness = Math.Clamp(squareSize * 0.086f, 1.8f, 5.8f);

            float thickness = ArrowRankScale.Lerp(weakestThickness, strongestThickness, prominence);
            // AdjustableArrowCap dimensions are scaled by Pen.Width. Interpolate
            // the final on-screen head dimensions, then convert back to cap units;
            // interpolating both independently would reintroduce a compounded curve.
            float weakestHeadWidthPixels = weakestThickness * 2.5f * widthScale;
            float strongestHeadWidthPixels = strongestThickness * 4.75f * widthScale * primaryHeadDamping;
            float weakestHeadLengthPixels = weakestThickness * 2.25f * lengthScale;
            float strongestHeadLengthPixels = strongestThickness * 4.25f * lengthScale * primaryHeadDamping;
            float headWidth = ArrowRankScale.Lerp(
                weakestHeadWidthPixels,
                strongestHeadWidthPixels,
                prominence) / thickness;
            float headLength = ArrowRankScale.Lerp(
                weakestHeadLengthPixels,
                strongestHeadLengthPixels,
                prominence) / thickness;
            Color arrowColor = Color.FromArgb(255, 38, 84, 132);

            using (var pen = new Pen(arrowColor, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Custom;

                using (var cap = new AdjustableArrowCap(headWidth, headLength, true))
                {
                    cap.MiddleInset = 0;
                    cap.WidthScale = 1f;
                    cap.BaseInset = 0;
                    pen.CustomEndCap = cap;

                    var state = g.Save();
                    try
                    {
                        g.SetClip(board, CombineMode.Intersect);
                        g.DrawLine(pen, fromX, fromY, toX, toY);
                    }
                    finally
                    {
                        g.Restore(state);
                    }
                }
            }
        }

        private void TryDrawDepthBadge(Graphics g, MoveArrow arrow, Rectangle board, float squareSize)
        {
            try
            {
                DrawDepthBadge(g, arrow, board, squareSize);
            }
            catch
            {
                // Depth labels are diagnostic only. Never let a badge drawing
                // issue suppress the actual move arrows.
            }
        }

        private void DrawDepthBadge(Graphics g, MoveArrow arrow, Rectangle board, float squareSize)
        {
            if (arrow.Strength != 1 || arrow.Depth <= 0)
                return;

            float fromX = arrow.IsFlipped
                ? board.X + (7 - arrow.FromFile + 0.5f) * squareSize
                : board.X + (arrow.FromFile + 0.5f) * squareSize;
            float fromY = arrow.IsFlipped
                ? board.Y + (arrow.FromRank + 0.5f) * squareSize
                : board.Y + (7 - arrow.FromRank + 0.5f) * squareSize;
            float toX = arrow.IsFlipped
                ? board.X + (7 - arrow.ToFile + 0.5f) * squareSize
                : board.X + (arrow.ToFile + 0.5f) * squareSize;
            float toY = arrow.IsFlipped
                ? board.Y + (arrow.ToRank + 0.5f) * squareSize
                : board.Y + (7 - arrow.ToRank + 0.5f) * squareSize;

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
            labelX = Math.Clamp(labelX, board.Left + 2f, board.Right - textSize.Width - 2f);
            labelY = Math.Clamp(labelY, board.Top + 2f, board.Bottom - textSize.Height - 2f);

            var textPoint = new PointF(labelX, labelY);
            using var edgeBrush = new SolidBrush(Color.FromArgb(120, 225, 225, 225));
            using var brush = new SolidBrush(Color.FromArgb(10, 10, 10));
            g.DrawString(text, font, edgeBrush, new PointF(labelX + 1.0f, labelY + 1.0f));
            g.DrawString(text, font, edgeBrush, new PointF(labelX - 0.8f, labelY - 0.8f));
            g.DrawString(text, font, brush, textPoint);
        }

        private void DrawPromotionHint(Graphics g, MoveArrow arrow, Rectangle board, float squareSize)
        {
            if (arrow.PromotionPiece == '\0')
                return;

            string pieceText = GetPromotionGlyph(arrow.PromotionPiece, arrow.MovingSide);

            float centerX = arrow.IsFlipped
                ? board.X + (7 - arrow.ToFile + 0.5f) * squareSize
                : board.X + (arrow.ToFile + 0.5f) * squareSize;
            float centerY = arrow.IsFlipped
                ? board.Y + (arrow.ToRank + 0.5f) * squareSize
                : board.Y + (7 - arrow.ToRank + 0.5f) * squareSize;

            float boxSize = Math.Clamp(squareSize * 0.96f, 42f, 68f);
            float margin = Math.Clamp(squareSize * 0.14f, 8f, 16f);
            float minX = board.Left + 4f;
            float maxX = board.Right - boxSize - 4f;
            float boxX = Math.Clamp(centerX - (boxSize / 2f), minX, maxX);
            bool topHalf = centerY < board.Top + (board.Height / 2f);
            float preferredBoxY = topHalf
                ? board.Top - boxSize - margin
                : board.Bottom + margin;
            float screenTop = Top + 8f;
            float screenBottom = Bottom - boxSize - 8f;
            float boxY = Math.Clamp(preferredBoxY, screenTop, screenBottom);

            var rect = new RectangleF(boxX, boxY, boxSize, boxSize);
            using GraphicsPath path = CreateRoundedRect(rect, MathF.Min(12f, boxSize * 0.24f));
            using var fill = new SolidBrush(Color.FromArgb(246, 22, 27, 36));
            using var glow = new SolidBrush(Color.FromArgb(40, 110, 170, 255));
            using var border = new Pen(Color.FromArgb(255, 108, 162, 255), Math.Max(2.0f, boxSize * 0.05f));
            using var textBrush = new SolidBrush(Color.FromArgb(255, 248, 249, 252));
            using var font = new Font("Segoe UI Symbol", Math.Max(24f, boxSize * 0.62f), GraphicsUnit.Pixel);
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
            var path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Win32
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x80000;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int LWA_ALPHA = 0x2;
        const int LWA_COLORKEY = 0x1;

        [DllImport("user32.dll")] static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    }

    public class MoveArrow
    {
        public int FromFile { get; set; } // 0-7 (a-h)
        public int FromRank { get; set; } // 0-7 (1-8)
        public int ToFile { get; set; }
        public int ToRank { get; set; }
        public int Strength { get; set; } // 1=best; larger values are lower-ranked moves
        public bool IsFlipped { get; set; } // For black perspective
        public char PromotionPiece { get; set; }
        public char MovingSide { get; set; }
        public int Depth { get; set; }
    }

    internal static class ArrowRankScale
    {
        internal static float GetProminence(int strength, int totalArrowCount)
        {
            if (totalArrowCount <= 1)
                return 1f;

            int rankIndex = Math.Clamp(strength - 1, 0, totalArrowCount - 1);
            return 1f - (rankIndex / (float)(totalArrowCount - 1));
        }

        internal static float Lerp(float weakest, float strongest, float prominence)
        {
            return weakest + ((strongest - weakest) * Math.Clamp(prominence, 0f, 1f));
        }
    }

    public sealed class CoachSquareMark
    {
        public int File { get; set; }
        public int Rank { get; set; }
        public int Strength { get; set; } = 1;
        public bool IsFlipped { get; set; }
        public string Label { get; set; } = "";
    }

    public sealed class CoachOverlayData
    {
        public int ComplexityScore { get; set; }
        public string Title { get; set; } = "Coach";
        public string Detail { get; set; } = "Position focus";
        public int Depth { get; set; }
        public int TargetDepth { get; set; }
        public bool IsLoading { get; set; }
        public bool ShowPanel { get; set; } = true;
        public List<CoachSquareMark> Marks { get; set; } = new();
    }

    internal sealed class DepthBadgeOverlayForm : Form
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WM_NCHITTEST = 0x84;
        private const int HTTRANSPARENT = -1;

        private readonly object _lock = new();
        private readonly List<MoveArrow> _arrows = new();
        private readonly int _padding;
        private readonly WinFormsTimer _timer;
        private Rectangle? _boardRect;
        private DateTime _hideAt = DateTime.MinValue;

        public DepthBadgeOverlayForm(int padding)
        {
            _padding = padding;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-1, -1, 1, 1);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Opacity = 1.0;
            DoubleBuffered = true;

            _timer = new WinFormsTimer { Interval = 75 };
            _timer.Tick += (_, _) =>
            {
                if (_hideAt != DateTime.MinValue && DateTime.UtcNow >= _hideAt)
                {
                    HideOverlay();
                }
            };
            _timer.Start();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                m.Result = (IntPtr)HTTRANSPARENT;
                return;
            }

            base.WndProc(ref m);
        }

        public void ShowDepthBadges(Rectangle boardScreen, IEnumerable<MoveArrow> arrows, int durationMs)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ShowDepthBadges(boardScreen, arrows, durationMs)));
                return;
            }

            var badges = arrows
                .Where(a => a.Strength == 1 && a.Depth > 0)
                .ToList();

            if (badges.Count == 0)
            {
                HideOverlay();
                return;
            }

            ApplyBoardScreenRect(boardScreen);
            lock (_lock)
            {
                _arrows.Clear();
                _arrows.AddRange(badges);
                _hideAt = DateTime.UtcNow.AddMilliseconds(Math.Max(durationMs, 1000));
            }

            if (!Visible) Show();
            TopMost = true;
            Invalidate();
        }

        public void SetBoardScreenPosition(Rectangle boardScreen)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetBoardScreenPosition(boardScreen)));
                return;
            }

            if (!Visible) return;
            ApplyBoardScreenRect(boardScreen);
        }

        public void HideOverlay()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(HideOverlay));
                return;
            }

            lock (_lock)
            {
                _arrows.Clear();
                _hideAt = DateTime.MinValue;
            }

            Hide();
        }

        private void ApplyBoardScreenRect(Rectangle boardScreen)
        {
            var formBounds = new Rectangle(
                boardScreen.X - _padding,
                boardScreen.Y - _padding,
                boardScreen.Width + (_padding * 2),
                boardScreen.Height + (_padding * 2));

            if (Bounds != formBounds)
            {
                Bounds = formBounds;
            }

            lock (_lock)
            {
                _boardRect = new Rectangle(_padding, _padding, boardScreen.Width, boardScreen.Height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            List<MoveArrow> arrows;
            Rectangle? board;
            lock (_lock)
            {
                arrows = new List<MoveArrow>(_arrows);
                board = _boardRect;
            }

            if (arrows.Count == 0 || board == null)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            float squareSize = board.Value.Width / 8f;
            foreach (var arrow in arrows)
            {
                DrawDepthBadge(e.Graphics, arrow, board.Value, squareSize);
            }
        }

        private static void DrawDepthBadge(Graphics g, MoveArrow arrow, Rectangle board, float squareSize)
        {
            float fromX = arrow.IsFlipped
                ? board.X + (7 - arrow.FromFile + 0.5f) * squareSize
                : board.X + (arrow.FromFile + 0.5f) * squareSize;
            float fromY = arrow.IsFlipped
                ? board.Y + (arrow.FromRank + 0.5f) * squareSize
                : board.Y + (7 - arrow.FromRank + 0.5f) * squareSize;
            float toX = arrow.IsFlipped
                ? board.X + (7 - arrow.ToFile + 0.5f) * squareSize
                : board.X + (arrow.ToFile + 0.5f) * squareSize;
            float toY = arrow.IsFlipped
                ? board.Y + (arrow.ToRank + 0.5f) * squareSize
                : board.Y + (7 - arrow.ToRank + 0.5f) * squareSize;

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
            labelX = Math.Clamp(labelX, board.Left + 2f, board.Right - textSize.Width - 2f);
            labelY = Math.Clamp(labelY, board.Top + 2f, board.Bottom - textSize.Height - 2f);

            using var edgeBrush = new SolidBrush(Color.FromArgb(210, 235, 235, 235));
            using var brush = new SolidBrush(Color.FromArgb(8, 8, 8));
            g.DrawString(text, font, edgeBrush, new PointF(labelX + 1.0f, labelY + 1.0f));
            g.DrawString(text, font, edgeBrush, new PointF(labelX - 0.8f, labelY - 0.8f));
            g.DrawString(text, font, brush, new PointF(labelX, labelY));
        }
    }
}
