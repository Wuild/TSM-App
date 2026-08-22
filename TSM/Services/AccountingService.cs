using System.Text;
using System.Globalization;
using TSM.Models;

namespace TSM.Services;

public sealed class AccountingService(LuaSavedVariablesParser parser)
{
    private static readonly IReadOnlyDictionary<string, string> DatabaseKeys = new Dictionary<string, string>
    {
        ["sales"] = "csvSales",
        ["purchases"] = "csvBuys",
        ["income"] = "csvIncome",
        ["expenses"] = "csvExpense",
        ["expired"] = "csvExpired",
        ["canceled"] = "csvCancelled"
    };

    public IReadOnlyList<TsmAccount> FindAccounts(IEnumerable<WowInstallation> installations)
    {
        var accounts = new List<TsmAccount>();
        foreach (var installation in installations.Where(item => Directory.Exists(item.SavedVariablesRoot)))
        {
            IEnumerable<string> accountDirectories;
            try
            {
                accountDirectories = Directory.EnumerateDirectories(installation.SavedVariablesRoot).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var accountDirectory in accountDirectories)
            {
                var file = Path.Combine(accountDirectory, "SavedVariables", "TradeSkillMaster.lua");
                if (!File.Exists(file))
                    continue;
                var name = Path.GetFileName(accountDirectory);
                var appHelperFile = Path.Combine(accountDirectory, "SavedVariables", "TradeSkillMaster_AppHelper.lua");
                var appDataFile = FindAppDataFile(installation.AddOnsPath);
                accounts.Add(new TsmAccount($"{name} · {installation.Name}", name, installation.Name, file,
                    appHelperFile, appDataFile ?? string.Empty));
            }
        }

        return accounts.OrderBy(account => account.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> FindRealms(TsmAccount account)
    {
        var database = LoadDatabase(account.SavedVariablesPath, "TradeSkillMasterDB");
        if (database is null || !database.TryGetTable("_scopeKeys", out var scopeKeys) ||
            !scopeKeys.TryGetTable("realm", out var realms))
            return [];

        return realms.Values.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public AccountingStatistics BuildStatistics(TsmAccount account, string realm, string? player, int? periodDays,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = parser.ParseFile(account.SavedVariablesPath);
        if (!document.TryGetTable("TradeSkillMasterDB", out var database))
            return AccountingStatistics.Empty;
        document.TryGetTable("TSMItemInfoDB", out var itemInfo);
        var itemNames = BuildItemNameLookup(itemInfo);
        var transactions = new List<AccountingTransaction>();
        ReadTransactions(database, realm, "sales", transactions, cancellationToken);
        ReadTransactions(database, realm, "purchases", transactions, cancellationToken);
        ReadTransactions(database, realm, "income", transactions, cancellationToken);
        ReadTransactions(database, realm, "expenses", transactions, cancellationToken);
        var goldSnapshots = ReadGoldSnapshots(database, realm, cancellationToken);
        var players = transactions.Select(transaction => transaction.Player)
            .Concat(goldSnapshots.Select(snapshot => snapshot.Player))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var cutoff = periodDays is { } days ? DateTimeOffset.UtcNow.AddDays(-days) : DateTimeOffset.MinValue;
        var selected = transactions
            .Where(transaction => transaction.Timestamp >= cutoff &&
                                  (string.IsNullOrWhiteSpace(player) ||
                                   string.Equals(transaction.Player, player, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(transaction => transaction.Timestamp).ToArray();
        var goldHistory = BuildGoldHistory(goldSnapshots, player, cutoff, periodDays, cancellationToken);
        if (selected.Length == 0 && goldHistory.Count == 0)
            return AccountingStatistics.Empty with { Players = players };

        var sales = selected.Where(transaction => transaction.Category == "sales").ToArray();
        var purchases = selected.Where(transaction => transaction.Category == "purchases").ToArray();
        var earned = sales.Sum(transaction => transaction.Amount);
        var spent = purchases.Sum(transaction => transaction.Amount);
        var itemSummaries = sales.Concat(purchases)
            .Where(transaction => transaction.ItemString.Length > 0)
            .GroupBy(transaction => transaction.ItemString, StringComparer.Ordinal)
            .Select(group =>
            {
                var itemEarned = group.Where(transaction => transaction.Category == "sales")
                    .Sum(transaction => transaction.Amount);
                var itemSpent = group.Where(transaction => transaction.Category == "purchases")
                    .Sum(transaction => transaction.Amount);
                return new AccountingItemSummary(group.Key, itemNames.GetValueOrDefault(group.Key, group.Key),
                    itemEarned, itemSpent, itemEarned - itemSpent,
                    group.Where(transaction => transaction.Category == "sales").Sum(transaction => transaction.Quantity),
                    group.Where(transaction => transaction.Category == "purchases").Sum(transaction => transaction.Quantity));
            }).ToArray();
        var topItems = itemSummaries.OrderByDescending(item => item.Earned + item.Spent).Take(12).ToArray();
        if (goldHistory.Count == 0)
        {
            var balance = 0L;
            foreach (var day in selected.GroupBy(transaction => transaction.Timestamp.UtcDateTime.Date)
                         .OrderBy(group => group.Key))
            {
                balance += day.Sum(transaction => transaction.Category is "sales" or "income"
                    ? transaction.Amount
                    : -transaction.Amount);
                goldHistory.Add(new AccountingGoldPoint(new DateTimeOffset(day.Key, TimeSpan.Zero), balance));
            }
        }
        var balances = goldHistory.Select(point => point.BalanceCopper).ToArray();
        var first = selected.FirstOrDefault()?.Timestamp;
        var last = selected.LastOrDefault()?.Timestamp;
        var activityStart = goldHistory.FirstOrDefault()?.Timestamp ?? first ?? DateTimeOffset.UtcNow;
        var activityDays = periodDays ?? DaysSince(activityStart);
        var saleDays = sales.Length == 0 ? 1 : DaysSince(sales.Min(transaction => transaction.Timestamp));
        var purchaseDays = purchases.Length == 0 ? 1 : DaysSince(purchases.Min(transaction => transaction.Timestamp));
        var averageEarned = earned / saleDays;
        var averageSpent = spent / purchaseDays;

        return new AccountingStatistics(
            players,
            goldHistory,
            topItems,
            earned,
            spent,
            earned - spent,
            balances.Max(),
            balances.Min(),
            sales.Select(transaction => transaction.Amount / transaction.Quantity).DefaultIfEmpty().Max(),
            purchases.Select(transaction => transaction.Amount / transaction.Quantity).DefaultIfEmpty().Max(),
            sales.Sum(transaction => transaction.Quantity) / Math.Max(1, activityDays),
            purchases.Sum(transaction => transaction.Quantity) / Math.Max(1, activityDays),
            averageEarned,
            averageSpent,
            averageEarned - averageSpent,
            TopItem(itemSummaries, item => item.Earned),
            TopItem(itemSummaries, item => item.Spent),
            TopItem(itemSummaries, MatchedProfit),
            first,
            last);
    }

    private LuaTable? LoadDatabase(string path, string name)
    {
        var document = parser.ParseFile(path);
        return document.TryGetTable(name, out var database) ? database : null;
    }

    private static void ReadTransactions(LuaTable database, string realm, string category,
        ICollection<AccountingTransaction> transactions, CancellationToken cancellationToken)
    {
        var rawKey = $"r@{realm}@internalData@{DatabaseKeys[category]}";
        if (!database.TryGetString(rawKey, out var csv) || string.IsNullOrWhiteSpace(csv))
            return;
        var rows = ParseCsv(csv, cancellationToken);
        if (rows.Count < 2)
            return;
        var header = rows[0].Select((name, index) => (name, index))
            .ToDictionary(entry => entry.name, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = Number(row, header, "time");
            if (timestamp <= 0 || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                continue;
            var quantity = Math.Max(1, Number(row, header, "quantity"));
            var amount = category is "sales" or "purchases"
                ? Multiply(Number(row, header, "price"), quantity)
                : Math.Max(0, Number(row, header, "amount"));
            if (amount <= 0)
                continue;
            transactions.Add(new AccountingTransaction(
                category,
                Text(row, header, "itemString"),
                Text(row, header, "player"),
                DateTimeOffset.FromUnixTimeSeconds(timestamp),
                amount,
                quantity));
        }
    }

    private static List<AccountingGoldPoint> BuildGoldHistory(
        IReadOnlyList<AccountingGoldSnapshot> snapshots,
        string? player,
        DateTimeOffset cutoff,
        int? periodDays,
        CancellationToken cancellationToken)
    {
        var selected = snapshots.Where(snapshot => string.IsNullOrWhiteSpace(player) ||
                                                   string.Equals(snapshot.Player, player,
                                                       StringComparison.OrdinalIgnoreCase));
        var events = new List<AccountingGoldSnapshot>();
        foreach (var group in selected.GroupBy(snapshot => snapshot.Player, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ordered = group.OrderBy(snapshot => snapshot.Timestamp).ToArray();
            var baseline = ordered.LastOrDefault(snapshot => snapshot.Timestamp < cutoff);
            if (baseline is not null)
                events.Add(baseline with { Timestamp = cutoff });
            events.AddRange(ordered.Where(snapshot => snapshot.Timestamp >= cutoff));
        }

        var balances = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var combined = new List<AccountingGoldPoint>();
        foreach (var group in events.OrderBy(snapshot => snapshot.Timestamp).GroupBy(snapshot => snapshot.Timestamp))
        {
            foreach (var snapshot in group)
                balances[snapshot.Player] = snapshot.Copper;
            combined.Add(new AccountingGoldPoint(group.Key, balances.Values.Sum()));
        }

        var bucket = periodDays switch
        {
            <= 1 => TimeSpan.FromMinutes(15),
            <= 7 => TimeSpan.FromHours(1),
            <= 31 => TimeSpan.FromHours(6),
            _ => TimeSpan.FromDays(1)
        };
        return combined.GroupBy(point => point.Timestamp.UtcTicks / bucket.Ticks)
            .Select(group => group.Last())
            .ToList();
    }

    private static IReadOnlyList<AccountingGoldSnapshot> ReadGoldSnapshots(LuaTable database, string realm,
        CancellationToken cancellationToken)
    {
        const string prefix = "s@";
        const string suffix = "@internalData@goldLog";
        var realmSuffix = $" - {realm}";
        var snapshots = new List<AccountingGoldSnapshot>();
        foreach (var entry in database.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Key is not string key || entry.Value is not string csv ||
                !key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var scope = key[prefix.Length..^suffix.Length];
            if (!scope.EndsWith(realmSuffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var playerAndFaction = scope[..^realmSuffix.Length];
            var separator = playerAndFaction.IndexOf(" - ", StringComparison.Ordinal);
            var player = separator < 0 ? playerAndFaction : playerAndFaction[..separator];
            var rows = ParseCsv(csv, cancellationToken);
            if (rows.Count < 2)
                continue;
            var header = rows[0].Select((name, index) => (name, index))
                .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Skip(1))
            {
                var minute = Number(row, header, "minute");
                var copper = Number(row, header, "copper");
                if (minute <= 0 || minute > DateTimeOffset.MaxValue.ToUnixTimeSeconds() / 60 || copper < 0)
                    continue;
                snapshots.Add(new AccountingGoldSnapshot(player, DateTimeOffset.FromUnixTimeSeconds(minute * 60), copper));
            }
        }
        return snapshots;
    }

    private static string TopItem(IEnumerable<AccountingItemSummary> items, Func<AccountingItemSummary, long> value)
    {
        var item = items.OrderByDescending(value).FirstOrDefault(candidate => value(candidate) > 0);
        return item?.ItemName ?? "—";
    }

    private static long MatchedProfit(AccountingItemSummary item)
    {
        if (item.SoldQuantity <= 0 || item.PurchasedQuantity <= 0)
            return 0;
        var averageSale = item.Earned / item.SoldQuantity;
        var averagePurchase = item.Spent / item.PurchasedQuantity;
        return Multiply(Math.Max(0, averageSale - averagePurchase),
            Math.Min(item.SoldQuantity, item.PurchasedQuantity));
    }

    private static int DaysSince(DateTimeOffset timestamp) =>
        Math.Max(1, (int)Math.Ceiling(Math.Max(0, (DateTimeOffset.UtcNow - timestamp).TotalDays)));

    private static string Text(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> header, string field) =>
        header.TryGetValue(field, out var index) && index < row.Count ? row[index] : string.Empty;

    private static long Number(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> header, string field) =>
        long.TryParse(Text(row, header, field), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static long Multiply(params long[] values)
    {
        decimal result = 1;
        foreach (var value in values)
            result *= value;
        return result >= long.MaxValue ? long.MaxValue : result <= 0 ? 0 : (long)result;
    }

    private sealed record AccountingGoldSnapshot(string Player, DateTimeOffset Timestamp, long Copper);

    private static string? FindAppDataFile(string addonsPath)
    {
        try
        {
            var appHelperDirectory = Directory.EnumerateDirectories(addonsPath)
                .FirstOrDefault(directory => string.Equals(Path.GetFileName(directory), "TradeSkillMaster_AppHelper",
                    StringComparison.OrdinalIgnoreCase));
            if (appHelperDirectory is null)
                return null;
            var path = Path.Combine(appHelperDirectory, "AppData.lua");
            return File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildItemNameLookup(LuaTable? itemInfo)
    {
        if (itemInfo is null || !itemInfo.TryGetValue("names", out var rawNames) ||
            !itemInfo.TryGetValue("itemStrings", out var rawStrings))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return new Dictionary<string, string>(TsmItemNameLookup.Build(rawNames, rawStrings), StringComparer.Ordinal);
    }

    internal static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string input,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < input.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var character = input[index];
            if (inQuotes)
            {
                if (character == '"' && index + 1 < input.Length && input[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < input.Length && input[index + 1] == '\n')
                    index++;
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (inQuotes)
            throw new FormatException("CSV contains an unterminated quoted field.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        if (rows.Count > 1 && rows.Skip(1).Any(candidate => candidate.Count != rows[0].Count))
            throw new FormatException("CSV rows do not match the header column count.");
        return rows;
    }

    private sealed record AccountingTransaction(
        string Category,
        string ItemString,
        string Player,
        DateTimeOffset Timestamp,
        long Amount,
        long Quantity);
}
