using System.Text;
using CoupleOS.Application;
using CoupleOS.Application.Attachments;
using CoupleOS.Application.Persistence;
using CoupleOS.Application.Reading;
using CoupleOS.Application.Security;
using CoupleOS.Domain.Enums;
using CoupleOS.Infrastructure.DependencyInjection;
using CoupleOS.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CoupleOS.IntegrationTests;

/// <summary>
/// Attachments against the real database and a real directory.
///
/// The property that matters is the one the store cannot enforce: an attachment
/// inherits its surface's scope, and a partner's private file is not merely
/// hidden on a page but invisible to the query that would fetch it. So the
/// assertions run through <see cref="IAttachments"/> under two different couple
/// scopes rather than through anything that knows what it is looking for.
///
/// The bytes go to a temporary directory rather than a fake store. A fake would
/// let the checksum, the atomic move and the traversal guard all pass by
/// agreement — and the reason those exist at all is that a filesystem does not
/// agree with anybody (ADR 0014).
/// </summary>
public sealed class AttachmentTests : IClassFixture<RlsFixture>, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "coupleos-attachments-" + Guid.NewGuid().ToString("N")[..8]);

    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddCoupleOsInfrastructure(RlsFixture.AppConnectionString)
            .AddCoupleOsApplication()
            .AddSingleton(new AttachmentStorageOptions { Root = _root })
            .BuildServiceProvider();

    private static AsyncServiceScope Begin(ServiceProvider provider, Guid couple, Guid user)
    {
        var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ICoupleScopeSetter>().Set(new CoupleScope(couple, user));

        return scope;
    }

    private static Stream Bytes(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static async Task<AttachmentUpload> UploadAsync(
        ServiceProvider provider,
        Guid couple,
        Guid user,
        Visibility visibility,
        string filename,
        string content)
    {
        await using var request = Begin(provider, couple, user);

        return await request.ServiceProvider
            .GetRequiredService<IAttachmentIntake>()
            .ReceiveAsync(filename, "text/plain", Bytes(content), visibility, dumpFileId: null);
    }

    [Fact]
    public async Task An_attachment_inherits_the_scope_of_the_surface_that_received_it()
    {
        await using var provider = BuildProvider();

        var shared = await UploadAsync(
            provider, RlsFixture.Couple3, RlsFixture.PartnerD, Visibility.SharedCouple, "list.txt", "rice");

        var private_ = await UploadAsync(
            provider, RlsFixture.Couple3, RlsFixture.PartnerD, Visibility.PrivateUser, "quote.pdf", "surprise trip");

        Assert.True(shared.Accepted);
        Assert.True(private_.Accepted);

        // attachments_owner_required_when_private is the database's half of this,
        // and it would have refused the row — so what is asserted here is that the
        // application wrote the pair the way the constraint expects rather than
        // being told off by it.
        Assert.Null(shared.Attachment!.OwnerUserId);
        Assert.Equal(RlsFixture.PartnerD, private_.Attachment!.OwnerUserId);

        // The partner sees the shared one and not the private one — through the
        // same call, with no argument distinguishing them, because the
        // distinguishing is the database's.
        await using var asPartner = Begin(provider, RlsFixture.Couple3, RlsFixture.PartnerE);
        var unitOfWork = asPartner.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var attachments = asPartner.ServiceProvider.GetRequiredService<IAttachments>();

        await using var transaction = await unitOfWork.BeginAsync();

        Assert.NotNull(await attachments.FindAsync(shared.Attachment.Id));
        Assert.Null(await attachments.FindAsync(private_.Attachment.Id));

        await transaction.CommitAsync();
    }

    [Fact]
    public async Task The_bytes_are_stored_and_come_back_byte_for_byte()
    {
        const string content = "AC service receipt, 2500";

        await using var provider = BuildProvider();

        var upload = await UploadAsync(
            provider, RlsFixture.Couple3, RlsFixture.PartnerD, Visibility.SharedCouple, "ac.txt", content);

        var store = provider.GetRequiredService<IAttachmentStore>();

        await using var read = await store.OpenAsync(upload.Attachment!.StorageKey);
        Assert.NotNull(read);

        using var reader = new StreamReader(read!);
        Assert.Equal(content, await reader.ReadToEndAsync());

        // Recorded from what arrived, not from what is on disk — see ADR 0014 for
        // why the difference is the whole content of a corrupt upload.
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)),
            upload.Attachment.Checksum);

        Assert.Equal(content.Length, upload.Attachment.ByteSize);

        // Namespaced by couple, and never the filename: a user-supplied name in a
        // path is a traversal waiting to happen.
        Assert.StartsWith($"{RlsFixture.Couple3:D}/", upload.Attachment.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain("ac.txt", upload.Attachment.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_storage_key_that_climbs_out_of_the_root_is_refused()
    {
        // No key this code issues can do it. The guard is for a key arriving from
        // somewhere else — a hand-edited row, a future import — and it is the kind
        // of check whose absence is noticed exactly once.
        await using var provider = BuildProvider();
        var store = provider.GetRequiredService<IAttachmentStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.OpenAsync("../../etc/passwd"));
    }

    [Fact]
    public async Task A_file_with_no_name_is_refused_and_nothing_is_written()
    {
        await using var provider = BuildProvider();

        var upload = await UploadAsync(
            provider, RlsFixture.Couple3, RlsFixture.PartnerD, Visibility.SharedCouple, "   ", "anything");

        Assert.False(upload.Accepted);
        Assert.NotNull(upload.Refusal);

        // Refused before the bytes were taken, so the directory has nothing in it
        // for this couple that a later reconciliation would have to explain.
        Assert.False(Directory.Exists(Path.Combine(_root, RlsFixture.Couple3.ToString("D"))));
    }

    [Fact]
    public async Task An_attachment_named_in_a_block_reaches_the_records_that_block_produced()
    {
        // The M5 criterion, end to end: upload, put the link in a line, process
        // the line, and find the attachment hanging off the row it produced.
        await using var provider = BuildProvider();

        var upload = await UploadAsync(
            provider, RlsFixture.Couple3, RlsFixture.PartnerD, Visibility.SharedCouple, "ac.jpg", "receipt bytes");

        var expenseId = Guid.CreateVersion7();

        await using (var request = Begin(provider, RlsFixture.Couple3, RlsFixture.PartnerD))
        {
            var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
            var attachments = request.ServiceProvider.GetRequiredService<IAttachments>();

            await using var transaction = await unitOfWork.BeginAsync();

            var referenced = AttachmentReference.In(
                $"AC service 2500 {AttachmentReference.Markdown(upload.Attachment!.Id, "ac.jpg")}");

            Assert.Equal([upload.Attachment.Id], referenced);

            await attachments.LinkAsync(referenced[0], [new AttachmentTarget("expense", expenseId)]);

            // Twice, because a block edited to answer a question re-runs every
            // tool the first pass ran (debt 31) — and attachment_links has a
            // composite primary key, so a second insert that threw would take the
            // whole run's transaction with it.
            await attachments.LinkAsync(referenced[0], [new AttachmentTarget("expense", expenseId)]);

            var found = await attachments.ForAsync("expense", [expenseId]);

            Assert.Equal(upload.Attachment.Id, Assert.Single(found).Id);

            await transaction.CommitAsync();
        }
    }

    [Fact]
    public async Task The_read_surface_shows_what_this_caller_may_see_and_says_nothing_it_may_not()
    {
        await using var provider = BuildProvider();

        await using var request = Begin(provider, RlsFixture.Couple3, RlsFixture.PartnerE);
        var unitOfWork = request.ServiceProvider.GetRequiredService<IScopedUnitOfWork>();
        var records = request.ServiceProvider.GetRequiredService<ICapturedRecords>();

        await using var transaction = await unitOfWork.BeginAsync();

        var view = await records.ReadAsync(50);

        await transaction.CommitAsync();

        // Whatever else the suite has written into this couple, nothing on the
        // page belongs to the other partner privately. Asserted as a property of
        // every row rather than as a count, so it holds however many rows other
        // tests happen to have left (STATUS debt 28).
        Assert.All(view.Memories, m =>
            Assert.True(
                m.Visibility != Visibility.PrivateUser || m.OwnerUserId == RlsFixture.PartnerE,
                $"A memory owned by {m.OwnerUserId} reached {RlsFixture.PartnerE}'s read surface."));

        // Superseded rows are history, not knowledge: a correction that retired
        // "likes Italian food" must not leave it under a heading reading "what we
        // know" (§45).
        Assert.All(view.Memories, m => Assert.Equal(MemoryStatus.Active, m.Status));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
