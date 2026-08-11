using CoupleOS.Application.Security;

namespace CoupleOS.Infrastructure.Security;

/// <summary>
/// Request-scoped storage for the current scope. Registered once and exposed
/// through two interfaces so that reading and establishing the security context
/// are separately grantable.
/// </summary>
public sealed class CoupleScopeHolder : ICoupleScopeAccessor, ICoupleScopeSetter
{
    private ICoupleScope? _scope;

    public bool HasScope => _scope is not null;

    public ICoupleScope Current =>
        _scope ?? throw new InvalidOperationException(
            "No couple scope has been established for this request. Authentication must call " +
            "ICoupleScopeSetter.Set before any data access. Without it every query returns zero " +
            "rows, because row-level security fails closed (ADR 0005).");

    public void Set(ICoupleScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Re-scoping mid-request would mean one unit of work spanning two
        // security contexts. There is no legitimate reason for it, and a bug
        // that caused it would be a cross-couple leak.
        if (_scope is not null)
        {
            throw new InvalidOperationException("The couple scope has already been set for this request.");
        }

        _scope = scope;
    }
}
