namespace CoupleOS.Application.Identity;

/// <summary>
/// The numbers from ADR 0007, in one place so they can be read against the ADR
/// rather than hunted for across the code that enforces them.
///
/// Defaults are the ADR's values. They are options rather than constants so tests
/// can compress a 15-minute window into something a test can wait out, not so
/// that a deployment can quietly loosen them.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>ADR 0007: 15 minutes.</summary>
    public TimeSpan MagicLinkLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>ADR 0007: 3 requests per email per 15 minutes.</summary>
    public int MaxLinksPerEmail { get; init; } = 3;

    public TimeSpan PerEmailWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>ADR 0007: 10 requests per IP per hour.</summary>
    public int MaxLinksPerIp { get; init; } = 10;

    public TimeSpan PerIpWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>ADR 0007: 30-day rolling expiry.</summary>
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How stale a session's <c>last_seen_at</c> must be before using it writes
    /// to the database.
    ///
    /// "Rolling" taken literally means an UPDATE on every authenticated request,
    /// including every static asset — a write per page view to move a timestamp
    /// by milliseconds. Renewing in steps keeps the 30-day window honest while
    /// making the common request read-only. The cost is that
    /// <c>last_seen_at</c> is accurate to within this interval, which is fine for
    /// what it is used for and would not be fine if it were ever used for
    /// security decisions.
    /// </summary>
    public TimeSpan SessionRenewalInterval { get; init; } = TimeSpan.FromHours(1);
}
