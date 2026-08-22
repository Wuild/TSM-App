using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TSM.Services;

public enum UpdatePackageKind
{
    None,
    AppImage,
    WindowsZip,
    MacAppZip
}

public sealed record GitHubAppUpdate(
    string Version,
    string Tag,
    string Title,
    Uri ReleasePage,
    Uri? PackageDownload,
    string? PackageName,
    UpdatePackageKind PackageKind,
    bool IsPrerelease);

public sealed class GitHubUpdateService : IDisposable
{
    public const string RepositoryOwner = "Wuild";
    public const string RepositoryName = "TSM-App";
    private const long MaximumPackageBytes = 1_000_000_000;
    private static readonly Uri ReleasesEndpoint = new(
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases?per_page=20");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public GitHubUpdateService(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TSM-App-Updater/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string InstalledVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public bool CanInstallAutomatically =>
        IsRunningAsAppImage ||
        (OperatingSystem.IsWindows() && IsPublishedWindowsApplication()) ||
        (OperatingSystem.IsMacOS() && FindMacBundle() is not null);

    private bool IsRunningAsAppImage =>
        OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPIMAGE"));

    public async Task<GitHubAppUpdate?> CheckAsync(
        bool includePrereleases,
        string? installedVersion = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ReleasesEndpoint, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<ReleaseDto>>(stream,
            cancellationToken: cancellationToken) ?? [];
        var current = ParseVersion(installedVersion ?? InstalledVersion);

        return releases
            .Where(release => !release.Draft && (includePrereleases || !release.Prerelease))
            .Select(ToUpdate)
            .Where(update => update is not null && ParseVersion(update.Version) > current)
            .OrderByDescending(update => ParseVersion(update!.Version))
            .FirstOrDefault();
    }

    public async Task InstallAsync(GitHubAppUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.PackageDownload is null || update.PackageKind == UpdatePackageKind.None)
            throw new InvalidOperationException("This release does not contain a package for the current platform and architecture.");

        switch (update.PackageKind)
        {
            case UpdatePackageKind.AppImage:
                await InstallAppImageAsync(update.PackageDownload, cancellationToken);
                break;
            case UpdatePackageKind.WindowsZip:
                await StageWindowsUpdateAsync(update.PackageDownload, cancellationToken);
                break;
            case UpdatePackageKind.MacAppZip:
                await StageMacUpdateAsync(update.PackageDownload, cancellationToken);
                break;
            default:
                throw new PlatformNotSupportedException("Automatic installation is not supported by this package.");
        }
    }

    public static void OpenReleasePage(GitHubAppUpdate update) =>
        Process.Start(new ProcessStartInfo(update.ReleasePage.AbsoluteUri) { UseShellExecute = true });

    public void Dispose()
    {
        if (_ownsClient)
            _httpClient.Dispose();
    }

    private async Task InstallAppImageAsync(Uri download, CancellationToken cancellationToken)
    {
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(appImagePath))
            throw new InvalidOperationException("The current Linux installation is not an AppImage.");

