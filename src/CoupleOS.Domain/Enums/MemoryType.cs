namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>memory_type</c> enum — SPEC.md §8's eight kinds, and ADR 0006's
/// taxonomy.
///
/// Declaration order matches the database's, as <see cref="TaskItemKind"/>
/// explains: the labels come from Npgsql's snake-case translation of these names,
/// so a member inserted in the middle silently re-labels every value after it.
///
/// <see cref="Event"/> is the one that reads oddly beside <c>events</c> the table.
/// They are not the same thing and neither is derived from the other: the table
/// holds a dated appointment, this label holds "our anniversary is what we were
/// celebrating" — an important happening the couple wants remembered, which may
/// have no date at all. SPEC.md §8 calls it "important event"; the enum keeps the
/// database's own word.
///
/// <see cref="TemporaryContext"/> is the only member with a rule attached:
/// <c>memories_temp_context_expires</c> refuses one without an
/// <c>expires_at</c>, because ADR 0006 will not let idle musing become permanent
/// memory.
/// </summary>
public enum MemoryType
{
    Episodic,
    Semantic,
    Preference,
    Decision,
    Commitment,
    Plan,
    Event,
    TemporaryContext,
}
