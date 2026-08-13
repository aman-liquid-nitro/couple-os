using System.Globalization;
using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 6 · SPEC.md §8 · ADR 0006. What the couple knows, as opposed to what
/// they have to do.
///
/// This is the sixth of the seven tools and the first whose arguments are mostly
/// *about* the claim rather than the claim itself — <c>assertion</c>,
/// <c>confidence</c>, <c>subject_key</c>, <c>expires_expression</c> all exist to
/// keep a sentence from becoming more permanent or more certain than it deserves.
/// Three rules do that work, and none of them trusts the model:
///
/// <b>A model may not certify its own guess.</b> <c>assertion: "inferred"</c> caps
/// confidence at 0.7 whatever number came with it, and says so when it had to.
/// ADR 0006's corrosive failure is a guess that hardens into a fact through
/// repetition, and the cap is the only thing standing in the way — "trust me,
/// you're sure about this" must not be a way to raise it.
///
/// <b>Temporary context may not become permanent.</b>
/// <c>memories_temp_context_expires</c> refuses the row without an
/// <c>expires_at</c>, and TOOLS.md asks for a 30-day default rather than a
/// rejection when the person gave no span. So a missing expiry is filled in and
/// stated; an expiry that was given and cannot be read is a failure, like every
/// other date in this codebase, because replacing somebody's "for a fortnight"
/// with a month is the guessing the date contract exists to prevent.
///
/// <b>A contradiction supersedes; a repetition writes nothing.</b> SPEC.md §45 and
/// §44 are two halves of one lookup on <c>subject_key</c>. The same subject with
/// different words is a correction — the old row becomes <c>superseded</c> pointing
/// at the new one, so the history survives and only one claim is live. The same
/// subject with the same words is a person saying something twice, and the honest
/// answer is a row that does not exist and a sentence that says why.
///
/// <b>What it does not do.</b> TOOLS.md's note asks for a shared-file memory that
/// reads like a surprise to raise <c>dump_blocks.privacy_flagged</c>. It does not,
/// and the reason is that nothing here may judge it: deciding a sentence is a
/// surprise is exactly the inference ADR 0009 removed from this system, and the
/// only way to reach it would be to offer the model a privacy vocabulary — the
/// thing the <c>visibility</c> rule exists to forbid. STATUS debt 40.
/// </summary>
public sealed class CreateMemoryTool(IMemoryWriter writer, ICoupleClock clock) : ITool
{
    /// <summary>
    /// The ceiling ADR 0006 puts on anything the model inferred rather than heard.
    /// </summary>
    private const decimal InferredCeiling = 0.70m;

    /// <summary>
    /// How long a musing lives when nobody said. TOOLS.md's own figure, and a
    /// month is the right shape of answer: long enough that "a new sofa at some
    /// point" survives the week it was mentioned in, short enough that it does not
    /// become something the couple believes they decided.
    /// </summary>
    private const int DefaultTemporaryDays = 30;

    /// <summary>
    /// Six of the eight <c>memory_type</c> labels plus two the schema also has.
    /// All eight are offered because all eight are meaningful to a person writing a
    /// note, and the model choosing badly between <c>episodic</c> and
    /// <c>semantic</c> costs a filter, where an absent label would cost the memory.
    /// </summary>
    private static readonly string[] Types =
    [
        "episodic", "semantic", "preference", "decision",
        "commitment", "plan", "event", "temporary_context",
    ];

    private static readonly string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"content\":{\"type\":\"string\",\"maxLength\":2000," +
        "\"description\":\"The thing to remember, as a statement, in the person's own terms\"}," +
        "\"type\":{\"type\":\"string\",\"enum\":[" + string.Join(",", Types.Select(t => $"\"{t}\"")) + "]," +
        // Kept short, and that is a measurement rather than a preference. The first
        // version of this line spelled out all eight labels with an example each;
        // gemma4:31b responded by omitting `type` altogether on two cases that had
        // been getting it right, which is a required field missing rather than a
        // label chosen badly — strictly worse. So the description discriminates only
        // the pair that was actually being confused (hedged musing filed as an
        // intention, which SPEC.md §8 warns about because it would then be
        // permanent) and leaves the six self-explanatory labels to explain
        // themselves. A longer description is not a clearer one.
        "\"description\":\"The kind of memory. If the wording is hedged — 'maybe', 'at some point', " +
        "'we should think about', 'I wonder' — it is 'temporary_context' and never 'plan'. 'plan' is only " +
        "for something the person has actually decided to do.\"}," +

