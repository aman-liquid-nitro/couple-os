using CoupleOS.Application.Time;

namespace CoupleOS.Application.Tools;

/// <summary>
/// A verbatim date argument, turned into an instant the same way in every tool.
///
/// Five of M3's tools take an expression the person wrote and none of them should
/// own the rules about it: the model does no calendar arithmetic
/// (<see cref="DateExpressionResolver"/> records the measurement), the couple's
/// timezone decides what "tomorrow" means, an unreadable expression fails the call
/// rather than becoming a guess, and anything filled in is stated. Written once
/// here so that four later tools cannot each get one of those slightly wrong.
/// </summary>
/// <param name="Instant">Null when no expression was given, or when it could not be read.</param>
/// <param name="Note">
/// What was assumed, phrased for the person who wrote the expression. Null when
/// nothing was assumed — a full date and time needs no commentary, and a note on
/// every row would train both partners to stop reading them.
/// </param>
/// <param name="Failure">
/// Why the expression could not be read. Non-null means the tool call must fail:
/// TOOLS.md is explicit that a resolver which cannot parse must fail rather than
/// pick a date.
/// </param>
public sealed record ToolDate(DateTimeOffset? Instant, string? Note = null, string? Failure = null)
{
    /// <summary>Nothing was given, which is only a problem for the tools that require one.</summary>
    /// <remarks>
    /// The cast is load-bearing: a record's compiler-generated copy constructor
    /// takes a single <c>ToolDate</c>, so a bare <c>new(null)</c> binds to that
    /// one and does not compile.
    /// </remarks>
    public static ToolDate Absent { get; } = new((DateTimeOffset?)null);

    public bool Failed => Failure is not null;

    public bool Present => Instant is not null;

    /// <summary>
    /// Resolves <paramref name="expression"/> against the couple's clock, or
    /// returns <see cref="Absent"/> when there is nothing to resolve.
    ///
    /// A blank expression counts as absent rather than as a failure. A model that
    /// emits <c>"due_expression": ""</c> has said it had no date, which for a task
    /// is legal and for a reminder is caught by that tool's own validation — and
    /// failing the whole line over an empty string would lose a note that was
    /// otherwise fine.
    /// </summary>
    public static async Task<ToolDate> ResolveAsync(
        ICoupleClock clock,
        string? expression,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Absent;
        }

        var now = await clock.NowAsync(cancellationToken);
        var resolution = DateExpressionResolver.Resolve(expression, now.Now, now.Zone);

        if (!resolution.Resolved)
        {
            return new ToolDate(null, Failure: resolution.Failure);
        }

        // UTC, and not the offset-carrying instant the resolver returns.
        // PostgreSQL's timestamptz stores an instant and Npgsql refuses to write a
        // DateTimeOffset whose offset is not zero rather than converting one — so
        // "friday" in Asia/Kolkata reaches this line as +05:30 and would throw at
        // the insert. It threw exactly once, in the first integration run, and the
        // dispatcher's catch-all turned it into a failed tool call with no row and
        // a puzzling message; the same instant, normalised here, is the value every
        // tool writes. The zone is not lost — it is what the note below is built
        // from, and what the column is read back into.
        return new ToolDate(resolution.Instant!.Value.ToUniversalTime(), Describe(expression, resolution, now));
    }

    /// <summary>
    /// The one sentence a person needs in order to correct a wrong reading, and
    /// nothing when there is nothing to correct.
    ///
    /// TOOLS.md asks for the assumed year to be stated "so a wrong guess is
    /// correctable". The resolved instant is spelled out alongside it, in the
    /// couple's own zone, because "assumed 9am" on its own does not say which day
    /// it landed on.
    /// </summary>
    private static string? Describe(string expression, DateResolution resolution, CoupleTime now)
    {
        if (resolution.Assumption is null && now.Recognised)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(resolution.Instant!.Value, now.Zone);

        var note = $"\"{expression.Trim()}\" read as {local:ddd d MMM yyyy, HH:mm}";

        if (resolution.Assumption is { } assumption)
        {
            note += $" — assumed {assumption}";
        }

        if (!now.Recognised)
        {
            // The couple's stored zone is not one this runtime knows, so every date
            // in this run resolved against a fallback. Said out loud rather than
            // swallowed: a reminder an hour out is a bug nobody can explain, and a
            // reminder an hour out with a sentence attached is a setting to fix.
            note += $" (in {now.Zone.Id}, because this couple's stored timezone was not recognised)";
        }

        return note;
    }
}
