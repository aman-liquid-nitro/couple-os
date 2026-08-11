using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using Xunit;

namespace CoupleOS.UnitTests;

public sealed class ToolDispatcherTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolExecutionContext Context(bool confirmed = false) => new()
    {
        CoupleId = CoupleId,
        UserId = UserId,
        Visibility = Visibility.SharedCouple,
        IsConfirmed = confirmed,
    };

    /// <summary>Records what it was asked to do, and does nothing else.</summary>
    private sealed class SpyTool(ToolTier tier = ToolTier.None) : ITool
    {
        private const string SchemaJson =
            "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"quantity\":{\"type\":\"string\"}},\"required\":[\"name\"]}";

        public string Name => "create_shopping_item";
        public string Description => "Adds one item.";
        public ToolTier Tier { get; } = tier;
        public bool IsIdempotent => true;
        public JsonElement ParametersSchema => Json(SchemaJson);

        public bool Executed { get; private set; }
        public ToolExecutionContext? SeenContext { get; private set; }
        public Func<ToolExecution>? OnExecute { get; init; }

        public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken ct = default)
        {
            var hasName = arguments.TryGetProperty("name", out var name)
                && !string.IsNullOrWhiteSpace(name.GetString());

            return ValueTask.FromResult(hasName
                ? ToolValidation.Valid
                : ToolValidation.Invalid("'name' is required."));
        }

        public Task<ToolExecution> ExecuteAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            Executed = true;
            SeenContext = invocation.Context;

            return Task.FromResult(OnExecute?.Invoke()
                ?? ToolExecution.Created("shopping_item", Guid.NewGuid()));
        }
    }

    private sealed class RecordingAuditSink : IToolAuditSink
    {
        public List<ToolAuditEntry> Entries { get; } = [];

        public Task RecordAsync(ToolAuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static IToolDispatcher Dispatcher(params ITool[] tools) =>
        new ToolDispatcher(new ToolRegistry(tools));

    [Fact]
    public async Task A_valid_call_executes_and_reports_the_entity_it_created()
    {
        var tool = new SpyTool();

        var result = await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"detergent\"}")),
            Context());

        Assert.True(result.Succeeded);
        Assert.Equal("shopping_item", result.EntityType);
        Assert.NotNull(result.EntityId);
        Assert.True(tool.Executed);
    }

    [Fact]
    public async Task The_tool_receives_the_session_context_not_the_arguments()
    {
        var tool = new SpyTool();

        await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"coffee\"}")),
            Context());

        Assert.Equal(CoupleId, tool.SeenContext!.CoupleId);
        Assert.Equal(Visibility.SharedCouple, tool.SeenContext.Visibility);
    }

    [Theory]
    [InlineData("couple_id")]
    [InlineData("owner_user_id")]
    [InlineData("user_id")]
    [InlineData("visibility")]
    public async Task Arguments_that_would_let_the_model_choose_its_own_scope_are_refused(string forbidden)
    {
        // TOOLS.md rules 2 and 5. A model that could set these could write into
        // another couple's data, or move a surprise into the shared scope.
        var tool = new SpyTool();

        var result = await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json($"{{\"name\":\"x\",\"{forbidden}\":\"anything\"}}")),
            Context());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);
        Assert.False(tool.Executed);
        Assert.Contains(result.Errors!, e => e.Contains(forbidden, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undeclared_properties_are_rejected_rather_than_ignored()
    {
        // Silently dropping one means the model believed it set something that
        // never took effect, and the change report would agree with the model.
        var tool = new SpyTool();

        var result = await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"rice\",\"urgency\":\"high\"}")),
            Context());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);
        Assert.False(tool.Executed);
        Assert.Contains(result.Errors!, e => e.Contains("urgency", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hallucinated_tool_name_is_refused_without_throwing()
    {
        var result = await Dispatcher(new SpyTool()).DispatchAsync(
            new LlmToolCall("summon_groceries", Json("{\"name\":\"x\"}")),
            Context());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);
    }

    [Fact]
    public async Task Validation_failure_prevents_execution()
    {
        var tool = new SpyTool();

        var result = await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"quantity\":\"2\"}")),
            Context());

        Assert.Equal(ToolOutcome.ValidationFailed, result.Outcome);
        Assert.False(tool.Executed);
    }

    [Fact]
    public async Task A_confirm_tier_tool_does_not_execute_until_confirmed()
    {
        var pendingTool = new SpyTool(ToolTier.Confirm);

        var pending = await Dispatcher(pendingTool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"ring\"}")),
            Context());

        Assert.Equal(ToolOutcome.ConfirmationRequired, pending.Outcome);
        Assert.False(pendingTool.Executed);

        var confirmedTool = new SpyTool(ToolTier.Confirm);

        var confirmed = await Dispatcher(confirmedTool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"ring\"}")),
            Context(confirmed: true));

        Assert.True(confirmed.Succeeded);
        Assert.True(confirmedTool.Executed);
    }

    [Fact]
    public async Task A_throwing_tool_becomes_a_failed_outcome_not_an_abandoned_run()
    {
        var tool = new SpyTool { OnExecute = () => throw new InvalidOperationException("database is on fire") };

        var result = await Dispatcher(tool).DispatchAsync(
            new LlmToolCall("create_shopping_item", Json("{\"name\":\"milk\"}")),
            Context());

        Assert.Equal(ToolOutcome.ExecutionFailed, result.Outcome);
        Assert.Contains(result.Errors!, e => e.Contains("on fire", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_tools_answering_to_one_name_is_refused_at_construction()
    {
        // Otherwise which one ran would depend on registration order, and the
        // audit trail would name the wrong code.
        Assert.Throws<InvalidOperationException>(() => new ToolRegistry([new SpyTool(), new SpyTool()]));
    }

    [Fact]
    public async Task Every_outcome_is_audited_including_refusals()
    {
        var sink = new RecordingAuditSink();
        var dispatcher = new AuditingToolDispatcher(Dispatcher(new SpyTool()), sink);

        await dispatcher.DispatchAsync(new LlmToolCall("create_shopping_item", Json("{\"name\":\"tea\"}")), Context());
        await dispatcher.DispatchAsync(new LlmToolCall("create_shopping_item", Json("{\"quantity\":\"2\"}")), Context());
        await dispatcher.DispatchAsync(new LlmToolCall("no_such_tool", Json("{}")), Context());

        Assert.Equal(3, sink.Entries.Count);
        Assert.Equal(ToolOutcome.Success, sink.Entries[0].Outcome);
        Assert.Equal(ToolOutcome.ValidationFailed, sink.Entries[1].Outcome);
        Assert.Equal(ToolOutcome.ValidationFailed, sink.Entries[2].Outcome);
        Assert.All(sink.Entries, e => Assert.Equal(CoupleId, e.Context.CoupleId));
        Assert.All(sink.Entries, e => Assert.True(e.Duration >= TimeSpan.Zero));
    }
}
