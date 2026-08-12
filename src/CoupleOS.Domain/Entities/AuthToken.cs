using System.Net;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// One issued magic link. The plaintext token is never stored — it exists in the
/// email and nowhere else (ADR 0007), so this row cannot be used to sign anyone
/// in even by whoever holds the database.
///
/// Rows are kept after consumption rather than deleted, for two reasons: the
/// rate limiter counts them, and a replayed link has to be distinguishable from
/// a link that never existed *internally* while being indistinguishable
/// *externally*. A deleted row loses both.
/// </summary>
public sealed class AuthToken
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// The address the link was sent to, held here rather than as a user
    /// reference on purpose: a link can be issued for an address with no user,
    /// which is what makes registration and enumeration-resistance the same code
    /// path rather than two branches with different timing.
    /// </summary>
    public required string Email { get; init; }

    /// <summary>SHA-256 of the plaintext token. Unique, so a hash collision is a constraint violation rather than a shared session.</summary>
    public required byte[] TokenHash { get; init; }

    public required MagicLinkPurpose Purpose { get; init; }

    /// <summary>Set only for <see cref="MagicLinkPurpose.CoupleInvite"/> — the couple the link grants entry to.</summary>
    public Guid? CoupleId { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Null until used. Consumption is a conditional UPDATE rather than a
    /// read-then-write, so this is only ever observed, never assigned here.
    /// </summary>
    public DateTimeOffset? ConsumedAt { get; init; }

    public IPAddress? CreatedIp { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
