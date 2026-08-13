using CoupleOS.Application.Capture;
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
    ICaptureProcessor captureProcessor) : PageModel
{
    private readonly ISharedFileEditor _sharedFile = sharedFile
        ?? throw new ArgumentNullException(nameof(sharedFile));

    private readonly ICaptureProcessor _captureProcessor = captureProcessor
        ?? throw new ArgumentNullException(nameof(captureProcessor));

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
