using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// One captured thought, and the unit everything in M2 is counted in.
///
/// A block exists so that the change report can account for the *input* rather
/// than for the actions. A line that produces no tool call still has a row and a
/// status here, so the report has something to say about it. Without this table
/// the only record of a run is what it did, and a line the model ignored leaves
/// no trace at all.
/// </summary>
public sealed class DumpBlock
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid DumpFileId { get; init; }

    public required Guid CoupleId { get; init; }

    /// <summary>Null on shared blocks, as on every other shared row.</summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>
    /// Inherited from the file, never read out of the text. This is the same
    /// value the tools will be handed, so a block and the rows it produces
    /// cannot disagree about who can see them.
    /// </summary>
    public required Visibility Visibility { get; init; }

    /// <summary>
    /// SHA-256 over the block's normalized text. Unique per file, which is what
    /// makes a second Process on an unchanged file create nothing.
    /// </summary>
    public required byte[] ContentHash { get; init; }

    /// <summary>What the person actually typed, kept verbatim for the report.</summary>
    public required string RawText { get; init; }

    public int? LineStart { get; init; }

    public int? LineEnd { get; init; }

    public DumpBlockStatus Status { get; set; }

    /// <summary>The run that first saw this block.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// Why this block is <see cref="DumpBlockStatus.Failed"/>, in the words the
    /// report will show. Set together with the status and never on its own — a
    /// failed block with no reason is the silence M2 exists to remove, moved one
    /// level down.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// What the run needs answering before it can act, set with
    /// <see cref="DumpBlockStatus.NeedsInput"/>. The schema's
    /// dump_blocks_question_when_needs_input check refuses one without the other,
    /// so the pair cannot drift apart.
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    /// When this block reached a terminal status. Null while unprocessed, and
    /// null on a block whose run died mid-flight — which is the distinction worth
    /// being able to make from the table.
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    // detected_intent, intent_confidence, answered_by_block_id and privacy_flagged
    // are columns nothing writes yet. They arrive with the run that fills them,
    // one at a time, as everything else in this codebase has.
}
