using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Adds the row and saves within the caller's transaction. It does not open one:
/// the tool call, the entity write and the audit row must commit or roll back
/// together, or a change report could cite a row that no longer exists.
/// </summary>
public sealed class ShoppingItemWriter(CoupleOsDbContext dbContext) : IShoppingItemWriter
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(ShoppingItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        _dbContext.ShoppingItems.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
