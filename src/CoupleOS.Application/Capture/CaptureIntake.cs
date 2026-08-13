using CoupleOS.Application.Persistence;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

/// <summary>What one intake saw.</summary>
/// <param name="BlocksSeen">
/// Every block in the file, not just the new ones. "Saw 12, recorded 0" is a
/// re-run; "saw 0" is an empty file. One number could not tell them apart, and
/// the change report has to.
/// </param>
/// <param name="Stranded">
/// Blocks still in the file that an earlier run left failed, and that nothing
/// will pick up again: <see cref="IDumpBlockStore.PendingAsync"/> reads only
/// unprocessed rows (STATUS debt 24).
///
/// Counted because the file rewrite made it visible. It leaves settled lines
/// filed and failed ones where they were, so the inbox of a couple who are up to
/// date is often *exactly* the failures — and the report used to greet that with
/// "all N blocks were processed by an earlier run", which is success language
/// over a failed action and the thing SPEC.md 46 forbids.
/// </param>
public sealed record IntakeResult(
    Guid RunId,
    Guid DumpFileId,
    IReadOnlyList<DumpBlock> NewBlocks,
    int BlocksSeen,
    int Stranded)
{
    /// <summary>
    /// Blocks the file already had. Also counts a thought written twice in the
    /// same file, which is the same outcome for the same reason: one hash, one
    /// block, one action.
    /// </summary>
    public int AlreadyRecorded => BlocksSeen - NewBlocks.Count;
}

/// <summary>
/// Reads the shared file and turns it into blocks. The first half of a Process
/// run, and useful on its own: after this, everything the file contains exists
/// as a row with a status, which is what lets the run that follows account for
/// every line rather than only for the ones that produced actions.
/// </summary>
public interface ICaptureIntake
{
    Task<IntakeResult> IngestAsync(CancellationToken cancellationToken = default);
}

public sealed class CaptureIntake(
    IDumpFileStore files,
    IDumpBlockStore blocks,
    IDumpRunStore runs,
    IScopedUnitOfWork unitOfWork) : ICaptureIntake
{
    private readonly IDumpFileStore _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly IDumpBlockStore _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
    private readonly IDumpRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    /// <summary>
    /// One transaction, and this is the transaction boundary decision M2 owed.
    ///
    /// Intake is atomic: the run row and the blocks it saw commit together, or a
    /// run would exist claiming to have seen blocks that are not there. What
    /// follows — classifying and executing each block — does not share this
    /// transaction. ARCHITECTURE.md 6 requires a block that fails to go to
    /// status = 'failed' while the rest of the run continues, and that is
    /// impossible inside one transaction: the failure that sets the status is
    /// the same failure that rolls it back. So processing takes a transaction
    /// per block, and a late failure in a two-hundred-line dump no longer
    /// discards the successes before it.
    ///
    /// This opens its own transaction, so callers must not already hold one.
    /// </summary>
    public async Task<IntakeResult> IngestAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var file = await _files.GetOrCreateSharedAsync(cancellationToken);
        var run = await _runs.StartAsync(file.Id, cancellationToken);

        var segmented = BlockSegmenter.Segment(file.Content);
        var added = await _blocks.AddNewAsync(file, segmented, run.Id, cancellationToken);

        run.BlocksSeen = segmented.Count;
        await _runs.FinishAsync(run, cancellationToken);

        var stranded = await CountStrandedAsync(file.Id, segmented, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new IntakeResult(run.Id, file.Id, added, segmented.Count, stranded);
    }

    /// <summary>
    /// How many of the file's current blocks are failures nothing will retry.
    ///
    /// Intersected against what the file says right now, so a failed line the
    /// user has since deleted does not produce a warning about a line that is not
    /// there. Hex strings because byte[] has reference equality and a HashSet of
    /// them would match nothing.
    /// </summary>
    private async Task<int> CountStrandedAsync(
        Guid fileId,
        IReadOnlyList<SegmentedBlock> segmented,
        CancellationToken cancellationToken)
    {
        if (segmented.Count == 0)
        {
            return 0;
        }

        var failed = await _blocks.FailedHashesAsync(fileId, cancellationToken);

        if (failed.Count == 0)
        {
            return 0;
        }

        var inTheFile = segmented.Select(b => Convert.ToHexString(b.ContentHash)).ToHashSet(StringComparer.Ordinal);

        return failed.Count(hash => inTheFile.Contains(Convert.ToHexString(hash)));
    }
}
