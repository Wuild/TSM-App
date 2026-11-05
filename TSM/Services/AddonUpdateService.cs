using System.IO.Compression;
using TSM.Models;

namespace TSM.Services;

public sealed class AddonUpdateService(ITsmApiClient apiClient)
{
    internal const string AppHelperName = "TradeSkillMaster_AppHelper";
    private const long MaximumArchiveBytes = 100_000_000;
    private const long MaximumExpandedBytes = 250_000_000;

    public async Task<IReadOnlyList<string>> UpdateInstalledAsync(
        IEnumerable<WowInstallation> installations,
        IEnumerable<TsmAddonVersion> available,
        string channel,
        CancellationToken cancellationToken = default)
    {
        var targets = installations.ToArray();
        var updated = new List<string>();
        foreach (var addon in available)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValidAddonName(addon.Name) || string.IsNullOrWhiteSpace(addon.Version))
                continue;

            var outdated = FindOutdatedTargets(targets, addon.Name, addon.Version);
            if (outdated.Count == 0)
                continue;

            var archive = await DownloadArchiveAsync(addon.Name, channel, addon.Version, cancellationToken);

            foreach (var addonsDirectory in outdated)
                await InstallAsync(addonsDirectory, addon.Name, archive, cancellationToken);
            updated.Add(addon.Name);
        }
        return updated;
    }

    public async Task ReinstallAsync(
        string addonsDirectory,
        string addonName,
        string version,
        string channel,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAddonName(addonName) || string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("The selected addon does not have a valid update package.");
        if (FindDirectory(addonsDirectory, addonName) is null)
            throw new DirectoryNotFoundException($"The {addonName} addon is no longer installed in this client.");

        var archive = await DownloadArchiveAsync(addonName, channel, version, cancellationToken);
        await InstallAsync(addonsDirectory, addonName, archive, cancellationToken);
    }

    public async Task InstallAppHelperAsync(string addonsDirectory, string version, string channel,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("TSM did not provide a valid AppHelper package.");
        if (!Directory.Exists(addonsDirectory))
            throw new DirectoryNotFoundException("The selected WoW client no longer has an AddOns folder.");
        if (FindDirectory(addonsDirectory, AppHelperName) is not null)
            throw new IOException($"The {AppHelperName} addon is already installed in this client.");

        var archive = await DownloadArchiveAsync(AppHelperName, channel, version, cancellationToken);
        await InstallAsync(addonsDirectory, AppHelperName, archive, cancellationToken, replaceExisting: false);
    }

    public static async Task RemoveAsync(
        string addonsDirectory,
        string addonName,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAddonName(addonName))
            throw new InvalidOperationException("The selected addon name is invalid.");
        var directory = FindDirectory(addonsDirectory, addonName);
        if (directory is null)
            return;

        var parent = Path.GetFullPath(addonsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(target), addonName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected addon folder is outside the configured WoW client.");

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Delete(target, true);
        }, cancellationToken);
    }

    private async Task<byte[]> DownloadArchiveAsync(
        string addonName,
        string channel,
        string version,
        CancellationToken cancellationToken)
    {
        var archive = await apiClient.DownloadAddonAsync(addonName, channel, version, cancellationToken);
        if (archive.LongLength is 0 or > MaximumArchiveBytes)
            throw new InvalidDataException($"The {addonName} archive has an invalid size.");
        return archive;
    }

    private static List<string> FindOutdatedTargets(
        IEnumerable<WowInstallation> installations,
        string addonName,
        string latestVersion)
    {
        var result = new List<string>();
        foreach (var installation in installations)
        {
            var addonDirectory = FindDirectory(installation.AddOnsPath, addonName);
            if (addonDirectory is null)
                continue; // Automatic updates mirror the legacy app: no implicit fresh install.
            var toc = FindFile(addonDirectory, addonName + ".toc");
            var installed = toc is null ? string.Empty : ReadVersion(toc);
            if (installed is "@project-version@" or "@tsm-project-version@")
                continue;
            if (!string.Equals(installed.TrimStart('v', 'V'), latestVersion.TrimStart('v', 'V'),
                    StringComparison.OrdinalIgnoreCase))
                result.Add(installation.AddOnsPath);
        }
        return result;
    }

    private static async Task InstallAsync(
        string addonsDirectory,
        string addonName,
        byte[] archive,
        CancellationToken cancellationToken, bool replaceExisting = true)
    {
        var stagingRoot = Path.Combine(addonsDirectory, $".tsm-addon-{Guid.NewGuid():N}");
        var stagedAddon = Path.Combine(stagingRoot, addonName);
        var destination = FindDirectory(addonsDirectory, addonName) ?? Path.Combine(addonsDirectory, addonName);
        var rollback = Path.Combine(addonsDirectory, $".tsm-addon-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var stream = new MemoryStream(archive, false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, false);
            ValidateArchive(zip, addonName);
            foreach (var entry in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                    continue;
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var output = Path.GetFullPath(Path.Combine(stagingRoot, relative));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var input = entry.Open();
                await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous);
                await input.CopyToAsync(file, cancellationToken);
            }

            if (FindFile(stagedAddon, addonName + ".toc") is null)
                throw new InvalidDataException($"The {addonName} archive does not contain its expected TOC file.");

            cancellationToken.ThrowIfCancellationRequested();
            if (!replaceExisting)
            {
                if (FindDirectory(addonsDirectory, addonName) is not null)
                    throw new IOException($"The {addonName} addon is already installed in this client.");
                Directory.Move(stagedAddon, destination);
                return;
            }

            Directory.Move(destination, rollback);
            try
            {
                Directory.Move(stagedAddon, destination);
            }
            catch
            {
                if (!Directory.Exists(destination) && Directory.Exists(rollback))
                    Directory.Move(rollback, destination);
                throw;
            }
            try
            {
                Directory.Delete(rollback, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, true);
        }
    }

    private static void ValidateArchive(ZipArchive zip, string addonName)
    {
        if (zip.Entries.Count == 0)
            throw new InvalidDataException("The addon archive is empty.");
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal) ||
                !normalized.StartsWith(addonName + "/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The addon archive contains an unsafe or unexpected path.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000)
                throw new InvalidDataException("The addon archive contains a symbolic link.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded addon archive is too large.");
        }
    }

    private static bool IsValidAddonName(string value) => value.Length is > 0 and <= 100 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static string? FindDirectory(string parent, string name) => Directory.Exists(parent)
        ? Directory.EnumerateDirectories(parent).FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
        : null;

    private static string? FindFile(string parent, string name) => Directory.Exists(parent)
        ? Directory.EnumerateFiles(parent).FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
        : null;

    private static string ReadVersion(string tocPath)
    {
        foreach (var line in File.ReadLines(tocPath))
        {
            if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase))
                return line[(line.IndexOf(':') + 1)..].Trim();
        }
        return string.Empty;
    }
}