        appImagePath = Path.GetFullPath(appImagePath);
        var directory = Path.GetDirectoryName(appImagePath)
                        ?? throw new InvalidOperationException("The AppImage directory could not be resolved.");
        EnsureDirectoryWritable(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(appImagePath)}.{Guid.NewGuid():N}.update");
        try
        {
            await DownloadAsync(download, temporaryPath, cancellationToken);
            File.SetUnixFileMode(temporaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(temporaryPath, appImagePath, true);
            Process.Start(new ProcessStartInfo(appImagePath) { UseShellExecute = true });
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private async Task StageWindowsUpdateAsync(Uri download, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !IsPublishedWindowsApplication())
            throw new InvalidOperationException("Automatic Windows updates require the published TSM application.");

        var targetDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var targetParent = Directory.GetParent(targetDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName
                           ?? throw new InvalidOperationException("The Windows installation directory could not be resolved.");
        EnsureSafeApplicationDirectory(targetDirectory, "TSM.exe");
        EnsureDirectoryWritable(targetParent);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(temporaryRoot, "update.zip");
        var stagedDirectory = Path.Combine(temporaryRoot, "payload");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await DownloadAsync(download, archivePath, cancellationToken);
            ZipFile.ExtractToDirectory(archivePath, stagedDirectory);
            EnsureSafeApplicationDirectory(stagedDirectory, "TSM.exe");
            File.Delete(archivePath);

            var helperPath = Path.Combine(temporaryRoot, "install-update.ps1");
            File.WriteAllText(helperPath, WindowsUpdaterScript);
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = temporaryRoot
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(helperPath);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-SourceDirectory");
            startInfo.ArgumentList.Add(stagedDirectory);
            startInfo.ArgumentList.Add("-TargetDirectory");
            startInfo.ArgumentList.Add(targetDirectory.TrimEnd(Path.DirectorySeparatorChar));
            startInfo.ArgumentList.Add("-TemporaryRoot");
            startInfo.ArgumentList.Add(temporaryRoot);
            startInfo.ArgumentList.Add("-LogPath");
            startInfo.ArgumentList.Add(WindowsUpdateLogPath());
            _ = Process.Start(startInfo) ??
                throw new InvalidOperationException("The Windows update helper could not be started.");
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
            throw;
        }
    }

    private async Task StageMacUpdateAsync(Uri download, CancellationToken cancellationToken)
    {
        var targetBundle = FindMacBundle();
        if (!OperatingSystem.IsMacOS() || targetBundle is null)
            throw new InvalidOperationException("Automatic macOS updates require the packaged TradeSkillMaster.app bundle.");

        var targetParent = Directory.GetParent(targetBundle)?.FullName
                           ?? throw new InvalidOperationException("The macOS installation directory could not be resolved.");
        EnsureDirectoryWritable(targetParent);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-update-{Guid.NewGuid():N}");
        var archivePath = Path.Combine(temporaryRoot, "update.zip");
        var extractedDirectory = Path.Combine(temporaryRoot, "payload");
        Directory.CreateDirectory(extractedDirectory);
        try
        {
            await DownloadAsync(download, archivePath, cancellationToken);
            var ditto = new ProcessStartInfo("/usr/bin/ditto") { UseShellExecute = false };
            ditto.ArgumentList.Add("-x");
            ditto.ArgumentList.Add("-k");
            ditto.ArgumentList.Add(archivePath);
            ditto.ArgumentList.Add(extractedDirectory);
            using (var process = Process.Start(ditto)
                                 ?? throw new InvalidOperationException("The macOS archive tool could not be started."))
            {
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode != 0)
                    throw new InvalidDataException("The macOS update package could not be extracted.");
            }

            var sourceBundle = Directory.EnumerateDirectories(extractedDirectory, "*.app").SingleOrDefault()
                               ?? throw new InvalidDataException("The update package does not contain one application bundle.");
            var executable = Path.Combine(sourceBundle, "Contents", "MacOS", "TSM");
            if (!File.Exists(executable))
                throw new InvalidDataException("The update package does not contain the TSM executable.");
            File.Delete(archivePath);

            var helperPath = Path.Combine(temporaryRoot, "install-update.sh");
            File.WriteAllText(helperPath, MacUpdaterScript);
            File.SetUnixFileMode(helperPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            startInfo.ArgumentList.Add(helperPath);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(sourceBundle);
            startInfo.ArgumentList.Add(targetBundle);
            startInfo.ArgumentList.Add(temporaryRoot);
            _ = Process.Start(startInfo) ??
                throw new InvalidOperationException("The macOS update helper could not be started.");
        }
        catch
        {
            if (Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, true);
            throw;
        }
    }

    private async Task DownloadAsync(Uri download, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(download,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumPackageBytes)
            throw new InvalidDataException("The update package is unexpectedly large.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81920, FileOptions.WriteThrough);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaximumPackageBytes)
                throw new InvalidDataException("The update package is unexpectedly large.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static GitHubAppUpdate? ToUpdate(ReleaseDto release)
    {
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page))
            return null;
        var packageKind = CurrentPackageKind();
        var package = release.Assets.FirstOrDefault(asset => IsPackageForCurrentPlatform(asset.Name, packageKind));
        Uri? download = null;
        if (package is not null)
            Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out download);
        var version = release.TagName.Trim().TrimStart('v', 'V');
        return new GitHubAppUpdate(version, release.TagName,
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            page, download, package?.Name, download is null ? UpdatePackageKind.None : packageKind,
            release.Prerelease);
    }

    private static UpdatePackageKind CurrentPackageKind()
    {
        if (OperatingSystem.IsLinux())
            return UpdatePackageKind.AppImage;
        if (OperatingSystem.IsWindows())
            return UpdatePackageKind.WindowsZip;
        if (OperatingSystem.IsMacOS())
            return UpdatePackageKind.MacAppZip;
        return UpdatePackageKind.None;
    }

    private static bool IsPackageForCurrentPlatform(string name, UpdatePackageKind kind)
    {
        var architecture = RuntimeInformation.OSArchitecture;
        var architectureMatches = architecture switch
        {
            Architecture.Arm64 => name.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                                  name.Contains("aarch64", StringComparison.OrdinalIgnoreCase),
            Architecture.X64 => name.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("amd64", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("x64", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!architectureMatches)
            return false;
        return kind switch
        {
            UpdatePackageKind.AppImage => name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase),
            UpdatePackageKind.WindowsZip => name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                                            name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            UpdatePackageKind.MacAppZip => name.Contains("macos", StringComparison.OrdinalIgnoreCase) &&
                                           name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsPublishedWindowsApplication()
    {
        var processPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(processPath) &&
               string.Equals(Path.GetFileName(processPath), "TSM.exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(Path.Combine(AppContext.BaseDirectory, "TSM.exe"));
    }

    private static string? FindMacBundle()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var contents = directory.Parent;
        if (!string.Equals(directory.Name, "MacOS", StringComparison.Ordinal) ||
            contents is null ||
            !string.Equals(contents.Name, "Contents", StringComparison.Ordinal) ||
            contents.Parent is not { } bundle ||
            !bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return null;
        return bundle.FullName;
    }

    private static void EnsureSafeApplicationDirectory(string directory, string executableName)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.GetParent(directory.TrimEnd(Path.DirectorySeparatorChar)) is null ||
            !File.Exists(Path.Combine(directory, executableName)))
            throw new InvalidOperationException("The application installation directory is not safe to replace.");
    }

    private static void EnsureDirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".tsm-update-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException(
                "The application folder is not writable. Move TSM to a user-writable folder or reinstall it for the current user.", exception);
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
    }

    private static string WindowsUpdateLogPath()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();
        return Path.Combine(localData, "TSM-CrossPlatform", "windows-update.log");
    }

