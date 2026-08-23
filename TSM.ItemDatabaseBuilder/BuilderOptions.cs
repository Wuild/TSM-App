using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TSM.ItemDatabaseBuilder;

internal sealed record ItemCatalog(string Flavor, string Namespace);

internal sealed class BuilderOptions
{
    private static readonly Regex TsmItemId = new(@"\bi:(?<id>[1-9][0-9]{0,9})(?::[^\s,;\]\}]*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingItemId = new(@"^\s*(?<id>[1-9][0-9]{0,9})(?:\s*[,;\t]|\s*$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string? IdsPath { get; init; }
    public bool DiscoverIds { get; init; }
    public string? DiscoveredIdsPath { get; init; }
    public required string OutputPath { get; init; }
    public string? ManifestPath { get; init; }
    public required string Region { get; init; }
    public required string Locale { get; init; }
    public required IReadOnlyList<ItemCatalog> Catalogs { get; init; }
    public bool Refresh { get; init; }
    public bool AuctionItemsOnly { get; init; }
    public int RequestsPerSecond { get; init; }
    public int MaximumAttempts { get; init; }
    public int Concurrency { get; init; }

    public static BuilderOptions Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--refresh" or "--all-active" or "--discover-ids" or "--auction-items-only")
            {
                switches.Add(argument);
                continue;
            }
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid or incomplete argument: {argument}");
            if (!values.TryGetValue(argument, out var entries))
                values[argument] = entries = [];
            entries.Add(args[++index]);
        }

        var region = One(values, "--region")?.ToLowerInvariant() ?? "eu";
        if (region is not ("us" or "eu" or "kr" or "tw"))
            throw new ArgumentException("--region must be us, eu, kr, or tw.");
        var locale = One(values, "--locale") ?? DefaultLocale(region);
        var catalogs = new Dictionary<string, ItemCatalog>(StringComparer.OrdinalIgnoreCase);
        if (switches.Contains("--all-active"))
        foreach (var edition in new[] { "vanilla", "tbc", "mop", "retail" })
        {
            var preset = EditionCatalog(edition, region);
            catalogs[preset.Flavor] = preset;
        }
        if (values.TryGetValue("--edition", out var editions))
        foreach (var edition in editions)
        {
            var preset = EditionCatalog(edition, region);
            catalogs[preset.Flavor] = preset;
        }
        if (values.TryGetValue("--catalog", out var catalogValues))
        foreach (var value in catalogValues)
        {
            var catalog = ParseCatalog(value);
            catalogs[catalog.Flavor] = catalog;
        }
        if (catalogs.Count == 0)
            catalogs["retail"] = EditionCatalog("retail", region);
        return new BuilderOptions
        {
            IdsPath = One(values, "--ids"),
            DiscoverIds = switches.Contains("--discover-ids"),
            DiscoveredIdsPath = One(values, "--write-discovered-ids"),
            OutputPath = One(values, "--output") ?? Path.Combine("artifacts", "item-data", "items.jsonl"),
            ManifestPath = One(values, "--manifest"),
            Region = region,
            Locale = locale,
            Catalogs = catalogs.Values.ToArray(),
            Refresh = switches.Contains("--refresh"),
            AuctionItemsOnly = switches.Contains("--auction-items-only"),
            RequestsPerSecond = Integer(values, "--requests-per-second", 12, 1, 50),
            MaximumAttempts = Integer(values, "--max-attempts", 6, 1, 20),
            Concurrency = Integer(values, "--concurrency", 8, 1, 64)
        };
    }

