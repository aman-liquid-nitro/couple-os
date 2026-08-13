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
/// <c>create_event</c> against a real database, where the two things the unit
/// tests cannot check are the two constraints: <c>events.starts_at</c> is
/// <c>NOT NULL</c> and <c>events_owner_required_when_private</c> refuses a private
/// row with no owner.
///
/// The dinner in the first test is the note from M0's first real run — the one
/// that vanished because no tool was registered to take it.
/// </summary>
public sealed class EventToolPipelineTests : IClassFixture<RlsFixture>
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
        new("create_event", JsonDocument.Parse(json).RootElement.Clone());

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    [Fact]
    public async Task The_dinner_that_used_to_vanish_becomes_a_row_with_both_ends()
    {
        var title = Unique("dinner at Priya's");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($$"""
                {"title":"{{title}}","date_expression":"saturday 8pm","end_expression":"until 11",
                 "category":"family","location":"their place"}
                """),
            Context());

        Assert.True(result.Succeeded);
        Assert.Equal("event", result.EntityType);

        var stored = await db.Events.SingleAsync(e => e.Title == title);
        Assert.Equal(RlsFixture.Couple1, stored.CoupleId);
        Assert.Null(stored.OwnerUserId);
        Assert.Equal("family", stored.Category);
        Assert.Equal("their place", stored.Location);
        Assert.NotNull(stored.EndsAt);

        // The end is three hours after the start, which is the whole point of
        // resolving "until 11" against the start rather than against today.
        Assert.Equal(TimeSpan.FromHours(3), stored.EndsAt!.Value - stored.StartsAt);

        var audit = await db.AiActions.SingleAsync(a => a.EntityId == stored.Id);
        Assert.Equal("create_event", audit.ToolName);
        Assert.Equal(ActionOutcome.Success, audit.Outcome);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task An_all_day_event_is_stored_at_local_midnight_and_reads_back_as_that_day()
    {
        // Local midnight in Asia/Kolkata is 18:30Z on the previous day, so a column
        // that quietly dropped the offset would read back as the 11th and nothing
        // would look wrong until a birthday arrived a day early.
        var title = Unique("birthday");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($$"""{"title":"{{title}}","date_expression":"September 12","all_day":true,"category":"birthday","recurrence":"yearly"}"""),
            Context());

        Assert.True(result.Succeeded);

        var stored = await db.Events.SingleAsync(e => e.Title == title);
        Assert.True(stored.AllDay);
        Assert.Equal("FREQ=YEARLY", stored.RecurrenceRule);
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0), stored.StartsAt.UtcDateTime);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_private_event_satisfies_the_constraint_that_a_private_row_names_its_owner()
    {
        // events_owner_required_when_private. The owner is derived from the surface's
        // visibility, so this asserts the derivation rather than the model's word.
        var title = Unique("the proposal");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($$"""{"title":"{{title}}","date_expression":"saturday 7pm"}"""),
            Context(Visibility.PrivateUser));

        Assert.True(result.Succeeded);

        var stored = await db.Events.SingleAsync(e => e.Title == title);
        Assert.Equal(Visibility.PrivateUser, stored.Visibility);
        Assert.Equal(RlsFixture.PartnerA, stored.OwnerUserId);

        await transaction.CommitAsync();
    }
}
