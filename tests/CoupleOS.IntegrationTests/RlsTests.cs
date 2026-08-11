using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The C# half of M0's question. data/rls-tests.sql proves PostgreSQL enforces
/// the ADR 0005 policies; this proves the enforcement survives EF Core, Npgsql
/// and connection pooling.
///
/// Services are resolved from a real container rather than constructed by hand,
/// so a mistake in AddCoupleOsInfrastructure fails here rather than in
/// production. The wiring is part of what is under test.
///
/// There are deliberately NO global query filters on the context. With one in
/// place these tests would pass whether or not row-level security worked.
/// </summary>
public sealed class RlsTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    /// <summary>One request: a container scope with the couple scope established.</summary>
    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));
        return scope;
    }

    private static async Task<List<string>> ReadMemoriesAsync(AsyncServiceScope request)
    {
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();
        return await db.Memories.Select(m => m.Content).ToListAsync();
    }

    [Fact]
    public async Task Partner_sees_shared_and_own_private_only()
    {
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var rows = await ReadMemoriesAsync(request);

        Assert.Equal(2, rows.Count);
        Assert.Contains("C1 SHARED", rows);
        Assert.Contains(rows, r => r.Contains("PRIVATE-A"));
        Assert.DoesNotContain(rows, r => r.Contains("PRIVATE-B"));
    }

    [Fact]
    public async Task Partner_cannot_see_the_other_partners_private_memory()
    {
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerB);

        var rows = await ReadMemoriesAsync(request);

        Assert.DoesNotContain(rows, r => r.Contains("necklace"));
    }

    [Fact]
    public async Task Stranger_sees_only_their_own_couple()
    {
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC);

        var rows = await ReadMemoriesAsync(request);

        Assert.Single(rows);
        Assert.Equal("C2 SHARED", rows[0]);
    }

    [Fact]
    public async Task Query_outside_a_scoped_transaction_returns_nothing()
    {
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        // Note: no unit of work. The settings are unset, so every policy fails
        // closed. Invisible beats leaked.
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        Assert.Equal(0, await db.Memories.CountAsync());
    }

    [Fact]
    public async Task Beginning_a_unit_of_work_without_a_scope_throws()
    {
        await using var provider = BuildProvider();
        await using var request = provider.CreateAsyncScope();   // scope never set

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.BeginAsync());
    }

    [Fact]
    public async Task Scope_does_not_survive_commit_on_a_pooled_connection()
    {
        await using var provider = BuildProvider();

        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

            await using var transaction = await unitOfWork.BeginAsync();
            Assert.Equal(2, await db.Memories.CountAsync());
            await transaction.CommitAsync();
        }

        // A fresh request very likely reuses the same physical connection from
        // Npgsql's pool. Session-scoped settings would still read Couple 1 here.
        await using var next = provider.CreateAsyncScope();
        var nextDb = next.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        Assert.Equal(0, await nextDb.Memories.CountAsync());
    }

    [Fact]
    public async Task Interleaved_scopes_on_reused_connections_never_cross()
    {
        await using var provider = BuildProvider();

        for (var i = 0; i < 50; i++)
        {
            var forCouple1 = i % 2 == 0;

            await using var request = forCouple1
                ? BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA)
                : BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC);

            var rows = await ReadMemoriesAsync(request);

            Assert.Equal(forCouple1 ? 2 : 1, rows.Count);
            Assert.DoesNotContain(rows, r => r.StartsWith(forCouple1 ? "C2" : "C1", StringComparison.Ordinal));
        }
    }
}
