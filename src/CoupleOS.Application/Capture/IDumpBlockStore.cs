using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

/// <summary>
/// A row a block produced, for dump_block_entities.
/// </summary>
/// <param name="Action">
/// created | updated | completed | superseded, as the schema's column documents.
/// A string rather than an enum because the set is the vocabulary of every tool
/// this system will ever have, and V0 registers one of seven.
/// </param>
public sealed record BlockEntity(string EntityType, Guid EntityId, string Action);

public interface IDumpBlockStore
{
    /// <summary>
    /// Records the blocks this file does not already have, and returns exactly
    /// those. A block already present is not an error and not a duplicate — it
    /// is the normal case, because every Process re-reads the whole file.
    ///
    /// The caller must not decide this by reading the existing hashes first.
    /// That is a read-then-write, and two partners pressing Process together
    /// would both find the block absent and both insert it. M1 learned this on
    /// magic links: the replay test passes and the concurrency test does not.
    /// </summary>
    Task<IReadOnlyList<DumpBlock>> AddNewAsync(
        DumpFile file,
        IReadOnlyList<SegmentedBlock> blocks,
        Guid runId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every block on this file still waiting to be processed, oldest line first.
    ///
    /// Reads by status rather than by run, which is what lets a run pick up
    /// blocks an earlier one abandoned. A crash between intake and processing
    /// leaves rows that belong to a finished run and have never been looked at;
    /// asking "what did I just insert" would strand them permanently.
    /// </summary>
    Task<IReadOnlyList<DumpBlock>> PendingAsync(
        Guid dumpFileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The hashes of every block on this file an earlier run left failed.
    ///
    /// Hashes rather than rows, and a list rather than a count, because the
    /// caller has to intersect them with what the file currently says: a failed
    /// block whose line has since been edited away is gone as far as the reader
    /// is concerned, and counting it would produce a warning about a line nobody
    /// can find. The intersection is done in the application, where it can be
    /// read, rather than by sending a bytea array into a query.
    ///
    /// Few rows in practice — a run that fails most of its blocks has a bigger
    /// problem than this query's cost.
    /// </summary>
    Task<IReadOnlyList<byte[]>> FailedHashesAsync(
        Guid dumpFileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a block's terminal status, with the reason or question that goes
    /// with it, and stamps processed_at.
    ///
    /// Status, error and question move together because the schema requires it:
    /// dump_blocks_question_when_needs_input refuses needs_input without a
    /// question. Writing them in one statement means the constraint is checked
    /// against the state the caller intended rather than against a half-applied
    /// version of it.
    /// </summary>
    Task MarkAsync(DumpBlock block, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links a block to the rows it produced.
    ///
    /// This is what makes "which line did this expense come from" answerable, and
    /// the reverse — deleting a line in the file does not delete the expense
    /// (ADR 0009: the file is the input, the rows are the state), so the link is
    /// the only trace back.
    /// </summary>
    Task LinkEntitiesAsync(
        Guid blockId,
        IReadOnlyList<BlockEntity> entities,
        CancellationToken cancellationToken = default);
}
