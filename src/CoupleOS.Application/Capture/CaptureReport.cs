using CoupleOS.Application.AI;
using CoupleOS.Application.Tools;

namespace CoupleOS.Application.Capture;

/// <summary>One thing that happened, phrased for a person rather than a log.</summary>
public sealed record CaptureChange(
    string ToolName,
    ToolOutcome Outcome,
    string? EntityType,
    Guid? EntityId,
    string Description);

/// <summary>
/// What a Process run did. Shown to the user immediately, and the reason ADR
/// 0009 chose an explicit Process button over silent background extraction:
/// the system states what it understood, and the user corrects it while the
/// context is still in their head.
/// </summary>
public sealed record CaptureReport(
    IReadOnlyList<CaptureChange> Applied,
    IReadOnlyList<CaptureChange> Refused,
    string? ModelSaid,
    LlmUsage? Usage)
{
    public static CaptureReport Empty { get; } = new([], [], null, null);

    public bool DidNothing => Applied.Count == 0 && Refused.Count == 0;
}
