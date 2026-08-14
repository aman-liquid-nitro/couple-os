using CoupleOS.Application.Persistence;
using CoupleOS.Application.Reading;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CoupleOS.Api.Pages;

/// <summary>
/// What the system understood, so a person can check whether it did.
///
/// <para>V0_SCOPE.md's minimal read surface, and the emphasis is on
/// <i>verify</i>. The change report says what one press of Process did, and it
/// is gone the next time the page loads; the archive in <c>shared.md</c> keeps
/// the lines but not the rows they became. Neither answers "is my anniversary
/// actually in there", which is the question the week of real use turns on.</para>
///
/// <para><b>Not a dashboard, and the difference is a decision.</b> No totals, no
/// aggregation, no derived numbers — finance aggregation is cut from V0 with a
/// stated reason, and `boundary-002` asserts that no path in this system
/// produces a spending figure. A page that quietly summed a column would answer
/// the question the tool layer refuses to.</para>
/// </summary>
public sealed class CapturedModel(ICapturedRecords records, IScopedUnitOfWork unitOfWork) : PageModel
{
    /// <summary>
    /// Per kind, not overall. Fifty expenses and no memories is a different page
    /// from ten of each, and a single overall cap would let whichever table
    /// happens to be busiest push the others off.
    /// </summary>
    private const int PerKind = 50;

    private readonly ICapturedRecords _records = records ?? throw new ArgumentNullException(nameof(records));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public CapturedView View { get; private set; } = CapturedView.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // One transaction for the whole page, so every section is read under the
        // same scope and at the same instant. Five separate ones would also work
        // and would let a Process running in another tab land halfway down the
        // page, which is a screen that contradicts itself.
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        View = await _records.ReadAsync(PerKind, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
