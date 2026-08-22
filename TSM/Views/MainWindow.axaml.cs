using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using TSM.ViewModels;
using TSM.Models;

namespace TSM.Views;

public sealed partial class MainWindow : Window
{
    private bool _exitConfirmationOpen;
    private CancellationTokenSource? _auctionHoverDelay;
    private double _normalWindowWidth = 1200;
    private double _normalWindowHeight = 780;
    private PixelPoint? _normalWindowPosition;
    private bool _wasMaximized;
    private bool _restoringWindowPlacement;
    private bool _restoreMaximizedAfterOpen;
    private PixelPoint? _restorePositionAfterOpen;
    private readonly DispatcherTimer _placementSaveTimer;

    public MainWindow()
    {
        InitializeComponent();
        _placementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _placementSaveTimer.Tick += SavePlacementTimerTick;
        AuctionList.AddHandler(ScrollViewer.ScrollChangedEvent, AuctionListScrolled, RoutingStrategies.Bubble);
        Opened += ApplyDeferredWindowPlacement;
        Opened += InitializeViewModel;
        Closed += DisposeViewModel;
        Closing += HandleClosing;
        PropertyChanged += HandleWindowPropertyChanged;
        SizeChanged += CaptureNormalWindowSize;
        PositionChanged += (_, _) => CaptureNormalWindowPosition();
    }

