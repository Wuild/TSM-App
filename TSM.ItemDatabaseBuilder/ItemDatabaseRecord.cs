using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSM.ItemDatabaseBuilder;

internal sealed record ItemDatabaseRecord(
    int SchemaVersion,
    string Flavor,
    string Namespace,
    string Region,
    string Locale,
    bool Found,
    long Id,
    DateTimeOffset FetchedAt,
    string Name,
    int Quality,
    string QualityType,
    int Level,
    int RequiredLevel,
    int ClassId,
    int SubclassId,
    string InventoryType,
    long SellPrice,
    int MaximumCount,
    bool IsEquippable,
    bool IsStackable,
    string IconUrl,
    [property: JsonPropertyName("item")] JsonElement Item,
    [property: JsonPropertyName("media")] JsonElement Media)
{
    public string Key => $"{Flavor}:{Id}";

    public static ItemDatabaseRecord Create(ItemCatalog catalog, string region, string locale, long id,
        JsonElement item, JsonElement media)
    {
        var iconUrl = string.Empty;
        if (media.TryGetProperty("assets", out var assets))
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("value", out var value))
                continue;
            var key = asset.TryGetProperty("key", out var keyValue) ? keyValue.GetString() : null;
            if (key is "icon" || iconUrl.Length == 0)
                iconUrl = value.GetString() ?? string.Empty;
            if (key == "icon")
                break;
        }

        return new ItemDatabaseRecord(
            1,
            catalog.Flavor,
            catalog.Namespace,
            region,
            locale,
            true,
            id,
            DateTimeOffset.UtcNow,
            Text(item, "name"),
            QualityNumber(NestedText(item, "quality", "type")),
            NestedText(item, "quality", "type"),
            Integer(item, "level"),
            Integer(item, "required_level"),
            NestedInteger(item, "item_class", "id"),
            NestedInteger(item, "item_subclass", "id"),
            NestedText(item, "inventory_type", "type"),
            Long(item, "sell_price"),
            Integer(item, "max_count"),
            Boolean(item, "is_equippable"),
            Boolean(item, "is_stackable"),
            iconUrl,
            item.Clone(),
            media.Clone());
    }

    public static ItemDatabaseRecord CreateMissing(ItemCatalog catalog, string region, string locale, long id)
    {
        using var empty = JsonDocument.Parse("{}");
        return new ItemDatabaseRecord(1, catalog.Flavor, catalog.Namespace, region, locale, false, id,
            DateTimeOffset.UtcNow, string.Empty, 0, string.Empty, 0, 0, 0, 0, string.Empty, 0, 0, false,
            false, string.Empty, empty.RootElement.Clone(), empty.RootElement.Clone());
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static int Integer(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : 0;

    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True;

    private static string NestedText(JsonElement element, string property, string nested) =>
        element.TryGetProperty(property, out var value) ? Text(value, nested) : string.Empty;

    private static int NestedInteger(JsonElement element, string property, string nested) =>
        element.TryGetProperty(property, out var value) ? Integer(value, nested) : 0;

    private static int QualityNumber(string quality) => quality.ToUpperInvariant() switch
    {
        "POOR" => 0,
        "COMMON" => 1,
        "UNCOMMON" => 2,
        "RARE" => 3,
        "EPIC" => 4,
        "LEGENDARY" => 5,
        "ARTIFACT" => 6,
        "HEIRLOOM" => 7,
        "WOW_TOKEN" => 8,
        _ => 1
    };
}
