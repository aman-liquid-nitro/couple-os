namespace CoupleOS.Application.Tools;

/// <summary>
/// Who the other person is.
///
/// One question, not a membership repository, and separate from
/// <c>IUserDirectory</c> on purpose: that interface belongs to sign-in and
/// invitation, which run before a couple scope exists. This one is asked from
/// inside a tool call, where the couple is already established and the only thing
/// missing is which of its two members is not the caller.
/// </summary>
public interface IPartnerLookup
{
    /// <summary>
    /// The other member of <paramref name="coupleId"/>, or null when there is
    /// only one.
    ///
    /// Null is a real answer and not an error: a couple has one member between
    /// creating it and the invitation being accepted, which is a normal state that
    /// lasts as long as it takes somebody to read their mail. What a caller must
    /// not do with null is guess — TOOLS.md requires a commitment with no
    /// resolvable partner to fail rather than quietly become a task.
    /// </summary>
    Task<Guid?> FindPartnerAsync(Guid coupleId, Guid userId, CancellationToken cancellationToken = default);
}
