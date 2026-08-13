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
/// The tool pipeline against a real database: dispatch a call, and a row plus
/// its audit entry appear, scoped to the caller and invisible to anyone else.
///
/// This is the first test where ADR 0004 and ADR 0005 have to hold at the same
/// time. Either alone is easier than both.
/// </summary>
public sealed class ToolPipelineTests : IClassFixture<RlsFixture>
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

    private static ToolExecutionContext SharedContext() => new()
    {
        CoupleId = RlsFixture.Couple1,
        UserId = RlsFixture.PartnerA,
        Visibility = Visibility.SharedCouple,
    };

    private static LlmToolCall Call(string json) =>
        new("create_shopping_item", JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task A_dispatched_call_writes_the_row_and_its_audit_entry()
    {
        var itemName = "detergent-" + Guid.NewGuid().ToString("N")[..8];

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($"{{\"name\":\"{itemName}\",\"quantity\":\"2\"}}"),
            SharedContext());

        Assert.True(result.Succeeded);
        Assert.Equal("shopping_item", result.EntityType);

        var item = await db.ShoppingItems.SingleAsync(i => i.Name == itemName);
        Assert.Equal(RlsFixture.Couple1, item.CoupleId);
        Assert.Equal(Visibility.SharedCouple, item.Visibility);
        Assert.Equal(itemName.ToLowerInvariant(), item.NormalizedName);
        Assert.Equal("2", item.Quantity);

        // Shared items have no owner. Setting one would make the row look
        // private to any code that reads owner_user_id without visibility.
        Assert.Null(item.OwnerUserId);
        Assert.Equal(RlsFixture.PartnerA, item.AddedBy);

        var audit = await db.AiActions.SingleAsync(a => a.EntityId == item.Id);
        Assert.Equal("create_shopping_item", audit.ToolName);
        Assert.Equal(ActionOutcome.Success, audit.Outcome);
        Assert.Contains(itemName, audit.Arguments, StringComparison.Ordinal);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_refused_call_writes_no_row_but_is_still_audited()
    {
        // TOOLS.md rule 3: a rejected call is as interesting as a successful one
        // when debugging trust.
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        // Counted INSIDE the scope. Outside one, row-level security returns zero
        // rows by design, so a "before" taken there would always be 0 and the
        // comparison would be meaningless — which is exactly how this test
        // failed the first time it ran.
        var before = await db.ShoppingItems.CountAsync();

        var result = await dispatcher.DispatchAsync(Call("{\"quantity\":\"2\"}"), SharedContext());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);
        Assert.Equal(before, await db.ShoppingItems.CountAsync());

        var audit = await db.AiActions
            .Where(a => a.Outcome == ActionOutcome.ValidationFailed)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Equal("create_shopping_item", audit.ToolName);
        Assert.NotNull(audit.ErrorMessage);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Asking_a_question_writes_no_row_and_is_still_audited()
    {
        // The audit trail's shape for the one tool that creates nothing: a
        // success with a null entity_type and a null entity_id. Both columns are
        // nullable in data/schema.sql and until now nothing exercised that —
        // every other successful call has a row to point at, so a NOT NULL on
        // either would have gone unnoticed until this tool shipped.
        var fragment = "dentist-" + Guid.NewGuid().ToString("N")[..8];

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var before = await db.ShoppingItems.CountAsync();

        var result = await dispatcher.DispatchAsync(
            new LlmToolCall(
                "request_clarification",
                JsonDocument.Parse($"{{\"question\":\"when?\",\"about\":\"book the {fragment}\"}}")
                    .RootElement.Clone()),
            SharedContext());

        Assert.True(result.Succeeded);
        Assert.True(result.Asked);
        Assert.Equal($"\"book the {fragment}\" — when?", result.Question);
        Assert.Equal(before, await db.ShoppingItems.CountAsync());

        // Found by tool name and recency rather than by matching the fragment
        // inside arguments: the column is jsonb, and a LIKE over it is a query
        // this test would be debugging instead of the behaviour it is about.
        var audit = await db.AiActions
            .Where(a => a.ToolName == "request_clarification")
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Equal(ActionOutcome.Success, audit.Outcome);
        Assert.Contains(fragment, audit.Arguments, StringComparison.Ordinal);
        Assert.Null(audit.EntityType);
        Assert.Null(audit.EntityId);
        Assert.Null(audit.ErrorMessage);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_call_that_tries_to_choose_its_own_couple_writes_nothing()
    {
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        var itemName = "injected-" + Guid.NewGuid().ToString("N")[..8];

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call($"{{\"name\":\"{itemName}\",\"couple_id\":\"{RlsFixture.Couple2}\"}}"),
            SharedContext());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);

        // Asserting on this specific row rather than a count: counts are shared
        // state, and a leftover from an earlier run would mask a real failure.
        Assert.Empty(await db.ShoppingItems.Where(i => i.Name == itemName).ToListAsync());

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task What_one_couple_creates_the_other_cannot_see()
    {
        var itemName = "rice-" + Guid.NewGuid().ToString("N")[..8];

        await using var provider = BuildProvider();

        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();

            await using var transaction = await unitOfWork.BeginAsync();
            var result = await dispatcher.DispatchAsync(Call($"{{\"name\":\"{itemName}\"}}"), SharedContext());
            Assert.True(result.Succeeded);
            await transaction.CommitAsync();
        }

        await using (var stranger = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC))
        {
            var unitOfWork = stranger.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var db = stranger.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

            await using var transaction = await unitOfWork.BeginAsync();

            Assert.Empty(await db.ShoppingItems.Where(i => i.Name == itemName).ToListAsync());
            Assert.Empty(await db.AiActions.Where(a => a.ToolName == "create_shopping_item").ToListAsync());
        }
    }
}
