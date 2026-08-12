using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

public interface IDumpRunStore
{
    /// <summary>Opens a run against a file, attributed to whoever pressed the button.</summary>
    Task<DumpRun> StartAsync(Guid dumpFileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps <c>finished_at</c> and persists whatever counters the caller set.
    /// A run with no finish time is a run that crashed, and that distinction is
    /// worth keeping readable in the table.
    /// </summary>
    Task FinishAsync(DumpRun run, CancellationToken cancellationToken = default);
}
