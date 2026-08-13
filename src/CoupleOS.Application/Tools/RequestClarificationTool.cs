using System.Text.Json;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 2a. The only legal way for the model to decline, and the one tool
/// that writes no row at all.
///
/// It exists because of an interaction between two rules that on their own look
/// harmless. TOOLS.md rule 1 forbids inventing a value to satisfy a required
/// field, and ADR 0004 makes tools the sole write path — so a model looking at
/// "remind me to book the dentist", with no time in it, has no compliant action
/// available. Verified against a local model: given no such tool it correctly
/// declined to invent a value, produced empty content, and the line vanished
/// with no record and no error. That is the same silence M2 exists to remove,
/// one level further in: the report can only account for a block if something
/// happened to it, and "the model had nothing legal to do" was not something.
///
/// What it produces is a status, not a row. <see cref="ToolExecution.Asks"/>
/// carries the question back out through the dispatcher, and
/// <c>BlockProcessor</c> is what turns that into
/// <c>dump_blocks.status = 'needs_input'</c> and a filled <c>question</c> — the
/// two columns the schema has always had and nothing has ever written. A tool
/// cannot do that itself and should not be able to: it is handed a couple, a
/// user and a visibility, and deliberately not the block it came from, so
/// nothing in the tool layer can reach across and rewrite the pipeline's own
/// bookkeeping.
/// </summary>
public sealed class RequestClarificationTool : ITool
{
    /// <summary>
    /// Both fields are required, and <c>about</c> is the one worth explaining.
    /// The question alone — "who paid?" — is unreadable a day later next to a
    /// note with three expenses in it, and the block's raw text is the whole
    /// note rather than the fragment in doubt.
    /// </summary>
    private const string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"question\":{\"type\":\"string\",\"description\":\"One specific question, as you would ask a person\",\"maxLength\":300}," +
        "\"about\":{\"type\":\"string\",\"description\":\"The fragment of the note the question is about, verbatim\",\"maxLength\":300}}," +
        "\"required\":[\"question\",\"about\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    public string Name => "request_clarification";

    /// <summary>
    /// Says what it is *not* for as plainly as what it is. A model that reaches
    /// for this on "saturday 8pm" has turned a resolvable date into a question
    /// the couple has to answer by hand, which is worse than the silence it
    /// replaced — the eval set asserts against exactly that (TOOLS.md 2a).
    /// </summary>
    public string Description =>
        "Ask the person one question when a note is actionable but a required value is genuinely " +
        "missing — who paid, which of two people, how much. Use this instead of guessing, and " +
        "instead of staying silent. Do not use it for a date or time the note already states in " +
        "any form: 'saturday 8pm', 'friday', 'next week' are values, not missing information.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>Writes nothing, so there is nothing to confirm.</summary>
    public ToolTier Tier => ToolTier.None;

    /// <summary>
    /// Asking twice records one question, not two. Nothing here depends on how
    /// many times it ran.
    /// </summary>
    public bool IsIdempotent => true;

    public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        Check("question", 300);
        Check("about", 300);

        return ValueTask.FromResult(errors.Count == 0
            ? ToolValidation.Valid
            : new ToolValidation(false, errors));

        void Check(string name, int maxLength)
        {
            if (!arguments.TryGetProperty(name, out var element) ||
                element.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(element.GetString()))
            {
                // The schema's check constraint refuses needs_input without a
                // question, so an empty one would fail at the database rather
                // than here — and fail the whole block, turning a model's sloppy
                // question into a lost line.
                errors.Add($"'{name}' is required and must be a non-empty string.");

                return;
            }

            if (element.GetString()!.Trim().Length > maxLength)
            {
                errors.Add($"'{name}' must be {maxLength} characters or fewer.");
            }
        }
    }

    public Task<ToolExecution> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var question = invocation.Arguments.GetProperty("question").GetString()!.Trim();
        var about = invocation.Arguments.GetProperty("about").GetString()!.Trim();

        // ADR 0009's line, composed here because this is the only place that has
        // both halves. The fragment is quoted because it is the user's own words
        // and the question is the system's, and the file they end up in is read
        // by two people who each wrote half of it. Straight quotes, matching the
        // ADR's sketch and FileRewriter's fallback for a question that is missing.
        return Task.FromResult(ToolExecution.Asks($"\"{Flatten(about)}\" — {Flatten(question)}"));
    }

    /// <summary>
    /// A newline here would break the list item this text is written into,
    /// turning the rest of the question into a block of its own that the next
    /// run would read as fresh input.
    /// </summary>
    private static string Flatten(string value) => value.ReplaceLineEndings(" ").Trim();
}
