namespace CoupleOS.Domain.Enums;

/// <summary>
/// The <c>priority_level</c> enum, shared by tasks and by whatever else acquires
/// a priority later.
///
/// Declaration order matches the database's, as <see cref="TaskItemKind"/>
/// explains — and here it matters twice over, because <c>Low</c> is the CLR
/// default for the type. Nothing may reach the database by omission: every
/// property mapped to this enum is <c>required</c>, so a caller states a
/// priority or does not compile.
/// </summary>
public enum PriorityLevel
{
    Low,
    Normal,
    High,
}
