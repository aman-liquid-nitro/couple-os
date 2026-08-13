using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>Adds the row and saves inside the caller's transaction.</summary>
public sealed class ExpenseWriter(CoupleOsDbContext dbContext) : IExpenseWriter
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(Expense expense, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        _dbContext.Expenses.Add(expense);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Matches on <c>lower(name)</c>, which is what the unique index on this table is
/// built on, and prefers the couple's own category over the system one of the same
/// name — that is the only reason a couple would add one.
///
/// On the couple-scoped context rather than the identity one, unlike
/// <see cref="PartnerLookup"/>: this read happens inside the tool's transaction and
/// the table has no row-level security, so the <c>couple_id</c> filter here is the
/// whole of the scoping. It is written explicitly for that reason.
/// </summary>
public sealed class ExpenseCategoryLookup(CoupleOsDbContext dbContext) : IExpenseCategoryLookup
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<Guid?> FindAsync(
        Guid coupleId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = name.Trim().ToLowerInvariant();

        var matches = await _dbContext.ExpenseCategories
            .AsNoTracking()
            .Where(c => (c.CoupleId == null || c.CoupleId == coupleId) && c.Name.ToLower() == normalized)
            .Select(c => new { c.Id, c.CoupleId })
            .ToListAsync(cancellationToken);

        return matches.OrderByDescending(m => m.CoupleId.HasValue).Select(m => (Guid?)m.Id).FirstOrDefault();
    }
}
