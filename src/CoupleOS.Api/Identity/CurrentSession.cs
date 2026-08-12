using CoupleOS.Application.Identity;

namespace CoupleOS.Api.Identity;

/// <summary>
/// Who the current request belongs to, for the pages to read. Scoped, set once by
/// <see cref="SessionAuthenticationMiddleware"/>.
///
/// Deliberately not the same thing as <c>ICoupleScopeAccessor</c>. That carries the
/// couple identity row-level security is enforced against and must never hold a
/// value that has not been proven; this carries the answer to "is anyone signed
/// in, and do they have a couple yet" — a question with three answers, one of
/// which the scope type cannot represent.
/// </summary>
public sealed class CurrentSession
{
    public AuthenticatedUser? User { get; set; }

    public bool IsSignedIn => User is not null;

    public bool HasCouple => User?.CoupleId is not null;
}
