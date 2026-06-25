using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ChessKit
{
    public sealed class GlobalKeyListener : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        private readonly Thread _hookThread;
        private readonly LowLevelKeyboardProc _hookProc;
        private readonly HashSet<Keys> _pressedKeys = new();
        private readonly HashSet<Keys> _suppressedKeys = new();
        private volatile bool _running;
        private IntPtr _hookId = IntPtr.Zero;
        private uint _hookThreadId;

        public event Func<Keys, bool>? KeyPressed;

        private static readonly HashSet<Keys> FunctionKeys = new()
        {
            Keys.F1, Keys.F2, Keys.F3, Keys.F4, Keys.F5, Keys.F6,
            Keys.F7, Keys.F8, Keys.F9, Keys.F10, Keys.F11, Keys.F12
        };

        public GlobalKeyListener()
        {
            _hookProc = HookCallback;
            _hookThread = new Thread(RunHookLoop)
            {
                IsBackground = true,
                Name = "KeyboardHook"
            };
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();
            DebugRuntime.WriteLine("[INFO] Global key listener started - suppressing mapped F1-F12 hotkeys");
        }

        public void Stop()
        {
            _running = false;

            if (_hookThreadId != 0)
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            if (_hookThread.IsAlive)
                _hookThread.Join(500);
        }

        public void Dispose()
        {
            Stop();
        }

        private void RunHookLoop()
        {
            _hookThreadId = GetCurrentThreadId();
            _hookId = SetHook(_hookProc);
            if (_hookId == IntPtr.Zero)
            {
                DebugRuntime.WriteLine("[ERROR] Failed to install global keyboard hook");
                return;
            }

            try
            {
                while (_running && GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (_hookId != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hookId);
                    _hookId = IntPtr.Zero;
                }
            }
        }

        private static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using Process currentProcess = Process.GetCurrentProcess();
            using ProcessModule? currentModule = currentProcess.MainModule;
            IntPtr moduleHandle = currentModule == null
                ? IntPtr.Zero
                : GetModuleHandle(currentModule.ModuleName);
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int message = wParam.ToInt32();
            var hook = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            Keys key = (Keys)hook.vkCode;

            if (!FunctionKeys.Contains(key))
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            bool keyDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool keyUp = message == WM_KEYUP || message == WM_SYSKEYUP;

            if (keyUp)
            {
                bool suppressKeyUp;
                lock (_pressedKeys)
                {
                    _pressedKeys.Remove(key);
                    suppressKeyUp = _suppressedKeys.Remove(key);
                }

                return suppressKeyUp
                    ? (IntPtr)1
                    : CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            if (!keyDown)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            bool firstPress;
            lock (_pressedKeys)
                firstPress = _pressedKeys.Add(key);

            if (firstPress)
            {
                try
                {
                    bool handled = KeyPressed?.Invoke(key) == true;
                    if (handled)
                    {
                        lock (_pressedKeys)
                            _suppressedKeys.Add(key);
                        return (IntPtr)1;
                    }
                }
                catch (Exception ex)
                {
                    DebugRuntime.WriteLine($"[ERROR] Key event handler error: {ex.Message}");
                }
            }

            lock (_pressedKeys)
            {
                if (_suppressedKeys.Contains(key))
                    return (IntPtr)1;
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private const int WM_QUIT = 0x0012;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    }
}
