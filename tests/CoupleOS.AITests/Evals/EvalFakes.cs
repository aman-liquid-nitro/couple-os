using CoupleOS.Application.Time;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// The eval harness judges extraction, not persistence, so tools are registered
/// with writers that do nothing. If an eval ever needed a real database to pass,
/// it would be measuring the wrong thing.
/// </summary>
public sealed class NullShoppingItemWriter : IShoppingItemWriter
{
    public Task AddAsync(ShoppingItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NullTaskWriter : ITaskWriter
{
    public Task AddAsync(TaskItem task, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NullEventWriter : IEventWriter
{
    public Task AddAsync(CalendarEvent calendarEvent, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class NullExpenseWriter : IExpenseWriter
{
    public Task AddAsync(Expense expense, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Every category name resolves, because what the eval set measures is whether the
/// model picked a sensible one — not whether this database was seeded.
/// </summary>
public sealed class StubCategoryLookup : IExpenseCategoryLookup
{
    public Task<Guid?> FindAsync(Guid coupleId, string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(Guid.CreateVersion7());
}

/// <summary>
/// A couple of two, so a commitment resolves. The identity of the partner is not
/// what any eval case measures — that it exists is, because a one-member couple
/// turns a correct extraction into a refused call.
/// </summary>
public sealed class StubPartnerLookup : IPartnerLookup
{
    private static readonly Guid Partner = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public Task<Guid?> FindPartnerAsync(
        Guid coupleId,
        Guid userId,
        CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(Partner);
}

/// <summary>
/// A clock fixed to the day the eval expectations were written against.
///
/// Fixed rather than real, because a case asserting "friday" resolves to a
/// particular instant would otherwise pass or fail depending on the day the suite
/// runs. STATUS debt 21 is the other end of this: the cases currently carry
/// hard-coded absolute dates, which rot as today moves — whichever way that is
/// settled, the harness has to hold "now" still for it to be settled against.
/// </summary>
public sealed class FixedCoupleClock : ICoupleClock
{
    /// <summary>Thursday, 13 August 2026, 10:00 in Asia/Kolkata — the schema's default zone.</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.FromHours(5.5));

    public Task<CoupleTime> NowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CoupleTime(Now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")));
}

public sealed class NullAuditSink : IToolAuditSink
{
    public Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
