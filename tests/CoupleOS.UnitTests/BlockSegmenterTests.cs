using CoupleOS.Application.Capture;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// Segmentation decides what the system will and will not read. Every silent
/// failure M2 exists to remove — a line ignored, a thought counted twice, a
/// processed item re-executed — is a segmentation bug before it is anything else,
/// so these are the assertions that hold the rest of the milestone up.
/// </summary>
public sealed class BlockSegmenterTests
{
    private static IReadOnlyList<string> TextOf(string content) =>
        [.. BlockSegmenter.Segment(content).Select(b => b.RawText)];

    [Fact]
    public void Each_line_is_its_own_block()
    {
        // The two-line note from the M0 page. Paragraph segmentation would fuse
        // these into one block, and one of the two would never be acted on.
        var blocks = TextOf("we're out of detergent and coffee\ndinner at Priya's parents on Saturday 8pm");

        Assert.Equal(
            ["we're out of detergent and coffee", "dinner at Priya's parents on Saturday 8pm"],
            blocks);
    }

    [Fact]
    public void List_items_are_blocks_and_keep_their_markers_in_the_raw_text()
    {
        var blocks = TextOf("- got the AC serviced, 2500\n* call the plumber\n1. renew insurance");

        Assert.Equal(
            ["- got the AC serviced, 2500", "* call the plumber", "1. renew insurance"],
            blocks);
    }

    [Fact]
    public void An_indented_line_continues_the_block_above_it()
    {
        var blocks = BlockSegmenter.Segment("- book the hotel\n  the one near the station\n- pack");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("- book the hotel\n  the one near the station", blocks[0].RawText);
        Assert.Equal(1, blocks[0].LineStart);
        Assert.Equal(2, blocks[0].LineEnd);
    }

    [Fact]
    public void Blank_lines_and_thematic_breaks_produce_no_blocks()
    {
        var blocks = TextOf("milk\n\n\n---\n\nbread");

        Assert.Equal(["milk", "bread"], blocks);
    }

    [Fact]
    public void Line_numbers_are_one_based_and_span_the_whole_file()
    {
        var blocks = BlockSegmenter.Segment("## Inbox\n\n- milk\n- bread");

        Assert.Equal(3, blocks[0].LineStart);
        Assert.Equal(3, blocks[0].LineEnd);
        Assert.Equal(4, blocks[1].LineStart);
    }

    [Fact]
    public void Headings_are_structure_and_never_content()
    {
        var blocks = TextOf("# shared.md\n## Inbox\n- milk");

        Assert.Equal(["- milk"], blocks);
    }

    [Fact]
    public void The_sections_a_run_writes_are_never_read_back()
    {
        // Without this, every Process would re-execute everything the previous
        // one did. The content hash would catch it — but relying on the dedup
        // index to undo a segmentation mistake means the archive grows into the
        // prompt of every future run.
        var file = """
            ## Inbox
            - milk

            ## Needs your input
            - "dinner was 2400" — who paid? Answer by adding a line below.

            ## Processed — 11 Aug
            - ~~we're out of detergent~~ → shopping: detergent
            """;

        Assert.Equal(["- milk"], TextOf(file));
    }

    [Fact]
    public void A_processed_heading_stops_being_output_at_the_next_heading()
    {
        var file = "## Processed — 11 Aug\n- ~~old~~\n\n## Inbox\n- milk";

        Assert.Equal(["- milk"], TextOf(file));
    }

    [Fact]
    public void Content_under_an_unrecognised_heading_is_read()
    {
        // The safe default, and the opposite of what "only ## Inbox is input"
        // would do. A user who renames the heading, or writes above it, must not
        // have their note silently ignored — that is the exact defect M2 exists
        // to remove, and it would be reintroduced by the stricter rule.
        var file = "## Groceries\n- milk\n\n## Inbox\n- bread";

        Assert.Equal(["- milk", "- bread"], TextOf(file));
    }

    [Fact]
    public void A_file_with_no_headings_at_all_is_entirely_input()
    {
        // Quick-add appends a line to a file that may never have been processed
        // or rewritten, so this is the ordinary case rather than the edge one.
        Assert.Equal(["milk"], TextOf("milk"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n\t\n")]
    [InlineData("## Inbox\n\n## Processed — 11 Aug\n- ~~old~~")]
    public void Nothing_to_read_produces_no_blocks(string? content) =>
        Assert.Empty(BlockSegmenter.Segment(content));

    [Fact]
    public void The_hash_ignores_markers_and_whitespace()
    {
        // The run rewrites the file — re-indenting and re-bulleting as it goes.
        // If that churn changed the hash, every rewrite would make every block
        // look new and the second Process would duplicate the first.
        var a = BlockSegmenter.Segment("- buy   detergent")[0].ContentHash;
        var b = BlockSegmenter.Segment("  *  buy detergent  ")[0].ContentHash;

        Assert.Equal(a, b);
    }

    [Fact]
    public void The_hash_distinguishes_different_wording()
    {
        var a = BlockSegmenter.Segment("buy detergent")[0].ContentHash;
        var b = BlockSegmenter.Segment("buy Detergent")[0].ContentHash;

        // Not the same block. Merging on case would silently discard whichever
        // wording arrived second, and the report would have nothing to show for
        // a line the user genuinely wrote.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void The_hash_is_sha256_of_the_normalized_text()
    {
        // Stated as an exact value, because the dedup guarantee is a promise
        // about this specific function. A change to Normalize that quietly
        // changed every hash would orphan every block already recorded.
        var expected = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("buy detergent"));

        Assert.Equal(expected, BlockSegmenter.Segment("- buy detergent")[0].ContentHash);
    }

    [Fact]
    public void Carriage_returns_do_not_change_a_block()
    {
        // A browser posts \r\n. A file written by this code joins with \n. The
        // same text through the two paths has to be one block.
        var windows = BlockSegmenter.Segment("- milk\r\n- bread")[0].ContentHash;
        var unix = BlockSegmenter.Segment("- milk\n- bread")[0].ContentHash;

        Assert.Equal(unix, windows);
    }
}
