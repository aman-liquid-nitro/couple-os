using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// One line of data/eval-cases.jsonl. Only the fields the harness uses are
/// modelled; the rest (notes, spec_ref) are documentation for humans.
/// </summary>
public sealed record EvalCase
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("surface")] public string? Surface { get; init; }
    [JsonPropertyName("spec_ref")] public string? SpecRef { get; init; }
    [JsonPropertyName("input")] public required string Input { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }
    [JsonPropertyName("expect")] public EvalExpectation? Expect { get; init; }

    public IReadOnlyList<EvalToolExpectation> ExpectedTools => Expect?.Tools ?? [];

    public override string ToString() => $"{Id} ({Category})";
}

public sealed record EvalExpectation
{
    [JsonPropertyName("tools")] public IReadOnlyList<EvalToolExpectation>? Tools { get; init; }
    [JsonPropertyName("clarification_required")] public bool? ClarificationRequired { get; init; }
    [JsonPropertyName("visibility_inherited")] public string? VisibilityInherited { get; init; }
}

public sealed record EvalToolExpectation
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("args")] public JsonElement Args { get; init; }
}

public static class EvalCaseLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Walks up from the test binary to the repository root. The eval set is
    /// data, not an embedded resource, so it can be edited and diffed without a
    /// rebuild — which is the point of keeping it as JSONL.
    /// </summary>
    public static IReadOnlyList<EvalCase> Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "data", "eval-cases.jsonl")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "data/eval-cases.jsonl was not found above the test binary. The harness reads the " +
                "eval set from the repository rather than embedding it.");
        }

        var path = Path.Combine(directory.FullName, "data", "eval-cases.jsonl");

        return
        [
            .. File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select((line, index) =>
                    JsonSerializer.Deserialize<EvalCase>(line, Options)
                    ?? throw new InvalidOperationException($"Line {index + 1} of eval-cases.jsonl did not parse."))
        ];
    }
}