    public IReadOnlyList<long> ReadItemIds()
    {
        if (string.IsNullOrWhiteSpace(IdsPath))
            throw new ArgumentException("--ids is required unless --discover-ids is used.");
        if (!File.Exists(IdsPath))
            throw new FileNotFoundException("The item ID input file does not exist.", IdsPath);
        var ids = new SortedSet<long>();
        var content = File.ReadAllText(IdsPath);
        try
        {
            using var document = JsonDocument.Parse(content);
            ReadJsonIds(document.RootElement, ids, true);
        }
        catch (JsonException)
        {
            foreach (var line in content.Split('\n'))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    ReadJsonIds(document.RootElement, ids, true);
                    continue;
                }
                catch (JsonException)
                {
                }
                var tsmMatches = TsmItemId.Matches(line);
                if (tsmMatches.Count > 0)
                {
                    foreach (Match match in tsmMatches)
                        Add(match.Groups["id"].Value, ids);
                    continue;
                }
                var leading = LeadingItemId.Match(line);
                if (leading.Success)
                    Add(leading.Groups["id"].Value, ids);
            }
        }
        if (ids.Count == 0)
            throw new InvalidOperationException("No item IDs were found in the input file.");
        return ids.ToArray();
    }

    private static void ReadJsonIds(JsonElement element, ISet<long> ids, bool itemProperty)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var entry in element.EnumerateArray())
                    ReadJsonIds(entry, ids, itemProperty);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    ReadJsonIds(property.Value, ids, IsItemProperty(property.Name));
                break;
            case JsonValueKind.Number when itemProperty:
                if (element.TryGetInt64(out var numericId) && numericId > 0)
                    ids.Add(numericId);
                break;
            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                foreach (Match match in TsmItemId.Matches(value))
                    Add(match.Groups["id"].Value, ids);
                if (itemProperty)
                    Add(value, ids);
                break;
        }
    }

    private static bool IsItemProperty(string name) => name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("itemId", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("item_id", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("itemString", StringComparison.OrdinalIgnoreCase);

    private static void Add(string value, ISet<long> ids)
    {
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            ids.Add(id);
    }

    private static string Required(IReadOnlyDictionary<string, List<string>> values, string name) =>
        One(values, name) ?? throw new ArgumentException($"{name} is required.");

    private static string? One(IReadOnlyDictionary<string, List<string>> values, string name)
    {
        if (!values.TryGetValue(name, out var entries))
            return null;
        if (entries.Count != 1)
            throw new ArgumentException($"{name} may only be supplied once.");
        return entries[0];
    }

    private static int Integer(IReadOnlyDictionary<string, List<string>> values, string name, int fallback,
        int minimum, int maximum)
    {
        var value = One(values, name);
        if (value is null)
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ||
            result < minimum || result > maximum)
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        return result;
    }

    private static ItemCatalog ParseCatalog(string value)
    {
        var separator = value.IndexOf('=');
        if (separator < 1 || separator == value.Length - 1)
            throw new ArgumentException("--catalog must use flavor=namespace, such as retail=static-eu.");
        var flavor = value[..separator].Trim().ToLowerInvariant();
        var apiNamespace = value[(separator + 1)..].Trim().ToLowerInvariant();
        if (!Regex.IsMatch(flavor, @"^[a-z0-9-]+$") || !Regex.IsMatch(apiNamespace, @"^[a-z0-9._-]+$"))
            throw new ArgumentException($"Invalid catalog: {value}");
        return new ItemCatalog(flavor, apiNamespace);
    }

    private static ItemCatalog EditionCatalog(string value, string region)
    {
        var edition = value.Trim().ToLowerInvariant();
        return edition switch
        {
            "vanilla" => new ItemCatalog("vanilla", $"static-classic1x-{region}"),
            "tbc" => new ItemCatalog("tbc", $"static-classicann-{region}"),
            "mop" => new ItemCatalog("mop", $"static-classic-{region}"),
            "retail" => new ItemCatalog("retail", $"static-{region}"),
            "wrath" or "cataclysm" => throw new ArgumentException(
                $"Blizzard does not publish a stable historical {edition} namespace. " +
                $"Use --catalog {edition}=EXACT_SNAPSHOT_NAMESPACE when you have one."),
            _ => throw new ArgumentException(
                $"Unknown edition '{value}'. Use vanilla, tbc, mop, retail, or an explicit --catalog.")
        };
    }

    private static string DefaultLocale(string region) => region switch
    {
        "us" => "en_US",
        "kr" => "ko_KR",
        "tw" => "zh_TW",
        _ => "en_GB"
    };
}
