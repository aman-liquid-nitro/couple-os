using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Adds the row and saves inside the caller's transaction, as
/// <see cref="ShoppingItemWriter"/> and <see cref="TaskWriter"/> do.
/// </summary>
public sealed class EventWriter(CoupleOsDbContext dbContext) : IEventWriter
{
    private readonly CoupleOsDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        _dbContext.Events.Add(calendarEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
