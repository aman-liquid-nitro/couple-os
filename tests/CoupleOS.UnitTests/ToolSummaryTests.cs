using CoupleOS.Application.AI;
using CoupleOS.Application.Tools;
using Xunit;
using static CoupleOS.UnitTests.ToolArguments;

namespace CoupleOS.UnitTests;

/// <summary>
/// The one line a person reads to know what a call was about.
///
/// Written after a browser run showed <i>"create memory: user_stated"</i> — the old
/// rule took the first string argument, and <c>create_memory</c> emits
/// <c>assertion</c> before <c>content</c>. The order a model emits properties in is
/// the model's business, so these cases put the subject last on purpose.
/// </summary>
public sealed class ToolSummaryTests
{
    private static LlmToolCall Call(string name, string json) => new(name, Json(json));

    [Theory]
    [InlineData("create_memory", """{"assertion":"user_stated","type":"preference","content":"Likes Italian food"}""", "Likes Italian food")]
    [InlineData("create_task", """{"priority":"high","title":"Call the plumber"}""", "Call the plumber")]
    [InlineData("create_shopping_item", """{"quantity":"2 kg","name":"detergent"}""", "detergent")]
    [InlineData("search_memory", """{"limit":5,"query":"italian food"}""", "italian food")]
    [InlineData("create_event", """{"category":"family","title":"Dinner at Priya's parents"}""", "Dinner at Priya's parents")]
    public void The_subject_is_named_whatever_order_the_model_emitted_it_in(
        string tool,
        string json,
        string expected) =>
        Assert.Equal(expected, ToolSummary.Of(Call(tool, json)));

    [Fact]
    public void A_number_reads_as_a_figure_when_it_is_all_there_is()
    {
        // create_expense requires only the amount. "2400" is a worse line than
        // "dinner" and a much better one than {"amount":2400}.
        Assert.Equal("2400", ToolSummary.Of(Call("create_expense", """{"amount":2400}""")));
    }

    [Fact]
    public void The_description_is_preferred_over_a_bare_amount()
    {
        Assert.Equal(
            "dinner",
            ToolSummary.Of(Call("create_expense", """{"amount":2400,"description":"dinner"}""")));
    }

    [Fact]
    public void A_call_naming_nothing_recognisable_falls_back_rather_than_failing()
    {
        // A tool added later, whose subject argument nobody listed. The fallback is
        // the old behaviour, which is wrong-ish rather than broken — and raw JSON
        // in front of a person is the thing worth avoiding.
        Assert.Equal("weekly", ToolSummary.Of(Call("future_tool", """{"cadence":"weekly"}""")));
    }

    [Fact]
    public void Arguments_that_are_not_an_object_are_not_dressed_up_as_one()
    {
        Assert.Equal("[]", ToolSummary.Of(new LlmToolCall("odd", Json("[]"))));
    }
}
