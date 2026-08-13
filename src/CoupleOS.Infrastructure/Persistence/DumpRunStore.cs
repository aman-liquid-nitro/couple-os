using CoupleOS.Application.Capture;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class DumpRunStore(
    CoupleOsDbContext dbContext,
    ICoupleScopeAccessor scopeAccessor,
    TimeProvider clock) : IDumpRunStore
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<DumpRun> StartAsync(Guid dumpFileId, CancellationToken cancellationToken = default)
    {
        var scope = _scopeAccessor.Current;

        var run = new DumpRun
        {
            CoupleId = scope.CoupleId,
            DumpFileId = dumpFileId,

            // Either partner may process the shared file, so this is the only
            // record of who did.
            TriggeredBy = scope.UserId,

            // Set here, not left to DEFAULT now(). The column is mapped, so EF
            // sends whatever the property holds — and default(DateTimeOffset) is
            // year 1, which would make dump_runs_file sort every run before every
            // other run forever.
            StartedAt = _clock.GetUtcNow(),
        };

        _dbContext.DumpRuns.Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return run;
    }

    public Task<DumpRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _dbContext.DumpRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

    public async Task FinishAsync(DumpRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        run.FinishedAt = _clock.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
