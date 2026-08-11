using CoupleOS.Application.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Wraps EF Core's transaction so the Application layer never sees a persistence
/// type. Without this, IScopedUnitOfWork would return IDbContextTransaction and
/// every consumer would take a transitive dependency on EF Core.
/// </summary>
public sealed class CoupleTransaction(IDbContextTransaction inner) : ICoupleTransaction
{
    private readonly IDbContextTransaction _inner = inner
        ?? throw new ArgumentNullException(nameof(inner));

    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        _inner.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
