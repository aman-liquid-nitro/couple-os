using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Attachments;

/// <summary>
/// The rows, under the caller's couple scope.
///
/// Separate from <see cref="IAttachmentStore"/> because they answer different
/// questions and fail differently: this one is a database under row-level
/// security, that one is a disk. Merging them would produce a single method whose
/// null could mean "you may not see it" or "the file is gone", and those want
/// different answers.
/// </summary>
public interface IAttachments
{
    Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// The row, or null when this caller cannot see it — which includes it not
    /// existing. Deliberately the same answer for both: a partner's private
    /// attachment must be an absence rather than a refusal, for the reason
    /// <c>search_memory</c> renders "nothing recorded … that I can see from here"
    /// instead of naming what it found (ADR 0005).
    /// </summary>
    Task<Attachment?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Links an attachment to the records a block produced.
    ///
    /// Idempotent, because it has to be: <c>attachment_links</c> has a composite
    /// primary key, and a block re-processed after an edit would otherwise fail on
    /// the second insert and take an entire run's transaction with it.
    /// </summary>
    Task LinkAsync(
        Guid attachmentId,
        IReadOnlyList<AttachmentTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>Which attachments a record carries, for the read surface.</summary>
    Task<IReadOnlyList<Attachment>> ForAsync(
        string entityType,
        IReadOnlyList<Guid> entityIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One record an attachment belongs to. <c>attachment_links</c> is polymorphic by
/// <c>(entity_type, entity_id)</c> rather than by a foreign key per table, which
/// is the schema's choice and not this interface's to relitigate.
/// </summary>
public sealed record AttachmentTarget(string EntityType, Guid EntityId);
