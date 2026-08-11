namespace CoupleOS.Application.Persistence;

/// <summary>
/// A unit of work scoped to one couple and one user.
///
/// Disposing without committing rolls back, which is the safe default: a
/// half-applied dump run should leave nothing behind rather than a partial
/// change report the user cannot reconcile.
/// </summary>
public interface ICoupleTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
