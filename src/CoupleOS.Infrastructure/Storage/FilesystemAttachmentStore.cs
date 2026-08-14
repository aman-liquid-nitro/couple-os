using System.Globalization;
using System.Security.Cryptography;
using CoupleOS.Application.Attachments;

namespace CoupleOS.Infrastructure.Storage;

/// <summary>Where the bytes go. One value, so a deployment can move them without a rebuild.</summary>
public sealed record AttachmentStorageOptions
{
    /// <summary>
    /// Defaulted rather than required, and to a path the container's Dockerfile
    /// creates and chowns. A store that threw on an unset option would make every
    /// `dotnet run` outside Docker fail at startup for a feature the developer may
    /// not be touching.
    /// </summary>
    public string Root { get; init; } = "/var/lib/coupleos/attachments";
}

/// <summary>
/// ADR 0014. The filesystem, on a named volume, and nothing above it knows that.
///
/// Four things it does are the reasons the ADR chose this over a
/// <c>bytea</c> column, and each of them is a line or two here that would be a
/// paragraph of caveats anywhere else.
/// </summary>
public sealed class FilesystemAttachmentStore(AttachmentStorageOptions options) : IAttachmentStore
{
    private readonly AttachmentStorageOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<StoredBytes> SaveAsync(
        Guid coupleId,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Opaque and namespaced by couple. Never the filename: a user-supplied
        // name in a path is a traversal waiting to happen, and the same name
        // uploaded twice would collide (ADR 0014).
        var key = string.Create(CultureInfo.InvariantCulture, $"{coupleId:D}/{Guid.CreateVersion7():N}");

        var destination = Resolve(key);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Written under a temporary name and moved into place. A partial write
        // must never be readable as an attachment, and File.Move within one volume
        // is atomic — so a key resolves to a complete file or to nothing at all.
        var staging = destination + ".part";

        long size;
        byte[] checksum;

        try
        {
            // Hashed on the way past, not by reading the file back afterwards.
            // Reading it back would double the I/O and — the part that matters —
            // would hash what was written rather than what arrived, and the gap
            // between those two is the whole content of a corrupt upload.
            using var sha = SHA256.Create();

            await using (var file = new FileStream(
                staging,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write))
            {
                await content.CopyToAsync(hashing, cancellationToken);
                await hashing.FlushFinalBlockAsync(cancellationToken);
            }

            checksum = sha.Hash!;
            size = new FileInfo(staging).Length;

            File.Move(staging, destination, overwrite: false);
        }
        catch
        {
            // The staging file is the only thing this method has created, and a
            // failure here means nobody will ever be told its name. Left behind it
            // would be unreachable rubbish that grows.
            TryDelete(staging);

            throw;
        }

        return new StoredBytes(key, size, checksum);
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = Resolve(key);

        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null);
    }

    /// <summary>
    /// A key to a path, refusing anything that leaves the root.
    ///
    /// Every key this class issues is two uuids and cannot escape, so this guard
    /// is about keys that arrive from somewhere else — a hand-edited row, a future
    /// import — and it is the kind of check whose absence is only ever noticed
    /// once. Compared on the resolved full path rather than by scanning for
    /// <c>..</c>, because the second is a game of spotting encodings and the first
    /// is an answer.
    /// </summary>
    private string Resolve(string key)
    {
        var root = Path.GetFullPath(_options.Root);
        var full = Path.GetFullPath(Path.Combine(root, key));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That storage key resolves outside the attachment root.");
        }

        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Cleaning up after a failure must not replace the failure with a
            // different one. The original exception is the useful sentence.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
