using CoupleOS.Application.Conversation;
using CoupleOS.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

/// <summary>
/// ADR 0009's private surface: one thread, one partner, one message at a time.
///
/// There is no Process button and no version, and both absences are the design.
/// A conversation has nobody else editing it, so there is no stale copy to
/// detect; and it acts as you speak, so there is nothing to defer. The shared
/// page needed both for the opposite reasons.
/// </summary>
public sealed class PrivateModel(IPrivateThread thread) : PageModel
{
    private readonly IPrivateThread _thread = thread ?? throw new ArgumentNullException(nameof(thread));

    /// <summary>
    /// What the person is typing. Named Said rather than Message because
    /// PageModel has no Message member today and might tomorrow — the same trap
    /// IndexModel.Text sidesteps.
    /// </summary>
    [BindProperty]
    public string? Said { get; set; }

    public IReadOnlyList<ConversationMessage> History { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        History = await _thread.ReadAsync(cancellationToken);

    public async Task<IActionResult> OnPostSayAsync(CancellationToken cancellationToken)
    {
        // A blank send is not an error and not an event, exactly as a blank
        // quick-add is not. Nothing is appended and nothing is swapped, rather
        // than a turn recorded for a stray press of Enter.
        if (string.IsNullOrWhiteSpace(Said))
        {
            return new EmptyResult();
        }

        var turn = await _thread.SayAsync(Said, cancellationToken);

        return Partial("_Turn", turn);
    }
}