    public void RestoreSavedWindowPlacement(AppSettings settings)
    {
        _restoringWindowPlacement = true;
        try
        {
            _normalWindowWidth = settings.WindowWidth;
            _normalWindowHeight = settings.WindowHeight;
            Width = _normalWindowWidth;
            Height = _normalWindowHeight;
            if (settings.WindowX is { } x && settings.WindowY is { } y)
            {
                var position = new PixelPoint(x, y);
                if (IsPositionVisible(position))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = position;
                    _normalWindowPosition = position;
                    _restorePositionAfterOpen = position;
                }
            }
            _wasMaximized = settings.WindowMaximized;
            _restoreMaximizedAfterOpen = settings.WindowMaximized;
        }
        finally
        {
            _restoringWindowPlacement = false;
        }
    }

    private void ApplyDeferredWindowPlacement(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _restoringWindowPlacement = true;
            try
            {
                Width = _normalWindowWidth;
                Height = _normalWindowHeight;
                if (_restorePositionAfterOpen is { } position && IsPositionVisible(position))
                    Position = position;
                if (_restoreMaximizedAfterOpen)
                    WindowState = WindowState.Maximized;
            }
            finally
            {
                _restoringWindowPlacement = false;
            }
        }, DispatcherPriority.Loaded);
    }

    private bool IsPositionVisible(PixelPoint position)
    {
        const int visibleEdge = 100;
        return Screens.All.Any(screen =>
        {
            var area = screen.WorkingArea;
            return position.X + visibleEdge > area.X && position.X < area.X + area.Width &&
                   position.Y + visibleEdge > area.Y && position.Y < area.Y + area.Height;
        });
    }

    private void CaptureNormalWindowSize(object? sender, SizeChangedEventArgs e)
    {
        if (_restoringWindowPlacement || WindowState != WindowState.Normal || e.NewSize.Width <= 0 ||
            e.NewSize.Height <= 0)
            return;
        _normalWindowWidth = e.NewSize.Width;
        _normalWindowHeight = e.NewSize.Height;
        ScheduleWindowPlacementSave();
    }

    private void CaptureNormalWindowPosition()
    {
        if (!_restoringWindowPlacement && WindowState == WindowState.Normal)
        {
            _normalWindowPosition = Position;
            ScheduleWindowPlacementSave();
        }
    }

    private void SaveWindowPlacement(MainWindowViewModel viewModel) => viewModel.SaveWindowPlacement(
        _normalWindowWidth,
        _normalWindowHeight,
        _normalWindowPosition?.X,
        _normalWindowPosition?.Y,
        _wasMaximized);

    public void PersistWindowPlacement()
    {
        _placementSaveTimer.Stop();
        if (DataContext is MainWindowViewModel viewModel)
            SaveWindowPlacement(viewModel);
    }

    public void ResetWindowPlacementToDefaults()
    {
        _placementSaveTimer.Stop();
        _restoringWindowPlacement = true;
        try
        {
            _restoreMaximizedAfterOpen = false;
            _restorePositionAfterOpen = null;
            _wasMaximized = false;
            _normalWindowWidth = 1200;
            _normalWindowHeight = 780;
            _normalWindowPosition = null;
            WindowState = WindowState.Normal;
            Width = _normalWindowWidth;
            Height = _normalWindowHeight;
        }
        finally
        {
            _restoringWindowPlacement = false;
        }
    }

    private void ScheduleWindowPlacementSave()
    {
        if (_restoringWindowPlacement || DataContext is not MainWindowViewModel)
            return;
        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void SavePlacementTimerTick(object? sender, EventArgs e)
    {
        _placementSaveTimer.Stop();
        PersistWindowPlacement();
    }

    private void AuctionListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scrollViewer || DataContext is not MainWindowViewModel viewModel)
            return;
        var remaining = scrollViewer.Extent.Height - scrollViewer.Offset.Y - scrollViewer.Viewport.Height;
        if (remaining <= Math.Max(240, scrollViewer.Viewport.Height / 2))
            viewModel.LoadMoreAuctionResults();
    }

    private void AuctionWorkspaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateAuctionDrawerMaximumHeight(e.NewSize.Height);
    }

    private void AuctionItemDrawerHeaderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateAuctionDrawerMaximumHeight(AuctionWorkspace.Bounds.Height);
    }

    private void UpdateAuctionDrawerMaximumHeight(double workspaceHeight)
    {
        if (workspaceHeight <= 0)
            return;
        var drawerMaximum = Math.Max(1, workspaceHeight - 24);
        var headerHeight = AuctionItemDrawerHeader.Bounds.Height > 0
            ? AuctionItemDrawerHeader.Bounds.Height
            : 74;
        AuctionItemDrawer.MaxHeight = drawerMaximum;
        AuctionItemDrawerBody.MaxHeight = Math.Max(1, drawerMaximum - headerHeight - 2);
    }

    private async void DisposeViewModel(object? sender, EventArgs e)
    {
        _placementSaveTimer.Stop();
        _auctionHoverDelay?.Cancel();
        _auctionHoverDelay?.Dispose();
        _auctionHoverDelay = null;
        if (DataContext is MainWindowViewModel viewModel)
            await viewModel.DisposeAsync();
    }

    private async void InitializeViewModel(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (viewModel.Settings.HasCompletedOnboarding && viewModel.Settings.StartMinimized)
            {
                if (viewModel.Settings.MinimizeToTray)
                    HideToTray();
                else
                    WindowState = WindowState.Minimized;
            }
            await viewModel.InitializeAsync();
        }
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
            Show();
        WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    public async Task<bool> ConfirmExitAsync()
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.Settings.ConfirmExit)
            return true;
        if (!IsVisible)
            RestoreFromTray();
        return await new ConfirmExitDialog().ShowDialog<bool>(this);
    }

    private async void HandleClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel placementViewModel)
            SaveWindowPlacement(placementViewModel);
        if (Application.Current is App { IsShuttingDown: true })
            return;
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (!viewModel.Settings.ConfirmExit)
            return;
        if (_exitConfirmationOpen)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _exitConfirmationOpen = true;
        try
        {
            if (await ConfirmExitAsync() && Application.Current is App app)
                app.Shutdown();
        }
        finally
        {
            _exitConfirmationOpen = false;
        }
    }

    private void HandleWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty || _restoringWindowPlacement)
            return;
        if (WindowState == WindowState.Maximized)
        {
            _wasMaximized = true;
            ScheduleWindowPlacementSave();
            return;
        }
        if (WindowState == WindowState.Normal)
        {
            _wasMaximized = false;
            CaptureNormalWindowPosition();
            ScheduleWindowPlacementSave();
            return;
        }
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        SaveWindowPlacement(viewModel);
        if (!viewModel.Settings.MinimizeToTray)
            return;
        Dispatcher.UIThread.Post(HideToTray);
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private async void BrowseWowDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the base World of Warcraft directory",
            AllowMultiple = false
        });

        if (folders.Count == 1 && DataContext is MainWindowViewModel viewModel)
            viewModel.SetWowPath(folders[0].Path.LocalPath);
    }

    private async void ResetApplicationClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;
        if (!await new ConfirmResetDialog().ShowDialog<bool>(this))
            return;
        await viewModel.ResetApplicationAsync();
        ResetWindowPlacementToDefaults();
    }

    private async void ReinstallAddonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AddonStatusRow addon } &&
            DataContext is MainWindowViewModel viewModel)
            await viewModel.ReinstallAddonAsync(addon);
    }

    private async void RemoveAddonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AddonStatusRow addon } ||
            DataContext is not MainWindowViewModel viewModel)
            return;
        if (!await new ConfirmAddonRemovalDialog(addon.DisplayName, addon.ClientName).ShowDialog<bool>(this))
            return;
        await viewModel.RemoveAddonAsync(addon);
    }

    private void OpenExternalLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string url } ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
            return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenThirdPartyNoticesClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenProjectNoticeClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "NOTICE.md");
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenRealmMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not RealmStatusRow realm ||
            DataContext is not MainWindowViewModel viewModel)
            return;

        viewModel.SelectedRealmStatus = realm;
        var syncCommand = realm.IsManaged
            ? viewModel.SyncManagedRealmsCommand
            : viewModel.RefreshCommand;
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                new MenuItem { Header = "Sync now", Command = syncCommand },
                new Separator(),
                new MenuItem
                {
                    Header = "Remove realm",
                    Command = viewModel.RemoveRealmCommand,
                    IsEnabled = realm.IsManaged
                }
            }
        };
        menu.Open(button);
    }

    private void AuctionRowAttached(object? sender, VisualTreeAttachmentEventArgs e) =>
        SetAuctionRowVisibility(sender, true);

    private void AuctionRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        CancelAuctionRowHover(sender);
        SetAuctionRowVisibility(sender, false);
    }

    private async void AuctionRowPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control { Tag: AuctionHouseItemViewModel item } row ||
            DataContext is not MainWindowViewModel viewModel)
            return;
        _auctionHoverDelay?.Cancel();
        _auctionHoverDelay?.Dispose();
        var cancellation = new CancellationTokenSource();
        _auctionHoverDelay = cancellation;
        try
        {
            await Task.Delay(220, cancellation.Token);
            if (!cancellation.IsCancellationRequested && row.IsPointerOver)
                viewModel.SetAuctionItemHovered(item, true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_auctionHoverDelay, cancellation))
                _auctionHoverDelay = null;
            cancellation.Dispose();
        }
    }

    private void AuctionRowPointerExited(object? sender, PointerEventArgs e) => CancelAuctionRowHover(sender);

    private void ToggleAuctionFavorite(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainWindowViewModel viewModel)
            return;
        var item = control.Tag as AuctionHouseItemViewModel ?? control.DataContext as AuctionHouseItemViewModel;
        if (item is null)
            return;
        viewModel.ToggleAuctionFavorite(item);
        e.Handled = true;
    }

    private void CancelAuctionRowHover(object? sender)
    {
        _auctionHoverDelay?.Cancel();
        if (sender is Control { Tag: AuctionHouseItemViewModel item } &&
            DataContext is MainWindowViewModel viewModel)
            viewModel.SetAuctionItemHovered(item, false);
    }

    private void SetAuctionRowVisibility(object? sender, bool isVisible)
    {
        if (sender is Control { Tag: AuctionHouseItemViewModel item } &&
            DataContext is MainWindowViewModel viewModel)
            viewModel.SetAuctionItemVisible(item, isVisible);
    }

}
