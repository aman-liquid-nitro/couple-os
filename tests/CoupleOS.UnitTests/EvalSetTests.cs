using CoupleOS.Evals;
using Xunit.Abstractions;

namespace CoupleOS.UnitTests;

/// <summary>
/// The eval set's own shape, checked before a single model is called.
///
/// M4's exit criterion is that the V0 done-checklist is machine-checked, and the
/// failure that criterion is really aimed at is not a red case — a red case is
/// visible. It is a case that runs, passes, and is measuring something other
/// than what it says. That happened: <c>multi-003</c> carried
/// <c>"response_must_contain_failure": true</c> for a milestone while the
/// harness modelled neither key, so it went green the day <c>create_expense</c>
/// was registered and reported extraction as honesty (STATUS debt 38).
///
/// These tests are cheap and run in the fast suite because they need neither a
/// model nor a database: they are about the file.
/// </summary>
public sealed class EvalSetTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static IReadOnlyList<EvalCase> Cases => EvalCaseLoader.Load();

    [Fact]
    public void Every_case_declares_which_harness_measures_it()
    {
        var undeclared = Cases.Where(c => c.Harness.Count == 0).Select(c => c.Id).ToList();

        Assert.True(
            undeclared.Count == 0,
            $"These cases name no harness, so nothing runs them and nothing reports them as " +
            $"unrun: {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void Every_declared_harness_is_one_that_exists()
    {
        var unknown = Cases
            .SelectMany(c => c.Harness.Select(h => (c.Id, Harness: h)))
            .Where(x => !EvalHarnessNames.All.Contains(x.Harness, StringComparer.Ordinal))
            .Select(x => $"{x.Id} → '{x.Harness}'")
            .ToList();

        Assert.True(
            unknown.Count == 0,
            $"A harness named in the eval set that no code claims runs nothing, quietly: " +
            $"{string.Join(", ", unknown)}");
    }

    /// <summary>
    /// The one that would have caught debt 38 a milestone earlier.
    ///
    /// Every key in a case's <c>expect</c> block must be read by one of the
    /// harnesses that case declares, be documentation, or be listed in the case's
    /// own <c>deferred</c> block with a reason. There is no fourth option, and
    /// that is the design: writing an expectation nobody asserts now costs a
    /// sentence saying so, in the file where the expectation lives.
    /// </summary>
    [Fact]
    public void Every_expectation_is_asserted_by_a_harness_or_deferred_out_loud()
    {
        var unclaimed = Cases
            .SelectMany(c => EvalKeys.Unclaimed(c).Select(k => $"{c.Id}.{k}"))
            .ToList();

        Assert.True(
            unclaimed.Count == 0,
            "These expectation keys are read by no harness the case declares and are not deferred, " +
            "so they read as coverage and are coverage of nothing: " + string.Join(", ", unclaimed) +
            ". Either add the key to a harness in EvalKeys and assert it, add the harness to the " +
            "case, or say in the case's `deferred` block why nothing measures it and when it will.");
    }

    /// <summary>
    /// The mirror failure, and the one that arrives later: a deferral that has
    /// been overtaken. It says "nothing measures this" about something that is
    /// now measured, and the next person to read the file believes it.
    /// </summary>
    [Fact]
    public void No_case_defers_a_key_that_is_now_asserted()
    {
        var stale = Cases
            .SelectMany(c => EvalKeys.StaleDeferrals(c).Select(k => $"{c.Id}.{k}"))
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"These keys are deferred and also read by a harness the case declares — the deferral " +
            $"is a lie the moment somebody trusts it: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Every_deferral_gives_a_reason()
    {
        var empty = Cases
            .SelectMany(c => c.Deferred.Where(d => string.IsNullOrWhiteSpace(d.Value))
                .Select(d => $"{c.Id}.{d.Key}"))
            .ToList();

        Assert.True(
            empty.Count == 0,
            $"A deferral with no reason is a silence with punctuation: {string.Join(", ", empty)}");
    }

    /// <summary>
    /// The coverage rule, shown to bite.
    ///
    /// The four tests above are green, and a green test whose failure path has
    /// never been exercised is the thing they exist to prevent one level up. So
    /// the rule is put to a case built to break it, in memory rather than by
    /// editing the file — a temporarily broken eval set that somebody forgets to
    /// restore is a worse outcome than no test.
    /// </summary>
    [Fact]
    public void A_key_no_declared_harness_reads_is_reported_as_unclaimed()
    {
        var invented = Synthetic(
            harness: [EvalHarnessNames.Extraction],
            expect: """{"tools": [], "response_must_state_no_data": true}""");

        // response_must_state_no_data is a database key, and this case declares
        // only extraction — exactly boundary-003's shape if somebody moved it.
        Assert.Equal(["response_must_state_no_data"], EvalKeys.Unclaimed(invented));

        var corrected = invented with { Harness = [EvalHarnessNames.Extraction, EvalHarnessNames.Database] };

        Assert.Empty(EvalKeys.Unclaimed(corrected));
    }

    [Fact]
    public void A_deferral_of_something_now_asserted_is_reported_as_stale()
    {
        var overtaken = Synthetic(
            harness: [EvalHarnessNames.Database],
            expect: """{"must_supersede": ["m1"]}""",
            deferred: """{"must_supersede": "nothing supersedes yet"}""");

        Assert.Equal(["must_supersede"], EvalKeys.StaleDeferrals(overtaken));
    }

    private static EvalCase Synthetic(IReadOnlyList<string> harness, string expect, string? deferred = null) =>
        System.Text.Json.JsonSerializer.Deserialize<EvalCase>(
            $$"""
              {"id":"synthetic","category":"happy_path","input":"x",
               "harness":{{System.Text.Json.JsonSerializer.Serialize(harness)}},
               "expect":{{expect}}{{(deferred is null ? "" : $",\"deferred\":{deferred}")}}}
              """,
            EvalCaseLoader.Options)!;

    [Fact]
    public void Case_ids_are_unique()
    {
        var duplicates = Cases
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate case ids: {string.Join(", ", duplicates)}");
    }

    /// <summary>
    /// Reports the shape of the set rather than asserting it.
    ///
    /// The number that used to matter — how many cases can run — is 55 of 55 as
    /// of this milestone, and an assertion on it would only restate the two tests
    /// above. What is worth printing is where the coverage actually is, and how
    /// much of the file is a promise rather than a measurement.
    /// </summary>
    [Fact]
    public void The_shape_of_the_eval_set_is_reported()
    {
        var cases = Cases;

        _output.WriteLine($"cases            : {cases.Count}");

        foreach (var harness in EvalHarnessNames.All)
        {
            var count = cases.Count(c => c.Harness.Contains(harness, StringComparer.Ordinal));
            _output.WriteLine($"  {harness,-12}: {count}");
        }

        _output.WriteLine($"case-harness runs: {cases.Sum(c => c.Harness.Count)}");
        _output.WriteLine("");
        _output.WriteLine("deferred expectations — stated in the file, asserted by nothing:");

        foreach (var (id, key, reason) in cases
            .SelectMany(c => c.Deferred.Select(d => (c.Id, d.Key, d.Value)))
            .OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {id}.{key}");
            _output.WriteLine($"      {reason}");
        }

        Assert.NotEmpty(cases);
    }
}
