using System.Globalization;
using System.Text;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Capture;

/// <summary>What a run settled one block as, ready to be written back into the file.</summary>
/// <param name="ContentHash">
/// How the block is found in the file. Matched by hash rather than by line
/// number because the line numbers were recorded when the block was first seen,
/// and the file has been edited since — by the partner, or by this same person
/// before pressing Process.
/// </param>
/// <param name="Detail">
/// The half of the archive line after the arrow: what the block became, or the
/// question it is parked on. Null falls back to a phrase for the status.
/// </param>
public sealed record BlockOutcome(byte[] ContentHash, DumpBlockStatus Status, string? Detail);

/// <summary>
/// Moves settled blocks out of the inbox and into the sections that record them.
///
/// The other half of <see cref="BlockSegmenter"/>, and written as its mirror:
/// pure, static, and sharing the classifier rather than reimplementing it. The
/// reader already refuses to read "Processed" and "Needs your input" back in;
/// this is what puts anything there.
///
/// Three rules carry it.
///
/// **Only settled blocks move.** A failed block stays in the inbox, because the
/// inbox is what the file says is outstanding and a failure is outstanding. It
/// will not be retried on its own (STATUS debt 24) — but a line the user can see
/// and retype is recoverable, and a line filed under "Processed" with an error
/// beside it is a line nobody will ever look at again.
///
/// **Everything else is left verbatim.** Not reflowed, not re-headed, not
/// reordered. The user's own headings, their blank lines and their spacing
/// survive a run untouched. Anything less makes Process feel like it takes the
/// file away from them.
///
/// **The archive is append-only within a day.** Each date gets one "Processed"
/// heading; a second run on the same day appends to it rather than starting a
/// rival section. New dates go above older ones, so the file reads newest-first
/// downward from the inbox and the oldest archive is the furthest to scroll.
/// </summary>
public static class FileRewriter
{
    /// <summary>
    /// Rendered with the year, unlike ADR 0009's sketch. "11 Aug" is unambiguous
    /// for a fortnight and quietly wrong forever after, and this file is meant to
    /// be the couple's record.
    /// </summary>
    private const string DateFormat = "d MMM yyyy";

    private const string NeedsInputHeading = "## Needs your input";

    /// <summary>
    /// Rewrites <paramref name="content"/> with the settled blocks moved.
    ///
    /// Returns the original text unchanged when there is nothing to move, which
    /// the caller uses to skip a write: a Process that only failed, or only
    /// touched blocks the file no longer contains, must not bump the version and
    /// make both partners' editors stale for nothing.
    /// </summary>
    /// <param name="today">
    /// The date the "Processed" heading carries. Passed in rather than read from
    /// a clock, because this is a pure function and because the couple's timezone
    /// is the caller's problem to know (STATUS debt 22).
    /// </param>
    public static string Rewrite(string? content, IReadOnlyList<BlockOutcome> outcomes, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        if (string.IsNullOrEmpty(content) || outcomes.Count == 0)
        {
            return content ?? string.Empty;
        }

        var settled = outcomes
            .Where(o => Archives(o.Status))
            .GroupBy(o => Convert.ToHexString(o.ContentHash))

            // Two blocks with the same hash cannot both be in the file — the
            // (dump_file_id, content_hash) index says so — but a run can settle a
            // block whose text the user has since written twice. First wins,
            // rather than the dictionary throwing on the duplicate key.
            .ToDictionary(g => g.Key, g => g.First());

        if (settled.Count == 0)
        {
            return content;
        }

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // The segmenter is what decides which lines are input, so asking it is the
        // only way to be sure the writer removes exactly the lines the reader read.
        var moved = new bool[lines.Length];
        var archived = new List<(DumpBlockStatus Status, string Line)>();

        foreach (var block in BlockSegmenter.Segment(content))
        {
            if (!settled.TryGetValue(Convert.ToHexString(block.ContentHash), out var outcome))
            {
                continue;
            }

            for (var line = block.LineStart; line <= block.LineEnd; line++)
            {
                moved[line - 1] = true;
            }

            archived.Add((outcome.Status, Entry(block.RawText, outcome)));
        }

        if (archived.Count == 0)
        {
            // Every settled block belongs to text that is no longer in the file —
            // the partner edited it out, or an earlier run's orphan finally ran.
            return content;
        }

        var regions = Parse(lines, moved);
        var todayHeading = $"## Processed — {today.ToString(DateFormat, CultureInfo.InvariantCulture)}";

        // Every "Needs your input" section in the file folds into one, whatever
        // its heading said. There is only ever one open list of questions.
        Merge(
            regions,
            SectionRole.NeedsInput,
            r => r.Role == SectionRole.NeedsInput,
            NeedsInputHeading,
            [.. archived.Where(a => a.Status == DumpBlockStatus.NeedsInput).Select(a => a.Line)]);

        // The archive folds only within a date. Yesterday's section is a separate
        // record and stays one.
        Merge(
            regions,
            SectionRole.Processed,
            r => r.Role == SectionRole.Processed && Heads(r, todayHeading),
            todayHeading,
            [.. archived.Where(a => a.Status != DumpBlockStatus.NeedsInput).Select(a => a.Line)]);

        return Render(Order(regions, todayHeading));
    }

