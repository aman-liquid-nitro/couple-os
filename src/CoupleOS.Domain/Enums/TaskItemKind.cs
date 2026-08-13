namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>task_kind</c> enum. One table discriminated by this column rather than
/// three near-identical ones (ARCHITECTURE.md §3).
///
/// Named for <see cref="Entities.TaskItem"/> rather than for the table, for the
/// reason recorded there: <c>Task</c> is taken in every async file in the
/// codebase. <c>Reminder</c> is not a shape of row — it is this label plus a
/// <c>due_at</c>, which <c>tasks_reminder_needs_due</c> requires.
///
/// Member order matches the database's declaration order, and has to: the labels
/// come from Npgsql's snake-case translation of these names, and a member
/// inserted in the middle would silently re-label every value after it.
/// </summary>
public enum TaskItemKind
{
    Task,
    Reminder,
    Commitment,
}
