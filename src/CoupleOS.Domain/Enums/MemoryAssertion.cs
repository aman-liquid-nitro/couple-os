namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>memory_assertion</c> enum — where a memory's claim came from, which
/// ADR 0006 makes the load-bearing distinction in this table.
///
/// The rule the enum exists to carry: <b>AI inference is not confirmed fact</b>.
/// <see cref="Inferred"/> is capped at 0.7 confidence by <c>CreateMemoryTool</c>
/// regardless of what the model claims, because a model may not certify its own
/// guess.
///
/// Two members are unwritten in V0 and mapped anyway, because they are read back
/// from a column whose database default is not theirs to change:
/// <see cref="UserConfirmed"/> is what an inferred memory becomes once somebody
/// says yes, and nothing asks yet (STATUS debt 29); <see cref="Imported"/> waits
/// on the document import V0 cuts.
///
/// Declaration order matches the database's, as <see cref="TaskItemKind"/>
/// explains.
/// </summary>
public enum MemoryAssertion
{
    UserStated,
    UserConfirmed,
    Inferred,
    Imported,
}
