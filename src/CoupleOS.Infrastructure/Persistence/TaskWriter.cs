using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Adds the row and saves within the caller's transaction, exactly as
/// <see cref="ShoppingItemWriter"/> does and for the same reason: the tool call,
/// the entity write and the audit row commit or roll back together, or a change
/// report could cite a row that no longer exists.
/// </summary>
public sealed class TaskWriter(CoupleOsDbContext dbContext) : ITaskWriter
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);

        _dbContext.Tasks.Add(task);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
