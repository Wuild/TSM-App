using System.Text;
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

    public AccountingExportResult Export(
        TsmAccount account,
        string realm,
        AccountingExportOptions options,
        string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var document = parser.ParseFile(account.SavedVariablesPath);
        if (!document.TryGetTable("TradeSkillMasterDB", out var database))
            return new AccountingExportResult([], [], ["TradeSkillMasterDB was not found in the SavedVariables file."]);

        document.TryGetTable("TSMItemInfoDB", out var itemInfo);
        var itemNames = BuildItemNameLookup(itemInfo);
        var files = new List<string>();
        var missing = new List<string>();
        var errors = new List<string>();

        foreach (var category in SelectedCategories(options))
        {
            var rawKey = $"r@{realm}@internalData@{DatabaseKeys[category]}";
            if (!database.TryGetString(rawKey, out var csv) || string.IsNullOrWhiteSpace(csv))
            {
                missing.Add(category);
                continue;
            }

            try
            {
                var rows = ParseCsv(csv);
                if (rows.Count == 0)
                {
                    missing.Add(category);
                    continue;
                }

                var header = rows[0].ToList();
                var itemStringIndex = header.IndexOf("itemString");
                var itemNameIndex = header.IndexOf("itemName");
                if (itemStringIndex >= 0 && itemNameIndex < 0)
                {
                    itemNameIndex = Math.Min(1, header.Count);
                    header.Insert(itemNameIndex, "itemName");
                }

                var outputRows = new List<IReadOnlyList<string>> { header };
                foreach (var sourceRow in rows.Skip(1))
                {
                    var row = sourceRow.ToList();
                    if (itemStringIndex >= 0 && itemNameIndex >= 0)
                    {
                        var itemString = itemStringIndex < row.Count ? row[itemStringIndex] : string.Empty;
                        row.Insert(itemNameIndex, itemNames.GetValueOrDefault(itemString, "?"));
                    }
                    outputRows.Add(row);
                }

                var path = Path.Combine(destinationDirectory, $"Accounting_{SanitizeFileName(realm)}_{category}.csv");
                File.WriteAllText(path, WriteCsv(outputRows), new UTF8Encoding(false));
                files.Add(path);
            }
            catch (FormatException exception)
            {
                errors.Add($"{category}: {exception.Message}");
            }
        }

        return new AccountingExportResult(files, missing, errors);
    }

    private LuaTable? LoadDatabase(string path, string name)
    {
        var document = parser.ParseFile(path);
        return document.TryGetTable(name, out var database) ? database : null;
    }

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

        var names = FlattenChunks(rawNames).SelectMany(value => value.Split('\x02')).Where(value => value.Length > 0);
        var itemStrings = FlattenChunks(rawStrings).SelectMany(value => value.Split('\x02')).Where(value => value.Length > 0);
        return itemStrings.Zip(names).ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
    }

    private static IEnumerable<string> FlattenChunks(object? value) => value switch
    {
        string text => [text],
        LuaTable table => table.Values.OfType<string>(),
        _ => []
    };

    private static IEnumerable<string> SelectedCategories(AccountingExportOptions options)
    {
        if (options.Sales) yield return "sales";
        if (options.Purchases) yield return "purchases";
        if (options.Income) yield return "income";
        if (options.Expenses) yield return "expenses";
        if (options.Expired) yield return "expired";
        if (options.Canceled) yield return "canceled";
    }

    internal static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string input)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < input.Length; index++)
        {
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

    private static string WriteCsv(IEnumerable<IReadOnlyList<string>> rows) =>
        string.Join(Environment.NewLine, rows.Select(row => string.Join(",", row.Select(EscapeCsv))));

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}
