using System.Globalization;
using System.Text;
using TSM.Models;

namespace TSM.Services;

public sealed class TsmDataUploadService(
    ITsmApiClient apiClient,
    LuaSavedVariablesParser parser,
    Func<string> systemId)
{
    public async Task<int> UploadAsync(IReadOnlyList<TsmAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        var uploaded = 0;
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(account.SavedVariablesPath) || !File.Exists(account.AppHelperSavedVariablesPath))
                continue;
            var tsmDocument = parser.ParseFile(account.SavedVariablesPath);
            var helperDocument = parser.ParseFile(account.AppHelperSavedVariablesPath);
            if (!tsmDocument.TryGetTable("TradeSkillMasterDB", out var tsm) ||
                !helperDocument.TryGetTable("TradeSkillMaster_AppHelperDB", out var helper) ||
                !helper.TryGetString("region", out var region) || string.IsNullOrWhiteSpace(region))
                continue;

            uploaded += await UploadAuctionScansAsync(account, region, tsm, cancellationToken);
            uploaded += await UploadAccountingAsync(account, region, tsm, cancellationToken);
            uploaded += await UploadHelperDataAsync(account, region, helper, cancellationToken);
            var timestamp = new FileInfo(account.SavedVariablesPath).LastWriteTimeUtc.ToUnixTimeSeconds();
            uploaded += await UploadIfNewAsync("sv", [region, Encode(account.AccountName)], timestamp,
                new Dictionary<string, object?>
                {
                    ["tsmDB"] = PrettifyTsm(tsm),
                    ["appHelperDB"] = ToJsonValue(helper),
                    ["timestamp"] = timestamp,
                    ["systemId"] = systemId()
                }, true, cancellationToken);
        }
        return uploaded;
    }

    private async Task<int> UploadAuctionScansAsync(TsmAccount account, string region, LuaTable database,
        CancellationToken cancellationToken)
    {
        if (!database.TryGetTable("_scopeKeys", out var scopes) ||
            !scopes.TryGetTable("factionrealm", out var factionRealms))
            return 0;
        var uploaded = 0;
        foreach (var factionRealm in factionRealms.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!database.TryGetString($"f@{factionRealm}@internalData@csvAuctionDBScan", out var csv) ||
                !database.TryGetValue($"f@{factionRealm}@internalData@auctionDBScanTime", out var rawTime))
                continue;
            var timestamp = ConvertNumber(rawTime);
            var records = ParseCsv(csv);
            if (timestamp <= 0 || records.Count < 2)
                continue;
            var header = records[0].Select((name, index) => (name, index))
                .ToDictionary(entry => entry.name, entry => entry.index, StringComparer.OrdinalIgnoreCase);
            var items = new Dictionary<string, object?>();
            foreach (var row in records.Skip(1))
            {
                var item = Text(row, header, "itemString").Split(':');
                if (item.Length != 2 || item[0] != "i" || !long.TryParse(item[1], out var itemId))
                    continue;
                items[itemId.ToString(CultureInfo.InvariantCulture)] = new Dictionary<string, long>
                {
                    ["min_buyout"] = Number(row, header, "minBuyout"),
                    ["market_value"] = (long)Math.Floor(RealNumber(row, header, "marketValue")),
                    ["num_auctions"] = Number(row, header, "numAuctions"),
                    ["quantity"] = Number(row, header, "quantity")
                };
            }
            if (items.Count == 0)
                continue;
            var separator = factionRealm.IndexOf(" - ", StringComparison.Ordinal);
            var faction = separator > 0 ? factionRealm[..separator] : string.Empty;
            var realm = separator > 0 ? factionRealm[(separator + 3)..] : factionRealm;
            if (!account.GameVersion.StartsWith("Retail", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(account.GameVersion, "PTR", StringComparison.OrdinalIgnoreCase))
                realm += $"-{faction}";
            uploaded += await UploadIfNewAsync("auctiondb_scan", [region, Encode(realm)], timestamp,
                new Dictionary<string, object?> { ["items"] = items, ["last_modified"] = timestamp }, false,
                cancellationToken);
        }
        return uploaded;
    }

    private async Task<int> UploadAccountingAsync(TsmAccount account, string region, LuaTable database,
        CancellationToken cancellationToken)
    {
        if (!apiClient.SupportsEndpoint("sales") ||
            !database.TryGetTable("_scopeKeys", out var scopes) || !scopes.TryGetTable("realm", out var realms))
            return 0;
        var uploaded = 0;
        foreach (var realm in realms.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = new[] { region, Encode(realm), Encode(account.AccountName) };
            var lastUpload = await apiClient.GetLastUploadAsync("sales", path, cancellationToken);
            var rows = new List<object[]>();
            CollectAccountingRows(database, realm, "csvSales", "saveTimeSales", 2, true, lastUpload, rows);
            CollectAccountingRows(database, realm, "csvBuys", "saveTimeBuys", 3, true, lastUpload, rows);
            CollectAccountingRows(database, realm, "csvExpired", "saveTimeExpires", 4, false, lastUpload, rows);
            CollectAccountingRows(database, realm, "csvCancelled", "saveTimeCancels", 5, false, lastUpload, rows);
            if (rows.Count == 0)
                continue;
            await apiClient.UploadAsync(new TsmUploadRequest("sales", path, rows), cancellationToken);
            uploaded++;
        }
        return uploaded;
    }

    private async Task<int> UploadHelperDataAsync(TsmAccount account, string region, LuaTable helper,
        CancellationToken cancellationToken)
    {
        var uploaded = 0;
        if (helper.TryGetTable("blackMarket", out var blackMarket))
        foreach (var entry in blackMarket.Entries.Where(entry => entry.Value is LuaTable))
        {
            var data = (LuaTable)entry.Value!;
            var timestamp = Number(data, "updateTime");
            uploaded += await UploadIfNewAsync("black_market", [region, Encode(entry.Key.ToString()!)], timestamp,
                ToJsonValue(data)!, false, cancellationToken);
        }
        uploaded += await UploadAccountCollectionAsync(account, helper, "analytics", "analytics", cancellationToken);
        uploaded += await UploadAccountCollectionAsync(account, helper, "errorReports", "error_report", cancellationToken);

        if (helper.TryGetTable("shoppingMaxPrices", out var shopping))
        foreach (var entry in shopping.Entries.Where(entry => entry.Value is LuaTable))
        {
            var profile = entry.Value as LuaTable;
            var timestamp = Number(profile!, "updateTime");
            var payload = new Dictionary<string, object?>
            {
                ["updateTime"] = timestamp,
                ["data"] = profile!.Entries.Where(value => !Equals(value.Key, "updateTime"))
                    .ToDictionary(value => value.Key.ToString() ?? string.Empty,
                        value => ToJsonValue(value.Value), StringComparer.Ordinal),
                ["profiles"] = shopping.Entries.Select(value => value.Key.ToString()).ToArray()
            };
            uploaded += await UploadIfNewAsync("groups", [Encode(account.AccountName), Encode(entry.Key.ToString()!)],
                timestamp, payload, false, cancellationToken);
        }
        return uploaded;
    }

    private async Task<int> UploadAccountCollectionAsync(TsmAccount account, LuaTable helper, string key,
        string endpoint, CancellationToken cancellationToken)
    {
        if (!helper.TryGetTable(key, out var data))
            return 0;
        var timestamp = Number(data, "updateTime");
        if (!data.TryGetTable("data", out var entries))
            return 0;
        var serialized = "[" + string.Join(',', entries.Values.OfType<string>()) + "]";
        serialized = endpoint == "analytics"
            ? serialized.Replace("\\", string.Empty, StringComparison.Ordinal)
            : serialized.Replace("\\\"", "\"", StringComparison.Ordinal);
        return await UploadIfNewAsync(endpoint, [Encode(account.AccountName)], timestamp,
            new Dictionary<string, object?> { ["data"] = serialized, ["updateTime"] = timestamp }, false,
            cancellationToken);
    }

    private async Task<int> UploadIfNewAsync(string endpoint, IReadOnlyList<string> path, long timestamp,
        object payload, bool binary, CancellationToken cancellationToken)
    {
        if (!apiClient.SupportsEndpoint(endpoint) || timestamp <= 0 ||
            timestamp <= await apiClient.GetLastUploadAsync(endpoint, path, cancellationToken))
            return 0;
        await apiClient.UploadAsync(new TsmUploadRequest(endpoint, path, payload, binary), cancellationToken);
        return 1;
    }

    private static void CollectAccountingRows(LuaTable database, string realm, string csvKey, string timesKey,
        int type, bool price, long lastUpload, ICollection<object[]> output)
    {
        if (!database.TryGetString($"r@{realm}@internalData@{csvKey}", out var csv) ||
            !database.TryGetString($"r@{realm}@internalData@{timesKey}", out var rawTimes))
            return;
        var records = ParseCsv(csv);
        if (records.Count < 2)
            return;
        var header = records[0].Select((name, index) => (name, index))
            .ToDictionary(entry => entry.name, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        var times = rawTimes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : 0).ToArray();
        for (var index = 1; index < records.Count && index - 1 < times.Length; index++)
        {
            var row = records[index];
            var saved = times[index - 1];
            if (saved <= lastUpload || type is 2 or 3 && !string.Equals(Text(row, header, "source"), "Auction",
                    StringComparison.Ordinal))
                continue;
            var item = Text(row, header, "itemString").Split(':');
            if (item.Length < 2 || item[0] != "i" || !long.TryParse(item[1], out var itemId))
                continue;
            output.Add([itemId, price ? Number(row, header, "price") : 0,
                Number(row, header, "stackSize"), Number(row, header, "quantity"),
                Number(row, header, "time"), saved, type]);
        }
    }

    private static Dictionary<string, object?> PrettifyTsm(LuaTable source)
    {
        var result = new Dictionary<string, object?>
        {
            ["realm"] = new Dictionary<string, object?>(), ["sync"] = new Dictionary<string, object?>(),
            ["factionrealm"] = new Dictionary<string, object?>(), ["global"] = new Dictionary<string, object?>(),
            ["char"] = new Dictionary<string, object?>(), ["profile"] = new Dictionary<string, object?>()
        };
        var scopes = new Dictionary<string, string>
        {
            ["r"] = "realm", ["s"] = "sync", ["f"] = "factionrealm", ["g"] = "global",
            ["c"] = "char", ["p"] = "profile"
        };
        foreach (var entry in source.Entries)
        {
            var key = entry.Key.ToString() ?? string.Empty;
            if (key.StartsWith('_'))
            {
                if (key is not "_hash" and not "_lastModifiedVersion")
                    result[key] = ToJsonValue(entry.Value);
                continue;
            }
            var parts = key.Split('@');
            if (parts.Length != 4 || !scopes.TryGetValue(parts[0], out var scope))
                continue;
            var scoped = (Dictionary<string, object?>)result[scope]!;
            if (!scoped.TryGetValue(parts[1], out var scopeValue))
                scoped[parts[1]] = scopeValue = new Dictionary<string, object?>();
            var namespaces = (Dictionary<string, object?>)scopeValue!;
            if (!namespaces.TryGetValue(parts[2], out var namespaceValue))
                namespaces[parts[2]] = namespaceValue = new Dictionary<string, object?>();
            ((Dictionary<string, object?>)namespaceValue!)[parts[3]] = ToJsonValue(entry.Value);
        }
        return result;
    }

    private static object? ToJsonValue(object? value) => value switch
    {
        LuaTable table => table.Entries.ToDictionary(entry => entry.Key.ToString() ?? string.Empty,
            entry => ToJsonValue(entry.Value), StringComparer.Ordinal),
        _ => value
    };

    private static long Number(LuaTable table, string key) =>
        table.TryGetValue(key, out var value) ? ConvertNumber(value) : 0;

    private static long ConvertNumber(object? value) => value switch
    {
        long number => number,
        double number when number >= long.MinValue && number <= long.MaxValue => (long)number,
        string text when long.TryParse(text, CultureInfo.InvariantCulture, out var number) => number,
        _ => 0
    };

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .Replace('+', '-').Replace('/', '_');

    private static List<string[]> ParseCsv(string source)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '"')
            {
                if (quoted && index + 1 < source.Length && source[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else
                    quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n' && !quoted)
            {
                if (character == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
                    index++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0))
                    rows.Add(row.ToArray());
                row.Clear();
            }
            else
                field.Append(character);
        }
        row.Add(field.ToString());
        if (row.Any(value => value.Length > 0))
            rows.Add(row.ToArray());
        return rows;
    }

    private static string Text(string[] row, IReadOnlyDictionary<string, int> header, string key) =>
        header.TryGetValue(key, out var index) && index < row.Length ? row[index] : string.Empty;

    private static long Number(string[] row, IReadOnlyDictionary<string, int> header, string key) =>
        long.TryParse(Text(row, header, key), CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double RealNumber(string[] row, IReadOnlyDictionary<string, int> header, string key) =>
        double.TryParse(Text(row, header, key), CultureInfo.InvariantCulture, out var value) ? value : 0;
}

internal static class FileInfoExtensions
{
    public static long ToUnixTimeSeconds(this DateTime value) => new DateTimeOffset(value).ToUnixTimeSeconds();
}
