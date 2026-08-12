using System.Net;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Identity;

/// <summary>
/// What a successful consume yields. Note what is absent: any indication of
/// <em>why</em> a consume failed.
///
/// ADR 0007 requires a replayed link to fail identically to an expired one. That
/// is easy to write and easy to erode later — someone adds a
/// <c>TokenStatus.AlreadyUsed</c> for a better error message and the guarantee is
/// gone. Modelling failure as the absence of a result means the caller has nothing
/// to leak, so the property survives people who have not read the ADR.
/// </summary>
public sealed record ConsumedToken(
    Guid TokenId,
    string Email,
    MagicLinkPurpose Purpose,
    Guid? CoupleId);

public interface IAuthTokenStore
{
    /// <summary>Records an issued link. Only the hash is stored.</summary>
    Task IssueAsync(
        string email,
        byte[] tokenHash,
        MagicLinkPurpose purpose,
        Guid? coupleId,
        DateTimeOffset expiresAt,
        IPAddress? ip,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a token used and returns it, or returns null.
    ///
    /// Must be one statement. A read-then-write lets two requests arriving
    /// together both see <c>consumed_at IS NULL</c> and both succeed, which turns
    /// "exactly one use" into "one use per concurrent request" — and a stolen link
    /// raced against its owner is precisely the case the single-use rule exists
    /// for.
    /// </summary>
    Task<ConsumedToken?> ConsumeAsync(
        byte[] tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>Links issued to this address since <paramref name="since"/>, matched case-insensitively as the unique index is.</summary>
    Task<int> CountRecentForEmailAsync(
        string email,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);

    /// <summary>Links issued from this address since <paramref name="since"/>. Counts every purpose, so an invitation spree is throttled too.</summary>
    Task<int> CountRecentForIpAsync(
        IPAddress ip,
        DateTimeOffset since,
        CancellationToken cancellationToken = default);
}
