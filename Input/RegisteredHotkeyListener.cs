using System.Runtime.InteropServices;

namespace ChessKit
{
    internal sealed class RegisteredHotkeyListener : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int BaseHotkeyId = 0x4300;
        private readonly Dictionary<int, Keys> _registered = new();
        private bool _disposed;

        public event Action<Keys>? KeyPressed;

        public RegisteredHotkeyListener()
        {
            CreateHandle(new CreateParams());
        }

        public void UpdateBindings(HotkeyBindings bindings)
        {
            if (_disposed)
                return;

            UnregisterAll();
            bindings = bindings.Clone();
            bindings.Normalize();

            RegisterBinding(0, bindings.ToggleOverlay);
            RegisterBinding(1, bindings.AnalyzeWhite);
            RegisterBinding(2, bindings.AnalyzeBlack);
            RegisterBinding(3, bindings.AnalyzeBoth);
            RegisterBinding(4, bindings.CopyFen);
            RegisterBinding(5, bindings.ToggleEngineLines);
            RegisterBinding(6, bindings.ToggleEvalBar);
        }

        private void RegisterBinding(int slot, Keys key)
        {
            if (key == Keys.None)
                return;

            int id = BaseHotkeyId + slot;
            if (RegisterHotKey(Handle, id, 0, (uint)key))
            {
                _registered[id] = key;
                DebugRuntime.WriteLine($"[INFO] Registered system hotkey fallback: {key}");
            }
            else
            {
                DebugRuntime.WriteLine($"[WARN] Could not register system hotkey fallback: {key}");
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && _registered.TryGetValue(m.WParam.ToInt32(), out Keys key))
            {
                try
                {
                    KeyPressed?.Invoke(key);
                }
                catch (Exception ex)
                {
                    DebugRuntime.WriteLine($"[ERROR] Registered hotkey handler error: {ex.Message}");
                }

                return;
            }

            base.WndProc(ref m);
        }

        private void UnregisterAll()
        {
            foreach (int id in _registered.Keys.ToArray())
            {
                try { UnregisterHotKey(Handle, id); } catch { }
            }

            _registered.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            UnregisterAll();
            try { DestroyHandle(); } catch { }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
