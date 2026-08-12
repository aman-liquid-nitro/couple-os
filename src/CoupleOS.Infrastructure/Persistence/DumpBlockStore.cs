using CoupleOS.Application.Capture;
using CoupleOS.Domain.Entities;
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
}
