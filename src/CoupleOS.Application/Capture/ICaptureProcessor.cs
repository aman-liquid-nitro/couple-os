namespace CoupleOS.Application.Capture;

public interface ICaptureProcessor
{
    /// <summary>
    /// Runs the couple's shared file: blocks in, records out, and an account of
    /// every block either way.
    ///
    /// It takes no text and no surface. The text is whatever the file holds —
    /// passing it in would let a caller process something the file does not
    /// contain — and the visibility of everything written comes from the block,
    /// which inherited it from the file (ADR 0009). A parameter for either would
    /// be a parameter someone could get wrong.
    ///
    /// Never throws for a model that behaves badly, or for one block that fails.
    /// Both are reports.
    /// </summary>
    Task<CaptureReport> ProcessAsync(CancellationToken cancellationToken = default);
}
