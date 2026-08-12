namespace CoupleOS.Domain.Entities;

/// <summary>
/// Joins a user to their couple. Two database constraints make the interesting
/// failures the database's problem rather than the application's:
/// <c>couple_members_one_couple_per_user</c> is unique on <c>user_id</c>, so a
/// person cannot belong to two couples, and a deferred constraint trigger caps
/// membership at two (SPEC.md 6). Both surface as exceptions on insert, which is
/// the right place for them — a check-then-insert in application code races.
/// </summary>
public sealed class CoupleMember
{
    public required Guid CoupleId { get; init; }
    public required Guid UserId { get; init; }
}
