using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

public sealed class ShoppingItem
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public required Visibility Visibility { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// Lower-cased and trimmed. Exists so "Detergent" and "detergent " are one
    /// item for dedup and for the repeat-purchase patterns in SPEC.md 12.
    /// Derived by the application, never by the model.
    /// </summary>
    public required string NormalizedName { get; init; }

    public string? Quantity { get; init; }
    public Guid? AddedBy { get; init; }

    /// <summary>
    /// The database's, never the CLR's — <c>ValueGeneratedOnAdd</c>, so an
    /// unset property does not overwrite <c>now()</c> with year one and sort
    /// first in every list forever.
    ///
    /// Mapped for the read surface, which orders by it. Ordering by the id
    /// instead would work today and stop working quietly: a v7 uuid is
    /// time-ordered to the millisecond and random below it, and the column
    /// default is <c>gen_random_uuid()</c> for anything this application did not
    /// write.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }
}
