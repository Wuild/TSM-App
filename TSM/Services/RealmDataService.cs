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
        var accountList = accounts.ToArray();
        var installationList = (installations ?? []).ToArray();
        var statuses = new Dictionary<string, Dictionary<string, LocalStatus>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, LocalStatus> GetFlavorStatuses(string flavor)
        {
            if (!statuses.TryGetValue(flavor, out var flavorStatuses))
                statuses[flavor] = flavorStatuses = new(StringComparer.OrdinalIgnoreCase);
            return flavorStatuses;
        }

        foreach (var account in accountList.Where(account => !ForeverMarket.IsForever(account.GameVersion)))
        {
            try
            {
                var realms = ReadRealms(account.SavedVariablesPath);
                var region = ReadAppHelperRegion(account.AppHelperSavedVariablesPath).Split('-', 2)[0].ToUpperInvariant();
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

        foreach (var installation in installationList.Where(installation => !ForeverMarket.IsForever(installation.Name)))
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
            .Concat(GetForeverStatuses(accountList.Where(account => ForeverMarket.IsForever(account.GameVersion)),
                installationList.Where(installation => ForeverMarket.IsForever(installation.Name))))
            .OrderBy(row => row.RegionRealm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IEnumerable<RealmStatusRow> GetForeverStatuses(IEnumerable<TsmAccount> accounts,
        IEnumerable<WowInstallation> installations)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accountRegions = new HashSet<string>();
        var localMarkets = new HashSet<(string Region, ForeverMarket Market)>();
        foreach (var account in accounts)
        {
            if (File.Exists(account.AppDataPath))
                paths.Add(account.AppDataPath);
            try
            {
                var region = ForeverMarket.RegionCode(ReadAppHelperRegion(account.AppHelperSavedVariablesPath));
                if (region.Length > 0)
                    accountRegions.Add(region);
                var scopes = ReadScopes(account.SavedVariablesPath);
                foreach (var scope in ReadScopeValues(scopes, "factionrealm"))
                {
                    var separator = scope.IndexOf(" - ", StringComparison.Ordinal);
                    if (separator > 0 && ForeverMarket.TryParse(
                            $"{scope[(separator + 3)..]}-{scope[..separator]}", out var market))
                        localMarkets.Add((region, market));
                }
                foreach (var scope in ReadScopeValues(scopes, "realm"))
                {
                    if (ForeverMarket.TryParse(scope, out var market))
                        localMarkets.Add((region, market));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
            }
        }

        foreach (var installation in installations)
        {
            try
            {
                if (FindAppDataPath(installation.AddOnsPath) is { } path)
                    paths.Add(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var statuses = new Dictionary<(string Region, ForeverMarket Market), LocalStatus>();
        var dataRegions = new HashSet<string>();
        var accountRegion = accountRegions.Count == 1 ? accountRegions.Single() : string.Empty;
        foreach (var path in paths)
        {
            try
            {
                var catalog = appDataParser.ParseFile(path);
                var region = ForeverMarket.GetDataRegion(catalog, accountRegion);
                if (region.Length > 0)
                    dataRegions.Add(region);
                foreach (var entry in catalog.Entries.Where(entry => entry.Type is AuctionDataType or ShoppingDataType))
                {
                    if (!ForeverMarket.TryParse(entry.Realm, out var market))
                        continue;
                    var key = (region, market);
                    if (!statuses.TryGetValue(key, out var current))
                        statuses[key] = current = new LocalStatus();
                    if (entry.Type == AuctionDataType)
                        current.AuctionUpdated = Math.Max(current.AuctionUpdated, entry.UpdatedAtUnix);
                    else
                        current.ShoppingUpdated = Math.Max(current.ShoppingUpdated, entry.UpdatedAtUnix);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var regions = accountRegions.Concat(dataRegions).Distinct().Take(2).ToArray();
        var fallbackRegion = regions.Length == 1 ? regions[0] : string.Empty;
        var normalizedLocalMarkets = localMarkets.Select(key =>
            (Region: key.Region.Length == 0 ? fallbackRegion : key.Region, key.Market)).ToArray();
        var qualifiedRulesets = normalizedLocalMarkets.Concat(statuses.Keys)
            .Where(key => key.Market.Faction.Length > 0)
            .Select(key => (key.Region, key.Market.Ruleset))
            .ToHashSet();
        foreach (var key in normalizedLocalMarkets)
        {
            if (key.Market.Faction.Length > 0 || !qualifiedRulesets.Contains((key.Region, key.Market.Ruleset)))
                statuses.TryAdd(key, new LocalStatus());
        }

        return statuses.Select(pair => ToRow(pair.Key.Market.DisplayName(pair.Key.Region), "Forever", pair.Value)
            with { MarketRegion = pair.Key.Region, MarketKey = pair.Key.Market.Key });
    }

    private IReadOnlyList<string> ReadRealms(string path) =>
        ReadScopeValues(ReadScopes(path), "realm").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private LuaTable? ReadScopes(string path)
    {
        var document = savedVariablesParser.ParseFile(path);
        return document.TryGetTable("TradeSkillMasterDB", out var database) &&
               database.TryGetTable("_scopeKeys", out var scopes) ? scopes : null;
    }

    private static IEnumerable<string> ReadScopeValues(LuaTable? scopes, string scope) =>
        scopes is not null && scopes.TryGetTable(scope, out var values) ? values.Values.OfType<string>() : [];

    private string ReadAppHelperRegion(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        var document = savedVariablesParser.ParseFile(path);
        if (!document.TryGetTable("TradeSkillMaster_AppHelperDB", out var database) ||
            !database.TryGetString("region", out var region))
            return string.Empty;
        return region;
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
