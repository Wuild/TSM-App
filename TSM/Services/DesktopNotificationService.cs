using System.Diagnostics;

namespace TSM.Services;

public sealed class DesktopNotificationService
{
    public void Show(string title, string message)
    {
        if (!Environment.UserInteractive || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            using var process = Process.Start(CreateStartInfo(title, message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            var escapedTitle = EscapeXml(title);
            var escapedMessage = EscapeXml(message);
            var script =
                "[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]>$null;" +
                "$xml=New-Object Windows.Data.Xml.Dom.XmlDocument;" +
                $"$xml.LoadXml('<toast><visual><binding template=\"ToastGeneric\"><text>{escapedTitle}</text><text>{escapedMessage}</text></binding></visual></toast>');" +
                "$toast=[Windows.UI.Notifications.ToastNotification]::new($xml);" +
                "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('TradeSkillMaster').Show($toast)";
            return Create("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script);
        }

        if (OperatingSystem.IsMacOS())
        {
            var script = $"display notification \"{EscapeAppleScript(message)}\" with title \"{EscapeAppleScript(title)}\"";
            return Create("osascript", "-e", script);
        }

        return Create("notify-send", "--app-name=TradeSkillMaster", title, message);
    }

    private static ProcessStartInfo Create(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string EscapeAppleScript(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
