using System.ComponentModel.DataAnnotations;
using CoupleOS.Api.Identity;
using CoupleOS.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages.Couple;

public sealed class InviteModel(
    CurrentSession currentSession,
    IMagicLinkService magicLinks,
    IdentityOptions options) : PageModel
{
    [BindProperty]
    [Required]
    [EmailAddress]
    public string? Email { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Sent { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool RateLimited { get; set; }

    public bool InvalidAddress { get; private set; }

    public string LifetimeDescription => $"{options.MagicLinkLifetime.TotalMinutes:0} minutes";

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (currentSession.User?.CoupleId is not { } coupleId)
        {
            return Redirect("/Couple/Create");
        }

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(Email))
        {
            InvalidAddress = true;
            return Page();
        }

        // Whether the couple already has two members is not checked here. The
        // deferred trigger decides it when the invitation is accepted, and by then
        // the answer may have changed — checking now would be a guess with a nicer
        // error message.
        var outcome = await magicLinks.InviteToCoupleAsync(
            Email,
            coupleId,
            invitedByDisplayName: null,
            HttpContext.Connection.RemoteIpAddress,
            cancellationToken);

        return outcome switch
        {
            LinkRequestOutcome.RateLimited => RedirectToPage(new { RateLimited = true }),
            _ => RedirectToPage(new { Sent = true }),
        };
    }
}
