using System.Text.Json;
using CoupleOS.Application.Tools;
using CoupleOS.Domain.Enums;
using Xunit;

namespace CoupleOS.UnitTests;

/// <summary>
/// The one tool that writes nothing, so almost everything worth asserting about
/// it is about what it refuses and what it hands back.
///
/// The refusals matter more here than in a writing tool. A malformed
/// create_shopping_item is a row that does not exist; a malformed
/// request_clarification is a block the pipeline tries to park with no question
/// on it, which the schema's dump_blocks_question_when_needs_input check rejects
/// — turning a model's sloppy question into a failed block and a lost line.
/// Validating here means the model gets told, and the block keeps its status.
/// </summary>
public sealed class RequestClarificationToolTests
{
    private static readonly RequestClarificationTool Tool = new();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ToolInvocation Invocation(string json) => new(
        Json(json),
        new ToolExecutionContext
        {
            CoupleId = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Visibility = Visibility.SharedCouple,
        });

    [Fact]
    public async Task It_composes_the_fragment_and_the_question_into_one_answerable_line()
    {
        // ADR 0009's own format, and composed here because this is the only place
        // that holds both halves: the model knows which fragment of a three-item
        // note is in doubt, and nothing downstream does.
        var execution = await Tool.ExecuteAsync(
            Invocation("""{"question":"when?","about":"book the dentist"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Equal("\"book the dentist\" — when?", execution.Question);

        // Nothing was created, and saying so is what keeps a question out of the
        // report's "added" list and out of dump_runs.entities_created.
        Assert.Null(execution.EntityType);
        Assert.Null(execution.EntityId);
    }

    [Fact]
    public async Task Asking_is_a_success_because_declining_is_the_thing_it_is_for()
    {
        // TOOLS.md 2a makes this the only legal way to decline. Recording it as a
        // failure would put an error beside the one decision the model got right,
        // and SPEC.md 46's rule runs in both directions: failure language over a
        // correct action misleads exactly as much.
        var execution = await Tool.ExecuteAsync(
            Invocation("""{"question":"who paid?","about":"dinner was 2400"}"""));

        Assert.Equal(ToolOutcome.Success, execution.Outcome);
        Assert.Null(execution.Error);
    }

    [Theory]
    [InlineData("""{"about":"dinner was 2400"}""", "question")]
    [InlineData("""{"question":"who paid?"}""", "about")]
    [InlineData("""{"question":"   ","about":"dinner was 2400"}""", "question")]
    [InlineData("""{"question":"who paid?","about":""}""", "about")]
    [InlineData("""{"question":42,"about":"dinner was 2400"}""", "question")]
    public async Task Both_halves_are_required_and_a_blank_one_is_not_a_half(string arguments, string missing)
    {
        var validation = await Tool.ValidateAsync(Json(arguments));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains($"'{missing}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_question_longer_than_the_column_is_refused_rather_than_truncated()
    {
        // 300 is TOOLS.md's limit and it is a limit on what a person will read on
        // a phone, not on what the column holds. Truncating would produce a
        // question with its own end missing, which is worse than being asked again.
        var validation = await Tool.ValidateAsync(
            Json($$"""{"question":"{{new string('x', 301)}}","about":"dinner"}"""));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Contains("300 characters", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_question_with_a_newline_in_it_is_flattened_to_one_line()
    {
        // It ends up as a markdown list item that the next run segments. A newline
        // inside it would end the item and turn the rest of the question into a
        // block of its own, which the next Process would read as fresh input and
        // send to a model.
        var execution = await Tool.ExecuteAsync(
            Invocation("""{"question":"who paid?\nyou or me?","about":"dinner\nwas 2400"}"""));

        Assert.Equal("\"dinner was 2400\" — who paid? you or me?", execution.Question);
    }

    [Fact]
    public void It_offers_the_model_no_vocabulary_it_must_not_have()
    {
        // The same property CreateShoppingItemTool has, asserted separately
        // because this schema was written by hand too. The dispatcher refuses
        // these arguments whatever a schema says; never offering the words is the
        // first line of defence (TOOLS.md rules 2 and 5).
        var properties = Tool.ParametersSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(["question", "about"], properties);
    }

    [Fact]
    public void It_needs_no_confirmation_because_it_changes_nothing()
    {
        Assert.Equal(ToolTier.None, Tool.Tier);
        Assert.True(Tool.IsIdempotent);
    }

    [Fact]
    public void The_description_tells_the_model_what_it_is_not_for()
    {
        // The failure mode that costs the most. A model reaching for this on
        // "saturday 8pm" turns a date the resolver handles into a question the
        // couple has to answer by hand — worse than the silence it replaced,
        // because it also looks like the system working.
        Assert.Contains("saturday 8pm", Tool.Description, StringComparison.OrdinalIgnoreCase);
    }
}
