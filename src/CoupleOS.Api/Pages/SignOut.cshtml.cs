using CoupleOS.Api.Identity;
using CoupleOS.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

/// <summary>
/// Named <c>SignOutPageModel</c> rather than <c>SignOutModel</c> because
/// <see cref="PageModel"/> already has a <c>SignOut</c> member, and the collision
/// is the kind that produces an error pointing at the wrong thing.
/// </summary>
public sealed class SignOutPageModel(CurrentSession currentSession, ISessionStore sessions) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (currentSession.User is { } user)
        {
            await sessions.RevokeAsync(user.SessionId, cancellationToken);
        }

        return ClearAndReturnToSignIn();
    }

    public async Task<IActionResult> OnPostEverywhereAsync(CancellationToken cancellationToken)
    {
        if (currentSession.User is { } user)
        {
            await sessions.RevokeAllForUserAsync(user.UserId, cancellationToken);
        }

        return ClearAndReturnToSignIn();
    }

    /// <summary>
    /// The cookie is cleared whether or not a session was found. If the row was
    /// already gone, leaving the cookie in place means every later request presents
    /// a dead token and gets redirected — which reads as "sign out did nothing".
    /// </summary>
    private IActionResult ClearAndReturnToSignIn()
    {
        Response.Cookies.Delete(SessionCookie.Name, SessionCookie.DeletionOptions());

        return RedirectToPage("/SignIn");
    }
}
