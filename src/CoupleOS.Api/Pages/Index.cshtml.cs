using CoupleOS.Application.Capture;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

public sealed class IndexModel(ICaptureProcessor captureProcessor) : PageModel
{
    private readonly ICaptureProcessor _captureProcessor = captureProcessor
        ?? throw new ArgumentNullException(nameof(captureProcessor));

    [BindProperty]
    public string? Text { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostProcessAsync(CancellationToken cancellationToken)
    {
        // The surface is stated here, not inferred. This page IS shared.md, so
        // everything captured through it is shared_couple (ADR 0009). The
        // private thread will pass PrivateThread and share every other line of
        // this pipeline.
        var report = await _captureProcessor.ProcessAsync(
            Text ?? string.Empty,
            CaptureSurface.SharedFile,
            cancellationToken);

        return Partial("_ChangeReport", report);
    }
}
