using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleOS.Application.Persistence;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>
/// One press of Process: read the file, turn it into blocks, take each block
/// through the pipeline, and account for all of them.
///
/// It orchestrates and nothing else. It does not talk to a model, does not know
/// what a tool does, and does not render. Each of those belongs to something it
/// depends on, which is what lets this be unit-tested without a database or a
/// GPU.
/// </summary>
public sealed class CaptureProcessor(
    ICaptureIntake intake,
    IBlockProcessor blockProcessor,
    IDumpBlockStore blocks,
    IDumpFileStore files,
    IDumpRunStore runs,
    IScopedUnitOfWork unitOfWork,
    TimeProvider clock) : ICaptureProcessor
{
    private readonly ICaptureIntake _intake = intake ?? throw new ArgumentNullException(nameof(intake));
    private readonly IBlockProcessor _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
    private readonly IDumpBlockStore _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
    private readonly IDumpFileStore _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly IDumpRunStore _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Enums as names, not as the numbers a future reordering would silently
    /// change the meaning of. This JSON is read by people looking at a row months
    /// later, and "2" is not a status.
    /// </summary>
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<CaptureReport> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var intake = await _intake.IngestAsync(cancellationToken);

        // Everything still unprocessed on this file, not only what intake just
        // recorded. The difference matters after a run that died halfway: its
        // blocks are in the table, they are nobody's "new" blocks any more, and
        // reading only intake's output would strand them as unprocessed forever.
        var pending = await ReadPendingAsync(intake.DumpFileId, cancellationToken);

        var reports = new List<BlockReport>(pending.Count);

        foreach (var block in pending)
        {
            // One transaction per block lives inside this call. A failure in the
            // last block of a two-hundred-line dump no longer discards the
            // hundred and ninety-nine successes before it, which is STATUS debt 7
            // and the reason intake commits separately from processing.
            reports.Add(await _blockProcessor.ProcessAsync(block, cancellationToken));
        }

        var report = new CaptureReport(
            reports,
            intake.BlocksSeen,

            // Intake's own figure: blocks the file already had, which is exactly
            // the dedup hits. Subtracting what this run handled would be wrong
            // and was — a run can handle blocks the file no longer contains,
            // because editing a line out of the file does not delete the block it
            // made (ADR 0009: the file is the input, the rows are the state). On
            // the first run after a crashed one, "seen minus handled" went
            // negative, and a negative count of already-processed blocks was
            // rendered as nothing at all rather than as the nonsense it was.
            intake.AlreadyRecorded,
            Summarise(reports));

        await FinishAsync(intake, report, cancellationToken);

        return report;
    }

    private async Task<IReadOnlyList<DumpBlock>> ReadPendingAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);
        var pending = await _blocks.PendingAsync(fileId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return pending;
    }

    /// <summary>
    /// Closes the run: the counters, the report, and the file's last_processed_at.
    ///
    /// In its own transaction, after every block has finished in its own. A run
    /// row that committed with the blocks would have to be the same transaction
    /// as one of them, and would then roll back with it.
    /// </summary>
    private async Task FinishAsync(
        IntakeResult intake,
        CaptureReport report,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var run = await _runs.GetAsync(intake.RunId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Run {intake.RunId} vanished between intake and its own completion.");

        // blocks_processed counts blocks this run took to a settled state —
        // including the ignored ones, because reading a line and deciding it
        // needs no action is work done, not work skipped. The two columns beside
        // it exist so that "processed" never has to quietly mean "attempted".
        run.BlocksProcessed = report.Blocks.Count(b =>
            b.Status is DumpBlockStatus.Processed or DumpBlockStatus.Ignored);

        run.BlocksNeedingInput = report.Blocks.Count(b => b.Status == DumpBlockStatus.NeedsInput);
        run.BlocksFailed = report.Blocks.Count(b => b.Status == DumpBlockStatus.Failed);
        run.EntitiesCreated = report.Applied.Count();
        run.Report = JsonSerializer.Serialize(report, ReportJson);

        await _runs.FinishAsync(run, cancellationToken);

        // Stamped even when the run changed nothing. "Last processed" answers
        // "has anyone pressed the button since I wrote this", and a run that
        // found nothing to do still answers it.
        await _files.MarkProcessedAsync(intake.DumpFileId, _clock.GetUtcNow(), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Sums what the blocks cost. Provider and model come from the first block
    /// that called a model — within one run they are the same for all of them,
    /// and a run where they were not would be a run whose configuration changed
    /// mid-flight.
    /// </summary>
    private static RunUsage? Summarise(IReadOnlyList<BlockReport> reports)
    {
        var used = reports.Select(r => r.Usage).OfType<AI.LlmUsage>().ToList();

        if (used.Count == 0)
        {
            return null;
        }

        return new RunUsage(
            used[0].Provider,
            used[0].Model,
            used.Count,
            used.Sum(u => u.PromptTokens),
            used.Sum(u => u.CompletionTokens),

            // Wall clock is what the person waited, and these ran one after
            // another, so the sum is the wait. It becomes a lie the day blocks
            // run in parallel, and that day this line has to change with them.
            TimeSpan.FromTicks(used.Sum(u => u.Duration.Ticks)));
    }
}
