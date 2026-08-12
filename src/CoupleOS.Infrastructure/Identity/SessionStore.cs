using System.Net;
using CoupleOS.Application.Identity;
using CoupleOS.Domain.Entities;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Identity;

public sealed class SessionStore(IdentityDbContext db, TimeProvider clock) : ISessionStore
{
    public async Task CreateAsync(
        Guid userId,
        byte[] tokenHash,
        DateTimeOffset expiresAt,
        string? userAgent,
        IPAddress? ip,
        CancellationToken cancellationToken = default)
    {
        db.Sessions.Add(new Session
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
            UserAgent = Truncate(userAgent, 512),
            Ip = ip,
            LastSeenAt = clock.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// User-Agent is attacker-controlled and unbounded, and the column is
    /// unbounded <c>text</c>. Nothing breaks at 40 kB, but nothing is gained by
    /// storing it either, and the value is only ever read by a person looking at
    /// their own session list.
    /// </summary>
    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    public async Task<AuthenticatedUser?> ResolveAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        // The couple is fetched in the same round trip, because every
        // authenticated request needs it to set app.current_couple_id — splitting
        // it would double the query count on the hottest path in the application.
        // Left join semantics via a subquery: no couple is a legitimate state for a
        // user who has registered but not yet created or joined one.
        var resolved = await db.Sessions
            .AsNoTracking()
            .Where(s => s.TokenHash == tokenHash && s.RevokedAt == null && s.ExpiresAt > now)
            .Select(s => new
            {
                s.Id,
                s.UserId,
                s.LastSeenAt,
                CoupleId = db.CoupleMembers
                    .Where(m => m.UserId == s.UserId)
                    .Select(m => (Guid?)m.CoupleId)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return resolved is null
            ? null
            : new AuthenticatedUser(resolved.Id, resolved.UserId, resolved.CoupleId, resolved.LastSeenAt);
    }

    public async Task RenewAsync(
        Guid sessionId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        // Still filtered on revoked_at: a session revoked between being resolved
        // and being renewed must not have its life extended by the same request
        // that was told it was valid.
        await db.Sessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ExpiresAt, expiresAt)
                      .SetProperty(x => x.LastSeenAt, now),
                cancellationToken);
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        await db.Sessions
            .Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), cancellationToken);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        // One statement, which is what ADR 0007 means by revocation being a table
        // rather than a token property. Returns the count so the caller can say
        // how many devices were signed out.
        return await db.Sessions
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, (DateTimeOffset?)now), cancellationToken);
    }
}
