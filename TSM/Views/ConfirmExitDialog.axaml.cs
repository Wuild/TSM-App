using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TSM.Views;

public sealed partial class ConfirmExitDialog : Window
{
    public ConfirmExitDialog() => InitializeComponent();

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void ExitClick(object? sender, RoutedEventArgs e) => Close(true);
}
