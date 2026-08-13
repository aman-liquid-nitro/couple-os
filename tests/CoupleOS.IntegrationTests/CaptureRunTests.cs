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

    private static async Task<SharedFileView> ReadFileAsync(ServiceProvider provider)
    {
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        return await request.ServiceProvider.GetRequiredService<ISharedFileEditor>().ReadAsync();
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

        // Nothing to do, and now for two reasons rather than one: the block is
        // recorded, *and* the first run moved its line out of the inbox, so the
        // second run has nothing to read at all. Before the rewrite this said
        // "saw 1, already recorded 1" — the dedup index carrying the guarantee on
        // its own. It still does, underneath; the file no longer makes it work.
        Assert.Empty(second.Blocks);
        Assert.Equal(0, second.BlocksSeen);
        Assert.True(second.NothingToRead);
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

        Assert.True(report.NothingToRead);
        Assert.Equal(0, report.BlocksSeen);
        Assert.Empty(provider.Prompts);

        // And nothing was written back. A run with nothing to move must not bump
        // the version, or every open editor goes stale for a file that did not
        // change and both partners get a conflict warning about nobody.
        Assert.Equal(FileRewriteOutcome.NothingToMove, report.Rewrite);
    }

    [Fact]
    public async Task The_run_files_what_it_settled_and_leaves_the_rest_in_the_inbox()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(text =>
            text.Contains("detergent", StringComparison.Ordinal) ? Creates($"detergent {run}")
            : text.Contains("breathe", StringComparison.Ordinal) ? SaysNothing("A note to self.")

            // An empty name: the tool layer refuses it, so this block fails.
            : new LlmCompletion(
                [new LlmToolCall(
                    "create_shopping_item",
                    JsonDocument.Parse("""{"name":""}""").RootElement.Clone())],
                Content: null,
                Usage: Usage()));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"""
            ## Inbox
            - we're out of detergent {run}
            - remember to breathe {run}
            - the one that fails {run}
            """);

        var report = await ProcessAsync(services);

        Assert.Equal(FileRewriteOutcome.Rewritten, report.Rewrite);

        var file = await ReadFileAsync(services);

        // Settled, so filed — and the ignored one carries its own words so that
        // moving it cannot be mistaken for having acted on it.
        Assert.Contains($"~~we're out of detergent {run}~~", file.Content, StringComparison.Ordinal);
        Assert.Contains($"~~remember to breathe {run}~~ → read, nothing to do", file.Content, StringComparison.Ordinal);

        // Not settled, so still outstanding, and still where the user will see it.
        // Nothing retries a failed block (STATUS debt 24), so the line staying put
        // is the only way back to it.
        Assert.Contains($"- the one that fails {run}", file.Content, StringComparison.Ordinal);
        Assert.Contains("## Inbox", file.Content, StringComparison.Ordinal);

        // The user's heading survived, and the archive went below it.
        Assert.True(
            file.Content.IndexOf("## Inbox", StringComparison.Ordinal)
                < file.Content.IndexOf("## Processed", StringComparison.Ordinal),
            "The inbox has to stay above the archive, or it scrolls off a phone.");

        // And the file the run wrote is a file the next run reads correctly: only
        // the failed line is still input.
        var remaining = BlockSegmenter.Segment(file.Content).Select(b => b.RawText).ToList();
        Assert.Equal([$"- the one that fails {run}"], remaining);
    }

    [Fact]
    public async Task A_line_that_failed_earlier_is_never_called_processed()
    {
        var run = Nonce();

        // Refused by the tool layer, so the block fails and its line stays in the
        // inbox — which after the rewrite means the inbox is nothing but that line.
        var provider = new ScriptedProvider(_ => new LlmCompletion(
            [new LlmToolCall(
                "create_shopping_item",
                JsonDocument.Parse("""{"name":""}""").RootElement.Clone())],
            Content: null,
            Usage: Usage()));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- the one that fails {run}");

        var first = await ProcessAsync(services);
        Assert.Single(first.With(DumpBlockStatus.Failed));

        // Nothing was settled, so nothing moved and the version did not budge.
        Assert.Equal(FileRewriteOutcome.NothingToMove, first.Rewrite);

        var second = await ProcessAsync(services);

        // PendingAsync reads unprocessed rows only, so the failure is invisible to
        // it and this run has nothing to do. What it must not do is call the line
        // processed: it is still sitting in the file, and it was not.
        Assert.True(second.NothingToDo);
        Assert.Equal(1, second.BlocksSeen);
        Assert.Equal(1, second.Stranded);

        // Asked once in total. The second run reaches no model, which is why the
        // user gets no new information unless the report volunteers it.
        Assert.Single(provider.Prompts);
    }

    [Fact]
    public async Task A_failed_line_edited_out_of_the_file_stops_being_counted()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(text => text.Contains("fails", StringComparison.Ordinal)
            ? new LlmCompletion(
                [new LlmToolCall(
                    "create_shopping_item",
                    JsonDocument.Parse("""{"name":""}""").RootElement.Clone())],
                Content: null,
                Usage: Usage())
            : Creates($"quinoa {run}"));

        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- the one that fails {run}");
        Assert.Single((await ProcessAsync(services)).With(DumpBlockStatus.Failed));

        // The user gives up on that line and deletes it. Its block stays in the
        // table forever (ADR 0009), so a count taken from the table alone would go
        // on warning about a line nobody can find and nobody can edit.
        await WriteFileAsync(services, $"- quinoa {run}");

        var report = await ProcessAsync(services);

        Assert.Equal(0, report.Stranded);
        Assert.Single(report.With(DumpBlockStatus.Processed));
    }

    [Fact]
    public async Task A_rewrite_bumps_the_version_the_editor_has_to_carry()
    {
        var run = Nonce();

        var provider = new ScriptedProvider(_ => Creates($"olives {run}"));
        await using var services = BuildProvider(provider);

        await WriteFileAsync(services, $"- olives {run}");

        var before = await ReadFileAsync(services);
        await ProcessAsync(services);
        var after = await ReadFileAsync(services);

        // The page has to be told, which is why the Process response swaps both
        // the textarea and the version out of band. A page left holding `before`
        // would, on its next Save, write the pre-run inbox back over the archive
        // and un-file everything the run just filed.
        Assert.Equal(before.Version + 1, after.Version);
        Assert.NotEqual(before.Content, after.Content);
    }
}
