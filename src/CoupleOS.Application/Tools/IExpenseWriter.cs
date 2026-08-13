using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Tools;

/// <summary>One row, one caller — narrow for the reason <see cref="IShoppingItemWriter"/> is.</summary>
public interface IExpenseWriter
{
    Task AddAsync(Expense expense, CancellationToken cancellationToken = default);
}

/// <summary>
/// A category name to its id, or null.
///
/// Separate from the writer because it is a read and because the answer being null
/// is normal: the model picks from the twelve seeded names, and a couple that has
/// added its own is not visible to it. Nothing here creates a category — a heading
/// the couple never chose is worse than no heading at all.
/// </summary>
public interface IExpenseCategoryLookup
{
    Task<Guid?> FindAsync(Guid coupleId, string name, CancellationToken cancellationToken = default);
}
