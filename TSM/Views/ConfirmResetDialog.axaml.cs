using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TSM.Views;

public sealed partial class ConfirmResetDialog : Window
{
    public ConfirmResetDialog() => InitializeComponent();

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void ResetClick(object? sender, RoutedEventArgs e) => Close(true);
}
