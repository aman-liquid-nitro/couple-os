using System.Text.Json;
using CoupleOS.Application;
using CoupleOS.Application.AI;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// A whole Process run against the real database, with a scripted model.
///
/// The model is a stub on purpose. What is being tested here is the accounting —
/// every block reaching a status, the report matching the file, a second run
/// creating nothing — and a real model would make those assertions depend on
/// whether it felt like calling a tool this time. ExtractionEvals measure the
/// model; these measure the pipeline around it.
///
/// The exit criterion M2 was written for is the first test below: a line that
/// produces no tool call must still appear in the report. It did not, once, and
/// an event disappeared with nobody able to tell.
/// </summary>
[Collection(SharedFileCollection.Name)]
public sealed class CaptureRunTests : IClassFixture<RlsFixture>
{
    /// <summary>
    /// Answers per block, keyed by a word in the block's text.
    ///
    /// Keyed by content rather than by call order because the run's whole claim
    /// is that each block gets its own completion. An order-keyed stub would pass
    /// just as happily if every block were sent in one call.
    /// </summary>
    private sealed class ScriptedProvider(Func<string, LlmCompletion> script) : ILlmProvider
    {
        public string Name => "scripted";

        public List<string> Prompts { get; } = [];

        public Task<LlmCompletion> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var text = request.Messages.Last(m => m.Role == LlmMessageRole.User).Content;
            Prompts.Add(text);

