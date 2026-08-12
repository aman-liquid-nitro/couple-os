using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// The eval harness judges extraction, not persistence, so tools are registered
/// with writers that do nothing. If an eval ever needed a real database to pass,
/// it would be measuring the wrong thing.
/// </summary>
public sealed class NullShoppingItemWriter : IShoppingItemWriter
{
    public Task AddAsync(ShoppingItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NullAuditSink : IToolAuditSink
{
    public Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
