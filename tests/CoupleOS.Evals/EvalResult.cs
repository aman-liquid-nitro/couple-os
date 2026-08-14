using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoupleOS.Evals;

/// <summary>
/// One run of one case.
///
/// <para><b>Cost and latency are on the attempt, not on the case</b>, which is
/// SPEC.md §49 read literally: the figure a person acts on is what one call
/// costs, and a case sampled three times has three of them. Summing them onto
/// the case would produce a number three times too large with nothing on the row
/// to say so — the same overcounting <see cref="CoupleOS.Application.AI.LlmAttribution"/>
/// exists to avoid one layer down.</para>
/// </summary>
/// <param name="Failure">
/// Why, in the assertion's own words. Null when it passed. Kept per attempt
/// because the interesting case is a set of attempts that disagree, and the
/// disagreement is only legible if each one says what it saw.
/// </param>
/// <param name="Errored">
/// True when the attempt produced no judgement at all — the provider was
/// unreachable, or answered 503.
///
/// <para><b>Separate from failing, and the separation is load-bearing.</b> A
/// hosted model returning 503 is not evidence that extraction got worse, and
/// folding it into the pass rate would put a network incident into a number
/// IMPLEMENTATION_PLAN.md reads as quality — the exact confusion debt 37
/// describes from the other direction. An errored attempt is excluded from
/// <see cref="EvalResult.PassRate"/> and counted out loud, and a case whose
/// every attempt errored is <i>not run</i>, which the gate already treats as a
/// failure of the run rather than of the model.</para>
/// </param>
public sealed record EvalAttempt(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("ms")] double Milliseconds,
    [property: JsonPropertyName("errored")] bool Errored = false);

/// <summary>
/// What a harness observed for one case, written to the run record and read by
/// the gate.
/// </summary>
/// <param name="Fingerprint">
/// What the model was actually asked — prompt version and tool catalogue
/// together (see <see cref="RequestFingerprint"/>). STATUS debt 42: without it a
/// later run cannot tell "the model got worse" from "somebody reworded a tool
/// description", and this milestone is the one that demonstrated the second
/// happening.
/// </param>
public sealed record EvalResult(
    [property: JsonPropertyName("case")] string CaseId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("attempts")] IReadOnlyList<EvalAttempt> Attempts)
{
    /// <summary>Attempts that produced a judgement at all.</summary>
    [JsonIgnore]
    public IReadOnlyList<EvalAttempt> Judged => [.. Attempts.Where(a => !a.Errored)];

    /// <summary>
    /// The share of judged attempts that passed.
    ///
    /// A rate rather than a verdict, because the gate's thresholds are
    /// percentages and a boolean would have to decide, here, whether two passes
    /// out of three is a pass — a decision that belongs to the threshold, not to
    /// the row. Debt 37 is exactly the ambiguity: "≥90% happy path" cannot
    /// distinguish a regression from a flake unless the flake is carried as a
    /// fraction.
    /// </summary>
    [JsonIgnore]
    public double PassRate => Judged.Count == 0 ? 0 : (double)Judged.Count(a => a.Passed) / Judged.Count;

    /// <summary>
    /// True when nothing was measured. Not the same as failing, and reported as
    /// its own thing: a run whose provider was down all morning must not read as
    /// a model that lost its touch, and must not read as green either.
    /// </summary>
    [JsonIgnore]
    public bool NotJudged => Judged.Count == 0;

    [JsonIgnore]
    public int Errors => Attempts.Count(a => a.Errored);

    /// <summary>
    /// True when the attempts disagreed with each other. Named separately from
    /// failure because it is a different problem with a different fix: a case
    /// that fails every time is a regression, and a case that fails one time in
    /// four is a prompt that is not firm enough or an assertion that is too tight.
    /// </summary>
    [JsonIgnore]
    public bool Moved => Judged.Count > 1 && Judged.Select(a => a.Passed).Distinct().Count() > 1;

    [JsonIgnore]
    public string? FirstFailure => Judged.FirstOrDefault(a => !a.Passed)?.Failure;
}

/// <summary>
/// Where a harness leaves its results for the gate to read.
///
/// A file rather than a shared static, because the three harnesses live in three
/// test projects in three processes, and the gate is a fourth. xUnit gives no
/// way to aggregate across that, and the attempt to fake one — a collection
/// fixture asserting thresholds on dispose — would put the gate's verdict inside
/// a teardown, which is the one place a failure is easiest to miss.
/// </summary>
public static class EvalResultLog
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lock Gate = new();

    public static string Directory =>
        System.IO.Path.Combine(EvalCaseLoader.RepositoryRoot().FullName, "artifacts", "eval");

    public static string PathFor(EvalHarness harness) =>
        System.IO.Path.Combine(Directory, $"{EvalHarnessNames.Of(harness)}.jsonl");

    /// <summary>
    /// Clears this harness's record. Called once as a harness starts, so a gate
    /// reading the directory cannot mistake yesterday's green for today's.
    /// </summary>
    public static void Reset(EvalHarness harness)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathFor(harness), string.Empty);
    }

    /// <summary>
    /// Appends one case's result. Locked because xUnit runs a theory's cases in
    /// parallel by default and two appends interleaving would produce a line
    /// neither the gate nor a person could read.
    /// </summary>
    public static void Append(EvalHarness harness, EvalResult result)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var line = JsonSerializer.Serialize(result, Options);

        lock (Gate)
        {
            File.AppendAllText(PathFor(harness), line + Environment.NewLine);
        }
    }

    public static IReadOnlyList<EvalResult> Read(EvalHarness harness)
    {
        var path = PathFor(harness);

        if (!File.Exists(path))
        {
            return [];
        }

        return
        [
            .. File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<EvalResult>(line, Options)!)
        ];
    }

    public static IReadOnlyList<EvalResult> ReadAll() =>
        [.. Enum.GetValues<EvalHarness>().SelectMany(Read)];
}
