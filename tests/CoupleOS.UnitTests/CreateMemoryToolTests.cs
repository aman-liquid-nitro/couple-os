using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// The three rules ADR 0006 puts on a memory, asserted where they are decided.
///
/// None of these is a question about the model. Whether a guess stays a guess,
/// whether a musing expires and whether a correction supersedes are all settled in
/// C#, so they are settled here rather than by asking a model twice and hoping it
/// agrees with itself.
/// </summary>
public sealed class CreateMemoryToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private sealed class RecordingMemoryWriter(params Memory[] existing) : IMemoryWriter
    {
        public List<Memory> Written { get; } = [];

        public List<Memory> Superseded { get; } = [];

        public Memory? Last => Written.Count == 0 ? null : Written[^1];

        public Task<IReadOnlyList<Memory>> FindBySubjectAsync(
            Guid coupleId,
            MemoryType type,
            string subjectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Memory>>(
                [.. existing.Where(m => m.Type == type && m.SubjectKey == subjectKey)]);

        public Task AddAsync(
            Memory memory,
            IReadOnlyList<Memory> superseded,
            CancellationToken cancellationToken = default)
        {
            Written.Add(memory);
            Superseded.AddRange(superseded);

            return Task.CompletedTask;
        }
    }

    private static Memory Existing(
        MemoryType type,
        string subjectKey,
        string content,
        DateTimeOffset? created = null) => new()
        {
            CoupleId = CoupleId,
            Type = type,
            Assertion = MemoryAssertion.UserStated,
            Source = DataSource.UserInput,
            Confidence = 1.00m,
            Content = content,
            SubjectKey = subjectKey,
            Visibility = Visibility.SharedCouple,
            CreatedAt = created ?? new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero),
        };

    private static ToolInvocation Call(string json, Visibility visibility = Visibility.SharedCouple) =>
        Invocation(json, CoupleId, UserId, visibility);

    private static CreateMemoryTool Tool(RecordingMemoryWriter writer) =>
        new(writer, new FixedCoupleClock());

    [Fact]
    public async Task A_stated_preference_is_recorded_as_certain_and_permanent()
    {
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"Likes Italian food","type":"preference","subject_key":"self:cuisine"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Equal("memory", execution.EntityType);
        Assert.Null(execution.Note);

        var memory = Assert.Single(writer.Written);
        Assert.Equal(MemoryType.Preference, memory.Type);
        Assert.Equal(MemoryAssertion.UserStated, memory.Assertion);
        Assert.Equal(1.00m, memory.Confidence);
        Assert.Null(memory.ExpiresAt);
        Assert.Equal(MemoryStatus.Active, memory.Status);

        // Written from the shared file, so it is nobody's private row — and the
        // source says which surface it came from rather than taking the column's
        // default of 'chat'.
        Assert.Equal(Visibility.SharedCouple, memory.Visibility);
        Assert.Null(memory.OwnerUserId);
        Assert.Equal(DataSource.UserInput, memory.Source);
    }

    [Fact]
    public async Task A_memory_written_privately_is_owned_and_sourced_to_the_thread()
    {
        var writer = new RecordingMemoryWriter();

        await Tool(writer).ExecuteAsync(
            Call("""{"content":"She really likes that bag","type":"preference"}""", Visibility.PrivateUser));

        var memory = Assert.Single(writer.Written);
        Assert.Equal(Visibility.PrivateUser, memory.Visibility);
        Assert.Equal(UserId, memory.OwnerUserId);
        Assert.Equal(DataSource.Chat, memory.Source);
    }

    [Fact]
    public async Task An_inferred_memory_cannot_certify_itself()
    {
        // ADR 0006's load-bearing rule. The model asked for 0.95 on something it
        // worked out; 0.7 is the ceiling and the reduction is stated, because a
        // number quietly lowered is a number the couple goes on believing.
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"May prefer Italian","type":"preference","assertion":"inferred","confidence":0.95}"""));

        Assert.Equal(0.70m, Assert.Single(writer.Written).Confidence);
        Assert.Contains("guess", execution.Note);
        Assert.Contains("0.7", execution.Note);
    }

    [Fact]
    public async Task An_inferred_memory_below_the_ceiling_is_left_alone_and_says_nothing()
    {
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"May prefer Italian","type":"preference","assertion":"inferred","confidence":0.5}"""));

        Assert.Equal(0.50m, Assert.Single(writer.Written).Confidence);
        Assert.Null(execution.Note);
    }

    [Fact]
    public async Task Temporary_context_with_no_span_is_kept_for_thirty_days_and_says_so()
    {
        // TOOLS.md: default rather than reject. memories_temp_context_expires would
        // refuse the row outright, and refusing loses the note; a month is the
        // documented answer and it is said out loud.
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"Maybe a new sofa at some point","type":"temporary_context"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var memory = Assert.Single(writer.Written);
        Assert.NotNull(memory.ExpiresAt);
        Assert.Equal(new DateOnly(2026, 9, 12), DateOnly.FromDateTime(memory.ExpiresAt!.Value.UtcDateTime));
        Assert.Contains("30 days", execution.Note);
    }

    [Fact]
    public async Task A_span_the_person_gave_is_resolved_in_the_couples_zone()
    {
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""
                {"content":"Thinking about a sofa","type":"temporary_context","expires_expression":"for two weeks"}
                """));

        var memory = Assert.Single(writer.Written);

        // Two weeks from Thursday 13 August 2026 in Asia/Kolkata.
        Assert.Equal(new DateOnly(2026, 8, 27), DateOnly.FromDateTime(memory.ExpiresAt!.Value.UtcDateTime));
        Assert.Contains("for two weeks", execution.Note);
    }

    [Fact]
    public async Task A_span_that_cannot_be_read_fails_rather_than_falling_back()
    {
        // The 30-day default answers "nobody said how long". It does not license
        // replacing a span somebody did give with a different one — for a temporary
        // memory that decides when the couple stops being reminded of their own idea.
        var writer = new RecordingMemoryWriter();

        var execution = await Tool(writer).ExecuteAsync(
            Call("""
                {"content":"Thinking about a sofa","type":"temporary_context","expires_expression":"until we move"}
                """));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_contradiction_on_the_same_subject_supersedes_rather_than_accumulating()
    {
        // SPEC.md §45, and eval case conflict-001. Both memories must not stay
        // live; the old one keeps its history rather than being deleted.
        var old = Existing(MemoryType.Preference, "self:cuisine", "Likes Italian food");
        var writer = new RecordingMemoryWriter(old);

        var execution = await Tool(writer).ExecuteAsync(
            Call("""
                {"content":"Does not like Italian food anymore","type":"preference","subject_key":"self:cuisine"}
                """));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var replaced = Assert.Single(writer.Superseded);
        Assert.Same(old, replaced);
        Assert.Contains("replaces \"Likes Italian food\"", execution.Note);
    }

    [Fact]
    public async Task The_same_statement_twice_writes_nothing_and_creates_no_entity()
    {
        // SPEC.md §44. Superseding an identical memory would file a correction that
        // corrects nothing, and Created would report a row and count an entity for
        // a statement that changed the couple's knowledge not at all.
        var writer = new RecordingMemoryWriter(
            Existing(MemoryType.Preference, "self:cuisine", "Likes Italian food."));

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"likes  italian food","type":"preference","subject_key":"self:cuisine"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Null(execution.EntityId);
        Assert.Empty(writer.Written);
        Assert.Empty(writer.Superseded);
        Assert.Contains("already recorded", execution.Note);
    }

    [Fact]
    public async Task A_memory_on_a_different_subject_supersedes_nothing()
    {
        var writer = new RecordingMemoryWriter(
            Existing(MemoryType.Preference, "self:cuisine", "Likes Italian food"));

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"Likes long walks","type":"preference","subject_key":"self:exercise"}"""));

        Assert.Single(writer.Written);
        Assert.Empty(writer.Superseded);
        Assert.Null(execution.Note);
    }

    [Fact]
    public async Task A_memory_with_no_subject_key_supersedes_nothing_and_is_never_a_duplicate()
    {
        // Two dinners in Munnar are two memories. Without a subject there is
        // nothing to correct, which is why most episodic memories carry none.
        var writer = new RecordingMemoryWriter(
            Existing(MemoryType.Episodic, "trip:munnar", "We visited Munnar in July"));

        await Tool(writer).ExecuteAsync(
            Call("""{"content":"We visited Munnar in July","type":"episodic"}"""));

        Assert.Single(writer.Written);
        Assert.Empty(writer.Superseded);
    }

    [Fact]
    public async Task A_subject_key_is_normalised_so_two_spellings_find_each_other()
    {
        var old = Existing(MemoryType.Decision, "decision:car", "We decided not to buy a car this year");
        var writer = new RecordingMemoryWriter(old);

        var execution = await Tool(writer).ExecuteAsync(
            Call("""{"content":"We did decide to buy the car","type":"decision","subject_key":"Decision:Car"}"""));

        Assert.Same(old, Assert.Single(writer.Superseded));
        Assert.Equal("decision:car", writer.Last!.SubjectKey);
    }

    [Theory]
    [InlineData("""{"type":"preference"}""")]
    [InlineData("""{"content":"  ","type":"preference"}""")]
    [InlineData("""{"content":"Likes Italian"}""")]
    [InlineData("""{"content":"Likes Italian","type":"chore"}""")]
    [InlineData("""{"content":"Likes Italian","type":"preference","assertion":"user_confirmed"}""")]
    [InlineData("""{"content":"Likes Italian","type":"preference","confidence":1.4}""")]
    [InlineData("""{"content":"Likes Italian","type":"preference","confidence":"high"}""")]
    [InlineData("""{"content":"Likes Italian","type":"preference","expires_expression":30}""")]
    public async Task Refused_before_execution(string json)
    {
        var validation = await Tool(new RecordingMemoryWriter()).ValidateAsync(Json(json));

        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task A_provenance_the_model_may_not_claim_is_refused_by_name()
    {
        // user_confirmed is a real memory_assertion and deliberately absent from the
        // schema: confirming a memory is something a person does, and V0 has no
        // screen that asks. A model that could claim it would be claiming a
        // provenance nobody granted.
        var validation = await Tool(new RecordingMemoryWriter()).ValidateAsync(
            Json("""{"content":"Likes Italian","type":"preference","assertion":"user_confirmed"}"""));

        Assert.Contains(validation.Errors, e => e.Contains("'assertion'"));
    }

    [Fact]
    public void The_schema_offers_no_vocabulary_for_scope_status_or_importance()
    {
        // visibility is checked across every tool by ToolCatalogueTests. These three
        // are this tool's own: status is the pipeline's to write, importance is
        // nothing V0 decides, and owner_user_id is the session's.
        var raw = Tool(new RecordingMemoryWriter()).ParametersSchema.GetRawText();

        Assert.DoesNotContain("status", raw);
        Assert.DoesNotContain("importance", raw);
        Assert.DoesNotContain("owner", raw);
    }
}
