using CoupleOS.Api.Identity;
using CoupleOS.Application.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages.Couple;

public sealed class CreateModel(CurrentSession currentSession, IUserDirectory users) : PageModel
{
    [BindProperty]
    public string? DisplayName { get; set; }

    public IActionResult OnGet() =>
        // Reachable while signed in but couple-less. Someone who already has one
        // arriving here would otherwise be offered a second, which the unique index
        // on couple_members.user_id would refuse at the last moment.
        currentSession.HasCouple ? Redirect("/") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (currentSession.User is not { } user)
        {
            return Redirect("/SignIn");
        }

        if (currentSession.HasCouple)
        {
            return Redirect("/");
        }

        await users.CreateCoupleAsync(user.UserId, DisplayName, cancellationToken);

        // Redirect rather than render: the couple scope for this request was
        // established by the middleware before the couple existed, so nothing on a
        // page rendered now would be able to read the couple's rows. The next
        // request resolves the session again and gets a scope.
        return Redirect("/Couple/Invite");
    }
}
