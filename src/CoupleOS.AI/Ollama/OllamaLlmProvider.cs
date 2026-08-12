using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using CoupleOS.Application.AI;
using Microsoft.Extensions.Options;

namespace CoupleOS.AI.Ollama;

/// <summary>
/// Talks to Ollama over /api/chat (ADR 0011) — a local instance, or the hosted
/// service, which speaks the identical endpoint and request body. The only
/// difference is the base address and a bearer token, so both are this one class
/// rather than two (ADR 0013).
///
/// Its whole job is translation: Application's vocabulary in, Ollama's wire
/// format out, and back. It does not decide which tools to offer, interpret a
/// tool call, write an audit row, or retry — each of those is someone else's
/// single responsibility, and folding any of them in here would mean this class
/// changed whenever they did.
/// </summary>
public sealed class OllamaLlmProvider(
    HttpClient httpClient,
    IOptions<OllamaOptions> options) : ILlmProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly OllamaOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Distinguishes the two hosts, because ADR 0011 rests on the distinction:
    /// "anything a local model tells us about extraction quality is a floor, not a
    /// result". This value is what lands in <c>LlmUsage.Provider</c> and from
    /// there into the audit trail — if every run were labelled "ollama", a floor
    /// measured on a 4B model and a result measured on a frontier one would be
    /// indistinguishable after the fact, which is the one thing the eval set
    /// exists to keep apart.
    /// </summary>
    public string Name => _options.IsHosted ? "ollama-cloud" : "ollama";

    public async Task<LlmCompletion> CompleteAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = _options.ModelFor(request.Role);
        var payload = BuildPayload(request, model);

        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsJsonAsync("/api/chat", payload, SerializerOptions, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("Ollama returned an empty body for /api/chat.");
        stopwatch.Stop();

        return new LlmCompletion(
            ToolCalls: ReadToolCalls(body),
            Content: body.Message?.Content is { Length: > 0 } content ? content : null,
            Usage: new LlmUsage(
                Provider: Name,
                Model: body.Model ?? model,
                PromptTokens: body.PromptEvalCount,
                CompletionTokens: body.EvalCount,
                Duration: stopwatch.Elapsed));
    }

    private OllamaChatRequest BuildPayload(LlmRequest request, string model) => new()
    {
        Model = model,
        Think = _options.Think,
        Options = new OllamaRequestOptions
        {
            NumCtx = _options.NumCtx,

            // Extraction is not a creative task. The same note should yield the
            // same tool calls today and next week, or the eval set measures
            // nothing and a user's correction never sticks.
            Temperature = 0,
        },
        Messages = [.. request.Messages.Select(m => new OllamaMessage
        {
            Role = m.Role switch
            {
                LlmMessageRole.System => "system",
                LlmMessageRole.User => "user",
                LlmMessageRole.Assistant => "assistant",
                _ => throw new ArgumentOutOfRangeException(nameof(request), m.Role, "Unknown message role."),
            },
            Content = m.Content,
        })],
        Tools = request.Tools.Count == 0
            ? null
            : [.. request.Tools.Select(t => new OllamaTool
            {
                Function = new OllamaFunction
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.ParametersSchema,
                },
            })],
    };

    private static IReadOnlyList<LlmToolCall> ReadToolCalls(OllamaChatResponse body)
    {
        var calls = body.Message?.ToolCalls;
        if (calls is null || calls.Count == 0)
        {
            return [];
        }

        var result = new List<LlmToolCall>(calls.Count);
        foreach (var call in calls)
        {
            // A call without a name cannot be dispatched. Dropping it silently
            // would surface as a block that vanished with no explanation, so it
            // is refused loudly instead.
            if (call.Function?.Name is not { Length: > 0 } name)
            {
                throw new InvalidOperationException(
                    "Ollama returned a tool call with no function name; the response cannot be dispatched.");
            }

            result.Add(new LlmToolCall(name, call.Function.Arguments));
        }

        return result;
    }
}
