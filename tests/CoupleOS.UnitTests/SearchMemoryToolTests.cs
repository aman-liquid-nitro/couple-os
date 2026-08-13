using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// The read-only tool, where the assertions are about what reaches a person's
/// screen rather than about what reaches a table.
///
/// Two properties matter more than the rest. The query the tool builds contains no
/// scope — no visibility, no owner — because scope is the database's (ADR 0005) and
/// a filter written here would be a second, weaker copy of it. And an inferred
/// memory is hedged by this rendering rather than by a model's good manners, which
/// is what makes ADR 0006's "surface an inference as a hypothesis" unconditional.
/// </summary>
public sealed class SearchMemoryToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class StubSearch(params Memory[] hits) : IMemorySearch
    {
        public MemoryQuery? Asked { get; private set; }

        public Task<IReadOnlyList<Memory>> SearchAsync(
            MemoryQuery query,
            CancellationToken cancellationToken = default)
        {
            Asked = query;

            return Task.FromResult<IReadOnlyList<Memory>>(hits);
        }
    }

    private static Memory Hit(
        string content,
        MemoryType type = MemoryType.Preference,
        MemoryAssertion assertion = MemoryAssertion.UserStated,
        decimal confidence = 1.00m) => new()
        {
            CoupleId = CoupleId,
            Type = type,
            Assertion = assertion,
            Source = DataSource.UserInput,
            Confidence = confidence,
            Content = content,
            Visibility = Visibility.SharedCouple,
            CreatedAt = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero),
        };

    private static ToolInvocation Call(string json) => Invocation(json, CoupleId, UserId);

    private static SearchMemoryTool Tool(StubSearch search) => new(search, new FixedCoupleClock());

    [Fact]
    public async Task A_hit_is_returned_as_an_answer_rather_than_as_a_change()
    {
        var search = new StubSearch(Hit("Likes Italian food"));

        var execution = await Tool(search).ExecuteAsync(Call("""{"query":"italian food"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        // No entity and no note: nothing was created, and the finding is not a
        // footnote to a change that did not happen.
        Assert.Null(execution.EntityId);
        Assert.Null(execution.EntityType);
        Assert.Null(execution.Note);

        Assert.Contains("Likes Italian food", execution.Answer);
        Assert.Contains("a preference", execution.Answer);
        Assert.Contains("12 Mar 2026", execution.Answer);
    }

    [Fact]
    public async Task An_inferred_memory_is_hedged_with_its_provenance()
    {
        // Eval case inference-002: an inferred memory must be surfaced as a
        // hypothesis, never as fact. Enforced by the rendering, so there is no path
        // from the row to a screen that states it plainly.
        var search = new StubSearch(
            Hit("May prefer Italian food", assertion: MemoryAssertion.Inferred, confidence: 0.50m));

        var execution = await Tool(search).ExecuteAsync(Call("""{"query":"food"}"""));

        Assert.Contains("guess", execution.Answer);
        Assert.Contains("0.5", execution.Answer);
    }

    [Fact]
    public async Task Finding_nothing_is_an_answer_and_says_what_was_searched()
    {
        var execution = await Tool(new StubSearch()).ExecuteAsync(Call("""{"query":"sailing"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Contains("Nothing recorded", execution.Answer);
        Assert.Contains("sailing", execution.Answer);

        // "that I can see from here" rather than a bare "nothing". A system with
        // three privacy scopes cannot honestly claim absence, and it must not hint
        // at the alternative either.
        Assert.Contains("from here", execution.Answer);
    }

    [Fact]
    public async Task The_query_carries_no_scope_of_any_kind()
    {
        var search = new StubSearch();

        await Tool(search).ExecuteAsync(Call("""{"query":"anything"}"""));

        var query = Assert.IsType<MemoryQuery>(search.Asked);
        Assert.Equal(CoupleId, query.CoupleId);

        // Everything the caller is allowed to see, decided by row-level security.
        // The properties this record does NOT have are the assertion.
        Assert.Empty(query.Types);
        Assert.Null(query.Since);
    }

    [Fact]
    public async Task A_type_filter_and_a_limit_are_passed_through()
    {
        var search = new StubSearch();

        await Tool(search).ExecuteAsync(
            Call("""{"query":"car","types":["decision","plan","decision"],"limit":3}"""));

        Assert.Equal([MemoryType.Decision, MemoryType.Plan], search.Asked!.Types);
        Assert.Equal(3, search.Asked.Limit);
    }

    [Fact]
    public async Task The_default_limit_is_ten()
    {
        var search = new StubSearch();

        await Tool(search).ExecuteAsync(Call("""{"query":"anything"}"""));

        Assert.Equal(10, search.Asked!.Limit);
    }

    [Fact]
    public async Task A_since_expression_is_resolved_in_the_couples_zone()
    {
        var search = new StubSearch();

        await Tool(search).ExecuteAsync(Call("""{"query":"goa","since_expression":"since june"}"""));

        // 1 June 2026 at the assumed hour, in Asia/Kolkata — normalised to UTC,
        // because timestamptz is an instant and Npgsql refuses an offset it would
        // have to convert.
        Assert.Equal(
            new DateTimeOffset(2026, 6, 1, 3, 30, 0, TimeSpan.Zero),
            search.Asked!.Since);
    }

    [Fact]
    public async Task A_since_expression_that_cannot_be_read_fails_rather_than_widening()
    {
        // Silently searching all of time answers a different question than the one
        // asked, and answers it confidently.
        var search = new StubSearch(Hit("Likes Italian food"));

        var execution = await Tool(search).ExecuteAsync(
            Call("""{"query":"food","since_expression":"since we moved"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Null(search.Asked);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"query":"   "}""")]
    [InlineData("""{"query":"food","types":"preference"}""")]
    [InlineData("""{"query":"food","types":["chore"]}""")]
    [InlineData("""{"query":"food","limit":0}""")]
    [InlineData("""{"query":"food","limit":21}""")]
    [InlineData("""{"query":"food","limit":"ten"}""")]
    [InlineData("""{"query":"food","since_expression":6}""")]
    public async Task Refused_before_execution(string json)
    {
        var validation = await Tool(new StubSearch()).ValidateAsync(Json(json));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public void Nothing_in_the_schema_could_widen_what_is_visible()
    {
        var raw = Tool(new StubSearch()).ParametersSchema.GetRawText();

        Assert.DoesNotContain("visibility", raw);
        Assert.DoesNotContain("owner", raw);
        Assert.DoesNotContain("private", raw);
        Assert.DoesNotContain("couple_id", raw);
    }
}
