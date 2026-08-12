namespace CoupleOS.Application.Identity;

/// <summary>Why a couple could not be joined. Distinguished because these are shown to a signed-in person about their own account, not to an anonymous caller.</summary>
public enum JoinCoupleOutcome
{
    Joined,

    /// <summary>The one-couple-per-user unique index refused it.</summary>
    AlreadyInACouple,

    /// <summary>The two-member constraint trigger refused it (SPEC.md 6).</summary>
    CoupleIsFull,
}

public interface IUserDirectory
{
    /// <summary>Finds a user by address, matched on <c>lower(email)</c> as the unique index is.</summary>
    Task<Guid?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the existing user for this address or creates one.
    ///
    /// Registration and sign-in are the same call deliberately. ADR 0007 requires
    /// identical response and timing whether or not the address exists; if an
    /// unknown address took a different path, the difference would be observable
    /// no matter how carefully the response was matched. Here there is no branch
    /// to observe.
    /// </summary>
    Task<Guid> GetOrCreateUserAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>The couple this user belongs to, or null. At most one, enforced by a unique index on <c>user_id</c>.</summary>
    Task<Guid?> FindCoupleIdForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Creates a couple with this user as its first member, in one transaction.</summary>
    Task<Guid> CreateCoupleAsync(Guid userId, string? displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a user to a couple, letting the database's constraints decide. The
    /// checks are not repeated here first: a check-then-insert races, and both
    /// constraints exist precisely so the answer is decided at write time.
    /// </summary>
    Task<JoinCoupleOutcome> JoinCoupleAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken = default);
}
