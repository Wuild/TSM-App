using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using TSM.Services;

namespace TSM.Views;

public sealed partial class ErrorDialog : Window
{
    private ApplicationError _error = new(DateTimeOffset.Now, "Application", "Exception",
        "An unexpected error occurred.", string.Empty, string.Empty, false);

    public ErrorDialog() => InitializeComponent();

    public ErrorDialog(ApplicationError error) : this()
    {
        _error = error;
        ContextText.Text = error.IsFatal ? $"FATAL · {error.Context}" : error.Context.ToUpperInvariant();
        MessageText.Text = error.Message;
        TimestampText.Text = $"{error.Timestamp:g} · {error.ExceptionType}";
        DetailsText.Text = error.Details;
    }

    private async void CopyDetailsClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            CopyStatusText.Text = "Clipboard is unavailable.";
            return;
        }
        await clipboard.SetTextAsync(_error.Details);
        CopyStatusText.Text = "Error details copied.";
    }

    private void OpenLogClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_error.LogPath))
                Process.Start(new ProcessStartInfo(_error.LogPath) { UseShellExecute = true });
            else
                CopyStatusText.Text = "The error log could not be found.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            CopyStatusText.Text = "The error log could not be opened.";
        }
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}
