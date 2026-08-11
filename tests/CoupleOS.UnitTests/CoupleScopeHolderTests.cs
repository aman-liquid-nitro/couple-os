using CoupleOS.Application.Security;
using CoupleOS.Infrastructure.Security;
using Xunit;

namespace CoupleOS.UnitTests;

public sealed class CoupleScopeHolderTests
{
    private static readonly CoupleScope AnyScope = new(Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void Reading_before_the_scope_is_set_throws_rather_than_returning_null()
    {
        var holder = new CoupleScopeHolder();

        Assert.False(holder.HasScope);
        Assert.Throws<InvalidOperationException>(() => holder.Current);
    }

    [Fact]
    public void The_scope_can_be_read_once_set()
    {
        var holder = new CoupleScopeHolder();
        holder.Set(AnyScope);

        Assert.True(holder.HasScope);
        Assert.Equal(AnyScope.CoupleId, holder.Current.CoupleId);
        Assert.Equal(AnyScope.UserId, holder.Current.UserId);
    }

    [Fact]
    public void Re_scoping_mid_request_is_refused()
    {
        // One unit of work spanning two security contexts has no legitimate
        // use, and a bug causing it would be a cross-couple leak.
        var holder = new CoupleScopeHolder();
        holder.Set(AnyScope);

        Assert.Throws<InvalidOperationException>(
            () => holder.Set(new CoupleScope(Guid.NewGuid(), Guid.NewGuid())));
    }

    [Fact]
    public void A_null_scope_is_refused()
    {
        var holder = new CoupleScopeHolder();

        Assert.Throws<ArgumentNullException>(() => holder.Set(null!));
    }
}
