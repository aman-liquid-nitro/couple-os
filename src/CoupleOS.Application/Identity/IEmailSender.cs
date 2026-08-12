namespace CoupleOS.Application.Identity;

public sealed record OutboundEmail(string To, string Subject, string Body);

/// <summary>
/// Sending mail is the one hard external dependency ADR 0007 accepts: with no
/// passwords, a provider outage locks both users out. It is behind an interface so
/// that stays a deployment concern rather than something the sign-in flow knows
/// about.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default);
}
