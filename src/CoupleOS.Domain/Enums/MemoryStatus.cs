namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>memory_status</c> enum. SPEC.md §45's requirement in one column:
/// a contradiction supersedes rather than accumulating silently.
///
/// <see cref="Superseded"/> is written by <c>IMemoryWriter</c> alongside
/// <c>superseded_by_id</c>, never on its own — a row marked superseded with
/// nothing pointing at what replaced it is a memory that has been deleted with
/// extra steps, and ADR 0006 promises an auditable history instead.
///
/// <see cref="Archived"/> is unwritten in V0. Nothing ages a memory out, which is
/// STATUS debt 29 and wants a corpus before it can be designed against.
///
/// Declaration order matches the database's, as <see cref="TaskItemKind"/>
/// explains.
/// </summary>
public enum MemoryStatus
{
    Active,
    Superseded,
    Archived,
}
