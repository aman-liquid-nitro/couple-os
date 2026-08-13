using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

public interface IDumpRunStore
{
    /// <summary>Opens a run against a file, attributed to whoever pressed the button.</summary>
    Task<DumpRun> StartAsync(Guid dumpFileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-reads a run so it can be completed.
    ///
    /// Needed because intake's transaction has long since committed by the time
    /// the last block finishes: the entity it returned belongs to a DbContext
    /// that is no longer in a transaction, and updating it would write through a
    /// change tracker whose view of the row predates every block in the run.
    /// </summary>
    Task<DumpRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <c>finished_at</c> and persists whatever counters the caller set.
    /// A run with no finish time is a run that crashed, and that distinction is
    /// worth keeping readable in the table.
    /// </summary>
    Task FinishAsync(DumpRun run, CancellationToken cancellationToken = default);
}
