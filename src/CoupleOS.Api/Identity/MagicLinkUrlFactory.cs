using CoupleOS.Application.Identity;

namespace CoupleOS.Api.Identity;

/// <summary>
/// Builds the absolute URL that carries a magic link token.
///
/// Absolute because it goes in an email, and the base has to come from the request
/// rather than from configuration if the project is to run on localhost, in a
/// container, and behind a domain without being told which. Configuration wins when
/// it is set, because a request's own host header is attacker-controlled: without an
/// override, a request forged with someone else's Host would mint links pointing at
/// that host, and the recipient would hand their token to it.
/// </summary>
public sealed class MagicLinkUrlFactory(
    IHttpContextAccessor accessor,
    IConfiguration configuration) : IMagicLinkUrlFactory
{
    public string CreateUrl(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var configured = configuration["PublicBaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : FromRequest();

        return $"{baseUrl}/auth/callback?token={Uri.EscapeDataString(token)}";
    }

    private string FromRequest()
    {
        var request = accessor.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "A magic link URL was requested outside a request, and PublicBaseUrl is not " +
                "configured. Set PublicBaseUrl for anything that issues links from a background task.");

        return $"{request.Scheme}://{request.Host}";
    }
}
