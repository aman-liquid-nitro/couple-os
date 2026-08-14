using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// A row in <c>events</c>. Named <c>CalendarEvent</c> rather than <c>Event</c>
/// for the reason <see cref="TaskItem"/> is not <c>Task</c>: <c>event</c> is a C#
/// keyword and a type called <c>Event</c> sitting next to real C# events is a
/// sentence nobody reads twice the same way.
///
/// <c>source</c>, <c>created_at</c>, <c>external_id</c> and the rest keep their
/// database defaults.
/// </summary>
public sealed class CalendarEvent
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>Null unless <see cref="Visibility"/> is <c>private_user</c> — <c>events_owner_required_when_private</c>.</summary>
    public Guid? OwnerUserId { get; init; }

    public required Visibility Visibility { get; init; }

    /// <summary>
    /// Where this row came from, from the surface that produced it
    /// (<see cref="CoupleOS.Application.Tools.ToolSource"/>).
    ///
    /// <c>required</c> rather than defaulted, for the reason Memory.Source is:
    /// the CLR default for this enum is <c>user_input</c> where the column's is
    /// <c>chat</c>, so an omission here would not inherit the schema's answer, it
    /// would silently contradict it — which is exactly what happened for three
    /// milestones (STATUS debt 39).
    /// </summary>
    public required DataSource Source { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// <c>NOT NULL</c> in the schema, and the reason
    /// <c>DateExpressionResolver</c> refuses rather than guesses: the tempting way
    /// to satisfy a required timestamp is to invent one, and an invented one is
    /// indistinguishable from a correct one on a calendar.
    /// </summary>
    public required DateTimeOffset StartsAt { get; init; }

    /// <summary>
    /// Optional, and constrained: <c>events_end_after_start</c> refuses an end
    /// before the start. The tool checks it too, so a person gets a sentence
    /// instead of the block failing on a constraint violation.
    /// </summary>
    public DateTimeOffset? EndsAt { get; init; }

    /// <summary>
    /// True for a birthday or an anniversary. The date is what matters and the
    /// hour is noise — so when this is set the resolver's assumed hour is dropped
    /// rather than stored, and the row starts at local midnight.
    /// </summary>
    public required bool AllDay { get; init; }

    public string? Location { get; init; }

    /// <summary>
    /// An RFC 5545 RRULE, or null. The model chooses from a small enum
    /// (<c>yearly</c>, <c>monthly</c>, <c>weekly</c>) and the application writes
    /// the rule: a model asked for "FREQ=YEARLY;INTERVAL=1" will eventually
    /// produce something a calendar library refuses, and nothing here would notice.
    /// </summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>Free text in the schema, one of a closed set in the tool that writes it.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// The database's, never the CLR's — <c>ValueGeneratedOnAdd</c>, so an
    /// unset property does not overwrite <c>now()</c> with year one and sort
    /// first in every list forever.
    ///
    /// Mapped for the read surface, which orders by it. Ordering by the id
    /// instead would work today and stop working quietly: a v7 uuid is
    /// time-ordered to the millisecond and random below it, and the column
    /// default is <c>gen_random_uuid()</c> for anything this application did not
    /// write.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
