namespace CoupleOS.Application.Persistence;

/// <summary>
/// The only supported way to reach couple data.
///
/// ADR 0005 enforces visibility with PostgreSQL row-level security, which reads
/// per-transaction session settings. Outside such a transaction those settings
/// are unset and every policy fails closed, so a query issued elsewhere returns
/// nothing at all. That is the intended failure — invisible beats leaked — but
/// it makes this the seam every read and write must pass through.
/// </summary>
public interface IScopedUnitOfWork
{
    Task<ICoupleTransaction> BeginAsync(CancellationToken cancellationToken = default);
}
