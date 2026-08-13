using CoupleOS.Application.AI;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// <c>create_expense</c> against a real database, which is the only place three of
/// its decisions can be checked: <c>numeric(14,2)</c> round-tripping as a decimal
/// rather than a float, <c>occurred_on</c> being a <c>date</c>, and the category
/// name resolving against the twelve rows <c>data/schema.sql</c> seeds.
/// </summary>
public sealed class ExpenseToolPipelineTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));

        return scope;
    }

    private static ToolExecutionContext Context(Visibility visibility = Visibility.SharedCouple) => new()
    {
        CoupleId = RlsFixture.Couple1,
        UserId = RlsFixture.PartnerA,
        Visibility = visibility,
    };

    private static LlmToolCall Call(string json) =>
        new("create_expense", JsonDocument.Parse(json).RootElement.Clone());

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    [Fact]
    public async Task An_amount_survives_the_round_trip_exactly_and_the_category_resolves()
    {
        var description = Unique("dinner");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($$"""{"amount":2400.55,"description":"{{description}}","category":"Dining","merchant":"Sunny's"}"""),
            Context());

        Assert.True(result.Succeeded);
        Assert.Equal("expense", result.EntityType);

        var stored = await db.Expenses.SingleAsync(e => e.Description == description);

        // Exactly, not approximately. A float column would return 2400.5499999...
        // and every total built on it would disagree with the receipt.
        Assert.Equal(2400.55m, stored.Amount);
        Assert.Equal("INR", stored.Currency);
        Assert.Equal(RlsFixture.PartnerA, stored.PaidBy);
        Assert.True(stored.IsShared);
        Assert.NotNull(stored.CategoryId);

        // The name resolved to one of the seeded rows, and to the system row rather
        // than to nothing — the whole point of a lookup over free text.
        var category = await db.ExpenseCategories.SingleAsync(c => c.Id == stored.CategoryId);
        Assert.Equal("Dining", category.Name);
        Assert.Null(category.CoupleId);

        var audit = await db.AiActions.SingleAsync(a => a.EntityId == stored.Id);
        Assert.Equal("create_expense", audit.ToolName);
        Assert.Equal(ActionOutcome.Success, audit.Outcome);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task The_day_written_is_the_couple_s_day_and_it_reads_back_as_a_date()
    {
        // occurred_on defaults to CURRENT_DATE, which is the database host's today.
        // This asserts the application wrote its own value rather than letting the
        // default decide — the difference shows up for a couple in Asia/Kolkata
        // between midnight and half past five in the morning.
        var description = Unique("groceries");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var clock = request.ServiceProvider.GetRequiredService<CoupleOS.Application.Time.ICoupleClock>();

        await using var transaction = await unitOfWork.BeginAsync();

        var now = await clock.NowAsync();
        var expected = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.Now, now.Zone).DateTime);

        var result = await dispatcher.DispatchAsync(
            Call($$"""{"amount":1200,"description":"{{description}}","category":"Groceries"}"""),
            Context());

        Assert.True(result.Succeeded);

        var stored = await db.Expenses.SingleAsync(e => e.Description == description);
        Assert.Equal(expected, stored.OccurredOn);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_gift_recorded_privately_is_invisible_to_the_person_it_is_for()
    {
        // SPEC.md §19 and ADR 0009 together: the surface decides privacy, and the
        // policy is what enforces it. This is the money version of the surprise the
        // conversation_messages leak was about.
        var description = Unique("necklace");

        await using var provider = BuildProvider();

        await using (var mine = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = mine.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var dispatcher = mine.ServiceProvider.GetRequiredService<IToolDispatcher>();

            await using var transaction = await unitOfWork.BeginAsync();

            var result = await dispatcher.DispatchAsync(
                Call($$"""{"amount":15000,"description":"{{description}}","category":"Gifts","is_shared":false}"""),
                Context(Visibility.PrivateUser));

            Assert.True(result.Succeeded);

            await transaction.CommitAsync();
        }

        await using (var theirs = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerB))
        {
            var unitOfWork = theirs.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var db = theirs.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

            await using var transaction = await unitOfWork.BeginAsync();

            Assert.Empty(await db.Expenses.Where(e => e.Description == description).ToListAsync());
        }
    }
}
