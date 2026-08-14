namespace CoupleOS.Domain.Entities;

/// <summary>
/// Which record an attachment belongs to.
///
/// Polymorphic by <c>(entity_type, entity_id)</c> rather than a foreign key per
/// table, which is <c>data/schema.sql</c>'s choice: an attachment can hang off an
/// expense, a task or an event, and six nullable columns would make five of them
/// null on every row and none of them checkable.
///
/// It carries no <c>couple_id</c>, and its row-level security policy is an
/// EXISTS against the attachment it names — so a link is visible exactly when the
/// thing it links is, with nothing to keep in step.
/// </summary>
public sealed class AttachmentLink
{
    public required Guid AttachmentId { get; init; }

    public required string EntityType { get; init; }

    public required Guid EntityId { get; init; }
}
