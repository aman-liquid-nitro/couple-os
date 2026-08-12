using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using Xunit;

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
/// No database and no model here. CaptureProcessor was built to be testable
/// against fakes and had no unit tests at all (STATUS debt 10); this is the first
/// of them.
/// </summary>
public sealed class CaptureProcessorAttributionTests
{
    private static readonly Guid Couple = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class StubProvider(LlmCompletion completion) : ILlmProvider
    {
        public string Name => "stub";

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(completion);
    }

    private sealed class EmptyRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> All => [];

        public ITool? Find(string name) => null;
    }

    /// <summary>Records the context of every dispatch, which is what carries the attribution.</summary>
    private sealed class CapturingDispatcher : IToolDispatcher
    {
        public List<ToolExecutionContext> Contexts { get; } = [];

        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);

            return Task.FromResult(new ToolResult(call.Name, ToolOutcome.Success, "shopping_item", Guid.CreateVersion7()));
        }
    }

    private sealed class NoopTransaction : ICoupleTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopUnitOfWork : IScopedUnitOfWork
    {
        public Task<ICoupleTransaction> BeginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ICoupleTransaction>(new NoopTransaction());
    }

    private sealed class FixedScope : ICoupleScopeAccessor
    {
        public bool HasScope => true;

        public ICoupleScope Current => new CoupleScope(Couple, User);
    }

    private static LlmToolCall Call(string name) =>
        new(name, JsonDocument.Parse($$"""{"name":"{{name}}-arg"}""").RootElement.Clone());

    private static (CaptureProcessor Processor, CapturingDispatcher Dispatcher) Build(int toolCalls)
    {
        var completion = new LlmCompletion(
            [.. Enumerable.Range(0, toolCalls).Select(i => Call($"create_shopping_item{i}"))],
            Content: null,
            Usage: new LlmUsage("ollama-cloud", "gemma4:31b", PromptTokens: 660, CompletionTokens: 42, Duration: TimeSpan.FromSeconds(2)));

        var dispatcher = new CapturingDispatcher();

        var processor = new CaptureProcessor(
            new StubProvider(completion),
            new EmptyRegistry(),
            dispatcher,
            new NoopUnitOfWork(),
            new FixedScope());

        return (processor, dispatcher);
    }

    [Fact]
    public async Task Every_call_records_which_model_proposed_it()
    {
        var (processor, dispatcher) = Build(toolCalls: 3);

        await processor.ProcessAsync("we need detergent, coffee and rice", CaptureSurface.SharedFile);

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

        await processor.ProcessAsync("we need detergent, coffee and rice", CaptureSurface.SharedFile);

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

        await processor.ProcessAsync("we need detergent", CaptureSurface.SharedFile);

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

        await processor.ProcessAsync("we need detergent and coffee", CaptureSurface.SharedFile);

        foreach (var context in dispatcher.Contexts)
        {
            Assert.Equal(Couple, context.CoupleId);
            Assert.Equal(User, context.UserId);
            Assert.Equal(CaptureSurface.SharedFile.ToVisibility(), context.Visibility);
        }
    }
}
