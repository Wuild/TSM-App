using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace TSM.Services;

public static class NativeConfirmationService
{
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconQuestion = 0x00000020;
    private const uint MbDefaultButton2 = 0x00000100;
    private const int IdYes = 6;

    public static async Task<bool?> ConfirmExitAsync(Window owner)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var result = MessageBox(handle,
                "Background synchronization will stop until TSM Desktop is opened again.",
                "Exit TSM Desktop?", MbYesNo | MbIconQuestion | MbDefaultButton2);
            return result == IdYes;
        }
        if (OperatingSystem.IsMacOS())
            return await RunDialogAsync("osascript",
            [
                "-e",
                "display alert \"Exit TSM Desktop?\" message \"Background synchronization will stop until TSM Desktop is opened again.\" as warning buttons {\"Cancel\", \"Exit\"} default button \"Cancel\" cancel button \"Cancel\""
            ]);
        if (OperatingSystem.IsLinux())
        {
            var result = await RunDialogAsync("zenity",
            [
                "--question", "--title=Exit TSM Desktop?",
                "--text=Background synchronization will stop until TSM Desktop is opened again.",
                "--ok-label=Exit", "--cancel-label=Cancel", "--default-cancel", "--icon-name=tsm-desktop"
            ]);
            if (result is not null)
                return result;
            result = await RunDialogAsync("kdialog",
            [
                "--title", "Exit TSM Desktop?", "--warningyesno",
                "Background synchronization will stop until TSM Desktop is opened again.",
                "--yes-label", "Exit", "--no-label", "Cancel"
            ]);
            if (result is not null)
                return result;
            return await RunDialogAsync("xmessage",
            [
                "-center", "-title", "Exit TSM Desktop?", "-buttons", "Exit:0,Cancel:1",
                "-default", "Cancel", "Background synchronization will stop until TSM Desktop is opened again."
            ]);
        }
        return null;
    }

    public static async Task<bool?> ConfirmRestoreAsync(Window owner, string account)
    {
        var message = $"The current TSM SavedVariables for {account} will be backed up and replaced. World of Warcraft must be closed.";
        if (OperatingSystem.IsWindows())
        {
            var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            return MessageBox(handle, message, "Restore TSM backup?",
                MbYesNo | MbIconQuestion | MbDefaultButton2) == IdYes;
        }
        if (OperatingSystem.IsMacOS())
            return await RunDialogAsync("osascript",
            [
                "-e",
                $"display alert \"Restore TSM backup?\" message \"{message.Replace("\"", "\\\"")}\" as warning buttons {{\"Cancel\", \"Restore\"}} default button \"Cancel\" cancel button \"Cancel\""
            ]);
        if (OperatingSystem.IsLinux())
        {
            var result = await RunDialogAsync("zenity",
            [
                "--question", "--title=Restore TSM backup?", $"--text={message}",
                "--ok-label=Restore", "--cancel-label=Cancel", "--default-cancel", "--icon-name=tsm-desktop"
            ]);
            if (result is not null)
                return result;
            return await RunDialogAsync("kdialog",
            [
                "--title", "Restore TSM backup?", "--warningyesno", message,
                "--yes-label", "Restore", "--no-label", "Cancel"
            ]);
        }
        return null;
    }

    private static async Task<bool?> RunDialogAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException)
        {
            return null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
