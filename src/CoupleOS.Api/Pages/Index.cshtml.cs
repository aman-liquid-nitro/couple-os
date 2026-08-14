using System.Text.Json;
using CoupleOS.Application.Attachments;
using CoupleOS.Application.Capture;
using CoupleOS.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

/// <summary>
/// What one press of Process produced, and the file it left behind.
///
/// All three, because the run writes the file twice: once with the user's own
/// text, and again with the settled blocks moved into the archive. A response
/// that rendered only the report would leave the page holding the text from
/// before the rewrite, at a version that no longer exists — so the next Save
/// would either be refused, or replace the archive with the pre-run inbox.
/// </summary>
/// <param name="File">
/// The file as it stands after the run, which is not <c>Save.File</c> whenever
/// the rewrite moved anything.
/// </param>
public sealed record ProcessResult(SharedFileSave Save, CaptureReport? Report, SharedFileView File);

public sealed class IndexModel(
    ISharedFileEditor sharedFile,
    ICaptureProcessor captureProcessor,
    IAttachmentIntake attachments) : PageModel
{
    private readonly ISharedFileEditor _sharedFile = sharedFile
        ?? throw new ArgumentNullException(nameof(sharedFile));

    private readonly ICaptureProcessor _captureProcessor = captureProcessor
        ?? throw new ArgumentNullException(nameof(captureProcessor));

    private readonly IAttachmentIntake _attachments = attachments
        ?? throw new ArgumentNullException(nameof(attachments));

    /// <summary>
    /// The editor's text. Named Text rather than Content because PageModel
    /// already has a Content(string) helper, and a property that hides a base
    /// member is a trap for whoever writes the next page.
    /// </summary>
    [BindProperty]
    public string? Text { get; set; }

    /// <summary>
    /// The version the browser was shown, sent back with every write so the
    /// database can tell whether it is still current. Round-tripped through a
    /// hidden field rather than re-read on the server, because a server-side
    /// re-read would always match itself and check nothing.
    /// </summary>
    [BindProperty]
    public int Version { get; set; }

    /// <summary>
    /// The quick-add box. Carries no version, by design — an append cannot
    /// conflict with anything, so there is nothing for the browser to hold on to
    /// between one thought and the next (ADR 0009).
    /// </summary>
    [BindProperty]
    public string? Line { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var file = await _sharedFile.ReadAsync(cancellationToken);

        Text = file.Content;
        Version = file.Version;
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken)
    {
        var save = await _sharedFile.SaveAsync(Text ?? string.Empty, Version, cancellationToken);

        return Partial("_SaveState", save);
    }

    /// <summary>
    /// One line in, nothing to read back. The response exists only to keep the
    /// editor on the same screen honest: the file now has a line the textarea does
    /// not, and a Save from that stale box would delete it.
    /// </summary>
    public async Task<IActionResult> OnPostQuickAddAsync(CancellationToken cancellationToken)
    {
        var file = await _sharedFile.QuickAddAsync(Line, cancellationToken);

        // A blank box is not an error and not an event. Nothing was written, so
        // nothing needs swapping — and re-reading the file to say so would be a
        // query per stray keypress on the Enter key.
        return file is null ? new EmptyResult() : Partial("_QuickAddResult", file);
    }

    /// <summary>
    /// Takes the file and hands back the link, and deliberately does not touch
    /// the couple's text.
    ///
    /// The obvious implementation appends the markdown to the file the way
    /// quick-add does, and it is wrong for the same reason quick-add is right:
    /// quick-add is a whole thought, and a receipt is evidence for a line
    /// somebody is in the middle of writing. Appending it would put the link at
    /// the end of the document, in a block of its own, which produces no tool
    /// call and therefore links to nothing (V0_SCOPE.md wants it linked to
    /// "whatever records the surrounding block produced"). So the markdown goes
    /// back to the browser, which knows where the caret is.
    ///
    /// Nothing is saved here either. The textarea is unsaved-by-nature between
    /// keystrokes, and a save from this handler would write text the person is
    /// still typing at a version they are still holding.
    /// </summary>
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? upload, CancellationToken cancellationToken)
    {
        if (upload is null || upload.Length == 0)
        {
            return Partial("_UploadResult", AttachmentUpload.Refused("No file arrived."));
        }

        await using var content = upload.OpenReadStream();

        // Shared, because this is the shared file. Never from the file's name or
        // its contents — ADR 0009 makes the surface the thing that decides, and a
        // receipt for a surprise uploaded here is shared because the person chose
        // this page, which is the same rule that keeps the system from relocating
        // their words.
        var result = await _attachments.ReceiveAsync(
            upload.FileName,
            upload.ContentType,
            content,
            Visibility.SharedCouple,
            dumpFileId: null,
            cancellationToken);

        if (result.Accepted)
        {
            // Handed to the page as an event rather than as markup. A <script>
            // swapped into the DOM runs once and stays there, so after four
            // uploads four of them are waiting to run again.
            Response.Headers["HX-Trigger-After-Swap"] = JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["coupleos:attached"] = new { markdown = result.Markdown },
                });
        }

        return Partial("_UploadResult", result);
    }

    public async Task<IActionResult> OnPostProcessAsync(CancellationToken cancellationToken)
    {
        // Save first. The file is the input, so processing text the file does not
        // contain would report on something nobody could go back and read — and
        // once intake reads blocks out of the row rather than out of this POST,
        // an unsaved Process would silently process the previous save.
        var save = await _sharedFile.SaveAsync(Text ?? string.Empty, Version, cancellationToken);

        if (!save.Accepted)
        {
            // Nothing was written, so there is nothing new to process. Running
            // the model over the partner's text instead would attribute their
            // words to this person and report changes they did not ask for.
            return Partial("_ProcessResult", new ProcessResult(save, Report: null, save.File));
        }

        // No text and no surface passed: the run reads the file it just wrote,
        // and the visibility of everything it creates comes from the file rather
        // than from this page's opinion of it (ADR 0009).
        var report = await _captureProcessor.ProcessAsync(cancellationToken);

        // Re-read rather than reconstruct. The rewrite is the run's own decision
        // about what the file should now say, and a second implementation of it
        // here would be a second thing to keep in step.
        var file = report.Rewrite == FileRewriteOutcome.Rewritten
            ? await _sharedFile.ReadAsync(cancellationToken)
            : save.File;

        return Partial("_ProcessResult", new ProcessResult(save, report, file));
    }
}
