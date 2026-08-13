using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Tools;

/// <summary>One row, one caller — narrow for the reason <see cref="IShoppingItemWriter"/> is.</summary>
public interface IEventWriter
{
    Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default);
}
