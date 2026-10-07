namespace BackloggdMirror.Services
{
    public enum SessionCheckStatus
    {
        /// <summary>Backloggd answered with a logged-in page.</summary>
        Valid,

        /// <summary>Backloggd answered, but nobody is logged in: the cookies are dead.</summary>
        Expired,

        /// <summary>No Backloggd page arrived (network error, anti-bot screen, browser failure). Says nothing about the cookies.</summary>
        Unreachable
    }

    public sealed record SessionCheckResult(SessionCheckStatus Status, string? Username = null);
}
