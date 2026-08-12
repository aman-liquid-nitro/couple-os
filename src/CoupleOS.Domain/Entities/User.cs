namespace CoupleOS.Domain.Entities;

/// <summary>
/// A person. No credential of any kind lives here — ADR 0007 stores no
/// passwords, so there is nothing to hash and nothing to leak.
/// </summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Unique on <c>lower(email)</c> in the database, not on <c>email</c>.
    /// Comparisons in application code must lower-case to match, or two rows
    /// that the database considers duplicates will look distinct here.
    /// </summary>
    public required string Email { get; init; }

    public required string DisplayName { get; init; }
}
