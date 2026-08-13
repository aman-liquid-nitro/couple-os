using CoupleOS.Application.Security;
using CoupleOS.Application.Time;
using CoupleOS.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// That the clock reads <c>couples.timezone</c> at all.
///
/// Worth its own test because the column had a default and no reader for three
/// milestones, which is STATUS debt 20's shape exactly: a value that is stored,
/// documented, and consulted by nothing. The unit tests prove the resolver does
/// the right arithmetic given a zone; this proves it is given the couple's.
/// </summary>
public sealed class CoupleClockTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .BuildServiceProvider();

    private static async Task SetZoneAsync(Guid coupleId, string zone)
    {
        // As admin: couples has no row-level security, but the app role has no
        // business changing a couple's zone and there is no product path that does.
        await using var connection = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "UPDATE couples SET timezone = @zone WHERE id = @id", connection);

        command.Parameters.AddWithValue("zone", zone);
        command.Parameters.AddWithValue("id", coupleId);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<CoupleTime> ReadAsync(ServiceProvider provider, Guid coupleId)
    {
        await using var request = provider.CreateAsyncScope();

        request.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(coupleId, RlsFixture.PartnerA));

        return await request.ServiceProvider.GetRequiredService<ICoupleClock>().NowAsync();
    }

    [Fact]
    public async Task The_zone_comes_from_the_couples_own_row()
    {
        await using var provider = BuildProvider();

        try
        {
            await SetZoneAsync(RlsFixture.Couple1, "Europe/London");

            var time = await ReadAsync(provider, RlsFixture.Couple1);

            Assert.Equal("Europe/London", time.Zone.Id);
            Assert.True(time.Recognised);

            // The point of reading it: what "tomorrow" means differs, and this is
            // the value that decides.
            Assert.NotEqual(
                TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata").GetUtcOffset(time.Now),
                time.Zone.GetUtcOffset(time.Now));
        }
        finally
        {
            // Restored, because the other tests in this assembly share the fixture
            // couple and a leftover zone would make an unrelated failure look like
            // a date bug.
            await SetZoneAsync(RlsFixture.Couple1, "Asia/Kolkata");
        }
    }

    [Fact]
    public async Task The_schema_default_is_what_a_couple_gets_without_asking()
    {
        await using var provider = BuildProvider();

        var time = await ReadAsync(provider, RlsFixture.Couple1);

        Assert.Equal("Asia/Kolkata", time.Zone.Id);
        Assert.True(time.Recognised);
    }

    [Fact]
    public async Task An_unresolvable_zone_falls_back_and_admits_it_rather_than_throwing()
    {
        // The column is text with no check constraint and no product path that
        // writes it, so a value this runtime cannot resolve means a hand-edited row
        // or a container with no tz database. Neither is a reason to fail somebody's
        // reminder, and both are a reason to say so.
        await using var provider = BuildProvider();

        try
        {
            await SetZoneAsync(RlsFixture.Couple1, "Mars/Olympus_Mons");

            var time = await ReadAsync(provider, RlsFixture.Couple1);

            Assert.Equal("Asia/Kolkata", time.Zone.Id);
            Assert.False(time.Recognised);
        }
        finally
        {
            await SetZoneAsync(RlsFixture.Couple1, "Asia/Kolkata");
        }
    }

    [Fact]
    public async Task The_instant_advances_within_a_request_even_though_the_zone_is_cached()
    {
        // The zone is cached for the request and the instant is not. A long dump run
        // resolving its last block against the time its first one started would put
        // "in an hour" an hour in the past by the end.
        await using var provider = BuildProvider();
        await using var request = provider.CreateAsyncScope();

        request.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(RlsFixture.Couple1, RlsFixture.PartnerA));

        var clock = request.ServiceProvider.GetRequiredService<ICoupleClock>();

        var first = await clock.NowAsync();
        await Task.Delay(15);
        var second = await clock.NowAsync();

        Assert.Same(first.Zone, second.Zone);
        Assert.True(second.Now > first.Now, $"{second.Now:O} should be after {first.Now:O}");
    }
}
