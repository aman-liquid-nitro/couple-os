using System.Globalization;
using System.Text.RegularExpressions;

namespace CoupleOS.Application.Time;

/// <summary>
/// Turns the verbatim expressions the model emits into instants, in the couple's
/// own timezone — STATUS debt 22, and the thing five of M3's six tools cannot
/// ship without.
///
/// **Why this exists at all.** TOOLS.md records the measurement: given a schema
/// asking for an ISO <c>starts_at</c>, three separate models resolved "Saturday
/// 8pm" against their own training cutoffs and returned dates wrong by 7 months,
/// 13 months and nearly three years — no error, a plausible timestamp, a calendar
/// entry in 2023. Given the same note with <c>date_expression</c>, all three
/// returned "Saturday 8pm" verbatim. So the model does no calendar arithmetic and
/// this does all of it.
///
/// **Two rules govern every line below.**
///
/// It refuses rather than guesses. An expression it cannot read returns a failure
/// and the tool call fails — TOOLS.md is explicit that a resolver which cannot
/// parse must fail rather than pick a date, because <c>events.starts_at</c> is
/// NOT NULL and the tempting way to satisfy that is to invent something.
///
/// It never completes an expression silently. Filling in a year, an hour, or
/// which Friday is allowed; doing it without saying so is not. Every such
/// decision comes back in <see cref="DateResolution.Assumption"/>.
///
/// **What it deliberately does not do.** This is a small, closed grammar, not a
/// natural-language date parser, and the list of what it refuses is as designed as
/// the list it accepts. "Sometime next month", "after the wedding", "when we get
/// back" are not dates and become clarifying questions. Widening the grammar is
/// cheap; widening it accidentally, so that a phrase nobody tested lands on a
/// plausible wrong date, is the failure this class exists to prevent.
/// </summary>
public static class DateExpressionResolver
{
    /// <summary>
    /// The hour a bare date resolves to when no time was given.
    ///
    /// Nine in the morning, and stated as an assumption every time. A reminder set
    /// for midnight is a reminder nobody sees, and midnight is what any "just take
    /// the date" implementation produces.
    /// </summary>
    private const int AssumedHour = 9;

