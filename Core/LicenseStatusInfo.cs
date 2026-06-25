namespace ChessKit
{
    /// <summary>
    /// Why the app is running as the limited Free Edition, when it is NOT an
    /// ordinary never-licensed Free user. Set once at startup from the license
    /// check and read cheaply at paint time by the Free watermark + toolbar chip
    /// so a user (and we, while testing) can tell an INACTIVE license apart from a
    /// normal Free session.
    /// </summary>
    internal enum LicenseInactiveReason
    {
        /// <summary>Ordinary Free Edition: never licensed (or no reason to surface).</summary>
        None = 0,
        Suspended,
        Expired,
        Revoked,
        /// <summary>Any other non-active state (incl. an offline/unknown check).</summary>
        Inactive
    }

    /// <summary>
    /// The single source of truth for WHY this Free session is limited.
    ///
    /// Mirrors <see cref="FreeTierServerState"/>: a tiny static the licensing layer
    /// writes once at startup (and the background monitor may clear when a purchase
    /// upgrades the session to Licensed) and the UI reads at paint time. Default is
    /// <see cref="LicenseInactiveReason.None"/> (ordinary Free) so a missing wire
    /// can never invent a scary "license inactive" banner for a normal Free user.
    ///
    /// A Licensed session never shows a watermark at all, so the reason is only
    /// meaningful while <c>BuildLimits.IsFreeEdition</c> is true; callers must gate
    /// on that before surfacing it.
    /// </summary>
    internal static class LicenseStatusInfo
    {
        // volatile: written from the startup/background-monitor worker thread,
        // read from the UI paint thread. A single enum write is atomic.
        private static volatile LicenseInactiveReason _reason = LicenseInactiveReason.None;

        /// <summary>
        /// The reason this Free session is limited, or <see cref="LicenseInactiveReason.None"/>
        /// for an ordinary Free user. Cheap to read at paint time.
        /// </summary>
        public static LicenseInactiveReason Reason => _reason;

        /// <summary>True when there is a distinct inactive reason worth surfacing.</summary>
        public static bool HasInactiveReason => _reason != LicenseInactiveReason.None;

        /// <summary>Sets the reason. Called from the licensing layer at startup.</summary>
        public static void SetReason(LicenseInactiveReason reason) => _reason = reason;

        /// <summary>
        /// Maps a normalized license status string (e.g. "suspended", "expired",
        /// "revoked", "active", "unlicensed") to a reason. Empty / "active" /
        /// "none" / "unlicensed" map to <see cref="LicenseInactiveReason.None"/>
        /// (ordinary Free); any other non-active status is treated as Inactive.
        /// </summary>
        public static LicenseInactiveReason FromStatus(string? status)
        {
            switch ((status ?? "").Trim().ToLowerInvariant())
            {
                case "suspended":
                    return LicenseInactiveReason.Suspended;
                case "expired":
                    return LicenseInactiveReason.Expired;
                case "revoked":
                    return LicenseInactiveReason.Revoked;
                case "":
                case "active":
                case "none":
                case "unlicensed":
                    return LicenseInactiveReason.None;
                default:
                    return LicenseInactiveReason.Inactive;
            }
        }

        /// <summary>
        /// The watermark lead-in for the current reason ("License suspended — Free
        /// mode", etc.), or empty for an ordinary Free user.
        /// </summary>
        public static string WatermarkLead(LicenseInactiveReason reason)
        {
            return reason switch
            {
                LicenseInactiveReason.Suspended => "License suspended — Free mode",
                LicenseInactiveReason.Expired => "License expired — Free mode",
                LicenseInactiveReason.Revoked => "License revoked — Free mode",
                LicenseInactiveReason.Inactive => "License inactive — Free mode",
                _ => ""
            };
        }

        /// <summary>
        /// The short amber toolbar chip label for the current reason ("SUSPENDED",
        /// "EXPIRED", "REVOKED", "INACTIVE"), or "FREE" for an ordinary Free user.
        /// </summary>
        public static string ChipLabel(LicenseInactiveReason reason)
        {
            return reason switch
            {
                LicenseInactiveReason.Suspended => "SUSPENDED",
                LicenseInactiveReason.Expired => "EXPIRED",
                LicenseInactiveReason.Revoked => "REVOKED",
                LicenseInactiveReason.Inactive => "INACTIVE",
                _ => "FREE"
            };
        }
    }
}
