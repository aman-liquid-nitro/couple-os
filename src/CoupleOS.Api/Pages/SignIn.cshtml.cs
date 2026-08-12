using System.ComponentModel.DataAnnotations;
using CoupleOS.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

public sealed class SignInModel(IMagicLinkService magicLinks, IdentityOptions options) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string? Email { get; set; }

    /// <summary>
    /// Set by the redirect after a successful POST rather than by rendering the
    /// confirmation from the POST itself, so a refresh does not re-request a link
    /// and eat one of the three the rate limiter allows.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public bool LinkSent { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RateLimited { get; set; }

    public bool InvalidAddress { get; private set; }

    public string LifetimeDescription =>
        $"{options.MagicLinkLifetime.TotalMinutes:0} minutes";

    /// <summary>
    /// Carried through so a person who followed a link to a page, got redirected
    /// here, and signed in lands where they were going. Validated as a local path
    /// on the way out, never trusted as given.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnTo { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Email))
        {
            InvalidAddress = true;
            return Page();
        }

        var outcome = await magicLinks.RequestSignInAsync(
            Email,
            HttpContext.Connection.RemoteIpAddress,
            cancellationToken);

        // Both branches redirect, and neither says anything about whether the
        // address was known — the service does not tell this page, because there is
        // nothing for it to tell (ADR 0007).
        return outcome switch
        {
            LinkRequestOutcome.RateLimited => RedirectToPage(new { RateLimited = true, ReturnTo }),
            _ => RedirectToPage(new { LinkSent = true, ReturnTo }),
        };
    }
}
