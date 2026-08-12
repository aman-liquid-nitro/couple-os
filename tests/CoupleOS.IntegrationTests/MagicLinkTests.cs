using System.Net;
using CoupleOS.Application.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// M1's exit criterion, in the part that can be asserted: "replay of a consumed
/// token fails identically to an expired one".
/// </summary>
[Collection(IdentityCollection.Name)]
public sealed class MagicLinkTests : IClassFixture<RlsFixture>, IAsyncLifetime
{
    private static readonly IPAddress Caller = IPAddress.Parse("203.0.113.7");

    public Task InitializeAsync() => IdentityHarness.ClearAsync();

    public Task DisposeAsync() => IdentityHarness.ClearAsync();

    private static async Task<string> RequestLinkAsync(IdentityHarness.Stack stack, string address)
    {
        await using var scope = stack.Provider.CreateAsyncScope();
        var outcome = await stack.MagicLinks(scope).RequestSignInAsync(address, Caller);

        Assert.Equal(LinkRequestOutcome.LinkSent, outcome);

        return TestUrlFactory.TokenFrom(stack.Email.Last);
    }

    private static async Task<SignedIn?> ConsumeAsync(IdentityHarness.Stack stack, string token)
    {
        await using var scope = stack.Provider.CreateAsyncScope();

        return await stack.MagicLinks(scope).ConsumeAsync(token, "xunit", Caller);
    }

    [Fact]
    public async Task A_link_signs_in_once()
    {
        await using var stack = IdentityHarness.Build();

        var token = await RequestLinkAsync(stack, IdentityHarness.Address("once"));

        var first = await ConsumeAsync(stack, token);

        Assert.NotNull(first);
        Assert.NotEqual(Guid.Empty, first.UserId);

        // Registration happens here, not at request time — the address was unknown
        // until the link was clicked.
        Assert.Null(first.CoupleId);
    }

