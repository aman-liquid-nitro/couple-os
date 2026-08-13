using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// A reminder is a task whose time is the point of it, so almost everything worth
/// asserting here is about the time: that it is required, that it is resolved in
/// the couple's zone, and that an unreadable one refuses the call.
/// </summary>
public sealed class CreateReminderToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private static (CreateReminderTool Tool, RecordingTaskWriter Writer) Build()
    {
        var writer = new RecordingTaskWriter();

        return (new CreateReminderTool(writer, new FixedCoupleClock()), writer);
    }

    private static ToolInvocation Call(string json, Visibility visibility = Visibility.SharedCouple) =>
        Invocation(json, CoupleId, UserId, visibility);

    [Fact]
    public async Task It_writes_a_task_labelled_reminder_with_the_time_resolved()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"book the dentist","due_expression":"friday 9am"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        // The entity type is the table, not the tool. Three tools write tasks and
        // the change report cites rows, so "reminder" here would name something
        // nothing else in the system can look up.
        Assert.Equal("task", execution.EntityType);

        var reminder = Assert.Single(writer.Written);
        Assert.Equal(TaskItemKind.Reminder, reminder.Kind);
        Assert.Equal(PriorityLevel.Normal, reminder.Priority);

        // Stored as an instant with no offset. PostgreSQL's timestamptz is an
        // instant and Npgsql refuses a DateTimeOffset carrying an offset rather
        // than converting it, so a +05:30 value throws at the insert — which it
        // did, on the first integration run, where the dispatcher's catch-all
        // turned it into a tool failure with no row and no obvious cause. Asserted
        // here because this is the cheap place to notice it.
        Assert.Equal(TimeSpan.Zero, reminder.DueAt!.Value.Offset);

        var local = TimeZoneInfo.ConvertTime(reminder.DueAt!.Value, Kolkata);
        Assert.Equal(new DateTime(2026, 8, 14, 9, 0, 0), local.DateTime);

        // The time was explicit and the *day* was not: "friday" said on a Thursday
        // could be tomorrow or in eight days, so which one was chosen is stated.
        // A note appears when something was decided, not when something was
        // missing — "tomorrow 4pm" produces none, as CreateTaskToolTests asserts.
        Assert.Contains("Fri 14 Aug 2026", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reminder_with_no_time_is_refused_and_told_which_tool_to_use()
    {
        // TOOLS.md 2 and SPEC.md §3.2's own example: "remind me to book the
        // dentist" has no time, so the tool is not called and
        // request_clarification is. Enforced from this side too, because the model
        // that got it wrong is the one that needs telling — and because
        // tasks_reminder_needs_due would otherwise refuse it as a constraint
        // violation, failing the whole block instead of one call.
        var (tool, writer) = Build();

        var validation = await tool.ValidateAsync(Json("""{"title":"book the dentist"}"""));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("request_clarification", StringComparison.Ordinal));
        Assert.Empty(writer.Written);
    }

    [Theory]
    [InlineData("""{"title":"book the dentist","due_expression":""}""")]
    [InlineData("""{"title":"book the dentist","due_expression":"   "}""")]
    [InlineData("""{"title":"book the dentist","due_expression":null}""")]
    public async Task An_empty_expression_is_the_same_refusal_as_none_at_all(string arguments)
    {
        // A model that sends an empty string has said it had no time, and the
        // answer must not depend on which shape of nothing it chose.
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(Json(arguments));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("'due_expression'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreadable_time_writes_nothing_and_says_why()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"call the school","due_expression":"sometime next month"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Contains("sometime next month", execution.Error!, StringComparison.Ordinal);
        Assert.Contains("nothing was recorded", execution.Error!, StringComparison.Ordinal);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_bare_date_is_given_an_hour_and_the_hour_is_stated()
    {
        // Midnight is what any "just take the date" implementation produces, and a
        // reminder at midnight is a reminder nobody sees. 9am, said out loud.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"renew the insurance","due_expression":"14 september"}"""));

        var local = TimeZoneInfo.ConvertTime(Assert.Single(writer.Written).DueAt!.Value, Kolkata);

        Assert.Equal(new DateTime(2026, 9, 14, 9, 0, 0), local.DateTime);
        Assert.Contains("9am", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_private_reminder_carries_its_owner()
    {
        var (tool, writer) = Build();

        await tool.ExecuteAsync(
            Call("""{"title":"order the cake","due_expression":"tomorrow 8am"}""", Visibility.PrivateUser));

        var reminder = Assert.Single(writer.Written);
        Assert.Equal(Visibility.PrivateUser, reminder.Visibility);
        Assert.Equal(UserId, reminder.OwnerUserId);
    }

    [Fact]
    public void The_description_tells_the_model_when_not_to_call_it()
    {
        var (tool, _) = Build();

        Assert.Contains("request_clarification", tool.Description, StringComparison.Ordinal);
        Assert.Equal(ToolTier.None, tool.Tier);
        Assert.True(tool.IsIdempotent);
    }
}
