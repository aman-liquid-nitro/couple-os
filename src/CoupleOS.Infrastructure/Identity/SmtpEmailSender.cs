using System.Net.Mail;
using CoupleOS.Application.Identity;

namespace CoupleOS.Infrastructure.Identity;

public sealed class SmtpOptions
{
    public string Host { get; init; } = "localhost";

    /// <summary>1025 is maildev's SMTP port, which is what docker-compose points this at.</summary>
    public int Port { get; init; } = 1025;

    public string FromAddress { get; init; } = "no-reply@coupleos.local";

    public string FromName { get; init; } = "Couple OS";
}

/// <summary>
/// Plain SMTP with no credentials and no TLS, which is correct for maildev and
/// would be wrong for anything else. A real provider means adding both here, and
/// the reason this class is small is so that change stays small.
/// </summary>
public sealed class SmtpEmailSender(SmtpOptions options) : IEmailSender
{
    public async Task SendAsync(OutboundEmail email, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient(options.Host, options.Port);
        using var message = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName),
            Subject = email.Subject,
            Body = email.Body,
            IsBodyHtml = false,
        };

        message.To.Add(email.To);

        // SmtpClient has no cancellable send, so cancellation is observed before
        // the call rather than during it. Saying that plainly beats a token
        // parameter that silently does nothing.
        cancellationToken.ThrowIfCancellationRequested();

        await client.SendMailAsync(message, cancellationToken);
    }
}
