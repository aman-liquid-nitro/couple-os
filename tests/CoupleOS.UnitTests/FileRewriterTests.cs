using System.Security.Cryptography;
using System.Text;
using CoupleOS.Application.Capture;
using CoupleOS.Domain.Enums;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// The rewrite is the only thing in the system that edits the user's own words,
/// so these tests are mostly about what it leaves alone.
///
/// Its output is also its own next input: the file a run writes is the file the
/// next run segments. A rewrite that produced something the segmenter reads back
/// as input would put every archived line through a model call again on every
/// press of Process, and the dedup index would hide it by making the second run
/// look merely slow. Two of the tests below close that loop explicitly.
/// </summary>
public sealed class FileRewriterTests
{
    private static readonly DateOnly Today = new(2026, 8, 13);

    private static BlockOutcome Outcome(string text, DumpBlockStatus status, string? detail = null) =>
        new(
            SHA256.HashData(Encoding.UTF8.GetBytes(BlockSegmenter.Normalize(text))),
            status,
            detail);

    private static BlockOutcome Done(string text, string detail) =>
        Outcome(text, DumpBlockStatus.Processed, detail);

    [Fact]
    public void A_processed_block_moves_into_a_dated_archive_section()
    {
        var file = FileRewriter.Rewrite(
            "- we're out of detergent\n- and milk",
            [Done("we're out of detergent", "create shopping item: detergent")],
            Today);

        Assert.Equal(
            """
            - and milk

            ## Processed — 13 Aug 2026
            - ~~we're out of detergent~~ → create shopping item: detergent

            """,
            file);
    }

    [Fact]
    public void What_the_run_wrote_is_not_read_back_as_input()
    {
        // The loop that matters. If this ever fails, every archived line goes
        // through a model call on every future run and only the dedup index stops
        // it becoming duplicate rows.
        var file = FileRewriter.Rewrite(
            "- we're out of detergent\n- and milk",
            [Done("we're out of detergent", "create shopping item: detergent")],
            Today);

        var remaining = BlockSegmenter.Segment(file).Select(b => b.RawText);

        Assert.Equal(["- and milk"], remaining);
    }

    [Fact]
    public void A_second_run_appends_to_the_same_day_rather_than_starting_a_rival_section()
    {
        var first = FileRewriter.Rewrite(
            "- detergent\n- milk",
            [Done("detergent", "shopping: detergent")],
            Today);

        var second = FileRewriter.Rewrite(first, [Done("milk", "shopping: milk")], Today);

        Assert.Equal(
            """
            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent
            - ~~milk~~ → shopping: milk

            """,
            second);
    }

    [Fact]
    public void A_new_day_gets_its_own_section_above_the_older_one()
    {
        var yesterday = FileRewriter.Rewrite(
            "- detergent\n- milk",
            [Done("detergent", "shopping: detergent")],
            new DateOnly(2026, 8, 12));

        var today = FileRewriter.Rewrite(yesterday, [Done("milk", "shopping: milk")], Today);

        Assert.Equal(
            """
            ## Processed — 13 Aug 2026
            - ~~milk~~ → shopping: milk

            ## Processed — 12 Aug 2026
            - ~~detergent~~ → shopping: detergent

            """,
            today);
    }

    [Fact]
    public void A_failed_block_stays_in_the_inbox()
    {
        // Failure is outstanding work, and the inbox is what the file says is
        // outstanding. Filing it under "Processed" with an error beside it would
        // put it somewhere nobody looks again — and nothing retries it (STATUS
        // debt 24), so retyping the line is the only recovery there is.
        const string original = "- book the plumber";

        Assert.Equal(
            original,
            FileRewriter.Rewrite(original, [Outcome("book the plumber", DumpBlockStatus.Failed)], Today));
    }