            return Task.FromResult(script(text));
        }
    }

    private static LlmUsage Usage() =>
        new("scripted", "stub-model", PromptTokens: 100, CompletionTokens: 10, TimeSpan.FromMilliseconds(250));

    private static LlmCompletion Creates(string itemName) =>
        new(
            [new LlmToolCall(
                "create_shopping_item",
                JsonDocument.Parse($"{{\"name\":\"{itemName}\"}}").RootElement.Clone())],
            Content: null,
            Usage: Usage());

    private static LlmCompletion SaysNothing(string narration) =>
        new([], narration, Usage());

    private static ServiceProvider BuildProvider(ILlmProvider llm) =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsApplication()
            .AddSingleton(llm)
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));
        return scope;
    }

    /// <summary>The shared file outlives the test run, so every note carries a nonce.</summary>
    private static string Nonce() => Guid.NewGuid().ToString("N")[..8];

    private static async Task WriteFileAsync(ServiceProvider provider, string content)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var editor = request.ServiceProvider.GetRequiredService<ISharedFileEditor>();

        var current = await editor.ReadAsync();
        var save = await editor.SaveAsync(content, current.Version);

        Assert.True(save.Accepted, "The shared file changed underneath this test's setup.");
    }

    private static async Task<CaptureReport> ProcessAsync(ServiceProvider provider)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        return await request.ServiceProvider.GetRequiredService<ICaptureProcessor>().ProcessAsync();
    }

    [Fact]
    public async Task Every_block_is_accounted_for_including_the_one_that_produced_no_tool_call()
    {
        var run = Nonce();

        // The note from the plan, verbatim in shape: two lines a tool covers and
        // one it does not. The third is the one that used to vanish.
        var provider = new ScriptedProvider(text =>
            text.Contains("detergent", StringComparison.Ordinal) ? Creates($"detergent {run}")
            : text.Contains("coffee", StringComparison.Ordinal) ? Creates($"coffee {run}")
            : SaysNothing("That is an event, and no tool is registered for events."));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"""
            ## Inbox
            - we're out of detergent {run}
            - and coffee {run}
            - dinner at Priya's parents on Saturday 8pm {run}
            """);

        var report = await ProcessAsync(services);

        // Three lines in, three blocks accounted for. The heading is a heading.
        Assert.Equal(3, report.Blocks.Count);
        Assert.Equal(3, report.BlocksSeen);
        Assert.Equal(0, report.AlreadyRecorded);

        // One completion per block, not one for the file.
        Assert.Equal(3, provider.Prompts.Count);

        Assert.Equal(2, report.With(DumpBlockStatus.Processed).Count());

        var ignored = Assert.Single(report.With(DumpBlockStatus.Ignored));
        Assert.Contains("dinner at Priya's parents", ignored.RawText);
        Assert.Contains("no tool is registered", ignored.Note);

        // The report is built from the input, so the user's own words are in it.
        Assert.All(report.Blocks, b => Assert.Contains(run, b.RawText));
    }

    [Fact]
    public async Task A_second_process_on_an_unchanged_file_creates_nothing()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(_ => Creates($"rice {run}"));
        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- we need rice {run}");

        var first = await ProcessAsync(services);
        Assert.Single(first.Blocks);

        var second = await ProcessAsync(services);

        // Nothing to do, and the numbers say why: the block is still there, it
        // has already been handled. "Saw 0" would be an empty file, which is a
        // different sentence to the user.
        Assert.Empty(second.Blocks);
        Assert.Equal(1, second.BlocksSeen);
        Assert.Equal(1, second.AlreadyRecorded);
        Assert.True(second.NothingToDo);

        // The model was asked once in total, not twice.
        Assert.Single(provider.Prompts);

        await using var request = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA);
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();

        await using var transaction = await unitOfWork.BeginAsync();

        // The point of all of it: one shopping item, not two.
        Assert.Equal(1, await db.ShoppingItems.CountAsync(i => i.Name == $"rice {run}"));
    }

    [Fact]
    public async Task A_block_that_fails_does_not_take_the_rest_of_the_run_with_it()
    {
        var run = Nonce();

        // The middle block asks for something the tool layer refuses. ADR 0004
        // makes that a result rather than an exception, and ARCHITECTURE.md 6
        // requires the blocks after it to run anyway.
        var provider = new ScriptedProvider(text =>
            text.Contains("second", StringComparison.Ordinal)
                ? new LlmCompletion(
                    [new LlmToolCall(
                        "create_shopping_item",
                        JsonDocument.Parse("""{"name":""}""").RootElement.Clone())],
                    Content: null,
                    Usage: Usage())
                : Creates($"{(text.Contains("first", StringComparison.Ordinal) ? "first" : "third")} {run}"));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"""
            - first item {run}
            - second item {run}
            - third item {run}
            """);

        var report = await ProcessAsync(services);

        Assert.Equal(3, report.Blocks.Count);
        Assert.Equal(2, report.With(DumpBlockStatus.Processed).Count());

        var failed = Assert.Single(report.With(DumpBlockStatus.Failed));
        Assert.Contains("second item", failed.RawText);

        await using var request = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA);
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();

        await using var transaction = await unitOfWork.BeginAsync();

        // Both survivors are in the database. Under one transaction per run, the
        // first would have been rolled back by the second's failure.
        Assert.Equal(1, await db.ShoppingItems.CountAsync(i => i.Name == $"first {run}"));
        Assert.Equal(1, await db.ShoppingItems.CountAsync(i => i.Name == $"third {run}"));
    }

    [Fact]
    public async Task The_run_records_its_counters_and_the_report_it_showed()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(text =>
            text.Contains("milk", StringComparison.Ordinal)
                ? Creates($"milk {run}")
                : SaysNothing("Nothing actionable here."));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"""
            - milk {run}
            - remember to breathe {run}
            """);

        var report = await ProcessAsync(services);

        await using var request = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA);
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();

        await using var transaction = await unitOfWork.BeginAsync();

        var row = await db.DumpRuns
            .OrderByDescending(r => r.StartedAt)
            .FirstAsync(r => r.CoupleId == RlsFixture.Couple1);

        Assert.Equal(2, row.BlocksSeen);

        // Ignored counts as processed: reading a line and deciding it needs no
        // action is work done, not work skipped.
        Assert.Equal(2, row.BlocksProcessed);
        Assert.Equal(0, row.BlocksFailed);
        Assert.Equal(1, row.EntitiesCreated);
        Assert.NotNull(row.FinishedAt);

        // The report survives the tab being closed, which is the whole reason it
        // is a column rather than only an htmx swap.
        Assert.NotNull(row.Report);
        Assert.Contains($"milk {run}", row.Report);

        // Stored as names, not as the ordinals a future reordering would silently
        // reinterpret.
        Assert.Contains("Ignored", row.Report);

        Assert.Equal(report.Blocks.Count, JsonDocument.Parse(row.Report)
            .RootElement.GetProperty("Blocks").GetArrayLength());

        var file = await db.DumpFiles.FirstAsync(f =>
            f.CoupleId == RlsFixture.Couple1 && f.Kind == DumpFileKind.Shared);

        Assert.NotNull(file.LastProcessedAt);
    }

    [Fact]
    public async Task A_processed_block_is_linked_to_the_row_it_produced()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(_ => Creates($"bread {run}"));
        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- bread {run}");

        var report = await ProcessAsync(services);
        var block = Assert.Single(report.Blocks);

        await using var request = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA);
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();

        await using var transaction = await unitOfWork.BeginAsync();

        var item = await db.ShoppingItems.SingleAsync(i => i.Name == $"bread {run}");

        // dump_block_entities is not mapped — nothing else reads it yet — so this
        // asks the database directly rather than teaching the model about a table
        // for the sake of one assertion.
        var linked = await db.Database
            .SqlQuery<Guid>($"""
                SELECT entity_id AS "Value" FROM dump_block_entities
                 WHERE block_id = {block.BlockId} AND action = 'created'
                """)
            .ToListAsync();

        Assert.Equal([item.Id], linked);
    }

    [Fact]
    public async Task A_run_picks_up_blocks_an_earlier_run_abandoned_and_still_counts_honestly()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(text =>
            Creates(text.Contains("orphan", StringComparison.Ordinal) ? $"orphan {run}" : $"fresh {run}"));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- orphan {run}");

        // A run that died between intake and processing: the block is in the
        // table, it belongs to a finished run, and nothing will ever call it new
        // again. Reading only intake's output would strand it permanently.
        await using (var request = BeginRequest(services, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            await request.ServiceProvider.GetRequiredService<ICaptureIntake>().IngestAsync();
        }

        // The line is then edited out of the file. Its block stays — the file is
        // the input, the rows are the state (ADR 0009).
        await WriteFileAsync(services, $"- fresh {run}");

        var report = await ProcessAsync(services);

        // Both: the orphan and the new line.
        Assert.Equal(2, report.Blocks.Count);

        // And the tally does not go negative doing it. "Seen minus handled" gave
        // -1 here, which rendered as nothing and read as a correct report.
        Assert.Equal(1, report.BlocksSeen);
        Assert.Equal(0, report.AlreadyRecorded);
        Assert.Contains(report.Blocks, b => b.RawText.Contains($"orphan {run}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_empty_file_is_reported_as_empty_rather_than_as_nothing_new()
    {
        var provider = new ScriptedProvider(_ => throw new InvalidOperationException(
            "The model must not be called for a file with no blocks in it."));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, string.Empty);

        var report = await ProcessAsync(services);

        Assert.True(report.FileIsEmpty);
        Assert.Equal(0, report.BlocksSeen);
        Assert.Empty(provider.Prompts);
    }
}
