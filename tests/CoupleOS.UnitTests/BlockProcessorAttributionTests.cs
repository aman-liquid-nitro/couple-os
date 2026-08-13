using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using Xunit;

using static CoupleOS.UnitTests.BlockProcessorFakes;

namespace CoupleOS.UnitTests;

/// <summary>
/// How model attribution reaches the audit trail, and specifically why the token
/// counts are not simply copied onto every row.
///
/// One completion can produce several tool calls and is billed once. SPEC.md 50
/// wants per-couple cost, which is a <c>SUM</c> over <c>ai_actions</c> — so
/// putting the completion's counts on all N rows would report N times the real
/// figure, quietly, with every row individually looking correct. These tests fix
/// the alternative: the counts land exactly once per completion.
///
/// The rule now lives in BlockProcessor because a completion is now one block's
/// worth rather than one file's. That makes the property more important, not
/// less: a twenty-line dump is twenty completions, and getting this wrong would
/// overcount a run by more than it used to overcount a note.
/// </summary>
public sealed class BlockProcessorAttributionTests
{
    private static (BlockProcessor Processor, CapturingDispatcher Dispatcher) Build(int toolCalls)
    {
        var completion = new LlmCompletion(
            [.. Enumerable.Range(0, toolCalls).Select(i => Call($"create_shopping_item{i}"))],
            Content: null,
            Usage: Usage());

        var dispatcher = new CapturingDispatcher();

        var processor = new BlockProcessor(
            new StubProvider(completion),
            new EmptyRegistry(),
            dispatcher,
            new RecordingBlockStore(),
            new CountingUnitOfWork(),
            new FixedScope(),
            TimeProvider.System);

        return (processor, dispatcher);
    }

    [Fact]
    public async Task Every_call_records_which_model_proposed_it()
    {
        var (processor, dispatcher) = Build(toolCalls: 3);

        await processor.ProcessAsync(Block("we need detergent, coffee and rice"));

        Assert.Equal(3, dispatcher.Contexts.Count);

        foreach (var context in dispatcher.Contexts)
        {
            Assert.NotNull(context.Attribution);
            Assert.Equal("ollama-cloud", context.Attribution.Provider);
            Assert.Equal("gemma4:31b", context.Attribution.Model);
            Assert.Equal(LlmRole.Fast, context.Attribution.Role);
        }
    }

    [Fact]
    public async Task Token_counts_are_recorded_once_per_completion_so_summing_them_is_correct()
    {
        // The defect this prevents: three rows each claiming 660 prompt tokens,
        // so a cost query reports 1980 for one 660-token call. Every row would
        // look right on its own.
        var (processor, dispatcher) = Build(toolCalls: 3);

        await processor.ProcessAsync(Block("we need detergent, coffee and rice"));

        var prompt = dispatcher.Contexts.Sum(c => c.Attribution?.PromptTokens ?? 0);
        var completion = dispatcher.Contexts.Sum(c => c.Attribution?.CompletionTokens ?? 0);

        Assert.Equal(660, prompt);
        Assert.Equal(42, completion);

        Assert.Equal(1, dispatcher.Contexts.Count(c => c.Attribution?.PromptTokens is not null));
    }

    [Fact]
    public async Task A_single_call_completion_still_records_its_tokens()
    {
        // The boundary the "first call only" rule must not break: with one call,
        // that call is the first one and must carry the counts.
        var (processor, dispatcher) = Build(toolCalls: 1);

        await processor.ProcessAsync(Block("we need detergent"));

        var only = Assert.Single(dispatcher.Contexts);
        Assert.Equal(660, only.Attribution?.PromptTokens);
        Assert.Equal(42, only.Attribution?.CompletionTokens);
    }

    [Fact]
    public async Task Attribution_does_not_disturb_the_scope_the_tool_runs_under()
    {
        // Attribution rides on the same record as the couple scope and visibility.
        // Copying that record per call must not lose the fields that decide who
        // can read the row (ADR 0005) or which surface it came from (ADR 0009).
        var (processor, dispatcher) = Build(toolCalls: 2);

        await processor.ProcessAsync(Block("we need detergent and coffee"));

        foreach (var context in dispatcher.Contexts)
        {
            Assert.Equal(Couple, context.CoupleId);
            Assert.Equal(User, context.UserId);
            Assert.Equal(CaptureSurface.SharedFile.ToVisibility(), context.Visibility);
        }
    }
}
