using CoupleOS.Application.Attachments;
using CoupleOS.Application.Reading;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Five reads and one join, through the scoped context.
///
/// Nothing here mentions <c>couple_id</c>, <c>owner_user_id</c> or
/// <c>visibility</c>. That absence is the assertion, and it is the same one
/// <c>MemorySearch</c> and <c>AttachmentStore</c> make: this is a read surface
/// over every couple-scoped table at once, which makes it the easiest place in
/// the codebase to accidentally re-implement authorization as a WHERE clause —
/// and a predicate here would move enforcement out of PostgreSQL and into a
/// string somebody has to keep correct (ADR 0005).
/// </summary>
public sealed class CapturedRecords(CoupleOsDbContext dbContext, IAttachments attachments) : ICapturedRecords
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly IAttachments _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));

    public async Task<CapturedView> ReadAsync(int limitPerKind, CancellationToken cancellationToken = default)
    {
        // One more than asked for, so "there are more of these" is a fact rather
        // than a guess. Asking for exactly the limit and finding it full is
        // indistinguishable from a list that happens to be that length.
        var take = limitPerKind + 1;

        var shopping = await _dbContext.ShoppingItems
            .AsNoTracking()
            .OrderByDescending(i => i.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var tasks = await _dbContext.Tasks
            .AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var events = await _dbContext.Events
            .AsNoTracking()

            // By when they happen, not by when they were written. An events list
            // ordered by creation is a log; ordered by start it is a calendar,
            // and the couple is looking for the next thing rather than the last
            // thing typed.
            .OrderBy(e => e.StartsAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var expenses = await _dbContext.Expenses
            .AsNoTracking()
            .OrderByDescending(e => e.OccurredOn)
            .Take(take)
            .ToListAsync(cancellationToken);

        var memories = await _dbContext.Memories
            .AsNoTracking()

            // Superseded rows are history, not knowledge. A correction that
            // retired "likes Italian food" must not leave it on a page headed
            // "what we know" — that is the silent accumulation SPEC.md §45
            // forbids, wearing a stylesheet.
            .Where(m => m.Status == MemoryStatus.Active)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        var truncated =
            shopping.Count > limitPerKind ||
            tasks.Count > limitPerKind ||
            events.Count > limitPerKind ||
            expenses.Count > limitPerKind ||
            memories.Count > limitPerKind;

        var linked = new Dictionary<Guid, IReadOnlyList<Attachment>>();

        await CollectAsync(linked, "expense", [.. expenses.Take(limitPerKind).Select(e => e.Id)], cancellationToken);
        await CollectAsync(linked, "task", [.. tasks.Take(limitPerKind).Select(t => t.Id)], cancellationToken);
        await CollectAsync(linked, "event", [.. events.Take(limitPerKind).Select(e => e.Id)], cancellationToken);

        return new CapturedView(
            [.. shopping.Take(limitPerKind)],
            [.. tasks.Take(limitPerKind)],
            [.. events.Take(limitPerKind)],
            [.. expenses.Take(limitPerKind)],
            [.. memories.Take(limitPerKind)],
            linked,
            truncated);
    }

    /// <summary>
    /// One query per entity type rather than one per row.
    ///
    /// The alternative is the shape that looks fine on a laptop with four rows
    /// and is a hundred round trips on the phone this page is for.
    /// </summary>
    private async Task CollectAsync(
        Dictionary<Guid, IReadOnlyList<Attachment>> into,
        string entityType,
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var found = await _attachments.ForAsync(entityType, ids, cancellationToken);

        if (found.Count == 0)
        {
            return;
        }

        // The links themselves say which attachment belongs to which row, and
        // ForAsync returns the attachments rather than the links — so the mapping
        // is rebuilt here from a second, cheap read rather than by widening that
        // interface for one caller.
        var links = await _dbContext.AttachmentLinks
            .AsNoTracking()
            .Where(l => l.EntityType == entityType && ids.Contains(l.EntityId))
            .ToListAsync(cancellationToken);

        var byId = found.ToDictionary(a => a.Id);

        foreach (var group in links.GroupBy(l => l.EntityId))
        {
            var attachments = group
                .Where(l => byId.ContainsKey(l.AttachmentId))
                .Select(l => byId[l.AttachmentId])
                .ToList();

            if (attachments.Count > 0)
            {
                into[group.Key] = attachments;
            }
        }
    }
}
