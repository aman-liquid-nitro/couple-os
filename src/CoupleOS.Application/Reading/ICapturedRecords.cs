using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Reading;

/// <summary>
/// Everything the couple has captured, grouped the way they think about it.
///
/// <para>V0_SCOPE.md asks for "a flat list of what was captured, grouped by
/// type, so the user can verify the system understood them" — and *verify* is the
/// operative word. This is not a dashboard and it is deliberately not one: no
/// totals, no aggregation, no derived numbers. Finance aggregation is cut from
/// V0 for a stated reason, and a page that quietly sums a column would be
/// answering a question the tool layer refuses to answer (boundary-002).</para>
///
/// <para>One round of queries rather than a query per section, because the point
/// of the page is a person scanning it on a phone and the cost of five sequential
/// round trips is visible there in a way it is not on a laptop.</para>
/// </summary>
public interface ICapturedRecords
{
    Task<CapturedView> ReadAsync(int limitPerKind, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the couple has, under the caller's own scope.
///
/// <para><b>Scope is the database's, not this record's.</b> Nothing here filters
/// on visibility or owner: the queries run inside the caller's transaction, so a
/// private row belonging to the partner contributes nothing — the same property
/// <c>search_memory</c> has, and asserted the same way (ADR 0005).</para>
/// </summary>
/// <param name="Truncated">
/// True when any section hit its limit. Said out loud rather than left as a
/// short list: a page that silently shows the first fifty of two hundred
/// memories is a page that tells the couple they said less than they did, and
/// this whole surface exists so they can check what the system understood.
/// </param>
public sealed record CapturedView(
    IReadOnlyList<ShoppingItem> Shopping,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<CalendarEvent> Events,
    IReadOnlyList<Expense> Expenses,
    IReadOnlyList<Memory> Memories,
    IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> Attachments,
    bool Truncated)
{
    public static CapturedView Empty { get; } = new(
        [], [], [], [], [],
        new Dictionary<Guid, IReadOnlyList<Attachment>>(),
        Truncated: false);

    public int Count => Shopping.Count + Tasks.Count + Events.Count + Expenses.Count + Memories.Count;

    public bool NothingYet => Count == 0;

    /// <summary>
    /// The files hanging off one record, or none. Keyed by entity id alone
    /// because a uuid is unique across the tables that produce it, and a
    /// composite key here would make every call site restate a type name the
    /// caller already knows from which list it is iterating.
    /// </summary>
    public IReadOnlyList<Attachment> AttachmentsFor(Guid entityId) =>
        Attachments.TryGetValue(entityId, out var found) ? found : [];
}
