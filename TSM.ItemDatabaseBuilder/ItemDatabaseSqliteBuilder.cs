using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TSM.ItemDatabaseBuilder;

internal sealed record ItemDatabaseImportResult(int Found, int Unavailable);

internal static class ItemDatabaseSqliteBuilder
{
    public static async Task<ItemDatabaseImportResult> BuildAsync(IReadOnlyList<string> inputs, string outputPath,
        CancellationToken cancellationToken)
    {
        foreach (var input in inputs)
        {
            if (!File.Exists(input))
                throw new FileNotFoundException("JSONL item database was not found.", input);
        }
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = fullOutputPath + $".{Guid.NewGuid():N}.tmp";
        var found = 0;
        var unavailable = 0;
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;",
                cancellationToken);
            await ExecuteAsync(connection, Schema, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var insert = CreateInsert(connection, transaction);
            foreach (var input in inputs)
            {
                foreach (var line in File.ReadLines(input))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var isFound = Boolean(root, "Found");
                    Bind(insert, root, isFound);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            found = await CountAsync(connection, (SqliteTransaction)transaction, true, cancellationToken);
            unavailable = await CountAsync(connection, (SqliteTransaction)transaction, false, cancellationToken);
            await using (var metadata = connection.CreateCommand())
            {
                metadata.Transaction = (SqliteTransaction)transaction;
                metadata.CommandText = "INSERT INTO metadata(key,value) VALUES('schema_version','1')," +
                                       "('generated_at',$generated),('available_items',$found)," +
                                       "('unavailable_items',$unavailable);";
                metadata.Parameters.AddWithValue("$generated", DateTimeOffset.UtcNow.ToString("O",
                    CultureInfo.InvariantCulture));
                metadata.Parameters.AddWithValue("$found", found.ToString(CultureInfo.InvariantCulture));
                metadata.Parameters.AddWithValue("$unavailable", unavailable.ToString(CultureInfo.InvariantCulture));
                await metadata.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            await ExecuteAsync(connection, "ANALYZE; PRAGMA optimize;", cancellationToken);
            await using (var integrity = connection.CreateCommand())
            {
                integrity.CommandText = "PRAGMA integrity_check;";
                var result = await integrity.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(result?.ToString(), "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SQLite integrity check failed: {result}");
            }
            await connection.CloseAsync();
            File.Move(temporaryPath, fullOutputPath, true);
            return new ItemDatabaseImportResult(found, unavailable);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static SqliteCommand CreateInsert(SqliteConnection connection, System.Data.Common.DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO items(
                flavor,id,found,namespace,region,locale,fetched_at,name,quality,quality_type,level,
                required_level,class_id,class_name,subclass_id,subclass_name,inventory_type,inventory_name,
                sell_price,maximum_count,is_equippable,is_stackable,icon_slug,preview_br)
            VALUES(
                $flavor,$id,$found,$namespace,$region,$locale,$fetched_at,$name,$quality,$quality_type,$level,
                $required_level,$class_id,$class_name,$subclass_id,$subclass_name,$inventory_type,$inventory_name,
                $sell_price,$maximum_count,$is_equippable,$is_stackable,$icon_slug,$preview_br)
            ON CONFLICT(flavor,id) DO UPDATE SET
                found=excluded.found, namespace=excluded.namespace, region=excluded.region,
                locale=excluded.locale, fetched_at=excluded.fetched_at, name=excluded.name,
                quality=excluded.quality, quality_type=excluded.quality_type, level=excluded.level,
                required_level=excluded.required_level, class_id=excluded.class_id,
                class_name=excluded.class_name, subclass_id=excluded.subclass_id,
                subclass_name=excluded.subclass_name, inventory_type=excluded.inventory_type,
                inventory_name=excluded.inventory_name, sell_price=excluded.sell_price,
                maximum_count=excluded.maximum_count, is_equippable=excluded.is_equippable,
                is_stackable=excluded.is_stackable, icon_slug=excluded.icon_slug,
                preview_br=excluded.preview_br;
            """;
        foreach (var name in new[]
                 {
                     "$flavor", "$id", "$found", "$namespace", "$region", "$locale", "$fetched_at", "$name",
                     "$quality", "$quality_type", "$level", "$required_level", "$class_id", "$class_name",
                     "$subclass_id", "$subclass_name", "$inventory_type", "$inventory_name", "$sell_price",
                     "$maximum_count", "$is_equippable", "$is_stackable", "$icon_slug", "$preview_br"
                 })
            command.Parameters.Add(new SqliteParameter(name, null));
        return command;
    }

    private static void Bind(SqliteCommand command, JsonElement root, bool found)
    {
        var item = root.TryGetProperty("item", out var itemValue) ? itemValue : default;
        var preview = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("preview_item", out var previewValue)
            ? previewValue
            : default;
        Set(command, "$flavor", Text(root, "Flavor"));
        Set(command, "$id", Long(root, "Id"));
        Set(command, "$found", found ? 1 : 0);
        Set(command, "$namespace", Text(root, "Namespace"));
        Set(command, "$region", Text(root, "Region"));
        Set(command, "$locale", Text(root, "Locale"));
        Set(command, "$fetched_at", Text(root, "FetchedAt"));
        Set(command, "$name", Text(root, "Name"));
        Set(command, "$quality", Long(root, "Quality"));
        Set(command, "$quality_type", Text(root, "QualityType"));
        Set(command, "$level", Long(root, "Level"));
        Set(command, "$required_level", Long(root, "RequiredLevel"));
        Set(command, "$class_id", Long(root, "ClassId"));
        Set(command, "$class_name", NestedText(item, "item_class", "name"));
        Set(command, "$subclass_id", Long(root, "SubclassId"));
        Set(command, "$subclass_name", NestedText(item, "item_subclass", "name"));
        Set(command, "$inventory_type", Text(root, "InventoryType"));
        Set(command, "$inventory_name", NestedText(item, "inventory_type", "name"));
        Set(command, "$sell_price", Long(root, "SellPrice"));
        Set(command, "$maximum_count", Long(root, "MaximumCount"));
        Set(command, "$is_equippable", Boolean(root, "IsEquippable") ? 1 : 0);
        Set(command, "$is_stackable", Boolean(root, "IsStackable") ? 1 : 0);
        Set(command, "$icon_slug", IconSlug(Text(root, "IconUrl")));
        Set(command, "$preview_br", Compress(preview.ValueKind == JsonValueKind.Object ? preview.GetRawText() : "{}"));
    }

    private static void Set(SqliteCommand command, string name, object value) => command.Parameters[name].Value = value;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static long Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var result) ? result : 0;

    private static bool Boolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static string NestedText(JsonElement element, string property, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? Text(value, name)
            : string.Empty;

    private static string IconSlug(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;
        return Path.GetFileNameWithoutExtension(uri.AbsolutePath).ToLowerInvariant();
    }

    private static byte[] Compress(string value)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, true))
            brotli.Write(Encoding.UTF8.GetBytes(value));
        return output.ToArray();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, SqliteTransaction transaction,
        bool found, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM items WHERE found=$found;";
        command.Parameters.AddWithValue("$found", found ? 1 : 0);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private const string Schema = """
        PRAGMA user_version=1;
        CREATE TABLE metadata(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE items(
            flavor TEXT NOT NULL COLLATE NOCASE,
            id INTEGER NOT NULL,
            found INTEGER NOT NULL,
            namespace TEXT NOT NULL,
            region TEXT NOT NULL,
            locale TEXT NOT NULL,
            fetched_at TEXT NOT NULL,
            name TEXT NOT NULL COLLATE NOCASE,
            quality INTEGER NOT NULL,
            quality_type TEXT NOT NULL,
            level INTEGER NOT NULL,
            required_level INTEGER NOT NULL,
            class_id INTEGER NOT NULL,
            class_name TEXT NOT NULL COLLATE NOCASE,
            subclass_id INTEGER NOT NULL,
            subclass_name TEXT NOT NULL COLLATE NOCASE,
            inventory_type TEXT NOT NULL,
            inventory_name TEXT NOT NULL COLLATE NOCASE,
            sell_price INTEGER NOT NULL,
            maximum_count INTEGER NOT NULL,
            is_equippable INTEGER NOT NULL,
            is_stackable INTEGER NOT NULL,
            icon_slug TEXT NOT NULL,
            preview_br BLOB NOT NULL,
            PRIMARY KEY(flavor,id)
        ) WITHOUT ROWID;
        CREATE INDEX items_name ON items(flavor,name);
        CREATE INDEX items_class ON items(flavor,class_id,subclass_id,inventory_type,quality);
        CREATE INDEX items_quality ON items(flavor,quality,name);
        """;
}
