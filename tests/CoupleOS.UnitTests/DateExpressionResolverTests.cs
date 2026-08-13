using CoupleOS.Application.Time;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// The resolver that exists because models cannot be trusted with calendars, and
/// which therefore has to be trustworthy itself.
///
/// Every case fixes "now" explicitly. TOOLS.md's own eval set is the cautionary
/// tale: STATUS debt 21 records eight — actually six — cases carrying hard-coded
/// absolute dates that rot as today moves, so a test suite for a date resolver
/// that read the system clock would start failing on a Tuesday in March for
/// reasons unrelated to the code.
/// </summary>
public sealed class DateExpressionResolverTests
{
    /// <summary>
    /// Asia/Kolkata, the schema's default for couples.timezone, and deliberately
    /// half an hour off the hour: a bug that drops the offset entirely still lands
    /// on a plausible time in a whole-hour zone, and does not here.
    /// </summary>
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

    /// <summary>Wednesday 12 August 2026, 14:00 in Kolkata.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 14, 0, 0, TimeSpan.FromHours(5.5));

    private static DateResolution Resolve(string? expression, DateTimeOffset? now = null, DateTimeOffset? relativeTo = null) =>
        DateExpressionResolver.Resolve(expression, now ?? Now, Kolkata, relativeTo);

    /// <summary>The resolved instant as the couple would read it off a clock.</summary>
    private static string Local(DateResolution resolution) =>
        TimeZoneInfo.ConvertTime(resolution.Instant!.Value, Kolkata).ToString("yyyy-MM-dd HH:mm");

    [Theory]
    [InlineData("today", "2026-08-12 09:00")]
    [InlineData("tomorrow", "2026-08-13 09:00")]
    [InlineData("yesterday", "2026-08-11 09:00")]
    [InlineData("next week", "2026-08-19 09:00")]
    [InlineData("next month", "2026-09-12 09:00")]
    public void The_words_people_actually_use_resolve(string expression, string expected)
    {
        var resolved = Resolve(expression);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Equal(expected, Local(resolved));
    }

    [Theory]
    // Wednesday the 12th. A bare weekday means the soonest one, and Friday is in
    // two days.
    [InlineData("friday", "2026-08-14")]
    [InlineData("on friday", "2026-08-14")]
    [InlineData("this friday", "2026-08-14")]
    // "next friday" on a Wednesday means the Friday of next week, not the one in
    // two days. Dropping the qualifier is a week's error with nothing to show it.
    [InlineData("next friday", "2026-08-21")]
    // Today is Wednesday. A bare "wednesday" means the next one, because somebody
    // saying it today would have said "today".
    [InlineData("wednesday", "2026-08-19")]
    // Unless they qualified it, in which case they meant today.
    [InlineData("this wednesday", "2026-08-12")]
    [InlineData("tuesday", "2026-08-18")]
    public void Weekdays_resolve_forward_and_the_qualifier_is_not_dropped(string expression, string expectedDate)
    {
        var resolved = Resolve(expression);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith(expectedDate, Local(resolved), StringComparison.Ordinal);

        // Which Friday it chose is stated, because "friday" is the expression most
        // likely to be read differently by the person who wrote it.
        Assert.Contains("2026", resolved.Assumption);
    }

    [Theory]
    [InlineData("saturday 8pm", "2026-08-15 20:00")]
    [InlineData("friday 9am", "2026-08-14 09:00")]
    [InlineData("friday at 9am", "2026-08-14 09:00")]
    [InlineData("tomorrow 8:30pm", "2026-08-13 20:30")]
    [InlineData("tomorrow at 20:00", "2026-08-13 20:00")]
    [InlineData("tomorrow noon", "2026-08-13 12:00")]
    [InlineData("tomorrow midnight", "2026-08-13 00:00")]
    public void A_time_attached_to_a_date_is_read_as_given(string expression, string expected)
    {
        var resolved = Resolve(expression);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Equal(expected, Local(resolved));
        Assert.True(resolved.HasExplicitTime);
    }

    [Fact]
    public void A_date_with_no_time_says_which_hour_it_assumed()
    {
        // The whole rule in one case: completing an expression is allowed, doing it
        // silently is not. Midnight is what "just take the date" produces, and a
        // reminder at midnight is a reminder nobody sees.
        var resolved = Resolve("friday");

        Assert.True(resolved.Resolved);
        Assert.False(resolved.HasExplicitTime);
        Assert.EndsWith("09:00", Local(resolved), StringComparison.Ordinal);
        Assert.Contains("9am", resolved.Assumption);
        Assert.Contains("no time was given", resolved.Assumption);
    }

