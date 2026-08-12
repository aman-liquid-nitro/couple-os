using CoupleOS.Api.Identity;
using CoupleOS.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages.Auth;

/// <summary>
/// Where a magic link lands. One outcome renders this page — failure — and it says
/// the same thing whether the link was expired, already used, or never existed,
/// because the service it calls cannot tell it which (ADR 0007).
/// </summary>
public sealed class CallbackModel(
    IMagicLinkService magicLinks,
    IdentityOptions options,
    TimeProvider clock) : PageModel
{
    public async Task<IActionResult> OnGetAsync(
        string? token,
        string? returnTo,
        CancellationToken cancellationToken)
    {
        var signedIn = await magicLinks.ConsumeAsync(
            token ?? string.Empty,
            Request.Headers.UserAgent.ToString(),
            HttpContext.Connection.RemoteIpAddress,
            cancellationToken);

        if (signedIn is null)
        {
            return Page();
        }

        Response.Cookies.Append(
            SessionCookie.Name,
            signedIn.SessionToken,
            SessionCookie.Options(clock.GetUtcNow() + options.SessionLifetime));

        // A user with no couple is sent to create one. The middleware would do this
        // anyway on the next request; doing it here means one redirect instead of two.
        if (signedIn.CoupleId is null)
        {
            return Redirect("/Couple/Create");
        }

        return Redirect(LocalOrHome(returnTo));
    }

    /// <summary>
    /// Only same-site paths. <c>returnTo</c> arrives in a URL that was emailed, so
    /// treating it as trusted would turn every sign-in link into an open redirect —
    /// a link genuinely from us, landing on a page that is not.
    /// </summary>
    private string LocalOrHome(string? returnTo) =>
        !string.IsNullOrWhiteSpace(returnTo) && Url.IsLocalUrl(returnTo) ? returnTo : "/";
}
