namespace CoupleOS.Domain.Enums;

/// <summary>
/// Mirrors the dump_block_status enum in data/schema.sql.
///
/// Every block carries one of these, and that is the mechanism M2 exists for.
/// The first real run of the M0 slice produced a report describing two shopping
/// items and said nothing at all about a third line that had produced no tool
/// call — the line vanished, and nobody reading the screen could have known.
/// A status per block makes silence unrepresentable: a block is in one of these
/// six states, and the report can enumerate them.
/// </summary>
public enum DumpBlockStatus
{
    /// <summary>Seen and stored, not yet run through the pipeline.</summary>
    Unprocessed,

    /// <summary>Claimed by a run that has not finished with it.</summary>
    Processing,

    /// <summary>Ran, and whatever it produced is recorded in dump_block_entities.</summary>
    Processed,

    /// <summary>
    /// The model called request_clarification. The block's question is filled and
    /// it parks in "Needs your input" rather than blocking the rest of the run
    /// (ADR 0009).
    /// </summary>
    NeedsInput,

    /// <summary>Read and deliberately no action — a heading, a note to self.</summary>
    Ignored,

    /// <summary>
    /// The block threw. Its error is recorded and the run continues, per
    /// ARCHITECTURE.md 6: one bad block does not discard the rest of a dump.
    /// </summary>
    Failed,
}
