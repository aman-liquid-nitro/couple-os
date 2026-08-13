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
/// The two task tools against a real database, which is where the parts the unit
/// tests fake have to agree with PostgreSQL: two enum types, three check
/// constraints, and a partner read that happens on a different DbContext because
/// <c>couple_members</c> has no row-level security.
///
/// The failures this catches are all of the same shape — a value that is correct
/// in C# and wrong at the wire. An enum label Npgsql translates differently than
/// the schema spells it does not throw at startup; it throws the first time
/// somebody records a commitment.
/// </summary>
public sealed class TaskToolPipelineTests : IClassFixture<RlsFixture>
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

    private static ToolExecutionContext Context(
        Guid couple,
        Guid user,
        Visibility visibility = Visibility.SharedCouple) => new()
        {
            CoupleId = couple,
            UserId = user,
            Visibility = visibility,
        };

    private static LlmToolCall Call(string tool, string json) =>
        new(tool, JsonDocument.Parse(json).RootElement.Clone());

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    [Fact]
    public async Task A_task_with_a_due_expression_becomes_a_row_with_a_resolved_instant()
    {
        var title = Unique("take the car in");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call("create_task", $$"""{"title":"{{title}}","due_expression":"friday","priority":"high"}"""),
            Context(RlsFixture.Couple1, RlsFixture.PartnerA));

        Assert.True(result.Succeeded);
        Assert.Equal("task", result.EntityType);

        // The note the resolver produced reaches the dispatcher's result, which is
        // what puts it in the change report. Without this hop the reading is
        // decided and never said, which is the silent completion the whole date
        // contract exists to prevent.
        Assert.Contains("assumed", result.Note!, StringComparison.Ordinal);

        var task = await db.Tasks.SingleAsync(t => t.Title == title);
        Assert.Equal(TaskItemKind.Task, task.Kind);
        Assert.Equal(PriorityLevel.High, task.Priority);
        Assert.Equal(RlsFixture.Couple1, task.CoupleId);
        Assert.Equal(Visibility.SharedCouple, task.Visibility);
        Assert.Null(task.OwnerUserId);
        Assert.NotNull(task.DueAt);

        var audit = await db.AiActions.SingleAsync(a => a.EntityId == task.Id);
        Assert.Equal("create_task", audit.ToolName);
        Assert.Equal(ActionOutcome.Success, audit.Outcome);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_reminder_satisfies_the_constraint_that_a_task_does_not_have_to()
    {
        // tasks_reminder_needs_due. The unit tests assert the tool refuses a
        // reminder with no time; this asserts the row it does write is one the
        // database accepts, which is the other half of the same rule.
        var title = Unique("book the dentist");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call("create_reminder", $$"""{"title":"{{title}}","due_expression":"friday 9am"}"""),
            Context(RlsFixture.Couple1, RlsFixture.PartnerA));

        Assert.True(result.Succeeded);

        var reminder = await db.Tasks.SingleAsync(t => t.Title == title);
        Assert.Equal(TaskItemKind.Reminder, reminder.Kind);
        Assert.NotNull(reminder.DueAt);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_commitment_names_the_other_member_read_from_a_context_with_no_couple_scope()
    {
        // The partner read runs on IdentityDbContext, outside the scoped unit of
        // work, because couple_members is one of the six tables with no row-level
        // security. That is a seam worth an assertion: it must find the partner
        // while the transaction on the other context is open, and it must find the
        // partner rather than the caller.
        var title = Unique("call your parents");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call("create_task", $$"""{"title":"{{title}}","kind":"commitment"}"""),
            Context(RlsFixture.Couple1, RlsFixture.PartnerA));

        Assert.True(result.Succeeded);

        var commitment = await db.Tasks.SingleAsync(t => t.Title == title);
        Assert.Equal(TaskItemKind.Commitment, commitment.Kind);
        Assert.Equal(RlsFixture.PartnerB, commitment.CommittedToUserId);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_commitment_in_a_couple_of_one_writes_nothing_and_is_audited_as_a_failure()
    {
        // Couple2 has a single member, which is the state every couple is in
        // between being created and the invitation being accepted.
        var title = Unique("call the plumber");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Call("create_task", $$"""{"title":"{{title}}","kind":"commitment"}"""),
            Context(RlsFixture.Couple2, RlsFixture.StrangerC));

        Assert.Equal(ToolOutcome.ExecutionFailed, result.Outcome);
        Assert.Empty(await db.Tasks.Where(t => t.Title == title).ToListAsync());

        // A refusal is as auditable as a write (TOOLS.md rule 3), and this one
        // reaches the database as an outcome label rather than as an exception.
        var audit = await db.AiActions
            .Where(a => a.ToolName == "create_task" && a.Outcome == ActionOutcome.ExecutionFailed)
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.NotNull(audit.ErrorMessage);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_private_task_is_invisible_to_the_other_partner()
    {
        // ADR 0009's asymmetry, on the table M3 adds. The surprise is the point:
        // a private task in a shared couple must not be readable by the person it
        // is for, and the policy — not this code — is what makes that true.
        var title = Unique("find a ring");

        await using var provider = BuildProvider();

        await using (var mine = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = mine.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var dispatcher = mine.ServiceProvider.GetRequiredService<IToolDispatcher>();

            await using var transaction = await unitOfWork.BeginAsync();

            var result = await dispatcher.DispatchAsync(
                Call("create_task", $$"""{"title":"{{title}}"}"""),
                Context(RlsFixture.Couple1, RlsFixture.PartnerA, Visibility.PrivateUser));

            Assert.True(result.Succeeded);

            await transaction.CommitAsync();
        }

        await using (var theirs = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerB))
        {
            var unitOfWork = theirs.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var db = theirs.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

            await using var transaction = await unitOfWork.BeginAsync();

            Assert.Empty(await db.Tasks.Where(t => t.Title == title).ToListAsync());
        }
    }

    [Fact]
    public async Task Both_new_enums_match_the_database_label_for_label_and_in_order()
    {
        // The same check DumpEnumMappingTests makes, for M3's two enums, and
        // asserted against the database's own list rather than a copy of it here —
        // a copy would drift with the schema and still pass. Order matters: these
        // are declaration-ordered in both places, and a member inserted in the
        // middle of one would silently re-label every value after it.
        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var kinds = await db.Database
            .SqlQuery<string>($"SELECT unnest(enum_range(NULL::task_kind))::text AS \"Value\"")
            .ToListAsync();

        Assert.Equal(["task", "reminder", "commitment"], kinds);
        Assert.Equal(kinds, Enum.GetValues<TaskItemKind>().Select(k => k.ToString().ToLowerInvariant()));

        var priorities = await db.Database
            .SqlQuery<string>($"SELECT unnest(enum_range(NULL::priority_level))::text AS \"Value\"")
            .ToListAsync();

        Assert.Equal(["low", "normal", "high"], priorities);
        Assert.Equal(priorities, Enum.GetValues<PriorityLevel>().Select(p => p.ToString().ToLowerInvariant()));

        // Reading is half of it; filtering is where a wrong label bites, because
        // PostgreSQL rejects an unknown label in a WHERE clause outright.
        Assert.True(await db.Tasks.Where(t => t.Kind == TaskItemKind.Reminder).CountAsync() >= 0);
    }
}
