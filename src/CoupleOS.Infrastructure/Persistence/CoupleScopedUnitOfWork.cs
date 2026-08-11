using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Opens a transaction and binds PostgreSQL's session settings to it, so every
/// row-level security policy in data/schema.sql evaluates against this caller.
///
/// Two details are load-bearing, both established by measurement rather than
/// preference (ADR 0005, data/rls-tests.sql):
///
/// 1. The settings are transaction-local. Set at session level they survive
///    COMMIT, and the next request on the same pooled connection reads the
///    previous couple's rows. That leak is three statements long.
///
/// 2. It uses set_config(...) rather than SET LOCAL. SET LOCAL cannot take a
///    query parameter — "SET LOCAL app.current_couple_id = $1" is a syntax
///    error — which pushes an implementer toward concatenating a value into
///    SQL. set_config is exactly equivalent and parameterises cleanly.
/// </summary>
public sealed class CoupleScopedUnitOfWork(
    CoupleOsDbContext dbContext,
    ICoupleScopeAccessor scopeAccessor) : IScopedUnitOfWork
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    public async Task<ICoupleTransaction> BeginAsync(CancellationToken cancellationToken = default)
    {
        // Throws when unset. Failing here is far kinder than silently returning
        // an empty result set from every subsequent query.
        var scope = _scopeAccessor.Current;

        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var coupleId = scope.CoupleId.ToString();
            var userId = scope.UserId.ToString();

            await _dbContext.Database.ExecuteSqlAsync(
                $"SELECT set_config('app.current_couple_id', {coupleId}, true), set_config('app.current_user_id', {userId}, true)",
                cancellationToken);
        }
        catch
        {
            // An unscoped open transaction would hold a pooled connection and
            // read nothing. Fail closed and give the connection back.
            await transaction.DisposeAsync();
            throw;
        }

        return new CoupleTransaction(transaction);
    }
}
