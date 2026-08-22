using System.ComponentModel;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TSM.Infrastructure;
using TSM.Models;
using TSM.Services;

namespace TSM.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private const int AuctionPageSize = 200;
    private enum AuctionSortColumn
    {
        Item,
        MinBuyout,
        Recent,
        Historical,
        SoldPerDay,
        SaleRate
    }

    public static TimeSpan AutomaticSyncInterval { get; } = TimeSpan.FromMinutes(30);
    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly WowInstallationService _wowService;
    private readonly AccountingService _accountingService;
    private readonly RealmDataService _realmDataService;
    private readonly AuctionHouseService _auctionHouseService;
    private readonly AuctionPriceHistoryStore _auctionPriceHistory;
    private readonly ItemIconService _itemIconService;
    private readonly ITsmApiClient _apiClient;
    private readonly AppDataSyncService _syncService;
    private readonly ILoginCredentialStore _credentialStore;
    private readonly BackgroundSyncScheduler _backgroundSync;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly DesktopNotificationService _notifications;
    private readonly GitHubUpdateService _appUpdates;
    private readonly DispatcherTimer _syncFreshnessTimer;
    private readonly DispatcherTimer _localDiscoveryTimer;
    private readonly Dictionary<string, string> _knownAddonVersions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WowInstallation> _installations = [];
    private bool _isLoggedIn;
    private bool _isSettingsOpen;
    private bool _isOnboardingVisible;
    private bool _isInitializing = true;
    private int _onboardingStep;
    private string _onboardingWowStatus = string.Empty;
    private bool _isBusy;
    private string _password = string.Empty;
    private string _loginError = string.Empty;
    private string _loginStatus = string.Empty;
    private string _headerText = "Sign in required";
    private string _statusDetail = "Connect a TradeSkillMaster account to continue";
    private int _selectedTab;
    private int _lastContentTab;
    private TsmAccount? _selectedAccount;
    private string? _selectedRealm;
    private bool _exportSales = true;
    private bool _exportPurchases = true;
    private bool _exportIncome;
    private bool _exportExpenses;
    private bool _exportExpired;
    private bool _exportCanceled;
    private string _exportStatus = "Select an account and realm to export local Accounting data.";
    private TsmRealmChoice? _selectedAvailableRealm;
    private ManagedRealmSelection? _selectedManagedRealm;
    private RealmStatusRow? _selectedRealmStatus;
    private string _realmManagementStatus = string.Empty;
    private string _addonManagementStatus = "Installed addons are checked during each data sync.";
    private bool _initialized;
    private bool _hasAddonBaseline;
    private bool _isCheckingLocalDiscovery;
    private string _localDiscoveryFingerprint = string.Empty;
    private IReadOnlyList<AuctionHouseItemRow> _auctionIndex = [];
    private IReadOnlyList<AuctionHouseItemViewModel> _auctionResults = [];
    private IReadOnlyList<AuctionMatch> _auctionMatches = [];
    private int _auctionTotalIndexed;
    private string _auctionSearchText = string.Empty;
    private string? _selectedAuctionRealm;
    private string _selectedAuctionFaction = "Both";
    private string _auctionResultSummary = "No auction data has been indexed yet.";
    private AuctionHouseItemViewModel? _selectedAuctionItem;
    private bool _isCheckingForUpdates;
    private string _updateStatus = "Updates have not been checked yet.";
    private GitHubAppUpdate? _availableAppUpdate;
    private CancellationTokenSource? _updateCheckCancellation;
    private Task? _updateCheckRunner;
    private CancellationTokenSource? _auctionIconCancellation;
    private readonly Dictionary<AuctionHouseItemViewModel, CancellationTokenSource> _auctionIconLoads = [];
    private readonly HashSet<AuctionHouseItemViewModel> _visibleAuctionItems = [];
    private readonly SemaphoreSlim _auctionIconSlots = new(4, 4);
    private CancellationTokenSource? _auctionSearchCancellation;
    private Task? _auctionSearchRunner;
    private AuctionSortColumn _auctionSortColumn = AuctionSortColumn.Item;
    private bool _auctionSortDescending;
    private bool _showAuctionFavoritesOnly;
    private readonly HashSet<string> _auctionFavoriteKeys = new(StringComparer.OrdinalIgnoreCase);

    private MainWindowViewModel(
        AppPaths paths,
        SettingsStore settingsStore,
        WowInstallationService wowService,
        AccountingService accountingService,
        RealmDataService realmDataService,
        AuctionHouseService auctionHouseService,
        AuctionPriceHistoryStore auctionPriceHistory,
        ItemIconService itemIconService,
        ITsmApiClient apiClient,
        AppDataSyncService syncService,
        ILoginCredentialStore credentialStore,
        BackgroundSyncScheduler backgroundSync,
        StartupRegistrationService startupRegistration,
        DesktopNotificationService notifications,
        GitHubUpdateService appUpdates,
        AppSettings settings)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _wowService = wowService;
        _accountingService = accountingService;
        _realmDataService = realmDataService;
        _auctionHouseService = auctionHouseService;
        _auctionPriceHistory = auctionPriceHistory;
        _itemIconService = itemIconService;
        _apiClient = apiClient;
        _syncService = syncService;
        _credentialStore = credentialStore;
        _backgroundSync = backgroundSync;
        _startupRegistration = startupRegistration;
        _notifications = notifications;
        _appUpdates = appUpdates;
        Settings = settings;
        _syncFreshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _syncFreshnessTimer.Tick += (_, _) => NotifySyncFreshnessChanged();
        _syncFreshnessTimer.Start();
        _localDiscoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _localDiscoveryTimer.Tick += DetectLocalChanges;
        _localDiscoveryTimer.Start();
        if (Enum.TryParse<AuctionSortColumn>(settings.AuctionSortColumn, true, out var savedSortColumn) &&
            Enum.IsDefined(savedSortColumn))
            _auctionSortColumn = savedSortColumn;
        _auctionSortDescending = settings.AuctionSortDescending;
        foreach (var favorite in settings.AuctionFavorites)
            _auctionFavoriteKeys.Add(ClassificationKey(favorite.Flavor, favorite.ItemString));
        _isOnboardingVisible = !settings.HasCompletedOnboarding;
        foreach (var realm in settings.ManagedRealms)
            ManagedRealms.Add(realm);
        SelectedManagedRealm = ManagedRealms.FirstOrDefault();

        LoginCommand = new AsyncCommand(LoginAsync, () => !IsBusy);
        LogoutCommand = new AsyncCommand(LogoutAsync, () => !IsBusy);
        OpenSettingsCommand = new AsyncCommand(OpenSettingsAsync, () => !IsBusy);
        CloseSettingsCommand = new DelegateCommand(SaveAndCloseSettings);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        FindWowCommand = new DelegateCommand(FindWowPath);
        OnboardingBackCommand = new DelegateCommand(GoBackInOnboarding);
        OnboardingNextCommand = new DelegateCommand(ContinueOnboarding);
        ExportAccountingCommand = new DelegateCommand(ExportAccounting, () => CanExportAccounting);
        AddRealmCommand = new AsyncCommand(AddSelectedRealmAsync,
            () => !IsBusy && IsLoggedIn && SelectedAvailableRealm is not null);
        RemoveRealmCommand = new AsyncCommand(RemoveSelectedRealmAsync,
            () => !IsBusy && IsLoggedIn && SelectedManagedRealm is not null);
        SyncManagedRealmsCommand = new AsyncCommand(SyncManagedRealmsAsync,
            () => !IsBusy && IsLoggedIn && SelectedManagedRealm is not null);
        ShowRealmDataCommand = new DelegateCommand(() => ShowContentPage(0));
        ShowAddonVersionsCommand = new DelegateCommand(() => ShowContentPage(1));
        CheckAddonUpdatesCommand = new AsyncCommand(CheckAddonUpdatesAsync,
            () => !IsBusy && IsLoggedIn && _installations.Count > 0);
        ShowAccountingCommand = new DelegateCommand(() => ShowContentPage(2));
        ShowAuctionHouseCommand = new DelegateCommand(() => ShowContentPage(3));
        RefreshAuctionIndexCommand = new DelegateCommand(LoadAuctionIndex);
        ToggleAuctionFavoritesFilterCommand = new DelegateCommand(ToggleAuctionFavoritesFilter);
        ClearAuctionSearchCommand = new DelegateCommand(ClearAuctionSearch);
        SortAuctionItemCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.Item));
        SortAuctionMinBuyoutCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.MinBuyout));
        SortAuctionRecentCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.Recent));
        SortAuctionHistoricalCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.Historical));
        SortAuctionSoldPerDayCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.SoldPerDay));
        SortAuctionSaleRateCommand = new DelegateCommand(() => ToggleAuctionSort(AuctionSortColumn.SaleRate));
        OpenAuctionItemInWowheadCommand = new DelegateCommand(OpenAuctionItemInWowhead,
            () => AuctionItemDrawerItem?.WowheadUri is not null);
        CloseAuctionItemDrawerCommand = new DelegateCommand(CloseAuctionItemDrawer);
        CheckForUpdatesCommand = new AsyncCommand(() => CheckForUpdatesAsync(true), () => !IsCheckingForUpdates);
        InstallUpdateCommand = new AsyncCommand(InstallUpdateAsync, () => AvailableAppUpdate is not null);

        RefreshLocalData();
    }

    public static MainWindowViewModel Create()
    {
        var paths = new AppPaths();
        var settingsStore = new SettingsStore(paths);
        var settings = settingsStore.Load();
        var wowService = new WowInstallationService();
        var savedVariablesParser = new LuaSavedVariablesParser();
        var accountingService = new AccountingService(savedVariablesParser);
        var appDataParser = new AppDataCatalogParser();
        var apiClient = new TsmLegacyApiClient(() => true);
        var credentialStore = new NativeLoginCredentialStore();
        var backgroundSync = new BackgroundSyncScheduler(AutomaticSyncInterval);
        var syncService = new AppDataSyncService(apiClient, appDataParser,
            new AppDataFileWriter(appDataParser), accountingService);
        return new MainWindowViewModel(paths, settingsStore, wowService,
            accountingService,
            new RealmDataService(savedVariablesParser, appDataParser),
            new AuctionHouseService(appDataParser, savedVariablesParser), new AuctionPriceHistoryStore(paths),
            new ItemIconService(paths), apiClient, syncService,
            credentialStore, backgroundSync, new StartupRegistrationService(),
            new DesktopNotificationService(), new GitHubUpdateService(), settings);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<RealmStatusRow> RealmStatuses { get; } = [];
    public ObservableCollection<AddonStatusRow> AddonStatuses { get; } = [];
    public ObservableCollection<TsmAccount> AccountingAccounts { get; } = [];
    public ObservableCollection<string> AccountingRealms { get; } = [];
    public ObservableCollection<TsmRealmChoice> AvailableRealms { get; } = [];
    public ObservableCollection<ManagedRealmSelection> ManagedRealms { get; } = [];
    public IReadOnlyList<AuctionHouseItemViewModel> AuctionResults
    {
        get => _auctionResults;
        private set => SetProperty(ref _auctionResults, value);
    }
    public ObservableCollection<string> AuctionRealmOptions { get; } = [];
    public IReadOnlyList<string> AuctionFactionOptions { get; } = ["Alliance", "Horde", "Both"];
    public IReadOnlyList<string> ReleaseChannels { get; } = ["Release", "Beta"];
    public IReadOnlyList<int> UpdateCheckIntervals { get; } = [1, 3, 6, 12, 24, 48, 168];
    public IReadOnlyList<ThirdPartyLicense> ThirdPartyLicenses { get; } =
    [
        new("TSM Desktop", "this application", "MIT", "Application source code",
            "https://github.com/Wuild/TSM-App", "https://github.com/Wuild/TSM-App/blob/main/LICENSE"),
        new("Avalonia UI", "12.1.0", "MIT", "Desktop UI framework and platform backends",
            "https://github.com/AvaloniaUI/Avalonia", "https://github.com/AvaloniaUI/Avalonia/blob/master/licence.md"),
        new("Avalonia BuildServices", "11.3.2", "MIT", "Avalonia build tooling",
            "https://github.com/AvaloniaUI/Avalonia.BuildServices", "https://github.com/AvaloniaUI/Avalonia.BuildServices/blob/master/LICENSE"),
        new("ANGLE", "2.1.27548.20260419", "BSD 3-Clause", "Windows graphics compatibility layer",
            "https://github.com/AvaloniaUI/angle", "https://github.com/AvaloniaUI/angle/blob/main/LICENSE"),
        new("Inter", "bundled by Avalonia 12.1.0", "SIL Open Font License 1.1", "Application typeface",
            "https://github.com/rsms/inter", "https://github.com/rsms/inter/blob/master/LICENSE.txt"),
        new("Lucide.Avalonia", "0.2.13", "MIT", "Avalonia integration for application icons",
            "https://github.com/dme-compunet/Lucide.Avalonia", "https://github.com/dme-compunet/Lucide.Avalonia/blob/main/LICENSE"),
        new("Lucide Icons", "via Lucide.Avalonia", "ISC", "Icon artwork",
            "https://github.com/lucide-icons/lucide", "https://github.com/lucide-icons/lucide/blob/main/LICENSE"),
        new("ktsu.CredentialCache", "1.3.19", "MIT", "Operating-system credential storage",
            "https://github.com/ktsu-dev/CredentialCache", "https://github.com/ktsu-dev/CredentialCache/blob/main/LICENSE.md"),
        new("ktsu.RoundTripStringJsonConverter", "1.0.18", "MIT", "Credential serialization dependency",
            "https://github.com/ktsu-dev/RoundTripStringJsonConverter", "https://github.com/ktsu-dev/RoundTripStringJsonConverter/blob/main/LICENSE.md"),
        new("ktsu.Semantics.Strings", "2.5.4", "MIT", "Typed credential strings",
            "https://github.com/ktsu-dev/Semantics", "https://github.com/ktsu-dev/Semantics/blob/main/LICENSE.md"),
        new("SkiaSharp", "3.119.4", "MIT", "2D rendering used by Avalonia",
            "https://github.com/mono/SkiaSharp", "https://github.com/mono/SkiaSharp/blob/main/LICENSE.md"),
        new("HarfBuzzSharp", "8.3.1.3", "MIT", "Text shaping used by Avalonia",
            "https://github.com/mono/SkiaSharp", "https://github.com/mono/SkiaSharp/blob/main/LICENSE.md"),
        new("MicroCom.Runtime", "0.11.6", "MIT", "Native interoperability used by Avalonia",
            "https://github.com/kekekeks/MicroCom", "https://github.com/kekekeks/MicroCom/blob/master/LICENSE"),
        new("Tmds.DBus.Protocol", "0.94.1", "MIT", "Linux desktop integration",
            "https://github.com/tmds/Tmds.DBus", "https://github.com/tmds/Tmds.DBus/blob/main/LICENSE"),
        new("Polyfill", "10.11.2", "MIT", "Build-time framework polyfills",
            "https://github.com/SimonCropp/Polyfill", "https://github.com/SimonCropp/Polyfill/blob/main/License.txt"),
        new("Microsoft .NET", "10", "MIT", "Self-contained application runtime",
            "https://github.com/dotnet/runtime", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"),
        new("tsm-app-linux", "compatibility reference", "MIT", "Clean-room protocol validation reference",
            "https://github.com/exceptionptr/tsm-app-linux", "https://github.com/exceptionptr/tsm-app-linux/blob/main/LICENSE")
    ];
    public string ExportDirectory => _paths.ExportDirectory;
    public string LastSyncText
    {
        get
        {
            if (Settings.LastSuccessfulSyncUtc is not { } timestamp)
                return "Never synced";
            var local = timestamp.ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? $"Last sync {local:HH:mm}"
                : $"Last sync {local:yyyy-MM-dd HH:mm}";
        }
    }
    public string LastSyncColor
    {
        get
        {
            if (Settings.LastSuccessfulSyncUtc is not { } timestamp)
                return "#737A81";
            var age = DateTimeOffset.UtcNow - timestamp.ToUniversalTime();
            if (age <= AutomaticSyncInterval + TimeSpan.FromMinutes(15))
                return "#69C58F";
            return age <= TimeSpan.FromHours(2) ? "#F2B84B" : "#F06464";
        }
    }

    private void NotifySyncFreshnessChanged()
    {
        OnPropertyChanged(nameof(LastSyncText));
        OnPropertyChanged(nameof(LastSyncColor));
    }
    public string InstalledAppVersion => _appUpdates.InstalledVersion;
    public bool CanAutomaticallyInstallUpdates => _appUpdates.CanInstallAutomatically;
    public string UpdateInstallAction => _appUpdates.CanInstallAutomatically ? "Install and restart" : "Open release page";
    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
                CheckForUpdatesCommand.RaiseCanExecuteChanged();
        }
    }
    public string UpdateStatus { get => _updateStatus; private set => SetProperty(ref _updateStatus, value); }
    public GitHubAppUpdate? AvailableAppUpdate
    {
        get => _availableAppUpdate;
        private set
        {
            if (!SetProperty(ref _availableAppUpdate, value))
                return;
            OnPropertyChanged(nameof(HasAvailableAppUpdate));
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasAvailableAppUpdate => AvailableAppUpdate is not null;

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        set
        {
            if (SetProperty(ref _isLoggedIn, value))
            {
                OnPropertyChanged(nameof(IsLoginVisible));
                OnPropertyChanged(nameof(IsShellVisible));
                OnPropertyChanged(nameof(IsContentVisible));
                OnPropertyChanged(nameof(IsLoggedOut));
                AddRealmCommand.RaiseCanExecuteChanged();
                RemoveRealmCommand.RaiseCanExecuteChanged();
                SyncManagedRealmsCommand.RaiseCanExecuteChanged();
                CheckAddonUpdatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsOnboardingVisible
    {
        get => _isOnboardingVisible;
        private set
        {
            if (!SetProperty(ref _isOnboardingVisible, value))
                return;
            OnPropertyChanged(nameof(IsLoginVisible));
            OnPropertyChanged(nameof(IsShellVisible));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsStartupLoadingVisible));
        }
    }
    public bool IsOnboardingLocationStep => _onboardingStep == 0;
    public bool IsOnboardingPreferencesStep => _onboardingStep == 1;
    public bool CanGoBackInOnboarding => _onboardingStep > 0;
    public string OnboardingNextText => IsOnboardingPreferencesStep ? "Finish setup" : "Continue";
    public string OnboardingWowStatus
    {
        get => _onboardingWowStatus;
        private set => SetProperty(ref _onboardingWowStatus, value);
    }
    public bool ShowNotifications
    {
        get => Settings.RealmDataNotification || Settings.AddonNotification ||
               Settings.AppUpdateNotification || Settings.NewsNotification;
        set
        {
            Settings.RealmDataNotification = value;
            Settings.AddonNotification = value;
            Settings.AppUpdateNotification = value;
            Settings.NewsNotification = value;
            OnPropertyChanged();
        }
    }
    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (SetProperty(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(IsLoginVisible));
                OnPropertyChanged(nameof(IsStartupLoadingVisible));
            }
        }
    }
    public bool IsStartupLoadingVisible => IsInitializing && !IsOnboardingVisible;
    public bool IsLoginVisible => !IsInitializing && !IsLoggedIn && !IsSettingsOpen && !IsOnboardingVisible;
    public bool IsLoggedOut => !IsLoggedIn;
    public bool IsShellVisible => (IsLoggedIn || IsSettingsOpen) && !IsOnboardingVisible;
    public bool IsContentVisible => IsLoggedIn && !IsSettingsOpen && !IsOnboardingVisible;
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        set
        {
            if (!SetProperty(ref _isSettingsOpen, value))
                return;
            OnPropertyChanged(nameof(IsLoginVisible));
            OnPropertyChanged(nameof(IsShellVisible));
            OnPropertyChanged(nameof(IsContentVisible));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(CurrentPageTitle));
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
                return;
            AddRealmCommand.RaiseCanExecuteChanged();
            RemoveRealmCommand.RaiseCanExecuteChanged();
            SyncManagedRealmsCommand.RaiseCanExecuteChanged();
            CheckAddonUpdatesCommand.RaiseCanExecuteChanged();
        }
    }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string LoginError { get => _loginError; private set => SetProperty(ref _loginError, value); }
    public string LoginStatus { get => _loginStatus; private set => SetProperty(ref _loginStatus, value); }
    public string HeaderText { get => _headerText; private set => SetProperty(ref _headerText, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value))
                return;
            OnPropertyChanged(nameof(IsRealmDataSelected));
            OnPropertyChanged(nameof(IsAddonVersionsSelected));
            OnPropertyChanged(nameof(IsAccountingSelected));
            OnPropertyChanged(nameof(IsAuctionHouseSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(CurrentPageTitle));
            if (value == 3)
                ResumeAuctionIconLoading();
            else
                SuspendAuctionIconLoading();
        }
    }
    public bool IsRealmDataSelected => SelectedTab == 0;
    public bool IsAddonVersionsSelected => SelectedTab == 1;
    public bool IsAccountingSelected => SelectedTab == 2;
    public bool IsAuctionHouseSelected => SelectedTab == 3;
    public bool IsSettingsSelected => SelectedTab == 4 && IsSettingsOpen;
    public string CurrentPageTitle => IsSettingsOpen ? "Settings" : SelectedTab switch
    {
        0 => "Realm data",
        1 => "Addons",
        2 => "Accounting export",
        3 => "Auction House",
        _ => "TSM Desktop"
    };
    public TsmAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
                return;
            LoadAccountingRealms();
            UpdateExportState();
        }
    }

    public string? SelectedRealm
    {
        get => _selectedRealm;
        set
        {
            if (SetProperty(ref _selectedRealm, value))
                UpdateExportState();
        }
    }

    public bool ExportSales { get => _exportSales; set { if (SetProperty(ref _exportSales, value)) UpdateExportState(); } }
    public bool ExportPurchases { get => _exportPurchases; set { if (SetProperty(ref _exportPurchases, value)) UpdateExportState(); } }
    public bool ExportIncome { get => _exportIncome; set { if (SetProperty(ref _exportIncome, value)) UpdateExportState(); } }
    public bool ExportExpenses { get => _exportExpenses; set { if (SetProperty(ref _exportExpenses, value)) UpdateExportState(); } }
    public bool ExportExpired { get => _exportExpired; set { if (SetProperty(ref _exportExpired, value)) UpdateExportState(); } }
    public bool ExportCanceled { get => _exportCanceled; set { if (SetProperty(ref _exportCanceled, value)) UpdateExportState(); } }
    public string ExportStatus { get => _exportStatus; private set => SetProperty(ref _exportStatus, value); }
    public string AuctionSearchText
    {
        get => _auctionSearchText;
        set
        {
            if (!SetProperty(ref _auctionSearchText, value))
                return;
            OnPropertyChanged(nameof(HasAuctionSearchText));
            QueueAuctionSearch(debounce: true);
        }
    }
    public bool HasAuctionSearchText => !string.IsNullOrWhiteSpace(AuctionSearchText);
    public string? SelectedAuctionRealm
    {
        get => _selectedAuctionRealm;
        set
        {
            if (SetProperty(ref _selectedAuctionRealm, value))
                QueueAuctionSearch();
        }
    }
    public string SelectedAuctionFaction
    {
        get => _selectedAuctionFaction;
        set
        {
            if (SetProperty(ref _selectedAuctionFaction, value))
                QueueAuctionSearch();
        }
    }
    public string AuctionResultSummary
    {
        get => _auctionResultSummary;
        private set => SetProperty(ref _auctionResultSummary, value);
    }
    public string AuctionItemSortIndicator => SortIndicator(AuctionSortColumn.Item);
    public string AuctionMinBuyoutSortIndicator => SortIndicator(AuctionSortColumn.MinBuyout);
    public string AuctionRecentSortIndicator => SortIndicator(AuctionSortColumn.Recent);
    public string AuctionHistoricalSortIndicator => SortIndicator(AuctionSortColumn.Historical);
    public string AuctionSoldPerDaySortIndicator => SortIndicator(AuctionSortColumn.SoldPerDay);
    public string AuctionSaleRateSortIndicator => SortIndicator(AuctionSortColumn.SaleRate);
    public bool ShowAuctionFavoritesOnly
    {
        get => _showAuctionFavoritesOnly;
        private set
        {
            if (!SetProperty(ref _showAuctionFavoritesOnly, value))
                return;
            OnPropertyChanged(nameof(AuctionFavoritesFilterColor));
        }
    }
    public string AuctionFavoritesFilterColor => ShowAuctionFavoritesOnly ? "#F2A93B" : "#A5ABB1";
    public string AuctionFavoritesLabel => $"Favorites ({_auctionFavoriteKeys.Count:N0})";
    public AuctionHouseItemViewModel? SelectedAuctionItem
    {
        get => _selectedAuctionItem;
        set
        {
            var previous = _selectedAuctionItem;
            if (!SetProperty(ref _selectedAuctionItem, value))
                return;
            OnPropertyChanged(nameof(AuctionItemDrawerItem));
            OnPropertyChanged(nameof(IsAuctionItemDrawerOpen));
            OpenAuctionItemInWowheadCommand.RaiseCanExecuteChanged();
            if (previous is not null && !_visibleAuctionItems.Contains(previous) &&
                _auctionIconLoads.TryGetValue(previous, out var previousCancellation))
                previousCancellation.Cancel();
            if (value is not null)
                StartAuctionIconLoad(value);
        }
    }
    public AuctionHouseItemViewModel? AuctionItemDrawerItem => SelectedAuctionItem;
    public bool IsAuctionItemDrawerOpen => AuctionItemDrawerItem is not null;
    public bool LoadOnlineItemIcons
    {
        get => Settings.LoadOnlineItemIcons;
        set
        {
            if (Settings.LoadOnlineItemIcons == value)
                return;
            Settings.LoadOnlineItemIcons = value;
            OnPropertyChanged();
            QueueAuctionSearch();
        }
    }
    public TsmRealmChoice? SelectedAvailableRealm
    {
        get => _selectedAvailableRealm;
        set
        {
            if (SetProperty(ref _selectedAvailableRealm, value))
                AddRealmCommand.RaiseCanExecuteChanged();
        }
    }
    public ManagedRealmSelection? SelectedManagedRealm
    {
        get => _selectedManagedRealm;
        set
        {
            if (SetProperty(ref _selectedManagedRealm, value))
            {
                RemoveRealmCommand?.RaiseCanExecuteChanged();
                SyncManagedRealmsCommand?.RaiseCanExecuteChanged();
            }
        }
    }
    public RealmStatusRow? SelectedRealmStatus
    {
        get => _selectedRealmStatus;
        set
        {
            if (!SetProperty(ref _selectedRealmStatus, value))
                return;
            SelectedManagedRealm = value is null || !value.IsManaged
                ? null
                : ManagedRealms.FirstOrDefault(realm =>
                    string.Equals(realm.GameVersion, value.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(realm.Id, value.RealmId, StringComparison.OrdinalIgnoreCase));
            SyncManagedRealmsCommand.RaiseCanExecuteChanged();
        }
    }
    public string RealmManagementStatus
    {
        get => _realmManagementStatus;
        private set => SetProperty(ref _realmManagementStatus, value);
    }
    public string AddonManagementStatus
    {
        get => _addonManagementStatus;
        private set => SetProperty(ref _addonManagementStatus, value);
    }
    public bool CanExportAccounting => SelectedAccount is not null && !string.IsNullOrWhiteSpace(SelectedRealm) &&
                                       (ExportSales || ExportPurchases || ExportIncome || ExportExpenses || ExportExpired || ExportCanceled);

    public AsyncCommand LoginCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public DelegateCommand CloseSettingsCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public DelegateCommand FindWowCommand { get; }
    public DelegateCommand OnboardingBackCommand { get; }
    public DelegateCommand OnboardingNextCommand { get; }
    public DelegateCommand ExportAccountingCommand { get; }
    public DelegateCommand ShowRealmDataCommand { get; }
    public DelegateCommand ShowAddonVersionsCommand { get; }
    public AsyncCommand CheckAddonUpdatesCommand { get; }
    public DelegateCommand ShowAccountingCommand { get; }
    public DelegateCommand ShowAuctionHouseCommand { get; }
    public DelegateCommand RefreshAuctionIndexCommand { get; }
    public DelegateCommand ToggleAuctionFavoritesFilterCommand { get; }
    public DelegateCommand ClearAuctionSearchCommand { get; }
    public DelegateCommand SortAuctionItemCommand { get; }
    public DelegateCommand SortAuctionMinBuyoutCommand { get; }
    public DelegateCommand SortAuctionRecentCommand { get; }
    public DelegateCommand SortAuctionHistoricalCommand { get; }
    public DelegateCommand SortAuctionSoldPerDayCommand { get; }
    public DelegateCommand SortAuctionSaleRateCommand { get; }
    public DelegateCommand OpenAuctionItemInWowheadCommand { get; }
    public DelegateCommand CloseAuctionItemDrawerCommand { get; }
    public AsyncCommand AddRealmCommand { get; private set; } = null!;
    public AsyncCommand RemoveRealmCommand { get; private set; } = null!;
    public AsyncCommand SyncManagedRealmsCommand { get; private set; } = null!;
    public AsyncCommand CheckForUpdatesCommand { get; }
    public AsyncCommand InstallUpdateCommand { get; }

    public void SetWowPath(string path)
    {
        Settings.WowPath = path;
        RefreshLocalData();
        OnboardingWowStatus = _installations.Count > 0
            ? $"Found {string.Join(", ", _installations.Select(installation => installation.Name))}"
            : "That folder does not contain a recognizable WoW installation.";
    }

    public async Task ReinstallAddonAsync(AddonStatusRow addon)
    {
        if (IsBusy || !addon.CanManage)
            return;
        IsBusy = true;
        AddonManagementStatus = $"Finding the latest {addon.DisplayName} package for {addon.ClientName}…";
        try
        {
            if (!_apiClient.IsAuthenticated)
                throw new InvalidOperationException("Sign in before reinstalling an addon.");
            var status = await _apiClient.GetStatusAsync(Settings.ReleaseChannel);
            var available = status.Addons.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, addon.AddonName, StringComparison.OrdinalIgnoreCase));
            if (available is null || string.IsNullOrWhiteSpace(available.Version))
                throw new InvalidOperationException($"TSM did not provide a {addon.AddonName} package for the {Settings.ReleaseChannel} channel.");

            await new AddonUpdateService(_apiClient).ReinstallAsync(addon.AddOnsPath, addon.AddonName,
                available.Version, Settings.ReleaseChannel);
            RefreshLocalData();
            AddonManagementStatus = $"Reinstalled {addon.DisplayName} {available.Version} for {addon.ClientName}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddonManagementStatus = $"Could not reinstall {addon.DisplayName}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RemoveAddonAsync(AddonStatusRow addon)
    {
        if (IsBusy || !addon.CanManage)
            return;
        IsBusy = true;
        AddonManagementStatus = $"Removing {addon.DisplayName} from {addon.ClientName}…";
        try
        {
            await AddonUpdateService.RemoveAsync(addon.AddOnsPath, addon.AddonName);
            RefreshLocalData();
            AddonManagementStatus = $"Removed {addon.DisplayName} from {addon.ClientName}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddonManagementStatus = $"Could not remove {addon.DisplayName}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SaveWindowPlacement(double width, double height, int? x, int? y, bool maximized)
    {
        Settings.WindowWidth = width;
        Settings.WindowHeight = height;
        Settings.WindowX = x;
        Settings.WindowY = y;
        Settings.WindowMaximized = maximized;
        _settingsStore.Save(Settings);
    }

    public async Task ResetApplicationAsync()
    {
        IsBusy = true;
        var issues = new List<string>();
        var credentialKey = Settings.CredentialKey;
        try
        {
            _updateCheckCancellation?.Cancel();
            _auctionSearchCancellation?.Cancel();
            CancelAuctionIconLoading();
            SelectedAuctionItem = null;
            foreach (var item in AuctionResults)
                item.Dispose();
            AuctionResults = [];

            if (!string.IsNullOrWhiteSpace(credentialKey))
            {
                try
                {
                    await Task.Run(() => _credentialStore.Delete(credentialKey));
                }
                catch (Exception exception)
                {
                    issues.Add($"remembered login: {exception.Message}");
                }
            }

            try
            {
                _startupRegistration.Apply(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                issues.Add($"startup registration: {exception.Message}");
            }

            try
            {
                await _itemIconService.ClearCacheAsync();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add($"item cache: {exception.Message}");
            }
            try
            {
                _auctionPriceHistory.Clear();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add($"price history: {exception.Message}");
            }

            _apiClient.Logout();
            Settings.ResetToDefaults();
            _auctionFavoriteKeys.Clear();
            ManagedRealms.Clear();
            AvailableRealms.Clear();
            Password = string.Empty;
            LoginError = string.Empty;
            LoginStatus = string.Empty;
            _auctionSortColumn = AuctionSortColumn.Item;
            _auctionSortDescending = false;
            ShowAuctionFavoritesOnly = false;
            _settingsStore.Save(Settings);
            RestartAutomaticUpdateChecks();
            SetOnboardingStep(0);
            IsInitializing = false;
            IsLoggedIn = false;
            IsSettingsOpen = false;
            IsOnboardingVisible = true;
            RefreshLocalData();
            OnboardingWowStatus = issues.Count == 0
                ? "App settings and caches were reset. Your WoW files were not changed."
                : $"Reset finished, but could not clear {string.Join("; ", issues)}";
            OnPropertyChanged(nameof(AuctionFavoritesLabel));
            OnPropertyChanged(nameof(ShowNotifications));
            OnPropertyChanged(nameof(LoadOnlineItemIcons));
            NotifySyncFreshnessChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void GoBackInOnboarding()
    {
        if (_onboardingStep > 0)
            SetOnboardingStep(_onboardingStep - 1);
    }

    private void ContinueOnboarding()
    {
        if (_onboardingStep == 0)
        {
            SetOnboardingStep(1);
            return;
        }

        Settings.HasCompletedOnboarding = true;
        SaveSettings();
        IsOnboardingVisible = false;
    }

    private void SetOnboardingStep(int step)
    {
        if (_onboardingStep == step)
            return;
        _onboardingStep = step;
        OnPropertyChanged(nameof(IsOnboardingLocationStep));
        OnPropertyChanged(nameof(IsOnboardingPreferencesStep));
        OnPropertyChanged(nameof(CanGoBackInOnboarding));
        OnPropertyChanged(nameof(OnboardingNextText));
    }

    public Task RefreshNowAsync() => IsBusy ? Task.CompletedTask : RefreshAsync();

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;
        _initialized = true;
        try
        {
            _backgroundSync.Start(AutomaticSyncAsync);
            RestartAutomaticUpdateChecks();
            ApplyStartupRegistration();

            if (!HasSavedLogin())
                return;

            IsBusy = true;
            LoginError = string.Empty;
            LoginStatus = $"Restoring saved login from {_credentialStore.StoreName}…";
            try
            {
                var result = await RestoreSavedLoginAsync();
                if (result is null)
                {
                    LoginStatus = string.Empty;
                    return;
                }

                if (!result.Success)
                {
                    LoginStatus = string.Empty;
                    LoginError = $"Saved login could not be restored. {result.Message}";
                    return;
                }

                IsLoggedIn = true;
                LoginStatus = string.Empty;
                await SyncDataAsync();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LoginStatus = string.Empty;
                LoginError = $"Could not read the saved login: {exception.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private bool HasSavedLogin() => Settings.RememberLogin &&
                                    !string.IsNullOrWhiteSpace(Settings.CredentialKey);

    private async Task LoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.Email) || string.IsNullOrWhiteSpace(Password))
        {
            LoginError = "Enter both an email address and password.";
            return;
        }

        IsBusy = true;
        LoginError = string.Empty;
        LoginStatus = string.Empty;
        try
        {
            var email = Settings.Email.Trim();
            var password = Password;
            var result = await _apiClient.LoginAsync(email, password);
            if (result.Success)
            {
                var credentialWarning = await UpdateSavedLoginAsync(email, password);
                Password = string.Empty;
                IsLoggedIn = true;
                _settingsStore.Save(Settings);
                await SyncDataAsync();
                if (credentialWarning is not null)
                    StatusDetail += $" · {credentialWarning}";
            }
            else
            {
                LoginError = result.Message;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LoginError = $"Authentication failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LogoutAsync()
    {
        IsBusy = true;
        _apiClient.Logout();
        try
        {
            if (!string.IsNullOrWhiteSpace(Settings.CredentialKey))
                await Task.Run(() => _credentialStore.Delete(Settings.CredentialKey));
            Settings.CredentialKey = string.Empty;
            LoginError = string.Empty;
        }
        catch (Exception exception)
        {
            Settings.RememberLogin = false;
            LoginError = $"Logged out, but the saved keychain entry could not be removed: {exception.Message}";
        }
        finally
        {
            Password = string.Empty;
            LoginStatus = string.Empty;
            IsSettingsOpen = false;
            IsLoggedIn = false;
            _settingsStore.Save(Settings);
            IsBusy = false;
        }
    }

    private void FindWowPath()
    {
        var path = _wowService.FindLikelyRoot();
        if (path is null)
        {
            StatusDetail = "No WoW installation was found automatically";
            OnboardingWowStatus = "No installation was found automatically. Choose the folder manually.";
            return;
        }

        SetWowPath(path);
    }

    private async Task OpenSettingsAsync()
    {
        if (SelectedTab is >= 0 and <= 3)
            _lastContentTab = SelectedTab;
        SelectedTab = 4;
        IsSettingsOpen = true;
        if (_apiClient.IsAuthenticated)
            await LoadAvailableRealmsAsync();
    }

    private async Task LoadAvailableRealmsAsync()
    {
        RealmManagementStatus = "Loading available realms…";
        try
        {
            var realms = await _apiClient.GetAvailableRealmsAsync();
            AvailableRealms.Clear();
            foreach (var realm in realms.Where(realm => !Settings.ManagedRealms.Any(managed =>
                         string.Equals(managed.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(managed.Id, realm.Id, StringComparison.OrdinalIgnoreCase))))
                AvailableRealms.Add(realm);
            SelectedAvailableRealm = AvailableRealms.FirstOrDefault();
            RealmManagementStatus = AvailableRealms.Count == 0
                ? "No additional realms are available for this account."
                : $"{AvailableRealms.Count} realm(s) available.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RealmManagementStatus = $"Could not load realms: {exception.Message}";
        }
    }

    private async Task AddSelectedRealmAsync()
    {
        var realm = SelectedAvailableRealm;
        if (realm is null)
            return;
        IsBusy = true;
        RealmManagementStatus = $"Adding {realm.DisplayName}…";
        try
        {
            await _apiClient.AddRealmAsync(realm.GameVersion, realm.Id);
            Settings.HiddenLocalRealms.RemoveAll(hidden =>
                string.Equals(hidden.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(hidden.Id, realm.Id, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(hidden.Name, realm.Name, StringComparison.OrdinalIgnoreCase)));
            if (!Settings.ManagedRealms.Any(managed =>
                    string.Equals(managed.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(managed.Id, realm.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var selection = new ManagedRealmSelection(
                    realm.GameVersion, realm.Region, realm.Name, realm.Id);
                Settings.ManagedRealms.Add(selection);
                ManagedRealms.Add(selection);
                SelectedManagedRealm = selection;
                _settingsStore.Save(Settings);
                SyncManagedRealmsCommand.RaiseCanExecuteChanged();
            }
            RealmManagementStatus = $"Added {realm.DisplayName}. Refreshing realm data…";
            await SyncDataAsync();
            AvailableRealms.Remove(realm);
            SelectedAvailableRealm = AvailableRealms.FirstOrDefault();
            RealmManagementStatus = $"Added {realm.DisplayName}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RealmManagementStatus = $"Could not add {realm.DisplayName}: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RemoveSelectedRealmAsync()
    {
        var realm = SelectedManagedRealm;
        if (realm is null)
            return;
        IsBusy = true;
        RealmManagementStatus = $"Removing {realm.DisplayName}…";
        try
        {
            await _apiClient.RemoveRealmAsync(realm.GameVersion, realm.Region, realm.Name);
            Settings.ManagedRealms.RemoveAll(managed =>
                string.Equals(managed.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(managed.Id, realm.Id, StringComparison.OrdinalIgnoreCase));
            if (!Settings.HiddenLocalRealms.Any(hidden =>
                    string.Equals(hidden.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(hidden.Id, realm.Id, StringComparison.OrdinalIgnoreCase)))
                Settings.HiddenLocalRealms.Add(realm);
            ManagedRealms.Remove(realm);
            SelectedManagedRealm = ManagedRealms.FirstOrDefault();
            SelectedRealmStatus = null;
            _settingsStore.Save(Settings);
            RefreshLocalData();
            await LoadAvailableRealmsAsync();
            RealmManagementStatus = $"Removed {realm.DisplayName}.";
            StatusDetail = $"Removed {realm.DisplayName}.";
            SyncManagedRealmsCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RealmManagementStatus = $"Could not remove {realm.DisplayName}: {exception.Message}";
            StatusDetail = RealmManagementStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SyncManagedRealmsAsync()
    {
        var realm = SelectedManagedRealm;
        if (realm is null)
            return;
        IsBusy = true;
        RealmManagementStatus = $"Syncing {realm.DisplayName}…";
        StatusDetail = $"Syncing {realm.DisplayName}…";
        try
        {
            await SyncDataAsync();
            RealmManagementStatus = $"Synced {realm.DisplayName}.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RealmManagementStatus = $"Could not sync added realms: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowContentPage(int index)
    {
        if (!IsLoggedIn)
            return;
        if (IsSettingsOpen)
            SaveSettings();
        IsSettingsOpen = false;
        SelectedTab = index;
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            if (_apiClient.IsAuthenticated)
                await SyncDataAsync();
            else
                RefreshLocalData();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckAddonUpdatesAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        AddonManagementStatus = $"Checking the TSM {Settings.ReleaseChannel} channel for addon updates…";
        try
        {
            if (!_apiClient.IsAuthenticated)
                throw new InvalidOperationException("Sign in before checking addon updates.");
            var status = await _apiClient.GetStatusAsync(Settings.ReleaseChannel);
            var updated = await new AddonUpdateService(_apiClient)
                .UpdateInstalledAsync(_installations, status.Addons, Settings.ReleaseChannel);
            RefreshLocalData();
            AddonManagementStatus = updated.Count == 0
                ? "All installed TSM addons are up to date."
                : $"Updated {string.Join(", ", updated)}.";
            if (updated.Count > 0 && Settings.AddonNotification)
                _notifications.Show("TradeSkillMaster addons updated", AddonManagementStatus);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AddonManagementStatus = $"Could not check addon updates: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SaveAndCloseSettings()
    {
        SaveSettings();
        IsSettingsOpen = false;
        SelectedTab = _lastContentTab;
    }

    private void SaveSettings()
    {
        _settingsStore.Save(Settings);
        RestartAutomaticUpdateChecks();
        ApplyStartupRegistration();
        RefreshLocalData();
    }

    private void ApplyStartupRegistration()
    {
        try
        {
            _startupRegistration.Apply(Settings.RunAtStartup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusDetail = $"Could not apply startup settings: {exception.Message}";
        }
    }

    private async Task SyncDataAsync(
        bool allowReauthentication = true,
        CancellationToken cancellationToken = default)
    {
        StatusDetail = "Checking for updated TSM realm data…";
        try
        {
            var installations = _wowService.Discover(Settings.WowPath);
            var accounts = _accountingService.FindAccounts(installations);
            var result = await _syncService.SyncAsync(installations, accounts,
                Settings.ReleaseChannel, cancellationToken);
            RefreshLocalData();
            ShowSyncNotifications(result);
            if (result.Errors.Count == 0)
            {
                Settings.LastSuccessfulSyncUtc = DateTimeOffset.UtcNow;
                NotifySyncFreshnessChanged();
                _settingsStore.Save(Settings);
            }
            if (result.Errors.Count > 0)
            {
                StatusDetail = $"Synced {result.DownloadedEntries} data set(s); " +
                               $"{result.Errors.Count} issue(s): {result.Errors[0]}";
            }
            else if (result.WrittenFiles == 0)
            {
                StatusDetail = "Signed in, but TradeSkillMaster_AppHelper was not found in a configured WoW client";
            }
            else
            {
                StatusDetail = result.DownloadedEntries == 0
                    ? $"Realm data is current ({result.WrittenFiles} AppHelper installation(s))"
                    : $"Downloaded {result.DownloadedEntries} data set(s) into {result.WrittenFiles} AppHelper installation(s)";
            }
        }
        catch (HttpRequestException exception) when (
            allowReauthentication &&
            exception.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            var restored = await RestoreSavedLoginAsync(cancellationToken);
            if (restored?.Success == true)
            {
                await SyncDataAsync(false, cancellationToken);
                return;
            }
            _apiClient.Logout();
            IsLoggedIn = false;
            LoginError = "Your saved login is no longer valid. Enter your password to sign in again.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = $"Data sync failed: {exception.Message}";
        }
    }

    private async Task AutomaticSyncAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            if (!_apiClient.IsAuthenticated)
            {
                var restored = await RestoreSavedLoginAsync(cancellationToken);
                if (restored?.Success != true)
                {
                    RefreshLocalData();
                    return;
                }
                IsLoggedIn = true;
            }
            await SyncDataAsync(cancellationToken: cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _syncFreshnessTimer.Stop();
        _localDiscoveryTimer.Stop();
        _localDiscoveryTimer.Tick -= DetectLocalChanges;
        if (_auctionSearchCancellation is not null)
            await _auctionSearchCancellation.CancelAsync();
        if (_auctionIconCancellation is not null)
            await _auctionIconCancellation.CancelAsync();
        if (_updateCheckCancellation is not null)
            await _updateCheckCancellation.CancelAsync();
        if (_updateCheckRunner is not null)
        {
            try
            {
                await _updateCheckRunner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_auctionSearchRunner is not null)
        {
            try
            {
                await _auctionSearchRunner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _updateCheckCancellation?.Dispose();
        _auctionIconCancellation?.Dispose();
        _auctionSearchCancellation?.Dispose();
        foreach (var item in AuctionResults)
            item.Dispose();
        _itemIconService.Dispose();
        _appUpdates.Dispose();
        await _backgroundSync.DisposeAsync();
    }

    private void RestartAutomaticUpdateChecks()
    {
        _updateCheckCancellation?.Cancel();
        _updateCheckCancellation?.Dispose();
        _updateCheckCancellation = null;
        _updateCheckRunner = null;
        if (!Settings.AutomaticUpdateChecks)
            return;

        _updateCheckCancellation = new CancellationTokenSource();
        _updateCheckRunner = RunAutomaticUpdateChecksAsync(_updateCheckCancellation.Token);
    }

    private async Task RunAutomaticUpdateChecksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CheckForUpdatesAsync(false, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(Settings.UpdateCheckIntervalHours), cancellationToken);
                await CheckForUpdatesAsync(false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckForUpdatesAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (IsCheckingForUpdates)
            return;
        IsCheckingForUpdates = true;
        if (manual)
            UpdateStatus = "Checking GitHub Releases…";
        try
        {
            var update = await _appUpdates.CheckAsync(
                string.Equals(Settings.ReleaseChannel, "Beta", StringComparison.OrdinalIgnoreCase),
                cancellationToken: cancellationToken);
            AvailableAppUpdate = update;
            if (update is null)
            {
                UpdateStatus = $"TSM {InstalledAppVersion} is up to date.";
                return;
            }

            UpdateStatus = update.PackageDownload is not null
                ? $"TSM {update.Version} is available for this platform."
                : $"TSM {update.Version} is available, but this release has no package for the current platform.";
            if (Settings.AppUpdateNotification &&
                !string.Equals(Settings.LastNotifiedGitHubVersion, update.Version, StringComparison.OrdinalIgnoreCase))
            {
                _notifications.Show("TradeSkillMaster update available", UpdateStatus);
                Settings.LastNotifiedGitHubVersion = update.Version;
                _settingsStore.Save(Settings);
            }

            if (!manual && Settings.AutomaticallyInstallUpdates &&
                _appUpdates.CanInstallAutomatically && update.PackageDownload is not null)
                await InstallUpdateAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateStatus = $"Could not check for updates: {exception.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        var update = AvailableAppUpdate;
        if (update is null)
            return;
        if (!_appUpdates.CanInstallAutomatically || update.PackageDownload is null)
        {
            GitHubUpdateService.OpenReleasePage(update);
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatus = $"Downloading TSM {update.Version}…";
        try
        {
            await _appUpdates.InstallAsync(update);
            UpdateStatus = "Update installed. Restarting…";
            if (Application.Current is App app)
                app.Shutdown();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateStatus = $"Could not install the update: {exception.Message}";
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private async Task<ApiLoginResult?> RestoreSavedLoginAsync(CancellationToken cancellationToken = default)
    {
        if (!Settings.RememberLogin ||
            string.IsNullOrWhiteSpace(Settings.CredentialKey))
            return null;
        var saved = await Task.Run(() => _credentialStore.Load(Settings.CredentialKey));
        if (saved is null)
            return null;
        Settings.Email = saved.Email;
        return await _apiClient.LoginAsync(saved.Email, saved.Password, cancellationToken);
    }

    private async Task<string?> UpdateSavedLoginAsync(string email, string password)
    {
        try
        {
            if (Settings.RememberLogin)
            {
                Settings.CredentialKey = await Task.Run(() =>
                    _credentialStore.Save(Settings.CredentialKey, email, password));
            }
            else if (!string.IsNullOrWhiteSpace(Settings.CredentialKey))
            {
                await Task.Run(() => _credentialStore.Delete(Settings.CredentialKey));
                Settings.CredentialKey = string.Empty;
            }
            return null;
        }
        catch (Exception exception)
        {
            return $"Login was not saved in {_credentialStore.StoreName}: {exception.Message}";
        }
    }

    private void ExportAccounting()
    {
        if (SelectedAccount is null || string.IsNullOrWhiteSpace(SelectedRealm))
            return;

        try
        {
            var options = new AccountingExportOptions(ExportSales, ExportPurchases, ExportIncome,
                ExportExpenses, ExportExpired, ExportCanceled);
            var result = _accountingService.Export(SelectedAccount, SelectedRealm, options, _paths.ExportDirectory);
            var parts = new List<string>();
            if (result.Files.Count > 0)
                parts.Add($"Exported {result.Files.Count} file(s) to {_paths.ExportDirectory}.");
            if (result.MissingCategories.Count > 0)
                parts.Add($"No data: {string.Join(", ", result.MissingCategories)}.");
            if (result.Errors.Count > 0)
                parts.Add($"Errors: {string.Join("; ", result.Errors)}");
            ExportStatus = parts.Count > 0 ? string.Join(" ", parts) : "No categories were selected.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
        {
            ExportStatus = $"Export failed: {exception.Message}";
        }
    }

    private async void DetectLocalChanges(object? sender, EventArgs e)
    {
        if (_isCheckingLocalDiscovery || IsBusy || string.IsNullOrWhiteSpace(Settings.WowPath))
            return;
        _isCheckingLocalDiscovery = true;
        try
        {
            var fingerprint = await Task.Run(() => _wowService.GetDiscoveryFingerprint(Settings.WowPath));
            if (string.Equals(fingerprint, _localDiscoveryFingerprint, StringComparison.Ordinal))
                return;
            RefreshLocalData(false);
            StatusDetail = "Detected changes to installed WoW clients, addons, or local realms";
            if (IsLoggedIn)
            {
                IsBusy = true;
                try
                {
                    await SyncDataAsync();
                }
                finally
                {
                    IsBusy = false;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _isCheckingLocalDiscovery = false;
        }
    }

    private void RefreshLocalData(bool refreshAuctionIndex = true)
    {
        _installations = _wowService.Discover(Settings.WowPath);
        RealmStatuses.Clear();
        AddonStatuses.Clear();

        var scannedAddons = _wowService.ScanAddons(_installations);
        if (_hasAddonBaseline && Settings.AddonNotification)
        {
            var changed = scannedAddons.Where(addon =>
                _knownAddonVersions.TryGetValue(addon.Name, out var previous) &&
                !string.Equals(previous, addon.Version, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (changed.Length > 0)
                _notifications.Show("TradeSkillMaster addons changed",
                    changed.Length == 1
                        ? $"{changed[0].Name} is now version {changed[0].Version}."
                        : $"Detected version changes for {changed.Length} installed addons.");
        }
        _knownAddonVersions.Clear();
        foreach (var addon in scannedAddons)
        {
            _knownAddonVersions[addon.Name] = addon.Version;
            AddonStatuses.Add(addon);
        }
        _hasAddonBaseline = true;

        if (_installations.Count == 0)
        {
            HeaderText = "WoW directory required";
            StatusDetail = "Choose the base World of Warcraft directory in Settings";
            AddonStatuses.Add(new AddonStatusRow("TradeSkillMaster", "—", "WoW directory not configured", "#FF6B6B"));
        }

        LoadAccountingAccounts();
        LoadRealmDataStatuses();
        if (refreshAuctionIndex)
            LoadAuctionIndex();

        if (_installations.Count > 0)
        {
            HeaderText = "Local game data connected";
            StatusDetail = $"{_installations.Count} client(s), {AddonStatuses.Count} addon folder(s), {RealmStatuses.Count} realm(s)";
        }
        _localDiscoveryFingerprint = _wowService.GetDiscoveryFingerprint(Settings.WowPath);
        CheckAddonUpdatesCommand.RaiseCanExecuteChanged();
    }

    private void LoadAccountingAccounts()
    {
        var previous = SelectedAccount?.DisplayName;
        AccountingAccounts.Clear();
        foreach (var account in _accountingService.FindAccounts(_installations))
            AccountingAccounts.Add(account);
        SelectedAccount = AccountingAccounts.FirstOrDefault(account => account.DisplayName == previous) ?? AccountingAccounts.FirstOrDefault();
        if (AccountingAccounts.Count == 0)
            ExportStatus = "No TradeSkillMaster.lua account data was found.";
    }

    private void LoadAuctionIndex()
    {
        var previousRealm = SelectedAuctionRealm;
        try
        {
            _auctionIndex = _auctionHouseService.BuildIndex(AccountingAccounts, _installations);
            _auctionPriceHistory.Capture(Settings.AuctionFavorites, _auctionIndex);
            AuctionRealmOptions.Clear();
            AuctionRealmOptions.Add("All realms");
            foreach (var realm in _auctionIndex
                         .Select(item => $"{item.Flavor} · {item.BaseRealm}")
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Order(StringComparer.OrdinalIgnoreCase))
                AuctionRealmOptions.Add(realm);
            SelectedAuctionRealm = AuctionRealmOptions.FirstOrDefault(option =>
                                       string.Equals(option, previousRealm, StringComparison.OrdinalIgnoreCase))
                                   ?? AuctionRealmOptions.FirstOrDefault();
            QueueAuctionSearch();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _auctionIndex = [];
            _auctionMatches = [];
            _auctionTotalIndexed = 0;
            _auctionSearchCancellation?.Cancel();
            CancelAuctionIconLoading();
            foreach (var item in AuctionResults)
                item.Dispose();
            AuctionResults = [];
            AuctionResultSummary = $"Auction data could not be indexed: {exception.Message}";
        }
    }

    private void ClearAuctionSearch()
    {
        AuctionSearchText = string.Empty;
    }

    private void ToggleAuctionFavoritesFilter()
    {
        ShowAuctionFavoritesOnly = !ShowAuctionFavoritesOnly;
        QueueAuctionSearch();
    }

    public void ToggleAuctionFavorite(AuctionHouseItemViewModel item)
    {
        var key = ClassificationKey(item.Flavor, item.ItemString);
        var isFavorite = !_auctionFavoriteKeys.Contains(key);
        if (isFavorite)
        {
            _auctionFavoriteKeys.Add(key);
            var baseItemString = BaseItemString(item.ItemString);
            if (!Settings.AuctionFavorites.Any(favorite =>
                    string.Equals(favorite.Flavor, item.Flavor, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(favorite.ItemString, baseItemString, StringComparison.OrdinalIgnoreCase)))
                Settings.AuctionFavorites.Add(new AuctionFavorite(item.Flavor, baseItemString));
        }
        else
        {
            _auctionFavoriteKeys.Remove(key);
            Settings.AuctionFavorites.RemoveAll(favorite =>
                string.Equals(ClassificationKey(favorite.Flavor, favorite.ItemString), key,
                    StringComparison.OrdinalIgnoreCase));
        }

        foreach (var row in AuctionResults.Where(row =>
                     string.Equals(ClassificationKey(row.Flavor, row.ItemString), key,
                         StringComparison.OrdinalIgnoreCase)))
            row.IsFavorite = isFavorite;
        OnPropertyChanged(nameof(AuctionFavoritesLabel));
        _settingsStore.Save(Settings);
        if (isFavorite)
            _auctionPriceHistory.Capture(Settings.AuctionFavorites, _auctionIndex);
        item.SetPriceHistory(_auctionPriceHistory.Get(item.Flavor, item.ItemString));
        QueueAuctionSearch();
    }

    private static string ClassificationKey(AuctionHouseItemRow item) =>
        $"{item.Flavor}|{item.BaseItemString}";

    private static string ClassificationKey(string flavor, string itemString)
    {
        return $"{flavor}|{BaseItemString(itemString)}";
    }

    private static string BaseItemString(string itemString)
    {
        var separator = itemString.StartsWith("i:", StringComparison.OrdinalIgnoreCase)
            ? itemString.IndexOf(':', 2)
            : -1;
        return separator > 2 ? itemString[..separator] : itemString;
    }

    private void QueueAuctionSearch(bool debounce = false)
    {
        var previousCancellation = _auctionSearchCancellation;
        previousCancellation?.Cancel();
        if (_auctionSearchRunner?.IsCompleted == true)
            previousCancellation?.Dispose();

        var cancellation = new CancellationTokenSource();
        _auctionSearchCancellation = cancellation;
        var index = _auctionIndex;
        var realm = SelectedAuctionRealm;
        var faction = SelectedAuctionFaction;
        var searchText = AuctionSearchText;
        var favoritesOnly = ShowAuctionFavoritesOnly;
        var favoriteKeys = new HashSet<string>(_auctionFavoriteKeys, StringComparer.OrdinalIgnoreCase);
        var sortColumn = _auctionSortColumn;
        var sortDescending = _auctionSortDescending;
        _auctionSearchRunner = RunAuctionSearchAsync(index, realm, faction, searchText,
            favoritesOnly, favoriteKeys, sortColumn,
            sortDescending, debounce, cancellation);
    }

    private async Task RunAuctionSearchAsync(IReadOnlyList<AuctionHouseItemRow> index, string? realm,
        string faction, string searchText,
        bool favoritesOnly, IReadOnlySet<string> favoriteKeys,
        AuctionSortColumn sortColumn, bool sortDescending,
        bool debounce, CancellationTokenSource cancellation)
    {
        try
        {
            if (debounce)
                await Task.Delay(140, cancellation.Token);
            var result = await Task.Run(() => FilterAuctionResults(index, realm, faction, searchText,
                favoritesOnly, favoriteKeys, sortColumn,
                sortDescending, cancellation.Token),
                cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested)
                    return;
                ApplyAuctionResults(result);
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!ReferenceEquals(_auctionSearchCancellation, cancellation))
                cancellation.Dispose();
        }
    }

    private static AuctionSearchResult FilterAuctionResults(IReadOnlyList<AuctionHouseItemRow> index,
        string? selectedRealm, string selectedFaction, string searchText,
        bool favoritesOnly, IReadOnlySet<string> favoriteKeys,
        AuctionSortColumn sortColumn, bool sortDescending, CancellationToken cancellationToken)
    {
        var filterRealm = !string.IsNullOrWhiteSpace(selectedRealm) &&
                          !string.Equals(selectedRealm, "All realms", StringComparison.OrdinalIgnoreCase);
        var searchTerms = searchText.Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var groups = new Dictionary<(string Flavor, string ItemString), List<AuctionHouseItemRow>>();
        for (var itemIndex = 0; itemIndex < index.Count; itemIndex++)
        {
            if ((itemIndex & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var item = index[itemIndex];
            if (filterRealm && !string.Equals($"{item.Flavor} · {item.BaseRealm}", selectedRealm,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(selectedFaction, "Both", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Faction, selectedFaction, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.Faction, "Both", StringComparison.OrdinalIgnoreCase))
                continue;
            if (searchTerms.Any(term =>
                    !item.ItemName.Contains(term, StringComparison.OrdinalIgnoreCase) &&
                    !item.ItemString.Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (favoritesOnly && !favoriteKeys.Contains(ClassificationKey(item)))
                continue;
            var key = (item.Flavor.ToUpperInvariant(), item.BaseItemString);
            if (!groups.TryGetValue(key, out var variants))
            {
                variants = [];
                groups.Add(key, variants);
            }
            variants.Add(item);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var matchIndex = 0;
        var matches = groups.Values.Select(variants =>
        {
            if ((matchIndex++ & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var summary = AggregateAuctionRows(variants);
            return new AuctionMatch(summary, variants, favoriteKeys.Contains(ClassificationKey(summary)));
        });
        var sortedMatches = SortAuctionMatches(matches, sortColumn, sortDescending).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new AuctionSearchResult(sortedMatches, index.Count);
    }

    private void ToggleAuctionSort(AuctionSortColumn column)
    {
        if (_auctionSortColumn == column)
            _auctionSortDescending = !_auctionSortDescending;
        else
        {
            _auctionSortColumn = column;
            _auctionSortDescending = false;
        }

        Settings.AuctionSortColumn = _auctionSortColumn.ToString();
        Settings.AuctionSortDescending = _auctionSortDescending;
        _settingsStore.Save(Settings);

        OnPropertyChanged(nameof(AuctionItemSortIndicator));
        OnPropertyChanged(nameof(AuctionMinBuyoutSortIndicator));
        OnPropertyChanged(nameof(AuctionRecentSortIndicator));
        OnPropertyChanged(nameof(AuctionHistoricalSortIndicator));
        OnPropertyChanged(nameof(AuctionSoldPerDaySortIndicator));
        OnPropertyChanged(nameof(AuctionSaleRateSortIndicator));
        _auctionMatches = SortAuctionMatches(_auctionMatches, _auctionSortColumn, _auctionSortDescending).ToArray();
        ReplaceVisibleAuctionResults(Math.Max(AuctionPageSize, AuctionResults.Count));
    }

    private void OpenAuctionItemInWowhead()
    {
        var uri = AuctionItemDrawerItem?.WowheadUri;
        if (uri is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            StatusDetail = $"Could not open Wowhead: {exception.Message}";
        }
    }

    private string SortIndicator(AuctionSortColumn column) => _auctionSortColumn == column
        ? _auctionSortDescending ? "↓" : "↑"
        : string.Empty;

    private static IOrderedEnumerable<AuctionMatch> SortAuctionMatches(
        IEnumerable<AuctionMatch> rows, AuctionSortColumn column, bool descending)
    {
        if (column == AuctionSortColumn.Item)
            return descending
                ? rows.OrderByDescending(row => row.IsFavorite)
                    .ThenByDescending(row => row.Summary.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Summary.ItemString, StringComparer.OrdinalIgnoreCase)
                : rows.OrderByDescending(row => row.IsFavorite)
                    .ThenBy(row => row.Summary.ItemName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Summary.ItemString, StringComparer.OrdinalIgnoreCase);

        Func<AuctionMatch, long?> value = column switch
        {
            AuctionSortColumn.MinBuyout => row => row.Summary.MinBuyout,
            AuctionSortColumn.Recent => row => row.Summary.MarketValueRecent,
            AuctionSortColumn.Historical => row => row.Summary.Historical,
            AuctionSortColumn.SoldPerDay => row => row.Summary.RegionSoldPerDay,
            AuctionSortColumn.SaleRate => row => row.Summary.RegionSaleRatePercent,
            _ => row => null
        };
        return descending
            ? rows.OrderByDescending(row => row.IsFavorite)
                .ThenBy(row => value(row) is null)
                .ThenByDescending(row => value(row))
                .ThenBy(row => row.Summary.ItemName, StringComparer.OrdinalIgnoreCase)
            : rows.OrderByDescending(row => row.IsFavorite)
                .ThenBy(row => value(row) is null)
                .ThenBy(row => value(row))
                .ThenBy(row => row.Summary.ItemName, StringComparer.OrdinalIgnoreCase);
    }

    private static AuctionHouseItemRow AggregateAuctionRows(IReadOnlyList<AuctionHouseItemRow> variants)
    {
        var first = variants[0];
        return new AuctionHouseItemRow(
            first.ItemString,
            variants.Select(item => item.ItemName).FirstOrDefault(name => name != first.ItemString) ?? first.ItemName,
            variants.Count == 1 ? first.Realm : "Multiple realms",
            first.Flavor,
            MinimumPositive(variants.Select(item => item.MinBuyout)),
            SumPositive(variants.Select(item => item.NumAuctions)),
            AveragePositive(variants.Select(item => item.MarketValueRecent)),
            AveragePositive(variants.Select(item => item.Historical)),
            variants.Max(item => item.UpdatedAt),
            AveragePositive(variants.Select(item => item.RegionSaleAverage)),
            AveragePositive(variants.Select(item => item.RegionSoldPerDay)),
            AveragePositive(variants.Select(item => item.RegionSaleRatePercent)));
    }

    private static long? MinimumPositive(IEnumerable<long?> values) =>
        values.Where(value => value is > 0).Min() is { } value ? value : null;

    private static long? AveragePositive(IEnumerable<long?> values)
    {
        var present = values.Where(value => value is > 0).Select(value => (decimal)value!.Value).ToArray();
        return present.Length == 0 ? null : (long)Math.Round(present.Average());
    }

    private static long? SumPositive(IEnumerable<long?> values)
    {
        var present = values.Where(value => value is > 0).Select(value => (decimal)value!.Value).ToArray();
        return present.Length == 0 ? null : (long)Math.Min(long.MaxValue, present.Sum());
    }

    private void ApplyAuctionResults(AuctionSearchResult result)
    {
        _auctionMatches = result.Rows;
        _auctionTotalIndexed = result.TotalIndexed;
        ReplaceVisibleAuctionResults(AuctionPageSize);
    }

    public void LoadMoreAuctionResults()
    {
        if (AuctionResults.Count >= _auctionMatches.Count)
            return;
        var next = _auctionMatches.Skip(AuctionResults.Count).Take(AuctionPageSize)
            .Select(CreateAuctionItemViewModel);
        AuctionResults = AuctionResults.Concat(next).ToArray();
        UpdateAuctionResultSummary();
    }

    private void ReplaceVisibleAuctionResults(int count)
    {
        SelectedAuctionItem = null;
        CancelAuctionIconLoading();
        foreach (var item in AuctionResults)
            item.Dispose();
        AuctionResults = _auctionMatches.Take(count)
            .Select(CreateAuctionItemViewModel).ToArray();
        UpdateAuctionResultSummary();
        if (LoadOnlineItemIcons && AuctionResults.Count > 0 && IsAuctionHouseSelected)
            _auctionIconCancellation = new CancellationTokenSource();
    }

    private void UpdateAuctionResultSummary()
    {
        AuctionResultSummary = _auctionTotalIndexed == 0
            ? "No AuctionDB item data was found. Sync a realm with AppHelper installed first."
            : AuctionResults.Count < _auctionMatches.Count
                ? $"Showing {AuctionResults.Count:N0} of {_auctionMatches.Count:N0} matches · scroll for more"
                : $"{_auctionMatches.Count:N0} item price record(s) · {_auctionTotalIndexed:N0} indexed locally";
    }

    private AuctionHouseItemViewModel CreateAuctionItemViewModel(AuctionMatch match) =>
        new(match.Summary, match.Variants, match.IsFavorite,
            _auctionPriceHistory.Get(match.Summary.Flavor, match.Summary.ItemString));

    private sealed record AuctionMatch(
        AuctionHouseItemRow Summary,
        IReadOnlyList<AuctionHouseItemRow> Variants,
        bool IsFavorite);
    private sealed record AuctionSearchResult(IReadOnlyList<AuctionMatch> Rows, int TotalIndexed);

    private void CancelAuctionIconLoading()
    {
        SuspendAuctionIconLoading();
        _visibleAuctionItems.Clear();
    }

    private void SuspendAuctionIconLoading()
    {
        _auctionIconCancellation?.Cancel();
        _auctionIconCancellation?.Dispose();
        _auctionIconCancellation = null;
        foreach (var cancellation in _auctionIconLoads.Values)
            cancellation.Cancel();
        _auctionIconLoads.Clear();
    }

    private void ResumeAuctionIconLoading()
    {
        if (!LoadOnlineItemIcons || AuctionResults.Count == 0 || _auctionIconCancellation is not null)
            return;
        _auctionIconCancellation = new CancellationTokenSource();
        foreach (var item in _visibleAuctionItems)
            StartAuctionIconLoad(item);
        if (SelectedAuctionItem is not null)
            StartAuctionIconLoad(SelectedAuctionItem);
    }

    public void SetAuctionItemVisible(AuctionHouseItemViewModel item, bool isVisible)
    {
        if (isVisible)
        {
            _visibleAuctionItems.Add(item);
            StartAuctionIconLoad(item);
            return;
        }

        _visibleAuctionItems.Remove(item);
        if (!ReferenceEquals(SelectedAuctionItem, item) &&
            _auctionIconLoads.TryGetValue(item, out var cancellation))
            cancellation.Cancel();
    }

    private void CloseAuctionItemDrawer()
    {
        SelectedAuctionItem = null;
    }

    private void StartAuctionIconLoad(AuctionHouseItemViewModel item)
    {
        if (!LoadOnlineItemIcons || item.Icon is not null || _auctionIconCancellation is null ||
            _auctionIconLoads.ContainsKey(item))
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_auctionIconCancellation.Token);
        _auctionIconLoads[item] = cancellation;
        _ = LoadAuctionIconAsync(item, cancellation);
    }

    private async Task LoadAuctionIconAsync(AuctionHouseItemViewModel row, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var slotAcquired = false;
        try
        {
            await _auctionIconSlots.WaitAsync(token);
            slotAcquired = true;
            var result = await _itemIconService.GetAsync(row.ItemString, row.Flavor, token);
            if (result is null)
                return;
            var icon = await Task.Run(() => new Bitmap(result.Path), token);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !AuctionResults.Contains(row))
                        return;
                    row.SetIcon(result, icon);
                    icon = null;
                });
            }
            finally
            {
                icon?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                                           ArgumentException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
        finally
        {
            if (slotAcquired)
                _auctionIconSlots.Release();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_auctionIconLoads.TryGetValue(row, out var current) && ReferenceEquals(current, cancellation))
                    _auctionIconLoads.Remove(row);
                var shouldRestart = cancellation.IsCancellationRequested &&
                                    (_visibleAuctionItems.Contains(row) || ReferenceEquals(SelectedAuctionItem, row));
                cancellation.Dispose();
                if (shouldRestart)
                    StartAuctionIconLoad(row);
            });
        }
    }

    private void LoadRealmDataStatuses()
    {
        var selectedGameVersion = SelectedRealmStatus?.GameVersion;
        var selectedRealmId = SelectedRealmStatus?.RealmId;
        RealmStatuses.Clear();
        foreach (var status in _realmDataService.GetRealmStatuses(AccountingAccounts, _installations))
        {
            if (!Settings.HiddenLocalRealms.Any(hidden => StatusMatchesRealm(status, hidden)))
                RealmStatuses.Add(status);
        }

        foreach (var managed in Settings.ManagedRealms)
        {
            var displayName = string.IsNullOrWhiteSpace(managed.Region)
                ? managed.Name
                : $"{managed.Region.ToUpperInvariant()}-{managed.Name}";
            if (RealmStatuses.Any(row => StatusMatchesRealm(row, managed)))
            {
                var index = RealmStatuses.ToList().FindIndex(row => StatusMatchesRealm(row, managed));
                RealmStatuses[index] = RealmStatuses[index] with
                {
                    GameVersion = managed.GameVersion,
                    RealmId = managed.Id,
                    IsManaged = true
                };
            }
            else
            {
                RealmStatuses.Add(new RealmStatusRow(displayName, "Pending", "Pending",
                    "Waiting for sync", "#F5A623", managed.GameVersion, managed.Id, true));
            }
        }

        if (RealmStatuses.Count == 0)
        {
            foreach (var installation in _installations)
                RealmStatuses.Add(new RealmStatusRow(installation.Name, "Missing", "Missing", "Never", "#F5A623",
                    installation.Name));
        }

        for (var index = 0; index < RealmStatuses.Count; index++)
        {
            var row = RealmStatuses[index];
            var flavor = FormatGameFlavor(row.GameVersion);
            var installed = IsGameFlavorInstalled(row.GameVersion);
            RealmStatuses[index] = row with
            {
                Flavor = flavor,
                InstallationStatus = installed ? "Installed" : "Not installed",
                InstallationColor = installed ? "#69C58F" : "#D99A43"
            };
        }
        SelectedRealmStatus = RealmStatuses.FirstOrDefault(row => row.IsManaged &&
            string.Equals(row.GameVersion, selectedGameVersion, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.RealmId, selectedRealmId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StatusMatchesRealm(RealmStatusRow row, ManagedRealmSelection realm)
    {
        var displayName = string.IsNullOrWhiteSpace(realm.Region)
            ? realm.Name
            : $"{realm.Region.ToUpperInvariant()}-{realm.Name}";
        if (!string.Equals(row.RegionRealm, displayName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(row.RegionRealm, realm.Name, StringComparison.OrdinalIgnoreCase) &&
            !row.RegionRealm.EndsWith("-" + realm.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrWhiteSpace(row.GameVersion))
            return true;
        return string.Equals(CanonicalGameVersion(row.GameVersion), CanonicalGameVersion(realm.GameVersion),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalGameVersion(string gameVersion) => gameVersion switch
    {
        "Retail" => "retail",
        "Classic" or "Progression" => "bcc",
        "Classic Era" => "classic",
        "Anniversary" => "anniversary",
        _ => gameVersion.Trim().ToLowerInvariant()
    };

    private bool IsGameFlavorInstalled(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
            return false;
        var expected = gameVersion switch
        {
            "retail" => "Retail",
            "bcc" => "Classic",
            "classic" => "Classic Era",
            "anniversary" => "Anniversary",
            _ => gameVersion
        };
        return _installations.Any(installation =>
            string.Equals(installation.Name, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatGameFlavor(string gameVersion) => gameVersion switch
    {
        "retail" => "Retail",
        "bcc" => "Progression",
        "classic" => "Classic Era",
        "anniversary" => "Anniversary",
        _ => string.IsNullOrWhiteSpace(gameVersion) ? "Unknown" : gameVersion
    };

    private void LoadAccountingRealms()
    {
        var previous = SelectedRealm;
        AccountingRealms.Clear();
        if (SelectedAccount is not null)
        {
            try
            {
                foreach (var realm in _accountingService.FindRealms(SelectedAccount))
                    AccountingRealms.Add(realm);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
                ExportStatus = $"Could not read SavedVariables: {exception.Message}";
            }
        }
        SelectedRealm = AccountingRealms.FirstOrDefault(realm => realm == previous) ?? AccountingRealms.FirstOrDefault();
    }

    private void UpdateExportState()
    {
        OnPropertyChanged(nameof(CanExportAccounting));
        ExportAccountingCommand.RaiseCanExecuteChanged();
    }

    private void ShowSyncNotifications(DataSyncResult result)
    {
        if (result.UpdatedAddons is { Count: > 0 } && Settings.AddonNotification)
            _notifications.Show("TradeSkillMaster addons updated",
                $"Installed updates for {string.Join(", ", result.UpdatedAddons)}.");

        if (Settings.AppUpdateNotification &&
            result.LatestAppVersion > TsmLegacyApiClient.ApplicationVersion &&
            result.LatestAppVersion != Settings.LastNotifiedAppVersion)
        {
            _notifications.Show("TradeSkillMaster update available",
                $"Desktop app version {result.LatestAppVersion} is available (installed protocol version {TsmLegacyApiClient.ApplicationVersion}).");
            Settings.LastNotifiedAppVersion = result.LatestAppVersion;
            _settingsStore.Save(Settings);
        }

        if (result.DownloadedEntries > 0 && Settings.RealmDataNotification)
            _notifications.Show("TradeSkillMaster realm data",
                $"Downloaded {result.DownloadedEntries} updated data set(s).");

        if (!Settings.NewsNotification || result.NewsId <= 0 || result.NewsId == Settings.LastNotifiedNewsId ||
            string.IsNullOrWhiteSpace(result.NewsMessage))
            return;
        _notifications.Show("TradeSkillMaster news", result.NewsMessage);
        Settings.LastNotifiedNewsId = result.NewsId;
        _settingsStore.Save(Settings);
    }
}
