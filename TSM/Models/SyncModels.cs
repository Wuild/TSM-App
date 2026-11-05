using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSM.Models;

public sealed class TsmStatusResponse
{
    [JsonPropertyName("appVersion")]
    public int AppVersion { get; init; }

    [JsonPropertyName("addons")]
    public List<TsmAddonVersion> Addons { get; init; } = [];

    [JsonPropertyName("realms")]
    public List<TsmRealmEntry> Realms { get; init; } = [];

    [JsonPropertyName("regions")]
    public List<TsmRealmEntry> Regions { get; init; } = [];

    [JsonPropertyName("realms-Progression")]
    public List<TsmRealmEntry> ProgressionRealms { get; init; } = [];

    [JsonPropertyName("regions-Progression")]
    public List<TsmRealmEntry> ProgressionRegions { get; init; } = [];

    [JsonPropertyName("extraClassicRealms")]
    public List<TsmRealmEntry> ClassicRealms { get; init; } = [];

    [JsonPropertyName("extraClassicRegions")]
    public List<TsmRealmEntry> ClassicRegions { get; init; } = [];

    [JsonPropertyName("extraAnniversaryRealms")]
    public List<TsmRealmEntry> AnniversaryRealms { get; init; } = [];

    [JsonPropertyName("extraAnniversaryRegions")]
    public List<TsmRealmEntry> AnniversaryRegions { get; init; } = [];

    [JsonPropertyName("extraForeverRealms")]
    public List<TsmRealmEntry> ForeverRealms { get; init; } = [];

    [JsonPropertyName("extraForeverRegions")]
    public List<TsmRealmEntry> ForeverRegions { get; init; } = [];

    [JsonPropertyName("addonMessage")]
    public TsmAddonMessage AddonMessage { get; init; } = new();
}

public sealed class TsmAddonVersion
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version_str")]
    public string Version { get; init; } = string.Empty;
}

public sealed class TsmAddonMessage
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;
}

public sealed class TsmRealmEntry
{
    [JsonPropertyName("id")]
    public JsonElement Id { get; init; }

    [JsonPropertyName("ahId")]
    public JsonElement AuctionHouseId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; init; } = string.Empty;

    [JsonPropertyName("appDataStrings")]
    public Dictionary<string, TsmDataString> AppDataStrings { get; init; } = new(StringComparer.Ordinal);
}

public sealed record TsmRealmChoice(string GameVersion, string GameLabel, string Region, string Name, string Id)
{
    public string DisplayName => $"{GameLabel}-{Region}-{Name}";
}

public sealed class TsmDataString
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("lastModified")]
    public long LastModified { get; init; }
}

public sealed record AppDataUpdate(string Type, string Realm, string DataBlob, long Timestamp);

public sealed record DataSyncResult(
    int DownloadedEntries,
    int WrittenFiles,
    IReadOnlyList<string> Errors,
    int NewsId = 0,
    string NewsMessage = "",
    int LatestAppVersion = 0,
    IReadOnlyList<string>? UpdatedAddons = null,
    int UploadedDataSets = 0,
    IReadOnlyList<string>? DataErrors = null)
{
    public bool DataSyncSucceeded => (DataErrors ?? Errors).Count == 0;
}

public sealed record TsmUploadRequest(
    string Endpoint,
    IReadOnlyList<string> Path,
    object Data,
    bool BinaryContent = false);

public sealed record TsmRemoteBackup(string SystemId, string Account, long Timestamp, bool Keep)
{
    public string RemoteName => $"{SystemId}_{Account}_{Timestamp}.zip";
}

public sealed record TsmBackupCatalog(
    IReadOnlyList<TsmRemoteBackup> Backups,
    IReadOnlySet<string> MissingNames);
