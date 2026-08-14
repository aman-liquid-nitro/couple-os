using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Attachments;

/// <summary>What an upload produced, or why it did not.</summary>
/// <param name="Markdown">
/// The link to put in front of the person: <c>[receipt.jpg](/attachments/…)</c>.
/// The surface decides what to do with it — the shared file quick-adds it, the
/// private thread sends it as a message — and neither has to know the format.
/// </param>
public sealed record AttachmentUpload(Attachment? Attachment, string? Markdown, string? Refusal)
{
    public bool Accepted => Attachment is not null;

    public static AttachmentUpload Refused(string reason) => new(null, null, reason);
}

public interface IAttachmentIntake
{
    /// <summary>
    /// Takes an uploaded file and returns the link that refers to it.
    ///
    /// <paramref name="visibility"/> comes from the surface and never from the
    /// file or its name (ADR 0009). A receipt uploaded in the private thread is
    /// private, and a picture of the same receipt uploaded into <c>shared.md</c>
    /// is shared, and nothing about the bytes distinguishes them.
    /// </summary>
    Task<AttachmentUpload> ReceiveAsync(
        string filename,
        string? mimeType,
        Stream content,
        Visibility visibility,
        Guid? dumpFileId,
        CancellationToken cancellationToken = default);
}

public sealed class AttachmentIntake(
    IAttachmentStore store,
    IAttachments attachments,
    IScopedUnitOfWork unitOfWork,
    ICoupleScopeAccessor scopeAccessor) : IAttachmentIntake
{
    /// <summary>
    /// Twenty megabytes, which is a phone photograph with room to spare and not a
    /// video.
    ///
    /// A limit has to exist and it has to be enforced here rather than only in the
    /// web layer: this is the last point that can refuse before the bytes are on
    /// the volume, and a limit configured in one place and forgotten in the other
    /// is how a disk fills up.
    /// </summary>
    public const long MaximumBytes = 20 * 1024 * 1024;

    private readonly IAttachmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAttachments _attachments = attachments ?? throw new ArgumentNullException(nameof(attachments));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    private readonly ICoupleScopeAccessor _scopeAccessor = scopeAccessor
        ?? throw new ArgumentNullException(nameof(scopeAccessor));

    public async Task<AttachmentUpload> ReceiveAsync(
        string filename,
        string? mimeType,
        Stream content,
        Visibility visibility,
        Guid? dumpFileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var name = Clean(filename);

        if (name is null)
        {
            return AttachmentUpload.Refused("That file has no name this can record.");
        }

        var scope = _scopeAccessor.Current;

        // The bytes first, the row second, and the order is deliberate. A crash
        // between them leaves a file nothing points at, which is wasted space; the
        // other order leaves a row pointing at nothing, which is a broken link on
        // a page and an error a person has to interpret. ADR 0014 records that
        // neither is reconciled in V0, and which of the two to prefer.
        var stored = await _store.SaveAsync(scope.CoupleId, content, cancellationToken);

        if (stored.ByteSize == 0)
        {
            return AttachmentUpload.Refused("That file is empty.");
        }

        if (stored.ByteSize > MaximumBytes)
        {
            return AttachmentUpload.Refused(
                $"That file is {stored.ByteSize / (1024 * 1024)} MB, and the limit is {MaximumBytes / (1024 * 1024)} MB.");
        }

        var attachment = new Attachment
        {
            CoupleId = scope.CoupleId,

            // The pair the schema's own CHECK requires, written together so it
            // cannot be half-applied: private means owned, shared means not.
            Visibility = visibility,
            OwnerUserId = visibility == Visibility.PrivateUser ? scope.UserId : null,

            DumpFileId = dumpFileId,
            Filename = name,

            // Recorded, not trusted. The download path ignores it entirely
            // (ADR 0014).
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType.Trim(),

            ByteSize = stored.ByteSize,
            StorageKey = stored.Key,
            Checksum = stored.Checksum,
            UploadedBy = scope.UserId,
        };

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        await _attachments.AddAsync(attachment, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new AttachmentUpload(
            attachment,
            AttachmentReference.Markdown(attachment.Id, attachment.Filename),
            Refusal: null);
    }

    /// <summary>
    /// The name a person will read, and nothing a filesystem will act on.
    ///
    /// The storage key is a uuid, so this is never used as a path — but it is
    /// rendered into the couple's own file and onto a page, and a name arriving
    /// with a directory in it is a name somebody is trying something with. Only
    /// the leaf is kept.
    /// </summary>
    private static string? Clean(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
        {
            return null;
        }

        var leaf = filename.Trim().Split('/', '\\').Last().Trim();

        if (leaf is "" or "." or "..")
        {
            return null;
        }

        return leaf.Length > 200 ? leaf[..200] : leaf;
    }
}
