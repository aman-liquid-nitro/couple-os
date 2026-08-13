using CoupleOS.AI.DependencyInjection;
using CoupleOS.Application.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace CoupleOS.AITests.Evals;

/// <summary>
/// Reports how much of the eval set can actually run.
///
/// Without this, a harness that executes four of fifty-five cases looks
/// identical to one that executes all of them: green. The untested remainder
/// would become a blind spot that feels like coverage — which is the same
/// failure this project has hit repeatedly, silence read as success.
/// </summary>
public sealed class EvalCoverage(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void The_share_of_the_eval_set_that_can_run_is_reported_and_meets_M0s_bar()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        using var provider = new ServiceCollection()
            .AddOllamaProvider(configuration)
            .AddCoupleOsTools()
            .AddSingleton<IShoppingItemWriter, NullShoppingItemWriter>()
            .AddSingleton<ITaskWriter, NullTaskWriter>()
            .AddSingleton<IEventWriter, NullEventWriter>()
            .AddSingleton<IPartnerLookup, StubPartnerLookup>()
            .AddSingleton<CoupleOS.Application.Time.ICoupleClock, FixedCoupleClock>()
            .AddSingleton<IToolAuditSink, NullAuditSink>()
            .BuildServiceProvider();

        var registered = provider.GetRequiredService<IToolRegistry>().All.Select(t => t.Name).ToHashSet();
        var cases = EvalCaseLoader.Load();

        var withTools = cases.Where(c => c.ExpectedTools.Count > 0).ToList();
        var runnable = withTools.Where(c => c.ExpectedTools.All(t => registered.Contains(t.Name))).ToList();

        var missing = withTools
            .SelectMany(c => c.ExpectedTools.Select(t => t.Name))
            .Where(name => !registered.Contains(name))
            .GroupBy(name => name)
            .OrderByDescending(g => g.Count())
            .ToList();

        _output.WriteLine($"eval cases            : {cases.Count}");
        _output.WriteLine($"cases expecting tools : {withTools.Count}");
        _output.WriteLine($"runnable today        : {runnable.Count}");
        _output.WriteLine($"registered tools      : {string.Join(", ", registered.OrderBy(n => n))}");
        _output.WriteLine("");
        _output.WriteLine("blocked on tools that do not exist yet:");

        foreach (var group in missing)
        {
            _output.WriteLine($"  {group.Count(),3}  {group.Key}");
        }

        // M0 asks for three cases wired in. The gate rises as tools land: M3
        // adds the remaining six, and this number should climb with it.
        Assert.True(
            runnable.Count >= 3,
            $"M0 requires at least three eval cases to run; {runnable.Count} can.");
    }
}
