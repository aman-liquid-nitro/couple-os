using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CoupleOS.Application.AI;

namespace CoupleOS.Application.Tools;

/// <summary>
/// Records every dispatch, then returns the result untouched.
///
/// A decorator rather than a step inside ToolDispatcher, for two reasons. The
/// dispatcher keeps one responsibility, and auditing becomes impossible to skip
/// — a tool author cannot forget it, because they never touch it. TOOLS.md
/// makes auditing universal, so it belongs in the composition, not in each
/// implementation.
/// </summary>
public sealed class AuditingToolDispatcher(IToolDispatcher inner, IToolAuditSink auditSink) : IToolDispatcher
{
    private readonly IToolDispatcher _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly IToolAuditSink _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));

    public async Task<ToolResult> DispatchAsync(
        LlmToolCall call,
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await _inner.DispatchAsync(call, context, cancellationToken);
        stopwatch.Stop();

        await _auditSink.RecordAsync(
            new ToolAuditEntry
            {
                ToolName = result.ToolName,
                Arguments = call.Arguments,
                Outcome = result.Outcome,
                Context = context,
                Duration = stopwatch.Elapsed,
                EntityType = result.EntityType,
                EntityId = result.EntityId,
                Error = result.Errors is { Count: > 0 } errors ? string.Join("; ", errors) : null,
                Result = ToResultJson(result),
            },
            cancellationToken);

        return result;
    }

    /// <summary>
    /// The three things a call can report beyond the row it wrote: the note on a
    /// completed value, the question it asked instead, and the answer it found.
    ///
    /// Null when there are none, rather than <c>{}</c>: an empty object in
    /// `result` reads as "the tool returned nothing", which is a claim, where a
    /// null reads as "there was nothing to return". The errors are not repeated
    /// here — they have their own column.
    /// </summary>
    private static string? ToResultJson(ToolResult result)
    {
        if (result.Note is null && result.Question is null && result.Answer is null)
        {
            return null;
        }

        return JsonSerializer.Serialize(
            new AuditedToolResult(result.Note, result.Question, result.Answer),
            AuditJson);
    }

    private static readonly JsonSerializerOptions AuditJson =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record AuditedToolResult(string? Note, string? Question, string? Answer);
}
