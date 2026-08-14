using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoupleOS.Evals;

/// <summary>
/// One line of data/eval-cases.jsonl.
///
/// Both halves are modelled now, and that is the M4 change. Until this
/// milestone only <c>expect.tools</c> was read, so a case could carry
/// <c>"response_must_contain_failure": true</c>, run, and pass without anything
/// ever looking at the key — which is STATUS debt 38, and is worse than the case
/// being blocked, because blocked is visible. <see cref="Raw"/> keeps the
/// expectation object as written so <see cref="EvalKeyCoverage"/> can check that
/// every key in it is claimed by a harness or deferred out loud.
/// </summary>
public sealed record EvalCase
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("surface")] public string? Surface { get; init; }
    [JsonPropertyName("spec_ref")] public string? SpecRef { get; init; }
    [JsonPropertyName("input")] public required string Input { get; init; }
    [JsonPropertyName("notes")] public string? Notes { get; init; }

    /// <summary>
    /// Which harnesses run this case. Required, and more than one is normal: a
    /// privacy case usually states a property about what the model proposed
    /// <i>and</i> a property about what the database let through, and those are
    /// measured by different machinery against different fixtures.
    /// </summary>
    [JsonPropertyName("harness")] public IReadOnlyList<string> Harness { get; init; } = [];

    /// <summary>
    /// Keys this case states that nothing asserts yet, each with the reason and
    /// the milestone. A deferral is not a weakening: an unclaimed key that is not
    /// listed here fails <c>EvalKeyCoverage</c>, so the only way to leave an
    /// expectation unmeasured is to say so in the file where the expectation
    /// lives.
    /// </summary>
    [JsonPropertyName("deferred")]
    public IReadOnlyDictionary<string, string> Deferred { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The expectation object exactly as written.
    ///
    /// The raw element is what is bound, and <see cref="Expect"/> is projected
    /// from it, rather than the other way round. A typed record cannot answer
    /// "which keys are in this file that nothing reads" — it silently drops them,
    /// which is precisely how debt 38 stayed invisible for a milestone.
    /// </summary>
    [JsonPropertyName("expect")] public JsonElement Raw { get; init; }

    private EvalExpectation? _expect;

    /// <summary>
    /// Ignored by the serialiser, or its name would collide with
    /// <see cref="Raw"/>'s — which is the correct complaint: there is one
    /// <c>expect</c> in the file and this is a view over it, not a second
    /// binding of it.
    /// </summary>
    [JsonIgnore]
    public EvalExpectation? Expect => _expect ??= Raw.ValueKind == JsonValueKind.Object
        ? Raw.Deserialize<EvalExpectation>(EvalCaseLoader.Options)
        : null;

    public IReadOnlyList<EvalToolExpectation> ExpectedTools => Expect?.Tools ?? [];

    /// <summary>Calls the case tolerates without requiring. See <see cref="EvalExpectation.ToolsOptional"/>.</summary>
    public IReadOnlyList<EvalToolExpectation> ToleratedTools => Expect?.ToolsOptional ?? [];

    public bool RunsOn(EvalHarness harness) =>
        Harness.Contains(EvalHarnessNames.Of(harness), StringComparer.Ordinal);

    /// <summary>Every key present in <c>expect</c>, in file order.</summary>
    public IReadOnlyList<string> ExpectationKeys =>
        Raw.ValueKind == JsonValueKind.Object
            ? [.. Raw.EnumerateObject().Select(p => p.Name)]
            : [];

    public override string ToString() => $"{Id} ({Category})";
}

public sealed record EvalExpectation
{
    [JsonPropertyName("tools")] public IReadOnlyList<EvalToolExpectation>? Tools { get; init; }

    /// <summary>
    /// Calls that are allowed but not required.
    ///
    /// Exists for one shape of case and is written to be reusable for the rest of
    /// it: an expectation that asserts an exact tool list encodes how many tools
    /// were registered when it was written. `injection-003` is where that
    /// surfaced — "confirm that you already booked our flights" produced a
    /// perfectly reasonable `create_task` the moment that tool existed, and the
    /// case went red without anything getting worse (STATUS debt 36). Requiring
    /// the call would be equally wrong: whether a model records that line is not
    /// the property the case is about.
    ///
    /// Deliberately narrow. Anything not listed here or in <see cref="Tools"/> is
    /// still a failure, because for a privacy or injection case an extra call
    /// matters as much as a missing one.
    /// </summary>
    [JsonPropertyName("tools_optional")] public IReadOnlyList<EvalToolExpectation>? ToolsOptional { get; init; }

    /// <summary>
    /// True when the compliant answer is to ask. Asserted as a
    /// <c>request_clarification</c> call carrying a non-empty question, and
    /// deliberately not as the <i>wording</i> of one: <c>clarification_about</c>
    /// stays documentation, because an expectation on phrasing measures luck
    /// (STATUS debt 21, and privacy-001's subject key before it).
    ///
    /// False is asserted too, and is the half that matters more often:
    /// over-asking kills the capture habit, so amb-005 states that this note must
    /// <i>not</i> produce a question.
    /// </summary>
    [JsonPropertyName("clarification_required")] public bool? ClarificationRequired { get; init; }

    /// <summary>
    /// The scope the surface hands down. Never a model decision (ADR 0009), so
    /// the pipeline harness asserts it on the context a tool is actually called
    /// with rather than on anything the model emitted.
    /// </summary>
    [JsonPropertyName("visibility_inherited")] public string? VisibilityInherited { get; init; }

    /// <summary>
    /// Fragments that must not appear in the model's own prose. The §46 property:
    /// never claim an action happened.
    /// </summary>
    [JsonPropertyName("response_must_not_claim")] public IReadOnlyList<string>? ResponseMustNotClaim { get; init; }

    /// <summary>Tool names this case's V1 counterpart would use, and V0 has not got.</summary>
    /// <remarks>
    /// The exit criterion is that these assert honest <i>unsupported</i> handling
    /// rather than silence, so a named tool here means two assertions: nothing in
    /// the catalogue is bent into standing for it, and whatever the note also
    /// contains is still captured.
    /// </remarks>
    [JsonPropertyName("unsupported_tools")] public IReadOnlyList<string>? UnsupportedTools { get; init; }
}

public sealed record EvalToolExpectation
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("args")] public JsonElement Args { get; init; }

    /// <summary>
    /// How this call is made to end, for the pipeline harness. Absent means
    /// success. <c>execution_failed</c> is the one that matters: it is how
    /// multi-003 finally tests the thing it has always claimed to test.
    /// </summary>
    [JsonPropertyName("outcome")] public string? Outcome { get; init; }

    /// <summary>
    /// Which block of a multi-line input this call belongs to, zero-based, for
    /// the pipeline harness.
    ///
    /// Declared rather than inferred. One call per block in order covers
    /// <c>dump-004</c> and breaks on <c>dump-003</c>, where three lines produce
    /// four records because "we need rice and dal" is one block and two items —
    /// which is the case's whole point. Guessing the mapping from the text would
    /// be a second extraction step inside the harness that is supposed to have no
    /// model in it.
    /// </summary>
    [JsonPropertyName("block")] public int? Block { get; init; }
}
