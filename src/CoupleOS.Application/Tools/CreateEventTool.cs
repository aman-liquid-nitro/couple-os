using System.Globalization;
using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 5 · SPEC.md §16. The tool whose absence started M2's whole argument:
/// the dinner line in the first real run produced no call, no report entry and no
/// error, because <c>create_event</c> was not registered.
///
/// It is also the tool the date contract was written for. <c>events.starts_at</c>
/// is <c>NOT NULL</c>, and TOOLS.md records what happens when a model is asked for
/// it directly — three models resolved "Saturday 8pm" against their own training
/// cutoffs and were wrong by 7 months, 13 months and nearly three years, with no
/// error and a plausible timestamp. So the model sends the words and this sends
/// them to <see cref="ToolDate"/>, which refuses rather than guesses.
///
/// Two arguments are translated rather than stored as given. <c>recurrence</c> is a
/// four-value enum that becomes an RFC 5545 rule here, because a model asked for
/// "FREQ=YEARLY" will eventually produce a rule a calendar library refuses and
/// nothing would notice; and <c>all_day</c> drops the resolver's assumed hour
/// instead of storing 9am on a birthday.
/// </summary>
public sealed class CreateEventTool(IEventWriter writer, ICoupleClock clock) : ITool
{
    private const string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"title\":{\"type\":\"string\",\"description\":\"What the event is\",\"maxLength\":200}," +
        "\"date_expression\":{\"type\":\"string\"," +
        "\"description\":\"When it happens, copied exactly as the person wrote it: 'saturday 8pm', 'December 14', 'tomorrow'. Never a date you worked out yourself.\"," +
        "\"maxLength\":100}," +
        "\"end_expression\":{\"type\":\"string\"," +
        "\"description\":\"When it ends, if the person said: 'until 11', 'till midnight'. Copied verbatim.\"," +
        "\"maxLength\":100}," +
        "\"all_day\":{\"type\":\"boolean\",\"description\":\"True when no time of day applies — birthdays, anniversaries\"}," +
        "\"location\":{\"type\":\"string\",\"description\":\"Where, if the person said\",\"maxLength\":300}," +
        "\"category\":{\"type\":\"string\",\"enum\":[\"birthday\",\"anniversary\",\"appointment\",\"trip\",\"family\",\"bill\",\"renewal\",\"other\"]}," +
        "\"recurrence\":{\"type\":\"string\",\"enum\":[\"none\",\"yearly\",\"monthly\",\"weekly\"]," +
        "\"description\":\"Birthdays and anniversaries repeat yearly\"}}," +
        "\"required\":[\"title\",\"date_expression\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private static readonly string[] Categories =
        ["birthday", "anniversary", "appointment", "trip", "family", "bill", "renewal", "other"];

    private readonly IEventWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly ICoupleClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string Name => "create_event";

    public string Description =>
        "Record something happening at a time: a dinner, a birthday, an appointment, a trip. " +
        "Call once per event. Use create_reminder instead when the point is being told beforehand. " +

        // Added when create_memory made the alternative reachable: "let's visit my
        // parents next month" came back as an event on a day nobody named, which is
        // the invented date the whole date contract exists to prevent — one month
        // out, stated as an assumption, and still not a day anybody agreed to. The
        // resolver cannot catch this: "next month" resolves perfectly well. Only the
        // choice of tool can, so the choice is described here.
        "Only when the person named the day. Something they intend with no day yet — 'next month', " +
        "'sometime in the summer' — is a plan; record it with create_memory instead of picking a date.";

    public JsonElement ParametersSchema => Schema;

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

        if (!arguments.TryGetProperty("date_expression", out var date) ||
            date.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(date.GetString()))
        {
            // events.starts_at is NOT NULL, so this is the one argument that cannot
            // be filled in later. A note with no time in it at all is
            // request_clarification's case, and the message says so.
            errors.Add(
                "'date_expression' is required — an event has to happen at some point, and " +
                "events.starts_at cannot be filled in later. If the note gives no time, call " +
                "request_clarification to ask for one.");
        }

        if (arguments.TryGetProperty("end_expression", out var end) &&
            end.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add("'end_expression' must be a string when supplied, copied from what the person wrote.");
        }

        if (arguments.TryGetProperty("all_day", out var allDay) &&
            allDay.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add("'all_day' must be true or false when supplied.");
        }

        if (arguments.TryGetProperty("category", out var category) &&
            (category.ValueKind != JsonValueKind.String ||
             !Categories.Contains(category.GetString()?.Trim().ToLowerInvariant())))
        {
            errors.Add($"'category' must be one of: {string.Join(", ", Categories)}.");
        }

