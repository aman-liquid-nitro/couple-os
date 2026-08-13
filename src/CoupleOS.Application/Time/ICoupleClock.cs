namespace CoupleOS.Application.Time;

/// <summary>
/// What time it is where the couple is.
/// </summary>
/// <param name="Zone">
/// From <c>couples.timezone</c>. Falls back to the schema's own default when the
/// column holds something this runtime cannot resolve — see
/// <c>CoupleClock</c> for why that is a fallback and not an exception.
/// </param>
/// <param name="Recognised">
/// False when the stored zone id could not be resolved and the default was used
/// instead. Carried so a caller can say so rather than quietly resolving dates
/// against the wrong country.
/// </param>
public sealed record CoupleTime(DateTimeOffset Now, TimeZoneInfo Zone, bool Recognised = true);

/// <summary>
/// The couple's clock, which is not the server's.
///
/// A service rather than a <c>TimeProvider</c> because the timezone is a stored
/// per-couple value, so answering "what day is it for them" takes a read. Scoped
/// and cached for the request: a dump run resolves a date per block and none of
/// them should re-read the same column.
/// </summary>
public interface ICoupleClock
{
    Task<CoupleTime> NowAsync(CancellationToken cancellationToken = default);
}
