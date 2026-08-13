using CoupleOS.Application.Security;
using CoupleOS.Application.Time;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Time;

/// <summary>
/// Reads <c>couples.timezone</c> once per request and answers what time it is
/// there.
///
/// On <see cref="IdentityDbContext"/> rather than the scoped one, because
/// <c>couples</c> is one of the six documented tables with no row-level security:
/// the read needs no couple scope, so the clock does not have to sit inside
/// <c>IScopedUnitOfWork</c> and cannot accidentally become the thing that opens a
/// transaction.
/// </summary>
public sealed class CoupleClock(
    IdentityDbContext dbContext,
    ICoupleScopeAccessor scopeAccessor,
    TimeProvider clock) : ICoupleClock
{
    private readonly IdentityDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// The schema's own default, repeated here as the fallback.
    ///
    /// A literal in two places, which is worth it: the alternative is a resolver
    /// that throws when the column holds something odd, and the thing it would be
    /// throwing during is somebody's reminder.
    /// </summary>
    private const string Fallback = "Asia/Kolkata";

    /// <summary>
    /// Cached for the life of the request. A dump run resolves a date per block and
    /// a couple does not move between them.
    /// </summary>
    private CoupleTime? _resolved;

    public async Task<CoupleTime> NowAsync(CancellationToken cancellationToken = default)
    {
        // The instant is read every call and only the zone is cached: a long run
        // should not resolve its last block against the time its first one started.
        if (_resolved is { } cached)
        {
            return cached with { Now = _clock.GetUtcNow() };
        }

        var coupleId = _scopeAccessor.Current.CoupleId;

        var stored = await _dbContext.Couples
            .AsNoTracking()
            .Where(c => c.Id == coupleId)
            .Select(c => c.TimeZoneId)
            .FirstOrDefaultAsync(cancellationToken);

        var (zone, recognised) = Resolve(stored);

        _resolved = new CoupleTime(_clock.GetUtcNow(), zone, recognised);

        return _resolved;
    }

    /// <summary>
    /// A stored zone id to a <see cref="TimeZoneInfo"/>, falling back rather than
    /// throwing.
    ///
    /// The column is <c>text</c> with a default and no check constraint, and
    /// nothing in the product lets a couple set it — so a value this runtime cannot
    /// resolve means either a hand-edited row or a container without the tz
    /// database. Neither is a reason to fail a tool call, and both are a reason to
    /// say so, which is what <see cref="CoupleTime.Recognised"/> is for.
    /// </summary>
    private static (TimeZoneInfo Zone, bool Recognised) Resolve(string? stored)
    {
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                return (TimeZoneInfo.FindSystemTimeZoneById(stored), true);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Falls through to the default below.
            }
        }

        try
        {
            return (TimeZoneInfo.FindSystemTimeZoneById(Fallback), stored is null);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // No tz database at all. UTC is wrong for this product and it is still
            // better than refusing to tell anyone what day it is.
            return (TimeZoneInfo.Utc, false);
        }
    }
}
