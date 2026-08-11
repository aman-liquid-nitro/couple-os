using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Proves the CLR enum and the PostgreSQL enum agree.
///
/// The mapping relies on Npgsql's default snake-case translator rather than an
/// explicit declaration, because [PgName] would drag Npgsql into the Domain
/// project. A convention is only safe if something checks it: a mismatch here
/// would not throw, it would mis-label rows, and mis-labelled visibility is the
/// one defect this system cannot afford.
/// </summary>
public sealed class VisibilityMappingTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    [Fact]
    public async Task Database_labels_round_trip_to_the_expected_enum_members()
    {
        await using var provider = BuildProvider();
        await using var request = provider.CreateAsyncScope();
        request.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(RlsFixture.Couple1, RlsFixture.PartnerA));

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();
        var memories = await db.Memories.ToListAsync();

        var shared = Assert.Single(memories, m => m.Content == "C1 SHARED");
        Assert.Equal(Visibility.SharedCouple, shared.Visibility);
        Assert.Null(shared.OwnerUserId);

        var mine = Assert.Single(memories, m => m.Content.Contains("PRIVATE-A"));
        Assert.Equal(Visibility.PrivateUser, mine.Visibility);
        Assert.Equal(RlsFixture.PartnerA, mine.OwnerUserId);
    }

    [Fact]
    public async Task Filtering_by_the_clr_enum_translates_to_the_database_enum()
    {
        // Reading is not enough. This proves the value survives translation into
        // a WHERE clause, which is where a mismatched label would actually bite.
        await using var provider = BuildProvider();
        await using var request = provider.CreateAsyncScope();
        request.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(RlsFixture.Couple1, RlsFixture.PartnerA));

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var sharedOnly = await db.Memories
            .Where(m => m.Visibility == Visibility.SharedCouple)
            .Select(m => m.Content)
            .ToListAsync();

        Assert.Equal(["C1 SHARED"], sharedOnly);
    }
}
