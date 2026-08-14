using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// A row in <c>tasks</c>: a task, a reminder, or a commitment. One table
/// discriminated by <see cref="Kind"/> (ARCHITECTURE.md §3), so this is the
/// entity behind two of M3's tools and not one each.
///
/// **Why it is not called <c>Task</c>.** It would shadow
/// <c>System.Threading.Tasks.Task</c> in every async file that touched it, so
/// every writer and every tool would need an alias or a fully qualified return
/// type. The survey that opened M3 called this out before a line of it existed;
/// this is that decision, taken.
///
/// <c>status</c>, <c>source</c>, <c>created_at</c> and the rest are deliberately
/// unmapped and keep their database defaults, as <c>ShoppingItem</c> does.
/// Mapping a column to restate its default invites the two definitions to drift,
/// and no V0 tool sets any of them: a task arrives <c>todo</c>, and the read
/// surface that will want to filter on status is M5's.
/// </summary>
public sealed class TaskItem
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>
    /// Null unless <see cref="Visibility"/> is <c>private_user</c>
    /// (ARCHITECTURE.md §5). This is a privacy column and not an assignment one —
    /// see <c>CreateTaskTool</c> for what that costs, and STATUS debt 35 for what
    /// is owed because of it.
    /// </summary>
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

    public required TaskItemKind Kind { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public required PriorityLevel Priority { get; init; }

    /// <summary>
    /// Resolved by the application from a verbatim expression, never by the model
    /// (TOOLS.md's date contract, and <c>DateExpressionResolver</c> for why).
    /// Required when <see cref="Kind"/> is <see cref="TaskItemKind.Reminder"/>,
    /// and the database says so: <c>tasks_reminder_needs_due</c>.
    /// </summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>
    /// The partner a commitment was made to. Required when <see cref="Kind"/> is
    /// <see cref="TaskItemKind.Commitment"/> —
    /// <c>tasks_commitment_needs_target</c> — which is why a commitment in a
    /// couple with one member is refused rather than downgraded to a task.
    /// </summary>
    public Guid? CommittedToUserId { get; init; }

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
