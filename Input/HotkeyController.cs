namespace ChessKit
{
    /// <summary>
    /// Owns the hotkey input layer: the low-level <see cref="GlobalKeyListener"/>,
    /// the <see cref="RegisteredHotkeyListener"/> fallback, the current key
    /// <see cref="HotkeyBindings"/>, and the duplicate-press suppression state.
    ///
    /// The controller resolves a pressed key to a <see cref="HotkeyCommand"/> and
    /// raises <see cref="CommandTriggered"/>, which the action dispatcher
    /// (<c>HandleHotkeyCommand</c>) subscribes to. The controller talks to the rest
    /// of the app only through the delegates injected at construction time.
    /// </summary>
    public sealed class HotkeyController : IDisposable
    {
        // Matches the original Program constants exactly.
        private const int DuplicateHotkeySuppressMs = 350;

        private readonly Func<bool> _isStartupComplete;
        private readonly Func<Control?> _getOverlay;
        private readonly Action<string> _log;

        private HotkeyBindings _hotkeys;
        private GlobalKeyListener? _keyListener;
        private RegisteredHotkeyListener? _registeredHotkeys;

        // Duplicate-press suppression: both hotkey listeners (low-level hook and
        // the RegisterHotKey fallback) can deliver the same physical press, so a
        // short window collapses them into one handled command.
        private Keys _lastHandledHotkeyKey = Keys.None;
        private DateTime _lastHandledHotkeyUtc = DateTime.MinValue;

        /// <summary>
        /// Raised on the calling listener's thread once a pressed key resolves to
        /// a command and passes startup + duplicate-suppression gating. Program
        /// subscribes once and calls <c>HandleHotkeyCommand</c>.
        /// </summary>
        public event Action<HotkeyCommand>? CommandTriggered;

        /// <param name="initialBindings">Initial key bindings; the controller takes
        /// ownership of this reference (callers pass a clone).</param>
        /// <param name="isStartupComplete">Gate that must return true before any
        /// hotkey runs feature logic (mirrors Program's <c>_startupComplete</c>).</param>
        /// <param name="getOverlay">Accessor for the overlay control used to marshal
        /// the registered-hotkey rebind onto the UI thread, exactly as Program did.</param>
        /// <param name="log">Program's logger, injected to preserve identical log output.</param>
        internal HotkeyController(
            HotkeyBindings initialBindings,
            Func<bool> isStartupComplete,
            Func<Control?> getOverlay,
            Action<string> log)
        {
            _hotkeys = initialBindings;
            _isStartupComplete = isStartupComplete;
            _getOverlay = getOverlay;
            _log = log;
        }

        /// <summary>
        /// The current key bindings owned by the controller. The setter only stores
        /// the reference (no rebind); it mirrors the original startup assignment
        /// <c>_hotkeys = _settingsToolbar.GetHotkeyBindings()</c>, where the rebind
        /// happened separately via <see cref="WireRegisteredListener"/>.
        /// </summary>
        internal HotkeyBindings Bindings
        {
            get => _hotkeys;
            set => _hotkeys = value;
        }

        /// <summary>
        /// Replaces the owned bindings and pushes them to the registered-hotkey
        /// fallback (rebinding on the UI thread when required). Mirrors Program's
        /// KeyBindingsChanged handling: assign + Normalize happens in Program; this
        /// just stores the already-normalized clone and rebinds.
        /// </summary>
        internal void SetBindings(HotkeyBindings bindings)
        {
            _hotkeys = bindings;
            RebindRegisteredHotkeys();
        }

        /// <summary>
        /// Creates the <see cref="RegisteredHotkeyListener"/>. MUST be called on the
        /// pumped UI thread (the listener is a <see cref="NativeWindow"/> whose
        /// handle is created in its constructor), matching the original creation
        /// site inside the UI-thread setup lambda.
        /// </summary>
        internal void CreateRegisteredListener()
        {
            _registeredHotkeys = new RegisteredHotkeyListener();
        }

        /// <summary>
        /// Subscribes the registered-hotkey fallback to <see cref="SafeHandleKeyPress"/>
        /// and applies the current bindings. Called on the UI thread right after the
        /// bindings are pulled from the toolbar, exactly as the original code did.
        /// </summary>
        internal void WireRegisteredListener()
        {
            if (_registeredHotkeys == null)
                return;

            _registeredHotkeys.KeyPressed += SafeHandleKeyPress;
            _registeredHotkeys.UpdateBindings(_hotkeys);
        }

        /// <summary>
        /// Creates and starts the low-level <see cref="GlobalKeyListener"/> and wires
        /// its key-press handler. The handler reproduces Program's original logic:
        /// ignore keys that map to no command, then marshal onto the overlay thread
        /// before invoking <see cref="SafeHandleKeyPress"/>.
        /// </summary>
        internal void StartGlobalListener()
        {
            _keyListener = new GlobalKeyListener();
            _keyListener.KeyPressed += key =>
            {
                if (ResolveHotkeyCommand(key) == null)
                    return false;

                Control? overlay = _getOverlay();
                if (overlay?.InvokeRequired == true)
                    overlay.BeginInvoke(new Action(() => SafeHandleKeyPress(key)));
                else
                    SafeHandleKeyPress(key);

                return true;
            };
            _keyListener.Start();
        }

        /// <summary>
        /// Reproduces the exact programmatic toggle-overlay path used by the
        /// system-tray and free-edition-window menu items: route the configured
        /// ToggleOverlay key through the same dedup + resolve pipeline as a real
        /// key press (does NOT bypass duplicate suppression).
        /// </summary>
        public void TriggerToggleOverlay()
        {
            SafeHandleKeyPress(_hotkeys.ToggleOverlay);
        }

        private void SafeHandleKeyPress(Keys key)
        {
            // Ignore hotkeys until startup finishes. Both hotkey listeners funnel
            // through here, and one of them is live before the license check, so a
            // key pressed during the loading splash would otherwise run feature
            // logic prematurely (freezing the splash + showing "Feature blocked").
            if (!_isStartupComplete())
            {
                TraceProtectedHotkey($"ignored {key} - startup not complete");
                return;
            }

            try
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (_lastHandledHotkeyKey == key &&
                    (nowUtc - _lastHandledHotkeyUtc).TotalMilliseconds < DuplicateHotkeySuppressMs)
                {
                    TraceProtectedHotkey($"suppressed duplicate {key} after {(nowUtc - _lastHandledHotkeyUtc).TotalMilliseconds:F0}ms");
                    return;
                }

                _lastHandledHotkeyKey = key;
                _lastHandledHotkeyUtc = nowUtc;
                TraceProtectedHotkey($"accepted {key}");
                HandleKeyPress(key);
            }
            catch (Exception ex)
            {
                _log($"[KEY ERROR] {key}: {ex.GetType().Name}: {ex.Message}");
                TraceProtectedHotkey($"error {key}: {ex.GetType().Name}: {ex.Message}");
                DebugRuntime.WriteLine($"[KEY ERROR] {key}: {ex}");
            }
        }

        private static void TraceProtectedHotkey(string message)
        {
#if DEBUG
            _ = message;
#endif
        }

        private void RebindRegisteredHotkeys()
        {
            void Rebind()
            {
                try { _registeredHotkeys?.UpdateBindings(_hotkeys); }
                catch (Exception ex) { _log($"[KEY ERROR] Failed to rebind system hotkeys: {ex.Message}"); }
            }

            Control? overlay = _getOverlay();
            if (overlay?.InvokeRequired == true)
                overlay.BeginInvoke(new Action(Rebind));
            else
                Rebind();
        }

        private void HandleKeyPress(Keys key)
        {
            var command = ResolveHotkeyCommand(key);
            if (command == null)
            {
                _log($"[KEY] {key} pressed but no command is mapped to it");
                return;
            }

            CommandTriggered?.Invoke(command.Value);
        }

        private HotkeyCommand? ResolveHotkeyCommand(Keys key)
        {
            if (_hotkeys.ToggleOverlay == key)
                return HotkeyCommand.ToggleOverlay;
            if (_hotkeys.AnalyzeWhite == key)
                return HotkeyCommand.AnalyzeWhite;
            if (_hotkeys.AnalyzeBlack == key)
                return HotkeyCommand.AnalyzeBlack;
            if (_hotkeys.AnalyzeBoth == key)
                return HotkeyCommand.AnalyzeBoth;
            if (_hotkeys.CopyFen == key)
                return HotkeyCommand.CopyFen;
            if (_hotkeys.ToggleEngineLines == key)
                return HotkeyCommand.ToggleEngineLines;
            if (_hotkeys.ToggleEvalBar == key)
                return HotkeyCommand.ToggleEvalBar;
            return null;
        }

        /// <summary>
        /// Disposes the registered-hotkey fallback. Must run on the UI thread that
        /// owns the native window (Program marshals this onto the overlay), matching
        /// the original disposal site.
        /// </summary>
        public void DisposeRegisteredListener()
        {
            try { _registeredHotkeys?.Dispose(); } catch { }
            _registeredHotkeys = null;
        }

        /// <summary>
        /// Disposes the low-level global key listener. Mirrors the original
        /// <c>using var keyListener</c> disposal in Program.Main (same main-thread
        /// call site at shutdown). The registered-hotkey fallback is a UI-thread
        /// NativeWindow and is disposed separately via
        /// <see cref="DisposeRegisteredListener"/>, marshalled onto the overlay.
        /// </summary>
        public void Dispose()
        {
            try { _keyListener?.Dispose(); } catch { }
            _keyListener = null;
        }
    }
}
