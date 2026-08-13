namespace CoupleOS.Application.Time;

/// <summary>
/// What an expression resolved to, or why it did not.
///
/// A refusal is a result rather than an exception, for the same reason
/// <c>ToolValidation</c> is: TOOLS.md requires a resolver that cannot read an
/// expression to fail the call rather than pick a date, and the caller has to be
/// able to turn that into a sentence for the person who wrote it.
/// </summary>
/// <param name="Assumption">
/// What was filled in that the person did not say — the year, the time of day,
/// which Friday. Null when nothing was assumed.
///
/// This is not decoration. TOOLS.md requires the response to state the year it
/// assumed "so a wrong guess is correctable", and the general rule behind that
/// is the one worth keeping: a resolver is allowed to complete an expression, and
/// is not allowed to do it silently. The alternative is a calendar entry in the
/// wrong year that looks exactly like a right one.
/// </param>
/// <param name="HasExplicitTime">
/// False when the expression carried a date and no time. The caller decides what
/// that means — an all-day event, or a reminder at the assumed hour — because the
/// answer differs per tool and the resolver has no business knowing which one
/// called it.
/// </param>
public sealed record DateResolution(
    DateTimeOffset? Instant,
    bool HasExplicitTime = false,
    string? Assumption = null,
    string? Failure = null)
{
    public bool Resolved => Failure is null && Instant is not null;

    public static DateResolution Of(DateTimeOffset instant, bool hasExplicitTime, string? assumption = null) =>
        new(instant, hasExplicitTime, assumption);

    /// <summary>
    /// Could not be read. The expression is quoted back verbatim because the
    /// message reaches a person who wrote it and will have to write it differently.
    /// </summary>
    public static DateResolution Unreadable(string? expression) =>
        new(null, Failure: string.IsNullOrWhiteSpace(expression)
            ? "No date was given."
            : $"I could not work out a date from \"{expression.Trim()}\".");
}
