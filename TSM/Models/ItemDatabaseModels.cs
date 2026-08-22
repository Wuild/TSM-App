namespace TSM.Models;

public sealed record BundledItemSummary(
    string Name,
    int Quality,
    int ClassId,
    int SubclassId,
    string IconSlug);

public sealed record BundledItemData(
    string Flavor,
    long Id,
    string Name,
    int Quality,
    string QualityType,
    int Level,
    int RequiredLevel,
    int ClassId,
    string ClassName,
    int SubclassId,
    string SubclassName,
    string InventoryType,
    string InventoryName,
    long SellPrice,
    int MaximumCount,
    bool IsEquippable,
    bool IsStackable,
    string IconSlug,
    string PreviewJson);

public sealed record ItemDatabaseQuery(
    string Flavor,
    string SearchText = "",
    int? Quality = null,
    int? ClassId = null,
    int? SubclassId = null,
    string InventoryType = "",
    int Limit = 100);
