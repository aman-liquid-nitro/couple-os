using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// The write half of <c>create_memory</c>, and the only writer in the tool layer
/// with two methods.
///
/// The read is here rather than in a lookup of its own — the shape
/// <see cref="IExpenseCategoryLookup"/> would suggest — because it is not a
/// question anything else would ask. "Which active memories share this subject"
/// exists solely so the write that follows can supersede them, and splitting the
/// pair across two interfaces would invite a caller to do the first without the
/// second: a memory created beside the one it contradicts, both live, which is
/// exactly the silent accumulation SPEC.md §45 forbids.
///
/// The <i>policy</i> is not here. Deciding whether an existing memory on the same
/// subject is a duplicate to leave alone or a contradiction to supersede is
/// <c>CreateMemoryTool</c>'s call, because it is also the sentence the change
/// report has to say out loud.
/// </summary>
public interface IMemoryWriter
{
    /// <summary>
    /// Active, undeleted memories of this couple with the same type and subject
    /// key, oldest first.
    ///
    /// Scoped by row-level security like every other read, so this cannot return a
    /// partner's private memory to a shared-surface caller — which matters here
    /// more than it looks: superseding a row the caller may not read would let a
    /// shared line silently retire a private one.
    /// </summary>
    Task<IReadOnlyList<Memory>> FindBySubjectAsync(
        Guid coupleId,
        MemoryType type,
        string subjectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the new memory and retires what it replaces, in one save.
    ///
    /// One save, because the two halves of a supersession are one fact. A new row
    /// without the retirement leaves two contradictory memories live; a retirement
    /// without the new row leaves the couple's knowledge with a hole where a
    /// correction should be.
    /// </summary>
    /// <param name="superseded">
    /// Rows to mark <c>superseded</c> with <c>superseded_by_id</c> pointing at
    /// <paramref name="memory"/>. Usually empty.
    /// </param>
    Task AddAsync(
        Memory memory,
        IReadOnlyList<Memory> superseded,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What <c>search_memory</c> is asked, with nothing in it the model chose that it
/// was not allowed to choose.
///
/// There is no visibility, no couple filter beyond the caller's own, and no owner:
/// scope comes from row-level security and from the surface (ADR 0005, 0009), so a
/// crafted query has nothing to widen. <see cref="CoupleId"/> is here for the
/// index rather than for security — <c>memories_couple_type</c> is what makes this
/// query fast, and the policy is what makes it correct.
/// </summary>
public sealed record MemoryQuery(
    Guid CoupleId,
    string Text,
    IReadOnlyList<MemoryType> Types,
    DateTimeOffset? Since,
    int Limit);

/// <summary>
/// V0's lexical search over <c>memories</c> — full text plus trigram similarity,
/// ranked by match, recency and importance (TOOLS.md 7, ADR 0002).
///
/// Read-only by construction: there is no method here that writes, so the one
/// tool a prompt injection would most like to reach cannot do anything but look.
/// </summary>
public interface IMemorySearch
{
    /// <summary>Best match first. Empty is a normal answer, not a failure.</summary>
    Task<IReadOnlyList<Memory>> SearchAsync(MemoryQuery query, CancellationToken cancellationToken = default);
}
