namespace CoupleOS.Domain.Entities;

/// <summary>
/// One press of Process, and the row the change report will eventually hang off.
///
/// It exists as a row rather than as a response body because ADR 0009 makes the
/// report the primary feedback mechanism: a report that lives only in an htmx
/// swap is gone the moment the page is closed, and SPEC.md 32's audit story then
/// covers single tool calls but not the batch that issued them.
/// </summary>
public sealed class DumpRun
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    public required Guid DumpFileId { get; init; }

    /// <summary>Whoever pressed the button. Either partner may process the shared file.</summary>
    public Guid? TriggeredBy { get; init; }

    /// <summary>
    /// Set by the application rather than left to the column's DEFAULT now().
    /// The column is mapped, so EF would otherwise send default(DateTimeOffset)
    /// and write year 1 — the same defect AuthToken.CreatedAt records.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Every block the file contained, not just the new ones. The distinction
    /// matters to a reader: "saw 12, processed 0" is a re-run, "saw 0" is an
    /// empty file, and a single number could not tell them apart.
    /// </summary>
    public int BlocksSeen { get; set; }

    // blocks_processed, blocks_needing_input, blocks_failed, entities_created,
    // entities_updated and report all default to 0 / null in the schema and are
    // written by the run that produces them.
}