    /// <summary>
    /// Whether this status belongs in an output section.
    ///
    /// Ignored moves with Processed. It is tempting to leave it in the inbox —
    /// nothing was created — but nothing ever will be either: the block is
    /// settled, so the next Process will not look at it again, and a line that
    /// will never be acted on sitting among lines that will is the file lying
    /// about what is outstanding. It moves, and it moves carrying the words
    /// "read, nothing to do" so the move is not mistaken for an action.
    /// </summary>
    private static bool Archives(DumpBlockStatus status) => status is
        DumpBlockStatus.Processed or DumpBlockStatus.Ignored or DumpBlockStatus.NeedsInput;

    /// <summary>
    /// One archive line, in ADR 0009's format.
    ///
    /// The text is flattened through <see cref="BlockSegmenter.Normalize"/>: a
    /// two-line block becomes one line, and the list marker it arrived with is
    /// dropped rather than nested inside the one written here. Safe to reshape
    /// because nothing reads this section back — and the normalized form is
    /// exactly the text the hash was taken over, so it stays recognisable as the
    /// same thought.
    /// </summary>
    private static string Entry(string rawText, BlockOutcome outcome)
    {
        var text = BlockSegmenter.Normalize(rawText).Replace('\n', ' ');

        if (outcome.Status == DumpBlockStatus.NeedsInput)
        {
            // The question arrives already carrying the fragment it is about —
            // request_clarification composes ADR 0009's `"fragment" — question`
            // from its two arguments, because the model is the only thing that
            // knows which half of a three-item note is in doubt. Quoting the
            // whole block here as well would print the line twice.
            var question = Blank(outcome.Detail)
                ? $"""
                  "{text}" — what did you mean?
                  """
                : OneLine(outcome.Detail!);

            return $"- {question} Answer by adding a line below.";
        }

        var became = outcome.Status == DumpBlockStatus.Ignored || Blank(outcome.Detail)
            ? "read, nothing to do"
            : outcome.Detail!;

        // Struck through, so the inbox and the archive cannot be confused at a
        // glance on a phone, where the headings scroll off the top.
        return $"- ~~{text}~~ → {OneLine(became)}";
    }

    private sealed class Region(string? heading, SectionRole role)
    {
        public string? Heading { get; } = heading;

        public SectionRole Role { get; } = role;

        public List<string> Body { get; } = [];
    }

    private static bool Heads(Region region, string heading) =>
        string.Equals(region.Heading?.Trim(), heading, StringComparison.Ordinal);

    /// <summary>
    /// Splits the file into regions at its headings, dropping the moved lines as
    /// it goes. The preamble — anything above the first heading — is a region
    /// with no heading, which is the whole file for a couple who have never used
    /// one.
    /// </summary>
    private static List<Region> Parse(string[] lines, bool[] moved)
    {
        var regions = new List<Region> { new(heading: null, SectionRole.Input) };

        for (var i = 0; i < lines.Length; i++)
        {
            if (BlockSegmenter.RoleOf(lines[i]) is { } role)
            {
                regions.Add(new Region(lines[i], role));
                continue;
            }

            if (!moved[i])
            {
                regions[^1].Body.Add(lines[i]);
            }
        }

        return regions;
    }

