using CoupleOS.Application.AI;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>One thing that happened, phrased for a person rather than a log.</summary>
public sealed record CaptureChange(
    string ToolName,
    ToolOutcome Outcome,
    string? EntityType,
    Guid? EntityId,
    string Description);

/// <summary>
/// What became of one block, and the record that makes silence impossible.
///
/// A block appears here whatever happened to it. The M0 slice reported actions,
/// so a line producing no tool call produced no line in the report and vanished
/// — an event, on the first real run, seen by nobody. Reporting per block rather
/// than per action inverts that: the report is built from the input, and a block
/// with nothing to say still has to say it.
/// </summary>
/// <param name="Note">
/// Why, in one sentence: the model's own words when it declined to act, the
/// error when the block failed, the question when it needs one answered.
/// </param>
public sealed record BlockReport(
    Guid BlockId,
    string RawText,
    DumpBlockStatus Status,
    IReadOnlyList<CaptureChange> Changes,
    string? Note)
{
    /// <summary>
    /// What this block's own completion cost. Null when no model was called —
    /// a block that failed before reaching one is free, and recording zero
    /// tokens for it would be indistinguishable from a call that returned
    /// nothing.
    /// </summary>
    public LlmUsage? Usage { get; init; }
}

/// <summary>
/// What a run cost, summed over its blocks.
///
/// One call per block means N completions, so this is a sum rather than a single
/// completion's usage. <paramref name="Calls"/> is carried because the sum on
/// its own cannot say whether 3,000 prompt tokens was one expensive block or
/// twelve cheap ones — and that difference is the whole of STATUS debt 8.
/// </summary>
public sealed record RunUsage(
    string Provider,
    string Model,
    int Calls,
    int PromptTokens,
    int CompletionTokens,
    TimeSpan Duration);

/// <summary>
/// What a Process run did — shown to the user immediately, and persisted to
/// dump_runs.report so it survives the tab being closed.
///
/// ADR 0009 chose an explicit Process button over silent background extraction
/// precisely so this exists: the system states what it understood, and the user
/// corrects it while the context is still in their head.
/// </summary>
/// <param name="BlocksSeen">
/// Every block the file contains, including ones earlier runs already handled.
/// </param>
/// <param name="AlreadyRecorded">
/// Blocks this run did not touch because they were already processed. Counted
/// rather than listed: on the twentieth run, enumerating nineteen runs' worth of
/// finished blocks would bury the two that just changed. The count is what makes
/// "saw 12, processed 0" legible as a re-run rather than as an empty file.
/// </param>
public sealed record CaptureReport(
    IReadOnlyList<BlockReport> Blocks,
    int BlocksSeen,
    int AlreadyRecorded,
    RunUsage? Usage)
{
    public static CaptureReport Empty { get; } = new([], 0, 0, null);

    public IEnumerable<BlockReport> With(DumpBlockStatus status) =>
        Blocks.Where(b => b.Status == status);

    /// <summary>Applied changes across every block, for the "added" section.</summary>
    public IEnumerable<CaptureChange> Applied =>
        Blocks.SelectMany(b => b.Changes).Where(c => c.Outcome == ToolOutcome.Success);

    public IEnumerable<CaptureChange> Refused =>
        Blocks.SelectMany(b => b.Changes).Where(c => c.Outcome != ToolOutcome.Success);

    /// <summary>True when this run had nothing to do, which is not the same as doing nothing.</summary>
    public bool NothingToDo => Blocks.Count == 0;

    public bool FileIsEmpty => BlocksSeen == 0;
}
