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

    /// <summary>Blocks this run took to a terminal status, including ignored ones.</summary>
    public int BlocksProcessed { get; set; }

    public int BlocksNeedingInput { get; set; }

    public int BlocksFailed { get; set; }

    public int EntitiesCreated { get; set; }

    /// <summary>
    /// Zero until a tool updates something rather than creating it. Written now
    /// rather than left unmapped because it is counted in the same loop as
    /// entities_created, and a column that is only sometimes maintained is worse
    /// than one that is honestly zero.
    /// </summary>
    public int EntitiesUpdated { get; set; }

    /// <summary>
    /// The change report as JSON, exactly as the user was shown it.
    ///
    /// Persisted because ADR 0009 makes the report the primary feedback
    /// mechanism, and a report that lives only in an htmx swap is gone the moment
    /// the tab closes — leaving SPEC.md 32's audit story able to explain single
    /// tool calls but not the run that issued them. Stored as the rendered
    /// account rather than recomputed later: what matters afterwards is what the
    /// person actually read.
    /// </summary>
    public string? Report { get; set; }
}