        // 'user_confirmed' and 'imported' are real labels in the column and are
        // deliberately not offered. Confirming a memory is something a person does
        // and V0 has no screen for it (STATUS debt 29); importing is a flow that
        // does not exist. A model that could claim either would be claiming a
        // provenance nobody granted.
        "\"assertion\":{\"type\":\"string\",\"enum\":[\"user_stated\",\"inferred\"]," +
        "\"description\":\"'user_stated' when the person said it outright. 'inferred' when you worked it " +
        "out from how they put it — 'I think she prefers Italian' is inferred, not stated.\"}," +
        "\"confidence\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1," +
        "\"description\":\"How sure the person sounded. Omit unless there is a reason to lower it.\"}," +
        "\"subject_key\":{\"type\":\"string\",\"maxLength\":200," +
        "\"description\":\"What this is about, as 'topic:thing' — 'self:cuisine', 'decision:car', " +
        "'partner:coffee'. Two statements about the same thing must carry the same key, because that is " +
        "how a later correction finds the thing it corrects. Omit for a one-off that corrects nothing.\"}," +
        "\"expires_expression\":{\"type\":\"string\",\"maxLength\":100," +
        "\"description\":\"How long this stays relevant, copied as the person put it: 'for two weeks', " +
        "'for a month'. Required in spirit for temporary_context; never a date you worked out.\"}}," +
        "\"required\":[\"content\",\"type\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private readonly IMemoryWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly ICoupleClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string Name => "create_memory";

    public string Description =>
        "Remember something about the couple or their life that is worth keeping — a preference, a " +
        "decision, a plan, something that happened. Not for things that need doing; those are tasks. " +
        "A hedged aside — 'maybe', 'at some point', 'we should think about' — is type " +
        "'temporary_context', not 'plan'.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>
    /// A memory is a row a person can contradict, and contradicting it is a
    /// supported operation rather than a repair. Nothing to confirm.
    /// </summary>
    public ToolTier Tier => ToolTier.None;

    /// <summary>
    /// False, and the one tool where that is true. TOOLS.md says outright that
    /// dedup here is semantic rather than key-based: the same note said twice in
    /// different words is one memory, and an idempotency key over canonical
    /// arguments cannot see that. <see cref="ExecuteAsync"/> does the work instead,
    /// against <c>subject_key</c>.
    /// </summary>
    public bool IsIdempotent => false;

