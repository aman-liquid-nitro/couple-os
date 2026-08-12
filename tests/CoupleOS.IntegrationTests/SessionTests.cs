using System.Net;
using CoupleOS.Application.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

[Collection(IdentityCollection.Name)]
public sealed class SessionTests : IClassFixture<RlsFixture>, IAsyncLifetime
{
    private static readonly IPAddress Caller = IPAddress.Parse("203.0.113.9");

    public Task InitializeAsync() => IdentityHarness.ClearAsync();

    public Task DisposeAsync() => IdentityHarness.ClearAsync();

    private static async Task<(Guid UserId, string Token)> SignInAsync(
        IdentityHarness.Stack stack,
        string localPart)
    {
        await using var scope = stack.Provider.CreateAsyncScope();
        var service = stack.MagicLinks(scope);

        await service.RequestSignInAsync(IdentityHarness.Address(localPart), Caller);
        var token = TestUrlFactory.TokenFrom(stack.Email.Last);

        var signedIn = await service.ConsumeAsync(token, "xunit", Caller);
        Assert.NotNull(signedIn);

        return (signedIn.UserId, signedIn.SessionToken);
    }

    private static async Task<AuthenticatedUser?> ResolveAsync(IdentityHarness.Stack stack, string token)
    {
        await using var scope = stack.Provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ISessionStore>()
            .ResolveAsync(SecretToken.Hash(token));
    }

    [Fact]
    public async Task Consuming_a_link_produces_a_session_that_resolves()
    {
        await using var stack = IdentityHarness.Build();

        var (userId, token) = await SignInAsync(stack, "resolve");
        var resolved = await ResolveAsync(stack, token);

        Assert.NotNull(resolved);
        Assert.Equal(userId, resolved.UserId);
        Assert.Null(resolved.CoupleId);
    }

    [Fact]
    public async Task A_cookie_value_that_was_never_issued_resolves_to_nothing()
    {
        await using var stack = IdentityHarness.Build();
        await SignInAsync(stack, "guessing");

        Assert.Null(await ResolveAsync(stack, SecretToken.Create()));
    }

    [Fact]
    public async Task An_expired_session_resolves_to_nothing()
    {
        await using var stack = IdentityHarness.Build();

        var (_, token) = await SignInAsync(stack, "stale");

        stack.Clock.Advance(stack.Options.SessionLifetime + TimeSpan.FromMinutes(1));

        Assert.Null(await ResolveAsync(stack, token));
    }

    [Fact]
    public async Task Revoking_a_session_ends_it_immediately()
    {
        await using var stack = IdentityHarness.Build();

        var (_, token) = await SignInAsync(stack, "revoked");
        var resolved = await ResolveAsync(stack, token);
        Assert.NotNull(resolved);

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISessionStore>()
                .RevokeAsync(resolved.SessionId);
        }

        Assert.Null(await ResolveAsync(stack, token));
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_every_session_for_that_person()
    {
        // Three browsers, one person. ADR 0007 wants this to be one statement, and
        // the count it returns is what the page reports.
        await using var stack = IdentityHarness.Build();

        var address = IdentityHarness.Address("everywhere");
        var tokens = new List<string>();
        Guid userId = default;

        // One link per browser, and the rate limit allows exactly three.
        for (var i = 0; i < 3; i++)
        {
            await using var scope = stack.Provider.CreateAsyncScope();
            var service = stack.MagicLinks(scope);

            await service.RequestSignInAsync(address, Caller);
            var signedIn = await service.ConsumeAsync(
                TestUrlFactory.TokenFrom(stack.Email.Last), $"browser-{i}", Caller);

            Assert.NotNull(signedIn);
            userId = signedIn.UserId;
            tokens.Add(signedIn.SessionToken);
        }

        foreach (var token in tokens)
        {
            Assert.NotNull(await ResolveAsync(stack, token));
        }

        int revoked;
        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            revoked = await scope.ServiceProvider.GetRequiredService<ISessionStore>()
                .RevokeAllForUserAsync(userId);
        }

        Assert.Equal(3, revoked);

        foreach (var token in tokens)
        {
            Assert.Null(await ResolveAsync(stack, token));
        }
    }

    [Fact]
    public async Task Renewing_pushes_expiry_out_and_keeps_the_session_alive_past_its_original_end()
    {
        // This is what "30-day rolling" means, and the assertion that would have
        // caught the renewal logic reading a value it had just computed: a session
        // used steadily must outlive its original expiry.
        await using var stack = IdentityHarness.Build();

        var (_, token) = await SignInAsync(stack, "rolling");

        // Most of the way through the original window.
        stack.Clock.Advance(stack.Options.SessionLifetime - TimeSpan.FromDays(1));

        var midway = await ResolveAsync(stack, token);
        Assert.NotNull(midway);

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISessionStore>()
                .RenewAsync(midway.SessionId, stack.Clock.GetUtcNow() + stack.Options.SessionLifetime);
        }

        // Past where the session would originally have died.
        stack.Clock.Advance(TimeSpan.FromDays(5));

        Assert.NotNull(await ResolveAsync(stack, token));
    }

    [Fact]
    public async Task Renewal_records_when_the_session_was_last_seen()
    {
        // The middleware decides whether to renew from this value. If it did not
        // move, every request would renew and every page view would be a write.
        await using var stack = IdentityHarness.Build();

        var (_, token) = await SignInAsync(stack, "lastseen");
        var initial = await ResolveAsync(stack, token);
        Assert.NotNull(initial);

        stack.Clock.Advance(TimeSpan.FromHours(3));

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISessionStore>()
                .RenewAsync(initial.SessionId, stack.Clock.GetUtcNow() + stack.Options.SessionLifetime);
        }

        var after = await ResolveAsync(stack, token);

        Assert.NotNull(after);
        Assert.True(
            after.LastSeenAt > initial.LastSeenAt,
            $"last_seen_at did not move: {initial.LastSeenAt:o} then {after.LastSeenAt:o}");
    }

    [Fact]
    public async Task A_revoked_session_cannot_be_renewed_back_to_life()
    {
        // Renewal filters on revoked_at for this reason. A request already in flight
        // when someone signs out everywhere must not extend the session it was told
        // was valid.
        await using var stack = IdentityHarness.Build();

        var (_, token) = await SignInAsync(stack, "zombie");
        var resolved = await ResolveAsync(stack, token);
        Assert.NotNull(resolved);

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionStore>();
            await sessions.RevokeAsync(resolved.SessionId);
            await sessions.RenewAsync(resolved.SessionId, stack.Clock.GetUtcNow() + TimeSpan.FromDays(30));
        }

        Assert.Null(await ResolveAsync(stack, token));
    }
}
