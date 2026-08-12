using System.Net;

namespace CoupleOS.Application.Identity;

/// <summary>
/// A resolved session: who it belongs to, and the couple they are in if any.
///
/// <see cref="CoupleId"/> is nullable because "signed in" and "in a couple" are
/// genuinely different states. A user who has just registered has no couple yet,
/// and there is no honest value to put here for them — a sentinel Guid would be
/// read as a real couple id by the row-level security policies, which is the one
/// mistake this system cannot make.
/// </summary>
/// <param name="LastSeenAt">
/// When the session was last renewed. Carried here because the alternative is
/// guessing: cookie attributes are not sent back by the browser, so a caller with
/// only the token has no way to tell a session renewed a minute ago from one
/// renewed a month ago, and rolling expiry would quietly stop rolling.
/// </param>
public sealed record AuthenticatedUser(
    Guid SessionId,
    Guid UserId,
    Guid? CoupleId,
    DateTimeOffset LastSeenAt);

public interface ISessionStore
{
    /// <summary>Creates a session and returns nothing — the caller already holds the plaintext token it hashed.</summary>
    Task CreateAsync(
        Guid userId,
        byte[] tokenHash,
        DateTimeOffset expiresAt,
        string? userAgent,
        IPAddress? ip,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a session token, or returns null if it is unknown, expired or
    /// revoked. As with <see cref="IAuthTokenStore.ConsumeAsync"/> the three are
    /// not distinguished, because nothing good comes of telling a caller which.
    /// </summary>
    Task<AuthenticatedUser?> ResolveAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends a session's expiry and updates <c>last_seen_at</c>. Called only
    /// when the session is older than the renewal interval, so this is not a
    /// write on every request — see <see cref="IdentityOptions.SessionRenewalInterval"/>.
    /// </summary>
    Task RenewAsync(
        Guid sessionId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes one session. Sign out on this device.</summary>
    Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Revokes every live session for a user. Sign out everywhere, which ADR 0007 wants to be a single statement.</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
