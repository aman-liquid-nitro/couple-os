using CoupleOS.AI.DependencyInjection;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Tools;
using CoupleOS.Evals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// What the extraction harness is actually asking a model, and whether every
/// case it claims can be asked.
///
/// The question this file existed to answer through M0–M3 — how many of the 55
/// cases can run at all — is answered by <c>EvalSetTests</c> now, over the file
/// rather than over the tool registry, and the answer is all of them. What is
/// left here is the half that still depends on what is registered: a case whose
/// required tool does not exist can be declared for extraction and will fail for
/// a reason that has nothing to do with the model.
/// </summary>
public sealed class EvalCoverage(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddOllamaProvider(new ConfigurationBuilder().AddInMemoryCollection([]).Build())
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

    /// <summary>
    /// A case that requires a tool nobody registered is red for a reason the
    /// model cannot fix, and its redness would be read as extraction quality.
    /// The eval set names such tools in <c>unsupported_tools</c> instead, where
    /// they are an assertion rather than an accident.
    /// </summary>
    [Fact]
    public void No_extraction_case_requires_a_tool_that_is_not_registered()
    {
        using var provider = BuildProvider();

        var registered = provider.GetRequiredService<IToolRegistry>().All.Select(t => t.Name).ToHashSet();

        var unavailable = EvalCaseLoader.For(EvalHarness.Extraction)
            .SelectMany(c => c.ExpectedTools.Select(t => (c.Id, t.Name)))
            .Where(x => !registered.Contains(x.Name))
            .Select(x => $"{x.Id} requires {x.Name}")
            .ToList();

        Assert.True(
            unavailable.Count == 0,
            "These cases require tools that are not registered, so they measure the catalogue rather " +
            "than the model. Name the tool in `unsupported_tools` and assert what V0 should do " +
            "instead: " + string.Join("; ", unavailable));
    }

    /// <summary>
    /// The other direction, and the one that goes stale on its own: a tool named
    /// as unsupported that has since been built. The case is then asserting the
    /// absence of a call the model is entitled to make.
    /// </summary>
    [Fact]
    public void No_case_calls_a_tool_unsupported_that_has_since_been_registered()
    {
        using var provider = BuildProvider();

        var registered = provider.GetRequiredService<IToolRegistry>().All.Select(t => t.Name).ToHashSet();

        var overtaken = EvalCaseLoader.Load()
            .SelectMany(c => (c.Expect?.UnsupportedTools ?? []).Select(name => (c.Id, Name: name)))
            .Where(x => registered.Contains(x.Name))
            .Select(x => $"{x.Id} calls {x.Name} unsupported")
            .ToList();

        Assert.True(
            overtaken.Count == 0,
            $"These tools exist now, so the cases need rewriting to the contract they have: " +
            string.Join("; ", overtaken));
    }

    /// <summary>
    /// Prints what the model is being asked, so a later run can tell "the model
    /// got worse" from "somebody reworded a schema" (STATUS debt 42).
    /// </summary>
    [Fact]
    public void The_request_the_eval_set_judges_is_reported()
    {
        using var provider = BuildProvider();

        var tools = provider.GetRequiredService<IToolRegistry>().All;
        var fingerprint = RequestFingerprint.Of(CapturePrompt.Version, CapturePrompt.System, tools);

        _output.WriteLine($"prompt version : {CapturePrompt.Version}");
        _output.WriteLine($"catalogue      : {tools.Count} tools");
        _output.WriteLine($"fingerprint    : {fingerprint}");
        _output.WriteLine("");

        foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            _output.WriteLine($"  {tool.Name,-24}{tool.ParametersSchema.GetRawText().Length,6} bytes of schema");
        }

        Assert.NotEmpty(tools);
    }
}
