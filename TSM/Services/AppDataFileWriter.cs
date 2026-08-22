using System.Text;
using TSM.Models;

namespace TSM.Services;

public sealed class AppDataFileWriter(AppDataCatalogParser parser)
{
    public async Task WriteAsync(
        string path,
        IEnumerable<AppDataUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var entries = new Dictionary<(string Type, string Realm), StoredEntry>();

        if (File.Exists(path))
        {
            foreach (var entry in parser.ParseFile(path).Entries)
            {
                ValidateKey(entry.Type, entry.Realm);
                entries[(entry.Type, entry.Realm)] =
                    new StoredEntry(entry.Type, entry.Realm, entry.UpdatedAtUnix, entry.RawPayload);
            }
        }

        foreach (var update in updates)
        {
            ValidateKey(update.Type, update.Realm);
            if (string.IsNullOrWhiteSpace(update.DataBlob))
                throw new InvalidDataException($"The {update.Type}/{update.Realm} data blob was empty.");
            entries[(update.Type, update.Realm)] =
                new StoredEntry(update.Type, update.Realm, update.Timestamp, Render(update));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new InvalidOperationException("AppData.lua has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".AppData.{Guid.NewGuid():N}.tmp");
        var content = string.Join('\n', entries.Values
            .OrderBy(entry => entry.Type, StringComparer.Ordinal)
            .ThenBy(entry => entry.Realm, StringComparer.Ordinal)
            .Select(entry => $"{entry.Payload} --<{entry.Type},{entry.Realm},{entry.Timestamp}>")
        ) + "\n";

        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string Render(AppDataUpdate update)
    {
        var blob = update.DataBlob.Trim();
        if (blob.StartsWith("return ", StringComparison.Ordinal))
            blob = blob[7..].TrimStart();
        var equals = string.Empty;
        while (blob.Contains($"]{equals}]", StringComparison.Ordinal))
            equals += "=";
        return $"select(2, ...).LoadData(\"{EscapeLua(update.Type)}\",\"{EscapeLua(update.Realm)}\",[{equals}[return {blob}]{equals}])";
    }

    private static void ValidateKey(string type, string realm)
    {
        if (!AppDataCatalogParser.SupportedTypes.Contains(type))
            throw new InvalidDataException($"Unsupported AppData type '{type}'.");
        if (realm.IndexOfAny([',', '\r', '\n']) >= 0)
            throw new InvalidDataException("AppData realm names cannot contain commas or newlines.");
    }

    private static string EscapeLua(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record StoredEntry(string Type, string Realm, long Timestamp, string Payload);
}
