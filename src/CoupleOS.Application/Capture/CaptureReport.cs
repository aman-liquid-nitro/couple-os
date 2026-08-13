using CoupleOS.Application.AI;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>One thing that happened, phrased for a person rather than a log.</summary>
/// <param name="IsQuestion">
/// True when this call asked rather than wrote. It succeeded and it created
/// nothing, which is a combination every count in this file has to be told
/// about: without it a clarification would be listed under "added" and counted
/// in <c>dump_runs.entities_created</c>.
/// </param>
/// <param name="IsAnswer">
/// True when this call looked rather than wrote, and its
/// <paramref name="Description"/> is therefore what was found.
///
/// Read by the private thread's turn, which leaves it out of the change list for
/// the same reason it leaves a question out: on that surface the answer <i>is</i>
/// the reply, and printing it beside itself reads as two searches. Verified in the
/// browser, where it did — the same defect the reply's own fallback was fixed for.
/// The shared surface keeps it, because the report line is the only place it has.
/// </param>
public sealed record CaptureChange(
    string ToolName,
    ToolOutcome Outcome,
    string? EntityType,
    Guid? EntityId,
    string Description,
    bool IsQuestion = false,
    bool IsAnswer = false);

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
/// What the run did to the file itself, after doing everything else.
/// </summary>
public enum FileRewriteOutcome
{
    /// <summary>
    /// Nothing settled, or everything that settled belongs to text the file no
    /// longer contains. The file was not written, deliberately: a version bump
    /// for no change makes both partners' open editors stale for nothing.
    /// </summary>
    NothingToMove,

    /// <summary>Blocks moved into the archive, and the file is a version newer.</summary>
    Rewritten,

    /// <summary>
    /// The other partner saved between this run reading the file and writing it
    /// back, so the rewrite was refused rather than applied over their text. The
    /// work itself is done and recorded — only the tidying was skipped, and the
    /// next Process will do it.
    /// </summary>
    PartnerSavedFirst,
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

    /// <summary>
    /// What became of the file. Init-only rather than positional because it is
    /// decided after the blocks are — the rewrite is the last thing a run does,
    /// and it needs their statuses to know what to move.
    /// </summary>
    public FileRewriteOutcome Rewrite { get; init; }

    /// <summary>
    /// Lines still in the file that an earlier run failed on and that no run will
    /// try again. Reported so that "nothing new" cannot be read as "nothing left".
    /// </summary>
    public int Stranded { get; init; }

    public IEnumerable<BlockReport> With(DumpBlockStatus status) =>
        Blocks.Where(b => b.Status == status);

    /// <summary>
    /// Applied changes across every block, for the "added" section.
    ///
    /// Filtered on the entity rather than on what kind of call it was, because
    /// there are now three successful calls that create nothing and enumerating
    /// them here would mean remembering to add the fourth: a question
    /// (<c>request_clarification</c>), an answer (<c>search_memory</c>) and a
    /// repetition (<c>create_memory</c> on a memory the couple already holds). What
    /// they have in common is the only thing this property cares about —
    /// <c>dump_runs.entities_created</c> is this sequence's length, so a change with
    /// no entity behind it must not be in it.
    /// </summary>
    public IEnumerable<CaptureChange> Applied =>
        Blocks.SelectMany(b => b.Changes)
            .Where(c => c.Outcome == ToolOutcome.Success && c.EntityId is not null);

    public IEnumerable<CaptureChange> Refused =>
        Blocks.SelectMany(b => b.Changes).Where(c => c.Outcome != ToolOutcome.Success);

    /// <summary>True when this run had nothing to do, which is not the same as doing nothing.</summary>
    public bool NothingToDo => Blocks.Count == 0;

    /// <summary>
    /// No readable blocks at all.
    ///
    /// Named for the inbox rather than the file, because since the rewrite these
    /// are different things: a couple three months in has a file full of archive
    /// and, most of the time, nothing left in front of it. "The file is empty"
    /// was true when the only way to reach zero was an empty file, and became a
    /// lie the day a run started clearing the inbox behind itself.
    /// </summary>
    public bool NothingToRead => BlocksSeen == 0;
}
