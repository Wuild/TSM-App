using System.Security;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TSM.Services;

public sealed class StartupRegistrationService
{
    private const string ApplicationId = "TradeSkillMaster-CrossPlatform";
    private const string LinuxDesktopId = "com.wuild.TSMDesktop.desktop";
    private const string LegacyLinuxDesktopId = "tradeskillmaster-crossplatform.desktop";

    public string Apply(bool enabled)
    {
        if (OperatingSystem.IsWindows())
            return ApplyWindows(enabled);
        return OperatingSystem.IsMacOS() ? ApplyMacOs(enabled) : ApplyLinux(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static string ApplyWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
            key.SetValue(ApplicationId, BuildQuotedCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ApplicationId, false);
        return @"Current user startup applications";
    }

    private static string ApplyLinux(bool enabled)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var directory = Path.Combine(configHome, "autostart");
        var path = Path.Combine(directory, LinuxDesktopId);
        var legacyPath = Path.Combine(directory, LegacyLinuxDesktopId);
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(legacyPath))
                File.Delete(legacyPath);
            return path;
        }

        Directory.CreateDirectory(directory);
        var command = BuildCommandParts().Select(QuoteDesktopArgument);
        File.WriteAllText(path,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=TSM Desktop\n" +
            $"Exec={string.Join(' ', command)}\n" +
            "Terminal=false\n" +
            "Hidden=false\n" +
            "StartupWMClass=TSM\n" +
            "X-GNOME-Autostart-enabled=true\n" +
            "X-GNOME-Autostart-Delay=3\n");
        if (File.Exists(legacyPath))
            File.Delete(legacyPath);
        return path;
    }

    private static string ApplyMacOs(bool enabled)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        var path = Path.Combine(directory, "com.tsm.crossplatform.plist");
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return path;
        }

        Directory.CreateDirectory(directory);
        var arguments = string.Join(string.Empty, BuildCommandParts().Select(value =>
            $"    <string>{SecurityElement.Escape(value)}</string>\n"));
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "  <key>Label</key><string>com.tsm.crossplatform</string>\n" +
            "  <key>ProgramArguments</key>\n  <array>\n" + arguments + "  </array>\n" +
            "  <key>RunAtLoad</key><true/>\n" +
            "</dict>\n</plist>\n");
        return path;
    }

    private static IReadOnlyList<string> BuildCommandParts()
    {
        if (OperatingSystem.IsLinux() &&
            Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage)
            return [appImage];
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application executable path is unavailable.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            return [executable];
        return [executable, typeof(StartupRegistrationService).Assembly.Location];
    }

    private static string BuildQuotedCommand() => string.Join(' ', BuildCommandParts().Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string QuoteDesktopArgument(string value) =>
        $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal)}\"";
}
