using System.ComponentModel.DataAnnotations;
using CoupleOS.Application.AI;

namespace CoupleOS.AI.Ollama;

public sealed class OllamaOptions
{
    public const string SectionName = "Llm:Ollama";

    [Required]
    public Uri BaseUrl { get; set; } = new("http://localhost:11434");

    /// <summary>Model serving the Fast role. See ADR 0011 for how it was chosen.</summary>
    [Required]
    public string FastModel { get; set; } = "qwen3.5:4b";

    [Required]
    public string DeepModel { get; set; } = "qwen3.5:4b";

    /// <summary>
    /// Context window. MUST be set explicitly: Ollama's default is far below what
    /// these models support, and exceeding it does not error — it truncates. In
    /// this system that silently drops the tail of a long dump file and produces
    /// a change report that looks complete but is not (ADR 0011).
    /// </summary>
    [Range(512, 131_072)]
    public int NumCtx { get; set; } = 4096;

    /// <summary>
    /// Thinking traces off. Measured: they were 571 of 673 generated tokens and
    /// made the same call 4.6x slower. The extraction path wants a tool call,
    /// not an essay about one.
    /// </summary>
    public bool Think { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 120;

    public string ModelFor(LlmRole role) => role switch
    {
        LlmRole.Fast => FastModel,
        LlmRole.Deep => DeepModel,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown model role."),
    };
}
