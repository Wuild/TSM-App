using TSM.Models;

namespace TSM.Services;

public sealed class RealmDataService(
    LuaSavedVariablesParser savedVariablesParser,
    AppDataCatalogParser appDataParser)
{
    private const string AuctionDataType = "AUCTIONDB_NON_COMMODITY_DATA";
    private const string ShoppingDataType = "SHOPPING_SEARCHES";

    public IReadOnlyList<RealmStatusRow> GetRealmStatuses(
        IEnumerable<TsmAccount> accounts,
        IEnumerable<WowInstallation>? installations = null)
    {
        var statuses = new Dictionary<string, Dictionary<string, LocalStatus>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LocalStatus> GetFlavorStatuses(string flavor)
        {
            if (!statuses.TryGetValue(flavor, out var flavorStatuses))
                statuses[flavor] = flavorStatuses = new(StringComparer.OrdinalIgnoreCase);
            return flavorStatuses;
        }

        foreach (var account in accounts)
        {
            try
            {
                var realms = ReadRealms(account.SavedVariablesPath);
                var region = ReadRegion(account.AppHelperSavedVariablesPath);
                var appData = File.Exists(account.AppDataPath)
                    ? appDataParser.ParseFile(account.AppDataPath)
                    : new AppDataCatalog([]);
                var flavorStatuses = GetFlavorStatuses(account.GameVersion);

                foreach (var realm in realms)
                {
                    var key = string.IsNullOrWhiteSpace(region) ? realm : $"{region}-{realm}";
                    var auctionUpdated = LastUpdateForRealm(appData, AuctionDataType, realm);
                    var shoppingUpdated = LastUpdateForRealm(appData, ShoppingDataType, realm);
                    if (!flavorStatuses.TryGetValue(key, out var current))
                        current = new LocalStatus();
                    current.AuctionUpdated = Math.Max(current.AuctionUpdated, auctionUpdated);
                    current.ShoppingUpdated = Math.Max(current.ShoppingUpdated, shoppingUpdated);
                    flavorStatuses[key] = current;
                }

                foreach (var entry in appData.Entries.Where(entry =>
                             entry.Type is AuctionDataType or ShoppingDataType && entry.Realm.Length > 0))
                {
                    var realm = NormalizeDataRealm(entry.Realm);
                    var key = string.IsNullOrWhiteSpace(region) ? realm : $"{region}-{realm}";
                    if (!flavorStatuses.TryGetValue(key, out var current))
                        current = new LocalStatus();
                    if (entry.Type == AuctionDataType)
                        current.AuctionUpdated = Math.Max(current.AuctionUpdated, entry.UpdatedAtUnix);
                    else
                        current.ShoppingUpdated = Math.Max(current.ShoppingUpdated, entry.UpdatedAtUnix);
                    flavorStatuses[key] = current;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
            }
        }

        foreach (var installation in installations ?? [])
        {
            try
            {
                var appDataPath = FindAppDataPath(installation.AddOnsPath);
                if (appDataPath is null)
                    continue;
                var appData = appDataParser.ParseFile(appDataPath);
                var flavorStatuses = GetFlavorStatuses(installation.Name);
                foreach (var entry in appData.Entries.Where(entry =>
                             entry.Type is AuctionDataType or ShoppingDataType && entry.Realm.Length > 0))
                {
                    var realm = NormalizeDataRealm(entry.Realm);
                    var key = flavorStatuses.Keys.FirstOrDefault(existing =>
                                  string.Equals(existing, realm, StringComparison.OrdinalIgnoreCase) ||
                                  existing.EndsWith("-" + realm, StringComparison.OrdinalIgnoreCase))
                              ?? realm;
                    if (!flavorStatuses.TryGetValue(key, out var current))
                        current = new LocalStatus();
                    if (entry.Type == AuctionDataType)
                        current.AuctionUpdated = Math.Max(current.AuctionUpdated, entry.UpdatedAtUnix);
                    else
                        current.ShoppingUpdated = Math.Max(current.ShoppingUpdated, entry.UpdatedAtUnix);
                    flavorStatuses[key] = current;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return statuses.SelectMany(flavor => flavor.Value.Select(pair => ToRow(pair.Key, flavor.Key, pair.Value)))
            .OrderBy(row => row.RegionRealm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> ReadRealms(string path)
    {
        var document = savedVariablesParser.ParseFile(path);
        if (!document.TryGetTable("TradeSkillMasterDB", out var database) ||
            !database.TryGetTable("_scopeKeys", out var scopeKeys) ||
            !scopeKeys.TryGetTable("realm", out var realms))
            return [];
        return realms.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string ReadRegion(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        var document = savedVariablesParser.ParseFile(path);
        if (!document.TryGetTable("TradeSkillMaster_AppHelperDB", out var database) ||
            !database.TryGetString("region", out var region))
            return string.Empty;
        return region.Split('-', 2)[0].ToUpperInvariant();
    }

    private static RealmStatusRow ToRow(string name, string gameVersion, LocalStatus status)
    {
        var lastUpdated = Math.Max(status.AuctionUpdated, status.ShoppingUpdated);
        return new RealmStatusRow(
            name,
            status.AuctionUpdated > 0 ? "Available" : "Missing",
            status.ShoppingUpdated > 0 ? "Available" : "Missing",
            FormatTimestamp(lastUpdated),
            status.AuctionUpdated > 0 ? "#51CF92" : "#F5A623",
            gameVersion);
    }

    private static long LastUpdateForRealm(AppDataCatalog catalog, string type, string localRealm) =>
        catalog.Entries
            .Where(entry => string.Equals(entry.Type, type, StringComparison.Ordinal) &&
                            (string.Equals(entry.Realm, localRealm, StringComparison.OrdinalIgnoreCase) ||
                             entry.Realm.StartsWith(localRealm + "-", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.UpdatedAtUnix)
            .DefaultIfEmpty(0)
            .Max();

    private static string NormalizeDataRealm(string value)
    {
        foreach (var suffix in new[] { "-Alliance", "-Horde", "-Neutral" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return value[..^suffix.Length];
        }
        return value;
    }

    private static string? FindAppDataPath(string addonsPath)
    {
        if (!Directory.Exists(addonsPath))
            return null;
        var helper = Directory.EnumerateDirectories(addonsPath).FirstOrDefault(directory =>
            string.Equals(Path.GetFileName(directory), "TradeSkillMaster_AppHelper",
                StringComparison.OrdinalIgnoreCase));
        if (helper is null)
            return null;
        var path = Path.Combine(helper, "AppData.lua");
        return File.Exists(path) ? path : null;
    }

    private static string FormatTimestamp(long timestamp)
    {
        if (timestamp <= 0)
            return "Never";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("g");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Invalid timestamp";
        }
    }

    private sealed class LocalStatus
    {
        public long AuctionUpdated { get; set; }
        public long ShoppingUpdated { get; set; }
    }
}