    [Theory]
    // September is next month, so this year.
    [InlineData("september 12", "2026-09-12")]
    [InlineData("12 september", "2026-09-12")]
    [InlineData("sep 12", "2026-09-12")]
    [InlineData("december 14", "2026-12-14")]
    [InlineData("december 14th", "2026-12-14")]
    // Already past this year, so it means next year. TOOLS.md's birthday case: the
    // current year would file it eight months in the past, where no reminder fires.
    [InlineData("march 3", "2027-03-03")]
    [InlineData("january 1", "2027-01-01")]
    public void A_year_less_date_resolves_to_the_next_occurrence_and_states_the_year(
        string expression,
        string expectedDate)
    {
        var resolved = Resolve(expression);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith(expectedDate, Local(resolved), StringComparison.Ordinal);

        // TOOLS.md, verbatim: the response states the year it assumed so a wrong
        // guess is correctable.
        Assert.Contains(expectedDate[..4], resolved.Assumption);
    }

    [Fact]
    public void A_stated_year_is_believed_even_when_it_is_in_the_past()
    {
        // "we went to Munnar in July 2024" is a memory, not a mistake. Rolling a
        // stated year forward would rewrite the person's own sentence.
        var resolved = Resolve("july 14 2024");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith("2024-07-14", Local(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void The_twenty_ninth_of_february_lands_on_a_year_that_has_one()
    {
        // An anniversary on a leap day is a real input, and rolling it to 1 March
        // would be a different date. 2028 is the next leap year after 2026.
        var resolved = Resolve("february 29");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith("2028-02-29", Local(resolved), StringComparison.Ordinal);
        Assert.Contains("2028", resolved.Assumption);
    }

    [Theory]
    [InlineData("in three days", "2026-08-15")]
    [InlineData("in 3 days", "2026-08-15")]
    [InlineData("in two weeks", "2026-08-26")]
    [InlineData("for two weeks", "2026-08-26")]
    [InlineData("in a month", "2026-09-12")]
    // Not something a person writes, and exactly what eval case amb-006 contains.
    [InlineData("+30d", "2026-09-11")]
    [InlineData("+2w", "2026-08-26")]
    public void Durations_resolve_whether_written_as_words_digits_or_shorthand(
        string expression,
        string expectedDate)
    {
        var resolved = Resolve(expression);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith(expectedDate, Local(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void Since_a_month_means_the_first_of_the_most_recent_one()
    {
        // search_memory's since_expression. June has passed this year, so it means
        // this June rather than next.
        var resolved = Resolve("since june");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith("2026-06-01", Local(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void A_month_still_ahead_means_the_one_that_has_already_happened()
    {
        // "since december", said in August, cannot mean a December that has not
        // arrived — a search window ending before it starts returns nothing and
        // looks like an empty database.
        var resolved = Resolve("since december");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith("2025-12-01", Local(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void A_bare_time_means_today_when_today_still_has_it()
    {
        // 14:00 now, so 8pm is still ahead.
        var resolved = Resolve("8pm");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Equal("2026-08-12 20:00", Local(resolved));
        Assert.DoesNotContain("tomorrow", resolved.Assumption ?? string.Empty);
    }

    [Fact]
    public void A_bare_time_already_gone_means_tomorrow_and_says_so()
    {
        // 22:00 now. "8pm" is not a request to travel backwards, and a reminder
        // resolved into the past never fires.
        var lateEvening = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.FromHours(5.5));

        var resolved = Resolve("8pm", now: lateEvening);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Equal("2026-08-13 20:00", Local(resolved));
        Assert.Contains("tomorrow", resolved.Assumption);
    }

    [Fact]
    public void A_bare_time_attached_to_a_weekday_is_never_rolled_forward_a_day()
    {
        // The bug the time-only check exists to prevent: rolling "friday 9am"
        // forward because 9am has passed today would move it to Saturday.
        var lateEvening = new DateTimeOffset(2026, 8, 12, 22, 0, 0, TimeSpan.FromHours(5.5));

        var resolved = Resolve("friday 9am", now: lateEvening);

        Assert.Equal("2026-08-14 09:00", Local(resolved));
    }

    [Fact]
    public void The_end_of_a_range_takes_its_date_from_the_start()
    {
        // create_event's end_expression. "until 11" carries a time and no date, and
        // the date it means is the one the event starts on — resolving it against
        // today would put the end before the start and trip events_end_after_start.
        var start = new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.FromHours(5.5));

        var resolved = Resolve("until 11pm", relativeTo: start);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Equal("2026-08-15 23:00", Local(resolved));
    }

    [Fact]
    public void The_couples_timezone_is_what_decides_which_day_tomorrow_is()
    {
        // 23:30 in Kolkata is 18:00 UTC the same day, so a resolver working in UTC
        // agrees by luck here. The half-hour offset is the part that does not
        // survive dropping the zone.
        var nearMidnight = new DateTimeOffset(2026, 8, 12, 23, 30, 0, TimeSpan.FromHours(5.5));

        var resolved = Resolve("tomorrow 8am", now: nearMidnight);

        Assert.Equal("2026-08-13 08:00", Local(resolved));

        // And the instant carries the couple's offset, not the server's.
        Assert.Equal(TimeSpan.FromHours(5.5), resolved.Instant!.Value.Offset);
    }

    [Fact]
    public void A_local_time_that_does_not_exist_is_moved_forward_and_the_move_is_stated()
    {
        // Not reachable in Asia/Kolkata, which is why it is tested: couples.timezone
        // is free text, and the first couple to set it to a zone with daylight
        // saving must not be the test.
        //
        // London clocks go forward at 01:00 GMT on 29 March 2026, so the wall times
        // 01:00–01:59 never happen that day. ConvertTimeToUtc throws on one rather
        // than returning something wrong, so the naive implementation is a 500 on
        // somebody's reminder. 02:30 was the first version of this case and it
        // passed for the wrong reason — that time exists, as BST.
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var beforeTheGap = new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero);

        var resolved = DateExpressionResolver.Resolve("tomorrow 1:30", beforeTheGap, london);

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.Contains("does not exist", resolved.Assumption);

        // Moved forward to the first instant that does exist, which is when the
        // clocks land: 02:00 BST.
        var local = TimeZoneInfo.ConvertTime(resolved.Instant!.Value, london);
        Assert.Equal(new DateTime(2026, 3, 29), local.Date);
        Assert.Equal(new TimeSpan(2, 0, 0), local.TimeOfDay);
    }

    [Theory]
    [InlineData("sometime next month")]
    [InlineData("after the wedding")]
    [InlineData("when we get back")]
    [InlineData("soon")]
    [InlineData("the usual time")]
    [InlineData("whenever")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void What_is_not_a_date_is_refused_rather_than_guessed(string? expression)
    {
        // TOOLS.md: a resolver that cannot parse the expression must fail the call
        // rather than pick a date. events.starts_at is NOT NULL, and the tempting
        // way to satisfy that is to invent something — which is how a note becomes
        // a calendar entry nobody asked for on a date nobody chose.
        var resolved = Resolve(expression);

        Assert.False(resolved.Resolved);
        Assert.Null(resolved.Instant);
        Assert.NotNull(resolved.Failure);
    }

    [Fact]
    public void A_refusal_quotes_the_expression_back()
    {
        // The message reaches the person who wrote it, and they have to write
        // something different. "Could not parse input" tells them nothing about
        // which of three dates in their note was the problem.
        var resolved = Resolve("after the wedding");

        Assert.Contains("after the wedding", resolved.Failure);
    }

    [Theory]
    [InlineData("february 30")]
    [InlineData("13 13")]
    [InlineData("september 99")]
    public void An_impossible_date_is_refused_and_not_rolled_into_the_next_month(string expression)
    {
        // DateTime would happily take 30 February as 2 March in some parsers. A
        // date the person cannot have meant is a question, not an adjustment.
        Assert.False(Resolve(expression).Resolved);
    }

    [Fact]
    public void An_iso_date_is_accepted_rather_than_refused()
    {
        // Nothing upstream should be sending one — the whole design is that the
        // model emits expressions — but refusing a value that is unambiguous would
        // be pedantry with a cost.
        var resolved = Resolve("2026-12-14");

        Assert.True(resolved.Resolved, resolved.Failure);
        Assert.StartsWith("2026-12-14", Local(resolved), StringComparison.Ordinal);
    }

    [Fact]
    public void Punctuation_and_case_do_not_change_the_answer()
    {
        var plain = Resolve("saturday 8pm");
        var dressed = Resolve("  On Saturday, 8PM. ");

        Assert.Equal(Local(plain), Local(dressed));
    }
}
