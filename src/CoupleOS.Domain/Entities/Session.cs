using System.Net;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// One signed-in browser. As with <see cref="AuthToken"/> only the hash is
/// stored, so the cookie value cannot be reconstructed from the database.
///
/// Revocation is a column rather than a delete, which is what makes "sign out
/// everywhere" a single UPDATE and leaves a record that a session existed.
/// </summary>
public sealed class Session
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid UserId { get; init; }

    /// <summary>SHA-256 of the cookie value.</summary>
    public required byte[] TokenHash { get; init; }

    /// <summary>
    /// Rolling: extended on use rather than fixed at issue, so an active browser
    /// is not signed out on the thirtieth day (ADR 0007).
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public string? UserAgent { get; init; }

    public IPAddress? Ip { get; init; }
}
