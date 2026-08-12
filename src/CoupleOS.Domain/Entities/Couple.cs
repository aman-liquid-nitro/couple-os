namespace CoupleOS.Domain.Entities;

/// <summary>
/// The unit every scoped row belongs to. Membership lives in
/// <see cref="CoupleMember"/> rather than in partner_a_id / partner_b_id, so
/// family mode (SPEC.md 51) is a constraint change and not a schema rewrite.
/// </summary>
public sealed class Couple
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Nullable in the database — a couple is usable before it is named.</summary>
    public string? DisplayName { get; init; }
}
