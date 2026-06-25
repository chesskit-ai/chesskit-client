using System.Runtime.InteropServices;

namespace ChessKit
{
    /// <summary>
    /// Win32 mouse-button polling helper.
    /// </summary>
    internal static class MouseInput
    {
        /// <summary>
        /// Returns true while the given virtual-key mouse button is physically
        /// held down (e.g. 0x01 = left, 0x02 = right).
        /// </summary>
        public static bool IsMouseButtonDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
