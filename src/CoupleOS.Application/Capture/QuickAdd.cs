namespace CoupleOS.Application.Capture;

/// <summary>
/// Puts one line into the file, in the one place a run will read it from.
///
/// ADR 0009 calls quick-add not optional, and says why: if capturing a thought
/// while standing in the bathroom is harder than it was in a chat box, people
/// stop capturing and V0 fails for an interface reason rather than an idea one.
/// So this is a single line, appended, with no response to read and no version to
/// carry.
///
/// It is a splice rather than a re-render, which is the whole difference between
/// this and <see cref="FileRewriter"/>. The rewrite has earned the right to
/// reshape the file — it is moving things and has to lay them out. An append has
/// not: the couple's blank lines, their indentation and their own headings come
/// back byte for byte, because a quick-add that quietly reflowed a file somebody
/// was in the middle of writing would be a strange thing to have happened.
///
/// **Where the line goes matters more than it looks.** Appending at the end of
/// the file is the obvious implementation and is wrong: after one run the end of
/// the file is inside the Processed archive, which the segmenter refuses to read,
/// so every quick-added line would vanish silently — the exact defect M2 exists
/// to eliminate, reintroduced by the cheapest possible shortcut.
/// </summary>
public static class QuickAdd
{
    /// <summary>
    /// A heading the line prefers, when the file has one. Matched on prefix, as
    /// output sections are, so "Inbox" and "Inbox — this week" are one section.
    /// </summary>
    private const string InboxPrefix = "inbox";

    /// <summary>
    /// Returns <paramref name="content"/> with <paramref name="line"/> added to
    /// the inbox, or null when there is nothing to add.
    ///
    /// Null rather than an unchanged string, because "the user pressed add with an
    /// empty box" and "the user added something that happened to change nothing"
    /// are different events and only one of them should write to the database.
    /// </summary>
    public static string? Append(string? content, string? line)
    {
        var text = Flatten(line);

        if (text.Length == 0)
        {
            return null;
        }

        // Written as a list item, which is what the rest of the file looks like
        // and what the segmenter's hash strips off anyway — so a line quick-added
        // and the same words typed into the editor are one block, not two.
        var entry = $"- {text}";

        if (string.IsNullOrWhiteSpace(content))
        {
            return entry + "\n";
        }

        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        lines.Insert(InsertAt(lines), entry);

        return string.Join('\n', lines);
    }

    /// <summary>
    /// The index the new line takes: just after the last line of content a run
    /// would read, preferring an explicit inbox section when the file has one.
    ///
    /// Both trackers ignore blank lines, so the entry lands against the last real
    /// line rather than after the gap below it — a file with three blank lines at
    /// the end of its inbox should not grow a fourth.
    /// </summary>
    private static int InsertAt(List<string> lines)
    {
        // -1 means "nothing yet", which renders as index 0: the top of the file.
        // That is the right answer for a file whose first line is a heading the
        // run writes, because everything above it is input.
        var lastInput = -1;
        var lastInbox = -1;

        var inInput = true;
        var inInbox = false;

        for (var i = 0; i < lines.Count; i++)
        {
            if (BlockSegmenter.RoleOf(lines[i]) is { } role)
            {
                inInput = role == SectionRole.Input;

                inInbox = inInput
                    && BlockSegmenter.TitleOf(lines[i])!
                        .StartsWith(InboxPrefix, StringComparison.OrdinalIgnoreCase);

                // The heading itself, so a line added to an empty "## Inbox"
                // lands under the heading rather than above it.
                if (inInbox)
                {
                    lastInbox = i;
                }
                else if (inInput)
                {
                    lastInput = i;
                }

                continue;
            }

            if (!inInput || string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            if (inInbox)
            {
                lastInbox = i;
            }
            else
            {
                lastInput = i;
            }
        }

        return (lastInbox >= 0 ? lastInbox : lastInput) + 1;
    }

    /// <summary>
    /// One line, whatever arrived. A quick-add box is one line by construction,
    /// but paste exists, and a pasted newline would turn one entry into two
    /// blocks — the second of them un-bulleted and possibly indented into being
    /// read as a continuation of the first.
    /// </summary>
    private static string Flatten(string? line) =>
        line is null
            ? string.Empty
            : string.Join(' ', line.ReplaceLineEndings("\n").Split('\n')
                .Select(part => part.Trim())
                .Where(part => part.Length > 0));
}
