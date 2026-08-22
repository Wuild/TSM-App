using System.Text.Json;
using TSM.Models;

namespace TSM.Services;

public sealed class AuctionPriceHistoryStore
{
    private const int MaximumPointsPerItem = 2160;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Dictionary<string, AuctionPriceHistorySeries> _series;

    public AuctionPriceHistoryStore(AppPaths paths)
    {
        _path = paths.AuctionPriceHistoryFile;
        _series = Load(_path).ToDictionary(series => Key(series.Flavor, series.ItemString),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AuctionPriceHistoryPoint> Get(string flavor, string itemString) =>
        _series.TryGetValue(Key(flavor, itemString), out var series)
            ? series.Points.OrderBy(point => point.Timestamp).ToArray()
            : [];

    public void Clear()
    {
        _series.Clear();
        if (File.Exists(_path))
            File.Delete(_path);
        var temporary = _path + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
    }

    public void Capture(IEnumerable<AuctionFavorite> favorites, IReadOnlyList<AuctionHouseItemRow> rows)
    {
        var changed = false;
        foreach (var favorite in favorites)
        {
            var matches = rows.Where(row =>
                    string.Equals(row.Flavor, favorite.Flavor, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.BaseItemString, BaseItemString(favorite.ItemString),
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
                continue;
            var timestamp = matches.Max(row => row.UpdatedAt);
            if (timestamp == DateTimeOffset.MinValue)
                continue;
            var minBuyout = MinimumPositive(matches.Select(row => row.MinBuyout));
            var marketValue = AveragePositive(matches.Select(row => row.MarketValueRecent));
            if (minBuyout is null && marketValue is null)
                continue;

            var key = Key(favorite.Flavor, favorite.ItemString);
            if (!_series.TryGetValue(key, out var series))
            {
                series = new AuctionPriceHistorySeries(favorite.Flavor, BaseItemString(favorite.ItemString), []);
                _series[key] = series;
            }
            var point = new AuctionPriceHistoryPoint(timestamp, minBuyout, marketValue);
            var existing = series.Points.FindIndex(value => value.Timestamp == timestamp);
            if (existing >= 0)
            {
                if (series.Points[existing] == point)
                    continue;
                series.Points[existing] = point;
            }
            else
            {
                series.Points.Add(point);
            }
            Trim(series.Points, timestamp);
            changed = true;
        }
        if (changed)
            Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_series.Values, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static IReadOnlyList<AuctionPriceHistorySeries> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<AuctionPriceHistorySeries>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static void Trim(List<AuctionPriceHistoryPoint> points, DateTimeOffset newest)
    {
        points.RemoveAll(point => newest - point.Timestamp > MaximumAge);
        points.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        if (points.Count > MaximumPointsPerItem)
            points.RemoveRange(0, points.Count - MaximumPointsPerItem);
    }

    private static string Key(string flavor, string itemString) =>
        $"{flavor.Trim().ToUpperInvariant()}|{BaseItemString(itemString).ToUpperInvariant()}";

    private static string BaseItemString(string itemString)
    {
        if (!itemString.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
            return itemString;
        var separator = itemString.IndexOf(':', 2);
        return separator > 2 ? itemString[..separator] : itemString;
    }

    private static long? MinimumPositive(IEnumerable<long?> values)
    {
        var present = values.Where(value => value is > 0).Select(value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Min();
    }

    private static long? AveragePositive(IEnumerable<long?> values)
    {
        var present = values.Where(value => value is > 0).Select(value => (decimal)value!.Value).ToArray();
        return present.Length == 0 ? null : (long)Math.Round(present.Average());
    }
}
