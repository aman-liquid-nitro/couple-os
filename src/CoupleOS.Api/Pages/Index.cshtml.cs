using CoupleOS.Application.Capture;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

/// <summary>
/// What one press of Process produced, and the file's version afterwards.
///
/// Both, because the version input on the page has to move even when the
/// interesting half of the answer is the change report. A response that renders
/// the report and leaves the version behind makes the next press conflict with
/// this one's own write.
/// </summary>
public sealed record ProcessResult(SharedFileSave Save, CaptureReport? Report);

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
            return Partial("_ProcessResult", new ProcessResult(save, Report: null));
        }

        // The surface is stated here, not inferred. This page IS shared.md, so
        // everything captured through it is shared_couple (ADR 0009). The
        // private thread will pass PrivateThread and share every other line of
        // this pipeline.
        var report = await _captureProcessor.ProcessAsync(
            save.File.Content,
            CaptureSurface.SharedFile,
            cancellationToken);

        return Partial("_ProcessResult", new ProcessResult(save, report));
    }
}
