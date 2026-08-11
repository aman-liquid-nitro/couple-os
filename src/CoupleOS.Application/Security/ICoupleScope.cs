namespace CoupleOS.Application.Security;

/// <summary>Who is asking, and on behalf of which couple.</summary>
public interface ICoupleScope
{
    Guid CoupleId { get; }
    Guid UserId { get; }
}

public sealed record CoupleScope(Guid CoupleId, Guid UserId) : ICoupleScope;
