using CoupleOS.Application.Persistence;

namespace CoupleOS.Application.Capture;

/// <summary>The shared file as an editor needs it: text, and the version that text is.</summary>
public sealed record SharedFileView(string Content, int Version, DateTimeOffset? LastProcessedAt);

/// <summary>
/// The result of a save, phrased for the screen that has to explain it.
/// </summary>
/// <param name="Accepted">
/// False means the other partner saved first and this text was not written.
/// </param>
/// <param name="File">
/// The file as it now stands. On a refusal this is the partner's text, which the
/// editor shows rather than discards: the person is about to decide whether to
/// overwrite it, and cannot decide that without seeing it.
/// </param>
public sealed record SharedFileSave(bool Accepted, SharedFileView File);

/// <summary>
/// Reading and writing <c>shared.md</c>.
///
/// This exists so a page never opens a transaction. Every read and write of
/// couple data has to happen inside one for row-level security to see a scope
/// (ADR 0005), and putting that in a PageModel would spread the requirement
/// across the UI, where a single forgotten <c>BeginAsync</c> reads as an empty
/// file rather than as an error.
/// </summary>
public interface ISharedFileEditor
{
    Task<SharedFileView> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the file if <paramref name="expectedVersion"/> still matches.
    ///
    /// A refusal is not an error and does not throw. Two partners editing the
    /// same file is the product working, not a fault, and ADR 0009 settles it as
    /// last-write-wins with a warning rather than a lock: the loser is told, is
    /// shown what they would have overwritten, and may then save again on top of
    /// it deliberately.
    /// </summary>
    Task<SharedFileSave> SaveAsync(
        string content,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds one line to the inbox, taking no version from the caller.
    ///
    /// Deliberately a different operation from <see cref="SaveAsync"/> rather than
    /// a convenience over it. A save replaces the file, so two of them conflict and
    /// somebody has to be told. An append does not: both lines belong in the file,
    /// so last-write-wins is not a policy here, it is data loss. ADR 0009 asks for
    /// no state and no response, and the reason it can have neither is that there
    /// is no conflict to report.
    ///
    /// So a refused version is retried rather than surfaced — re-read, re-splice,
    /// write again — and the retry is safe precisely because the operation adds a
    /// line rather than asserting the whole file.
    /// </summary>
    /// <returns>
    /// The file after the line landed, so the editor open on the same screen can
    /// be brought up to date. Null when the line was blank and nothing was written.
    /// </returns>
    Task<SharedFileView?> QuickAddAsync(
        string? line,
        CancellationToken cancellationToken = default);
}

public sealed class SharedFileEditor(
    IDumpFileStore files,
    IScopedUnitOfWork unitOfWork) : ISharedFileEditor
{
    private readonly IDumpFileStore _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly IScopedUnitOfWork _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));

    public async Task<SharedFileView> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        // A read that creates the row looks odd and is right: opening the editor
        // is the first thing a new couple does, and the alternative is a null
        // file every caller has to handle for the ten seconds before the first
        // save. GetOrCreateShared is safe under a race, so two partners opening
        // the page together is not a special case.
        var file = await _files.GetOrCreateSharedAsync(cancellationToken);

        var view = new SharedFileView(file.Content, file.ContentVersion, file.LastProcessedAt);

        await transaction.CommitAsync(cancellationToken);

        return view;
    }

    public async Task<SharedFileSave> SaveAsync(
        string content,
        int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var save = await _files.SaveSharedAsync(content, expectedVersion, cancellationToken);

        // Committed on a refusal too. Nothing was written, and the transaction
        // still read rows — rolling back here would say "the save failed" about a
        // read that succeeded, and the file it returns has to be the real one.
        await transaction.CommitAsync(cancellationToken);

        return new SharedFileSave(
            save.Accepted,
            new SharedFileView(save.File.Content, save.File.ContentVersion, save.File.LastProcessedAt));
    }

    /// <summary>
    /// Read under a row lock, splice, write. One attempt, no retry, no branch for
    /// losing — because holding the lock means there is nobody to lose to.
    ///
    /// The first version of this was optimistic with a bounded retry, and it
    /// starved: twelve concurrent appends leave the unlucky writers exhausting
    /// their attempts while the lucky ones keep winning. The integration test that
    /// found it is the same shape as M1's magic-link one, and taught the same
    /// lesson — a sequential test passes either way, and only contention tells you
    /// which model you actually chose.
    /// </summary>
    public async Task<SharedFileView?> QuickAddAsync(
        string? line,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _unitOfWork.BeginAsync(cancellationToken);

        var file = await _files.GetSharedForUpdateAsync(cancellationToken);
        var appended = QuickAdd.Append(file.Content, line);

        if (appended is null)
        {
            // An empty box. Committed rather than rolled back for the same reason a
            // refused save is: rows were read, nothing was written, and there is no
            // failure to report. The lock is released by the commit.
            await transaction.CommitAsync(cancellationToken);

            return null;
        }

        // The version check in here cannot fail while the lock is held, which is
        // the point. It stays because the day somebody calls this without the lock,
        // a refusal is a far better outcome than a silent overwrite.
        var save = await _files.SaveSharedAsync(appended, file.ContentVersion, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        if (!save.Accepted)
        {
            throw new InvalidOperationException(
                "shared.md changed while this append held a lock on it, which should not be possible. " +
                "Either the lock was not taken or the append ran outside a transaction.");
        }

        return new SharedFileView(save.File.Content, save.File.ContentVersion, save.File.LastProcessedAt);
    }
}
