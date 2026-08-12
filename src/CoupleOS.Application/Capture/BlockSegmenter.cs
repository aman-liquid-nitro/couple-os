using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CoupleOS.Application.Capture;

/// <summary>One captured thought, located in the file and identified by its hash.</summary>
/// <param name="RawText">Verbatim, including the list marker. The report quotes this.</param>
/// <param name="ContentHash">SHA-256 of <see cref="BlockSegmenter.Normalize"/>'s output.</param>
/// <param name="LineStart">1-based, inclusive, over the whole file.</param>
/// <param name="LineEnd">1-based, inclusive.</param>
public sealed record SegmentedBlock(string RawText, byte[] ContentHash, int LineStart, int LineEnd);

/// <summary>
/// Splits a dump file into blocks and identifies each one.
///
/// Pure and static, like <see cref="Identity.SecretToken"/> and
/// <see cref="CapturePrompt"/>, because it makes a decision the rest of the
/// system depends on and has no business needing a database or a model to make
/// it. Two rules carry the weight:
///
/// **What counts as input.** Sections named "Processed" and "Needs your input"
/// are output — the run writes them, so re-reading them would re-execute work
/// already done. Everything else is input, including text under a heading this
/// code has never heard of. The inverse rule (only "## Inbox" is input) was the
/// obvious one and is wrong: a user who renames or deletes that heading would
/// have their writing silently ignored, which is precisely the failure M2 exists
/// to eliminate. Unrecognised content is read; recognised output is not.
///
/// **What counts as a block.** One non-blank line, plus any indented lines
/// following it. Not one paragraph: people write dumps one thought per line and
/// do not leave blank lines between them, so paragraph segmentation would fuse
/// "we're out of detergent" and "dinner at Priya's parents Saturday 8pm" into a
/// single block and one of them would be lost. The cost of the finer rule is
/// that genuinely wrapped prose splits into blocks that produce no tool call —
/// and a block that produces nothing is still a block with a status, which the
/// change report has to account for. The failure is visible rather than silent.
/// </summary>
public static partial class BlockSegmenter
{
    [GeneratedRegex(@"^ {0,3}#{1,6}\s+(?<title>.*)$")]
    private static partial Regex Heading { get; }

    /// <summary>A bullet or an ordered item. Trailing whitespace is required, so a thematic break is not a list.</summary>
    [GeneratedRegex(@"^ {0,3}(?:[-*+]|\d{1,9}[.)])\s+")]
    private static partial Regex ListMarker { get; }

    /// <summary>--- , *** , ___ — markdown structure, never content.</summary>
    [GeneratedRegex(@"^ {0,3}(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex ThematicBreak { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun { get; }

    /// <summary>
    /// Headings the run itself writes. Matched on prefix because the date moves:
    /// "Processed — 11 Aug" and "Processed — 12 Aug" are the same section.
    /// </summary>
    private static readonly string[] OutputSections = ["processed", "needs your input"];

    public static IReadOnlyList<SegmentedBlock> Segment(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var lines = content.Split('\n');
        var blocks = new List<SegmentedBlock>();

        var inOutputSection = false;
        List<string>? current = null;
        var currentStart = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var lineNumber = i + 1;

            var heading = Heading.Match(raw);
            if (heading.Success)
            {
                Flush();
                inOutputSection = IsOutputSection(heading.Groups["title"].Value);
                continue;
            }

            if (inOutputSection || raw.Trim().Length == 0 || ThematicBreak.IsMatch(raw))
            {
                Flush();
                continue;
            }

            // An indented line continues the block above it — a wrapped list item,
            // or the second line of a note. With no block open it starts one, so
            // stray indentation cannot swallow a line.
            var isContinuation = current is not null
                && raw.StartsWith("  ", StringComparison.Ordinal)
                && !ListMarker.IsMatch(raw);

            if (isContinuation)
            {
                current!.Add(raw);
                continue;
            }

            Flush();
            current = [raw];
            currentStart = lineNumber;
        }

        Flush();
        return blocks;

        void Flush()
        {
            if (current is null)
            {
                return;
            }

            var rawText = string.Join('\n', current);
            var normalized = Normalize(rawText);

            // A block whose content normalizes away is punctuation, not a thought.
            if (normalized.Length > 0)
            {
                blocks.Add(new SegmentedBlock(
                    rawText,
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalized)),
                    currentStart,
                    currentStart + current.Count - 1));
            }

            current = null;
        }
    }

    /// <summary>
    /// The exact text the hash is taken over, and public for that reason: the
    /// dedup guarantee is only as meaningful as this function is legible.
    ///
    /// It strips list markers and collapses whitespace, so the run's own rewrite
    /// of the file — which re-indents and re-bullets — cannot make an unchanged
    /// thought look new. It does **not** lower-case: "call Mom" and "call mom"
    /// stay distinct, because merging them would silently discard whichever
    /// wording arrived second, and the two are not always the same thought.
    /// </summary>
    public static string Normalize(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        var lines = rawText
            .Split('\n')
            .Select(line => ListMarker.Replace(line.TrimEnd('\r'), string.Empty))
            .Select(line => WhitespaceRun.Replace(line, " ").Trim())
            .Where(line => line.Length > 0);

        return string.Join('\n', lines);
    }

    private static bool IsOutputSection(string title)
    {
        var trimmed = title.Trim();

        return OutputSections.Any(section =>
            trimmed.StartsWith(section, StringComparison.OrdinalIgnoreCase));
    }
}
