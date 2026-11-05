using System.Text.RegularExpressions;
using TSM.Models;

namespace TSM.Services;

public sealed partial class WowInstallationService
{
    private static readonly (string Folder, string Name)[] Variants =
    [
        ("_retail_", "Retail"),
        ("_forever_", "Forever"),
        ("_classic_", "Classic"),
        ("_classic_era_", "Classic Era"),
        ("_anniversary_", "Anniversary"),
        ("_classic_beta_", "Classic Beta"),
        ("_retail_beta_", "Retail Beta"),
        ("_ptr_", "PTR"),
        ("_retail_ptr_", "Retail PTR"),
        ("_classic_ptr_", "Classic PTR"),
        ("_classic_era_ptr_", "Classic Era PTR"),
        ("_anniversary_ptr_", "Anniversary PTR")
    ];

    public string? FindLikelyRoot()
    {
        foreach (var path in CandidateRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsValidRoot(path))
                return path;
        }

        return null;
    }

    public bool IsValidRoot(string? root) => !string.IsNullOrWhiteSpace(root) && Discover(root).Count > 0;

    public string GetDiscoveryFingerprint(string? root)
    {
        var parts = new List<string>();
        foreach (var installation in Discover(root).OrderBy(value => value.RootPath, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add($"client:{installation.Name}:{Path.GetFullPath(installation.RootPath)}");
            AddDirectoryEntries(parts, installation.SavedVariablesRoot, "account", directory =>
            {
                var savedVariables = Path.Combine(directory, "SavedVariables", "TradeSkillMaster.lua");
                return File.Exists(savedVariables) ? savedVariables : null;
            }, false);
            AddDirectoryEntries(parts, installation.AddOnsPath, "addon", directory =>
            {
                var name = Path.GetFileName(directory);
                if (!name.StartsWith("TradeSkillMaster", StringComparison.OrdinalIgnoreCase))
                    return null;
                try
                {
                    return Directory.EnumerateFiles(directory, "*.toc").Order(StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            });
        }
        return string.Join('|', parts);
    }

    public IReadOnlyList<WowInstallation> Discover(string? root)
    {
        var result = new List<WowInstallation>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return result;

        foreach (var (folder, name) in Variants)
        {
            var variantRoot = Path.Combine(root, folder);
            var addons = FindCaseInsensitivePath(variantRoot, "Interface", "AddOns");
            if (addons is null)
                continue;

            var savedVariables = FindCaseInsensitivePath(variantRoot, "WTF", "Account") ?? Path.Combine(variantRoot, "WTF", "Account");
            result.Add(new WowInstallation(name, variantRoot, addons, savedVariables));
        }

        if (result.Count == 0)
        {
            var addons = FindCaseInsensitivePath(root, "Interface", "AddOns");
            if (addons is not null)
                result.Add(new WowInstallation("World of Warcraft", root, addons, Path.Combine(root, "WTF", "Account")));
        }

        return result;
    }

    public IReadOnlyList<AddonStatusRow> ScanAddons(IEnumerable<WowInstallation> installations)
    {
        var rows = new List<AddonStatusRow>();
        foreach (var installation in installations)
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(installation.AddOnsPath, "TradeSkillMaster*").ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var addonName = Path.GetFileName(directory);
                string? toc;
                try
                {
                    toc = Directory.EnumerateFiles(directory, "*.toc").FirstOrDefault();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    toc = null;
                }
                var version = toc is null ? "Unknown" : ReadVersion(toc);
                rows.Add(new AddonStatusRow($"{addonName} · {installation.Name}", version, "Installed", "#51CF92",
                    addonName, installation.Name, installation.AddOnsPath));
            }
        }

        return rows;
    }

    private static string ReadVersion(string tocPath)
    {
        try
        {
            foreach (var line in File.ReadLines(tocPath))
            {
                var match = VersionLine().Match(line);
                if (match.Success)
                    return match.Groups[1].Value.Trim();
            }
        }
        catch (IOException)
        {
        }

        return "Unknown";
    }

    private static void AddDirectoryEntries(
        ICollection<string> parts,
        string parent,
        string prefix,
        Func<string, string?> trackedFile,
        bool includeFileMetadata = true)
    {
        if (!Directory.Exists(parent))
            return;
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(parent)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var file = trackedFile(directory);
            if (file is null)
                continue;
            var info = new FileInfo(file);
            parts.Add(includeFileMetadata
                ? $"{prefix}:{Path.GetFileName(directory)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : $"{prefix}:{Path.GetFileName(directory)}:{Path.GetFullPath(file)}");
        }
    }

    private static string? FindCaseInsensitivePath(string root, params string[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (!Directory.Exists(current))
                return null;

            try
            {
                current = Directory.EnumerateDirectories(current)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), segment, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (current.Length == 0)
                return null;
        }

        return current;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                yield return Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "World of Warcraft");
                yield return Path.Combine(drive.RootDirectory.FullName, "Program Files", "World of Warcraft");
                yield return Path.Combine(drive.RootDirectory.FullName, "Games", "World of Warcraft");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Applications", "World of Warcraft");
            yield return "/Applications/World of Warcraft";
        }
        else
        {
            yield return Path.Combine(home, "Games", "World of Warcraft");
            yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "World of Warcraft");
            yield return Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "runners", "wine", "World of Warcraft");
        }
    }

    [GeneratedRegex(@"^##\s*Version\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLine();
}
