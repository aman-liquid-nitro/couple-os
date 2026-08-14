using System.Globalization;
using System.Text.RegularExpressions;

namespace CoupleOS.Application.Attachments;

/// <summary>
/// How an attachment appears inside the couple's own text, and how it is found
/// there again.
///
/// <para>Pure and static, like <see cref="Capture.BlockSegmenter"/> and for the
/// same reason: the writer and the reader must agree exactly. The upload writes a
/// markdown link into the file; a later Process reads that link back out of a
/// block to know which attachment the block's records should be linked to. Two
/// implementations of that format would be two chances to disagree, and the
/// disagreement is silent — a receipt that uploads fine, appears in the file, and
/// is linked to nothing.</para>
///
/// <para><b>The link carries the id, not the filename.</b> Writing
/// <c>[receipt](attachments/ac-service.jpg)</c> — which is what the eval set
/// sketched — means resolving a name back to a row, and two receipts called
/// <c>photo.jpg</c> then resolve to whichever the query happened to return.
/// A uuid resolves to one row or none, and the row is what row-level security
/// governs, so an id in the text of a file the partner can read still reveals
/// nothing they may not already see.</para>
/// </summary>
public static partial class AttachmentReference
{
    /// <summary>
    /// The URL an attachment link points at. Also the download route, so the link
    /// in the file is a link that works.
    /// </summary>
    public static string Path(Guid attachmentId) =>
        string.Create(CultureInfo.InvariantCulture, $"/attachments/{attachmentId}");

    /// <summary>
    /// The markdown a person sees. The label is the filename, because the file is
    /// what they recognise; if they rename it in the text, the link still works,
    /// which is the point of not resolving by name.
    /// </summary>
    public static string Markdown(Guid attachmentId, string filename) =>
        string.Create(CultureInfo.InvariantCulture, $"[{Label(filename)}]({Path(attachmentId)})");

    /// <summary>
    /// Every attachment this text refers to, in order and without repeats.
    ///
    /// Deduplicated because <c>attachment_links</c> has a composite primary key
    /// and the same file mentioned twice in one block is one link, not a
    /// constraint violation halfway through a run.
    /// </summary>
    public static IReadOnlyList<Guid> In(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var found = new List<Guid>();
        var seen = new HashSet<Guid>();

        foreach (Match match in LinkPattern().Matches(text))
        {
            if (Guid.TryParse(match.Groups["id"].Value, out var id) && seen.Add(id))
            {
                found.Add(id);
            }
        }

        return found;
    }

    /// <summary>
    /// Square brackets and parentheses are markdown's own delimiters, so a
    /// filename containing either would produce a link that renders as text.
    /// Replaced rather than escaped: the label is a courtesy, and the id beside it
    /// is what carries the meaning.
    /// </summary>
    private static string Label(string filename) =>
        filename.Replace('[', '(').Replace(']', ')').Trim();

    /// <summary>
    /// Matches the path this class writes, and only that. A loose pattern would
    /// pick a uuid out of any parenthesis in the couple's prose and link a
    /// receipt to a line that never mentioned one.
    /// </summary>
    [GeneratedRegex(
        @"\]\(\s*/attachments/(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex LinkPattern();
}
