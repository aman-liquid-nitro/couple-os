using System.Text.Json;
using CoupleOS.AI.DependencyInjection;
using CoupleOS.Application.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.AITests;

/// <summary>
/// Exercises a real local model. This is not a unit test and is not meant to be
/// — ADR 0004 makes tool calls the only way anything gets written, so "does this
/// model emit a dispatchable tool call" is the single assumption the whole
/// ingestion pipeline rests on. Mocking it would test the mock.
///
/// Requires Ollama running with the configured model pulled.
/// </summary>
public sealed class OllamaLlmProviderTests
{
    private const string Note = "we're out of detergent and coffee. also dinner at Priya's parents on Saturday 8pm";

    private static ILlmProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Ollama:BaseUrl"] = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434",
                ["Llm:Ollama:FastModel"] = Environment.GetEnvironmentVariable("LLM_FAST_MODEL") ?? "qwen3.5:4b",
                ["Llm:Ollama:DeepModel"] = Environment.GetEnvironmentVariable("LLM_DEEP_MODEL") ?? "qwen3.5:4b",
                ["Llm:Ollama:NumCtx"] = "4096",
                ["Llm:Ollama:Think"] = "false",
            })
            .Build();

        return new ServiceCollection()
            .AddOllamaProvider(configuration)
            .BuildServiceProvider()
            .GetRequiredService<ILlmProvider>();
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<LlmTool> Tools() =>
    [
        new LlmTool(
            "create_shopping_item",
            "Add one item to the couple's shopping list. Call once per distinct item.",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "name":     { "type": "string", "description": "The item, singular, lowercase" },
                    "quantity": { "type": "string", "description": "Optional quantity as written" }
                  },
                  "required": ["name"]
                }
                """)),

        new LlmTool(
            "create_event",
            "Create a calendar event. Do NOT compute a date; pass the person's own wording in date_expression.",
            Schema("""
                {
                  "type": "object",
                  "properties": {
                    "title":           { "type": "string" },
                    "date_expression": { "type": "string", "description": "Verbatim, e.g. 'saturday 8pm'" },
                    "location":        { "type": "string" }
                  },
                  "required": ["title", "date_expression"]
                }
                """)),
    ];

    private static LlmRequest Request() => new(
        LlmRole.Fast,
        [
            new LlmMessage(LlmMessageRole.System,
                "You convert a person's note into structured actions by calling tools. " +
                "Act only by calling tools; never describe a call in prose. " +
                "Never invent a value to satisfy a required field. " +
                "Do not do calendar arithmetic — emit the date as the person wrote it. " +
                "Call one tool per distinct item."),
            new LlmMessage(LlmMessageRole.User, Note),
        ],
        Tools());

    /// <summary>
    /// Turns "connection actively refused" into an instruction. These tests
    /// exercise a real model on purpose (ADR 0004 makes tool calls the only
    /// write path, so mocking would test the mock), which means an absent
    /// Ollama is a setup problem, not a defect — and should read like one.
    /// </summary>
    private static async Task<LlmCompletion> CompleteAsync(ILlmProvider provider)
    {
        try
        {
            return await provider.CompleteAsync(Request());
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                "Ollama is not reachable. Start it, then confirm the model is pulled:\n" +
                "    ollama serve        (or the tray application on Windows)\n" +
                "    ollama pull qwen3.5:4b\n" +
                "Override the endpoint with OLLAMA_BASE_URL if it runs elsewhere.",
                ex);
        }
    }

    [Fact]
    public async Task A_note_becomes_dispatchable_tool_calls()
    {
        var provider = BuildProvider();

        var completion = await CompleteAsync(provider);

        Assert.Equal(3, completion.ToolCalls.Count);

        var shopping = completion.ToolCalls
            .Where(c => c.Name == "create_shopping_item")
            .Select(c => c.Arguments.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(2, shopping.Count);
        Assert.Contains(shopping, s => s!.Contains("detergent", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(shopping, s => s!.Contains("coffee", StringComparison.OrdinalIgnoreCase));

        var calendar = Assert.Single(completion.ToolCalls, c => c.Name == "create_event");
        var expression = calendar.Arguments.GetProperty("date_expression").GetString();

        // The model must pass the wording through, not resolve it. It cannot
        // know today's date, and a plausible-but-wrong ISO timestamp would book
        // dinner on the wrong weekend with no visible error (docs/TOOLS.md).
        Assert.Contains("aturday", expression);
    }

    [Fact]
    public async Task The_model_narrates_nothing_when_tools_are_available()
    {
        var provider = BuildProvider();

        var completion = await CompleteAsync(provider);

        // Prose instead of a tool call means nothing is written and the change
        // report shows no entities created — a silent no-op. Worth asserting.
        Assert.True(
            string.IsNullOrWhiteSpace(completion.Content),
            $"Expected tool calls only, but the model also wrote prose: {completion.Content}");
    }

    [Fact]
    public async Task Usage_is_reported_so_cost_can_be_audited()
    {
        var provider = BuildProvider();

        var completion = await CompleteAsync(provider);

        // SPEC.md 49 and 50 require per-couple cost tracking. Local inference is
        // free, but ai_actions records the same fields regardless so the switch
        // to a paid provider is configuration rather than new plumbing.
        Assert.Equal("ollama", completion.Usage.Provider);
        Assert.False(string.IsNullOrWhiteSpace(completion.Usage.Model));
        Assert.True(completion.Usage.PromptTokens > 0, "Prompt tokens were not reported.");
        Assert.True(completion.Usage.CompletionTokens > 0, "Completion tokens were not reported.");
        Assert.True(completion.Usage.Duration > TimeSpan.Zero, "Duration was not measured.");
    }
}
