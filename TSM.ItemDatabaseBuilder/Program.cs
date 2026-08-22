using System.IO.Compression;
using System.Text.Json;
using TSM.ItemDatabaseBuilder;

if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
{
    PrintHelp();
    return 0;
}

if (args.Contains("--import-jsonl", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var inputs = Values(args, "--import-jsonl");
        var database = Value(args, "--database") ??
                       throw new ArgumentException("--database is required with --import-jsonl.");
        var result = await ItemDatabaseSqliteBuilder.BuildAsync(inputs, database, CancellationToken.None);
        Console.WriteLine($"Wrote SQLite database: {database}");
        Console.WriteLine($"Imported: {result.Found:N0} available, {result.Unavailable:N0} unavailable.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 1;
    }
}

try
{
    var options = BuilderOptions.Parse(args);
    var clientId = Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_ID");
    var clientSecret = Environment.GetEnvironmentVariable("BLIZZARD_CLIENT_SECRET");
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        throw new InvalidOperationException(
            "Set BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET before running the builder.");

    var completed = options.Refresh ? [] : ReadCompletedKeys(options.OutputPath);
    var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
    Directory.CreateDirectory(outputDirectory);
    await using var output = new FileStream(options.OutputPath, FileMode.Append, FileAccess.Write, FileShare.Read);
    await using var writer = new StreamWriter(output);
    using var writeLock = new SemaphoreSlim(1, 1);
    using var api = new BlizzardApiClient(clientId, clientSecret, options.Region, options.Locale,
        options.RequestsPerSecond, options.MaximumAttempts);
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };

    var idsByCatalog = new Dictionary<ItemCatalog, IReadOnlyList<long>>();
    if (options.DiscoverIds)
    {
        foreach (var catalog in options.Catalogs)
            idsByCatalog[catalog] = await api.DiscoverItemIdsAsync(catalog.Namespace, cancellation.Token);
        if (!string.IsNullOrWhiteSpace(options.DiscoveredIdsPath))
        {
            var discoveredPath = Path.GetFullPath(options.DiscoveredIdsPath);
            Directory.CreateDirectory(Path.GetDirectoryName(discoveredPath)!);
            await File.WriteAllLinesAsync(discoveredPath,
                idsByCatalog.Values.SelectMany(ids => ids).Distinct().Order().Select(id => id.ToString()),
                cancellation.Token);
        }
    }
    else
    {
        var itemIds = options.ReadItemIds();
        foreach (var catalog in options.Catalogs)
            idsByCatalog[catalog] = itemIds;
    }

    var work = idsByCatalog
        .SelectMany(entry => entry.Value.Select(itemId => (Catalog: entry.Key, ItemId: itemId)))
        .Where(item => !completed.Contains($"{item.Catalog.Flavor}:{item.ItemId}"))
        .ToArray();
    var total = idsByCatalog.Values.Sum(ids => ids.Count);
    var resumed = total - work.Length;
    var processed = resumed;
    var written = 0;
    var missing = 0;
    var consoleLock = new object();
    await Parallel.ForEachAsync(work, new ParallelOptions
    {
        MaxDegreeOfParallelism = options.Concurrency,
        CancellationToken = cancellation.Token
    }, async (entry, itemCancellationToken) =>
    {
        var catalog = entry.Catalog;
        var itemId = entry.ItemId;
        using var item = await api.GetItemAsync(itemId, catalog.Namespace, itemCancellationToken);
        ItemDatabaseRecord record;
        if (item is null)
        {
            record = ItemDatabaseRecord.CreateMissing(catalog, options.Region, options.Locale, itemId);
            Interlocked.Increment(ref missing);
        }
        else
        {
            using var media = await api.GetItemMediaAsync(itemId, catalog.Namespace, itemCancellationToken);
            using var emptyMedia = JsonDocument.Parse("{}");
            record = ItemDatabaseRecord.Create(catalog, options.Region, options.Locale, itemId,
                item.RootElement, media?.RootElement ?? emptyMedia.RootElement);
            Interlocked.Increment(ref written);
        }
        await writeLock.WaitAsync(itemCancellationToken);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(record));
            await writer.FlushAsync(itemCancellationToken);
        }
        finally
        {
            writeLock.Release();
        }
        var current = Interlocked.Increment(ref processed);
        if (current == total || current % 100 == 0)
        {
            lock (consoleLock)
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] [{current:N0}/{total:N0}] " +
                                  $"{catalog.Flavor} item {itemId}");
        }
    });

    if (options.ManifestPath is not null)
        await WriteManifestAsync(options.OutputPath, options.ManifestPath, cancellation.Token);
    Console.WriteLine($"Finished: {written:N0} written, {missing:N0} unavailable, {resumed:N0} resumed.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Completed records remain in the output and will be resumed next time.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static HashSet<string> ReadCompletedKeys(string path)
{
    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!File.Exists(path))
        return keys;
    foreach (var line in File.ReadLines(path))
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var flavor = root.GetProperty("Flavor").GetString();
            var id = root.GetProperty("Id").GetInt64();
            if (!string.IsNullOrWhiteSpace(flavor))
                keys.Add($"{flavor}:{id}");
        }
        catch (JsonException)
        {
        }
    }
    return keys;
}

