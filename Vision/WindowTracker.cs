using System.Runtime.InteropServices;

namespace ChessKit
{
    /// <summary>
    /// Tracks the chess board's parent Win32 window. After vision finds the
    /// board on the screen, we resolve which top-level window contains it
    /// and cache (a) the HWND, (b) the board's offset relative to the
    /// window's top-left. From then on, polling GetWindowRect on each
    /// frame gives us pixel-perfect tracking with zero vision overhead —
    /// orders of magnitude faster than re-running the YOLO detector.
    ///
    /// Vision detection still runs:
    ///   - Initially, to find the board on the screen
    ///   - When the window resizes (board offset may have changed)
    ///   - Periodically as a verification (board could have scrolled or
    ///     the window content could have reflowed)
    ///   - When the cached HWND becomes invalid (window closed/minimized)
    ///
    /// State transitions are driven from the main loop in Program.cs;
    /// this class is mostly stateless helpers + Win32 wrappers.
    /// </summary>
    internal static class WindowTracker
    {
        // ===== Win32 P/Invoke =====

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr _GetForegroundWindow();

        public static IntPtr GetForegroundWindow() => _GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        private static extern uint GetTickCount();

        /// <summary>
        /// Returns the system tick count (ms since boot) of the last
        /// user input event (mouse move/click, key press) anywhere on
        /// the system. Wraps around every ~49 days. Compare via signed
        /// subtraction to handle wrap-around correctly.
        /// </summary>
        public static uint GetLastUserInputTick()
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return 0;
            return lii.dwTime;
        }

        /// <summary>
        /// Current system tick count (ms since boot). Same clock as
        /// GetLastUserInputTick — use for delta comparisons.
        /// </summary>
        public static uint GetSystemTick() => GetTickCount();

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        private static extern bool _IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        private static extern bool _IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        private static extern bool _IsWindowVisible(IntPtr hWnd);

        // Public wrappers for direct polling — used by the lost-tracking
        // recovery path to check the cached HWND's state without going
        // through IsTrackable (which combines them and thus loses info).
        public static bool IsWindow(IntPtr h) => _IsWindow(h);
        public static bool IsIconic(IntPtr h) => _IsIconic(h);
        public static bool IsWindowVisible(IntPtr h) => _IsWindowVisible(h);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        // GetAncestor flag for "topmost owner window" — climbs past child
        // controls (Chrome's tab content area, browser viewport, etc.) to
        // the actual draggable window.
        private const uint GA_ROOT = 2;

        /// <summary>
        /// Returns true if the chess board (as positioned at boardRect)
        /// is occluded by another window from above. Uses WindowFromPoint
        /// at the board's center: if the topmost window there is NOT the
        /// tracked window (or a child of it), the board is covered.
        ///
        /// We sample 5 points across the board (center + 4 mid-quadrants)
        /// for robustness against thin overlays or partial occlusions.
        /// "Obscured" requires 3+ samples to fail — so a small overlay
        /// covering only one corner doesn't trigger it, but a window
        /// covering most of the board does.
        ///
        /// IMPORTANT: This relies on our own overlay being click-through
        /// (WS_EX_TRANSPARENT) so WindowFromPoint passes through it.
        /// Otherwise the overlay itself would always be detected as
        /// obscuring the board.
        /// </summary>
        public static bool IsBoardObscured(IntPtr trackedHwnd, OpenCvSharp.Rect boardRect)
        {
            if (trackedHwnd == IntPtr.Zero) return false;

            int cx = boardRect.X + boardRect.Width / 2;
            int cy = boardRect.Y + boardRect.Height / 2;
            int qx = boardRect.Width / 4;
            int qy = boardRect.Height / 4;

            POINT[] samples = new[]
            {
                new POINT(cx, cy),                  // center
                new POINT(cx - qx, cy - qy),        // top-left quadrant
                new POINT(cx + qx, cy - qy),        // top-right quadrant
                new POINT(cx - qx, cy + qy),        // bottom-left quadrant
                new POINT(cx + qx, cy + qy),        // bottom-right quadrant
            };

            int obscured = 0;
            foreach (var pt in samples)
            {
                IntPtr h = WindowFromPoint(pt);
                if (h == IntPtr.Zero) { obscured++; continue; }
                IntPtr top = GetAncestor(h, GA_ROOT);
                if (top != trackedHwnd) obscured++;
            }
            // 3+ of 5 samples obscured = considered covered.
            return obscured >= 3;
        }

        // ===== Public API =====

        /// <summary>
        /// Resolve the top-level window that contains the given board rect.
        /// Returns IntPtr.Zero if no suitable window is found. Caller should
        /// fall back to vision-only tracking if zero is returned.
        /// </summary>
        public static IntPtr ResolveTopLevelWindow(OpenCvSharp.Rect boardRect)
        {
            // Probe the window at the board's center. Center is more robust
            // than corners when the board has anti-aliased edges or sits
            // near a window border.
            var center = new POINT(
                boardRect.X + boardRect.Width / 2,
                boardRect.Y + boardRect.Height / 2);

            IntPtr hwnd = WindowFromPoint(center);
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;

            IntPtr top = GetAncestor(hwnd, GA_ROOT);
            if (top == IntPtr.Zero || !IsWindow(top)) return IntPtr.Zero;

            // Sanity check: the window should actually contain the board.
            // If not, WindowFromPoint may have caught a stale frame mid-
            // animation; bail rather than tracking the wrong window.
            if (!GetWindowRect(top, out RECT rect)) return IntPtr.Zero;
            if (boardRect.X < rect.Left || boardRect.Y < rect.Top ||
                boardRect.X + boardRect.Width > rect.Right ||
                boardRect.Y + boardRect.Height > rect.Bottom)
            {
                return IntPtr.Zero;
            }

            return top;
        }

