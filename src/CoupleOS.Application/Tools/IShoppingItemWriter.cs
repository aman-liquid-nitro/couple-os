using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Tools;

/// <summary>
/// Narrow on purpose. Not a general repository — this exists so one tool can
/// create one row, and a wider surface would invite reads that bypass the
/// scoped unit of work.
/// </summary>
public interface IShoppingItemWriter
{
    Task AddAsync(ShoppingItem item, CancellationToken cancellationToken = default);
}