static async Task WriteManifestAsync(string databasePath, string manifestPath,
    CancellationToken cancellationToken)
{
    var icons = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(databasePath))
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var flavor = root.GetProperty("Flavor").GetString() ?? string.Empty;
            var id = root.GetProperty("Id").GetInt64().ToString();
            var iconUrl = root.GetProperty("IconUrl").GetString() ?? string.Empty;
            var icon = Path.GetFileNameWithoutExtension(new Uri(iconUrl).AbsolutePath);
            if (flavor.Length == 0 || icon.Length == 0)
                continue;
            if (!icons.TryGetValue(flavor, out var flavorIcons))
                icons[flavor] = flavorIcons = new Dictionary<string, string>();
            flavorIcons[id] = icon;
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException)
        {
        }
    }
    var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
    Directory.CreateDirectory(directory);
    var temporaryPath = manifestPath + $".{Guid.NewGuid():N}.tmp";
    try
    {
        await using var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var brotli = new BrotliStream(file, CompressionLevel.SmallestSize);
        await JsonSerializer.SerializeAsync(brotli, icons, cancellationToken: cancellationToken);
        await brotli.FlushAsync(cancellationToken);
        File.Move(temporaryPath, manifestPath, true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
    Console.WriteLine($"Wrote icon manifest: {manifestPath}");
}

static void PrintHelp()
{
    Console.WriteLine("""
        TSM Blizzard item database builder

        Required environment variables:
          BLIZZARD_CLIENT_ID
          BLIZZARD_CLIENT_SECRET

        Usage:
          dotnet run --project TSM.ItemDatabaseBuilder -- --ids FILE [options]

        Options:
          --ids FILE                   Text, CSV, JSON, or JSONL containing item IDs
          --discover-ids               Discover IDs from each selected Blizzard namespace
          --write-discovered-ids FILE  Save discovered IDs for reproducible future builds
          --output FILE                Resumable JSONL database output
          --manifest FILE              Optional Brotli icon manifest output
          --region us|eu|kr|tw         API region (default: eu)
          --locale LOCALE              API locale (default depends on region)
          --all-active                 Vanilla, TBC, MoP Classic, and Retail
          --edition EDITION            vanilla, tbc, mop, or retail; repeat as needed
          --catalog FLAVOR=NAMESPACE   Repeat for each game flavor
          --requests-per-second N      Shared request pace from 1 to 50 (default: 12)
          --concurrency N              Concurrent item workers from 1 to 64 (default: 8)
          --max-attempts N             Retry attempts from 1 to 20 (default: 6)
          --import-jsonl FILE          Build SQLite from JSONL; repeat for each flavor
          --database FILE              SQLite output used with --import-jsonl
          --refresh                    Fetch records even when already present
          --help                       Show this help

        Example:
          dotnet run --project TSM.ItemDatabaseBuilder -- --ids item-ids.txt \
            --all-active \
            --output artifacts/item-data/items.jsonl \
            --manifest TSM/Assets/item-icons.json.br
    """);
}

static IReadOnlyList<string> Values(string[] arguments, string name)
{
    var values = new List<string>();
    for (var index = 0; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            continue;
        if (++index >= arguments.Length)
            throw new ArgumentException($"{name} requires a value.");
        values.Add(arguments[index]);
    }
    if (values.Count == 0)
        throw new ArgumentException($"At least one {name} is required.");
    return values;
}

static string? Value(string[] arguments, string name)
{
    var values = Values(arguments, name);
    return values.Count == 1 ? values[0] : throw new ArgumentException($"{name} may only be supplied once.");
}
