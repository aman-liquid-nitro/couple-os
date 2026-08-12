using System.Net;
using CoupleOS.Application.Identity;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Identity;

public sealed class AuthTokenStore(IdentityDbContext db, TimeProvider clock) : IAuthTokenStore
{
    public async Task IssueAsync(
        string email,
        byte[] tokenHash,
        MagicLinkPurpose purpose,
        Guid? coupleId,
        DateTimeOffset expiresAt,
        IPAddress? ip,
        CancellationToken cancellationToken = default)
    {
        db.AuthTokens.Add(new AuthToken
        {
            Email = email,
            TokenHash = tokenHash,
            Purpose = purpose,
            CoupleId = coupleId,
            ExpiresAt = expiresAt,
            CreatedIp = ip,

            // Set here rather than left to the column's DEFAULT now(). The column
            // is mapped, so EF would send default(DateTimeOffset) and write year 1
            // instead — and the rate limiter counts on this column, so every
            // window would be permanently empty and every limit would never fire.
            CreatedAt = clock.GetUtcNow(),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ConsumedToken?> ConsumeAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        // The claim is this single UPDATE. Two requests arriving together both run
        // it, PostgreSQL serialises them on the row, and the second sees
        // consumed_at already set and matches nothing — so exactly one caller gets
        // a 1 back, however many arrive.
        var claimed = await db.AuthTokens
            .Where(t => t.TokenHash == tokenHash && t.ConsumedAt == null && t.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, (DateTimeOffset?)now), cancellationToken);

        if (claimed == 0)
        {
            return null;
        }

        // Safe to read as a second statement: the UPDATE above committed, and it is
        // what makes this row unclaimable by anyone else. Nobody can be racing us
        // for it any more, because losing that race is what returning 0 means.
        var token = await db.AuthTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Select(t => new ConsumedToken(t.Id, t.Email, t.Purpose, t.CoupleId))
            .SingleAsync(cancellationToken);

        return token;
    }

    public Task<int> CountRecentForEmailAsync(
        string email,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        // lower() on both sides, matching auth_tokens_email_created, which is an
        // index on lower(email) — comparing the raw column would not use it and
        // would miss rows differing only in case.
        var normalized = email.ToLowerInvariant();

        return db.AuthTokens
            .Where(t => t.Email.ToLower() == normalized && t.CreatedAt > since)
            .CountAsync(cancellationToken);
    }

    public Task<int> CountRecentForIpAsync(
        IPAddress ip,
        DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        db.AuthTokens
            .Where(t => t.CreatedIp != null && t.CreatedIp.Equals(ip) && t.CreatedAt > since)
            .CountAsync(cancellationToken);
}
