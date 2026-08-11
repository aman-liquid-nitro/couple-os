namespace CoupleOS.Domain.Enums;

/// <summary>
/// Mirrors the action_outcome enum in data/schema.sql exactly.
///
/// Kept separate from Application's ToolOutcome on purpose. ToolOutcome also
/// carries ConfirmationRequired, which has no database label; if the two shared
/// a type, the compiler would happily let an unpersistable value reach the audit
/// sink and the failure would appear at runtime as a mapping error, or worse, as
/// a silently substituted label. Two types make the gap explicit.
/// </summary>
public enum ActionOutcome
{
    Success,
    ValidationFailed,
    Unauthorized,
    ExecutionFailed,
    CancelledByUser,
}
