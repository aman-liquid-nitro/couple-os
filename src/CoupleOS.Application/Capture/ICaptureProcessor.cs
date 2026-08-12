namespace CoupleOS.Application.Capture;

public interface ICaptureProcessor
{
    /// <summary>
    /// Turns a note into records, and reports exactly what it did.
    /// Never throws for a model that behaves badly — that is a report, not an
    /// exception.
    /// </summary>
    Task<CaptureReport> ProcessAsync(
        string text,
        CaptureSurface surface,
        CancellationToken cancellationToken = default);
}
