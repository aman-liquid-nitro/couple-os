using CoupleOS.Application;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Security;
using CoupleOS.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The editor against a real database: text survives, and two partners writing
/// at once do not silently erase each other.
///
/// The second half is the reason this file exists. `shared.md` is one row that
/// both partners write, and the failure worth preventing is not an exception —
/// it is Partner B's evening plans disappearing because Partner A had the page
/// open and pressed Save. Nothing about that failure is visible afterwards: the
/// row is valid, the version is plausible, and the words are gone.
/// </summary>
[Collection(SharedFileCollection.Name)]
public sealed class SharedFileEditorTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsApplication()
            .BuildServiceProvider();

    /// <summary>
    /// One request, scoped as one person. Every call goes through its own scope
    /// because a partner is a different request, not a different argument — and
    /// sharing a scope would share a DbContext, which is exactly the sharing the
    /// concurrency assertions are trying to rule out.
    /// </summary>
    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));
        return scope;
    }

    private static async Task<SharedFileView> ReadAsync(ServiceProvider provider, Guid couple, Guid user)
    {
        await using var request = BeginRequest(provider, couple, user);
        return await request.ServiceProvider.GetRequiredService<ISharedFileEditor>().ReadAsync();
    }

    private static async Task<SharedFileSave> SaveAsync(
        ServiceProvider provider, Guid couple, Guid user, string content, int expectedVersion)
    {
        await using var request = BeginRequest(provider, couple, user);
        return await request.ServiceProvider.GetRequiredService<ISharedFileEditor>()
            .SaveAsync(content, expectedVersion);
    }

    /// <summary>
    /// The shared file is one row per couple and outlives the test run, so no
    /// test may assume a version number — only that it moved by one.
    /// </summary>
    private static string Nonce() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Text_survives_the_round_trip_and_the_version_moves_by_one()
    {
        await using var provider = BuildProvider();

        var before = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var text = $"milk and coffee {Nonce()}";

        var save = await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, text, before.Version);

        Assert.True(save.Accepted);
        Assert.Equal(text, save.File.Content);
        Assert.Equal(before.Version + 1, save.File.Version);

        // Read back in a new request. The save returning the right text only
        // proves the object was built correctly; this proves the database has it.
        var after = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerB);

        Assert.Equal(text, after.Content);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact]
    public async Task A_save_against_a_stale_version_writes_nothing_and_hands_back_what_is_there()
    {
        await using var provider = BuildProvider();

        // Both partners open the page and see the same version.
        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var theirs = $"dentist on Thursday {Nonce()}";
        var mine = $"call the plumber {Nonce()}";

        var first = await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerB, theirs, opened.Version);
        Assert.True(first.Accepted);

        // Partner A now saves holding the version from before B's write.
        var refused = await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, mine, opened.Version);

        Assert.False(refused.Accepted);

        // The refusal carries the partner's text, not the caller's. The screen
        // has to show what it refused in favour of, and cannot ask the database
        // again — by then a third save could have changed it.
        Assert.Equal(theirs, refused.File.Content);
        Assert.Equal(first.File.Version, refused.File.Version);

        // Nothing was written: the row still holds B's text at B's version.
        var current = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        Assert.Equal(theirs, current.Content);
        Assert.Equal(first.File.Version, current.Version);
    }

    [Fact]
    public async Task A_refused_save_succeeds_when_repeated_against_the_version_it_was_given()
    {
        await using var provider = BuildProvider();

        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var theirs = $"bins go out tonight {Nonce()}";
        var mine = $"book the flights {Nonce()}";

        await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerB, theirs, opened.Version);
        var refused = await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, mine, opened.Version);
        Assert.False(refused.Accepted);

        // ADR 0009 is last-write-wins with a warning, not a lock. Pressing Save
        // again after being shown the partner's text is a decision, and it has to
        // be one the product allows — otherwise the warning is a dead end and the
        // only way out is to reload and lose what you typed.
        var second = await SaveAsync(
            provider, RlsFixture.Couple1, RlsFixture.PartnerA, mine, refused.File.Version);

        Assert.True(second.Accepted);
        Assert.Equal(mine, second.File.Content);
        Assert.Equal(refused.File.Version + 1, second.File.Version);
    }

    [Fact]
    public async Task Simultaneous_saves_of_the_same_version_produce_exactly_one_winner()
    {
        await using var provider = BuildProvider();

        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var run = Nonce();

        // Twelve at once, as M1 did to magic links. A read-then-compare version
        // check passes the stale-save test above and fails this one: every
        // request reads the same version, every request finds it current, and
        // eleven writes vanish into the twelfth.
        var attempts = Enumerable.Range(0, 12).Select(i => SaveAsync(
            provider,
            RlsFixture.Couple1,

            // Alternating partners, because either may hold the page open, and
            // the row is shared by both.
            i % 2 == 0 ? RlsFixture.PartnerA : RlsFixture.PartnerB,
            $"{run} attempt {i}",
            opened.Version));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Accepted);

        // The stronger assertion: the version advanced by one, not by twelve. A
        // lost update would leave the counter correct-looking either way, so
        // count the writes rather than trust the successes.
        var current = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.Equal(opened.Version + 1, current.Version);
        Assert.StartsWith(run, current.Content);

        // And the row holds the text of the request that was told it had won.
        var winner = results.Single(r => r.Accepted);
        Assert.Equal(winner.File.Content, current.Content);
    }

    private static Task<SharedFileView?> QuickAddAsync(
        ServiceProvider provider, Guid couple, Guid user, string line) =>
        Task.Run(async () =>
        {
            await using var request = BeginRequest(provider, couple, user);
            return await request.ServiceProvider.GetRequiredService<ISharedFileEditor>().QuickAddAsync(line);
        });

    [Fact]
    public async Task A_quick_added_line_lands_where_a_run_will_read_it()
    {
        await using var provider = BuildProvider();

        var line = $"detergent {Nonce()}";

        // The state that makes placement non-obvious: a file whose last line is
        // inside a section the segmenter refuses to read.
        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        await SaveAsync(
            provider, RlsFixture.Couple1, RlsFixture.PartnerA,
            "## Processed — 12 Aug 2026\n- ~~old~~ → shopping: old",
            opened.Version);

        var after = await QuickAddAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, line);

        Assert.NotNull(after);
        Assert.Equal(opened.Version + 2, after.Version);

        // Readable, which is the only assertion that means anything here. Stored
        // but unreadable is the silent failure, and it looks identical from the row.
        Assert.Contains(
            BlockSegmenter.Segment(after.Content),
            b => b.RawText.Contains(line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_partners_adding_at_the_same_moment_both_keep_their_line()
    {
        await using var provider = BuildProvider();

        var run = Nonce();

        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, "## Inbox", opened.Version);

        // Twelve at once, as the magic-link concurrency test does, because this is
        // the same shape of claim: an append is not a save, so "one winner" is the
        // wrong answer. Both lines belong in the file. A read-modify-write with no
        // retry passes a sequential test and loses a line here.
        var lines = Enumerable.Range(0, 12).Select(i => $"item {i} {run}").ToList();

        await Task.WhenAll(lines.Select(line =>
            QuickAddAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, line)));

        var current = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.All(lines, line => Assert.Contains(line, current.Content, StringComparison.Ordinal));

        // And each is its own block, not twelve lines fused into one entry.
        Assert.Equal(
            lines.Count,
            BlockSegmenter.Segment(current.Content)
                .Count(b => b.RawText.Contains(run, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_empty_quick_add_writes_nothing()
    {
        await using var provider = BuildProvider();

        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.Null(await QuickAddAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, "   "));

        // Not even a version bump. Enter on an empty box is not an event, and a
        // bump would make the other partner's open editor stale for nothing.
        var current = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        Assert.Equal(opened.Version, current.Version);
    }

    [Fact]
    public async Task One_couples_file_is_invisible_to_another()
    {
        await using var provider = BuildProvider();

        var text = $"couple one only {Nonce()}";

        var opened = await ReadAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        await SaveAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, text, opened.Version);

        // The stranger's couple gets its own file rather than a policy error, and
        // that is the failure mode worth asserting: row-level security makes the
        // other couple's row unreadable, so GetOrCreateShared finds nothing and
        // creates one. Reading someone else's shopping list is impossible; being
        // told the file does not exist is what impossible looks like from here.
        var other = await ReadAsync(provider, RlsFixture.Couple2, RlsFixture.StrangerC);

        Assert.DoesNotContain(text, other.Content);
    }
}
