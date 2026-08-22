using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TSM.Views;

public sealed partial class ConfirmAddonRemovalDialog : Window
{
    public ConfirmAddonRemovalDialog()
    {
        InitializeComponent();
    }

    public ConfirmAddonRemovalDialog(string addonName, string clientName) : this()
    {
        DescriptionText.Text = $"This removes {addonName} from the {clientName} client. You can reinstall it later from the Addons page if another copy remains available.";
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close(false);
    private void RemoveClick(object? sender, RoutedEventArgs e) => Close(true);
}