    [Fact]
    public void An_ignored_block_moves_but_says_so()
    {
        var file = FileRewriter.Rewrite(
            "- remember to breathe",
            [Outcome("remember to breathe", DumpBlockStatus.Ignored)],
            Today);

        Assert.Contains("- ~~remember to breathe~~ → read, nothing to do", file, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_block_goes_to_needs_your_input_above_the_archive()
    {
        var file = FileRewriter.Rewrite(
            "- dinner was 2400\n- milk",
            [
                Outcome("dinner was 2400", DumpBlockStatus.NeedsInput, "who paid?"),
                Done("milk", "shopping: milk"),
            ],
            Today);

        Assert.Equal(
            """
            ## Needs your input
            - "dinner was 2400" — who paid? Answer by adding a line below.

            ## Processed — 13 Aug 2026
            - ~~milk~~ → shopping: milk

            """,
            file);
    }

    [Fact]
    public void The_users_own_headings_and_spacing_survive()
    {
        var file = FileRewriter.Rewrite(
            """
            # our week

            ## Groceries
            - detergent
            - the good olive oil

            ## Notes
            some prose that is not a list
            """,
            [Done("detergent", "shopping: detergent")],
            Today);

        Assert.Equal(
            """
            # our week

            ## Groceries
            - the good olive oil

            ## Notes
            some prose that is not a list

            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent

            """,
            file);
    }

    [Fact]
    public void The_inbox_is_moved_back_above_an_archive_someone_wrote_under()
    {
        // A partner replying underneath the archive is the ordinary way this
        // happens on a phone, where the headings have scrolled off the top.
        var file = FileRewriter.Rewrite(
            """
            ## Processed — 12 Aug 2026
            - ~~detergent~~ → shopping: detergent

            ## Inbox
            - milk
            - bread
            """,
            [Done("milk", "shopping: milk")],
            Today);

        Assert.Equal(
            """
            ## Inbox
            - bread

            ## Processed — 13 Aug 2026
            - ~~milk~~ → shopping: milk

            ## Processed — 12 Aug 2026
            - ~~detergent~~ → shopping: detergent

            """,
            file);
    }

    [Fact]
    public void Two_archive_sections_for_one_day_fold_into_one()
    {
        // Two headings for one day is what an interrupted run, or a paste, leaves
        // behind. Without folding, the next run adds a third.
        var file = FileRewriter.Rewrite(
            """
            - milk

            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent

            ## Processed — 13 Aug 2026
            - ~~coffee~~ → shopping: coffee
            """,
            [Done("milk", "shopping: milk")],
            Today);

        Assert.Equal(
            """
            ## Processed — 13 Aug 2026
            - ~~detergent~~ → shopping: detergent
            - ~~coffee~~ → shopping: coffee
            - ~~milk~~ → shopping: milk

            """,
            file);
    }

    [Fact]
    public void A_multi_line_block_becomes_one_archive_line()
    {
        var file = FileRewriter.Rewrite(
            "- book the hotel\n  the one near the station\n- pack",
            [Done("- book the hotel\n  the one near the station", "task: book the hotel")],
            Today);

        Assert.Equal(
            """
            - pack

            ## Processed — 13 Aug 2026
            - ~~book the hotel the one near the station~~ → task: book the hotel

            """,
            file);
    }

    [Fact]
    public void A_detail_containing_a_newline_cannot_break_the_list_item()
    {
        // A tool error is the likely source, and a raw newline here would put the
        // rest of it on a line of its own inside an output section — invisible
        // now, and read as input the moment someone moves the heading.
        var file = FileRewriter.Rewrite(
            "- detergent",
            [Done("detergent", "refused:\nquantity must be positive")],
            Today);

        Assert.Contains(
            "- ~~detergent~~ → refused: quantity must be positive",
            file,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_the_file_no_longer_contains_changes_nothing()
    {
        // The orphan case: a run picks up a block an earlier run abandoned, and
        // the line it came from has since been edited away. Writing here would
        // bump the version and make both partners' open editors stale for a file
        // that did not change.
        const string original = "- something else entirely";

        Assert.Equal(
            original,
            FileRewriter.Rewrite(original, [Done("a line that was deleted", "shopping: ghost")], Today));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_means_nothing_out(string? content) =>
        Assert.Equal(string.Empty, FileRewriter.Rewrite(content, [Done("milk", "shopping: milk")], Today));

    [Fact]
    public void No_outcomes_leaves_the_file_byte_for_byte_alone()
    {
        const string original = "## Inbox\n- milk\n\n\n   ";

        Assert.Equal(original, FileRewriter.Rewrite(original, [], Today));
    }

    [Fact]
    public void A_file_emptied_by_the_rewrite_keeps_only_its_archive()
    {
        var file = FileRewriter.Rewrite("milk", [Done("milk", "shopping: milk")], Today);

        Assert.Equal(
            """
            ## Processed — 13 Aug 2026
            - ~~milk~~ → shopping: milk

            """,
            file);
    }

    [Fact]
    public void Carriage_returns_from_a_browser_do_not_survive_into_the_file()
    {
        // A textarea posts \r\n; everything this class writes joins with \n. A
        // mixed file is not wrong, but it makes every later diff and every string
        // assertion depend on which path the text came in through.
        var file = FileRewriter.Rewrite(
            "- milk\r\n- bread",
            [Done("milk", "shopping: milk")],
            Today);

        Assert.DoesNotContain('\r', file);
    }
}