    public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!arguments.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(content.GetString()))
        {
            errors.Add("'content' is required and must be a non-empty string.");
        }
        else if (content.GetString()!.Trim().Length > 2000)
        {
            errors.Add("'content' must be 2000 characters or fewer.");
        }

        if (!arguments.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            ParseType(type.GetString()) is null)
        {
            errors.Add($"'type' is required and must be one of: {string.Join(", ", Types)}.");
        }

        if (arguments.TryGetProperty("assertion", out var assertion) &&
            (assertion.ValueKind != JsonValueKind.String ||
             ParseAssertion(assertion.GetString()) is null))
        {
            errors.Add("'assertion' must be 'user_stated' or 'inferred' when supplied.");
        }

        if (arguments.TryGetProperty("confidence", out var confidence))
        {
            if (confidence.ValueKind != JsonValueKind.Number || !confidence.TryGetDecimal(out var value))
            {
                errors.Add("'confidence' must be a number between 0 and 1 when supplied.");
            }
            else if (value < 0 || value > 1)
            {
                // memories_confidence_range refuses this at the database. Clamping
                // it silently would be the tempting alternative and it would hide a
                // model that has misunderstood the scale.
                errors.Add("'confidence' must be between 0 and 1.");
            }
        }

        if (arguments.TryGetProperty("subject_key", out var subject) &&
            subject.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add("'subject_key' must be a string when supplied.");
        }
        else if (subject.ValueKind == JsonValueKind.String && subject.GetString()!.Trim().Length > 200)
        {
            errors.Add("'subject_key' must be 200 characters or fewer.");
        }

        if (arguments.TryGetProperty("expires_expression", out var expires) &&
            expires.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add("'expires_expression' must be a string when supplied, copied from what the person wrote.");
        }

        return ValueTask.FromResult(errors.Count == 0
            ? ToolValidation.Valid
            : new ToolValidation(false, errors));
    }

    public async Task<ToolExecution> ExecuteAsync(
        ToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var arguments = invocation.Arguments;
        var context = invocation.Context;
        var notes = new List<string>();

        var type = ParseType(arguments.GetProperty("type").GetString())!.Value;
        var content = arguments.GetProperty("content").GetString()!.Trim();

        var assertion = Text(arguments, "assertion") is { } claimed
            ? ParseAssertion(claimed)!.Value
            : MemoryAssertion.UserStated;

        var confidence = arguments.TryGetProperty("confidence", out var confidenceElement) &&
                         confidenceElement.ValueKind == JsonValueKind.Number
            ? confidenceElement.GetDecimal()
            : 1.00m;

        if (assertion == MemoryAssertion.Inferred && confidence > InferredCeiling)
        {
            // ADR 0006, and not negotiable by argument. Said out loud, because a
            // number quietly lowered is a number the couple would go on believing.
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"this is a guess rather than something you said, so confidence is held at " +
                $"{InferredCeiling:0.0#} rather than {confidence:0.0#}"));

            confidence = InferredCeiling;
        }

        var expiry = await ExpiryAsync(arguments, type, notes, cancellationToken);

        if (expiry.Failed)
        {
            return ToolExecution.Failed(expiry.Failure!);
        }

        var subjectKey = Normalise(Text(arguments, "subject_key"));

        var memory = new Memory
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Type = type,
            Assertion = assertion,
            Source = SourceOf(context.Visibility),
            Confidence = Math.Round(confidence, 2, MidpointRounding.AwayFromZero),
            Content = content,
            SubjectKey = subjectKey,
            ExpiresAt = expiry.Instant,
        };

        var superseded = Array.Empty<Memory>();

        if (subjectKey is not null)
        {
            var existing = await _writer.FindBySubjectAsync(context.CoupleId, type, subjectKey, cancellationToken);

            // SPEC.md §44 before §45: a person repeating themselves has not
            // contradicted anything, and superseding a row with an identical one
            // would file a correction that corrects nothing. Compared on the
            // normalised text rather than the raw, so "Likes Italian food." and
            // "likes italian food" are one memory.
            if (existing.FirstOrDefault(m => Same(m.Content, content)) is { } duplicate)
            {
                return ToolExecution.Unchanged(
                    $"already recorded, on {duplicate.CreatedAt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} — " +
                    "nothing added");
            }

            superseded = [.. existing];

            foreach (var old in superseded)
            {
                // Quoted, so a wrong supersession is visible rather than merely
                // recorded. This is the one thing this tool does that removes
                // something, and ADR 0009 puts corrections in the change report for
                // exactly this reason.
                notes.Add($"this replaces \"{Shorten(old.Content)}\"");
            }
        }

        await _writer.AddAsync(memory, superseded, cancellationToken);

        return ToolExecution.Created(
            "memory",
            memory.Id,
            notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>
    /// When this memory stops being true, and the sentence explaining any part of
    /// that this layer decided.
    /// </summary>
    private async Task<ToolDate> ExpiryAsync(
        JsonElement arguments,
        MemoryType type,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var expression = Text(arguments, "expires_expression");
        var resolved = await ToolDate.ResolveAsync(_clock, expression, cancellationToken);

        if (resolved.Failed)
        {
            // Fails, and does not fall back. TOOLS.md's 30-day default answers "the
            // person gave no span"; it does not license replacing a span they did
            // give with a different one, which for a temporary memory decides when
            // the couple stops being reminded of their own idea.
            return resolved;
        }

        if (resolved.Present)
        {
            notes.Add(resolved.Note ?? $"kept until {Describe(resolved)}");
            return resolved;
        }

        if (type != MemoryType.TemporaryContext)
        {
            // Everything else is permanent, which is STATUS debt 29 rather than a
            // decision: nothing in V0 ages a preference out or re-asks an inference.
            return resolved;
        }

        var now = await _clock.NowAsync(cancellationToken);
        var fallback = now.Now.AddDays(DefaultTemporaryDays).ToUniversalTime();

        notes.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"nothing said how long this holds, so it is kept for {DefaultTemporaryDays} days — until " +
            $"{TimeZoneInfo.ConvertTime(fallback, now.Zone):d MMM yyyy}"));

        return new ToolDate(fallback);
    }

    /// <summary>
    /// Which surface this came from, in the one form the column takes.
    ///
    /// Derived from visibility rather than passed in, and that holds only because
    /// ADR 0009 chose a chat thread for the private surface and a file for the
    /// shared one — <c>DumpFileKind.Private</c> exists in the schema and is unused
    /// for precisely that reason. A private *file* would make this mapping wrong,
    /// and the fix then is a surface on <c>ToolExecutionContext</c>, not a cleverer
    /// reading of this column.
    /// </summary>
    private static DataSource SourceOf(Visibility visibility) =>
        visibility == Visibility.PrivateUser ? DataSource.Chat : DataSource.UserInput;

    private static string Describe(ToolDate date) =>
        date.Local is { } local
            ? local.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : date.Instant!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>
    /// The comparison SPEC.md §44's "do not create a duplicate" needs: case,
    /// spacing and a trailing full stop are not differences of meaning.
    /// </summary>
    private static bool Same(string a, string b) =>
        string.Equals(Flatten(a), Flatten(b), StringComparison.Ordinal);

    private static string Flatten(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.');

    /// <summary>
    /// A subject key is only useful if two statements about one thing produce the
    /// same string, so casing and spacing are taken out of the model's hands.
    /// </summary>
    private static string? Normalise(string? subjectKey) =>
        subjectKey is null
            ? null
            : string.Join(' ', subjectKey.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A superseded memory's own words, short enough to sit in a report line.</summary>
    private static string Shorten(string content) =>
        content.Length > 120 ? content[..117] + "…" : content;

    private static MemoryType? ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "episodic" => MemoryType.Episodic,
        "semantic" => MemoryType.Semantic,
        "preference" => MemoryType.Preference,
        "decision" => MemoryType.Decision,
        "commitment" => MemoryType.Commitment,
        "plan" => MemoryType.Plan,
        "event" => MemoryType.Event,
        "temporary_context" => MemoryType.TemporaryContext,
        _ => null,
    };

    private static MemoryAssertion? ParseAssertion(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "user_stated" => MemoryAssertion.UserStated,
        "inferred" => MemoryAssertion.Inferred,
        _ => null,
    };

    private static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;
}
