using System.Net;

namespace CoupleOS.Application.Identity;

/// <summary>
/// Turns a plaintext token into the URL that carries it. Implemented in the web
/// layer, which is the only part that knows the base address and the route.
/// </summary>
public interface IMagicLinkUrlFactory
{
    string CreateUrl(string token);
}

public enum LinkRequestOutcome
{
    /// <summary>A link was issued and handed to the mail sender.</summary>
    LinkSent,

    /// <summary>
    /// A rate limit refused it. Safe to show, and worth showing: the counters are
    /// keyed on the address as typed and on the caller's IP, neither of which
    /// depends on whether an account exists, so saying so reveals nothing that
    /// "check your email" would have hidden. Staying silent would only mean the
    /// person waits for mail that is never coming.
    /// </summary>
    RateLimited,
}

/// <summary>Everything a caller needs after a link is consumed, and nothing about why one was not.</summary>
public sealed record SignedIn(string SessionToken, Guid UserId, Guid? CoupleId);

public interface IMagicLinkService
{
    Task<LinkRequestOutcome> RequestSignInAsync(
        string email,
        IPAddress? ip,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invites an address to join a couple. Same token mechanism as sign-in, per
    /// ADR 0007 — the invite is a magic link carrying a couple-join claim, so
    /// there is one authentication path rather than two.
    /// </summary>
    Task<LinkRequestOutcome> InviteToCoupleAsync(
        string email,
        Guid coupleId,
        string? invitedByDisplayName,
        IPAddress? ip,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes a link and establishes a session, or returns null.
    ///
    /// Null covers unknown, expired, already used and — for an invitation — a
    /// couple that has since filled up. Collapsing them is the point: ADR 0007
    /// requires replay to be indistinguishable from expiry, and the cheapest way
    /// to keep that true is for the caller to have nothing else to report.
    /// </summary>
    Task<SignedIn?> ConsumeAsync(
        string token,
        string? userAgent,
        IPAddress? ip,
        CancellationToken cancellationToken = default);
}
