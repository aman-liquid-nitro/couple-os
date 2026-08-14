using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// What <c>search_memory</c> puts <i>first</i>, over a corpus big enough for the
/// question to mean something (<see cref="MemoryCorpusFixture"/>).
///
/// V0_SCOPE's done-checklist asks for relevant results "over a seeded corpus of ≥100
/// memories" and until this file there was no corpus: every other memory test writes
/// one or two rows carrying a token nothing else contains, so what they assert is
/// that the query matches at all. Ranking is what the couple actually experiences —
/// the model is handed the top rows and answers from them, so a wrong first result is
/// a wrong answer, not a scrolling inconvenience.
///
/// <b>Two of these tests assert behaviour that is wrong.</b> They are named for what
/// happens rather than for what should, and they are the reason debts 50 and 51
/// exist. A characterisation test is not an endorsement: it fails when the ranking is
/// fixed, which is the point — the fix should have to come here and say so.
/// </summary>
public sealed class MemorySearchRankingTests(MemoryCorpusFixture corpus)
    : IClassFixture<RlsFixture>, IClassFixture<MemoryCorpusFixture>
{
    // Referenced so the fixture is constructed — and therefore seeded — before any
    // test in the class runs, rather than by whichever test happened to be first.
    private readonly MemoryCorpusFixture _corpus = corpus;

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsTools()
            .BuildServiceProvider();

    private static async Task<IReadOnlyList<Memory>> SearchAsync(
        string text,
        int limit = 10,
        IReadOnlyList<MemoryType>? types = null)
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(MemoryCorpusFixture.Couple4, MemoryCorpusFixture.PartnerF));

        // Inside a transaction, because the scope is applied with set_config(...,
        // true) and is therefore transaction-local. Without one the search runs
        // unscoped and returns nothing at all — which reads as "the couple has no
        // such memory" rather than as a missing transaction, and is the same trap
        // the attachment download page has a comment about.
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        await using var transaction = await unitOfWork.BeginAsync();

        var search = scope.ServiceProvider.GetRequiredService<IMemorySearch>();

        var found = await search.SearchAsync(new MemoryQuery(
            MemoryCorpusFixture.Couple4, text, types ?? [], Since: null, Limit: limit));

        await transaction.CommitAsync();

        return found;
    }

    [Fact]
    public async Task The_corpus_is_at_least_the_hundred_rows_the_checklist_asks_for()
    {
        // The fixture refuses to seed fewer, so this asserts the rows survived to the
        // application role — a corpus the app cannot see is not a corpus. It is also
        // the only test here that would notice the seed silently failing, since every
        // other one asks about order and an empty table has an order.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>()
            .Set(new CoupleScope(MemoryCorpusFixture.Couple4, MemoryCorpusFixture.PartnerF));

        var unitOfWork = scope.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        await using var transaction = await unitOfWork.BeginAsync();

        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.CoupleOsDbContext>();
        var visible = db.Memories.Count();

        await transaction.CommitAsync();

        Assert.True(
            visible >= MemoryCorpusFixture.MinimumCorpusSize,
            $"The application role can see {visible} memories; the checklist asks for at least " +
            $"{MemoryCorpusFixture.MinimumCorpusSize}.");
    }

    [Fact]
    public async Task Anniversary_dinner_picks_the_focused_recent_memory_out_of_three()
    {
        // Three rows contain both words. The other two are a long rambling one from
        // eleven months ago and a cancelled booking from fourteen — so this is the
        // ordinary case, where every term of the blend agrees and the answer is the
        // one a person would give. If this ever fails, the blend has been changed
        // into something that disagrees with itself.
        var results = await SearchAsync("anniversary dinner");

        Assert.True(results.Count >= 3, $"Expected the three anniversary-dinner rows, got {results.Count}.");
        Assert.Equal("Anniversary dinner at the rooftop place was perfect", results[0].Content);
    }

    [Fact]
    public async Task Recency_outweighs_a_doubled_importance_on_two_identical_memories()
    {
        // Two rows, the same sentence, one month old and twenty — and the old one
        // carries 0.60 importance against the new one's 0.30. Text terms are equal by
        // construction, so the newer row can only win if the recency fraction is
        // larger than the 0.30 importance it is giving away. It is: 0.50 against
        // 0.048.
        //
        // The importance gap is what makes this a test. With both rows at the same
        // importance it passed with the recency term deleted from the query, because
        // the ORDER BY ends in created_at DESC — measuring the tiebreak and reporting
        // it as the ranking.
        var results = await SearchAsync("repotting the balcony plants");

        var repotting = results.Where(m => m.Content == "The balcony plants need repotting").ToList();

        Assert.Equal(2, repotting.Count);
        Assert.True(
            repotting[0].CreatedAt > repotting[1].CreatedAt,
            "The older of two identical memories was ranked first, so the recency term is not being applied.");
    }

    [Fact]
    public async Task A_type_filter_promotes_a_lower_ranked_row_rather_than_only_shortening_the_list()
    {
        // Four rows mention the car and a decision tops them. Asking the same question
        // about what happened rather than what was decided has to answer with the
        // third row, not with a shorter list headed by the same one — which is the
        // difference between filtering before the ranking and filtering after it.
        var unfiltered = await SearchAsync("car");
        var episodic = await SearchAsync("car", types: [MemoryType.Episodic]);

        Assert.NotEmpty(unfiltered);
        Assert.NotEmpty(episodic);
        Assert.All(episodic, m => Assert.Equal(MemoryType.Episodic, m.Type));

        Assert.NotEqual(unfiltered[0].Id, episodic[0].Id);

        // And it was promoted from further down the same ranking, rather than being a
        // row the unfiltered query never returned at all.
        var promotedFrom = unfiltered.Select(m => m.Id).ToList().IndexOf(episodic[0].Id);
        Assert.True(promotedFrom > 0, "The filtered answer was not present in the unfiltered ranking.");
    }

    [Fact]
    public async Task The_limit_returns_the_best_rows_rather_than_the_first_ones_found()
    {
        // LIMIT after ORDER BY is the whole claim, and it is invisible on a corpus of
        // two. A query matching many rows, asked twice: the short answer must be the
        // head of the long one. If the limit were applied before the ranking — a plain
        // enough mistake to make in raw SQL — these would diverge.
        var many = await SearchAsync("we decided", limit: 20);
        var few = await SearchAsync("we decided", limit: 3);

        Assert.True(many.Count > few.Count, $"Expected more than {few.Count} matches for the ranking to matter.");
        Assert.Equal(3, few.Count);
        Assert.Equal(many.Take(3).Select(m => m.Id), few.Select(m => m.Id));
    }

    /// <summary>
    /// <b>Characterisation, not endorsement. STATUS debt 50.</b>
    /// </summary>
    [Fact]
    public async Task A_short_empty_memory_outranks_a_long_precise_one_on_the_same_word()
    {
        // Two rows contain "insurance" once each and are the same age and importance.
        // One says everything — when the renewal is due, what was agreed, why — and
        // the other says "Insurance sorted".
        //
        // The short one wins, and not narrowly. similarity() compares the query's
        // trigrams against the whole content, so a longer memory scores lower for
        // being longer, and that term is doubled while ts_rank — the one measuring
        // relevance — is identical for both. The blend is rewarding brevity and
        // calling it relevance.
        var results = await SearchAsync("insurance", limit: 20);

        var ranked = results
            .Where(m => m.Content.Contains("nsurance", StringComparison.Ordinal))
            .Select(m => m.Content)
            .ToList();

        Assert.Contains("Insurance sorted", ranked);
        Assert.Contains(ranked, c => c.StartsWith("The car insurance renewal", StringComparison.Ordinal));

        Assert.True(
            ranked.IndexOf("Insurance sorted") < ranked.FindIndex(c => c.StartsWith("The car insurance renewal", StringComparison.Ordinal)),
            "The detailed memory now outranks the empty one. If that is deliberate, debt 50 is paid and this "
            + "test should be inverted rather than deleted.");
    }

    /// <summary>
    /// <b>Characterisation, not endorsement. STATUS debt 51.</b>
    /// </summary>
    [Fact]
    public async Task A_misspelling_finds_nothing_once_the_memory_is_a_sentence()
    {
        // MemorySearch's own comment says trigram "catches the misspelling and the
        // partial word". It does not, for anything a person would actually write.
        // similarity('We are out of detergent', 'detrgent') is 0.28 against a 0.3
        // threshold, and every longer memory scores lower still — the branch only
        // fires when the content is barely more than the word itself.
        var misspelled = await SearchAsync("detrgent");
        var spelled = await SearchAsync("detergent");

        Assert.Contains(spelled, m => m.Content == "We are out of detergent");

        Assert.DoesNotContain(misspelled, m => m.Content == "We are out of detergent");
        Assert.Empty(misspelled);
    }
}