    /// <summary>
    /// Folds every region <paramref name="matches"/> selects into one, under
    /// <paramref name="heading"/>, and appends the run's new entries to it.
    ///
    /// Folding matters more than it looks. A user who pastes text below the
    /// archive, or a run interrupted halfway, can leave two "Processed" headings
    /// with a stretch of inbox between them; without this the file would grow a
    /// third on the next run and the inbox would end up fragmented between
    /// archives. Collapsing on every run makes the layout converge whatever state
    /// it starts in.
    ///
    /// The new region is inserted where the first one it replaced was, so this
    /// changes content and not order. <see cref="Order"/> owns order.
    /// </summary>
    private static void Merge(
        List<Region> regions,
        SectionRole role,
        Func<Region, bool> matches,
        string heading,
        IReadOnlyList<string> entries)
    {
        var matching = regions.Where(matches).ToList();

        if (matching.Count == 0 && entries.Count == 0)
        {
            return;
        }

        // Rebuilt rather than reused even when there is exactly one, so the
        // heading is always the canonical spelling. A section the user retitled
        // "## Needs Your Input!!" keeps its questions and loses its punctuation.
        var target = new Region(heading, role);
        var at = matching.Count == 0 ? regions.Count : regions.IndexOf(matching[0]);

        foreach (var region in matching)
        {
            // Trimmed on the way in, not only on the way out. A section ends with
            // a blank line — the file's own trailing newline is one — and
            // appending after it would put a gap between yesterday's last entry
            // and today's first, widening by one every run.
            target.Body.AddRange(Trim(region.Body));
            regions.Remove(region);
        }

        target.Body.AddRange(entries);

        // An empty output section is noise, and happens when the only entries it
        // ever had have since been deleted by hand.
        if (target.Body.All(Blank))
        {
            return;
        }

        regions.Insert(Math.Min(at, regions.Count), target);
    }

    /// <summary>
    /// The file's shape after a run: everything the couple wrote, then the open
    /// questions, then the archive newest-first.
    ///
    /// Input regions keep their order among themselves and always come first,
    /// which is the only reordering this class does to a user's own text — and it
    /// is the one that matters, because an inbox that ends up below three months
    /// of archive is an inbox nobody writes in from a phone.
    /// </summary>
    private static List<Region> Order(List<Region> regions, string todayHeading) =>
    [
        .. regions.Where(r => r.Role == SectionRole.Input),
        .. regions.Where(r => r.Role == SectionRole.NeedsInput),
        .. regions.Where(r => r.Role == SectionRole.Processed && Heads(r, todayHeading)),
        .. regions.Where(r => r.Role == SectionRole.Processed && !Heads(r, todayHeading)),
    ];

    /// <summary>
    /// Regions back to text: one blank line between them, no trailing blanks
    /// inside them, and a single newline at the end of the file.
    /// </summary>
    private static string Render(List<Region> regions)
    {
        var builder = new StringBuilder();

        foreach (var region in regions)
        {
            var body = Trim(region.Body);

            if (region.Heading is null && body.Count == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            if (region.Heading is not null)
            {
                builder.Append(region.Heading).Append('\n');
            }

            foreach (var line in body)
            {
                builder.Append(line).Append('\n');
            }
        }

        return builder.ToString();
    }

    /// <summary>Leading and trailing blank lines, gone; the ones in the middle, kept.</summary>
    private static List<string> Trim(List<string> body)
    {
        var start = 0;
        var end = body.Count - 1;

        while (start <= end && Blank(body[start]))
        {
            start++;
        }

        while (end >= start && Blank(body[end]))
        {
            end--;
        }

        return body.GetRange(start, end - start + 1);
    }

    private static bool Blank(string? line) => string.IsNullOrWhiteSpace(line);

    /// <summary>
    /// A tool description with a newline in it would break the list item it is
    /// written into, turning the rest of the detail into a block of its own the
    /// next run would read as input.
    /// </summary>
    private static string OneLine(string value) =>
        value.ReplaceLineEndings(" ").Trim();
}
