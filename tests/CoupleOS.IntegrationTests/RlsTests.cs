using CoupleOS.Domain.Entities;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The C# half of M0's question. data/rls-tests.sql already proves PostgreSQL
/// enforces these policies; this proves the enforcement survives EF Core and
/// Npgsql — connection pooling, prepared statements and all.
///
/// There are deliberately NO global query filters on the context. With one in
/// place these tests would pass whether or not RLS worked, which is the exact
/// false confidence worth avoiding.
/// </summary>
public sealed class RlsTests : IClassFixture<RlsFixture>
{
    private static CoupleOsDbContext AppContext()
    {
        var options = new DbContextOptionsBuilder<CoupleOsDbContext>()
            .UseNpgsql(RlsFixture.AppConnectionString)
            .Options;
        return new CoupleOsDbContext(options);
    }

    private static CoupleScope Scope(Guid couple, Guid user) => new() { CoupleId = couple, UserId = user };

    [Fact]
    public async Task PartnerA_sees_shared_and_own_private_only()
    {
        await using var db = AppContext();
        await using var tx = await CoupleScopedTransaction.BeginAsync(db, Scope(RlsFixture.Couple1, RlsFixture.PartnerA));

        var rows = await db.Memories.Select(m => m.Content).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains("C1 SHARED", rows);
        Assert.Contains(rows, r => r.Contains("PRIVATE-A"));
        Assert.DoesNotContain(rows, r => r.Contains("PRIVATE-B"));
    }

    [Fact]
    public async Task PartnerB_cannot_see_the_necklace()
    {
        await using var db = AppContext();
        await using var tx = await CoupleScopedTransaction.BeginAsync(db, Scope(RlsFixture.Couple1, RlsFixture.PartnerB));

        var rows = await db.Memories.Select(m => m.Content).ToListAsync();

        Assert.DoesNotContain(rows, r => r.Contains("necklace"));
    }

    [Fact]
    public async Task Stranger_sees_only_their_own_couple()
    {
        await using var db = AppContext();
        await using var tx = await CoupleScopedTransaction.BeginAsync(db, Scope(RlsFixture.Couple2, RlsFixture.StrangerC));

        var rows = await db.Memories.Select(m => m.Content).ToListAsync();

        Assert.Single(rows);
        Assert.Equal("C2 SHARED", rows[0]);
    }

    [Fact]
    public async Task Query_outside_a_scoped_transaction_returns_nothing()
    {
        await using var db = AppContext();

        var count = await db.Memories.CountAsync();

        Assert.Equal(0, count);   // fails closed, never open
    }

    [Fact]
    public async Task Scope_does_not_survive_commit_on_a_pooled_connection()
    {
        await using (var db = AppContext())
        {
            await using var tx = await CoupleScopedTransaction.BeginAsync(db, Scope(RlsFixture.Couple1, RlsFixture.PartnerA));
            Assert.Equal(2, await db.Memories.CountAsync());
            await tx.CommitAsync();
        }

        // A new context very likely reuses the same physical connection from
        // Npgsql's pool. If the settings had been session-scoped it would still
        // be able to read Couple 1.
        await using var next = AppContext();
        Assert.Equal(0, await next.Memories.CountAsync());
    }

    [Fact]
    public async Task Interleaved_scopes_on_reused_connections_never_cross()
    {
        for (var i = 0; i < 50; i++)
        {
            var forCouple1 = i % 2 == 0;
            var scope = forCouple1
                ? Scope(RlsFixture.Couple1, RlsFixture.PartnerA)
                : Scope(RlsFixture.Couple2, RlsFixture.StrangerC);

            await using var db = AppContext();
            await using var tx = await CoupleScopedTransaction.BeginAsync(db, scope);

            var rows = await db.Memories.Select(m => m.Content).ToListAsync();

            Assert.Equal(forCouple1 ? 2 : 1, rows.Count);
            Assert.DoesNotContain(rows, r => r.StartsWith(forCouple1 ? "C2" : "C1", StringComparison.Ordinal));
        }
    }
}
