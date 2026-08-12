using CoupleOS.Application.Identity;
using Microsoft.Extensions.Logging;

namespace CoupleOS.Infrastructure.Identity;

/// <summary>
/// Writes every message to the log, then tries to deliver it, and does not fail
/// sign-in if delivery does not work.
///
/// ADR 0007: "In development, links are written to the console and to a local
/// maildev container. No email provider is required to run the project locally."
/// The second sentence is the one with teeth — if a missing maildev threw, the
/// project would require a mail container to sign in, and the log line would never
/// be reached by the person who needs it.
///
/// Registered only outside production. In production a send that fails has to fail
/// loudly, because there is no log for the user to read.
/// </summary>
public sealed class DevelopmentEmailSender(IEmailSender inner, ILogger<DevelopmentEmailSender> logger)
    : IEmailSender
{
    public async Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        // The body carries the link, so this is the console fallback the ADR asks
        // for. Logged before the attempt, so it survives the attempt failing.
        logger.LogInformation(
            "Development mail to {Recipient} — {Subject}\n{Body}",
            email.To,
            email.Subject,
            email.Body);

        try
        {
            await inner.SendAsync(email, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Could not deliver development mail to {Recipient}. The link above still works — " +
                "maildev is at http://localhost:1080 when the compose stack is running.",
                email.To);
        }
    }
}