        /// <summary>
        /// Returns true if the cached HWND is still alive and visible.
        /// False means we should drop tracking and resume full-screen
        /// vision search.
        /// </summary>
        public static bool IsTrackable(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (!IsWindow(hwnd)) return false;
            if (IsIconic(hwnd)) return false;          // minimized
            if (!IsWindowVisible(hwnd)) return false;  // hidden / occluded by alt-tab etc.
            if (IsCloaked(hwnd)) return false;         // other virtual desktop / DWM-cloaked
            return true;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        private const int DWMWA_CLOAKED = 14;

        /// <summary>
        /// A window on another virtual desktop (or UWP-suspended) stays
        /// IsWindowVisible==true and !IsIconic while DWM "cloaks" it — the only
        /// reliable signal is DWMWA_CLOAKED. Without this check the overlay
        /// lingered over other apps after a desktop switch.
        /// </summary>
        public static bool IsCloaked(IntPtr hwnd)
        {
            try
            {
                return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                       && cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Snapshot the window's current bounds. Caller passes this to
        /// ProjectBoardRect to get the implied board screen rect.
        /// </summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out RECT rect)
        {
            return GetWindowRect(hwnd, out rect);
        }

        /// <summary>
        /// Given the board's offset within the window (computed once when
        /// vision found the board), and the current window rect, project
        /// the board's current screen rectangle.
        /// </summary>
        public static OpenCvSharp.Rect ProjectBoardRect(RECT windowRect, OpenCvSharp.Rect boardOffsetInWindow)
        {
            return new OpenCvSharp.Rect(
                windowRect.Left + boardOffsetInWindow.X,
                windowRect.Top + boardOffsetInWindow.Y,
                boardOffsetInWindow.Width,
                boardOffsetInWindow.Height);
        }

        /// <summary>
        /// Compute the board's offset relative to a window's top-left.
        /// Called once when vision has found a board and we want to start
        /// tracking the window.
        /// </summary>
        public static OpenCvSharp.Rect ComputeOffset(OpenCvSharp.Rect boardScreenRect, RECT windowRect)
        {
            return new OpenCvSharp.Rect(
                boardScreenRect.X - windowRect.Left,
                boardScreenRect.Y - windowRect.Top,
                boardScreenRect.Width,
                boardScreenRect.Height);
        }

        /// <summary>
        /// Returns true if the window's size changed enough that the board
        /// offset is probably stale and we should re-confirm with vision.
        /// Pure positional moves are not considered "changed" — we only
        /// need to re-confirm when the window resized.
        /// </summary>
        public static bool WindowSizeChanged(RECT prev, RECT current, int tolerancePx = 2)
        {
            return Math.Abs(prev.Width - current.Width) > tolerancePx
                || Math.Abs(prev.Height - current.Height) > tolerancePx;
        }

        // ===== System-event hook for instant minimize / restore detection =====

        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        public const uint EVENT_MIN = 0x00000001;
        public const uint EVENT_MAX = 0x7FFFFFFF;
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_HIDE = 0x8003;
        public const uint EVENT_OBJECT_REORDER = 0x8004;
        public const uint EVENT_OBJECT_FOCUS = 0x8005;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        public const uint EVENT_OBJECT_CLOAKED = 0x8017;
        public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private static readonly List<IntPtr> _winEventHooks = new();
        private static WinEventDelegate? _winEventDelegate;

        /// <summary>
        /// Subscribe to the WinEvents that matter for overlay visibility. The
        /// handler is called the moment a window emits one (minimize/restore,
        /// show/hide/destroy, cloak/uncloak for virtual desktops, foreground).
        /// The caller's handler filters by HWND. Returns true on success.
        ///
        /// Deliberately NOT the full EVENT_MIN..EVENT_MAX range: that hook
        /// delivered every LOCATIONCHANGE (cursor moves!) system-wide through
        /// the overlay UI thread's message pump, so the one MINIMIZESTART that
        /// mattered queued behind thousands of junk events exactly when the
        /// system was busy — the cause of overlays lingering after minimize.
        /// Targeted ranges keep the pump quiet and the hide at ~10ms.
        /// </summary>
        public static bool RegisterWindowStateHook(WinEventDelegate handler)
        {
            if (_winEventHooks.Count > 0)
            {
                return true; // already registered
            }
            _winEventDelegate = handler;
            // No FOREGROUND range: OnSystemWindowEvent has no branch for it
            // (foreground changes are handled by the main loop's poll), so
            // subscribing would only add wakeups to the pump this narrowing
            // exists to keep quiet.
            (uint min, uint max)[] ranges =
            {
                (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE),
                (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
            };
            foreach (var (min, max) in ranges)
            {
                IntPtr h = SetWinEventHook(min, max, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                if (h != IntPtr.Zero)
                    _winEventHooks.Add(h);
            }
            if (_winEventHooks.Count == 0)
            {
                _winEventDelegate = null;
                return false;
            }
            return true;
        }

        public static void UnregisterWindowStateHook()
        {
            foreach (IntPtr h in _winEventHooks)
            {
                UnhookWinEvent(h);
            }
            _winEventHooks.Clear();
            _winEventDelegate = null;
        }
    }
}
