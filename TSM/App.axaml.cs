using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TSM.ViewModels;
using TSM.Views;

namespace TSM;

public sealed partial class App : Application
{
    private MainWindow? _mainWindow;
    private MainWindowViewModel? _viewModel;

    public bool IsShuttingDown { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = MainWindowViewModel.Create();
            var window = new MainWindow { DataContext = viewModel };
            window.RestoreSavedWindowPlacement(viewModel.Settings);
            _viewModel = viewModel;
            _mainWindow = window;
            desktop.MainWindow = window;
            viewModel.Settings.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(viewModel.Settings.MinimizeToTray))
                    UpdateTrayVisibility();
            };
            UpdateTrayVisibility();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindowFromTray(object? sender, EventArgs e) => _mainWindow?.RestoreFromTray();

    private async void RefreshFromTray(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
            await _viewModel.RefreshNowAsync();
    }

    private async void QuitFromTray(object? sender, EventArgs e)
    {
        if (_mainWindow is not null && !await _mainWindow.ConfirmExitAsync())
            return;
        Shutdown();
    }

    public void Shutdown()
    {
        _mainWindow?.PersistWindowPlacement();
        IsShuttingDown = true;
        SetTrayVisible(false);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void UpdateTrayVisibility() => SetTrayVisible(_viewModel?.Settings.MinimizeToTray == true);

    private void SetTrayVisible(bool isVisible)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is { Count: > 0 })
            icons[0].IsVisible = isVisible;
    }
}
