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

    public async Task<DumpFileSave> SaveSharedAsync(
        string content,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Created on demand here too: the first thing a new couple does is type
        // into an empty editor and save it, which is the same path as any other
        // save and should not need a different one.
        var file = await GetOrCreateSharedAsync(cancellationToken);

        // content_version + 1 is computed by the database, not by adding one to
        // the value this request read. The version that gets written is then the
        // version that was checked, in one statement, with no window between.
        //
        // updated_at is set here because no trigger sets it. A column that is
        // right only when someone remembers is worse than no column, so it is
        // written in the one statement that changes the row.
        var updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE dump_files
                SET content = {content},
                    content_version = content_version + 1,
                    updated_at = now()
              WHERE id = {file.Id}
                AND content_version = {expectedVersion}
             """,
            cancellationToken);

        // The tracked entity is stale either way — on success because the
        // database incremented the version, on refusal because the other partner
        // did. Raw SQL does not tell the change tracker anything, so reload
        // rather than hand back an entity that disagrees with the row.
        await _dbContext.Entry(file).ReloadAsync(cancellationToken);

        return new DumpFileSave(updated == 1, file);
    }

    private Task<DumpFile?> FindSharedAsync(Guid coupleId, CancellationToken cancellationToken) =>
        _dbContext.DumpFiles
            .FirstOrDefaultAsync(
                f => f.CoupleId == coupleId && f.Kind == DumpFileKind.Shared,
                cancellationToken);
}
