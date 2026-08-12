using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Capture;

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
}
