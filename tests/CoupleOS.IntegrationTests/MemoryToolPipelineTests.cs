using CoupleOS.Application.AI;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// The two memory tools against a real database, which is where four of their
/// claims can be checked and nowhere else.
///
/// The enum labels round-trip — four new PostgreSQL enums, and Npgsql's snake-case
/// translation of <c>TemporaryContext</c> either matches
/// <c>temporary_context</c> or every insert fails. The supersession is one save, so
/// a correction and the retirement it implies either both land or neither does. The
/// search is raw SQL, so <c>ts_rank</c>, <c>similarity</c> and <c>%</c> are either
/// available on this database or they are not, and <c>MemorySearch.Label</c> either
/// agrees with the mapped enum or the type filter silently matches nothing. And
/// the search runs under row-level security rather than under a predicate, which is
/// the claim that matters most: a hand-written query is exactly where ADR 0005's
/// enforcement boundary would be easiest to step outside of.
/// </summary>
public sealed class MemoryToolPipelineTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));

        return scope;
    }

    private static ToolExecutionContext Context(
        Guid user,
        Visibility visibility = Visibility.SharedCouple) => new()
        {
            CoupleId = RlsFixture.Couple3,
            UserId = user,
            Visibility = visibility,
        };

    private static LlmToolCall Create(string json) =>
        new("create_memory", JsonDocument.Parse(json).RootElement.Clone());

    private static LlmToolCall Search(string json) =>
        new("search_memory", JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>
    /// A word no other test's rows contain, so a search can assert on what it found
    /// rather than on how many rows happened to be in the table.
    /// </summary>
    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task Every_new_enum_label_round_trips_through_the_column()
    {
        // temporary_context is the label that would break: Npgsql translates the CLR
        // name, and TemporaryContext must arrive as temporary_context or the insert
        // fails on a type nobody wrote by hand. The same insert proves assertion,
        // status and source, which are three more enums this milestone added.
        var content = Unique("sofa");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var result = await dispatcher.DispatchAsync(
            Create($$"""
                {"content":"Maybe a new {{content}} at some point","type":"temporary_context",
                 "assertion":"inferred","confidence":0.4}
                """),
            Context(RlsFixture.PartnerD));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors ?? []));

        var stored = await db.Memories.SingleAsync(m => m.Content.Contains(content));

        Assert.Equal(MemoryType.TemporaryContext, stored.Type);
        Assert.Equal(MemoryAssertion.Inferred, stored.Assertion);
        Assert.Equal(MemoryStatus.Active, stored.Status);
        Assert.Equal(DataSource.UserInput, stored.Source);
        Assert.Equal(0.40m, stored.Confidence);

        // memories_temp_context_expires would have refused the row without this, so
        // its presence is the constraint agreeing with the tool's default.
        Assert.NotNull(stored.ExpiresAt);

        // Left to the database rather than mapped, so this is now() and not the
        // CLR's year one — which would sort first in every recency ranking forever.
        Assert.True(stored.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        var audit = await db.AiActions.SingleAsync(a => a.EntityId == stored.Id);
        Assert.Equal("create_memory", audit.ToolName);
        Assert.Equal(ActionOutcome.Success, audit.Outcome);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_correction_retires_the_memory_it_replaces_in_one_save()
    {
        // SPEC.md §45. Both rows must not stay active, and the old one keeps its
        // history — superseded_by_id pointing at what replaced it, never a delete.
        var subject = $"cuisine:{Guid.NewGuid():N}"[..20];
        var first = Unique("italian");
        var second = Unique("thai");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var original = await dispatcher.DispatchAsync(
            Create($$"""{"content":"Likes {{first}}","type":"preference","subject_key":"{{subject}}"}"""),
            Context(RlsFixture.PartnerD));

        Assert.True(original.Succeeded, string.Join("; ", original.Errors ?? []));

        var correction = await dispatcher.DispatchAsync(
            Create($$"""{"content":"Prefers {{second}} now","type":"preference","subject_key":"{{subject}}"}"""),
            Context(RlsFixture.PartnerD));

        Assert.True(correction.Succeeded, string.Join("; ", correction.Errors ?? []));
        Assert.Contains("replaces", correction.Note);

        var old = await db.Memories.AsNoTracking().SingleAsync(m => m.Id == original.EntityId);
        Assert.Equal(MemoryStatus.Superseded, old.Status);
        Assert.Equal(correction.EntityId, old.SupersededById);

        var live = await db.Memories.AsNoTracking()
            .Where(m => m.SubjectKey == subject && m.Status == MemoryStatus.Active)
            .ToListAsync();

        Assert.Equal(correction.EntityId, Assert.Single(live).Id);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task The_same_statement_twice_leaves_one_row_and_no_supersession()
    {
        // SPEC.md §44. A person repeating themselves has contradicted nothing, so
        // there is nothing to retire and nothing to add — and no entity to count.
        var subject = $"coffee:{Guid.NewGuid():N}"[..20];
        var content = Unique("arabica");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var json = $$"""{"content":"Drinks {{content}}","type":"preference","subject_key":"{{subject}}"}""";

        Assert.True((await dispatcher.DispatchAsync(Create(json), Context(RlsFixture.PartnerD))).Succeeded);

        var again = await dispatcher.DispatchAsync(Create(json), Context(RlsFixture.PartnerD));

        Assert.True(again.Succeeded);
        Assert.Null(again.EntityId);
        Assert.Contains("already recorded", again.Note);

        Assert.Single(await db.Memories.AsNoTracking().Where(m => m.SubjectKey == subject).ToListAsync());

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Search_finds_a_memory_by_its_words_and_returns_an_answer_not_a_row()
    {
        var word = Unique("munnar");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();

        await using var transaction = await unitOfWork.BeginAsync();

        Assert.True((await dispatcher.DispatchAsync(
            Create($$"""{"content":"We visited {{word}} in July","type":"episodic"}"""),
            Context(RlsFixture.PartnerD))).Succeeded);

        var found = await dispatcher.DispatchAsync(
            Search($$"""{"query":"{{word}}"}"""),
            Context(RlsFixture.PartnerD));

        Assert.True(found.Succeeded, string.Join("; ", found.Errors ?? []));
        Assert.True(found.Found);
        Assert.Null(found.EntityId);
        Assert.Contains(word, found.Answer);
        Assert.Contains("something that happened", found.Answer);

        // The audit row is a success with no entity, exactly as a clarification is.
        // Both columns have been nullable since the first commit and this is the
        // second thing to exercise it.
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var audit = await db.AiActions
            .Where(a => a.ToolName == "search_memory")
            .OrderByDescending(a => a.Id)
            .FirstAsync();

        Assert.Equal(ActionOutcome.Success, audit.Outcome);
        Assert.Null(audit.EntityId);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task The_type_filter_uses_labels_the_database_agrees_with()
    {
        // MemorySearch.Label derives the snake-case label rather than tabulating it,
        // and a wrong derivation would not error — it would silently match nothing,
        // which reads as "the couple has no such memory". temporary_context is the
        // only multi-word label, so it is the one worth asserting.
        var word = Unique("veranda");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();

        await using var transaction = await unitOfWork.BeginAsync();

        Assert.True((await dispatcher.DispatchAsync(
            Create($$"""
                {"content":"Maybe a {{word}} one day","type":"temporary_context","expires_expression":"for a month"}
                """),
            Context(RlsFixture.PartnerD))).Succeeded);

        var matched = await dispatcher.DispatchAsync(
            Search($$"""{"query":"{{word}}","types":["temporary_context"]}"""),
            Context(RlsFixture.PartnerD));

        Assert.Contains(word, matched.Answer);

        var excluded = await dispatcher.DispatchAsync(
            Search($$"""{"query":"{{word}}","types":["decision"]}"""),
            Context(RlsFixture.PartnerD));

        // Asserted on the memory's own words rather than on the search term. A
        // "nothing recorded" answer names what was looked for, so the query string
        // is in it either way — the thing that must be absent is the content.
        Assert.DoesNotContain("Maybe a", excluded.Answer);
        Assert.Contains("Nothing recorded", excluded.Answer);

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task A_partners_private_memory_is_not_a_candidate_for_the_others_search()
    {
        // Eval case privacy-003, asserted where it is actually enforced. The row is
        // not filtered out of the result — it is invisible to the query, because the
        // policy runs before the ranking does. Nothing in MemorySearch mentions
        // visibility, and this is what says that is sufficient.
        var word = Unique("handbag");

        await using var provider = BuildProvider();

        await using (var mine = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD))
        {
            var unitOfWork = mine.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var dispatcher = mine.ServiceProvider.GetRequiredService<IToolDispatcher>();

            await using var transaction = await unitOfWork.BeginAsync();

            Assert.True((await dispatcher.DispatchAsync(
                Create($$"""{"content":"She really likes that {{word}}","type":"preference"}"""),
                Context(RlsFixture.PartnerD, Visibility.PrivateUser))).Succeeded);

            // Visible to the person who wrote it, from their own thread.
            var own = await dispatcher.DispatchAsync(
                Search($$"""{"query":"{{word}}"}"""),
                Context(RlsFixture.PartnerD, Visibility.PrivateUser));

            Assert.Contains(word, own.Answer);

            await transaction.CommitAsync();
        }

        await using (var theirs = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerE))
        {
            var unitOfWork = theirs.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var dispatcher = theirs.ServiceProvider.GetRequiredService<IToolDispatcher>();

            await using var transaction = await unitOfWork.BeginAsync();

            var searched = await dispatcher.DispatchAsync(
                Search($$"""{"query":"{{word}}"}"""),
                Context(RlsFixture.PartnerE));

            Assert.True(searched.Succeeded);

            // An absence, and not a hint at one. The surprise's own words are not
            // in the answer, it is not counted, and it is not alluded to. Checked on
            // the content rather than on the search term, which a "nothing recorded"
            // answer repeats back by design.
            Assert.DoesNotContain("really likes", searched.Answer);
            Assert.Contains("Nothing recorded", searched.Answer);

            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task An_expired_memory_stops_being_an_answer()
    {
        // Nothing sweeps the table (STATUS debt 29), so expiry has to be enforced on
        // read or ADR 0006's promise that temporary context does not become
        // permanent is only true of the column.
        var word = Unique("hammock");

        await using var provider = BuildProvider();
        await using var request = BeginRequest(provider, RlsFixture.Couple3, RlsFixture.PartnerD);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var dispatcher = request.ServiceProvider.GetRequiredService<IToolDispatcher>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var created = await dispatcher.DispatchAsync(
            Create($$"""{"content":"Thinking about a {{word}}","type":"temporary_context"}"""),
            Context(RlsFixture.PartnerD));

        Assert.True(created.Succeeded);

        // Aged past its expiry through the mapped column, which is the only way to
        // do it without waiting a month.
        await db.Memories
            .Where(m => m.Id == created.EntityId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)));

        var searched = await dispatcher.DispatchAsync(
            Search($$"""{"query":"{{word}}"}"""),
            Context(RlsFixture.PartnerD));

        Assert.DoesNotContain("Thinking about", searched.Answer);
        Assert.Contains("Nothing recorded", searched.Answer);

        await transaction.CommitAsync();
    }
}

