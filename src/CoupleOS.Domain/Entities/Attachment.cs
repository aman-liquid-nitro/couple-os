using CoupleOS.Domain.Enums;

namespace CoupleOS.Domain.Entities;

/// <summary>
/// A file the couple uploaded, and the row that says whose it is.
///
/// The bytes are not here — <c>storage_key</c> points at them (ADR 0014). What
/// this row carries is everything authorization and the change report need, so a
/// question about who may see an attachment is answered without touching a disk.
/// </summary>
public sealed class Attachment
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required Guid CoupleId { get; init; }

    /// <summary>
    /// Null unless the row is private. <c>attachments_owner_required_when_private</c>
    /// enforces the pair, and the row-level security policy reads this column —
    /// which is why it is never used for anything else (ARCHITECTURE.md §5).
    /// </summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>From the surface that received the upload, never from the file (ADR 0009).</summary>
    public required Visibility Visibility { get; init; }

    /// <summary>
    /// The shared file this was uploaded alongside, when it was. Null from the
    /// private thread, which has no <c>dump_files</c> row.
    /// </summary>
    public Guid? DumpFileId { get; init; }

    /// <summary>
    /// What the browser called it, kept for the person and never used as a path.
    /// The key is a uuid for exactly this reason (ADR 0014).
    /// </summary>
    public required string Filename { get; init; }

    /// <summary>
    /// What the browser claimed it was. Recorded because it is worth knowing and
    /// deliberately not trusted: the download path serves
    /// <c>application/octet-stream</c> whatever this says, because a private-data
    /// application that echoes a user-chosen Content-Type is serving an XSS
    /// vector to the one session that can read everything.
    /// </summary>
    public required string MimeType { get; init; }

    public required long ByteSize { get; init; }

    public required string StorageKey { get; init; }

    /// <summary>
    /// SHA-256, computed while the bytes were streaming past rather than by
    /// reading the file back — which would hash what was written instead of what
    /// arrived, and that difference is what a corrupt write consists of.
    /// </summary>
    public byte[]? Checksum { get; init; }

    public Guid? UploadedBy { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Soft deletion, which nothing writes in V0 and every read filters on
    /// anyway. Mapped rather than ignored so the day a delete path exists it is a
    /// handler rather than a migration plus an audit of every query that forgot.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; init; }

    // ocr_status and ocr_text are deliberately unmapped. V0 does not read images
    // (V0_SCOPE.md), the column exists so the pipeline arrives without a
    // migration, and leaving it unmapped means nothing here can set it by
    // accident — a row claiming OCR was attempted would be the §46 failure with
    // a database column behind it.
}
