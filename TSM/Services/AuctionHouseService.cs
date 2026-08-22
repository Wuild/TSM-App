using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

public sealed partial class AuctionHouseService(
    AppDataCatalogParser appDataParser,
    LuaSavedVariablesParser savedVariablesParser)
{
    public IReadOnlyList<AuctionHouseItemRow> BuildIndex(
        IEnumerable<TsmAccount> accounts,
        IEnumerable<WowInstallation> installations)
    {
        var accountList = accounts.ToArray();
        var names = BuildItemNameLookup(accountList);
        var sources = new Dictionary<string, AuctionSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accountList.Where(account => File.Exists(account.AppDataPath)))
            sources[account.AppDataPath] = new AuctionSource(account.GameVersion,
                ReadRegion(account.AppHelperSavedVariablesPath));
        foreach (var installation in installations)
        {
            var path = FindAppDataPath(installation.AddOnsPath);
            if (path is not null)
                sources[path] = sources.TryGetValue(path, out var existing)
                    ? existing with { Flavor = installation.Name }
                    : new AuctionSource(installation.Name, string.Empty);
        }

        var rows = new Dictionary<(string Flavor, string Realm, string Item), MutableRow>();
        foreach (var (path, source) in sources)
        {
            AppDataCatalog catalog;
            try
            {
                catalog = appDataParser.ParseFile(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var regionEntries = catalog.Entries
                .Where(entry => entry.Type == "AUCTIONDB_REGION_SALE")
                .ToArray();
            var selectedRegion = source.Region;
            if (string.IsNullOrWhiteSpace(selectedRegion))
            {
                var availableRegions = regionEntries.Select(entry => entry.Realm)
                    .Where(region => region.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (availableRegions.Length == 1)
                    selectedRegion = availableRegions[0];
            }
            var regionalSales = regionEntries
                .Where(entry => RegionKeysMatch(entry.Realm, selectedRegion))
                .SelectMany(ParseEntry)
                .GroupBy(row => BaseItemString(row.ItemString), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in catalog.Entries.Where(entry =>
                         entry.Type.StartsWith("AUCTIONDB_", StringComparison.Ordinal) &&
                         !entry.Type.StartsWith("AUCTIONDB_REGION_", StringComparison.Ordinal)))
            {
                foreach (var decoded in ParseEntry(entry))
                {
                    var key = (source.Flavor, entry.Realm, decoded.ItemString);
                    if (!rows.TryGetValue(key, out var current))
                        current = new MutableRow(source.Flavor, entry.Realm, decoded.ItemString);
                    current.Apply(decoded, entry.UpdatedAtUnix);
                    if (regionalSales.TryGetValue(BaseItemString(decoded.ItemString), out var regionalSale))
                        current.ApplyRegionalSale(regionalSale);
                    rows[key] = current;
                }
            }
        }

        return rows.Values.Select(row => row.ToRow(names.GetValueOrDefault(row.ItemString, row.ItemString)))
            .OrderBy(row => row.ItemName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Realm, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<DecodedAuctionRow> ParseEntry(AppDataEntry entry)
    {
        var metadata = DataMetadata().Match(entry.RawPayload);
        if (!metadata.Success)
            return [];
        var fields = QuotedValue().Matches(metadata.Groups["fields"].Value)
            .Select(match => match.Groups["value"].Value).ToArray();
        if (fields.Length < 2 || !string.Equals(fields[0], "itemString", StringComparison.Ordinal))
            return [];

        var result = new List<DecodedAuctionRow>();
        foreach (Match match in DataRow().Matches(metadata.Groups["data"].Value))
        {
            var tokens = match.Groups["values"].Value.Split(',');
            if (tokens.Length != fields.Length)
                continue;
            var rawItem = tokens[0].Trim().Trim('"');
            var itemString = rawItem.All(char.IsAsciiDigit) ? $"i:{rawItem}" : rawItem;
            if (itemString.Length == 0)
                continue;
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var index = 1; index < fields.Length; index++)
            {
                if (TryDecodeBase32(tokens[index].Trim(), out var value))
                    values[fields[index]] = value;
            }
            result.Add(new DecodedAuctionRow(itemString, values));
        }
        return result;
    }

    internal static bool TryDecodeBase32(string value, out long result)
    {
        result = 0;
        if (value.Length == 0)
            return false;
        try
        {
            foreach (var character in value)
            {
                var digit = character switch
                {
                    >= '0' and <= '9' => character - '0',
                    >= 'A' and <= 'V' => character - 'A' + 10,
                    >= 'a' and <= 'v' => character - 'a' + 10,
                    _ => -1
                };
                if (digit < 0)
                    return false;
                result = checked(result * 32 + digit);
            }
            return true;
        }
        catch (OverflowException)
        {
            result = 0;
            return false;
        }
    }

    private Dictionary<string, string> BuildItemNameLookup(IEnumerable<TsmAccount> accounts)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var account in accounts)
        {
            try
            {
                var document = savedVariablesParser.ParseFile(account.SavedVariablesPath);
                if (!document.TryGetTable("TSMItemInfoDB", out var database) ||
                    !database.TryGetValue("names", out var rawNames) ||
                    !database.TryGetValue("itemStrings", out var rawStrings))
                    continue;
                var names = FlattenChunks(rawNames).SelectMany(value => value.Split('\x02')).Where(value => value.Length > 0);
                var itemStrings = FlattenChunks(rawStrings).SelectMany(value => value.Split('\x02')).Where(value => value.Length > 0);
                foreach (var (itemString, name) in itemStrings.Zip(names))
                    result.TryAdd(itemString, name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
            }
        }
        return result;
    }

    private string ReadRegion(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        try
        {
            var document = savedVariablesParser.ParseFile(path);
            return document.TryGetTable("TradeSkillMaster_AppHelperDB", out var database) &&
                   database.TryGetString("region", out var region)
                ? region.Trim()
                : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
        {
            return string.Empty;
        }
    }

    private static string BaseItemString(string itemString)
    {
        if (!itemString.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
            return itemString;
        var separator = itemString.IndexOf(':', 2);
        return separator > 2 ? itemString[..separator] : itemString;
    }

    private static bool RegionKeysMatch(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(NormalizeRegionKey(left), NormalizeRegionKey(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRegionKey(string value) => string.Join('-', value
        .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Order(StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> FlattenChunks(object? value) => value switch
    {
        string text => [text],
        LuaTable table => table.Values.OfType<string>(),
        _ => []
    };

    private static string? FindAppDataPath(string addonsPath)
    {
        if (!Directory.Exists(addonsPath))
            return null;
        var helper = Directory.EnumerateDirectories(addonsPath).FirstOrDefault(directory =>
            string.Equals(Path.GetFileName(directory), "TradeSkillMaster_AppHelper", StringComparison.OrdinalIgnoreCase));
        if (helper is null)
            return null;
        var path = Path.Combine(helper, "AppData.lua");
        return File.Exists(path) ? path : null;
    }

    internal sealed record DecodedAuctionRow(string ItemString, IReadOnlyDictionary<string, long> Values);
    private sealed record AuctionSource(string Flavor, string Region);

    private sealed class MutableRow(string flavor, string realm, string itemString)
    {
        public string Flavor { get; } = flavor;
        public string Realm { get; } = realm;
        public string ItemString { get; } = itemString;
        private long? MinBuyout { get; set; }
        private long? NumAuctions { get; set; }
        private long? MarketValueRecent { get; set; }
        private long? Historical { get; set; }
        private long? RegionSaleAverage { get; set; }
        private long? RegionSoldPerDay { get; set; }
        private long? RegionSaleRatePercent { get; set; }
        private long UpdatedAtUnix { get; set; }

        public void Apply(DecodedAuctionRow row, long updatedAtUnix)
        {
            MinBuyout = Get(row, "minBuyout") ?? MinBuyout;
            NumAuctions = Get(row, "numAuctions") ?? NumAuctions;
            var recent = Get(row, "marketValueRecent");
            if (recent is not null)
                MarketValueRecent = recent;
            else if (MarketValueRecent is null)
                MarketValueRecent = Get(row, "marketValue");
            Historical = Get(row, "historical") ?? Get(row, "regionHistorical") ?? Historical;
            UpdatedAtUnix = Math.Max(UpdatedAtUnix, updatedAtUnix);
        }

        public void ApplyRegionalSale(DecodedAuctionRow row)
        {
            RegionSaleAverage = Get(row, "regionSale") ?? RegionSaleAverage;
            RegionSoldPerDay = Get(row, "regionSoldPerDay") ?? RegionSoldPerDay;
            RegionSaleRatePercent = Get(row, "regionSalePercent") ?? RegionSaleRatePercent;
        }

        public AuctionHouseItemRow ToRow(string name) => new(
            ItemString, name, Realm, Flavor, MinBuyout, NumAuctions, MarketValueRecent, Historical,
            UpdatedAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(UpdatedAtUnix) : DateTimeOffset.MinValue,
            RegionSaleAverage, RegionSoldPerDay, RegionSaleRatePercent);

        private static long? Get(DecodedAuctionRow row, string field) =>
            row.Values.TryGetValue(field, out var value) && value > 0 ? value : null;
    }

    [GeneratedRegex(@"fields=\{(?<fields>.*?)\},data=\{(?<data>.*)\}\}\]\]", RegexOptions.CultureInvariant)]
    private static partial Regex DataMetadata();

    [GeneratedRegex("\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValue();

    [GeneratedRegex(@"\{(?<values>[^{}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex DataRow();
}
