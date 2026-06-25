namespace ChessKit
{
    /// <summary>
    /// Pure helpers for interpreting WinEvent (SetWinEventHook) notifications.
    /// </summary>
    internal static class WinEventHelper
    {
        public static string GetWinEventName(uint eventType)
        {
            return eventType switch
            {
                WindowTracker.EVENT_SYSTEM_FOREGROUND => "EVENT_SYSTEM_FOREGROUND",
                WindowTracker.EVENT_SYSTEM_MOVESIZESTART => "EVENT_SYSTEM_MOVESIZESTART",
                WindowTracker.EVENT_SYSTEM_MOVESIZEEND => "EVENT_SYSTEM_MOVESIZEEND",
                WindowTracker.EVENT_SYSTEM_MINIMIZESTART => "EVENT_SYSTEM_MINIMIZESTART",
                WindowTracker.EVENT_SYSTEM_MINIMIZEEND => "EVENT_SYSTEM_MINIMIZEEND",
                WindowTracker.EVENT_OBJECT_DESTROY => "EVENT_OBJECT_DESTROY",
                WindowTracker.EVENT_OBJECT_SHOW => "EVENT_OBJECT_SHOW",
                WindowTracker.EVENT_OBJECT_HIDE => "EVENT_OBJECT_HIDE",
                WindowTracker.EVENT_OBJECT_REORDER => "EVENT_OBJECT_REORDER",
                WindowTracker.EVENT_OBJECT_FOCUS => "EVENT_OBJECT_FOCUS",
                WindowTracker.EVENT_OBJECT_LOCATIONCHANGE => "EVENT_OBJECT_LOCATIONCHANGE",
                WindowTracker.EVENT_OBJECT_NAMECHANGE => "EVENT_OBJECT_NAMECHANGE",
                _ => $"EVENT_0x{eventType:X4}"
            };
        }

        public static bool IsWinEventWindowObject(int idObject, int idChild)
        {
            const int OBJID_WINDOW = 0;
            const int CHILDID_SELF = 0;
            return idObject == OBJID_WINDOW && idChild == CHILDID_SELF;
        }
    }
}
