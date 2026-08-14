using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Which surface a row came from, asserted on the column that claims to know.
///
/// <c>data_source</c> defaults to <c>chat</c> on all four tables that carry it,
/// and for three milestones only <c>create_memory</c> wrote it — so a task, an
/// event or an expense typed into <c>shared.md</c> recorded the provenance of a
/// conversation that never took place (STATUS debt 39). Nothing read the column,
/// which is why nothing noticed; the shape of the defect is a populated column
/// saying something untrue, which is worse than an empty one.
///
/// Asserted here rather than in each tool's own tests, because the property is
/// about all of them agreeing: one tool getting it right and three inheriting a
/// default is exactly the state this replaces.
/// </summary>
public sealed class ProvenanceTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    [Theory]
    [InlineData(Visibility.SharedCouple, DataSource.UserInput)]
    [InlineData(Visibility.PrivateUser, DataSource.Chat)]
    public async Task Every_tool_that_can_record_provenance_records_the_surface_it_was_called_from(
        Visibility surface,
        DataSource expected)
    {
        var token = Unique("provenance");

        await using var provider = BuildProvider();
        await using var request = Begin(provider);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var context = new ToolExecutionContext
        {
            CoupleId = RlsFixture.Couple3,
            UserId = RlsFixture.PartnerD,
            Visibility = surface,
        };

        await DispatchAsync(dispatcher, context, "create_task", $$"""{"title":"{{token}} task"}""");
        await DispatchAsync(dispatcher, context, "create_reminder",
            $$"""{"title":"{{token}} reminder","due_expression":"tomorrow"}""");
        await DispatchAsync(dispatcher, context, "create_event",
            $$"""{"title":"{{token}} event","date_expression":"14 December 2026"}""");
        await DispatchAsync(dispatcher, context, "create_expense",
            $$"""{"amount":2400,"description":"{{token}} dinner","paid_by":"me"}""");
        await DispatchAsync(dispatcher, context, "create_memory",
            $$"""{"content":"{{token}} was remembered","type":"episodic"}""");

        Assert.All(
            await db.Tasks.Where(t => t.Title.Contains(token)).ToListAsync(),
            t => Assert.Equal(expected, t.Source));

        Assert.Equal(
            expected,
            (await db.Events.SingleAsync(e => e.Title.Contains(token))).Source);

        Assert.Equal(
            expected,
            (await db.Expenses.SingleAsync(e => e.Description!.Contains(token))).Source);

        Assert.Equal(
            expected,
            (await db.Memories.SingleAsync(m => m.Content.Contains(token))).Source);

        await transaction.CommitAsync();
    }

    private static async Task DispatchAsync(
        IToolDispatcher dispatcher,
        ToolExecutionContext context,
        string tool,
        string arguments)
    {
        var result = await dispatcher.DispatchAsync(
            new LlmToolCall(tool, JsonDocument.Parse(arguments).RootElement.Clone()),
            context);

        Assert.True(result.Succeeded, $"{tool}: {string.Join("; ", result.Errors ?? [])}");
    }

    private static AsyncServiceScope Begin(ServiceProvider provider)
    {
        var scope = provider.CreateAsyncScope();

        scope.ServiceProvider
            .GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(RlsFixture.Couple3, RlsFixture.PartnerD));

        return scope;
    }
}
