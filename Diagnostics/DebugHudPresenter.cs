using System.Drawing;

namespace ChessKit
{
    /// <summary>
    /// Owns the floating <see cref="DebugHudForm"/> and its placement. The
    /// presenter talks to the rest of the app only through the anchor-provider
    /// delegate injected at construction time.
    /// </summary>
    public sealed class DebugHudPresenter : IDisposable
    {
        private DebugHudForm? _debugHud;
        private readonly Func<Point?> _getToolbarAnchor;

        public DebugHudPresenter(Func<System.Drawing.Point?> getToolbarAnchor)
        {
            _getToolbarAnchor = getToolbarAnchor;
        }

        /// <summary>The underlying HUD form, if created. Used by Program's
        /// IsFormHandle check in window tracking.</summary>
        public DebugHudForm? Form => _debugHud;

        public bool IsEnabled => _debugHud is { IsEnabled: true };

        public void SetEnabled(bool enabled)
        {
            _debugHud ??= new DebugHudForm();
            _debugHud.SetEnabled(enabled);
            if (enabled)
            {
                PositionDebugHud();
            }
        }

        public void UpdateMetrics(double fps, double fenPerSec, string captureMode,
                                  string engineName, string executionMode,
                                  bool tracking, bool boardTracked, bool analysisOn,
                                  string analysisSide, int arrowCount,
                                  bool waitingForOpponent, double lastMoveLatencyMs,
                                  string lastEvent, string lastEngineFen,
                                  string streamTransport, bool mirrorActive)
        {
            if (_debugHud is not { IsEnabled: true }) return;

            _debugHud.UpdateMetrics(
                fps: fps,
                fenPerSec: fenPerSec,
                captureMode: captureMode,
                engineName: engineName,
                executionMode: executionMode,
                tracking: tracking,
                boardTracked: boardTracked,
                analysisOn: analysisOn,
                analysisSide: analysisSide,
                arrowCount: arrowCount,
                waitingForOpponent: waitingForOpponent,
                lastMoveLatencyMs: lastMoveLatencyMs,
                lastEvent: lastEvent,
                lastEngineFen: lastEngineFen,
                streamTransport: streamTransport,
                mirrorActive: mirrorActive);

            PositionDebugHud();
        }

        /// <summary>
        /// Places the debug HUD just below the settings toolbar, or in the
        /// top-left of the primary screen if the toolbar isn't visible yet.
        /// Called whenever the HUD is enabled or whenever metrics tick (so the
        /// HUD follows the toolbar if the toolbar moves).
        /// </summary>
        private void PositionDebugHud()
        {
            if (_debugHud == null) return;

            System.Drawing.Point target = _getToolbarAnchor() ?? DefaultAnchor();

            if (_debugHud.InvokeRequired)
            {
                _debugHud.BeginInvoke(new Action(() => _debugHud!.UpdatePosition(target)));
            }
            else
            {
                _debugHud.UpdatePosition(target);
            }
        }

        private static Point DefaultAnchor()
        {
            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            return new Point(screen.X + 20, screen.Y + 20);
        }

        public void Dispose()
        {
            try { _debugHud?.Dispose(); } catch { }
            _debugHud = null;
        }
    }
}
