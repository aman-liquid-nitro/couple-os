using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

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
}
