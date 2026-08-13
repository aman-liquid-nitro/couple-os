using System.Text.Json;
using CoupleOS.Application.AI;

namespace CoupleOS.Application.Tools;

/// <summary>
/// One line naming what a tool call was about, for the change report and for the
/// private thread's change list.
///
/// One implementation rather than two agreeing ones, for the reason
/// <c>BlockSegmenter.RoleOf</c> is public: the two surfaces are required to
/// describe one tool in one voice, and each having its own copy is each having its
/// own chance to drift. They did — this text was duplicated in
/// <c>BlockProcessor</c> and <c>PrivateThread</c>, and only one of them printed
/// the tool's note.
///
/// <b>Named arguments first, and that is the whole of it.</b> The original rule was
/// "the first string argument is nearly always the human-meaningful one", which
/// held for four tools and broke on the fifth: <c>create_memory</c> emits
/// <c>assertion</c> before <c>content</c>, so a browser run showed
/// <i>"create memory: user_stated"</i> — a line naming a taxonomy label instead of
/// the thing being remembered. A model chooses the order it emits properties in,
/// so any rule that depends on that order is a rule about the model rather than
/// about the call.
/// </summary>
public static class ToolSummary
{
    /// <summary>
    /// The argument names that carry what a person would call the subject, in
    /// preference order. Every tool in the catalogue has one of these.
    /// </summary>
    private static readonly string[] Subjects =
        ["content", "title", "name", "description", "query", "fragment", "amount"];

    public static string Of(LlmToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.Arguments.ValueKind != JsonValueKind.Object)
        {
            return call.Arguments.GetRawText();
        }

        foreach (var subject in Subjects)
        {
            if (call.Arguments.TryGetProperty(subject, out var value) && Readable(value) is { } named)
            {
                return named;
            }
        }

        // A tool whose arguments name none of the above. Better than the raw JSON,
        // and a reminder that a new tool wants a line in Subjects — which the
        // catalogue test cannot enforce, because "meaningful to a person" is not a
        // property a test can read.
        foreach (var property in call.Arguments.EnumerateObject())
        {
            if (Readable(property.Value) is { } fallback)
            {
                return fallback;
            }
        }

        return call.Arguments.GetRawText();
    }

    /// <summary>
    /// Strings and numbers both, so <c>create_expense</c>'s amount reads as a
    /// figure rather than as raw JSON when it is all there is.
    /// </summary>
    private static string? Readable(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() is { Length: > 0 } text => text,
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };
}
