using CoupleOS.Application.Attachments;
using CoupleOS.Application.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages.Attachments;

/// <summary>
/// Hands back a file the caller is permitted to see, and cannot be persuaded to
/// do anything else.
///
/// <para><b>Authorization is not written here.</b> The lookup runs inside a
/// couple-scoped transaction, so an attachment belonging to another couple — or
/// to the partner's private thread — produces no row, and no row is a 404. Not a
/// 403: a refusal tells the person there is something there, which is the hint
/// <c>search_memory</c> is carefully worded to avoid, and the same rule applies
/// to a URL somebody guessed (ADR 0005).</para>
///
/// <para><b>Nothing is served with a Content-Type the uploader chose.</b> The
/// column records what the browser claimed because that is worth knowing, and
/// this ignores it. A private-data application that serves user-uploaded files
/// inline with an attacker-chosen type is serving an XSS vector to the one
/// session that can read everything the couple has ever written
/// (ADR 0014).</para>
/// </summary>
public sealed class DownloadModel(
    IAttachments attachments,
    IAttachmentStore store,
    IScopedUnitOfWork unitOfWork) : PageModel
{
    private readonly IAttachments _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
    private readonly IAttachmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Domain.Entities.Attachment? attachment;

        await using (var transaction = await _unitOfWork.BeginAsync(cancellationToken))
        {
            // Every read needs a scope, and this one needs it most: without the
            // transaction there is no set_config, and the row-level security
            // policy would return nothing for everybody — which would look like a
            // broken feature rather than like the leak its absence prevents.
            attachment = await _attachments.FindAsync(id, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        if (attachment is null)
        {
            return NotFound();
        }

        var content = await _store.OpenAsync(attachment.StorageKey, cancellationToken);

        if (content is null)
        {
            // The row exists and the bytes do not. ADR 0014 records that the
            // bytes are written first precisely so this is the rare direction,
            // and it is reported as a 404 rather than a 500 because from the
            // person's side the file is simply not there.
            return NotFound();
        }

        // nosniff, so a browser cannot decide for itself that octet-stream was
        // really HTML. Without it the conservative Content-Type above is advice
        // rather than a decision.
        Response.Headers.XContentTypeOptions = "nosniff";

        return File(content, "application/octet-stream", fileDownloadName: attachment.Filename);
    }
}
