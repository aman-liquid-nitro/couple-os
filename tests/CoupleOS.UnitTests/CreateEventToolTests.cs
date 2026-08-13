using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// The tool M2's founding defect was about: the dinner line that produced no
/// call, no report entry and no error because nothing was registered to take it.
///
/// What is asserted here is mostly the two translations this tool performs —
/// a four-value recurrence into an RFC 5545 rule, and an all-day flag into a date
/// with the assumed hour thrown away — plus the refusals, which is where a
/// calendar earns or loses trust.
/// </summary>
public sealed class CreateEventToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    private sealed class RecordingEventWriter : IEventWriter
    {
        public List<CalendarEvent> Written { get; } = [];

        public Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
        {
            Written.Add(calendarEvent);

            return Task.CompletedTask;
        }
    }

    private static (CreateEventTool Tool, RecordingEventWriter Writer) Build()
    {
        var writer = new RecordingEventWriter();

        return (new CreateEventTool(writer, new FixedCoupleClock()), writer);
    }

    private static ToolInvocation Call(string json, Visibility visibility = Visibility.SharedCouple) =>
        Invocation(json, CoupleId, UserId, visibility);

    private static DateTime Local(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, Kolkata).DateTime;

    [Fact]
    public async Task A_dinner_with_a_time_becomes_a_row_at_that_time()
    {
        // The M0 note, finally landing somewhere: "dinner at Priya's parents on
        // Saturday 8pm". Now on a Thursday, so Saturday is two days out.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"dinner at Priya's parents","date_expression":"saturday 8pm","category":"family"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Equal("event", execution.EntityType);

        var calendarEvent = Assert.Single(writer.Written);
        Assert.Equal(new DateTime(2026, 8, 15, 20, 0, 0), Local(calendarEvent.StartsAt));
        Assert.Equal("family", calendarEvent.Category);
        Assert.False(calendarEvent.AllDay);
        Assert.Null(calendarEvent.EndsAt);
        Assert.Null(calendarEvent.RecurrenceRule);

        // Which Saturday, stated. A bare weekday is the expression most likely to be
        // read differently by the person and the resolver.
        Assert.Contains("15 Aug 2026", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_birthday_is_stored_as_a_day_and_says_which_year_it_chose()
    {
        // TOOLS.md's own example. A year-less date resolves to the next occurrence,
        // and the response has to state the year "so a wrong guess is correctable".
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"Priya's birthday","date_expression":"September 12","all_day":true,"category":"birthday","recurrence":"yearly"}"""));

        var calendarEvent = Assert.Single(writer.Written);
        Assert.True(calendarEvent.AllDay);
        Assert.Equal("FREQ=YEARLY", calendarEvent.RecurrenceRule);

        // Midnight, not the resolver's 9am. A birthday stored at 9am renders as a
        // nine-o'clock appointment.
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0), calendarEvent.StartsAt.DateTime);
        Assert.Equal(TimeSpan.Zero, calendarEvent.StartsAt.Offset);

        Assert.Contains("12 Sep 2026", execution.Note!, StringComparison.Ordinal);

        // And the hour this branch discarded is not mentioned, because it is no
        // longer true of the row.
        Assert.DoesNotContain("9am", execution.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_time_given_outranks_an_all_day_flag_and_the_disagreement_is_reported()
    {
        // Both cannot be honoured. The person's own words win, and saying so is what
        // stops the row from quietly disagreeing with the note it came from.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"anniversary dinner","date_expression":"December 14 8pm","all_day":true}"""));

        var calendarEvent = Assert.Single(writer.Written);
        Assert.False(calendarEvent.AllDay);
        Assert.Equal(new DateTime(2026, 12, 14, 20, 0, 0), Local(calendarEvent.StartsAt));
        Assert.Contains("not an all-day event", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_end_time_is_read_against_the_day_the_event_starts_on()
    {
        // "until 11" carries a time and no date. Resolved against today it would end
        // this evening; the date it means is the one the event begins on.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"party","date_expression":"saturday 8pm","end_expression":"until 11pm"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var calendarEvent = Assert.Single(writer.Written);
        Assert.Equal(new DateTime(2026, 8, 15, 23, 0, 0), Local(calendarEvent.EndsAt!.Value));
    }

    [Fact]
    public async Task A_bare_hour_at_the_end_of_a_range_is_read_as_the_evening_and_says_so()
    {
        // "party saturday 8pm until 11" means eleven at night, and the resolver
        // deliberately does not decide that: it reads a bare hour as written and
        // leaves the 12-hour reading to the one place holding the start time to
        // compare against. This is that place.
        //
        // The resolver could not read "until 11" at all when this test was written —
        // its own comment claimed a bare hour was "read as written" and no pattern
        // did it, so the expression failed as unreadable. Found here, fixed there.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"party","date_expression":"saturday 8pm","end_expression":"until 11"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var calendarEvent = Assert.Single(writer.Written);
        Assert.Equal(new DateTime(2026, 8, 15, 23, 0, 0), Local(calendarEvent.EndsAt!.Value));
        Assert.Contains("as the evening", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_end_that_is_stated_and_still_before_the_start_is_refused()
    {
        // events_end_after_start would refuse this at the database and fail the whole
        // block. Caught here, it is a sentence — and the 12-hour reading above does
        // not apply, because a stated "7pm" is not half a clock away from what the
        // person meant, it is a mistake worth reporting.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"party","date_expression":"saturday 8pm","end_expression":"until 7pm"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Contains("before the event starts", execution.Error!, StringComparison.Ordinal);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_date_that_cannot_be_read_writes_nothing_and_says_so()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"title":"trip","date_expression":"sometime after the wedding"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Contains("nothing was recorded", execution.Error!, StringComparison.Ordinal);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_missing_date_names_the_tool_that_should_have_been_called()
    {
        // events.starts_at is NOT NULL, so this is the one argument that cannot be
        // filled in later — and the note with no time in it is
        // request_clarification's case, which is what unknowndate-002 asserts.
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(Json("""{"title":"the electricity bill"}"""));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("request_clarification", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"title":"x","date_expression":"friday","category":"wedding"}""", "'category'")]
    [InlineData("""{"title":"x","date_expression":"friday","recurrence":"fortnightly"}""", "'recurrence'")]
    [InlineData("""{"title":"x","date_expression":"friday","all_day":"yes"}""", "'all_day'")]
    public async Task Values_outside_the_closed_sets_are_refused(string arguments, string expected)
    {
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(Json(arguments));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Recurrence_none_is_a_valid_answer_and_not_a_rule()
    {
        // "none" and "nonsense" both produce no rule, and only one of them is an
        // error — which is why the translation reports whether it recognised the
        // word rather than only what it produced.
        var (tool, writer) = Build();

        var validation = await tool.ValidateAsync(Json("""{"title":"x","date_expression":"friday","recurrence":"none"}"""));
        Assert.True(validation.IsValid);

        await tool.ExecuteAsync(Call("""{"title":"x","date_expression":"friday","recurrence":"none"}"""));
        Assert.Null(Assert.Single(writer.Written).RecurrenceRule);
    }

    [Fact]
    public async Task A_private_event_carries_its_owner()
    {
        var (tool, writer) = Build();

        await tool.ExecuteAsync(
            Call("""{"title":"table booked for the proposal","date_expression":"saturday 8pm"}""", Visibility.PrivateUser));

        var calendarEvent = Assert.Single(writer.Written);
        Assert.Equal(Visibility.PrivateUser, calendarEvent.Visibility);
        Assert.Equal(UserId, calendarEvent.OwnerUserId);
    }

    [Fact]
    public void The_model_is_offered_no_word_for_the_columns_it_must_not_choose()
    {
        var (tool, _) = Build();

        var properties = tool.ParametersSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        // starts_at and ends_at are absent: the model sends words, the application
        // resolves them. This is the tool where that rule was measured.
        Assert.DoesNotContain("starts_at", properties);
        Assert.DoesNotContain("ends_at", properties);
        Assert.Contains("date_expression", properties);
    }
}