    private static readonly string[] Weekdays =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>
    /// Number words up to twelve. Past that people write digits, and a resolver
    /// that understands "seventeen" and not "nineteen" is worse than one that
    /// understands neither, because the gap is invisible until it is hit.
    /// </summary>
    private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12,
    };

    /// <summary>
    /// Resolves <paramref name="expression"/> against the couple's clock.
    /// </summary>
    /// <param name="now">
    /// The instant "today" means. Passed in rather than read from a clock, so this
    /// is a pure function and so its tests do not drift with the calendar — the
    /// eval set already learned that lesson the other way round (STATUS debt 21,
    /// whose hard-coded absolute dates rot as "today" moves).
    /// </param>
    /// <param name="zone">
    /// The couple's timezone, from <c>couples.timezone</c>. Not the server's: a
    /// couple in Asia/Kolkata writing "tomorrow" at 11pm means their tomorrow, and
    /// a container running UTC would have already started it.
    /// </param>
    /// <param name="relativeTo">
    /// An earlier instant this expression continues from, for the end of a range —
    /// "until 11" carries a time and no date, and the date it means is the one the
    /// event starts on. Null for a standalone expression.
    /// </param>
    public static DateResolution Resolve(
        string? expression,
        DateTimeOffset now,
        TimeZoneInfo zone,
        DateTimeOffset? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return DateResolution.Unreadable(expression);
        }

        var text = Normalize(expression);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var anchor = relativeTo is { } start ? TimeZoneInfo.ConvertTime(start, zone).Date : localNow.Date;

        // The time is lifted out first and the remainder is read as a date. Doing
        // it the other way round means every date pattern below has to know about
        // every time pattern, which is how a grammar becomes untestable.
        var (withoutTime, time) = ExtractTime(text);

        var date = ResolveDate(withoutTime, localNow, anchor, out var dateAssumption);

        if (date is null)
        {
            return DateResolution.Unreadable(expression);
        }

        var assumptions = new List<string>();

        if (dateAssumption is not null)
        {
            assumptions.Add(dateAssumption);
        }

        var timeOfDay = time;

        if (timeOfDay is null)
        {
            timeOfDay = new TimeSpan(AssumedHour, 0, 0);
            assumptions.Add($"{AssumedHour}am, since no time was given");
        }

        var local = date.Value.Add(timeOfDay.Value);

        // A bare time with no date means today — unless today's has gone, in which
        // case it means tomorrow, and that gets said. "8pm" typed at nine in the
        // evening is not a request to travel backwards.
        if (IsTimeOnly(withoutTime) && relativeTo is null && local < localNow.DateTime)
        {
            local = local.AddDays(1);
            assumptions.Add("tomorrow, since that time has passed today");
        }

        var instant = ToInstant(local, zone, assumptions);

        return DateResolution.Of(
            instant,
            hasExplicitTime: time is not null,
            assumptions.Count == 0 ? null : string.Join("; ", assumptions));
    }

    /// <summary>
    /// A local wall-clock time to an instant, handling the two cases a naive
    /// conversion gets wrong.
    ///
    /// Both are real and neither is reachable in Asia/Kolkata, which is exactly why
    /// they are handled: <c>couples.timezone</c> is free text with a default, and
    /// the first couple to set it to something with daylight saving must not be the
    /// test. A local time inside the spring-forward gap does not exist, and
    /// <c>ConvertTimeToUtc</c> throws on it rather than returning something wrong —
    /// so it is moved to the first instant that does exist. A local time in the
    /// autumn overlap happens twice, and the earlier reading is taken.
    /// </summary>
    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone, List<string> assumptions)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // Walk forward in minutes rather than jumping an hour: gaps are
            // usually sixty minutes and are not required to be.
            var shifted = unspecified;

            while (zone.IsInvalidTime(shifted))
            {
                shifted = shifted.AddMinutes(1);
            }

            assumptions.Add(
                $"{shifted:HH:mm}, because {unspecified:HH:mm} does not exist on this date in {zone.Id}");

            unspecified = shifted;
        }

        var offset = zone.IsAmbiguousTime(unspecified)
            ? zone.GetAmbiguousTimeOffsets(unspecified).Max()
            : zone.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset);
    }

    /// <summary>
    /// The date half of the expression, or null when it cannot be read.
    ///
    /// Ordered longest-pattern-first. "next friday" has to be tried before
    /// "friday", or the qualifier is silently dropped and the answer is a week out
    /// with no indication that anything was ignored.
    /// </summary>
    private static DateTime? ResolveDate(
        string text,
        DateTimeOffset localNow,
        DateTime anchor,
        out string? assumption)
    {
        assumption = null;
        var today = localNow.Date;

        if (text.Length == 0)
        {
            // Time only. The anchor is today for a standalone expression, or the
            // start date when this is the end of a range.
            return anchor;
        }

        switch (text)
        {
            case "today" or "tonight":
                return today;
            case "tomorrow":
                return today.AddDays(1);
            case "yesterday":
                return today.AddDays(-1);
            case "next week":
                assumption = "one week from today";
                return today.AddDays(7);
            case "next month":
                assumption = "one month from today";
                return today.AddMonths(1);
            case "next year":
                assumption = "one year from today";
                return today.AddYears(1);
        }

        // "in three days", "for two weeks", "in a month". "for" reads as a
        // duration and "in" as a delay, and they land on the same date — which is
        // why create_memory's expires_expression can say "for two weeks".
        if (Match(text, @"^(?:in|after|for)\s+(?<n>\d+|[a-z]+)\s+(?<unit>day|days|week|weeks|month|months|year|years)$") is { } spanMatch &&
            Count(spanMatch.Groups["n"].Value) is { } count)
        {
            return Add(today, count, spanMatch.Groups["unit"].Value);
        }

        // "+30d", "+2w" — not something a person writes, and exactly what the eval
        // set's amb-006 case contains, so it is read rather than refused.
        if (Match(text, @"^\+(?<n>\d+)(?<unit>d|w|m|y)$") is { } shorthand)
        {
            return Add(today, int.Parse(shorthand.Groups["n"].Value, CultureInfo.InvariantCulture),
                shorthand.Groups["unit"].Value switch
                {
                    "d" => "days",
                    "w" => "weeks",
                    "m" => "months",
                    _ => "years",
                });
        }

        // "friday", "next friday", "this friday", "on friday".
        if (Match(text, @"^(?:on\s+)?(?<q>next|this|coming)?\s*(?<day>sunday|monday|tuesday|wednesday|thursday|friday|saturday)$") is { } dayMatch)
        {
            var target = Array.IndexOf(Weekdays, dayMatch.Groups["day"].Value);
            var ahead = (target - (int)today.DayOfWeek + 7) % 7;

            // "next friday" said on a Wednesday means the Friday of next week, not
            // the one in two days. "this friday" and a bare "friday" mean the
            // soonest one. Today counts as the soonest only when qualified,
            // because "friday" said on Friday almost always means the next one.
            var qualifier = dayMatch.Groups["q"].Value;

            if (ahead == 0)
            {
                ahead = qualifier == "this" ? 0 : 7;
            }

            if (qualifier == "next" && ahead < 7)
            {
                ahead += 7;
            }

            var resolved = today.AddDays(ahead);
            assumption = resolved.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

            return resolved;
        }

        // "september 12", "12 september", "dec 14th", and the same with a year.
        if (ResolveMonthDay(text, today, out var monthDay, out assumption))
        {
            return monthDay;
        }

        // "since june" — a month name alone, which search_memory's
        // since_expression is for. The first of that month, most recent occurrence.
        if (Match(text, @"^(?:since\s+|from\s+)?(?<month>[a-z]+)$") is { } monthMatch &&
            MonthOf(monthMatch.Groups["month"].Value) is { } month)
        {
            var year = month > today.Month ? today.Year - 1 : today.Year;
            assumption = $"1 {CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month)} {year}";

            return new DateTime(year, month, 1);
        }

        // An ISO date, because something upstream may already have resolved one and
        // round-tripping it is better than refusing it.
        if (DateTime.TryParseExact(
                text,
                ["yyyy-MM-dd", "yyyy/MM/dd"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var iso))
        {
            return iso.Date;
        }

        return null;
    }

    /// <summary>
    /// A month and a day, with the year filled in as the next occurrence when it is
    /// missing.
    ///
    /// The next occurrence and not the current year, because TOOLS.md's example is a
    /// birthday: "her birthday is September 12" said in December means next
    /// September, and a resolver that picks the current year files it ten months in
    /// the past where no reminder will ever fire. The year chosen is always stated.
    /// </summary>
    private static bool ResolveMonthDay(string text, DateTime today, out DateTime resolved, out string? assumption)
    {
        resolved = default;
        assumption = null;

        var match = Match(text, @"^(?:on\s+)?(?<a>[a-z]+|\d{1,2})\s+(?<b>[a-z]+|\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<year>\d{4}))?$")
            ?? Match(text, @"^(?:on\s+)?(?<a>[a-z]+)\s+(?<b>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<year>\d{4}))?$");

        if (match is null)
        {
            return false;
        }

        var a = match.Groups["a"].Value;

        // Not trimmed of an ordinal suffix here — the pattern already consumes one
        // as its own group. Trimming "st"/"nd"/"rd"/"th" off the captured token was
        // the first implementation and it ate month names from the right:
        // "september" lost its "r" and became "septembe", so "12 september"
        // resolved to nothing while "september 12" worked.
        var b = match.Groups["b"].Value;

        int month;
        int day;

        if (MonthOf(a) is { } fromName && int.TryParse(b, CultureInfo.InvariantCulture, out day))
        {
            month = fromName;
        }
        else if (int.TryParse(a, CultureInfo.InvariantCulture, out day) && MonthOf(b) is { } fromSecond)
        {
            month = fromSecond;
        }
        else
        {
            return false;
        }

        if (day is < 1 or > 31)
        {
            return false;
        }

        if (match.Groups["year"].Success)
        {
            var stated = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);

            if (!Valid(stated, month, day, out resolved))
            {
                return false;
            }

            return true;
        }

        // 29 February in a year that does not have one is a real input — an
        // anniversary — and rolling it silently to 1 March would be wrong. Walk
        // forward to a year that has the date, and say which.
        for (var year = today.Year; year <= today.Year + 8; year++)
        {
            if (!Valid(year, month, day, out var candidate) || candidate < today)
            {
                continue;
            }

            resolved = candidate;
            assumption = $"{resolved:d MMMM yyyy}";

            return true;
        }

        return false;
    }

    private static bool Valid(int year, int month, int day, out DateTime date)
    {
        if (day > DateTime.DaysInMonth(year, month))
        {
            date = default;

            return false;
        }

        date = new DateTime(year, month, day);

        return true;
    }

    /// <summary>
    /// Pulls a time off the end of the expression and returns what is left.
    ///
    /// "8pm", "8:30pm", "at 9am", "20:00", "until 11", "noon", "midnight". A bare
    /// hour with no meridiem is the ambiguous one: "until 11" after an 8pm start is
    /// obviously 11pm and "at 11" on its own is probably morning, and neither
    /// reading is safe to hard-code — so a bare 1–11 with no meridiem is read as
    /// written and the caller sees HasExplicitTime, while the 12-hour guess is left
    /// to the one place that has the start time to compare against.
    /// </summary>
    private static (string Remainder, TimeSpan? Time) ExtractTime(string text)
    {
        if (Match(text, @"\b(?:at\s+|until\s+|till\s+|by\s+|from\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<mer>am|pm)\b") is { } meridiem)
        {
            var hour = int.Parse(meridiem.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = meridiem.Groups["m"].Success
                ? int.Parse(meridiem.Groups["m"].Value, CultureInfo.InvariantCulture)
                : 0;

            if (hour is < 1 or > 12 || minute > 59)
            {
                return (text, null);
            }

            hour = meridiem.Groups["mer"].Value == "pm"
                ? (hour == 12 ? 12 : hour + 12)
                : (hour == 12 ? 0 : hour);

            return (Strip(text, meridiem), new TimeSpan(hour, minute, 0));
        }

        if (Match(text, @"\b(?:at\s+|until\s+|till\s+|by\s+|from\s+)?(?<h>[01]?\d|2[0-3]):(?<m>\d{2})\b") is { } clock)
        {
            return (
                Strip(text, clock),
                new TimeSpan(
                    int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture),
                    int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture),
                    0));
        }

        // A bare hour, and only where a preposition marks it as one: "at 9",
        // "until 11". The preposition is what keeps "in 3 days" from being read as
        // three o'clock and "the 14th" from becoming two in the afternoon, which is
        // why an unmarked number is left to the date patterns.
        //
        // This case was described in this comment before it was implemented: the
        // paragraph above claimed a bare 1–11 was "read as written", and no pattern
        // did it, so "party saturday 8pm until 11" failed as unreadable. Found by
        // create_event, which is the caller the last sentence was written for.
        if (Match(text, @"\b(?:at|until|till|by|from)\s+(?<h>\d{1,2})(?!\s*[:\d])\b") is { } bare)
        {
            var hour = int.Parse(bare.Groups["h"].Value, CultureInfo.InvariantCulture);

            if (hour <= 23)
            {
                return (Strip(text, bare), new TimeSpan(hour, 0, 0));
            }
        }

        if (Match(text, @"\bnoon\b") is { } noon)
        {
            return (Strip(text, noon), new TimeSpan(12, 0, 0));
        }

        if (Match(text, @"\bmidnight\b") is { } midnight)
        {
            return (Strip(text, midnight), TimeSpan.Zero);
        }

        return (text, null);
    }

    /// <summary>
    /// Whether what remained after the time was removed carries no date at all.
    ///
    /// "at 8pm" leaves nothing; "friday 8pm" leaves "friday". Only the first may be
    /// rolled to tomorrow, because rolling "friday" forward a day would move it to
    /// Saturday.
    /// </summary>
    private static bool IsTimeOnly(string remainder) => remainder.Length == 0;

    private static string Strip(string text, Match match) =>
        Normalize(text.Remove(match.Index, match.Length));

    private static Match? Match(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            is { Success: true } match
            ? match
            : null;

    /// <summary>
    /// Lowercased, punctuation that carries no meaning removed, whitespace
    /// collapsed. "on Friday," and "friday" are one expression.
    /// </summary>
    private static string Normalize(string text) =>
        Regex.Replace(text.ToLowerInvariant().Replace(",", " ").Replace(".", " "), @"\s+", " ",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();

    private static int? Count(string token) =>
        int.TryParse(token, CultureInfo.InvariantCulture, out var digits)
            ? digits
            : Numbers.TryGetValue(token, out var word) ? word : null;

    private static DateTime Add(DateTime from, int count, string unit) => unit switch
    {
        "day" or "days" => from.AddDays(count),
        "week" or "weeks" => from.AddDays(count * 7),
        "month" or "months" => from.AddMonths(count),
        _ => from.AddYears(count),
    };

    /// <summary>
    /// A month from its name or its abbreviation, invariant. Not
    /// <c>DateTime.TryParse</c>: that accepts a great deal more than a month name
    /// and would quietly turn "the" into something.
    /// </summary>
    private static int? MonthOf(string token)
    {
        var format = CultureInfo.InvariantCulture.DateTimeFormat;

        for (var month = 1; month <= 12; month++)
        {
            if (token.Equals(format.GetMonthName(month), StringComparison.OrdinalIgnoreCase) ||
                token.Equals(format.GetAbbreviatedMonthName(month), StringComparison.OrdinalIgnoreCase))
            {
                return month;
            }
        }

        return null;
    }
}
