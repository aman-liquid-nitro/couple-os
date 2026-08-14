using System.Text.Json;
using CoupleOS.Application.Attachments;
using CoupleOS.Application.Conversation;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
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
public sealed class PrivateModel(IPrivateThread thread, IAttachmentIntake attachments) : PageModel
{
    private readonly IPrivateThread _thread = thread ?? throw new ArgumentNullException(nameof(thread));

    private readonly IAttachmentIntake _attachments = attachments
        ?? throw new ArgumentNullException(nameof(attachments));

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

    /// <summary>
    /// The same upload, on the surface that makes it private.
    ///
    /// Both surfaces have to receive files or "attachments inherit their
    /// surface's scope" is a property of one surface and a sentence about the
    /// other. Nothing here decides the scope — <c>PrivateUser</c> is passed
    /// because this is the private page, which is the whole of ADR 0009's rule
    /// and the same line the shared page has with the other value.
    ///
    /// The link goes into the message box rather than into a file, because there
    /// is no file: a conversation's unit of input is the message, so a receipt
    /// belongs in the sentence the person is about to send.
    /// </summary>
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? upload, CancellationToken cancellationToken)
    {
        if (upload is null || upload.Length == 0)
        {
            return Partial("_UploadResult", AttachmentUpload.Refused("No file arrived."));
        }

        await using var content = upload.OpenReadStream();

        var result = await _attachments.ReceiveAsync(
            upload.FileName,
            upload.ContentType,
            content,
            Visibility.PrivateUser,
            dumpFileId: null,
            cancellationToken);

        if (result.Accepted)
        {
            Response.Headers["HX-Trigger-After-Swap"] = JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["coupleos:attached"] = new { markdown = result.Markdown },
                });
        }

        return Partial("_UploadResult", result);
    }
}
