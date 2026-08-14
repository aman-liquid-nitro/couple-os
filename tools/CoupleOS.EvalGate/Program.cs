using CoupleOS.Evals;

// The gate from IMPLEMENTATION_PLAN.md M4, and the thing that turns the harness
// into one: ≥90% happy path, 100% privacy, 100% prompt injection, 100%
// idempotency, over a run record the harnesses wrote.
//
// Nothing runs this but a person (there is no CI, by decision), which makes the
// third failure below the important one rather than a nicety: the run record is
// whatever was left in artifacts/eval, and a gate that judged only what it found
// there would report green on a harness somebody skipped ten minutes ago.
//
// It fails on three things and the third is the one worth having. A category
// under its bar, obviously. A result naming a case the eval set does not
// declare, which catches a stale record. And a case the set declares that no
// harness reported — because a gate that judges only what it was handed cannot
// notice a harness that never ran, and "40 of 55, all green" is exactly the
// shape of success this project keeps finding underneath a failure.
//
// Usage:
//   dotnet run --project tools/CoupleOS.EvalGate            judge every harness
//   dotnet run --project tools/CoupleOS.EvalGate -- extraction pipeline
//
// Naming harnesses narrows both sides of the comparison, so a developer who ran
// only the fast ones is told about the fast ones rather than about the eleven
// database cases they did not ask for.

var requested = args.Length == 0
    ? Enum.GetValues<EvalHarness>()
    : [.. args.Select(Parse)];

var cases = EvalCaseLoader.Load();
var results = requested.SelectMany(EvalResultLog.Read).ToList();

var scoped = cases
    .Select(c => c with { Harness = [.. c.Harness.Where(h => requested.Any(r => EvalHarnessNames.Of(r) == h))] })
    .Where(c => c.Harness.Count > 0)
    .ToList();

var verdict = EvalGate.Judge(scoped, results);

Console.WriteLine(EvalGate.Render(verdict, results));

// Known failures are scored as the failures they are — declaring one does not
// move a threshold — so they are listed here to say why a category is short
// rather than to excuse it.
var known = scoped
    .SelectMany(c => c.KnownFailure.Select(k => (c.Id, Harness: k.Key, Reason: k.Value)))
    .Where(k => requested.Any(r => EvalHarnessNames.Of(r) == k.Harness))
    .ToList();

if (known.Count > 0)
{
    Console.WriteLine("known failures, counted against the score rather than excused:");

    foreach (var (id, harness, reason) in known)
    {
        Console.WriteLine($"  {id} [{harness}]");
        Console.WriteLine($"      {Wrap(reason)}");
    }

    Console.WriteLine();
}

var notJudged = results.Where(r => r.NotJudged).ToList();

if (notJudged.Count > 0)
{
    Console.WriteLine(
        $"{notJudged.Count} case(s) produced no judgement at all — the provider never answered: " +
        string.Join(", ", notJudged.Select(r => r.CaseId)));
    Console.WriteLine();
}

var errors = results.Sum(r => r.Errors);

if (errors > 0)
{
    Console.WriteLine($"{errors} attempt(s) errored and were retried or excluded from the rates above.");
    Console.WriteLine();
}

return verdict.Passed ? 0 : 1;

static EvalHarness Parse(string name) => name.Trim().ToLowerInvariant() switch
{
    EvalHarnessNames.Extraction => EvalHarness.Extraction,
    EvalHarnessNames.Pipeline => EvalHarness.Pipeline,
    EvalHarnessNames.Database => EvalHarness.Database,
    var other => throw new ArgumentException(
        $"Unknown harness '{other}'. Expected one of: {string.Join(", ", EvalHarnessNames.All)}."),
};

static string Wrap(string text)
{
    var words = text.Split(' ');
    var lines = new List<string>();
    var line = new System.Text.StringBuilder();

    foreach (var word in words)
    {
        if (line.Length + word.Length > 92)
        {
            lines.Add(line.ToString());
            line.Clear();
        }

        if (line.Length > 0)
        {
            line.Append(' ');
        }

        line.Append(word);
    }

    lines.Add(line.ToString());

    return string.Join("\n      ", lines);
}
