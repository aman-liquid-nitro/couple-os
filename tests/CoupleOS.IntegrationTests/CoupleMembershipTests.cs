using System.Net;
using CoupleOS.Application.Identity;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The other half of M1's exit criterion: "two real people in two browsers,
/// correct isolation". Two people arrive here the way they would in the product —
/// one signs in and creates a couple, the other accepts an invitation — and the
/// isolation is then asserted through the couple scope a real session establishes,
/// not through fixtures inserted for the purpose.
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class CoupleMembershipTests : IClassFixture<RlsFixture>, IAsyncLifetime
{
    private static readonly IPAddress Caller = IPAddress.Parse("203.0.113.11");

    public Task InitializeAsync() => IdentityHarness.ClearAsync();

    public Task DisposeAsync() => IdentityHarness.ClearAsync();

    private static async Task<SignedIn> SignInAsync(IdentityHarness.Stack stack, string localPart)
    {
        await using var scope = stack.Provider.CreateAsyncScope();
        var service = stack.MagicLinks(scope);

        await service.RequestSignInAsync(IdentityHarness.Address(localPart), Caller);
        var signedIn = await service.ConsumeAsync(
            TestUrlFactory.TokenFrom(stack.Email.Last), "xunit", Caller);

        Assert.NotNull(signedIn);
        return signedIn;
    }

    private static async Task<Guid> CreateCoupleAsync(IdentityHarness.Stack stack, Guid userId)
    {
        await using var scope = stack.Provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
            .CreateCoupleAsync(userId, "Test couple");
    }

    /// <summary>Invites an address into a couple and accepts it, returning the invitee's session.</summary>
    private static async Task<SignedIn?> InviteAndAcceptAsync(
        IdentityHarness.Stack stack,
        Guid coupleId,
        string localPart)
    {
        await using var scope = stack.Provider.CreateAsyncScope();
        var service = stack.MagicLinks(scope);

        var outcome = await service.InviteToCoupleAsync(
            IdentityHarness.Address(localPart), coupleId, "Partner A", Caller);

        Assert.Equal(LinkRequestOutcome.LinkSent, outcome);

        return await service.ConsumeAsync(
            TestUrlFactory.TokenFrom(stack.Email.Last), "xunit", Caller);
    }

    [Fact]
    public async Task Creating_a_couple_makes_its_creator_a_member()
    {
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "creator");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);

        await using var scope = stack.Provider.CreateAsyncScope();
        var found = await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
            .FindCoupleIdForUserAsync(a.UserId);

        Assert.Equal(coupleId, found);
    }

    [Fact]
    public async Task An_invitation_puts_the_second_person_in_the_same_couple()
    {
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "partner-a");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);

        var b = await InviteAndAcceptAsync(stack, coupleId, "partner-b");

        Assert.NotNull(b);
        Assert.Equal(coupleId, b.CoupleId);
        Assert.NotEqual(a.UserId, b.UserId);
    }

    [Fact]
    public async Task An_invitation_registers_someone_who_had_no_account()
    {
        // ADR 0007: the invite is a magic link carrying a couple-join claim, so
        // accepting one is also a registration. There is no separate sign-up.
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "inviter");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);

        var address = IdentityHarness.Address("brand-new");

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Null(await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
                .FindUserIdByEmailAsync(address));
        }

        var b = await InviteAndAcceptAsync(stack, coupleId, "brand-new");

        Assert.NotNull(b);
        Assert.Equal(coupleId, b.CoupleId);

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(b.UserId, await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
                .FindUserIdByEmailAsync(address));
        }
    }

    [Fact]
    public async Task A_third_person_cannot_join_a_couple()
    {
        // SPEC.md 6, enforced by the deferred constraint trigger. Asserted through
        // the store so the exception's arrival at COMMIT is covered too.
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "cap-a");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);

        Assert.NotNull(await InviteAndAcceptAsync(stack, coupleId, "cap-b"));

        var c = await SignInAsync(stack, "cap-c");

        await using var scope = stack.Provider.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
            .JoinCoupleAsync(c.UserId, coupleId);

        Assert.Equal(JoinCoupleOutcome.CoupleIsFull, outcome);
    }

    [Fact]
    public async Task Someone_already_in_a_couple_cannot_join_another()
    {
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "taken-a");
        var first = await CreateCoupleAsync(stack, a.UserId);

        var b = await SignInAsync(stack, "taken-b");
        var second = await CreateCoupleAsync(stack, b.UserId);

        Assert.NotEqual(first, second);

        await using var scope = stack.Provider.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IUserDirectory>()
            .JoinCoupleAsync(a.UserId, second);

        Assert.Equal(JoinCoupleOutcome.AlreadyInACouple, outcome);
    }

    [Fact]
    public async Task Accepting_an_invitation_to_a_full_couple_still_signs_the_person_in()
    {
        // The token was valid and is now spent. Refusing the sign-in as well would
        // consume the link and leave the person with nothing to show for it, so they
        // are signed in with no couple — the same state a new registration is in,
        // and one the pages already handle.
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "full-a");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);
        Assert.NotNull(await InviteAndAcceptAsync(stack, coupleId, "full-b"));

        var third = await InviteAndAcceptAsync(stack, coupleId, "full-c");

        Assert.NotNull(third);
        Assert.Null(third.CoupleId);
    }

    [Fact]
    public async Task Creating_a_second_couple_leaves_no_orphan_behind()
    {
        // CreateCoupleAsync inserts the couple and the membership in one
        // SaveChanges, so the unique index rejecting the membership must roll the
        // couple back too. An orphan couple would be invisible to everyone and
        // impossible to clean up.
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "orphan");
        await CreateCoupleAsync(stack, a.UserId);

        var before = await CountCouplesAsync();

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(
                () => scope.ServiceProvider.GetRequiredService<IUserDirectory>()
                    .CreateCoupleAsync(a.UserId, "Second"));
        }

        Assert.Equal(before, await CountCouplesAsync());
    }

    private static async Task<long> CountCouplesAsync()
    {
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM couples";

        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Two_people_in_one_couple_see_shared_rows_and_only_their_own_private_ones()
    {
        // M1's exit criterion. The scope is not handed to the test — it is derived
        // from each person's session exactly as SessionAuthenticationMiddleware
        // derives it, so this fails if sign-in produces the wrong couple as surely
        // as if a policy were wrong.
        await using var stack = IdentityHarness.Build();

        var a = await SignInAsync(stack, "iso-a");
        var coupleId = await CreateCoupleAsync(stack, a.UserId);
        var b = await InviteAndAcceptAsync(stack, coupleId, "iso-b");
        Assert.NotNull(b);

        await SeedMemoriesAsync(coupleId, a.UserId, b.UserId);

        var seenByA = await ReadMemoriesThroughSessionAsync(stack, a.SessionToken);
        var seenByB = await ReadMemoriesThroughSessionAsync(stack, b.SessionToken);

        Assert.Contains("ISO SHARED", seenByA);
        Assert.Contains("ISO SHARED", seenByB);

        Assert.Contains("ISO PRIVATE-A", seenByA);
        Assert.DoesNotContain("ISO PRIVATE-A", seenByB);

        Assert.Contains("ISO PRIVATE-B", seenByB);
        Assert.DoesNotContain("ISO PRIVATE-B", seenByA);
    }

    private static async Task SeedMemoriesAsync(Guid coupleId, Guid userA, Guid userB)
    {
        // Written as admin because memories is policy-protected and this is setup,
        // not the thing under test.
        await using var conn = new NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories (couple_id, owner_user_id, visibility, type, content) VALUES
              (@couple, NULL,  'shared_couple', 'semantic', 'ISO SHARED'),
              (@couple, @userA, 'private_user', 'episodic', 'ISO PRIVATE-A'),
              (@couple, @userB, 'private_user', 'episodic', 'ISO PRIVATE-B')
            """;
        cmd.Parameters.AddWithValue("couple", coupleId);
        cmd.Parameters.AddWithValue("userA", userA);
        cmd.Parameters.AddWithValue("userB", userB);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Resolves a session cookie to a scope and reads under it — the same two steps
    /// the middleware performs, without a browser.
    /// </summary>
    private static async Task<List<string>> ReadMemoriesThroughSessionAsync(
        IdentityHarness.Stack stack,
        string sessionToken)
    {
        await using var scope = stack.Provider.CreateAsyncScope();

        var resolved = await scope.ServiceProvider.GetRequiredService<ISessionStore>()
            .ResolveAsync(SecretToken.Hash(sessionToken));

        Assert.NotNull(resolved);
        Assert.NotNull(resolved.CoupleId);

        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(resolved.CoupleId.Value, resolved.UserId));

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = scope.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        return await db.Memories.Select(m => m.Content).ToListAsync();
    }
}
