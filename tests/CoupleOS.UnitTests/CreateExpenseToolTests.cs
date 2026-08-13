using CoupleOS.Application.Tools;
using CoupleOS.Domain.Entities;
using CoupleOS.Domain.Enums;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// Money, where the interesting cases are all about what the tool refuses to
/// decide: who paid when the note does not say, what day it was when the server
/// disagrees with the couple, and which category a name it does not recognise
/// belongs to.
/// </summary>
public sealed class CreateExpenseToolTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid PartnerId = Guid.NewGuid();
    private static readonly Guid DiningId = Guid.NewGuid();

    private sealed class RecordingExpenseWriter : IExpenseWriter
    {
        public List<Expense> Written { get; } = [];

        public Task AddAsync(Expense expense, CancellationToken cancellationToken = default)
        {
            Written.Add(expense);

            return Task.CompletedTask;
        }
    }

    /// <summary>Knows one name, so the miss can be asserted as well as the hit.</summary>
    private sealed class StubCategories : IExpenseCategoryLookup
    {
        public Task<Guid?> FindAsync(Guid coupleId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(name.Equals("Dining", StringComparison.OrdinalIgnoreCase) ? DiningId : (Guid?)null);
    }

    private static (CreateExpenseTool Tool, RecordingExpenseWriter Writer) Build(Guid? partner = null)
    {
        var writer = new RecordingExpenseWriter();

        return (
            new CreateExpenseTool(writer, new StubCategories(), new StubPartnerLookup(partner), new FixedCoupleClock()),
            writer);
    }

    private static ToolInvocation Call(string json, Visibility visibility = Visibility.SharedCouple) =>
        Invocation(json, CoupleId, UserId, visibility);

    [Fact]
    public async Task A_bare_amount_is_recorded_as_spent_today_by_the_person_who_wrote_it()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"amount":2400,"description":"dinner","category":"Dining"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Equal("expense", execution.EntityType);

        var expense = Assert.Single(writer.Written);
        Assert.Equal(2400m, expense.Amount);
        Assert.Equal("INR", expense.Currency);
        Assert.Equal(DiningId, expense.CategoryId);
        Assert.Equal(UserId, expense.PaidBy);
        Assert.True(expense.IsShared);

        // The couple's today, not the server's. FixedCoupleClock says 10:00 on
        // 13 August 2026 in Asia/Kolkata.
        Assert.Equal(new DateOnly(2026, 8, 13), expense.OccurredOn);

        Assert.Null(execution.Note);
    }

    [Fact]
    public async Task An_unknown_payer_is_left_unrecorded_and_said_out_loud()
    {
        // SPEC.md §14: if the payer is ambiguous, ask. Asking parks the whole block
        // and STATUS debt 31 would then duplicate the expense when the line is
        // edited to answer, so the row is written with the gap stated instead — the
        // amount is the part that is hard to reconstruct a week later.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(Call("""{"amount":800,"paid_by":"unknown"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);

        var expense = Assert.Single(writer.Written);
        Assert.Null(expense.PaidBy);
        Assert.Contains("who paid is not recorded", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_partner_paying_resolves_to_the_partner()
    {
        var (tool, writer) = Build(PartnerId);

        await tool.ExecuteAsync(Call("""{"amount":1200,"paid_by":"partner"}"""));

        Assert.Equal(PartnerId, Assert.Single(writer.Written).PaidBy);
    }

    [Fact]
    public async Task The_partner_paying_in_a_couple_of_one_is_refused_rather_than_reattributed()
    {
        // Recording it as the speaker's spending would be a wrong number under the
        // right heading, which is the shape of error nobody ever finds.
        var (tool, writer) = Build(partner: null);

        var execution = await tool.ExecuteAsync(Call("""{"amount":1200,"paid_by":"partner"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Empty(writer.Written);
        Assert.Contains("only one member", execution.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_date_the_person_gave_is_kept_as_a_day_and_the_reading_is_stated()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"amount":300,"occurred_expression":"yesterday"}"""));

        var expense = Assert.Single(writer.Written);
        Assert.Equal(new DateOnly(2026, 8, 12), expense.OccurredOn);
        Assert.Contains("12 Aug 2026", execution.Note!, StringComparison.Ordinal);

        // A day has no hour, so the resolver's assumed 9am must not be mentioned on
        // a row that does not hold it.
        Assert.DoesNotContain("9am", execution.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_date_that_cannot_be_read_writes_nothing()
    {
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"amount":300,"occurred_expression":"a while back"}"""));

        Assert.Equal(ToolOutcome.ExecutionFailed, execution.Outcome);
        Assert.Empty(writer.Written);
    }

    [Fact]
    public async Task A_category_nobody_has_is_recorded_as_uncategorised_rather_than_created()
    {
        // Creating it would file the couple's spending under a heading nobody chose,
        // and it would be their heading forever.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(
            Call("""{"amount":500,"category":"Fireworks"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Null(Assert.Single(writer.Written).CategoryId);
        Assert.Contains("uncategorised", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_third_decimal_place_is_rounded_and_the_rounding_is_reported()
    {
        // Money changed on the way in is money the person did not write. Rounded
        // rather than refused, because "a third of 2500" is a real note and
        // 833.333… is not a real payment — and stated, because it is their money.
        var (tool, writer) = Build();

        var execution = await tool.ExecuteAsync(Call("""{"amount":833.335}"""));

        Assert.Equal(833.34m, Assert.Single(writer.Written).Amount);
        Assert.Contains("rounded", execution.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gift_written_privately_is_private_and_not_shared()
    {
        // SPEC.md §19's case, and the two flags are independent: visibility comes
        // from the surface and is_shared is about whose expense it is.
        var (tool, writer) = Build();

        await tool.ExecuteAsync(
            Call("""{"amount":15000,"description":"necklace","category":"Gifts","is_shared":false}""",
                Visibility.PrivateUser));

        var expense = Assert.Single(writer.Written);
        Assert.Equal(Visibility.PrivateUser, expense.Visibility);
        Assert.Equal(UserId, expense.OwnerUserId);
        Assert.False(expense.IsShared);
    }

    [Fact]
    public async Task A_named_currency_is_upper_cased_and_an_absent_one_defaults()
    {
        var (tool, writer) = Build();

        await tool.ExecuteAsync(Call("""{"amount":40,"currency":"usd"}"""));

        Assert.Equal("USD", Assert.Single(writer.Written).Currency);
    }

    [Theory]
    [InlineData("""{"description":"dinner"}""", "'amount'")]
    [InlineData("""{"amount":"2400"}""", "'amount'")]
    [InlineData("""{"amount":0}""", "'amount'")]
    [InlineData("""{"amount":-50}""", "'amount'")]
    [InlineData("""{"amount":1000000000000.00}""", "'amount'")]
    [InlineData("""{"amount":10,"currency":"rupees"}""", "'currency'")]
    [InlineData("""{"amount":10,"paid_by":"her"}""", "'paid_by'")]
    [InlineData("""{"amount":10,"is_shared":"no"}""", "'is_shared'")]
    public async Task What_cannot_be_stored_exactly_is_refused_before_it_is_stored(string arguments, string expected)
    {
        var (tool, _) = Build();

        var validation = await tool.ValidateAsync(Json(arguments));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void The_schema_offers_the_twelve_seeded_categories_and_no_free_text()
    {
        var (tool, _) = Build();

        var category = tool.ParametersSchema.GetProperty("properties").GetProperty("category");
        var allowed = category.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToList();

        Assert.Equal(12, allowed.Count);
        Assert.Contains("Dining", allowed);
        Assert.Contains("Gifts", allowed);
    }
}
