using CoupleOS.Application.Capture;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

public sealed class DumpFileStore(
    CoupleOsDbContext dbContext,
    ICoupleScopeAccessor scopeAccessor) : IDumpFileStore
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    /// <summary>
    /// The name the surface goes by, in the product and in ADR 0009. It is a
    /// title, not a filename — nothing is written to disk.
    /// </summary>
    private const string SharedTitle = "shared.md";

    public async Task<DumpFile> GetOrCreateSharedAsync(CancellationToken cancellationToken = default)
    {
        var coupleId = _scopeAccessor.Current.CoupleId;

        if (await FindSharedAsync(coupleId, cancellationToken) is { } existing)
        {
            return existing;
        }

        // Raw SQL for one reason: ON CONFLICT. Two partners pressing Process at
        // the same moment on a couple with no file yet both reach this line, and
        // dump_files_one_shared_per_couple means the loser would otherwise get a
        // unique-violation exception where the correct outcome is "someone else
        // made it, use theirs".
        //
        // The WHERE clause is not decoration: the unique index is partial, and
        // PostgreSQL will not infer a partial index without its predicate.
        //
        // content and content_version are left to their column defaults rather
        // than sent from the entity, so the empty file is empty by the schema's
        // definition rather than by this method's opinion of it.
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO dump_files (couple_id, visibility, kind, title)
             VALUES ({coupleId}, 'shared_couple', 'shared', {SharedTitle})
             ON CONFLICT (couple_id) WHERE kind = 'shared' DO NOTHING
             """,
            cancellationToken);

        // Null here would mean row-level security hid a row this transaction just
        // wrote, which the policy cannot do for a row carrying this couple's id.
        // Saying so beats returning null and letting the caller find out later.
        return await FindSharedAsync(coupleId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"The shared dump file for couple {coupleId} is neither readable nor creatable. " +
                "The couple scope and the row's couple_id disagree.");
    }

    private Task<DumpFile?> FindSharedAsync(Guid coupleId, CancellationToken cancellationToken) =>
        _dbContext.DumpFiles
            .FirstOrDefaultAsync(
                f => f.CoupleId == coupleId && f.Kind == DumpFileKind.Shared,
                cancellationToken);
}
