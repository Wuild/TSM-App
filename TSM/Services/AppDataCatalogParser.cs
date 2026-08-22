using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

public sealed partial class AppDataCatalogParser
{
    public static IReadOnlySet<string> SupportedTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AUCTIONDB_NON_COMMODITY_DATA",
        "AUCTIONDB_NON_COMMODITY_SCAN_STAT",
        "AUCTIONDB_NON_COMMODITY_HISTORICAL",
        "AUCTIONDB_COMMODITY_DATA",
        "AUCTIONDB_COMMODITY_SCAN_STAT",
        "AUCTIONDB_COMMODITY_HISTORICAL",
        "AUCTIONDB_REGION_STAT",
        "AUCTIONDB_REGION_HISTORICAL",
        "AUCTIONDB_REGION_SALE",
        "SHOPPING_SEARCHES",
        "APP_INFO"
    };

    public AppDataCatalog Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var entries = new List<AppDataEntry>();
        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var match = MetadataLine().Match(line.Trim());
            if (!match.Success)
                continue;

            var type = match.Groups["type"].Value;
            if (!SupportedTypes.Contains(type) ||
                !long.TryParse(match.Groups["timestamp"].Value, out var timestamp))
                continue;

            entries.Add(new AppDataEntry(type, match.Groups["realm"].Value.Trim(), timestamp,
                match.Groups["payload"].Value.TrimEnd()));
        }

        return new AppDataCatalog(entries);
    }

    public AppDataCatalog ParseFile(string path) => Parse(File.ReadAllText(path));

    [GeneratedRegex(@"^(?<payload>.*?)\s+--<(?<type>[^,>]+),(?<realm>[^,>]*),(?<timestamp>\d+)>\s*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MetadataLine();
}
