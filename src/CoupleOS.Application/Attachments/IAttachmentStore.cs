namespace CoupleOS.Application.Attachments;

/// <summary>What the store recorded about bytes it has taken responsibility for.</summary>
/// <param name="Checksum">
/// SHA-256 of what actually arrived, computed as it streamed past. Hashing the
/// file afterwards would hash what was written rather than what was sent, and
/// the gap between those two is the entire content of a corrupt upload.
/// </param>
public sealed record StoredBytes(string Key, long ByteSize, byte[] Checksum);

/// <summary>
/// Where attachment bytes live, with no opinion about whose they are.
///
/// Two methods and neither mentions a path, which is ADR 0014's point: the
/// filesystem is right for a couple uploading a few receipts a week and wrong at
/// any size beyond that, so moving to object storage should be a second
/// implementation rather than a change to anything that calls this.
///
/// <b>Authorization is not here.</b> The store takes a key and returns bytes. What
/// stops a partner reading a private attachment is the same thing that stops them
/// reading a private memory — the row is invisible under row-level security, so
/// the key is never resolved at all, and the answer is an absence rather than a
/// refusal (ADR 0005).
/// </summary>
public interface IAttachmentStore
{
    /// <summary>
    /// Takes the bytes and returns the key that finds them again.
    ///
    /// <paramref name="coupleId"/> namespaces the key rather than authorizing
    /// anything: a mis-scoped read is then at least a mis-scoped path, and
    /// deleting one couple's files is a directory rather than a query.
    /// </summary>
    Task<StoredBytes> SaveAsync(
        Guid coupleId,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the bytes for reading, or null when the key resolves to nothing.
    ///
    /// Null rather than an exception, because a row pointing at a missing file is
    /// a state this system can reach — the bytes are written before the row, so a
    /// crash between them leaves an orphaned file, and the reverse would leave a
    /// broken link on a page (ADR 0014). The caller decides what to say about it;
    /// throwing here would make a missing file indistinguishable from a broken
    /// disk.
    /// </summary>
    Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken = default);
}
