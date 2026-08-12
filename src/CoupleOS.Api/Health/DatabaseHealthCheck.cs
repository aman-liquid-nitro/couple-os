using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CoupleOS.Api.Health;

/// <summary>
/// Reports unhealthy unless the application can open a connection to Postgres
/// as its configured role.
///
/// A liveness-only check would report healthy while every page returned an
/// error, which is the same class of dishonesty as claiming a tool call
/// succeeded when it failed (SPEC.md §46).
///
/// This is one of the two places that touch <see cref="CoupleOsDbContext"/>
/// without going through <c>IScopedUnitOfWork</c>, and for the same reason as
/// <c>DevelopmentSeeder</c>: it establishes no couple scope because it reads no
/// rows. <c>SELECT 1</c> touches no table, so there is nothing for row-level
/// security to filter and nothing for an unscoped connection to leak.
///
/// It does not use <c>CanConnectAsync</c>, which was the obvious choice and the
/// wrong one: that method catches the provider's exception and returns a bare
/// false, so the probe reported Unhealthy while discarding the only useful part
/// — whether the host was wrong, the role missing, or the password refused.
/// The integration test asserting a diagnosis exists is what caught it.
/// </summary>
public sealed class DatabaseHealthCheck(CoupleOsDbContext db) : IHealthCheck
{
    private readonly CoupleOsDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // A statement, not merely an open connection. Npgsql pools physical
            // connections, so opening one can be satisfied without reaching the
            // server at all; a round trip is what proves the server answers.
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);

            return HealthCheckResult.Healthy("Database reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message matters more than the status here: the usual causes
            // are a wrong host or a role that does not exist, and both are
            // invisible if the probe only reports a bare 503.
            return HealthCheckResult.Unhealthy("Database connection failed.", ex);
        }
    }
}
