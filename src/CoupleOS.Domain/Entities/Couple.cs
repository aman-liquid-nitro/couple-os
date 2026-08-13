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

    /// <summary>
    /// An IANA zone id, `NOT NULL DEFAULT 'Asia/Kolkata'` in the schema.
    ///
    /// Mapped as of M3 because the date resolver needs it, and unread by anything
    /// before that: "tomorrow" typed at 11pm means the couple's tomorrow, and a
    /// container running UTC has already started it. Nothing lets a couple change
    /// this yet — there is no settings screen — so in practice it is the default
    /// until one exists.
    /// </summary>
    public string TimeZoneId { get; init; } = "Asia/Kolkata";
}
