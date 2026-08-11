using System.Text.Json;
using CoupleOS.Domain.Entities;

namespace CoupleOS.Application.Tools;

/// <summary>
/// SPEC.md 12. The first real tool, and the template for the rest.
///
/// Note what it does NOT take: couple_id, owner_user_id and visibility are
/// absent from the schema entirely, so the model has no vocabulary for them.
/// The dispatcher refuses them as well, but the first line of defence is simply
/// never offering the words.
/// </summary>
public sealed class CreateShoppingItemTool(IShoppingItemWriter writer) : ITool
{
    private const string SchemaJson =
        "{\"type\":\"object\"," +
        "\"properties\":{" +
        "\"name\":{\"type\":\"string\",\"description\":\"The item, singular, lowercase\",\"maxLength\":200}," +
        "\"quantity\":{\"type\":\"string\",\"description\":\"Optional quantity as written\",\"maxLength\":50}}," +
        "\"required\":[\"name\"]}";

    private static readonly JsonElement Schema = JsonDocument.Parse(SchemaJson).RootElement.Clone();

    private readonly IShoppingItemWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public string Name => "create_shopping_item";

    public string Description => "Add one item to the couple's shopping list. Call once per distinct item.";

    public JsonElement ParametersSchema => Schema;

    /// <summary>Cheap and reversible; nobody wants a confirmation prompt for detergent.</summary>
    public ToolTier Tier => ToolTier.None;

    public bool IsIdempotent => true;

    public ValueTask<ToolValidation> ValidateAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        if (!arguments.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            // Rule 1: never invent a value. An item with no name is a
            // clarifying question, not a row called "unknown".
            errors.Add("'name' is required and must be a non-empty string.");
        }
        else if (nameElement.GetString()!.Length > 200)
        {
            errors.Add("'name' must be 200 characters or fewer.");
        }

        if (arguments.TryGetProperty("quantity", out var quantityElement) &&
            quantityElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            // "2 kg" and "a couple" are both valid; a number is not, because the
            // unit would be lost.
            errors.Add("'quantity' must be a string when supplied.");
        }

        return ValueTask.FromResult(errors.Count == 0
            ? ToolValidation.Valid
            : new ToolValidation(false, errors));
    }

    public async Task<ToolExecution> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken = default)
    {
        var name = invocation.Arguments.GetProperty("name").GetString()!.Trim();

        var quantity = invocation.Arguments.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.String
            ? q.GetString()?.Trim()
            : null;

        var context = invocation.Context;

        var item = new ShoppingItem
        {
            CoupleId = context.CoupleId,
            OwnerUserId = context.Visibility == Domain.Enums.Visibility.PrivateUser ? context.UserId : null,
            Visibility = context.Visibility,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = string.IsNullOrWhiteSpace(quantity) ? null : quantity,
            AddedBy = context.UserId,
        };

        await _writer.AddAsync(item, cancellationToken);

        return ToolExecution.Created("shopping_item", item.Id);
    }
}
