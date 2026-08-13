using CoupleOS.Application.Tools;
using Microsoft.EntityFrameworkCore;

namespace CoupleOS.Infrastructure.Persistence;

/// <summary>
/// Reads <c>couple_members</c>, on <see cref="IdentityDbContext"/> rather than
/// the scoped context — the same seam <c>CoupleClock</c> uses, for the same
/// reason. <c>couple_members</c> is one of the six documented tables with no
/// row-level security, so the read needs no couple scope and this cannot become
/// the thing that opens a transaction inside a tool call.
///
/// That places the burden on the caller instead: the couple id comes from
/// <c>ToolExecutionContext</c>, which comes from the session, so a tool cannot ask
/// about a couple it was not invoked for. A model has no vocabulary for the
/// argument at all (TOOLS.md rule 5, enforced by the dispatcher).
/// </summary>
public sealed class PartnerLookup(IdentityDbContext dbContext) : IPartnerLookup
{
    private readonly IdentityDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public Task<Guid?> FindPartnerAsync(
        Guid coupleId,
        Guid userId,
        CancellationToken cancellationToken = default) =>
        _dbContext.CoupleMembers
            .AsNoTracking()
            .Where(m => m.CoupleId == coupleId && m.UserId != userId)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
}
