using OpenCvSharp;

namespace ChessKit
{
    /// <summary>
    /// Pure board-rectangle / window geometry helpers extracted verbatim from the
    /// Program tracking pipeline. These methods touch no shared mutable state and
    /// call no other Program members, so they are safe to host in a real class.
    /// Program reaches them unqualified via <c>using static ChessKit.BoardGeometry;</c>,
    /// so every former call site is byte-for-byte unchanged.
    /// </summary>
    internal static class BoardGeometry
    {
        public static Rect NormalizeExternalBoardRect(Rect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
                return rect;

            int side = Math.Min(rect.Width, rect.Height);
            int x = rect.X + (rect.Width - side) / 2;
            int y = rect.Y + (rect.Height - side) / 2;
            return new Rect(x, y, side, side);
        }

        public static bool IsSignificantBoardMove(Rect oldRect, Rect newRect)
        {
            double oldCenterX = oldRect.X + oldRect.Width / 2.0;
            double oldCenterY = oldRect.Y + oldRect.Height / 2.0;
            double newCenterX = newRect.X + newRect.Width / 2.0;
            double newCenterY = newRect.Y + newRect.Height / 2.0;

            double centerDistance = Math.Sqrt(
                Math.Pow(newCenterX - oldCenterX, 2) +
                Math.Pow(newCenterY - oldCenterY, 2));

            double movementThreshold = Math.Max(40, Math.Min(oldRect.Width, oldRect.Height) * 0.18);
            double widthDelta = Math.Abs(newRect.Width - oldRect.Width);
            double heightDelta = Math.Abs(newRect.Height - oldRect.Height);
            double sizeThreshold = Math.Max(20, Math.Min(oldRect.Width, oldRect.Height) * 0.12);

            return centerDistance >= movementThreshold ||
                   widthDelta >= sizeThreshold ||
                   heightDelta >= sizeThreshold;
        }

        public static bool IsHardWindowLayoutJump(int previousWidth, int previousHeight, int currentWidth, int currentHeight)
        {
            int widthDelta = Math.Abs(currentWidth - previousWidth);
            int heightDelta = Math.Abs(currentHeight - previousHeight);
            int previousShortSide = Math.Max(1, Math.Min(previousWidth, previousHeight));
            int currentShortSide = Math.Max(1, Math.Min(currentWidth, currentHeight));
            double relativeShortSideDelta = Math.Abs(currentShortSide - previousShortSide) / (double)previousShortSide;

            return widthDelta >= 320 ||
                   heightDelta >= 260 ||
                   relativeShortSideDelta >= 0.22;
        }

        public static int GetNoBoardProbeBackoffMs(int misses)
        {
            return Math.Min(Math.Max(1, misses) switch
            {
                1 => 700,
                2 => 1400,
                3 => 2600,
                4 => 4200,
                _ => 6000
            }, 6000);
        }

        public static bool IsBoardRectInsideWindow(Rect boardRect, WindowTracker.RECT windowRect)
        {
            const int tolerance = 12;

            if (windowRect.Width <= 0 || windowRect.Height <= 0)
                return false;

            int boardLeft = boardRect.X;
            int boardTop = boardRect.Y;
            int boardRight = boardRect.X + boardRect.Width;
            int boardBottom = boardRect.Y + boardRect.Height;

            return boardLeft >= windowRect.Left - tolerance &&
                boardTop >= windowRect.Top - tolerance &&
                boardRight <= windowRect.Right + tolerance &&
                boardBottom <= windowRect.Bottom + tolerance;
        }

        public static bool IsMeaningfullyDifferentExternalBoard(Rect candidate, Rect tracked)
        {
            double trackedCenterX = tracked.X + tracked.Width / 2.0;
            double trackedCenterY = tracked.Y + tracked.Height / 2.0;
            double candidateCenterX = candidate.X + candidate.Width / 2.0;
            double candidateCenterY = candidate.Y + candidate.Height / 2.0;

            double centerDistance = Math.Sqrt(
                Math.Pow(candidateCenterX - trackedCenterX, 2) +
                Math.Pow(candidateCenterY - trackedCenterY, 2));

            double sizeDelta =
                Math.Abs(candidate.Width - tracked.Width) / (double)Math.Max(1, tracked.Width) +
                Math.Abs(candidate.Height - tracked.Height) / (double)Math.Max(1, tracked.Height);

            double minBoardSide = Math.Max(1, Math.Min(tracked.Width, tracked.Height));
            return centerDistance > Math.Max(80, minBoardSide * 0.35) || sizeDelta > 0.35;
        }

        public static Rectangle GetVirtualScreenBounds()
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (var screen in Screen.AllScreens)
            {
                bounds = bounds.IsEmpty ? screen.Bounds : Rectangle.Union(bounds, screen.Bounds);
            }

            return bounds.IsEmpty
                ? (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080))
                : bounds;
        }
    }
}
