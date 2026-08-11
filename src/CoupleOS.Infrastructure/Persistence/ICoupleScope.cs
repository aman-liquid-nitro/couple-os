namespace CoupleOS.Infrastructure.Persistence;

/// <summary>Who is asking, and on behalf of which couple. Resolved per request.</summary>
public interface ICoupleScope
{
    Guid CoupleId { get; }
    Guid UserId { get; }
}

public sealed class CoupleScope : ICoupleScope
{
    public required Guid CoupleId { get; init; }
    public required Guid UserId { get; init; }
}