        if (arguments.TryGetProperty("recurrence", out var recurrence) &&
            (recurrence.ValueKind != JsonValueKind.String ||
             RuleFor(recurrence.GetString()) is { Recognised: false }))
        {
            errors.Add("'recurrence' must be one of: none, yearly, monthly, weekly.");
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

        var allDay = arguments.TryGetProperty("all_day", out var allDayElement) &&
                     allDayElement.ValueKind == JsonValueKind.True;

        var expression = arguments.GetProperty("date_expression").GetString()!.Trim();

        var start = await ToolDate.ResolveAsync(_clock, expression, cancellationToken);

        if (start.Failed || start.Instant is null)
        {
            return ToolExecution.Failed(
                (start.Failure ?? "No date could be worked out from that.") +
                " An event has to happen at some point, so nothing was recorded.");
        }

        var notes = new List<string>();

        // An all-day event keeps the day and throws the hour away — but only the
        // hour this layer invented. "her birthday is September 12" resolves to 9am
        // because a reminder needs an hour; a birthday stored at 9am would render
        // as a nine-o'clock appointment. If the person did give a time, they did
        // not mean all-day, and the flag is what gets dropped instead.
        var startsAt = start.Instant.Value;

        if (allDay && start.HasExplicitTime)
        {
            allDay = false;
            notes.Add("kept the time you gave, so this is not an all-day event");

            if (start.Note is { } kept)
            {
                notes.Add(kept);
            }
        }
        else if (allDay)
        {
            startsAt = new DateTimeOffset(start.LocalDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            // The resolver's own sentence names the hour it filled in, and that hour
            // is exactly what this branch just discarded — so the reading is stated
            // as a date. The year still has to be in it: TOOLS.md requires "her
            // birthday is September 12" to say which September it landed on, because
            // a year-less date resolves to the next occurrence and a wrong guess is
            // only correctable if it is visible.
            if (start.Note is not null)
            {
                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{expression}\" read as {start.LocalDate:ddd d MMM yyyy}"));
            }
        }
        else if (start.Note is { } startNote)
        {
            notes.Add(startNote);
        }

        DateTimeOffset? endsAt = null;

        if (Text(arguments, "end_expression") is { } endExpression)
        {
            // Resolved relative to the start, because "until 11" carries a time and
            // no date and the date it means is the one the event begins on. Without
            // this it would resolve against today and an event next Saturday would
            // end this afternoon.
            var end = await ToolDate.ResolveAsync(_clock, endExpression, cancellationToken, startsAt);

            if (end.Failed)
            {
                return ToolExecution.Failed(end.Failure!);
            }

            endsAt = end.Instant;

            // "party saturday 8pm until 11" means eleven at night, and only this
            // method can know that: the resolver deliberately reads a bare hour as
            // written and leaves the 12-hour reading to "the one place that has the
            // start time to compare against", which is here. Applied only when the
            // hour could plausibly be the other half of the clock — a stated "7pm"
            // ending before an 8pm start is a mistake to report, not a reading to
            // correct — and stated either way.
            if (endsAt < startsAt && end.LocalHour <= 12)
            {
                var shifted = endsAt.Value.AddHours(12);

                if (shifted >= startsAt)
                {
                    endsAt = shifted;
                    notes.Add($"read \"{endExpression}\" as the evening, since the event starts earlier in the day");
                }
            }

            if (endsAt < startsAt)
            {
                // events_end_after_start would refuse this. Caught here so the
                // person gets a sentence rather than the block failing on a
                // constraint violation, and nothing is written from a note whose two
                // halves contradict each other.
                return ToolExecution.Failed(
                    $"\"{endExpression}\" came out before the event starts, so nothing was recorded.");
            }

            if (end.Note is { } endNote)
            {
                notes.Add(endNote);
            }
        }

        var calendarEvent = new CalendarEvent
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Title = arguments.GetProperty("title").GetString()!.Trim(),
            StartsAt = startsAt,
            EndsAt = endsAt,
            AllDay = allDay,
            Location = Text(arguments, "location"),
            Category = Text(arguments, "category")?.ToLowerInvariant(),
            RecurrenceRule = arguments.TryGetProperty("recurrence", out var recurrence)
                ? RuleFor(recurrence.GetString()).Rule
                : null,
        };

        await _writer.AddAsync(calendarEvent, cancellationToken);

        return ToolExecution.Created(
            "event",
            calendarEvent.Id,
            notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>
    /// The model's four-value enum to an RFC 5545 rule.
    ///
    /// <c>Recognised</c> is separate from a null rule because "none" and "nonsense"
    /// both produce no rule and only one of them is an error.
    /// </summary>
    private static (bool Recognised, string? Rule) RuleFor(string? recurrence) =>
        recurrence?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" => (true, null),
            "yearly" => (true, "FREQ=YEARLY"),
            "monthly" => (true, "FREQ=MONTHLY"),
            "weekly" => (true, "FREQ=WEEKLY"),
            _ => (false, null),
        };

    private static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;
}
