using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ChessKit
{
    /// <summary>
    /// Excludes ChessKit's analysis surfaces (arrows, eval bar, engine lines)
    /// from ALL screen capture via SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE).
    ///
    /// Two wins in one API:
    ///   1. Feedback prevention. Our own vision captures the desktop through
    ///      DXGI Desktop Duplication (see ScreenCapture), which composites in our
    ///      top-most arrow overlay. Without exclusion the arrows we draw over the
    ///      board are re-read by our own detector - the pixels on the arrowed
    ///      squares change, inflating delta patches and adding self-inflicted
    ///      noise. WDA_EXCLUDEFROMCAPTURE makes those windows invisible to the
    ///      duplicated output while still rendering normally to the user's eyes.
    ///   2. Stream-safety. The same exclusion hides the overlay from OBS, Discord
    ///      screen-share, and other captures - expected behaviour for this tool.
    ///
    /// WDA_EXCLUDEFROMCAPTURE requires Windows 10 2004 (build 19041)+. On older
    /// builds the call returns false; we degrade silently to normal (captured)
    /// rendering rather than failing.
    ///
    /// NOTE: excluded windows are also invisible in capture-based remote views
    /// (some RDP/VM-console paths). Users debugging ChessKit inside a VM they
    /// view via screen-share should turn this off (the setting toggle) so the
    /// arrows remain visible in that shared view.
    /// </summary>
    internal static class CaptureExclusion
    {
        private const uint WDA_NONE = 0x0;
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        private static readonly object Gate = new();
        private static readonly List<Form> Tracked = new();
        // Default ON: feedback-prevention + stream-safety is the correct product
        // behaviour. Overridden from persisted settings at startup.
        private static bool _enabled = true;
        private static bool _loggedUnsupported;

        /// <summary>
        /// Registers an analysis-surface form. Applies the current affinity now
        /// (if the handle exists) and re-applies whenever the handle is recreated
        /// - WinForms rebuilds handles on some property changes, and affinity is
        /// per-HWND, so it must be re-set each time. Auto-unregisters on dispose.
        /// </summary>
        public static void Register(Form form)
        {
            if (form == null)
                return;

            lock (Gate)
            {
                if (!Tracked.Contains(form))
                    Tracked.Add(form);
            }

            form.HandleCreated += (_, _) => ApplyTo(form);
            form.Disposed += (_, _) =>
            {
                lock (Gate) { Tracked.Remove(form); }
            };

            if (form.IsHandleCreated)
                ApplyTo(form);
        }

        /// <summary>
        /// Enables or disables capture exclusion and re-applies to every live
        /// tracked surface. Safe to call from the UI thread on a setting change.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            Form[] snapshot;
            lock (Gate)
            {
                _enabled = enabled;
                snapshot = Tracked.ToArray();
            }

            foreach (var form in snapshot)
                ApplyTo(form);
        }

        public static bool IsEnabled
        {
            get { lock (Gate) { return _enabled; } }
        }

        private static void ApplyTo(Form form)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;

            bool enabled;
            lock (Gate) { enabled = _enabled; }

            try
            {
                if (form.InvokeRequired)
                {
                    form.BeginInvoke(new Action(() => ApplyHandle(form, enabled)));
                    return;
                }
            }
            catch
            {
                // Handle can race a dispose between the checks above and the
                // InvokeRequired read; treat as gone.
                return;
            }

            ApplyHandle(form, enabled);
        }

        private static void ApplyHandle(Form form, bool enabled)
        {
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;

            try
            {
                bool ok = SetWindowDisplayAffinity(form.Handle, enabled ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
                if (!ok && enabled && !_loggedUnsupported)
                {
                    _loggedUnsupported = true;
                    DebugRuntime.WriteLine("[CaptureExclusion] SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed; " +
                        "overlays will be visible to screen capture (needs Windows 10 2004+).");
                }
            }
            catch
            {
                // P/Invoke should not throw, but never let overlay setup die here.
            }
        }
    }
}
