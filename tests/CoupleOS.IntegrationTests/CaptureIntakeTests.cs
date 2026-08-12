using CoupleOS.Application;
using CoupleOS.Application.Capture;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Intake against a real database: a file appears, its lines become blocks, and
/// running it again changes nothing.
///
/// The last of those is M2's exit criterion and the reason the dedup index
/// exists. It is asserted through the same path a user takes rather than by
/// inserting two identical rows, because the interesting failure is not "the
/// index is missing" — it is "segmentation produced a different hash for text
/// nobody changed", which an index test would pass and a user would experience
/// as their shopping list doubling.
/// </summary>
public sealed class CaptureIntakeTests : IClassFixture<RlsFixture>
{
    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsApplication()
            .BuildServiceProvider();

    private static AsyncServiceScope BeginRequest(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));
        return scope;
    }

    /// <summary>
    /// A nonce per test. The shared file is one row per couple and outlives the
    /// run, so a fixed note would be "already recorded" the second time the suite
    /// executes and every assertion about new blocks would invert.
    /// </summary>
    private static string Nonce() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// Writes the file's content the way the editor will, in its own request and
    /// its own transaction, so what intake reads afterwards has genuinely been
    /// through the database rather than through a tracked entity.
    /// </summary>
    private static async Task SetContentAsync(ServiceProvider provider, Guid couple, Guid user, string content)
    {
        await using var request = BeginRequest(provider, couple, user);

        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var files = request.ServiceProvider.GetRequiredService<IDumpFileStore>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var file = await files.GetOrCreateSharedAsync();
        file.Content = content;
        await db.SaveChangesAsync();

        await transaction.CommitAsync();
    }

    private static async Task<IntakeResult> IngestAsync(ServiceProvider provider, Guid couple, Guid user)
    {
        await using var request = BeginRequest(provider, couple, user);
        return await request.ServiceProvider.GetRequiredService<ICaptureIntake>().IngestAsync();
    }

    [Fact]
    public async Task The_shared_file_is_created_once_and_belongs_to_one_couple()
    {
        await using var provider = BuildProvider();

        Guid first, second, other;

        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var files = request.ServiceProvider.GetRequiredService<IDumpFileStore>();

            await using var transaction = await unitOfWork.BeginAsync();
            var file = await files.GetOrCreateSharedAsync();

            first = file.Id;
            Assert.Equal(DumpFileKind.Shared, file.Kind);
            Assert.Equal(Visibility.SharedCouple, file.Visibility);

            // A shared file has no owner. An owner here would make the row look
            // private to any code reading owner_user_id without visibility.
            Assert.Null(file.OwnerUserId);
            Assert.Equal("shared.md", file.Title);

            await transaction.CommitAsync();
        }

        // The partner, in a separate request, must land on the same file — this
        // is the surface they share, and two of them would be two inboxes.
        await using (var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerB))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync();
            second = (await request.ServiceProvider.GetRequiredService<IDumpFileStore>()
                .GetOrCreateSharedAsync()).Id;
            await transaction.CommitAsync();
        }

        await using (var request = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            await using var transaction = await unitOfWork.BeginAsync();
            other = (await request.ServiceProvider.GetRequiredService<IDumpFileStore>()
                .GetOrCreateSharedAsync()).Id;
            await transaction.CommitAsync();
        }

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public async Task Every_line_becomes_a_block_carrying_the_files_scope()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(
            provider,
            RlsFixture.Couple1,
            RlsFixture.PartnerA,
            $"## Inbox\n- detergent {nonce}\n- coffee {nonce}");

        var result = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.Equal(2, result.NewBlocks.Count);
        Assert.Equal(2, result.BlocksSeen);
        Assert.Equal(0, result.AlreadyRecorded);

        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var stored = await db.DumpBlocks
            .Where(b => b.RunId == result.RunId)
            .OrderBy(b => b.LineStart)
            .ToListAsync();

        Assert.Equal([$"- detergent {nonce}", $"- coffee {nonce}"], stored.Select(b => b.RawText));

        foreach (var block in stored)
        {
            // Inherited from the file, never read out of the text (ADR 0009).
            Assert.Equal(Visibility.SharedCouple, block.Visibility);
            Assert.Null(block.OwnerUserId);

            Assert.Equal(RlsFixture.Couple1, block.CoupleId);
            Assert.Equal(result.DumpFileId, block.DumpFileId);

            // Nothing has run yet, and the status says so rather than implying
            // the block was seen and dismissed.
            Assert.Equal(DumpBlockStatus.Unprocessed, block.Status);
        }

        Assert.Equal(2, stored[0].LineStart);
        Assert.Equal(3, stored[1].LineStart);
    }

    [Fact]
    public async Task A_second_run_over_unchanged_content_records_nothing()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, $"- rice {nonce}");

        var first = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var second = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.Single(first.NewBlocks);

        Assert.Empty(second.NewBlocks);
        Assert.Equal(1, second.BlocksSeen);
        Assert.Equal(1, second.AlreadyRecorded);

        // Seen and recorded are different numbers on purpose: a run that saw one
        // block and recorded none is a re-run, and a run that saw none is an
        // empty file. A single count could not tell a user which happened.
        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        Assert.Single(await db.DumpBlocks.Where(b => b.RawText == $"- rice {nonce}").ToListAsync());
    }

    [Fact]
    public async Task An_added_line_records_only_the_new_block()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, $"- rice {nonce}");
        await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        await SetContentAsync(
            provider,
            RlsFixture.Couple1,
            RlsFixture.PartnerA,
            $"- rice {nonce}\n- lentils {nonce}");

        var second = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        var added = Assert.Single(second.NewBlocks);
        Assert.Equal($"- lentils {nonce}", added.RawText);
        Assert.Equal(2, second.BlocksSeen);
    }

    [Fact]
    public async Task One_thought_written_twice_in_a_file_is_one_block()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(
            provider,
            RlsFixture.Couple1,
            RlsFixture.PartnerA,

            // The second line differs only in marker and spacing, which is what
            // a person does when they forget they already wrote it.
            $"- milk {nonce}\n*  milk   {nonce}");

        var result = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        Assert.Single(result.NewBlocks);
        Assert.Equal(2, result.BlocksSeen);
        Assert.Equal(1, result.AlreadyRecorded);
    }

    [Fact]
    public async Task The_run_records_who_pressed_the_button_and_what_it_saw()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerB, $"- soap {nonce}");

        var before = DateTimeOffset.UtcNow.AddMinutes(-1);
        var result = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerB);

        await using var request = BeginRequest(provider, RlsFixture.Couple1, RlsFixture.PartnerA);
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = request.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        var run = await db.DumpRuns.SingleAsync(r => r.Id == result.RunId);

        Assert.Equal(RlsFixture.PartnerB, run.TriggeredBy);
        Assert.Equal(result.DumpFileId, run.DumpFileId);
        Assert.Equal(1, run.BlocksSeen);

        // started_at is written by the application, not by DEFAULT now(). If it
        // were left to EF's default the value would be year 1 and every run would
        // sort before every other run forever.
        Assert.True(run.StartedAt > before, $"started_at was {run.StartedAt:o}");

        // A run with no finish time is a run that crashed. This one did not.
        Assert.NotNull(run.FinishedAt);

        // Partner A reading Partner B's run is correct: the shared file is shared,
        // and dump_runs is scoped by couple alone.
        Assert.Equal(RlsFixture.Couple1, run.CoupleId);
    }

    [Fact]
    public async Task What_one_couple_captures_the_other_cannot_see()
    {
        var nonce = Nonce();

        await using var provider = BuildProvider();
        await SetContentAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA, $"- surprise {nonce}");
        var result = await IngestAsync(provider, RlsFixture.Couple1, RlsFixture.PartnerA);

        await using var stranger = BeginRequest(provider, RlsFixture.Couple2, RlsFixture.StrangerC);
        var unitOfWork = stranger.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var db = stranger.ServiceProvider.GetRequiredService<CoupleOsDbContext>();

        await using var transaction = await unitOfWork.BeginAsync();

        Assert.Empty(await db.DumpBlocks.Where(b => b.RawText == $"- surprise {nonce}").ToListAsync());
        Assert.Empty(await db.DumpRuns.Where(r => r.Id == result.RunId).ToListAsync());
        Assert.Empty(await db.DumpFiles.Where(f => f.Id == result.DumpFileId).ToListAsync());
    }
}
