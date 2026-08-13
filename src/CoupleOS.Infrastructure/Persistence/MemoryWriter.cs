using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Writes a memory, and retires what it replaces, inside the caller's transaction.
/// </summary>
public sealed class MemoryWriter(CoupleOsDbContext dbContext) : IMemoryWriter
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>
    /// Tracked, not <c>AsNoTracking</c>, and that is the point: these rows come back
    /// so that <see cref="AddAsync"/> can mark them superseded. Reading them
    /// untracked and then attaching would be the same query twice.
    /// </summary>
    public async Task<IReadOnlyList<Memory>> FindBySubjectAsync(
        Guid coupleId,
        MemoryType type,
        string subjectKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subjectKey))
        {
            return [];
        }

        return await _dbContext.Memories
            .Where(m => m.CoupleId == coupleId &&
                        m.Type == type &&
                        m.SubjectKey == subjectKey &&
                        m.Status == MemoryStatus.Active)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(
        Memory memory,
        IReadOnlyList<Memory> superseded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(superseded);

        _dbContext.Memories.Add(memory);

        foreach (var old in superseded)
        {
            // Both columns, always together. A row marked superseded with nothing
            // pointing at its replacement is a deletion wearing a status, and ADR
            // 0006 promises an auditable history instead.
            old.Status = MemoryStatus.Superseded;
            old.SupersededById = memory.Id;
        }

        // One SaveChanges, so the correction and the retirement are one fact. EF
        // orders the insert before the updates here because the updates reference
        // the new row's id — which is client-generated, so there is no dependency
        // for EF to get wrong the way it did with couple_members and couples.
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
