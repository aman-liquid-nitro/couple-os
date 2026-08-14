using System.Text.Json;

namespace CoupleOS.Evals;

public static class EvalCaseLoader
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Walks up from the calling binary to the repository root. The eval set is
    /// data, not an embedded resource, so it can be edited and diffed without a
    /// rebuild — which is the point of keeping it as JSONL.
    /// </summary>
    public static IReadOnlyList<EvalCase> Load() =>
        [
            .. File.ReadLines(Path())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select((line, index) =>
                    JsonSerializer.Deserialize<EvalCase>(line, Options)
                    ?? throw new InvalidOperationException($"Line {index + 1} of eval-cases.jsonl did not parse."))
        ];

    /// <summary>Cases this harness is declared to run.</summary>
    public static IReadOnlyList<EvalCase> For(EvalHarness harness) =>
        [.. Load().Where(c => c.RunsOn(harness))];

    public static string Path() => System.IO.Path.Combine(RepositoryRoot().FullName, "data", "eval-cases.jsonl");

    /// <summary>
    /// The repository root, found by the file that identifies it. Also where run
    /// records are written, so the gate reads them from one agreed place rather
    /// than from wherever a particular test runner happened to set the working
    /// directory.
    /// </summary>
    public static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(System.IO.Path.Combine(directory.FullName, "data", "eval-cases.jsonl")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            "data/eval-cases.jsonl was not found above the binary. The harness reads the eval set " +
            "from the repository rather than embedding it.");
    }
}
