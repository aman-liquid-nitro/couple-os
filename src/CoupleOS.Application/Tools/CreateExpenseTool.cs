using System.Globalization;
using System.Text.Json;
using CoupleOS.Application.Time;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;

namespace CoupleOS.Application.Tools;

/// <summary>
/// TOOLS.md 4 · SPEC.md §14. Money, which is the one thing here that has to be
/// exactly right rather than approximately right.
///
/// Three decisions are worth reading before changing anything.
///
/// **The payer is not defaulted to the speaker.** SPEC.md §14 says outright that an
/// ambiguous payer is asked about, so <c>paid_by: "unknown"</c> is a legitimate
/// model output and it writes a null <c>paid_by</c> — with the gap stated in the
/// change report. TOOLS.md says this "triggers a follow-up question"; it does not,
/// yet, and that is deliberate rather than missed. Asking parks the whole block
/// (an open question outranks a success), and STATUS debt 31 means answering it
/// re-hashes the line into a new block that re-runs every tool the first pass ran —
/// so asking would today turn one expense into two. Recording the amount with the
/// payer left blank loses nothing: the number is the part that is hard to
/// reconstruct a week later.
///
/// **The date is the couple's, not the server's.** <c>occurred_on</c> is a
/// <c>date</c> defaulting to <c>CURRENT_DATE</c>, which is the database host's
/// today — for a couple in Asia/Kolkata spending money at 3am, yesterday. So the
/// column is always written, from <see cref="ICoupleClock"/>.
///
/// **The category is looked up, never created.** The model chooses from the twelve
/// seeded names; anything else resolves to null and says so. Creating a category
/// row from a model's word would file a couple's spending under a heading nobody
/// chose, and it would be their heading forever.
/// </summary>
public sealed class CreateExpenseTool(
    IExpenseWriter writer,
    IExpenseCategoryLookup categories,
    IPartnerLookup partners,
    ICoupleClock clock) : ITool
{
    /// <summary>
    /// The twelve seeded names, offered as a closed set. Free text would produce a
    /// different spelling of "Dining" every week and a category column that cannot
    /// be grouped by, which is the one thing an expense category is for.
    /// </summary>
    private static readonly string[] SeededCategories =
    [
        "Groceries", "Dining", "Transport", "Utilities", "Rent", "Health",
        "Entertainment", "Shopping", "Travel", "Subscriptions", "Gifts", "Other",
    ];

    /// <summary>numeric(14,2) holds twelve digits before the point.</summary>
    private const decimal Largest = 999_999_999_999.99m;

    private static readonly string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"amount\":{\"type\":\"number\",\"exclusiveMinimum\":0,\"description\":\"How much, as a number: 2400 for '₹2400', 2500 for 'two and a half thousand'\"}," +
        "\"currency\":{\"type\":\"string\",\"description\":\"Three-letter code. Omit unless the person named a currency; the couple's default is INR.\"}," +
        "\"description\":{\"type\":\"string\",\"description\":\"What it was for, in the person's own words\",\"maxLength\":500}," +
        "\"category\":{\"type\":\"string\",\"enum\":[" +
        string.Join(",", SeededCategories.Select(c => $"\"{c}\"")) + "]}," +
        "\"merchant\":{\"type\":\"string\",\"description\":\"Where, if the person said\",\"maxLength\":200}," +
        "\"paid_by\":{\"type\":\"string\",\"enum\":[\"me\",\"partner\",\"unknown\"]," +
        // One example each side, for the pair that is actually confused.
        //
        // This description used to give an example for 'me' and none for
        // 'unknown', and the asymmetry did what an asymmetry does: "Dinner was
        // 2400." — a sentence with no person in it at all — came back as
        // paid_by: "me". Found by amb-002 on the first run in which it was ever
        // executed, which is the whole argument for M4.
        //
        // Deliberately not longer. M3 established that lengthening a description
        // can be strictly worse — spelling out all eight memory types made the
        // model omit the field entirely — and that the fix is to discriminate the
        // one pair being got wrong.
        "\"description\":\"'me' when the person says they spent or paid it — 'I spent 2400 on dinner' says 'me'. " +
        "'partner' when they say the other person did. 'unknown' when the note names nobody — " +
        "'Dinner was 2400' says 'unknown'. Never guess which of the two people it was.\"}," +
        "\"is_shared\":{\"type\":\"boolean\",\"description\":\"False for something bought for the other person, or purely personal\"}," +
        "\"occurred_expression\":{\"type\":\"string\"," +
        "\"description\":\"When it happened, copied exactly as the person wrote it: 'yesterday', 'on Friday'. Omit for today.\"," +
        "\"maxLength\":100}}," +
        "\"required\":[\"amount\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private readonly IExpenseWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private readonly IExpenseCategoryLookup _categories = categories ?? throw new ArgumentNullException(nameof(categories));
    private readonly IPartnerLookup _partners = partners ?? throw new ArgumentNullException(nameof(partners));
    private readonly ICoupleClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public string Name => "create_expense";

    public string Description =>
        "Record money spent. Call once per distinct purchase. A gift for the other person belongs " +
        "here too — write it from the private thread, and it stays private.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>
    /// Recording a number is not spending it. Nothing here moves money, so a
    /// confirmation prompt would train the couple to dismiss prompts.
    /// </summary>
    public ToolTier Tier => ToolTier.None;

    public bool IsIdempotent => true;

    public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!arguments.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number)
        {
            // Never a string. "₹2400" reaching the column would have to be parsed
            // here anyway, and a model that sends one has not read the schema — a
            // guess about which symbol it meant is a guess about money.
            errors.Add("'amount' is required and must be a number, with no currency symbol.");
        }
        else if (!amount.TryGetDecimal(out var value))
        {
            errors.Add("'amount' is not a number this can store exactly. Give it in units, without a symbol.");
        }
        else if (value <= 0)
        {
            // expenses_amount_check refuses this at the database. A refund is not a
            // negative expense in this schema, and inventing that meaning here would
            // make every total wrong in a way nobody could see.
            errors.Add("'amount' must be greater than zero.");
        }
        else if (value > Largest)
        {
            errors.Add($"'amount' must be {Largest.ToString("N2", CultureInfo.InvariantCulture)} or less.");
        }

        if (arguments.TryGetProperty("currency", out var currency))
        {
            var code = currency.ValueKind == JsonValueKind.String ? currency.GetString()?.Trim() : null;

            if (code is null || code.Length != 3 || !code.All(char.IsLetter))
            {
                errors.Add("'currency' must be a three-letter code such as INR or USD when supplied.");
            }
        }

        if (arguments.TryGetProperty("paid_by", out var paidBy) &&
            (paidBy.ValueKind != JsonValueKind.String ||
             paidBy.GetString()?.Trim().ToLowerInvariant() is not ("me" or "partner" or "unknown")))
        {
            errors.Add("'paid_by' must be 'me', 'partner' or 'unknown' when supplied.");
        }

        if (arguments.TryGetProperty("is_shared", out var isShared) &&
            isShared.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add("'is_shared' must be true or false when supplied.");
        }

        if (arguments.TryGetProperty("occurred_expression", out var occurred) &&
            occurred.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            errors.Add("'occurred_expression' must be a string when supplied, copied from what the person wrote.");
        }

        if (arguments.TryGetProperty("description", out var description) &&
            description.ValueKind == JsonValueKind.String &&
            description.GetString()!.Trim().Length > 500)
        {
            errors.Add("'description' must be 500 characters or fewer.");
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

        var amount = arguments.GetProperty("amount").GetDecimal();

        // Rounded here rather than by the database, which would truncate silently.
        // Money changed on the way in is money the person did not write, so the
        // rounding is stated — and it is a rounding rather than a refusal because
        // "a third of 2500" is a real note and 833.333… is not a real payment.
        var stored = Math.Round(amount, 2, MidpointRounding.AwayFromZero);

        if (stored != amount)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"rounded {amount} to {stored}, since money is kept to two decimal places"));
        }

        var paidBy = arguments.TryGetProperty("paid_by", out var paidByElement) && paidByElement.ValueKind == JsonValueKind.String
            ? paidByElement.GetString()!.Trim().ToLowerInvariant()
            : "me";

        Guid? payer = null;

        switch (paidBy)
        {
            case "me":
                payer = context.UserId;
                break;

            case "partner":
                payer = await _partners.FindPartnerAsync(context.CoupleId, context.UserId, cancellationToken);

                if (payer is null)
                {
                    // Refused rather than reattributed to the caller. "She paid" in a
                    // couple with one member is a note this system does not
                    // understand yet, and recording it as the speaker's spending
                    // would be a wrong number under the right heading.
                    return ToolExecution.Failed(
                        "The note says the other partner paid, and this couple has only one member yet, " +
                        "so there is nobody to attribute it to.");
                }

                break;

            default:
                // SPEC.md §14: an ambiguous payer is not defaulted to the speaker.
                // The row is still worth having, and the gap is said out loud.
                notes.Add("who paid is not recorded, because the note did not say");
                break;
        }

        var occurred = await ToolDate.ResolveAsync(_clock, Text(arguments, "occurred_expression"), cancellationToken);

        if (occurred.Failed)
        {
            return ToolExecution.Failed(occurred.Failure!);
        }

        DateOnly occurredOn;

        if (occurred.LocalDate is { } resolved)
        {
            occurredOn = resolved;

            // Only the day is kept, so the resolver's note about an assumed hour
            // would describe something this row does not hold.
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"\"{Text(arguments, "occurred_expression")}\" read as {occurredOn:d MMM yyyy}"));
        }
        else
        {
            var now = await _clock.NowAsync(cancellationToken);
            occurredOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now.Now, now.Zone).DateTime);
        }

        Guid? categoryId = null;

        if (Text(arguments, "category") is { } categoryName)
        {
            categoryId = await _categories.FindAsync(context.CoupleId, categoryName, cancellationToken);

            if (categoryId is null)
            {
                notes.Add($"no category called \"{categoryName}\" exists, so this is uncategorised");
            }
        }

        var expense = new Expense
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Amount = stored,
            Currency = (Text(arguments, "currency") ?? "INR").ToUpperInvariant(),
            CategoryId = categoryId,
            Description = Text(arguments, "description"),
            Merchant = Text(arguments, "merchant"),
            PaidBy = payer,

            // Shared unless the person said otherwise, which is the schema's default
            // and the right one for a couple: the exception is a gift, and a gift
            // written in the private thread is already private by surface.
            IsShared = !arguments.TryGetProperty("is_shared", out var isShared) ||
                       isShared.ValueKind != JsonValueKind.False,
            OccurredOn = occurredOn,
        };

        await _writer.AddAsync(expense, cancellationToken);

        return ToolExecution.Created(
            "expense",
            expense.Id,
            notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private static string? Text(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!.Trim()
            : null;
}
