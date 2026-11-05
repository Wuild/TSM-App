namespace TSM.Models;

internal readonly record struct ForeverMarket(string Ruleset, string Faction)
{
    private static readonly string[] Rulesets = ["Normal", "PvP", "Roleplay", "Roleplay PvP", "Hardcore"];
    private static readonly string[] Factions = ["Alliance", "Horde", "Neutral"];

    public string Key => Faction.Length == 0 ? Ruleset : $"{Ruleset}-{Faction}";

    public string DisplayName(string region)
    {
        var name = Faction.Length == 0 ? Ruleset : $"{Ruleset} · {Faction}";
        return region.Length == 0 ? name : $"{region} · {name}";
    }

    public static bool IsForever(string flavor) => string.Equals(flavor, "Forever", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string key, out ForeverMarket market)
    {
        var separator = key.LastIndexOf('-');
        var ruleset = separator < 0 ? key : key[..separator];
        var canonicalRuleset = Rulesets.FirstOrDefault(value =>
            string.Equals(value, ruleset, StringComparison.OrdinalIgnoreCase));
        var faction = separator < 0 ? string.Empty : Factions.FirstOrDefault(value =>
            string.Equals(value, key[(separator + 1)..], StringComparison.OrdinalIgnoreCase));
        if (canonicalRuleset is null || faction is null)
        {
            market = default;
            return false;
        }
        market = new ForeverMarket(canonicalRuleset, faction);
        return true;
    }

    public static string RegionCode(string key) => key.Split('-', StringSplitOptions.TrimEntries)
        .Select(part => part.ToUpperInvariant())
        .FirstOrDefault(part => part is "US" or "EU" or "KR" or "TW" or "CN") ?? string.Empty;

    public static string GetDataRegion(AppDataCatalog catalog, string accountRegion)
    {
        var regions = catalog.Entries.Select(entry => entry.Realm)
            .Where(key => key.StartsWith("Forever-", StringComparison.OrdinalIgnoreCase))
            .Select(RegionCode)
            .Where(region => region.Length > 0)
            .Distinct()
            .ToArray();
        return regions.Length == 0 ? accountRegion : regions.Length == 1 ? regions[0] : string.Empty;
    }
}
