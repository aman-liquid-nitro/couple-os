using System.Globalization;
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
/// <param name="HasExplicitTime">
/// False when the expression carried a date and no time, so the hour in
/// <paramref name="Instant"/> is this layer's assumption rather than the person's.
/// Only a caller that treats the two differently needs it — an all-day event
/// stores the date and ignores the hour, where a reminder at the assumed hour is
/// the whole point.
/// </param>
/// <param name="Local">
/// The same instant in the couple's own zone.
///
/// Carried rather than left to the caller, because converting it back means
/// fetching the zone again and doing that with the *server's* zone is the bug this
/// record exists to prevent. Two callers need it and neither could get it right on
/// its own: <c>expenses.occurred_on</c> is a <c>date</c>, and 11pm in Kolkata is
/// the previous day in UTC; an all-day event stores local midnight, which is not
/// midnight anywhere else.
/// </param>
public sealed record ToolDate(
    DateTimeOffset? Instant,
    string? Note = null,
    string? Failure = null,
    bool HasExplicitTime = false,
    DateTimeOffset? Local = null)
{
    /// <summary>The resolved day where the couple is. What a <c>date</c> column takes.</summary>
    public DateOnly? LocalDate => Local is { } local ? DateOnly.FromDateTime(local.DateTime) : null;

    /// <summary>The hour of day where the couple is, or null when nothing resolved.</summary>
    public int? LocalHour => Local?.Hour;

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
    /// <param name="relativeTo">
    /// An instant this expression continues from, for the end of a range. "until
    /// 11" carries a time and no date, and the date it means is the one the event
    /// starts on — so the caller with both halves passes the first in.
    /// </param>
    public static async Task<ToolDate> ResolveAsync(
        ICoupleClock clock,
        string? expression,
        CancellationToken cancellationToken = default,
        DateTimeOffset? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (string.IsNullOrWhiteSpace(expression))
        {
            return Absent;
        }

        var now = await clock.NowAsync(cancellationToken);
        var resolution = DateExpressionResolver.Resolve(expression, now.Now, now.Zone, relativeTo);

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
        var local = TimeZoneInfo.ConvertTime(resolution.Instant!.Value, now.Zone);

        return new ToolDate(
            resolution.Instant.Value.ToUniversalTime(),
            Describe(expression, resolution, now),
            Failure: null,
            HasExplicitTime: resolution.HasExplicitTime,
            Local: local);
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

        // Invariant, like the resolver's own assumptions. The server's culture
        // decides how a month is abbreviated — en-IN writes "Sept", en-US writes
        // "Sep" — and a sentence that changes with the host's locale is one a test
        // can only pin by accident. Found exactly that way.
        var note = string.Create(
            CultureInfo.InvariantCulture,
            $"\"{expression.Trim()}\" read as {local:ddd d MMM yyyy, HH:mm}");

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
