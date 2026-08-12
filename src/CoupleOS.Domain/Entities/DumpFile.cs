using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// A capture surface, stored as text. For V0 there is exactly one per couple —
/// shared.md — and the schema's partial unique index enforces that.
///
/// This is an event log, not a document (ADR 0009). Lines are appended,
/// processed, and frozen into an archive section; a correction is a new line
/// rather than an edit. That is why deleting a line here does not delete the
/// expense it produced: the file is the input, the rows are the state.
/// </summary>
public sealed class DumpFile
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>Null for the shared file. The schema's CHECK constraint requires it.</summary>
    public Guid? OwnerUserId { get; init; }

    public required Visibility Visibility { get; init; }

    public required DumpFileKind Kind { get; init; }

    public required string Title { get; init; }

    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optimistic concurrency for two partners editing at once. Last write wins
    /// with a stale-version warning, never a silent overwrite (ADR 0009).
    /// Read here; the editor that increments it arrives with the save path.
    /// </summary>
    public int ContentVersion { get; set; }

    public DateTimeOffset? LastProcessedAt { get; set; }
}
