namespace CoupleOS.Application.Security;

/// <summary>
/// Reads the scope for the current request. Split from <see cref="ICoupleScopeSetter"/>
/// deliberately: everything that queries needs to read the scope, but only
/// authentication may establish it. Handing every consumer a setter would make
/// "who can change the security context" unanswerable by grep.
/// </summary>
public interface ICoupleScopeAccessor
{
    bool HasScope { get; }

    /// <summary>The current scope.</summary>
    /// <exception cref="InvalidOperationException">No scope has been established.</exception>
    ICoupleScope Current { get; }
}

/// <summary>Establishes the scope. Authentication depends on this; nothing else should.</summary>
public interface ICoupleScopeSetter
{
    void Set(ICoupleScope scope);
}
