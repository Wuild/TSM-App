using System.Collections.Concurrent;
using TSM.Models;

namespace TSM.Services;

public sealed class AppDataSyncService(
    ITsmApiClient apiClient,
    AppDataCatalogParser parser,
    AppDataFileWriter writer,
    AccountingService accountingService,
    AddonUpdateService? addonUpdater = null,
    TsmDataUploadService? dataUploader = null,
    Func<bool>? uploadsEnabled = null)
{
    public async Task<DataSyncResult> SyncAsync(
        IReadOnlyList<WowInstallation> installations,
        IReadOnlyList<TsmAccount> accounts,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var status = await apiClient.GetStatusAsync(channel, cancellationToken);
        var errors = new List<string>();
        var dataErrors = new List<string>();
        void AddDataError(string message)
        {
            errors.Add(message);
            dataErrors.Add(message);
        }
        IReadOnlyList<string> updatedAddons = [];
        try
        {
            updatedAddons = await (addonUpdater ?? new AddonUpdateService(apiClient))
                .UpdateInstalledAsync(installations, status.Addons, channel, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add($"Addon update failed: {exception.Message}");
        }
        var downloadCache = new ConcurrentDictionary<string, Task<string>>(StringComparer.Ordinal);
        var downloaded = 0;
        var writtenFiles = 0;

        foreach (var installation in installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var helperDirectory = FindAppHelperDirectory(installation.AddOnsPath);
            if (helperDirectory is null)
                continue;

            var mapping = SelectMapping(Path.GetFileName(installation.RootPath), status);
            if (mapping is null)
                continue;

            var appDataPath = Path.Combine(helperDirectory, "AppData.lua");
            AppDataCatalog catalog;
            try
            {
                catalog = File.Exists(appDataPath) ? parser.ParseFile(appDataPath) : new AppDataCatalog([]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddDataError($"{installation.Name}: could not read AppData.lua ({exception.Message})");
                continue;
            }

            var localRealms = mapping.FilterToLocalRealms
                ? FindLocalRealms(accounts.Where(account =>
                    string.Equals(account.GameVersion, installation.Name, StringComparison.OrdinalIgnoreCase)),
                    errors, dataErrors)
                : null;
            var realmEntries = localRealms is null
                ? mapping.Realms
                : mapping.Realms.Where(entry => localRealms.Any(realm =>
                    IsRealmDataName(entry.Name, realm))).ToArray();
            var localRegions = localRealms is null
                ? null
                : realmEntries.Select(entry => entry.Region).Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var regionEntries = localRegions is null
                ? mapping.Regions
                : mapping.Regions.Where(entry => localRegions.Contains(entry.Name)).ToArray();

            var updates = new List<AppDataUpdate>
            {
                CreateAppInfo(status)
            };
            foreach (var entry in realmEntries.Concat(regionEntries))
            {
                foreach (var (type, dataString) in entry.AppDataStrings)
                {
                    if (!AppDataCatalogParser.SupportedTypes.Contains(type) ||
                        string.IsNullOrWhiteSpace(dataString.Url) ||
                        dataString.LastModified <= catalog.LastUpdate(type, entry.Name))
                        continue;

                    try
                    {
                        var task = downloadCache.GetOrAdd(dataString.Url,
                            url => apiClient.DownloadDataAsync(url, cancellationToken));
                        var blob = await task;
                        if (string.IsNullOrWhiteSpace(blob) ||
                            blob.Contains("<html", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("the download was empty or HTML");
                        updates.Add(new AppDataUpdate(type, entry.Name, blob, dataString.LastModified));
                        downloaded++;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        downloadCache.TryRemove(dataString.Url, out _);
                        AddDataError($"{installation.Name} · {entry.Name} · {type}: {exception.Message}");
                    }
                }
            }

            try
            {
                await writer.WriteAsync(appDataPath, updates, cancellationToken);
                writtenFiles++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddDataError($"{installation.Name}: could not write AppData.lua ({exception.Message})");
            }
        }

        var uploaded = 0;
        if (dataUploader is not null && (uploadsEnabled?.Invoke() ?? true))
        {
            try
            {
                uploaded = await dataUploader.UploadAsync(accounts, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"TSM data upload failed: {exception.Message}");
            }
        }

        return new DataSyncResult(downloaded, writtenFiles, errors,
            status.AddonMessage.Id, status.AddonMessage.Message, status.AppVersion, updatedAddons, uploaded,
            dataErrors);
    }

    private HashSet<string> FindLocalRealms(IEnumerable<TsmAccount> accounts, ICollection<string> errors,
        ICollection<string> dataErrors)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var account in accounts)
        {
            try
            {
                result.UnionWith(accountingService.FindRealms(account));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
                var message = $"{account.DisplayName}: could not identify local realms ({exception.Message})";
                errors.Add(message);
                dataErrors.Add(message);
            }
        }
        return result;
    }

    private static AppDataUpdate CreateAppInfo(TsmStatusResponse status)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var version = status.AppVersion > 0 ? status.AppVersion : TsmLegacyApiClient.ApplicationVersion;
        var message = EscapeLua(status.AddonMessage.Message);
        var blob = $"{{version={version},lastSync={now},message={{id={status.AddonMessage.Id},msg=\"{message}\"}},news={{}}}}";
        return new AppDataUpdate("APP_INFO", "Global", blob, now);
    }

    private static Mapping? SelectMapping(string gameDirectory, TsmStatusResponse status) => gameDirectory switch
    {
        "_retail_" or "_retail_beta_" or "_retail_ptr_" or "_ptr_" => new Mapping(status.Realms, status.Regions, false),
        "_classic_" or "_classic_beta_" or "_classic_ptr_" => new Mapping(status.ProgressionRealms, status.ProgressionRegions, true),
        "_classic_era_" or "_classic_era_ptr_" => new Mapping(status.ClassicRealms, status.ClassicRegions, true),
        "_anniversary_" or "_anniversary_ptr_" => new Mapping(status.AnniversaryRealms, status.AnniversaryRegions, true),
        _ => null
    };

    private static string? FindAppHelperDirectory(string addonsPath)
    {
        try
        {
            return Directory.EnumerateDirectories(addonsPath).FirstOrDefault(directory =>
                string.Equals(Path.GetFileName(directory), "TradeSkillMaster_AppHelper",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string EscapeLua(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool IsRealmDataName(string dataName, string localRealm) =>
        string.Equals(dataName, localRealm, StringComparison.OrdinalIgnoreCase) ||
        dataName.StartsWith(localRealm + "-", StringComparison.OrdinalIgnoreCase);

    private sealed record Mapping(
        IReadOnlyList<TsmRealmEntry> Realms,
        IReadOnlyList<TsmRealmEntry> Regions,
        bool FilterToLocalRealms);
}
