using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

public sealed record ItemIconResult(
    string Path,
    int Quality,
    string Name,
    string TooltipText,
    IReadOnlyList<ItemTooltipLine> TooltipLines,
    AuctionItemClassification Classification);

public sealed record ItemTooltipLine(
    string Text,
    string Color,
    string SocketColor,
    bool HasSocket,
    bool IsMoney,
    AuctionMoneyParts Money)
{
    public bool IsText => !IsMoney;
}

public sealed partial class ItemIconService : IDisposable
{
    private const int MaximumIconBytes = 2_000_000;
    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new(StringComparer.Ordinal);

    public ItemIconService(AppPaths paths, HttpClient? httpClient = null)
    {
        _paths = paths;
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TSM-Desktop/0.1 (+https://github.com/Wuild/TSM-App)");
    }

    public async Task<ItemIconResult?> GetAsync(string itemString, string flavor,
        CancellationToken cancellationToken = default)
    {
        var match = NumericItem().Match(itemString);
        if (!match.Success)
            return null;

        var itemId = match.Groups["id"].Value;
        var flavorKey = ResolveFlavorKey(flavor);
        var cacheKey = $"{flavorKey}:{itemId}";
        var cacheLock = _cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            return await GetCoreAsync(itemId, flavorKey, cancellationToken);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public async Task<AuctionItemClassification?> GetClassificationAsync(string itemString, string flavor,
        CancellationToken cancellationToken = default)
    {
        var match = NumericItem().Match(itemString);
        if (!match.Success)
            return null;
        var itemId = match.Groups["id"].Value;
        var flavorKey = ResolveFlavorKey(flavor);
        var cacheLock = _cacheLocks.GetOrAdd($"{flavorKey}:{itemId}", _ => new SemaphoreSlim(1, 1));
        await cacheLock.WaitAsync(cancellationToken);
        try
        {
            var metadata = await GetMetadataAsync(itemId, flavorKey, cancellationToken);
            return metadata is null ? null : Classify(metadata);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var locks = _cacheLocks.Values.Distinct().ToArray();
        foreach (var cacheLock in locks)
            await cacheLock.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(_paths.ItemIconDirectory))
                Directory.Delete(_paths.ItemIconDirectory, true);
            _cacheLocks.Clear();
        }
        finally
        {
            foreach (var cacheLock in locks)
                cacheLock.Release();
        }
    }

    private async Task<ItemIconResult?> GetCoreAsync(string itemId, string flavorKey,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(_paths.ItemIconDirectory, flavorKey);
        var metadata = await GetMetadataAsync(itemId, flavorKey, cancellationToken);
        if (metadata is null)
            return null;

        var iconPath = Path.Combine(cacheDirectory, $"{metadata.Icon}.jpg");
        if (!File.Exists(iconPath))
            await DownloadIconAsync(metadata.Icon, iconPath, cancellationToken);

        if (!File.Exists(iconPath))
            return null;
        var tooltip = ConvertTooltip(metadata.Tooltip, metadata.Name);
        return new ItemIconResult(iconPath, metadata.Quality, metadata.Name, tooltip.Text, tooltip.Lines,
            Classify(metadata));
    }

    private async Task<IconMetadata?> GetMetadataAsync(string itemId, string flavorKey,
        CancellationToken cancellationToken)
    {
        var cacheDirectory = Path.Combine(_paths.ItemIconDirectory, flavorKey);
        Directory.CreateDirectory(cacheDirectory);
        var metadataPath = Path.Combine(cacheDirectory, $"{itemId}.json");
        IconMetadata? metadata = null;
        try
        {
            if (File.Exists(metadataPath))
                metadata = JsonSerializer.Deserialize<IconMetadata>(
                    await File.ReadAllTextAsync(metadataPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
        }

        if (metadata is null || !SafeIconName().IsMatch(metadata.Icon))
        {
            using var response = await GetTooltipResponseAsync(itemId, flavorKey, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            metadata = await JsonSerializer.DeserializeAsync<IconMetadata>(stream,
                cancellationToken: cancellationToken);
            if (metadata is null || !SafeIconName().IsMatch(metadata.Icon))
                return null;

            var temporaryMetadataPath = metadataPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryMetadataPath, JsonSerializer.Serialize(metadata),
                    cancellationToken);
                File.Move(temporaryMetadataPath, metadataPath, true);
            }
            finally
            {
                if (File.Exists(temporaryMetadataPath))
                    File.Delete(temporaryMetadataPath);
            }
        }

        return metadata;
    }

    private static AuctionItemClassification Classify(IconMetadata metadata)
    {
        var match = ItemClass().Match(metadata.Tooltip);
        return match.Success && int.TryParse(match.Groups["class"].Value, out var classId) &&
               int.TryParse(match.Groups["subclass"].Value, out var subclassId)
            ? WowItemTaxonomy.Classify(metadata.Quality, classId, subclassId)
            : new AuctionItemClassification(metadata.Quality,
                WowItemTaxonomy.Classify(metadata.Quality, 15, 4).QualityName, "Miscellaneous", "Other");
    }

    private async Task DownloadIconAsync(string icon, string iconPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://wow.zamimg.com/images/wow/icons/large/{icon}.jpg");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumIconBytes)
            return;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var temporaryPath = iconPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.WriteThrough);
            var buffer = new byte[81920];
            var total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumIconBytes)
                    return;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await target.FlushAsync(cancellationToken);
            File.Move(temporaryPath, iconPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task<HttpResponseMessage> GetTooltipResponseAsync(string itemId, string flavorKey,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(BuildTooltipUri(itemId, flavorKey), cancellationToken);
        if (response.IsSuccessStatusCode || flavorKey != "beta")
            return response;

        response.Dispose();
        return await _httpClient.GetAsync(BuildTooltipUri(itemId, "retail"), cancellationToken);
    }

    private static string ResolveFlavorKey(string flavor)
    {
        var value = flavor.Trim().ToLowerInvariant();
        if (value.Contains("anniversary", StringComparison.Ordinal))
            return "tbc";
        if (value.Contains("classic era", StringComparison.Ordinal) && value.Contains("ptr", StringComparison.Ordinal))
            return "classic-ptr";
        if (value.Contains("classic ptr", StringComparison.Ordinal))
            return "classic-ptr";
        if (value == "ptr" || value.Contains("retail ptr", StringComparison.Ordinal))
            return "ptr";
        if (value.Contains("retail beta", StringComparison.Ordinal))
            return "beta";
        if (value.Contains("era", StringComparison.Ordinal))
            return "classic";
        if (value.Contains("progress", StringComparison.Ordinal) || value.Contains("mop", StringComparison.Ordinal) ||
            value.Contains("classic", StringComparison.Ordinal) || value is "bcc")
            return "mop-classic";
        if (value.Contains("ptr", StringComparison.Ordinal))
            return "ptr";
        if (value.Contains("beta", StringComparison.Ordinal))
            return "beta";
        return "retail";
    }

    private static Uri BuildTooltipUri(string itemId, string flavorKey) => flavorKey switch
    {
        "classic" => new Uri($"https://nether.wowhead.com/classic/tooltip/item/{itemId}"),
        "classic-ptr" => new Uri($"https://nether.wowhead.com/classic-ptr/tooltip/item/{itemId}"),
        "tbc" => new Uri($"https://nether.wowhead.com/tbc/tooltip/item/{itemId}"),
        "mop-classic" => new Uri($"https://nether.wowhead.com/mop-classic/tooltip/item/{itemId}"),
        "beta" => new Uri($"https://nether.wowhead.com/beta/tooltip/item/{itemId}"),
        "ptr" => new Uri($"https://nether.wowhead.com/ptr/tooltip/item/{itemId}"),
        _ => new Uri($"https://nether.wowhead.com/tooltip/item/{itemId}")
    };

    public static Uri? BuildItemPageUri(string itemString, string flavor)
    {
        var match = NumericItem().Match(itemString);
        if (!match.Success)
            return null;
        var itemId = match.Groups["id"].Value;
        var flavorPath = ResolveFlavorKey(flavor) switch
        {
            "classic" => "classic/",
            "classic-ptr" => "classic-ptr/",
            "tbc" => "tbc/",
            "mop-classic" => "mop-classic/",
            "beta" => "beta/",
            "ptr" => "ptr/",
            _ => string.Empty
        };
        return new Uri($"https://www.wowhead.com/{flavorPath}item={itemId}");
    }

    private static ParsedTooltip ConvertTooltip(string html, string itemName)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new ParsedTooltip(string.Empty, []);

        var source = ItemNameBlock().Replace(html, string.Empty);
        source = PhaseBlock().Replace(source, string.Empty);
        source = CellBoundary().Replace(source, " · ");
        source = TooltipLineBreak().Replace(source, "\n");
        source = HtmlComment().Replace(source, string.Empty);
        var lines = new List<ItemTooltipLine>();
        foreach (var rawLine in source.Split('\n'))
        {
            var text = WebUtility.HtmlDecode(HtmlTag().Replace(rawLine, string.Empty))
                .Replace('\u00a0', ' ');
            text = HorizontalWhitespace().Replace(text, " ").Trim(' ', '·');
            if (text.Length == 0 || string.Equals(text, itemName, StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Phase ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("SoD Phase ", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Dropped by", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Drop chance", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("Max Stack:", StringComparison.OrdinalIgnoreCase))
                continue;

            var money = ParseMoney(rawLine);
            var socketColor = rawLine.Contains("socket-blue", StringComparison.OrdinalIgnoreCase) ? "#2878D0" :
                rawLine.Contains("socket-yellow", StringComparison.OrdinalIgnoreCase) ? "#E5C83B" :
                rawLine.Contains("socket-red", StringComparison.OrdinalIgnoreCase) ? "#D94A4A" :
                rawLine.Contains("socket-meta", StringComparison.OrdinalIgnoreCase) ? "#B9A5E8" :
                rawLine.Contains("socket-prismatic", StringComparison.OrdinalIgnoreCase) ? "#D8D8D8" : string.Empty;
            var color = socketColor.Length > 0 ? socketColor : TooltipTextColor(rawLine, text);
            lines.Add(new ItemTooltipLine(
                money is not null ? "Sell Price:" : text,
                color,
                socketColor,
                socketColor.Length > 0,
                money is not null,
                money ?? new AuctionMoneyParts(string.Empty, string.Empty, string.Empty, string.Empty)));
        }

        var plainText = string.Join('\n', lines.Select(line => line.IsMoney
            ? $"{line.Text} {line.Money.Gold}{line.Money.Silver}{line.Money.Copper}".TrimEnd()
            : line.Text));
        if (plainText.Length > 2000)
            plainText = plainText[..2000] + "…";
        return new ParsedTooltip(plainText, lines);
    }

    private static AuctionMoneyParts? ParseMoney(string html)
    {
        var values = MoneySpan().Matches(html).ToDictionary(
            match => match.Groups["kind"].Value,
            match => WebUtility.HtmlDecode(match.Groups["value"].Value).Trim(),
            StringComparer.OrdinalIgnoreCase);
        if (values.Count == 0)
            return null;
        var hasGold = values.TryGetValue("gold", out var gold);
        var hasSilver = values.TryGetValue("silver", out var silver);
        var hasCopper = values.TryGetValue("copper", out var copper);
        return new AuctionMoneyParts(
            string.Empty,
            hasGold ? $"{FormatCoinValue(gold!, false)}g " : string.Empty,
            hasSilver ? $"{FormatCoinValue(silver!, hasGold)}s " : string.Empty,
            hasCopper ? $"{FormatCoinValue(copper!, hasGold || hasSilver)}c" : string.Empty);
    }

    private static string FormatCoinValue(string value, bool padTwoDigits)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (!long.TryParse(digits, out var number))
            return value;
        return padTwoDigits
            ? number.ToString("00", CultureInfo.InvariantCulture)
            : number.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string TooltipTextColor(string html, string text)
    {
        if (html.Contains("class=\"q2", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Equip:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Use:", StringComparison.OrdinalIgnoreCase))
            return "#1EFF00";
        if (html.Contains("class=\"q3", StringComparison.OrdinalIgnoreCase))
            return "#0070DD";
        if (html.Contains("class=\"q4", StringComparison.OrdinalIgnoreCase))
            return "#A335EE";
        if (html.Contains("class=\"q5", StringComparison.OrdinalIgnoreCase))
            return "#FF8000";
        if (text.StartsWith("Item Level", StringComparison.OrdinalIgnoreCase))
            return "#FFD36A";
        if (text.StartsWith("Requires ", StringComparison.OrdinalIgnoreCase))
            return "#F0A35B";
        if (text.StartsWith("Socket Bonus:", StringComparison.OrdinalIgnoreCase))
            return "#9D9D9D";
        return "#E4E4E4";
    }

    private sealed record ParsedTooltip(string Text, IReadOnlyList<ItemTooltipLine> Lines);

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private sealed class IconMetadata
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("icon")] public string Icon { get; init; } = string.Empty;
        [JsonPropertyName("quality")] public int Quality { get; init; }
        [JsonPropertyName("tooltip")] public string Tooltip { get; init; } = string.Empty;
    }

    [GeneratedRegex(@"^i:(?<id>\d+)(?::.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericItem();

    [GeneratedRegex(@"^[a-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIconName();

    [GeneratedRegex(@"<(?:br\s*/?|table(?:\s[^>]*)?|/tr|/div|/p|/table)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooltipLineBreak();

    [GeneratedRegex(@"<!--nstart-->.*?<!--nend-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ItemNameBlock();

    [GeneratedRegex("<b[^>]*class=\"[^\"]*whtt-extra[^\"]*\"[^>]*>.*?</b>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PhaseBlock();

    [GeneratedRegex(@"</td>\s*<th[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CellBoundary();

    [GeneratedRegex("class=\"[^\"]*money(?<kind>gold|silver|copper)[^\"]*\"[^>]*>(?<value>[^<]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MoneySpan();

    [GeneratedRegex(@"scstart(?<class>\d+):(?<subclass>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemClass();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"\n(?:\s*\n)+", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessLines();
}
