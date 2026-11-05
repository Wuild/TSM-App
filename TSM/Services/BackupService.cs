using System.IO.Compression;
using System.Diagnostics;
using TSM.Models;

namespace TSM.Services;

public sealed class BackupService(AppPaths paths, ITsmApiClient apiClient, Func<string> systemId)
{
    private static readonly TimeSpan BackupPeriod = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<BackupEntry>> SyncAsync(IReadOnlyList<TsmAccount> accounts,
        bool cloudEnabled, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.BackupDirectory);
        PurgeExpired();
        foreach (var account in accounts)
            CreateIfNeeded(account, false);
        var local = ReadLocal().ToList();
        if (!cloudEnabled || !apiClient.IsPremium || !apiClient.SupportsEndpoint("backup"))
            return local.OrderByDescending(value => value.Created).ToArray();

        var remote = await apiClient.GetBackupsAsync(cancellationToken);
        foreach (var backup in local)
        {
            var remoteName = RemoteName(backup.Account, backup.Timestamp);
            if (remote.Backups.Any(value => string.Equals(value.RemoteName, remoteName,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            await apiClient.UploadBackupAsync(remoteName, await File.ReadAllBytesAsync(backup.LocalPath,
                cancellationToken), cancellationToken);
        }
        remote = await apiClient.GetBackupsAsync(cancellationToken);
        return Merge(local, remote.Backups);
    }

    public BackupEntry CreateNow(TsmAccount account)
    {
        Directory.CreateDirectory(paths.BackupDirectory);
        return Create(account, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public IReadOnlyList<BackupEntry> CreateAllNow(IReadOnlyList<TsmAccount> accounts) =>
        accounts.Select(CreateNow).ToArray();

    public async Task RestoreAsync(BackupEntry backup, IReadOnlyList<TsmAccount> accounts,
        bool cloudEnabled, CancellationToken cancellationToken = default)
    {
        if (IsWowRunning())
            throw new InvalidOperationException("Close World of Warcraft before restoring a backup.");
        var account = accounts.FirstOrDefault(value => string.Equals(AccountKey(value), backup.Account,
            StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
            "The backup's WoW account is not installed on this computer.");
        CreateNow(account);
        byte[] archive;
        if (backup.IsLocal && File.Exists(backup.LocalPath))
            archive = await File.ReadAllBytesAsync(backup.LocalPath, cancellationToken);
        else
        {
            if (!cloudEnabled || !apiClient.IsPremium || !apiClient.SupportsEndpoint("backup") ||
                backup.RemoteName.Length == 0)
                throw new InvalidOperationException("Cloud backup access is unavailable.");
            archive = await apiClient.DownloadBackupAsync(backup.RemoteName, cancellationToken);
        }
        using var stream = new MemoryStream(archive, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var targetDirectory = Path.GetDirectoryName(account.SavedVariablesPath)!;
        var restored = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry.FullName);
            if (name is not ("TradeSkillMaster.lua" or "TradeSkillMaster_AppHelper.lua"))
                continue;
            var target = Path.Combine(targetDirectory, name);
            await using var source = entry.Open();
            await using var destination = new FileStream(target + ".restore.tmp", FileMode.Create,
                FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            destination.Close();
            File.Move(target + ".restore.tmp", target, true);
            restored++;
        }
        if (restored == 0)
            throw new InvalidDataException("The backup did not contain recognized TSM SavedVariables files.");
    }

    private void CreateIfNeeded(TsmAccount account, bool force)
    {
        var key = AccountKey(account);
        var latest = ReadLocal().Where(value => string.Equals(value.Account, key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Created).FirstOrDefault();
        var newestSource = new[] { account.SavedVariablesPath, account.AppHelperSavedVariablesPath }
            .Where(File.Exists).Select(path => new FileInfo(path).LastWriteTimeUtc).DefaultIfEmpty().Max();
        if (!force && latest is not null &&
            (DateTimeOffset.UtcNow - latest.Created.ToUniversalTime() < BackupPeriod ||
             newestSource <= latest.Created.UtcDateTime))
            return;
        Create(account, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private BackupEntry Create(TsmAccount account, long timestamp)
    {
        var key = AccountKey(account);
        var path = Path.Combine(paths.BackupDirectory, $"{key}_{timestamp}.zip");
        while (File.Exists(path))
        {
            timestamp++;
            path = Path.Combine(paths.BackupDirectory, $"{key}_{timestamp}.zip");
        }
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        foreach (var source in new[] { account.SavedVariablesPath, account.AppHelperSavedVariablesPath }.Where(File.Exists))
            archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.SmallestSize);
        return new BackupEntry(systemId(), key, timestamp, true, false, false, path, RemoteName(key, timestamp));
    }

    private IReadOnlyList<BackupEntry> ReadLocal()
    {
        if (!Directory.Exists(paths.BackupDirectory))
            return [];
        var result = new List<BackupEntry>();
        foreach (var path in Directory.EnumerateFiles(paths.BackupDirectory, "*.zip"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var separator = name.LastIndexOf('_');
            if (separator <= 0 || !long.TryParse(name[(separator + 1)..], out var timestamp))
                continue;
            var account = name[..separator];
            result.Add(new BackupEntry(systemId(), account, timestamp, true, false, false, path,
                RemoteName(account, timestamp)));
        }
        return result;
    }

    private IReadOnlyList<BackupEntry> Merge(IReadOnlyList<BackupEntry> local,
        IReadOnlyList<TsmRemoteBackup> remote)
    {
        var result = local.ToDictionary(value => value.RemoteName, StringComparer.OrdinalIgnoreCase);
        foreach (var item in remote)
        {
            if (result.TryGetValue(item.RemoteName, out var existing))
                result[item.RemoteName] = existing with { IsRemote = true, Keep = item.Keep };
            else
                result[item.RemoteName] = new BackupEntry(item.SystemId, item.Account, item.Timestamp, false, true,
                    item.Keep, RemoteName: item.RemoteName);
        }
        return result.Values.OrderByDescending(value => value.Created).ToArray();
    }

    private void PurgeExpired()
    {
        if (!Directory.Exists(paths.BackupDirectory))
            return;
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var backup in ReadLocal().Where(value => value.Created < cutoff))
            File.Delete(backup.LocalPath);
    }

    private string RemoteName(string account, long timestamp) => $"{systemId()}_{account}_{timestamp}.zip";

    private static string AccountKey(TsmAccount account)
    {
        var suffix = account.GameVersion switch
        {
            "Classic Era" => "-Classic",
            "Classic" => "-Progression",
            "Anniversary" => "-Anniversary",
            "Forever" => "-Forever",
            _ => string.Empty
        };
        return account.AccountName + suffix;
    }

    private static bool IsWowRunning()
    {
        try
        {
            return Process.GetProcesses().Any(process =>
            {
                using (process)
                    return process.ProcessName.StartsWith("Wow", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch
        {
            return false;
        }
    }
}
