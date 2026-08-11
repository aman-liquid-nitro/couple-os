using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoupleOS.AI.Ollama;

// Ollama's /api/chat payloads. Internal on purpose: the wire format is Ollama's
// to change, and nothing outside this folder should be shaped by it.

internal sealed record OllamaChatRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("stream")] public bool Stream => false;
    [JsonPropertyName("think")] public required bool Think { get; init; }
    [JsonPropertyName("options")] public required OllamaRequestOptions Options { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<OllamaMessage> Messages { get; init; }
    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OllamaTool>? Tools { get; init; }
}

internal sealed record OllamaRequestOptions
{
    [JsonPropertyName("num_ctx")] public required int NumCtx { get; init; }
    [JsonPropertyName("temperature")] public required double Temperature { get; init; }
}

internal sealed record OllamaMessage
{
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
}

internal sealed record OllamaTool
{
    [JsonPropertyName("type")] public string Type => "function";
    [JsonPropertyName("function")] public required OllamaFunction Function { get; init; }
}

internal sealed record OllamaFunction
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public required string Description { get; init; }
    [JsonPropertyName("parameters")] public required JsonElement Parameters { get; init; }
}

internal sealed record OllamaChatResponse
{
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("message")] public OllamaResponseMessage? Message { get; init; }
    [JsonPropertyName("done_reason")] public string? DoneReason { get; init; }
    [JsonPropertyName("prompt_eval_count")] public int PromptEvalCount { get; init; }
    [JsonPropertyName("eval_count")] public int EvalCount { get; init; }
    [JsonPropertyName("total_duration")] public long TotalDurationNanoseconds { get; init; }
}

internal sealed record OllamaResponseMessage
{
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("tool_calls")] public IReadOnlyList<OllamaToolCall>? ToolCalls { get; init; }
}

internal sealed record OllamaToolCall
{
    [JsonPropertyName("function")] public OllamaToolCallFunction? Function { get; init; }
}

internal sealed record OllamaToolCallFunction
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("arguments")] public JsonElement Arguments { get; init; }
}
