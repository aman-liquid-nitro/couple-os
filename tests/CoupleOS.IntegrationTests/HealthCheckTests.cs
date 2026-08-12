using CoupleOS.Api.Health;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The container's HEALTHCHECK calls /health, and docker compose decides whether
/// the api service is healthy from the answer. A probe that cannot report failure
/// is worse than no probe: it turns a broken stack into a green one.
///
/// The unhealthy direction is asserted against a database that is not there,
/// rather than by stopping the container, so it runs in the ordinary suite.
/// </summary>
public sealed class HealthCheckTests : IClassFixture<RlsFixture>
{
    private static DatabaseHealthCheck CheckAgainst(string connectionString)
    {
        var provider = new ServiceCollection()
            .AddCoupleOsInfrastructure(connectionString)
            .BuildServiceProvider();

        return new DatabaseHealthCheck(provider.GetRequiredService<CoupleOsDbContext>());
    }

    [Fact]
    public async Task Reports_healthy_when_the_database_answers()
    {
        var result = await CheckAgainst(RlsFixture.AppConnectionString)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_when_the_database_is_unreachable()
    {
        // A port nothing listens on, so the failure is a refused connection —
        // the same shape as `docker compose stop db`, which is what this stands
        // in for. Port 1 is privileged and unassignable, so it cannot be a
        // developer's stray process.
        var unreachable = "Host=localhost;Port=1;Database=coupleos;Username=app_user;" +
                          "Password=dev_app;Timeout=2;Command Timeout=2";

        var result = await CheckAgainst(unreachable)
            .CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);

        // The message is the point of catching the exception at all: "Unhealthy"
        // alone sends you reading Npgsql stack traces to learn the host was wrong.
        Assert.NotNull(result.Exception);
    }
}
