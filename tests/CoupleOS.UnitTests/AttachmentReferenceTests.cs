using CoupleOS.Application.AI;
using CoupleOS.Application.Attachments;
using CoupleOS.Application.Capture;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.BlockProcessorFakes;

namespace CoupleOS.UnitTests;

/// <summary>
/// The format an upload writes and a run reads back.
///
/// Reader and writer of one format need one implementation, not two agreeing
/// ones — the rule <c>BlockSegmenter.RoleOf</c> is public for. The failure here
/// would be quiet in the same way: a receipt that uploads fine, appears in the
/// file, and is linked to nothing.
/// </summary>
public sealed class AttachmentReferenceTests
{
    private static readonly Guid Receipt = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");

    [Fact]
    public void What_the_upload_writes_is_what_the_run_reads_back()
    {
        var markdown = AttachmentReference.Markdown(Receipt, "ac-service.jpg");

        Assert.Equal($"[ac-service.jpg](/attachments/{Receipt})", markdown);
        Assert.Equal([Receipt], AttachmentReference.In($"AC service 2500 {markdown}"));
    }

    [Fact]
    public void A_uuid_the_couple_merely_wrote_down_is_not_a_link()
    {
        // The pattern matches the path this class writes and only that. A loose
        // one would pick an id out of any parenthesis in the couple's prose and
        // link a receipt to a line that never mentioned one.
        Assert.Empty(AttachmentReference.In($"the reference is ({Receipt})"));
        Assert.Empty(AttachmentReference.In($"see /attachments/{Receipt} for the receipt"));
        Assert.Empty(AttachmentReference.In("[receipt](/attachments/not-a-uuid)"));
    }

    [Fact]
    public void The_same_file_twice_in_one_block_is_one_link()
    {
        // attachment_links has a composite primary key, so a repeat is a
        // constraint violation halfway through a run rather than a second row.
        var text = $"{AttachmentReference.Markdown(Receipt, "a.jpg")} and again " +
                   $"{AttachmentReference.Markdown(Receipt, "a.jpg")}";

        Assert.Equal([Receipt], AttachmentReference.In(text));
    }

    [Fact]
    public void A_filename_carrying_markdown_delimiters_still_produces_a_link()
    {
        var markdown = AttachmentReference.Markdown(Receipt, "invoice [final].pdf");

        Assert.Equal([Receipt], AttachmentReference.In(markdown));
    }

    [Fact]
    public async Task An_attachment_in_a_block_is_linked_to_every_record_that_block_produced()
    {
        // V0_SCOPE.md: linked to "whatever records the surrounding block
        // produced". Every one of them, rather than a guess at which receipt goes
        // with which row — a block is one thought, and picking would be the system
        // inferring meaning from a sentence.
        var attachments = new RecordingAttachments();

        var processor = new BlockProcessor(
            new StubProvider(new LlmCompletion(
                [Call("create_expense"), Call("create_task")],
                Content: null,
                Usage())),
            new EmptyRegistry(),
            new CapturingDispatcher(),
            new RecordingBlockStore(),
            attachments,
            new CountingUnitOfWork(),
            new FixedScope(),
            TimeProvider.System);

        await processor.ProcessAsync(
            Block($"AC service 2500 {AttachmentReference.Markdown(Receipt, "ac-service.jpg")}"));

        Assert.Equal(2, attachments.Links.Count);
        Assert.All(attachments.Links, l => Assert.Equal(Receipt, l.Attachment));
        Assert.All(attachments.Links, l => Assert.Equal("shopping_item", l.EntityType));
    }

    [Fact]
    public async Task A_block_that_produced_nothing_links_nothing()
    {
        // A link needs both ends. An attachment mentioned in a line no tool
        // covered has nothing to attach to, and writing a row with a null entity
        // would be a link to the idea of a record.
        var attachments = new RecordingAttachments();

        var processor = new BlockProcessor(
            new StubProvider(new LlmCompletion([], "Nothing to do here.", Usage())),
            new EmptyRegistry(),
            new CapturingDispatcher(),
            new RecordingBlockStore(),
            attachments,
            new CountingUnitOfWork(),
            new FixedScope(),
            TimeProvider.System);

        await processor.ProcessAsync(
            Block($"just a photo {AttachmentReference.Markdown(Receipt, "a.jpg")}", Visibility.SharedCouple));

        Assert.Empty(attachments.Links);
    }
}
