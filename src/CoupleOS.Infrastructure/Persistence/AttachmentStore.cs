using CoupleOS.Application.Attachments;
using CoupleOS.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// The <c>attachments</c> and <c>attachment_links</c> rows, through the scoped
/// context and therefore under row-level security.
///
/// Nothing here filters on <c>couple_id</c>, <c>owner_user_id</c> or
/// <c>visibility</c>, and that absence is the assertion — the same one
/// <c>MemorySearch</c> makes. The day a predicate on one of those columns appears
/// in this file, enforcement has moved out of the database and into a query
/// somebody has to keep correct (ADR 0005).
/// </summary>
public sealed class AttachmentStore(CoupleOsDbContext dbContext) : IAttachments
{
    private readonly CoupleOsDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        _dbContext.Attachments.Add(attachment);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<Attachment?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _dbContext.Attachments
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, cancellationToken);

    /// <summary>
    /// Raw, and <c>ON CONFLICT DO NOTHING</c>, for the reason
    /// <c>DumpBlockStore.AddNewAsync</c> is: the primary key is the dedup, and
    /// letting the database decide is exact under concurrency where "read the
    /// links, then insert the missing ones" is not.
    ///
    /// It matters more here than there. Editing a line to answer a question
    /// re-hashes it into a new block that re-runs every tool the first pass ran
    /// (STATUS debt 31) — so a link written by the first pass would be re-written
    /// by the second, and a duplicate-key error at that point takes the whole
    /// block's transaction with it.
    /// </summary>
    public async Task LinkAsync(
        Guid attachmentId,
        IReadOnlyList<AttachmentTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);

        foreach (var target in targets)
        {
            await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO attachment_links (attachment_id, entity_type, entity_id)
                 VALUES ({attachmentId}, {target.EntityType}, {target.EntityId})
                 ON CONFLICT DO NOTHING
                 """,
                cancellationToken);
        }
    }

    public async Task<IReadOnlyList<Attachment>> ForAsync(
        string entityType,
        IReadOnlyList<Guid> entityIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);

        if (entityIds.Count == 0)
        {
            return [];
        }

        var ids = entityIds.ToArray();

        // The join runs inside the caller's transaction and under its scope, so
        // attachment_links' own policy — an EXISTS against a visible attachment —
        // is what decides. An attachment the caller may not see contributes no
        // rows rather than being filtered out afterwards.
        return await _dbContext.Attachments
            .AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Where(a => _dbContext.AttachmentLinks
                .Any(l => l.AttachmentId == a.Id && l.EntityType == entityType && ids.Contains(l.EntityId)))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }
}
