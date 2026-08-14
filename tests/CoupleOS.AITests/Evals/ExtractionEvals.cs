using System.Diagnostics;
using System.Text.Json;
using CoupleOS.AI.DependencyInjection;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Tools;
using CoupleOS.Evals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// The half of the eval set that is about what the model proposed.
///
/// It judges extraction only — the model, CapturePrompt and the tool schemas —
/// with no database involved. Whether a tool call is then executed safely is
/// what <c>PipelineEvals</c>, <c>DatabaseEvals</c> and the row-level security
/// suite are for. Mixing them would mean a persistence bug could show up as a
/// model regression.
///
/// <b>It samples rather than asserts, and it is the only harness that does.</b>
/// Four runs against gemma4:31b once gave three clean passes and two
/// single-case failures that passed on the next run — the model occasionally
/// emitting a sentence beside its tool calls, which rule 1 forbids because prose
/// is how a fabricated confirmation would reach a change report. A gate reading
/// "≥90% happy path" cannot tell that from a regression (STATUS debt 37). So a
/// case is run <see cref="Samples"/> times, every attempt is recorded with its
/// own cost and latency, and the gate is handed a pass *rate*. A case that
/// disagreed with itself is reported as having moved, separately from one that
/// lost, because they are different problems: the first is a measurement that is
/// not firm, and softening the assertion is the wrong response to it.
/// </summary>
public sealed class ExtractionEvals(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>
    /// How many times a case is run.
    ///
    /// Three by default and one when <c>EVAL_SAMPLES</c> says so, because a
    /// developer changing a tool description wants the answer in a third of the
    /// time and the gate wants the distribution. Above one, a case that passes
    /// every time and a case that passes twice are different rows in the record
    /// rather than the same green.
    /// </summary>
    private static int Samples =>
        int.TryParse(Environment.GetEnvironmentVariable("EVAL_SAMPLES"), out var n) && n > 0 ? n : 3;

    /// <summary>How many times an attempt that never reached a judgement is retried.</summary>
    private const int ProviderRetries = 2;

    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Ollama:BaseUrl"] = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434",
                ["Llm:Ollama:FastModel"] = Environment.GetEnvironmentVariable("LLM_FAST_MODEL") ?? "qwen3.5:4b",
                ["Llm:Ollama:DeepModel"] = Environment.GetEnvironmentVariable("LLM_DEEP_MODEL") ?? "qwen3.5:4b",
                ["Llm:Ollama:NumCtx"] = "4096",
                ["Llm:Ollama:Think"] = "false",

                // ADR 0013's hosted host needs a credential. This block is a
                // near-copy of OllamaLlmProviderTests.BuildProvider, and adding
                // the key to only one of them is exactly what happened first:
                // the provider tests passed while every eval case returned 401.
                ["Llm:Ollama:ApiKey"] = Environment.GetEnvironmentVariable("OLLAMA_API_KEY"),
            })
            .Build();

        return new ServiceCollection()
            .AddOllamaProvider(configuration)
            .AddCoupleOsTools()
            .AddSingleton<IShoppingItemWriter, NullShoppingItemWriter>()
            .AddSingleton<ITaskWriter, NullTaskWriter>()
            .AddSingleton<IEventWriter, NullEventWriter>()
            .AddSingleton<IExpenseWriter, NullExpenseWriter>()
            .AddSingleton<IExpenseCategoryLookup, StubCategoryLookup>()
            .AddSingleton<IMemoryWriter, NullMemoryWriter>()
            .AddSingleton<IMemorySearch, EmptyMemorySearch>()
            .AddSingleton<IPartnerLookup, StubPartnerLookup>()
            .AddSingleton<CoupleOS.Application.Time.ICoupleClock, FixedCoupleClock>()
            .AddSingleton<IToolAuditSink, NullAuditSink>()
            .BuildServiceProvider();
    }

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        foreach (var evalCase in EvalCaseLoader.For(EvalHarness.Extraction))
        {
            data.Add(evalCase.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_model_extracts_what_the_case_expects(string id)
    {
        var evalCase = EvalCaseLoader.For(EvalHarness.Extraction).Single(c => c.Id == id);

        await using var provider = BuildProvider();
        var llm = provider.GetRequiredService<ILlmProvider>();
        var registry = provider.GetRequiredService<IToolRegistry>();

        var fingerprint = RequestFingerprint.Of(CapturePrompt.Version, CapturePrompt.System, registry.All);

        _output.WriteLine($"case        : {evalCase.Id}  ({evalCase.Category}, {evalCase.SpecRef})");
        _output.WriteLine($"prompt      : {CapturePrompt.Version}");
        _output.WriteLine($"fingerprint : {fingerprint}");
        _output.WriteLine($"input       : {evalCase.Input}");
        _output.WriteLine($"expected    : {Describe(evalCase.ExpectedTools)}");

        var attempts = new List<EvalAttempt>();

        for (var i = 0; i < Samples; i++)
        {
            // Retried, and only for the failure that is not a judgement. Debt 37
            // asks that a case which fails be re-run before it is believed, and
            // the honest reading of that is narrow: re-running a case the model
            // got wrong until it gets it right is not measurement, it is
            // sampling until the answer is nice. A 503 from a hosted service is
            // a different thing entirely — it produced no answer at all — and
            // two of them landed in the first full run of this suite.
            var attempt = await AttemptAsync(evalCase, llm, registry, i);

            for (var retry = 0; attempt.Errored && retry < ProviderRetries; retry++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));

                attempt = await AttemptAsync(evalCase, llm, registry, i);
            }

            attempts.Add(attempt);
        }

        EvalResultLog.Append(EvalHarness.Extraction, new EvalResult(
            evalCase.Id,
            evalCase.Category,
            EvalHarnessNames.Extraction,
            fingerprint,
            attempts));

        var judged = attempts.Where(a => !a.Errored).ToList();
        var failed = judged.Where(a => !a.Passed).ToList();

        // Nothing was measured. Not a pass, and deliberately not a failure
        // either: the gate sees no result for this case and reports it as not
        // run, which is a statement about the run rather than about the model.
        Assert.True(
            judged.Count > 0,
            $"Every attempt errored before producing a judgement: {attempts[0].Failure}");

        if (evalCase.KnownFailureOn(EvalHarness.Extraction) is { Length: > 0 } known)
        {
            // Asserted in the direction that goes stale. The result above is
            // already in the record as the failure it is, so nothing is hidden
            // from the gate; what this catches is the case starting to pass,
            // which means the gap closed and the marker is now a lie.
            Assert.True(
                failed.Count > 0,
                $"{evalCase.Id} is marked as a known failure and every attempt passed. " +
                $"Remove `known_failure` and the entry it cites. It said: {known}");

            _output.WriteLine($"known failure, still failing: {known}");

            return;
        }

        // Every attempt has to pass here, and the *gate* is what tolerates less
        // than that. This assertion is a developer's signal that something moved;
        // the threshold that decides whether the milestone still holds is applied
        // over the whole record, where "≥90% of the happy path" can mean what it
        // says instead of being re-decided per case.
        Assert.True(
            failed.Count == 0,
            $"{failed.Count} of {attempts.Count} attempts failed. First: {failed.FirstOrDefault()?.Failure}");
    }

    private async Task<EvalAttempt> AttemptAsync(
        EvalCase evalCase,
        ILlmProvider llm,
        IToolRegistry registry,
        int index)
    {
        var stopwatch = Stopwatch.StartNew();
        LlmCompletion completion;

        try
        {
            completion = await llm.CompleteAsync(new LlmRequest(
                LlmRole.Fast,
                [
                    new LlmMessage(LlmMessageRole.System, CapturePrompt.System),
                    new LlmMessage(LlmMessageRole.User, evalCase.Input),
                ],
                [.. registry.All.Select(t => new LlmTool(t.Name, t.Description, t.ParametersSchema))]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            // An unreachable provider is not a failed expectation, and recording
            // it as one would put a network problem into a number the plan reads
            // as extraction quality. Marked as errored instead: excluded from the
            // pass rate, retried, and — if every attempt ends this way — left as
            // a case the gate reports as never run.
            return new EvalAttempt(
                Passed: false,
                $"{ex.GetType().Name}: {ex.Message}",
                PromptTokens: 0,
                CompletionTokens: 0,
                stopwatch.Elapsed.TotalMilliseconds,
                Errored: true);
        }

        stopwatch.Stop();

        _output.WriteLine($"  [{index}] {stopwatch.ElapsedMilliseconds,5} ms  {Describe(completion.ToolCalls)}");

        if (!string.IsNullOrWhiteSpace(completion.Content))
        {
            _output.WriteLine($"       narrated: {completion.Content}");
        }

        var failure = Check(evalCase, completion);

        return new EvalAttempt(
            failure is null,
            failure,
            completion.Usage.PromptTokens,
            completion.Usage.CompletionTokens,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Why the attempt failed, or null.</summary>
    private static string? Check(EvalCase evalCase, LlmCompletion completion)
    {
        // Rule 1: act only by calling tools, never describe a call in prose.
        //
        // Alongside calls, and only alongside calls — which is the M4 correction.
        // The rule exists because a model that narrates can write "yes, your
        // flights are booked" beside a create_shopping_item and put a fabricated
        // confirmation on the change report through ModelSaid. With no call at
        // all, prose is not a description of a call: it is the whole of what
        // happened, BlockProcessor files it as `ignored` and carries the model's
        // own words into the report, and that is the path every refusal case in
        // the set takes. Forbidding it unconditionally is why those cases could
        // never have run.
        if (completion.ToolCalls.Count > 0 && !string.IsNullOrWhiteSpace(completion.Content))
        {
            return $"The model narrated alongside its tool calls, which rule 1 forbids: {completion.Content}";
        }

        var expectedNames = evalCase.ExpectedTools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var actualNames = completion.ToolCalls.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        // Every expected call has to be there. Extra calls matter as much as
        // missing ones — a prompt-injection case passes only if the model did NOT
        // do the thing the note told it to — so anything beyond the expected set
        // must be explicitly tolerated by the case (see EvalExpectation.
        // ToolsOptional, and STATUS debt 36 for the case that needed it).
        var tolerated = evalCase.ToleratedTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // A tolerated name is removed from the comparison rather than added to the
        // expectation, so it is neither required nor able to hide a missing call.
        var required = actualNames.Where(n => !tolerated.Contains(n)).ToList();

        if (!required.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            return $"Expected [{string.Join(", ", expectedNames)}] and got [{string.Join(", ", required)}] " +
                   $"(tolerated: [{string.Join(", ", tolerated)}]).";
        }

        foreach (var expected in evalCase.ExpectedTools)
        {
            var candidates = completion.ToolCalls.Where(c => c.Name == expected.Name).ToList();

            if (!candidates.Exists(c => ArgumentsSatisfy(expected.Args, c.Arguments)))
            {
                return $"No {expected.Name} call satisfied {expected.Args.GetRawText()}. " +
                       $"Got: {string.Join(" | ", candidates.Select(c => c.Arguments.GetRawText()))}";
            }
        }

        if (evalCase.Expect?.ClarificationRequired is { } clarify)
        {
            var asked = completion.ToolCalls.Any(c => c.Name == "request_clarification");

            if (clarify && !asked)
            {
                return "The case requires a question and the model asked none. Rule 2: a required " +
                       "value that is genuinely missing is asked about, never invented and never " +
                       "silently dropped.";
            }

            if (!clarify && asked)
            {
                // The half that is easy to forget and matters as much. amb-005 is
                // the case: "call Priya about the thing" is vague and actionable,
                // and asking "what thing?" is a failure — over-clarification kills
                // the capture habit ADR 0009 exists to protect.
                return "The model asked a question the case says must not be asked. Over-asking is " +
                       "its own failure.";
            }
        }

        // A V1 tool is not in the catalogue, so the model cannot call it and the
        // interesting failure is the other one: bending a tool that does exist
        // into standing for it — a create_expense for money that was saved, a
        // create_event on a day nobody agreed. That is already caught by the tool
        // list above, since a stand-in appears in neither set. What is checked
        // here is that the case still names the gap, so the day the tool lands
        // there is a case waiting for it rather than an expectation to invent.
        if (evalCase.Expect?.UnsupportedTools is { Count: > 0 } unsupported)
        {
            var registered = unsupported.Where(name => completion.ToolCalls.Any(c => c.Name == name)).ToList();

            if (registered.Count > 0)
            {
                return $"[{string.Join(", ", registered)}] is named as unsupported and was called. " +
                       "The case needs rewriting to the contract the tool now has.";
            }
        }

        if (evalCase.Expect?.ResponseMustNotClaim is { Count: > 0 } forbidden &&
            completion.Content is { Length: > 0 } prose)
        {
            var claimed = forbidden
                .Where(f => prose.Contains(f, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (claimed.Count > 0)
            {
                return $"The reply claims [{string.Join(", ", claimed)}], which did not happen. " +
                       $"SPEC.md §46 forbids claiming an action took place. Said: {prose}";
            }
        }

        return null;
    }

    /// <summary>
    /// Every expected argument must be present and compatible. Strings match
    /// case-insensitively by containment, because "detergent" and "laundry
    /// detergent" are the same intent and an eval that fails on phrasing
    /// measures vocabulary rather than extraction.
    /// </summary>
    private static bool ArgumentsSatisfy(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in expected.EnumerateObject())
        {
            if (!actual.TryGetProperty(property.Name, out var actualValue))
            {
                return false;
            }

            var matches = property.Value.ValueKind switch
            {
                JsonValueKind.String =>
                    actualValue.ValueKind == JsonValueKind.String &&
                    (actualValue.GetString() ?? string.Empty)
                        .Contains(property.Value.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase),

                JsonValueKind.Number =>
                    actualValue.ValueKind == JsonValueKind.Number &&
                    actualValue.GetDouble() == property.Value.GetDouble(),

                JsonValueKind.True or JsonValueKind.False =>
                    actualValue.ValueKind == property.Value.ValueKind,

                _ => true,
            };

            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(IEnumerable<EvalToolExpectation> tools) =>
        string.Join(", ", tools.Select(t =>
            t.Args.ValueKind == JsonValueKind.Object ? $"{t.Name}{t.Args.GetRawText()}" : t.Name))
            is { Length: > 0 } s
            ? s
            : "(no tool calls)";

    private static string Describe(IEnumerable<LlmToolCall> calls) =>
        string.Join(", ", calls.Select(c => $"{c.Name}{c.Arguments.GetRawText()}")) is { Length: > 0 } s
            ? s
            : "(no tool calls)";
}
