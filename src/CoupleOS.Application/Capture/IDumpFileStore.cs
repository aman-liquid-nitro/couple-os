using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

/// <summary>What a save did, and the file as it stands afterwards.</summary>
/// <param name="Accepted">
/// False when the version the editor held was not the version in the database —
/// the other partner saved in between. Nothing was written.
/// </param>
/// <param name="File">
/// Always the file as the database now has it: the caller's own text when the
/// save was accepted, and the other partner's when it was not. A refusal that
/// did not hand back the current content would leave the editor unable to show
/// what it was refused in favour of.
/// </param>
public sealed record DumpFileSave(bool Accepted, DumpFile File);

/// <summary>
/// The couple's capture files. One per couple in V0, and the schema's partial
/// unique index is what makes that true rather than a convention.
/// </summary>
public interface IDumpFileStore
{
    /// <summary>
    /// The couple's shared.md, created on first use.
    ///
    /// Created on demand rather than seeded at couple creation: a file seeded
    /// there would be one more thing the invitation path has to get right, and
    /// M1 deleted the seeding path on purpose. Two partners pressing Process at
    /// the same moment on a couple that has never captured anything both take
    /// this branch, so the create must be safe under a race — it is, and the
    /// integration tests hold it to that.
    /// </summary>
    Task<DumpFile> GetOrCreateSharedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the shared file, but only if <paramref name="expectedVersion"/> is
    /// still the version the database holds.
    ///
    /// The check belongs in the UPDATE's WHERE clause rather than in a read
    /// followed by a write. Both partners looking at version 4 and saving at the
    /// same moment is the case this exists for, and a read-then-compare would
    /// find version 4 twice and let the second write silently erase the first —
    /// the same shape of defect M1 found on magic-link consumption, where the
    /// replay test passed and the concurrency test did not.
    /// </summary>
    Task<DumpFileSave> SaveSharedAsync(
        string content,
        int expectedVersion,
        CancellationToken cancellationToken = default);
}
