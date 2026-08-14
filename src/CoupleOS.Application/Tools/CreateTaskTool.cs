using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 1 · SPEC.md §11. A concrete action, or a commitment made to the
/// other person.
///
/// **What this tool does not accept, and why.** TOOLS.md's schema lists
/// <c>assigned_to</c> with values <c>me</c> / <c>partner</c> / <c>either</c>, and
/// there is nowhere in the database to put it. <c>tasks</c> has
/// <c>owner_user_id</c>, which ARCHITECTURE.md §5 defines as null unless the row
/// is private — it is the column the row-level security policy reads, so writing
/// an assignee into it would make a shared task look private to every other
/// reader of that column. <c>committed_to_user_id</c> is the one place a person
/// other than the author is legitimately named, and its meaning is narrower:
/// <c>tasks_commitment_needs_target</c> ties it to <c>kind = 'commitment'</c>.
/// SPEC.md §11's own model has no <c>assignedTo</c> either, so the argument is
/// TOOLS.md's alone. The choice was between adding a column under ADR 0012 and
/// shipping the tool without the argument; the argument is dropped, recorded as
/// STATUS debt 35, and the model is offered no vocabulary it cannot honour —
/// which is the same principle as never offering <c>visibility</c>.
///
/// So <c>kind</c> is where the second person enters: "I said I would call your
/// parents" is a commitment and records who it was made to.
/// </summary>
public sealed class CreateTaskTool(
    ITaskWriter writer,
    ICoupleClock clock,
    IPartnerLookup partners) : ITool
{
    private const string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"What is to be done, as an action\",\"maxLength\":200}," +
        "\"description\":{\"type\":\"string\",\"description\":\"Optional detail the title does not carry\",\"maxLength\":2000}," +
        "\"kind\":{\"type\":\"string\",\"enum\":[\"task\",\"commitment\"]," +
        "\"description\":\"'commitment' when the person says they will do something for or with their partner. Otherwise 'task'.\"}," +
        "\"due_expression\":{\"type\":\"string\"," +
        "\"description\":\"When it is due, copied exactly as the person wrote it: 'friday', 'next week', 'the 14th'. Never a date you worked out yourself. Omit if they gave no time.\"," +
        "\"maxLength\":100}," +
        "\"priority\":{\"type\":\"string\",\"enum\":[\"low\",\"normal\",\"high\"]," +
        "\"description\":\"Only when the person says so. 'urgent', 'whenever' — not your own reading of how important it sounds.\"}}," +
        "\"required\":[\"title\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private readonly ITaskWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly ICoupleClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IPartnerLookup _partners = partners ?? throw new ArgumentNullException(nameof(partners));

    public string Name => "create_task";

    public string Description =>
        "Record one thing that needs doing. Call once per distinct task. Use create_reminder " +
        "instead when the point of the note is a time to be told at.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>A task is a row a person can delete. Confirming it would cost more than it saves.</summary>
    public ToolTier Tier => ToolTier.None;

    public bool IsIdempotent => true;

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

        if (arguments.TryGetProperty("description", out var description))
        {
            if (description.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                errors.Add("'description' must be a string when supplied.");
            }
            else if (description.ValueKind == JsonValueKind.String && description.GetString()!.Trim().Length > 2000)
            {
                errors.Add("'description' must be 2000 characters or fewer.");
            }
        }

        // 'reminder' is a real task_kind and deliberately not offered here: a
        // reminder without a due time fails tasks_reminder_needs_due at the
        // database, so the tool that requires the time is the one that may create
        // the label. Named in the error rather than refused blankly, because the
        // model asking for it has understood the note correctly and reached for the
        // wrong door.
        if (arguments.TryGetProperty("kind", out var kind))
        {
            var value = kind.ValueKind == JsonValueKind.String ? kind.GetString()?.Trim().ToLowerInvariant() : null;

            if (value is not ("task" or "commitment"))
            {
                errors.Add(value == "reminder"
                    ? "'kind' cannot be 'reminder' here — call create_reminder, which requires the time."
                    : "'kind' must be 'task' or 'commitment' when supplied.");
            }
        }

        if (arguments.TryGetProperty("priority", out var priority) && ParsePriority(priority) is null)
        {
            errors.Add("'priority' must be 'low', 'normal' or 'high' when supplied.");
        }

        if (arguments.TryGetProperty("due_expression", out var due) &&
            due.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            // Refused as a type rather than resolved as a number. A model that
            // sends 20260814 has done the arithmetic the whole date contract
            // exists to prevent.
            errors.Add("'due_expression' must be a string when supplied, copied from what the person wrote.");
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

        var kind = arguments.TryGetProperty("kind", out var kindElement) &&
                   kindElement.ValueKind == JsonValueKind.String &&
                   kindElement.GetString()!.Trim().Equals("commitment", StringComparison.OrdinalIgnoreCase)
            ? TaskItemKind.Commitment
            : TaskItemKind.Task;

        Guid? committedTo = null;

        if (kind == TaskItemKind.Commitment)
        {
            // TOOLS.md's authorization clause: it fails rather than guessing if the
            // couple has one member. Downgrading it to a task would be the tempting
            // alternative and it would lose the only part that made the note worth
            // recording — "I said I would" is about somebody else.
            committedTo = await _partners.FindPartnerAsync(context.CoupleId, context.UserId, cancellationToken);

            if (committedTo is null)
            {
                return ToolExecution.Failed(
                    "A commitment is made to the other partner, and this couple has only one member yet. " +
                    "Once they have joined, this can be recorded as a commitment.");
            }
        }

        var due = await ToolDate.ResolveAsync(_clock, Text(arguments, "due_expression"), cancellationToken);

        if (due.Failed)
        {
            // A date was given and could not be read. Dropping it and creating the
            // task anyway is the quiet failure this refuses: the row would look
            // complete and the deadline would be gone.
            return ToolExecution.Failed(due.Failure!);
        }

        var task = new TaskItem
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Source = ToolSource.Of(context.Visibility),
            Kind = kind,
            Title = arguments.GetProperty("title").GetString()!.Trim(),
            Description = Text(arguments, "description"),
            Priority = arguments.TryGetProperty("priority", out var priorityElement)
                ? ParsePriority(priorityElement) ?? PriorityLevel.Normal
                : PriorityLevel.Normal,
            DueAt = due.Instant,
            CommittedToUserId = committedTo,
        };

        await _writer.AddAsync(task, cancellationToken);

        return ToolExecution.Created("task", task.Id, due.Note);
    }

    /// <summary>A trimmed string, or null for absent, null, or whitespace.</summary>
    private static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;

    private static PriorityLevel? ParsePriority(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim().ToLowerInvariant() switch
            {
                "low" => PriorityLevel.Low,
                "normal" => PriorityLevel.Normal,
                "high" => PriorityLevel.High,
                _ => null,
            }
            : null;
}
