using System.Diagnostics;
using System.Text.Json;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using CoupleOS.Evals;
using Xunit.Abstractions;

namespace CoupleOS.UnitTests;

/// <summary>
/// The half of the eval set that is about what the system did with a tool call,
/// not about whether a model proposed the right one.
///
/// <b>Deterministic on purpose.</b> The completion is scripted from the case's
/// own expectation and the outcomes are forced, so no model is called. That
/// looks like weakening the test and is the opposite: these properties are
/// decided in C# — a forced failure must not produce success language, a
/// question must park its block without touching the others, every change must
/// reach the report — and driving them through a model would make each
/// assertion depend on that model reproducing the situation twice in a row.
/// STATUS debt 37 is what that costs when it is unavoidable; here it is
/// avoidable.
///
/// <b>Why it did not exist before.</b> `multi-003` has carried
/// <c>"outcome": "execution_failed"</c> and
/// <c>"response_must_contain_failure": true</c> since M0 and nothing read either
/// key, so the case passed by comparing tool names — the single most important
/// honesty test in the suite, measuring extraction (debt 38). Testing it needs a
/// harness that can inject a fault into one call and read the rendered report,
/// which is a different fixture from "call a model and compare tool calls".
/// This is that fixture.
/// </summary>
public sealed class PipelineEvals(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();

        foreach (var evalCase in EvalCaseLoader.For(EvalHarness.Pipeline))
        {
            data.Add(evalCase.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_pipeline_does_what_the_case_expects(string id)
    {
        var evalCase = EvalCaseLoader.For(EvalHarness.Pipeline).Single(c => c.Id == id);
        var stopwatch = Stopwatch.StartNew();

        var failure = await RunAsync(evalCase);

        stopwatch.Stop();

        EvalResultLog.Append(EvalHarness.Pipeline, new EvalResult(
            evalCase.Id,
            evalCase.Category,
            EvalHarnessNames.Pipeline,

            // No model was asked, so there is no prompt or catalogue behind this
            // number and recording one would invite a comparison that means
            // nothing. Deterministic results are comparable across prompts by
            // construction.
            Fingerprint: "n/a",
            [new EvalAttempt(failure is null, failure, 0, 0, stopwatch.Elapsed.TotalMilliseconds)]));

        Assert.True(failure is null, failure);
    }

    /// <summary>
    /// The honesty assertion, shown to bite.
    ///
    /// `multi-003` passes, and it passed before this harness existed too — by
    /// comparing tool names while the failure it names never happened. So the
    /// same case is run with the forced failure suppressed: both calls succeed,
    /// the report is entirely truthful about a run in which nothing went wrong,
    /// and the case must fail. If it does not, this harness is the previous one
    /// with more code in it.
    /// </summary>
    [Fact]
    public async Task The_failure_assertion_fails_when_nothing_actually_fails()
    {
        var evalCase = EvalCaseLoader.For(EvalHarness.Pipeline).Single(c => c.Id == "multi-003");

        var failure = await RunAsync(evalCase, honourForcedFailures: false);

        Assert.NotNull(failure);
        Assert.Contains("no failure line", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs one case and returns why it failed, or null.
    ///
    /// A returned string rather than an assertion, so the result reaches the run
    /// record either way — a case that throws past the recorder is a case the
    /// gate counts as never run, which is the same silence as being unrunnable.
    /// </summary>
    private async Task<string?> RunAsync(EvalCase evalCase, bool honourForcedFailures = true)
    {
        var surface = evalCase.Surface == "private_chat" ? Visibility.PrivateUser : Visibility.SharedCouple;
        var segments = BlockSegmenter.Segment(evalCase.Input);

        if (segments.Count == 0)
        {
            return "The case's input segments into no blocks at all.";
        }

        var script = Distribute(evalCase.ExpectedTools, segments.Count);

        var reports = new List<BlockReport>();
        var dispatched = new List<(string Tool, ToolOutcome Outcome)>();

        for (var i = 0; i < segments.Count; i++)
        {
            var block = new DumpBlock
            {
                DumpFileId = Guid.CreateVersion7(),
                CoupleId = BlockProcessorFakes.Couple,
                Visibility = surface,
                OwnerUserId = surface == Visibility.PrivateUser ? BlockProcessorFakes.User : null,
                ContentHash = segments[i].ContentHash,
                RawText = segments[i].RawText,
            };

            var dispatcher = new ScriptedDispatcher(script[i], honourForcedFailures);

            var processor = new BlockProcessor(
                new BlockProcessorFakes.StubProvider(new LlmCompletion(
                    [.. script[i].Select(Call)],
                    Content: null,
                    BlockProcessorFakes.Usage())),
                new BlockProcessorFakes.EmptyRegistry(),
                dispatcher,
                new BlockProcessorFakes.RecordingBlockStore(),
                new BlockProcessorFakes.RecordingAttachments(),
                new BlockProcessorFakes.CountingUnitOfWork(),
                new BlockProcessorFakes.FixedScope(),
                TimeProvider.System);

            reports.Add(await processor.ProcessAsync(block));
            dispatched.AddRange(dispatcher.Dispatched);

            // The block object is what a relocation would move, so it is read
            // after processing rather than before. Nothing in the pipeline is
            // supposed to be able to — which is exactly why surface-001 asserts
            // it: ADR 0009 says relocating a user's words on AI judgment is its
            // own violation, and "nothing does that today" is a claim, not a fact.
            if (Expects(evalCase, "must_not_relocate_block") && block.Visibility != surface)
            {
                return $"The block's visibility moved from {surface} to {block.Visibility} during processing.";
            }
        }

        var report = new CaptureReport(reports, reports.Count, 0, null);

        foreach (var line in report.Blocks)
        {
            _output.WriteLine($"[{line.Status}] {line.RawText}");

            foreach (var change in line.Changes)
            {
                _output.WriteLine($"    {change.Outcome,-16} {change.Description}");
            }

            if (line.Note is { Length: > 0 })
            {
                _output.WriteLine($"    note: {line.Note}");
            }
        }

        return Check(evalCase, report, dispatched);
    }

    private static string? Check(
        EvalCase evalCase,
        CaptureReport report,
        IReadOnlyList<(string Tool, ToolOutcome Outcome)> dispatched)
    {
        var changes = report.Blocks.SelectMany(b => b.Changes).ToList();

        // Every scripted call reached the dispatcher. Cheap, and it is what makes
        // every other assertion below mean something: a report with no failure
        // line in it is correct when no call failed.
        foreach (var expected in evalCase.ExpectedTools)
        {
            if (!dispatched.Any(d => d.Tool == expected.Name))
            {
                return $"{expected.Name} was scripted and never dispatched.";
            }
        }

        if (Expects(evalCase, "response_must_contain_failure"))
        {
            var failures = changes.Where(c => c.Outcome != ToolOutcome.Success).ToList();

            if (failures.Count == 0)
            {
                return "The report contains no failure line, and the case forced a call to fail. " +
                       "SPEC.md §46: when a tool call fails the response must say so.";
            }

            // Not merely present — legible. A report line that says a call
            // failed without saying which call is the same silence in a longer
            // sentence.
            var forced = evalCase.ExpectedTools.Where(t => t.Outcome == "execution_failed").Select(t => t.Name);

            foreach (var name in forced)
            {
                if (!failures.Any(f => f.ToolName == name))
                {
                    return $"{name} was forced to fail and no failure line names it. " +
                           $"Failure lines: {string.Join(" | ", failures.Select(f => f.Description))}";
                }
            }
        }

        if (evalCase.Raw.TryGetProperty("response_must_not_claim_success_for", out var forbidden) &&
            forbidden.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in forbidden.EnumerateArray())
            {
                var name = element.GetString();

                if (name is null)
                {
                    continue;
                }

                // Two ways this could go wrong and both are checked, because the
                // one that would actually ship is the second. A failed call
                // counted as applied would be a bug in CaptureReport.Applied; a
                // failed call *described* in the language of a success is a bug
                // in the sentence, and the sentence is what the couple reads.
                if (report.Applied.Any(c => c.ToolName == name))
                {
                    return $"{name} failed and is listed among the changes that were applied.";
                }

                var line = changes.FirstOrDefault(c => c.ToolName == name && c.Outcome != ToolOutcome.Success);

                if (line is not null && !line.Description.Contains("not applied", StringComparison.Ordinal))
                {
                    return $"{name} failed and its report line does not say so: \"{line.Description}\"";
                }
            }
        }

        if (Expects(evalCase, "change_report_must_list_all"))
        {
            foreach (var expected in evalCase.ExpectedTools)
            {
                if (!changes.Any(c => c.ToolName == expected.Name))
                {
                    return $"{expected.Name} produced a call and no line in the report. " +
                           "Silent omission is §46's failure in batch form.";
                }
            }

            if (changes.Count != evalCase.ExpectedTools.Count)
            {
                return $"The report has {changes.Count} lines for {evalCase.ExpectedTools.Count} calls.";
            }
        }

        if (evalCase.Raw.TryGetProperty("block_status", out var status) && status.ValueKind == JsonValueKind.String)
        {
            var wanted = status.GetString() switch
            {
                "needs_input" => DumpBlockStatus.NeedsInput,
                "processed" => DumpBlockStatus.Processed,
                "failed" => DumpBlockStatus.Failed,
                "ignored" => DumpBlockStatus.Ignored,
                var other => throw new InvalidOperationException($"Unknown block_status '{other}'."),
            };

            if (!report.Blocks.Any(b => b.Status == wanted))
            {
                return $"No block reached status {wanted}. Got: " +
                       string.Join(", ", report.Blocks.Select(b => $"{b.Status}"));
            }
        }

        if (Expects(evalCase, "must_not_block_other_blocks"))
        {
            var parked = report.Blocks.Count(b => b.Status == DumpBlockStatus.NeedsInput);
            var settled = report.Blocks.Count(b => b.Status == DumpBlockStatus.Processed);

            if (parked == 0)
            {
                return "Nothing parked, so the case cannot show that parking left the others alone.";
            }

            if (settled == 0)
            {
                return $"One block parked and none of the other {report.Blocks.Count - parked} was processed — " +
                       "a question blocked the run, which is the failure this case exists for.";
            }
        }

        return null;
    }

    private static bool Expects(EvalCase evalCase, string key) =>
        evalCase.Raw.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Which call goes to which block.
    ///
    /// A single block takes all of them and needs no ceremony. Past that the
    /// case has to say, via <see cref="EvalToolExpectation.Block"/> — a call
    /// whose block is unstated lands in the first one, so a two-block case that
    /// forgot to label its calls fails loudly rather than distributing them
    /// plausibly.
    /// </summary>
    private static IReadOnlyList<EvalToolExpectation>[] Distribute(
        IReadOnlyList<EvalToolExpectation> tools,
        int blocks)
    {
        var script = new List<EvalToolExpectation>[blocks];

        for (var i = 0; i < blocks; i++)
        {
            script[i] = [];
        }

        foreach (var tool in tools)
        {
            var index = blocks == 1 ? 0 : Math.Clamp(tool.Block ?? 0, 0, blocks - 1);

            script[index].Add(tool);
        }

        return [.. script];
    }

    private static LlmToolCall Call(EvalToolExpectation expectation) =>
        new(expectation.Name,
            expectation.Args.ValueKind == JsonValueKind.Object
                ? expectation.Args
                : JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>
    /// Executes the case's script: the outcome each call is to have, and the
    /// shape of result that outcome implies.
    ///
    /// It answers as the real tools do rather than as a uniform stub, because
    /// the properties under test are about how the report renders a result — a
    /// question has to arrive as <c>Asked</c> or the block will not park, and a
    /// failure has to arrive with an error or the report line has nothing to say.
    /// </summary>
    private sealed class ScriptedDispatcher(IReadOnlyList<EvalToolExpectation> script, bool honourForcedFailures)
        : IToolDispatcher
    {
        public List<(string Tool, ToolOutcome Outcome)> Dispatched { get; } = [];

        public Task<ToolResult> DispatchAsync(
            LlmToolCall call,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            var expectation = script.FirstOrDefault(t => t.Name == call.Name);
            var failed = honourForcedFailures && expectation?.Outcome == "execution_failed";

            var result = failed
                ? new ToolResult(call.Name, ToolOutcome.ExecutionFailed, Errors: ["the writer refused the row"])
                : call.Name == "request_clarification"
                    ? new ToolResult(call.Name, ToolOutcome.Success, Question: "When is it due?")
                    : call.Name == "search_memory"
                        ? new ToolResult(call.Name, ToolOutcome.Success, Answer: "Nothing recorded that I can see.")
                        : new ToolResult(call.Name, ToolOutcome.Success, EntityOf(call.Name), Guid.CreateVersion7());

            Dispatched.Add((call.Name, result.Outcome));

            return Task.FromResult(result);
        }

        private static string EntityOf(string toolName) => toolName switch
        {
            "create_shopping_item" => "shopping_item",
            "create_task" or "create_reminder" => "task",
            "create_event" => "event",
            "create_expense" => "expense",
            _ => "memory",
        };
    }
}