    [Fact]
    public async Task The_plaintext_token_is_never_stored()
    {
        // The whole point of hashing at rest: holding the database must not be
        // enough to sign in as anyone.
        await using var stack = IdentityHarness.Build();

        var address = IdentityHarness.Address("nostore");
        var token = await RequestLinkAsync(stack, address);

        await using var scope = stack.Provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAuthTokenStore>();

        // The hash matches, so the row is the right row...
        Assert.NotNull(await store.ConsumeAsync(SecretToken.Hash(token)));

        // ...and the plaintext appears nowhere in the table.
        await using var conn = new Npgsql.NpgsqlConnection(RlsFixture.AdminConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM auth_tokens WHERE token_hash::text LIKE @needle";
        cmd.Parameters.AddWithValue("needle", "%" + token + "%");

        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Replaying_a_consumed_link_is_indistinguishable_from_an_expired_one()
    {
        await using var stack = IdentityHarness.Build();

        // Case one: used, then used again.
        var usedToken = await RequestLinkAsync(stack, IdentityHarness.Address("replay"));
        Assert.NotNull(await ConsumeAsync(stack, usedToken));
        var replay = await ConsumeAsync(stack, usedToken);

        // Case two: never used, but out of time.
        var expiredToken = await RequestLinkAsync(stack, IdentityHarness.Address("expired"));
        stack.Clock.Advance(stack.Options.MagicLinkLifetime + TimeSpan.FromSeconds(1));
        var expired = await ConsumeAsync(stack, expiredToken);

        // Case three: a token that was never issued at all.
        var unknown = await ConsumeAsync(stack, SecretToken.Create());

        // All three are the same value, and there is no field to compare beyond
        // that, which is the property being asserted — see ConsumedToken's remarks.
        Assert.Null(replay);
        Assert.Null(expired);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task An_expired_link_is_not_consumed_by_the_attempt()
    {
        // Distinct from the assertion above: the caller sees null either way, but
        // the row must record that an expired link was never used. Marking it
        // consumed would make expiry and use genuinely the same thing, and a
        // successful consume after a clock correction impossible to distinguish
        // from a replay in the audit trail.
        await using var stack = IdentityHarness.Build();

        var address = IdentityHarness.Address("untouched");
        var token = await RequestLinkAsync(stack, address);

        stack.Clock.Advance(stack.Options.MagicLinkLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(await ConsumeAsync(stack, token));

        var (consumedAt, count) = await IdentityHarness.TokenStateAsync(address);

        Assert.Equal(1, count);
        Assert.Null(consumedAt);
    }

    [Fact]
    public async Task Only_one_of_many_simultaneous_uses_of_the_same_link_succeeds()
    {
        // The reason ConsumeAsync is one statement. A read-then-write passes the
        // test above and fails this one, and the case it protects is a link that
        // leaked being raced against its owner.
        await using var stack = IdentityHarness.Build();

        var token = await RequestLinkAsync(stack, IdentityHarness.Address("race"));

        const int Racers = 12;

        var attempts = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => ConsumeAsync(stack, token)));

        var winners = attempts.Count(a => a is not null);

        Assert.Equal(1, winners);

        // And every winner is the same person, since they all name one address.
        var distinctUsers = attempts.Where(a => a is not null).Select(a => a!.UserId).Distinct().Count();
        Assert.Equal(1, distinctUsers);
    }

    [Fact]
    public async Task A_known_and_an_unknown_address_are_answered_identically()
    {
        // ADR 0007's enumeration requirement. Both return LinkSent, both send one
        // message, and the bodies differ only in the token — so there is nothing in
        // the response to tell them apart.
        await using var stack = IdentityHarness.Build();

        var known = IdentityHarness.Address("known");
        await ConsumeAsync(stack, await RequestLinkAsync(stack, known));

        stack.Email.Clear();

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(known, Caller));
        }

        var forKnown = stack.Email.Last;
        stack.Email.Clear();

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(
                    IdentityHarness.Address("stranger"), Caller));
        }

        var forUnknown = stack.Email.Last;

        Assert.Equal(forKnown.Subject, forUnknown.Subject);
        Assert.Equal(
            forKnown.Body.Replace(TestUrlFactory.TokenFrom(forKnown), "TOKEN", StringComparison.Ordinal),
            forUnknown.Body.Replace(TestUrlFactory.TokenFrom(forUnknown), "TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fourth_link_for_one_address_inside_the_window_is_refused()
    {
        await using var stack = IdentityHarness.Build();

        var address = IdentityHarness.Address("throttled");

        for (var i = 0; i < stack.Options.MaxLinksPerEmail; i++)
        {
            await using var scope = stack.Provider.CreateAsyncScope();
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(address, Caller));
        }

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.RateLimited,
                await stack.MagicLinks(scope).RequestSignInAsync(address, Caller));
        }

        // And it recovers once the window has passed rather than latching.
        stack.Clock.Advance(stack.Options.PerEmailWindow + TimeSpan.FromSeconds(1));

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(address, Caller));
        }
    }

    [Fact]
    public async Task A_refused_request_sends_no_mail_and_issues_no_token()
    {
        // Otherwise the limit would throttle the response while still doing the work,
        // which is the part that costs money and annoys the recipient.
        await using var stack = IdentityHarness.Build(
            new IdentityOptions { MaxLinksPerEmail = 1 });

        var address = IdentityHarness.Address("nowork");

        await RequestLinkAsync(stack, address);
        stack.Email.Clear();

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.RateLimited,
                await stack.MagicLinks(scope).RequestSignInAsync(address, Caller));
        }

        Assert.Empty(stack.Email.Sent);

        var (_, count) = await IdentityHarness.TokenStateAsync(address);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task The_per_ip_limit_catches_a_spread_across_many_addresses()
    {
        // The per-email limit does nothing against one caller walking a list of
        // addresses, which is both an enumeration attempt and a way to use this
        // server to send mail to strangers.
        await using var stack = IdentityHarness.Build(
            new IdentityOptions { MaxLinksPerEmail = 3, MaxLinksPerIp = 4 });

        for (var i = 0; i < stack.Options.MaxLinksPerIp; i++)
        {
            await using var scope = stack.Provider.CreateAsyncScope();
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(
                    IdentityHarness.Address($"spread{i}"), Caller));
        }

        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.RateLimited,
                await stack.MagicLinks(scope).RequestSignInAsync(
                    IdentityHarness.Address("spread-last"), Caller));
        }

        // A different caller is unaffected — the limit is per IP, not global.
        await using (var scope = stack.Provider.CreateAsyncScope())
        {
            Assert.Equal(
                LinkRequestOutcome.LinkSent,
                await stack.MagicLinks(scope).RequestSignInAsync(
                    IdentityHarness.Address("elsewhere"),
                    IPAddress.Parse("198.51.100.22")));
        }
    }
}
