using System.Security;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TSM.Services;

public sealed class StartupRegistrationService
{
    private const string ApplicationId = "TradeSkillMaster-CrossPlatform";

    public void Apply(bool enabled)
    {
        if (OperatingSystem.IsWindows())
            ApplyWindows(enabled);
        else if (OperatingSystem.IsMacOS())
            ApplyMacOs(enabled);
        else
            ApplyLinux(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
            key.SetValue(ApplicationId, BuildQuotedCommand(), RegistryValueKind.String);
        else
            key.DeleteValue(ApplicationId, false);
    }

    private static void ApplyLinux(bool enabled)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var directory = Path.Combine(configHome, "autostart");
        var path = Path.Combine(directory, "tradeskillmaster-crossplatform.desktop");
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(directory);
        var command = BuildCommandParts().Select(QuoteDesktopArgument);
        File.WriteAllText(path,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=TSM Desktop\n" +
            $"Exec={string.Join(' ', command)}\n" +
            "Terminal=false\n" +
            "NoDisplay=true\n" +
            "StartupWMClass=TSM\n" +
            "X-GNOME-Autostart-enabled=true\n");
    }

    private static void ApplyMacOs(bool enabled)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents");
        var path = Path.Combine(directory, "com.tsm.crossplatform.plist");
        if (!enabled)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
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
