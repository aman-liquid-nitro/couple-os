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
}
