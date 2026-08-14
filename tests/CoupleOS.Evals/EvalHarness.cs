namespace CoupleOS.Evals;

/// <summary>
/// Which machinery measures a case.
///
/// Three, because the eval set states three different kinds of property and one
/// harness cannot hold them. Mixing them was the M0 shortcut and it has a cost
/// this milestone is paying off: a case whose property is about a rendered
/// report was being judged by a harness that only compares tool names, so it
/// passed for reasons unrelated to what it claimed (STATUS debt 38).
/// </summary>
public enum EvalHarness
{
    /// <summary>
    /// Model, prompt and tool catalogue in, tool calls out. No database, no
    /// execution. What it can answer: did the model pick the right tool, with
    /// arguments in the contract's shape, and did it stay quiet where staying
    /// quiet was the whole point.
    ///
    /// This is the only harness that calls a model, so it is the only one that
    /// samples rather than asserts (STATUS debt 37).
    /// </summary>
    Extraction,

    /// <summary>
    /// Scripted completions through the real <c>BlockProcessor</c> and the real
    /// report, against doubles. Deterministic on purpose: the properties here are
    /// decided in C# — a forced tool failure must not produce success language,
    /// a question must park without blocking its neighbours, a surface's
    /// visibility must reach the tool — and asking a model to reproduce the
    /// situation would make the assertion depend on it agreeing with itself.
    /// </summary>
    Pipeline,

    /// <summary>
    /// The whole path against PostgreSQL, under row-level security and with rows
    /// already in the tables. The only harness that can answer "was the partner's
    /// private memory absent", "did the old preference supersede", "was the
    /// second Process a no-op" — every one of which is a property of the
    /// database's state, not of anything a model said.
    /// </summary>
    Database,
}

public static class EvalHarnessNames
{
    public const string Extraction = "extraction";
    public const string Pipeline = "pipeline";
    public const string Database = "database";

    public static string Of(EvalHarness harness) => harness switch
    {
        EvalHarness.Extraction => Extraction,
        EvalHarness.Pipeline => Pipeline,
        EvalHarness.Database => Database,
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "Unknown harness."),
    };

    public static IReadOnlyList<string> All { get; } = [Extraction, Pipeline, Database];
}

/// <summary>
/// Which harness asserts which key, and which keys assert nothing on purpose.
///
/// This map is the anti-blind-spot. An expectation key in the eval file must
/// appear in exactly one of these sets, or in a case's own <c>deferred</c> block
/// with a reason — otherwise <c>EvalKeyCoverage</c> fails and says which key and
/// which case. Adding an expectation to the set is therefore a change somebody
/// has to make here as well, which is the whole point: the failure being
/// prevented is a key that reads as coverage and is read by nothing.
/// </summary>
public static class EvalKeys
{
    /// <summary>
    /// Keys the extraction harness reads. All of them are properties of what the
    /// model proposed, and none of them is a property of what happened next.
    /// </summary>
    public static IReadOnlySet<string> Extraction { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tools",
        "tools_optional",
        "clarification_required",
        "response_must_not_claim",
        "unsupported_tools",
    };

    public static IReadOnlySet<string> Pipeline { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "tools",
        "response_must_contain_failure",
        "response_must_not_claim_success_for",
        "change_report_must_list_all",
        "block_status",
        "must_not_block_other_blocks",
        "must_not_relocate_block",
    };

    public static IReadOnlySet<string> Database { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "entities_created",
        "must_supersede",
        "must_not_return_private_partner_data",
        "must_not_return_memory_ids",
        "response_must_not_reveal",
        "must_respect_visibility",
        "response_must_mention_existing",
        "response_must_hedge",
        "response_must_not_state_as_fact",
        "response_must_state_no_data",
        "response_must_not_estimate",

        // M5. All three were deferred through M4 with "there are no attachments
        // yet", which was true and is the kind of deferral that has to be taken
        // off the moment it stops being.
        "attachment_linked_to_entity",
        "attachment_visibility",
        "ocr_attempted",
    };

    /// <summary>
    /// Keys that are notes to a human and are asserted by nothing, deliberately
    /// and permanently.
    ///
    /// <para><c>intent</c> is a label for the reader: nothing in the system emits
    /// an intent, so an assertion on it would be an assertion on this file's own
    /// vocabulary. <c>clarification_about</c> and <c>question_about</c> name
    /// which value is missing, and the wording of the question that results is
    /// not measured — an expectation on phrasing measures luck, which is the
    /// lesson debt 21 paid for twice.</para>
    ///
    /// <para><c>visibility_inherited</c> is the one worth explaining, because it
    /// looks like the most assertable key in the file. It is not a property of a
    /// case: no model emits it, no case can change it, and it is the same single
    /// line of <c>BlockProcessor</c> — <c>Visibility = block.Visibility</c> —
    /// whichever note is being processed. <c>BlockProcessorAttributionTests</c>
    /// asserts it structurally, once, from both surfaces. Copying that assertion
    /// onto thirty cases would run one line of C# thirty times and measure
    /// nothing the first run did not; what it would add is thirty places to
    /// update when the surface changes.</para>
    /// </summary>
    public static IReadOnlySet<string> Documentation { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "intent",
        "clarification_about",
        "question_about",
        "notes_expected",
        "visibility_inherited",
    };

    public static IReadOnlySet<string> For(EvalHarness harness) => harness switch
    {
        EvalHarness.Extraction => Extraction,
        EvalHarness.Pipeline => Pipeline,
        EvalHarness.Database => Database,
        _ => throw new ArgumentOutOfRangeException(nameof(harness), harness, "Unknown harness."),
    };

    /// <summary>
    /// Keys the case declares that nothing reading it will assert. Empty is the
    /// only acceptable answer unless the case's <c>deferred</c> block says
    /// otherwise, and that is what the coverage test checks.
    /// </summary>
    public static IReadOnlyList<string> Unclaimed(EvalCase evalCase)
    {
        ArgumentNullException.ThrowIfNull(evalCase);

        var claimed = new HashSet<string>(Documentation, StringComparer.Ordinal);

        foreach (var name in evalCase.Harness)
        {
            if (name == EvalHarnessNames.Extraction) claimed.UnionWith(Extraction);
            if (name == EvalHarnessNames.Pipeline) claimed.UnionWith(Pipeline);
            if (name == EvalHarnessNames.Database) claimed.UnionWith(Database);
        }

        return [.. evalCase.ExpectationKeys.Where(k => !claimed.Contains(k) && !evalCase.Deferred.ContainsKey(k))];
    }

    /// <summary>
    /// Deferrals that have been overtaken by a harness that now reads the key.
    /// A stale deferral is the mirror failure of an unclaimed key: it says
    /// "nothing measures this" about something that is measured, and the next
    /// person believes it.
    /// </summary>
    public static IReadOnlyList<string> StaleDeferrals(EvalCase evalCase)
    {
        ArgumentNullException.ThrowIfNull(evalCase);

        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in evalCase.Harness)
        {
            if (name == EvalHarnessNames.Extraction) claimed.UnionWith(Extraction);
            if (name == EvalHarnessNames.Pipeline) claimed.UnionWith(Pipeline);
            if (name == EvalHarnessNames.Database) claimed.UnionWith(Database);
        }

        return [.. evalCase.Deferred.Keys.Where(claimed.Contains)];
    }
}
