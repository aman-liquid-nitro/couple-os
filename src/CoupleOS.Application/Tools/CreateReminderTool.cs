using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 2 · SPEC.md §7. A task whose time is the point of it.
///
/// The whole difference from <see cref="CreateTaskTool"/> is that
/// <c>due_expression</c> is required, and the database agrees:
/// <c>tasks_reminder_needs_due</c> refuses <c>kind = 'reminder'</c> with a null
/// <c>due_at</c>, so this is one row shape and one constraint rather than a second
/// table (ARCHITECTURE.md §3).
///
/// **The case this tool exists to get right.** "Remind me to book the dentist" has
/// no time in it. TOOLS.md is explicit that the tool is then *not* called and
/// <c>request_clarification</c> is, and SPEC.md §3.2 shows that note resolving to
/// <c>Needs clarification: Yes</c>. This validates the same rule from the other
/// side: a call with no expression is refused with a message naming the tool that
/// should have been used, so a model that guesses wrong is told rather than
/// obeyed. A reminder at an invented time is worse than no reminder — it fires
/// while nobody is expecting it and stops the person from setting a real one.
///
/// **What it does not accept.** TOOLS.md lists <c>for_whom</c> (<c>me</c> /
/// <c>partner</c> / <c>both</c>). It describes who gets *notified*, and V0 has no
/// notifications at all — the delivery half is cut in V0_SCOPE.md, and there is no
/// column for the intent either. Recorded as STATUS debt 35 along with
/// <c>create_task</c>'s <c>assigned_to</c>, which is the same missing column
/// wearing a different name.
/// </summary>
public sealed class CreateReminderTool(ITaskWriter writer, ICoupleClock clock) : ITool
{
    private const string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"What to be reminded of, as an action\",\"maxLength\":200}," +
        "\"due_expression\":{\"type\":\"string\"," +
        "\"description\":\"When to be reminded, copied exactly as the person wrote it: 'friday 9am', 'tomorrow morning', 'the 14th at 8'. Never a date you worked out yourself.\"," +
        "\"maxLength\":100}}," +
        "\"required\":[\"title\",\"due_expression\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private readonly ITaskWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly ICoupleClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string Name => "create_reminder";

    public string Description =>
        "Record something to be reminded of at a stated time. Only call this when the person gave " +
        "a time. If they did not, call request_clarification instead — never pick a time yourself.";

    public ToolTier Tier => ToolTier.None;

    public bool IsIdempotent => true;

    public JsonElement ParametersSchema => Schema;

    public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!arguments.TryGetProperty("title", out var title) ||
            title.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(title.GetString()))
        {
            errors.Add("'title' is required and must be a non-empty string.");
        }
        else if (title.GetString()!.Trim().Length > 200)
        {
            errors.Add("'title' must be 200 characters or fewer.");
        }

        if (!arguments.TryGetProperty("due_expression", out var due) ||
            due.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(due.GetString()))
        {
            errors.Add(
                "'due_expression' is required for a reminder — it is the whole difference between a " +
                "reminder and a task. If the person gave no time, call request_clarification to ask " +
                "for one, or create_task if it is simply something to be done.");
        }

        return ValueTask.FromResult(errors.Count == 0
            ? ToolValidation.Valid
            : new ToolValidation(false, errors));
    }

    public async Task<ToolExecution> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var arguments = invocation.Arguments;
        var context = invocation.Context;

        var expression = arguments.GetProperty("due_expression").GetString()!.Trim();

        var due = await ToolDate.ResolveAsync(_clock, expression, cancellationToken);

        if (due.Failed || due.Instant is null)
        {
            // "Sometime next month" is not a time, and this is where that becomes a
            // sentence somebody can act on rather than a constraint violation. The
            // resolver quotes the expression back verbatim because the person who
            // wrote it is the one who has to write it differently.
            return ToolExecution.Failed(
                (due.Failure ?? "No time could be worked out from that.") +
                " A reminder has to have one, so nothing was recorded.");
        }

        var reminder = new TaskItem
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Source = ToolSource.Of(context.Visibility),
            Kind = TaskItemKind.Reminder,
            Title = arguments.GetProperty("title").GetString()!.Trim(),

            // Not a decision this tool gets to make. A reminder is not urgent by
            // virtue of having a time, and inferring priority from language is the
            // kind of quiet guess the whole tool layer is arranged to prevent.
            Priority = PriorityLevel.Normal,
            DueAt = due.Instant,
        };

        await _writer.AddAsync(reminder, cancellationToken);

        return ToolExecution.Created("task", reminder.Id, due.Note);
    }
}
