using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// The task half of M3's first pair. No database and no model: what is asserted
/// here is the row this tool decides to write, and the two things it refuses to
/// decide — a date it cannot read, and a partner who does not exist.
/// </summary>
public sealed class CreateTaskToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PartnerId = Guid.NewGuid();

    private static (CreateTaskTool Tool, RecordingTaskWriter Writer) Build(Guid? partner = null) =>
        BuildWith(new FixedCoupleClock(), partner);

    private static (CreateTaskTool Tool, RecordingTaskWriter Writer) BuildWith(
        FixedCoupleClock clock,
        Guid? partner = null)
    {
        var writer = new RecordingTaskWriter();

        return (new CreateTaskTool(writer, clock, new StubPartnerLookup(partner)), writer);
    }

    private static ToolInvocation Call(string json, Visibility visibility = Visibility.SharedCouple) =>
        Invocation(json, CoupleId, UserId, visibility);

    [Fact]
    public async Task A_title_alone_is_enough_and_produces_a_plain_shared_task()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(Call("""{"title":"take the car in"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Equal("task", execution.EntityType);

        var task = Assert.Single(writer.Written);
        Assert.Equal("take the car in", task.Title);
        Assert.Equal(TaskItemKind.Task, task.Kind);
        Assert.Equal(PriorityLevel.Normal, task.Priority);
        Assert.Equal(CoupleId, task.CoupleId);
        Assert.Equal(Visibility.SharedCouple, task.Visibility);
        Assert.Null(task.DueAt);
        Assert.Null(task.CommittedToUserId);

        // A shared row has no owner. owner_user_id is what the row-level security
        // policy reads for private rows, so filling it in on a shared task would
        // make it look private to anything reading that column alone.
        Assert.Null(task.OwnerUserId);

        // Nothing was assumed, so nothing is said. A note on every row would train
        // both partners to stop reading them.
        Assert.Null(execution.Note);
    }

    [Fact]
    public async Task A_private_task_carries_its_owner_because_the_schema_requires_one()
    {
        // tasks_owner_required_when_private. The visibility comes from the capture
        // surface and the owner is derived from it here — neither is the model's
        // to choose (ADR 0009).
        var (tool, writer) = Build();

        await tool.ExecuteAsync(Call("""{"title":"find a ring"}""", Visibility.PrivateUser));

        var task = Assert.Single(writer.Written);
        Assert.Equal(Visibility.PrivateUser, task.Visibility);
        Assert.Equal(UserId, task.OwnerUserId);
    }

    [Fact]
    public async Task A_date_the_person_wrote_is_resolved_in_their_own_zone_and_the_reading_is_stated()
    {
        // "friday" on a Thursday means tomorrow, and the resolver says which Friday
        // and what hour it filled in. That sentence is the whole reason ToolDate
        // exists: completing an expression is allowed, doing it silently is not.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"call the plumber","due_expression":"friday"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var task = Assert.Single(writer.Written);
        Assert.NotNull(task.DueAt);

        var local = TimeZoneInfo.ConvertTime(task.DueAt!.Value, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
        Assert.Equal(new DateTime(2026, 8, 14, 9, 0, 0), local.DateTime);

        Assert.NotNull(execution.Note);
        Assert.Contains("friday", execution.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14 Aug 2026", execution.Note!, StringComparison.Ordinal);
        Assert.Contains("assumed", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_time_the_person_gave_is_not_commented_on()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"ring the bank","due_expression":"tomorrow 4pm"}"""));

        var task = Assert.Single(writer.Written);
        var local = TimeZoneInfo.ConvertTime(task.DueAt!.Value, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));

        Assert.Equal(new DateTime(2026, 8, 14, 16, 0, 0), local.DateTime);
        Assert.Null(execution.Note);
    }

    [Fact]
    public async Task An_unrecognised_couple_timezone_is_reported_on_the_row_it_affected()
    {
        // CoupleTime.Recognised exists so a caller can say so rather than quietly
        // resolving dates against the wrong country. Saying it here, on the change
        // itself, is the difference between a reminder an hour out that nobody can
        // explain and a setting somebody can fix.
        var (tool, _) = BuildWith(new FixedCoupleClock(recognised: false));

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"ring the bank","due_expression":"tomorrow 4pm"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.NotNull(execution.Note);
        Assert.Contains("not recognised", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_date_that_cannot_be_read_fails_the_call_rather_than_dropping_the_deadline()
    {
        // The quiet failure this prevents: a task created without its due date,
        // which looks complete and has lost the only part that made it urgent.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"book the hotel","due_expression":"after the wedding"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Contains("after the wedding", execution.Error!, StringComparison.Ordinal);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_commitment_records_who_it_was_made_to()
    {
        var (tool, writer) = Build(PartnerId);

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"call your parents","kind":"commitment"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var task = Assert.Single(writer.Written);
        Assert.Equal(TaskItemKind.Commitment, task.Kind);
        Assert.Equal(PartnerId, task.CommittedToUserId);
    }

    [Fact]
    public async Task A_commitment_in_a_one_person_couple_is_refused_rather_than_downgraded()
    {
        // TOOLS.md: it fails rather than guessing if the couple has one member.
        // Writing it as a plain task would be the tempting alternative and would
        // lose the only part worth recording — "I said I would" is about somebody
        // else. tasks_commitment_needs_target would refuse it anyway; failing here
        // means the refusal is a sentence rather than a constraint violation.
        var (tool, writer) = Build(partner: null);

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"call your parents","kind":"commitment"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Empty(writer.Written);
        Assert.Contains("one member", execution.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("low", PriorityLevel.Low)]
    [InlineData("high", PriorityLevel.High)]
    [InlineData("HIGH", PriorityLevel.High)]
    public async Task Priority_is_taken_from_the_person_and_defaults_to_normal(string given, PriorityLevel expected)
    {
        var (tool, writer) = Build();

        await tool.ExecuteAsync(Call($$"""{"title":"pay the bill","priority":"{{given}}"}"""));

        Assert.Equal(expected, Assert.Single(writer.Written).Priority);
    }

    [Fact]
    public async Task A_title_is_required_and_a_blank_one_is_not_a_title()
    {
        var (tool, _) = Build();

        var missing = await tool.ValidateAsync(Json("""{"due_expression":"friday"}"""));

        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, e => e.Contains("'title'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"title":"x","kind":"reminder"}""", "create_reminder")]
    [InlineData("""{"title":"x","kind":"errand"}""", "'kind'")]
    [InlineData("""{"title":"x","priority":"urgent"}""", "'priority'")]
    [InlineData("""{"title":"x","due_expression":20260814}""", "'due_expression'")]
    public async Task Values_outside_the_schema_are_refused_with_the_reason(string arguments, string expected)
    {
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(Json(arguments));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_title_longer_than_the_schema_allows_is_refused_rather_than_truncated()
    {
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(
            Json($$"""{"title":"{{new string('x', 201)}}"}"""));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("200 characters", StringComparison.Ordinal));
    }

    [Fact]
    public void It_offers_the_model_no_vocabulary_for_who_the_row_belongs_to()
    {
        // assigned_to is absent deliberately, not by oversight: there is no column
        // for it (STATUS debt 35), and offering an argument the tool cannot honour
        // would produce a model that believes it assigned something.
        var (tool, _) = Build();

        var properties = tool.ParametersSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["title", "description", "kind", "due_expression", "priority"], properties);
    }
}
