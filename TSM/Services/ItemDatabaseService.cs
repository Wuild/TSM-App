using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using TSM.Models;

namespace TSM.Services;

public sealed class ItemDatabaseService
{
    private const int MaximumPreviewBytes = 1_000_000;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, Lazy<BundledItemData?>> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<BundledItemSummary?>> _summaryCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ItemDatabaseService(string? databasePath = null)
    {
        _databasePath = databasePath ?? ResolveDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public bool IsAvailable => File.Exists(_databasePath);

    public BundledItemData? Get(string itemString, string flavor)
    {
        if (!TryItemId(itemString, out var itemId))
            return null;
        var databaseFlavor = ResolveFlavor(flavor);
        return _cache.GetOrAdd($"{databaseFlavor}:{itemId}", _ =>
            new Lazy<BundledItemData?>(() => Read(databaseFlavor, itemId))).Value;
    }

    public BundledItemSummary? GetSummary(string itemString, string flavor)
    {
        if (!TryItemId(itemString, out var itemId))
            return null;
        var databaseFlavor = ResolveFlavor(flavor);
        return _summaryCache.GetOrAdd($"{databaseFlavor}:{itemId}", _ =>
            new Lazy<BundledItemSummary?>(() => ReadSummary(databaseFlavor, itemId))).Value;
    }

    public IReadOnlyList<BundledItemData> Query(ItemDatabaseQuery query)
    {
        if (!IsAvailable)
            return [];
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = CreateFilters(command, query);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000));
        command.CommandText = $"{SelectSql} WHERE {string.Join(" AND ", filters)} ORDER BY name LIMIT $limit";
        using var reader = command.ExecuteReader();
        var results = new List<BundledItemData>();
        while (reader.Read())
            results.Add(ReadRow(reader));
        return results;
    }

    public IReadOnlySet<long> QueryIds(ItemDatabaseQuery query)
    {
        if (!IsAvailable)
            return new HashSet<long>();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var filters = CreateFilters(command, query);
            command.CommandText = $"SELECT id FROM items WHERE {string.Join(" AND ", filters)}";
            using var reader = command.ExecuteReader();
            var ids = new HashSet<long>();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
            return ids;
        }
        catch (SqliteException)
        {
            return new HashSet<long>();
        }
    }

    private static List<string> CreateFilters(SqliteCommand command, ItemDatabaseQuery query)
    {
        var filters = new List<string> { "flavor=$flavor", "found=1" };
        command.Parameters.AddWithValue("$flavor", ResolveFlavor(query.Flavor));
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            filters.Add("name LIKE $search ESCAPE '\\'");
            command.Parameters.AddWithValue("$search", $"%{EscapeLike(query.SearchText.Trim())}%");
        }
        if (query.Quality is { } quality)
        {
            filters.Add("quality=$quality");
            command.Parameters.AddWithValue("$quality", quality);
        }
        if (query.ClassId is { } classId)
        {
            filters.Add("class_id=$class_id");
            command.Parameters.AddWithValue("$class_id", classId);
        }
        if (query.SubclassId is { } subclassId)
        {
            filters.Add("subclass_id=$subclass_id");
            command.Parameters.AddWithValue("$subclass_id", subclassId);
        }
        var inventoryTypes = query.InventoryType.Split('|',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (inventoryTypes.Length > 0)
        {
            var parameters = new List<string>();
            for (var index = 0; index < inventoryTypes.Length; index++)
            {
                var parameter = $"$inventory_type_{index}";
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, inventoryTypes[index]);
            }
            filters.Add($"inventory_type IN ({string.Join(",", parameters)})");
        }
        return filters;
    }

    private BundledItemData? Read(string flavor, long itemId)
    {
        if (!IsAvailable)
            return null;
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"{SelectSql} WHERE flavor=$flavor AND id=$id AND found=1 LIMIT 1";
            command.Parameters.AddWithValue("$flavor", flavor);
            command.Parameters.AddWithValue("$id", itemId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private BundledItemSummary? ReadSummary(string flavor, long itemId)
    {
        if (!IsAvailable)
            return null;
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name,quality,class_id,subclass_id,icon_slug
                FROM items WHERE flavor=$flavor AND id=$id AND found=1 LIMIT 1
                """;
            command.Parameters.AddWithValue("$flavor", flavor);
            command.Parameters.AddWithValue("$id", itemId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new BundledItemSummary(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.GetString(4))
                : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static BundledItemData ReadRow(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
        reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8), reader.GetInt32(9),
        reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetInt64(13), reader.GetInt32(14),
        reader.GetBoolean(15), reader.GetBoolean(16), reader.GetString(17), Decompress((byte[])reader[18]));

    private static string Decompress(byte[] compressed)
    {
        using var source = new MemoryStream(compressed, false);
        using var brotli = new BrotliStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = brotli.Read(buffer)) > 0)
        {
            total += read;
            if (total > MaximumPreviewBytes)
                throw new InvalidDataException("Bundled item preview exceeds the allowed size.");
            target.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(target.ToArray());
    }

    private static bool TryItemId(string itemString, out long itemId)
    {
        itemId = 0;
        if (!itemString.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
            return false;
        var value = itemString.AsSpan(2);
        var separator = value.IndexOf(':');
        if (separator >= 0)
            value = value[..separator];
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out itemId) && itemId > 0;
    }

    private static string ResolveFlavor(string flavor)
    {
        var value = flavor.Trim().ToLowerInvariant();
        if (value.Contains("anniversary", StringComparison.Ordinal) || value is "tbc" or "bcc")
            return "tbc";
        if (value is "classic" || value.Contains("era", StringComparison.Ordinal) ||
            value.Contains("vanilla", StringComparison.Ordinal))
            return "vanilla";
        if (value.Contains("progress", StringComparison.Ordinal) || value.Contains("mop", StringComparison.Ordinal))
            return "mop";
        return "retail";
    }

    private static string ResolveDatabasePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "items.db"),
            Path.Combine(AppContext.BaseDirectory, "items.db")
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private const string SelectSql = """
        SELECT flavor,id,name,quality,quality_type,level,required_level,class_id,class_name,
               subclass_id,subclass_name,inventory_type,inventory_name,sell_price,maximum_count,
               is_equippable,is_stackable,icon_slug,preview_br
        FROM items
        """;
}
