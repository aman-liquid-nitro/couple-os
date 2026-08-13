using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Tools;

/// <summary>
/// Narrow, for the same reason <see cref="IShoppingItemWriter"/> is: two tools
/// create one kind of row between them, and a wider surface would invite reads
/// that bypass the scoped unit of work.
/// </summary>
public interface ITaskWriter
{
    Task AddAsync(TaskItem task, CancellationToken cancellationToken = default);
}
