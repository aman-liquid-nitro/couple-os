using System.Net;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Identity;

/// <summary>
/// The sign-in flow. Orchestrates only: every secret is made by
/// <see cref="SecretToken"/>, every row is written by a store, and every
/// single-use guarantee is a database constraint rather than a check in here.
/// </summary>
public sealed class MagicLinkService(
    IAuthTokenStore tokens,
    ISessionStore sessions,
    IUserDirectory users,
    IEmailSender email,
    IMagicLinkUrlFactory urls,
    IdentityOptions options,
    TimeProvider clock) : IMagicLinkService
{
    public Task<LinkRequestOutcome> RequestSignInAsync(
        string email,
        IPAddress? ip,
        CancellationToken cancellationToken = default) =>
        IssueAsync(email, MagicLinkPurpose.SignIn, coupleId: null, invitedBy: null, ip, cancellationToken);

    public Task<LinkRequestOutcome> InviteToCoupleAsync(
        string email,
        Guid coupleId,
        string? invitedByDisplayName,
        IPAddress? ip,
        CancellationToken cancellationToken = default) =>
        IssueAsync(email, MagicLinkPurpose.CoupleInvite, coupleId, invitedByDisplayName, ip, cancellationToken);

    private async Task<LinkRequestOutcome> IssueAsync(
        string address,
        MagicLinkPurpose purpose,
        Guid? coupleId,
        string? invitedBy,
        IPAddress? ip,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var normalized = address.Trim();
        var now = clock.GetUtcNow();

        if (await IsRateLimitedAsync(normalized, ip, now, cancellationToken))
        {
            return LinkRequestOutcome.RateLimited;
        }

        // No branch on whether the address is known, and that is the whole
        // enumeration defence. Registration happens on consume, so an unknown
        // address takes the same path, does the same work, sends the same mail and
        // returns the same answer as a known one. There is no timing difference to
        // measure because there are no two paths.
        var token = SecretToken.Create();
        var expiresAt = now + options.MagicLinkLifetime;

        await tokens.IssueAsync(
            normalized,
            SecretToken.Hash(token),
            purpose,
            coupleId,
            expiresAt,
            ip,
            cancellationToken);

        await email.SendAsync(
            MagicLinkEmail.For(purpose, normalized, urls.CreateUrl(token), invitedBy, options.MagicLinkLifetime),
            cancellationToken);

        return LinkRequestOutcome.LinkSent;
    }

    private async Task<bool> IsRateLimitedAsync(
        string address,
        IPAddress? ip,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var perEmail = await tokens.CountRecentForEmailAsync(
            address, now - options.PerEmailWindow, cancellationToken);

        if (perEmail >= options.MaxLinksPerEmail)
        {
            return true;
        }

        // A missing IP is not treated as an exemption, but it cannot be counted
        // either — every caller without one would share a single bucket and
        // throttle each other. In practice it is absent only for requests that
        // did not arrive over the network.
        if (ip is null)
        {
            return false;
        }

        var perIp = await tokens.CountRecentForIpAsync(ip, now - options.PerIpWindow, cancellationToken);

        return perIp >= options.MaxLinksPerIp;
    }

    public async Task<SignedIn?> ConsumeAsync(
        string token,
        string? userAgent,
        IPAddress? ip,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // One statement decides it. Everything after this point is reached by at
        // most one caller for a given link, however many arrive together.
        var consumed = await tokens.ConsumeAsync(SecretToken.Hash(token), cancellationToken);
        if (consumed is null)
        {
            return null;
        }

        var userId = await users.GetOrCreateUserAsync(consumed.Email, cancellationToken);

        var coupleId = consumed.Purpose == MagicLinkPurpose.CoupleInvite && consumed.CoupleId is { } invitedTo
            ? await AcceptInvitationAsync(userId, invitedTo, cancellationToken)
            : await users.FindCoupleIdForUserAsync(userId, cancellationToken);

        var sessionToken = SecretToken.Create();

        await sessions.CreateAsync(
            userId,
            SecretToken.Hash(sessionToken),
            clock.GetUtcNow() + options.SessionLifetime,
            userAgent,
            ip,
            cancellationToken);

        return new SignedIn(sessionToken, userId, coupleId);
    }

    /// <summary>
    /// Accepts an invitation, and signs the person in either way.
    ///
    /// A full couple or a user who already belongs to one does not fail the
    /// sign-in: the token was valid and single-use, so refusing here would consume
    /// the link and leave them with nothing. They are signed in with whatever
    /// couple they actually have, which for the already-in-a-couple case is the
    /// right couple, and for the full-couple case is none — a state the pages
    /// handle because a freshly registered user is in it too.
    /// </summary>
    private async Task<Guid?> AcceptInvitationAsync(
        Guid userId,
        Guid coupleId,
        CancellationToken cancellationToken)
    {
        var outcome = await users.JoinCoupleAsync(userId, coupleId, cancellationToken);

        return outcome == JoinCoupleOutcome.Joined
            ? coupleId
            : await users.FindCoupleIdForUserAsync(userId, cancellationToken);
    }
}
