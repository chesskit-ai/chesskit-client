using System.Diagnostics;

namespace ChessKit
{
    /// <summary>
    /// The single source of truth for the SERVER-driven Free tier signal.
    ///
    /// The server now governs the Free limit as a per-HWID move-count window plus
    /// a cooldown, and reports it on every analysis response: how many moves remain
    /// in the current window (<see cref="FreeMovesRemaining"/>) and, only while a
    /// cooldown is active, the seconds until it resets (<see cref="FreeCooldownSeconds"/>).
    /// The client no longer counts moves itself; it just displays this and pauses
    /// new analysis while a cooldown is running.
    ///
    /// The cooldown countdown ticks down LOCALLY: we seed a deadline from the
    /// server's <c>freeCooldownSeconds</c> the moment we observe it, and every
    /// reader (the toolbar repaint timer, the per-second metrics push, the analysis
    /// gate) derives the remaining seconds from that deadline. That keeps the
    /// watermark visible and counting for the whole cooldown without needing a
    /// fresh server report each second, and it auto-resumes at zero.
    ///
    /// A Licensed session never trips any of this: licensed responses carry no free
    /// fields, so <see cref="IsFreeLimited"/> stays false and the cooldown is never
    /// seeded — licensed users see no watermark and are never paused.
    /// </summary>
    internal static class FreeTierServerState
    {
        private static readonly object Gate = new();

        // Monotonic clock so the countdown is immune to wall-clock changes.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private static bool _free;
        private static int _movesRemaining;
        // Monotonic timestamp (Clock.ElapsedMilliseconds) at which the current
        // cooldown ends. 0 = no cooldown active.
        private static long _cooldownEndsAtMs;
        // The server-reported cooldown length that seeded _cooldownEndsAtMs, so a
        // repeated identical report doesn't keep re-seeding (which would freeze the
        // countdown). A materially different value re-seeds (server is authoritative).
        private static int _seededCooldownSeconds;

        /// <summary>
        /// Folds in the latest free signal from a server analysis response.
        /// Pass free=false (the licensed case, or any response with no free tag)
        /// to clear all free/cooldown state.
        /// </summary>
        public static void Report(bool free, int movesRemaining, int cooldownSeconds)
        {
            lock (Gate)
            {
                _free = free;

                if (!free)
                {
                    _movesRemaining = 0;
                    _cooldownEndsAtMs = 0;
                    _seededCooldownSeconds = 0;
                    return;
                }

                _movesRemaining = Math.Max(0, movesRemaining);

                if (cooldownSeconds > 0)
                {
                    // Seed the local countdown when nothing is running, or when the
                    // server reports LESS time left than our local deadline (let the
                    // server shorten the wait). NEVER extend an active local
                    // countdown from a server report: a re-arm, an over-report, or a
                    // server that keeps re-issuing a fresh cooldown must not trap the
                    // countdown above zero. The monotonic local clock always drains
                    // to zero on its own, so a cooldown can never get stuck — it
                    // auto-resumes the instant it elapses.
                    long nowMs = Clock.ElapsedMilliseconds;
                    long serverEndsAtMs = nowMs + (long)cooldownSeconds * 1000L;
                    bool notRunning = _cooldownEndsAtMs <= nowMs;
                    if (notRunning || serverEndsAtMs < _cooldownEndsAtMs - 1000L)
                    {
                        _cooldownEndsAtMs = serverEndsAtMs;
                        _seededCooldownSeconds = cooldownSeconds;
                    }
                }
                else
                {
                    // Server says the window is open again.
                    _cooldownEndsAtMs = 0;
                    _seededCooldownSeconds = 0;
                }
            }
        }

        /// <summary>Clears all free/cooldown state (e.g. on disabling analysis).</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _free = false;
                _movesRemaining = 0;
                _cooldownEndsAtMs = 0;
                _seededCooldownSeconds = 0;
            }
        }

        /// <summary>Whether the most recent server response tagged the session as Free.</summary>
        public static bool IsFreeLimited
        {
            get { lock (Gate) { return _free; } }
        }

        /// <summary>Moves left in the current Free window per the server.</summary>
        public static int FreeMovesRemaining
        {
            get { lock (Gate) { return _movesRemaining; } }
        }

        /// <summary>
        /// Seconds remaining in the current cooldown, derived locally from the
        /// seeded deadline. 0 when no cooldown is running. Rounds UP so the
        /// display shows "0:01" rather than "0:00" during the final second.
        /// </summary>
        public static int CooldownRemainingSeconds
        {
            get
            {
                lock (Gate)
                {
                    if (_cooldownEndsAtMs <= 0)
                        return 0;
                    long remainingMs = _cooldownEndsAtMs - Clock.ElapsedMilliseconds;
                    if (remainingMs <= 0)
                    {
                        // Expired: clear so the very next read resumes cleanly.
                        _cooldownEndsAtMs = 0;
                        _seededCooldownSeconds = 0;
                        return 0;
                    }
                    return (int)((remainingMs + 999) / 1000);
                }
            }
        }

        /// <summary>True while a Free cooldown countdown is still running.</summary>
        public static bool IsInCooldown => CooldownRemainingSeconds > 0;

        /// <summary>Formats the remaining cooldown as M:SS for the watermark/toolbar.</summary>
        public static string FormatCooldown(int totalSeconds)
        {
            if (totalSeconds < 0)
                totalSeconds = 0;
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            return $"{minutes}:{seconds:D2}";
        }
    }
}
