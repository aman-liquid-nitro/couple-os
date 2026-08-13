using CoupleOS.Application.Capture;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class DumpBlockStore(CoupleOsDbContext dbContext) : IDumpBlockStore
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>
    /// One INSERT per block, each deciding for itself whether it is new.
    ///
    /// ON CONFLICT DO NOTHING against dump_blocks_dedup is the whole dedup
    /// mechanism, and the affected-row count is how we learn the answer: 1 means
    /// this call recorded it, 0 means the file already had it. That reading is
    /// exact under concurrency in a way that "SELECT the hashes, then insert what
    /// is missing" is not.
    ///
    /// The id is generated here rather than returned by the database, so no
    /// RETURNING clause is needed and the affected count stays the only signal.
    /// </summary>
    public async Task<IReadOnlyList<DumpBlock>> AddNewAsync(
        DumpFile file,
        IReadOnlyList<SegmentedBlock> blocks,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(blocks);

        var added = new List<DumpBlock>();

        foreach (var segmented in blocks)
        {
            var block = new DumpBlock
            {
                DumpFileId = file.Id,
                CoupleId = file.CoupleId,

                // Both inherited from the file, which is ADR 0009 stated as code:
                // the surface decides who can see what, and a block cannot be
                // scoped differently from the file it was written in.
                OwnerUserId = file.OwnerUserId,
                Visibility = file.Visibility,

                ContentHash = segmented.ContentHash,
                RawText = segmented.RawText,
                LineStart = segmented.LineStart,
                LineEnd = segmented.LineEnd,
                RunId = runId,
            };

            var inserted = await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO dump_blocks
                     (id, dump_file_id, couple_id, owner_user_id, visibility,
                      content_hash, raw_text, line_start, line_end, run_id)
                 VALUES
                     ({block.Id}, {block.DumpFileId}, {block.CoupleId}, {block.OwnerUserId},
                      {block.Visibility}, {block.ContentHash}, {block.RawText},
                      {block.LineStart}, {block.LineEnd}, {block.RunId})
                 ON CONFLICT (dump_file_id, content_hash) DO NOTHING
                 """,
                cancellationToken);

            if (inserted == 1)
            {
                added.Add(block);
            }
        }

        return added;
    }

    /// <summary>
    /// Ordered by line, so the report reads in the order the person wrote in.
    /// AsNoTracking because these are handed to a processor that updates them
    /// through raw SQL — a tracked copy would be a second source of truth for
    /// the same row, and the stale one would be the one in memory.
    /// </summary>
    public async Task<IReadOnlyList<DumpBlock>> PendingAsync(
        Guid dumpFileId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.DumpBlocks
            .AsNoTracking()
            .Where(b => b.DumpFileId == dumpFileId && b.Status == DumpBlockStatus.Unprocessed)
            .OrderBy(b => b.LineStart)
            .ThenBy(b => b.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<byte[]>> FailedHashesAsync(
        Guid dumpFileId,
        CancellationToken cancellationToken = default) =>
        await _dbContext.DumpBlocks
            .AsNoTracking()
            .Where(b => b.DumpFileId == dumpFileId && b.Status == DumpBlockStatus.Failed)
            .Select(b => b.ContentHash)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Raw SQL for the same reason AddNewAsync uses it: these rows were inserted
    /// with ids generated here and never tracked, so there is no entity for
    /// SaveChanges to update. Attaching one to issue an UPDATE would be a longer
    /// way to write this statement, and a way to accidentally write columns the
    /// run never touched.
    /// </summary>
    public async Task MarkAsync(DumpBlock block, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);

        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE dump_blocks
                SET status = {block.Status},
                    error_message = {block.ErrorMessage},
                    question = {block.Question},
                    processed_at = {block.ProcessedAt}
              WHERE id = {block.Id}
             """,
            cancellationToken);
    }

    public async Task LinkEntitiesAsync(
        Guid blockId,
        IReadOnlyList<BlockEntity> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        foreach (var entity in entities)
        {
            // ON CONFLICT DO NOTHING against the composite primary key. The same
            // block linking the same row twice with the same action is one fact
            // stated twice, not two facts, and a run that is retried should not
            // fail on its own previous success.
            await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO dump_block_entities (block_id, entity_type, entity_id, action)
                 VALUES ({blockId}, {entity.EntityType}, {entity.EntityId}, {entity.Action})
                 ON CONFLICT DO NOTHING
                 """,
                cancellationToken);
        }
    }
}
