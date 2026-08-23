using System.Text.Json;

namespace TSM.ItemDatabaseBuilder;

internal static class AuctionItemFilter
{
    private static readonly HashSet<int> ExcludedClasses = [10, 12, 13, 14];
    private static readonly HashSet<string> ExcludedBindings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ON_ACQUIRE",
        "QUEST",
        "TO_BNETACCOUNT",
        "TO_ACCOUNT"
    };

    public static bool IsSearchCandidate(JsonElement item)
    {
        var quality = NestedInteger(item, "quality", "id");
        if (quality.HasValue && quality.Value == 0)
            return false;
        var qualityType = NestedText(item, "quality", "type");
        if (string.Equals(qualityType, "POOR", StringComparison.OrdinalIgnoreCase))
            return false;
        var classId = NestedInteger(item, "item_class", "id");
        return !classId.HasValue || !ExcludedClasses.Contains(classId.Value);
    }

    public static bool IsAuctionRelevant(JsonElement item)
    {
        if (!IsSearchCandidate(item))
            return false;
        var preview = item.TryGetProperty("preview_item", out var value) ? value : default;
        var binding = NestedText(preview, "binding", "type");
        return string.IsNullOrWhiteSpace(binding) || !ExcludedBindings.Contains(binding);
    }

    private static int? NestedInteger(JsonElement element, string property, string nested)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(nested, out var result) ||
            !result.TryGetInt32(out var number))
            return null;
        return number;
    }

    private static string NestedText(JsonElement element, string property, string nested)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(nested, out var result) ||
            result.ValueKind != JsonValueKind.String)
            return string.Empty;
        return result.GetString() ?? string.Empty;
    }
}
