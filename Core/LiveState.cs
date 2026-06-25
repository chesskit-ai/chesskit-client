using OpenCvSharp;

namespace ChessKit
{
    /// <summary>
    /// Shared live-pipeline state holder for the real-time tracking / FEN /
    /// analysis / arrow loop that runs across the <c>partial class Program</c>
    /// hot path (<c>Program.cs</c> Main plus the Tracking / Fen / Analysis /
    /// Arrows / WindowEvents partials).
    ///
    /// Program keeps a single instance (<c>Program._state</c>) and exposes each
    /// field back to the hot-path code through an <c>internal static</c> property
    /// shim. Fields that are <c>volatile</c>, used with
    /// <c>Interlocked</c>/<c>Volatile</c>/<c>ref</c>/<c>out</c>, are
    /// <c>readonly</c> locks/collections, or have non-trivial construction remain
    /// on Program as <c>internal static</c> rather than living here.
    ///
    /// This type holds data only; all behavior lives in Program.
    /// </summary>
    internal sealed class LiveState
    {
        // === Confirmed live position / detector output ===
        public string CurrentFen = "";
        public bool CurrentFenIsAnalysisBoard = false;

        // === Loop / tracking status ===
        public bool IsTracking = false;
        public bool ShowingMoves = false;
        public bool MenuExpanded = false;
        public bool CoachModeEnabled = false;
        public int BoardLostFrames = 0;
        public int InvalidFenFrames = 0;
        public bool RequestBoardRefresh = false;
        public IntPtr TrackedHwnd = IntPtr.Zero;
        public IntPtr LostHwndCache = IntPtr.Zero;
        public Rect? LastTrackedBox = null;

        // === Analysis toggles / turn inference ===
        public bool ContinuousAnalysisEnabled = false;
        public bool AnalysisIsBlackPerspective = false;
        public bool WaitingForOpponentMove = false;
        public char InferredSideToMove = 'w';

        // === External-board orientation ===
        public bool ExternalBoardDetectedFlipped = false;
        public bool ExternalOrientationLockedForCurrentGame = false;
        public int ExternalTrackedPositionCount = 0;

        // === Arrow / variation display state ===
        public List<MoveArrow>? CurrentMoveArrows = null;
        public List<MoveVariation>? LastAnalysisVariations = null;
        public string LastArrowSourceFen = "";

        // === Move-latency window ===
        public DateTime LatencyT0Utc = DateTime.MinValue;
        public int LatencyT0ChangedSquares = 0;
    }
}
