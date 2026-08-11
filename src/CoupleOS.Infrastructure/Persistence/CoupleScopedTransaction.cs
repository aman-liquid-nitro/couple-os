using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Opens a transaction and scopes it to one couple and one user.
///
/// Two things here are load-bearing, both established by measurement rather
/// than preference (ADR 0005, data/rls-tests.sql):
///
/// 1. The settings are transaction-local. Set at session level instead, they
///    survive COMMIT and the next request on the same pooled connection reads
///    the previous couple's rows. That is a cross-couple data leak, and it is
///    three statements long.
///
/// 2. It uses set_config(...) rather than SET LOCAL. SET LOCAL cannot take a
///    query parameter — "SET LOCAL app.current_couple_id = $1" is a syntax
///    error — which pushes you toward concatenating a value into SQL.
///    set_config('app.current_couple_id', $1, true) is exactly equivalent and
///    takes parameters, so nothing is ever interpolated.
///
/// Consequence worth knowing: a query issued OUTSIDE one of these transactions
/// sees zero rows, because the settings are unset and every policy fails
/// closed. That is the intended failure — invisible beats leaked.
/// </summary>
public static class CoupleScopedTransaction
{
    public static async Task<IDbContextTransaction> BeginAsync(
        CoupleOsDbContext db,
        ICoupleScope scope,
        CancellationToken ct = default)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        await ApplyAsync(db, scope, ct);
        return tx;
    }

    /// <summary>Applies the scope to the transaction already in progress.</summary>
    public static Task ApplyAsync(CoupleOsDbContext db, ICoupleScope scope, CancellationToken ct = default)
    {
        var couple = scope.CoupleId.ToString();
        var user = scope.UserId.ToString();

        return db.Database.ExecuteSqlAsync(
            $"SELECT set_config('app.current_couple_id', {couple}, true), set_config('app.current_user_id', {user}, true)",
            ct);
    }
}
