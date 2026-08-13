using CoupleOS.Application.Capture;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// Quick-add has exactly one way to fail, and it fails silently: put the line
/// somewhere a run will not read, and the user watches their words go into a box
/// and never hears about them again.
///
/// So most of these assert placement, and two of them assert it the only way that
/// really counts — by segmenting the result and checking the line came back out.
/// </summary>
public sealed class QuickAddTests
{
    private static IReadOnlyList<string> ReadableIn(string content) =>
        [.. BlockSegmenter.Segment(content).Select(b => b.RawText)];

    [Fact]
    public void A_line_added_to_an_empty_file_is_the_file()
    {
        Assert.Equal("- milk\n", QuickAdd.Append(string.Empty, "milk"));
        Assert.Equal("- milk\n", QuickAdd.Append(null, "milk"));
    }

    [Fact]
    public void A_line_lands_under_an_inbox_heading_when_there_is_one()
    {
        var file = QuickAdd.Append(
            """
            ## Inbox
            - bread

            ## Notes
            some prose
            """,
            "milk");

        Assert.Equal(
            """
            ## Inbox
            - bread
            - milk

            ## Notes
            some prose
            """,
            file);
    }

    [Fact]
    public void An_empty_inbox_section_gets_the_line_directly_under_its_heading()
    {
        var file = QuickAdd.Append("## Inbox\n\n## Processed — 13 Aug 2026\n- ~~old~~ → done", "milk");

        Assert.Equal(
            """
            ## Inbox
            - milk

            ## Processed — 13 Aug 2026
            - ~~old~~ → done
            """,
            file);
    }

    [Fact]
    public void A_line_never_lands_in_the_archive()
    {
        // The defect this class exists to prevent. Appending at the end of the
        // file is the obvious implementation, and after one run the end of the
        // file is inside a section the segmenter refuses to read — so the line
        // would be stored, displayed, and never processed.
        var file = QuickAdd.Append(
            """
            - bread

            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent
            """,
            "milk");

        Assert.Equal(["- bread", "- milk"], ReadableIn(file!));
    }

    [Fact]
    public void A_file_that_is_nothing_but_archive_gets_the_line_above_it()
    {
        // The ordinary state of a couple who are up to date, and the case with no
        // input region to append to at all.
        var file = QuickAdd.Append("## Processed — 13 Aug 2026\n- ~~detergent~~ → shopping: detergent", "milk");

        Assert.Equal(
            """
            - milk
            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent
            """,
            file);

        Assert.Equal(["- milk"], ReadableIn(file!));
    }

    [Fact]
    public void A_line_goes_after_the_last_input_line_when_there_is_no_inbox_heading()
    {
        var file = QuickAdd.Append("## Groceries\n- bread\n\n## Needs your input\n- \"x\" — who?", "milk");

        Assert.Equal(
            """
            ## Groceries
            - bread
            - milk

            ## Needs your input
            - "x" — who?
            """,
            file);
    }

    [Fact]
    public void Trailing_blank_lines_are_not_pushed_further_down()
    {
        // Otherwise a file quick-added to five times has five orphaned gaps in it.
        Assert.Equal("- bread\n- milk\n\n\n", QuickAdd.Append("- bread\n\n\n", "milk"));
    }

    [Fact]
    public void The_couples_own_spacing_comes_back_byte_for_byte()
    {
        // A splice, not a re-render. FileRewriter has earned the right to lay the
        // file out; an append has not, and someone mid-sentence should not find
        // their draft reflowed because their partner added milk.
        const string original = "# our week\n\n\n   indented note\n\n## Groceries\n- bread\n";

        // Stated in full rather than derived from the input by a Replace, because a
        // clever expected value can agree with a wrong implementation.
        Assert.Equal(
            "# our week\n\n\n   indented note\n\n## Groceries\n- bread\n- milk\n",
            QuickAdd.Append(original, "milk"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t \r\n")]
    public void Nothing_worth_adding_returns_null_rather_than_writing(string? line) =>
        Assert.Null(QuickAdd.Append("- bread", line));

    [Fact]
    public void A_pasted_multi_line_value_becomes_one_entry()
    {
        // The box is one line by construction and paste exists. Two lines would be
        // two blocks, and the second — un-bulleted, possibly indented — could be
        // read as a continuation of the first instead of a thought of its own.
        var file = QuickAdd.Append("- bread", "milk\n  and coffee\n\ndetergent");

        Assert.Equal("- bread\n- milk and coffee detergent", file);
        Assert.Equal(2, BlockSegmenter.Segment(file!).Count);
    }

    [Fact]
    public void A_quick_added_line_is_the_same_block_as_the_same_words_typed_in_the_editor()
    {
        // The entry is written as a list item, and Normalize strips list markers,
        // so the hashes match and the dedup index treats them as one thought.
        // Without this, adding "milk" then typing "milk" would create two.
        var added = BlockSegmenter.Segment(QuickAdd.Append(string.Empty, "milk"))[0].ContentHash;
        var typed = BlockSegmenter.Segment("milk")[0].ContentHash;

        Assert.Equal(typed, added);
    }

    [Fact]
    public void Carriage_returns_from_a_browser_do_not_survive_into_the_file()
    {
        var file = QuickAdd.Append("- bread\r\n\r\n## Processed — 13 Aug 2026\r\n- ~~old~~ → done", "milk");

        Assert.DoesNotContain('\r', file!);
    }
}