    private static Version ParseVersion(string value)
    {
        value = value.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            value = value[..suffix];
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        while (parts.Length < 3)
            parts = [.. parts, "0"];
        return Version.TryParse(string.Join('.', parts), out var parsed) ? parsed : new Version(0, 0, 0);
    }

    private const string WindowsUpdaterScript =
        """
        param(
          [Parameter(Mandatory=$true)][int]$ProcessId,
          [Parameter(Mandatory=$true)][string]$SourceDirectory,
          [Parameter(Mandatory=$true)][string]$TargetDirectory,
          [Parameter(Mandatory=$true)][string]$TemporaryRoot,
          [Parameter(Mandatory=$true)][string]$LogPath
        )
        $ErrorActionPreference = 'Stop'
        function Write-UpdateLog([string]$Message) {
          $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
          Add-Content -LiteralPath $LogPath -Value "[$timestamp] $Message"
        }
        function Invoke-WithRetry([scriptblock]$Operation, [string]$Description) {
          $lastError = $null
          for ($attempt = 1; $attempt -le 40; $attempt++) {
            try {
              & $Operation
              return
            } catch {
              $lastError = $_
              Start-Sleep -Milliseconds 250
            }
          }
          throw "$Description failed after 10 seconds: $lastError"
        }
        $logDirectory = Split-Path -Parent $LogPath
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        Set-Content -LiteralPath $LogPath -Value "TSM Windows update started."
        Set-Location -LiteralPath $TemporaryRoot
        Write-UpdateLog "Waiting for process $ProcessId to exit."
        Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
        $backup = "$TargetDirectory.update-backup"
        try {
          if (Test-Path -LiteralPath $backup) {
            Invoke-WithRetry { Remove-Item -LiteralPath $backup -Recurse -Force } 'Removing previous backup'
          }
          Invoke-WithRetry { Move-Item -LiteralPath $TargetDirectory -Destination $backup } 'Backing up installation'
          try {
            Invoke-WithRetry { Move-Item -LiteralPath $SourceDirectory -Destination $TargetDirectory } 'Installing update'
            if (-not (Test-Path -LiteralPath (Join-Path $TargetDirectory 'TSM.exe'))) {
              throw 'The installed update does not contain TSM.exe.'
            }
            Write-UpdateLog 'Update installed successfully. Starting the new application.'
            try {
              Invoke-WithRetry { Remove-Item -LiteralPath $backup -Recurse -Force } 'Removing backup'
            } catch {
              Write-UpdateLog "The update succeeded but backup cleanup failed: $_"
            }
            Start-Process -FilePath (Join-Path $TargetDirectory 'TSM.exe')
          } catch {
            Write-UpdateLog "Installation failed: $_"
            if (Test-Path -LiteralPath $TargetDirectory) {
              Invoke-WithRetry { Remove-Item -LiteralPath $TargetDirectory -Recurse -Force } 'Removing failed update'
            }
            Invoke-WithRetry { Move-Item -LiteralPath $backup -Destination $TargetDirectory } 'Restoring backup'
            throw
          }
        } catch {
          Write-UpdateLog "Update failed: $_"
          if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $TargetDirectory)) {
            try { Move-Item -LiteralPath $backup -Destination $TargetDirectory } catch { Write-UpdateLog "Rollback failed: $_" }
          }
          if (Test-Path -LiteralPath (Join-Path $TargetDirectory 'TSM.exe')) {
            Start-Process -FilePath (Join-Path $TargetDirectory 'TSM.exe')
          }
        } finally {
          Start-Sleep -Milliseconds 500
          Set-Location -LiteralPath ([System.IO.Path]::GetTempPath())
          if (Test-Path -LiteralPath $TemporaryRoot) {
            try { Remove-Item -LiteralPath $TemporaryRoot -Recurse -Force } catch { Write-UpdateLog "Cleanup failed: $_" }
          }
        }
        """;

    private const string MacUpdaterScript =
        """
        process_id="$1"
        source_bundle="$2"
        target_bundle="$3"
        temporary_root="$4"
        backup_bundle="${target_bundle}.update-backup"
        while kill -0 "$process_id" 2>/dev/null; do sleep 0.2; done
        rm -rf -- "$backup_bundle"
        if ! mv -- "$target_bundle" "$backup_bundle"; then exit 1; fi
        if mv -- "$source_bundle" "$target_bundle"; then
          open "$target_bundle"
          rm -rf -- "$backup_bundle"
          rm -rf -- "$temporary_root"
          exit 0
        fi
        rm -rf -- "$target_bundle"
        mv -- "$backup_bundle" "$target_bundle"
        open "$target_bundle"
        exit 1
        """;

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string TagName { get; init; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; init; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
        [JsonPropertyName("assets")] public List<AssetDto> Assets { get; init; } = [];
    }

    private sealed class AssetDto
    {
        [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = string.Empty;
    }
}
