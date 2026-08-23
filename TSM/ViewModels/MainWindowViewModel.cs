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
    private readonly ItemDatabaseService _itemDatabase;
    private readonly ItemIconService _itemIconService;
    private readonly ITsmApiClient _apiClient;
    private readonly AppDataSyncService _syncService;
    private readonly ILoginCredentialStore _credentialStore;
    private readonly BackgroundSyncScheduler _backgroundSync;
    private readonly StartupRegistrationService _startupRegistration;
    private readonly DesktopNotificationService _notifications;
    private readonly GitHubUpdateService _appUpdates;
    private readonly BackupService _backupService;
    private readonly AppLogService _logService;
    private readonly DispatcherTimer _syncFreshnessTimer;
    private readonly DispatcherTimer _localDiscoveryTimer;
    private readonly DispatcherTimer _accountingFileChangeTimer;
    private readonly DispatcherTimer _settingsApplyTimer;
    private readonly List<FileSystemWatcher> _accountingFileWatchers = [];
    private readonly Dictionary<string, string> _knownAddonVersions = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WowInstallation> _installations = [];
    private bool _isLoggedIn;
    private bool _isSettingsOpen;
    private bool _isOnboardingVisible;
    private bool _isInitializing = true;
    private bool _suppressSettingsPersistence;
    private int _onboardingStep;
    private string _onboardingWowStatus = string.Empty;
    private bool _isBusy;
    private string _password = string.Empty;
    private string _loginError = string.Empty;
    private string _loginStatus = string.Empty;
    private string _headerText = "Sign in required";
    private string _statusDetail = "Connect a TradeSkillMaster account to continue";
    private string _startupRegistrationStatus = string.Empty;
    private BackupEntry? _selectedBackup;
    private string _backupStatus = "Backups have not been loaded.";
    private string _logText = string.Empty;
    private string _logStatus = "Logs have not been loaded.";
    private int _selectedTab = 2;
    private int _lastContentTab = 2;
    private TsmAccount? _selectedAccount;
    private string? _selectedRealm;
    private AccountingStatistics _accountingStatistics = AccountingStatistics.Empty;
    private string _selectedAccountingPlayer = "All players";
    private IReadOnlyList<string> _accountingPlayers = ["All players"];
    private AccountingPeriodOption _selectedAccountingPeriod;
    private string _accountingStatisticsStatus = "Select an account and realm to view statistics.";
    private CancellationTokenSource? _accountingStatisticsCancellation;
    private Task? _accountingStatisticsRunner;
    private CancellationTokenSource? _accountingIconCancellation;
    private Task? _accountingIconRunner;
    private IReadOnlyList<AccountingItemViewModel> _accountingTopItems = [];
    private AccountingItemViewModel? _selectedAccountingItem;
    private readonly SemaphoreSlim _accountingIconSlots = new(3, 3);
    private bool _suppressAccountingRefresh;
    private bool _accountingStatisticsDirty = true;
    private string? _loadedAccountingStatisticsKey;
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
    private bool _showAdvancedAuctionFilters;
    private string _selectedAuctionCategory = "All categories";
    private string _selectedAuctionSubcategory = "All subcategories";
    private string _selectedAuctionQuality = "All qualities";
    private string _selectedAuctionInventoryType = "All slots";
    private string _auctionResultSummary = "No auction data has been indexed yet.";
    private AuctionHouseItemViewModel? _selectedAuctionItem;
    private bool _isCheckingForUpdates;
    private bool _isInstallingUpdate;
    private double _updateProgressPercent;
    private bool _isUpdateProgressIndeterminate;
    private string _updateProgressDetail = string.Empty;
    private string _updateStatus = "Updates have not been checked yet.";
    private GitHubAppUpdate? _availableAppUpdate;
    private CancellationTokenSource? _updateCheckCancellation;
    private Task? _updateCheckRunner;
    private CancellationTokenSource? _auctionIconCancellation;
    private readonly Dictionary<AuctionHouseItemViewModel, CancellationTokenSource> _auctionIconLoads = [];
    private readonly HashSet<string> _queuedAuctionItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<AuctionHouseItemViewModel> _visibleAuctionItems = [];
    private readonly SemaphoreSlim _auctionIconSlots = new(3, 3);
    private readonly SemaphoreSlim _cachedAuctionIconSlots = new(4, 4);
    private readonly SemaphoreSlim _tooltipSlot = new(1, 1);
    private CancellationTokenSource? _activeTooltipCancellation;
    private Task? _activeTooltipRunner;
    private AuctionHouseItemViewModel? _activeTooltipItem;
    private int _auctionItemInfoTotal;
    private int _auctionItemInfoFinished;
    private int _auctionItemInfoFailed;
    private DateTimeOffset? _auctionItemInfoPausedUntil;
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
        ItemDatabaseService itemDatabase,
        ItemIconService itemIconService,
        ITsmApiClient apiClient,
        AppDataSyncService syncService,
        ILoginCredentialStore credentialStore,
        BackgroundSyncScheduler backgroundSync,
        StartupRegistrationService startupRegistration,
        DesktopNotificationService notifications,
        GitHubUpdateService appUpdates,
        BackupService backupService,
        AppLogService logService,
        AppSettings settings)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _wowService = wowService;
        _accountingService = accountingService;
        _realmDataService = realmDataService;
        _auctionHouseService = auctionHouseService;
        _auctionPriceHistory = auctionPriceHistory;
        _itemDatabase = itemDatabase;
        _itemIconService = itemIconService;
        _itemIconService.DownloadPaused += ItemInfoDownloadPaused;
        _apiClient = apiClient;
        _syncService = syncService;
        _credentialStore = credentialStore;
        _backgroundSync = backgroundSync;
        _startupRegistration = startupRegistration;
        _notifications = notifications;
        _appUpdates = appUpdates;
        _backupService = backupService;
        _logService = logService;
        Settings = settings;
        Settings.PropertyChanged += SettingsChanged;
        _selectedAccountingPlayer = settings.SelectedAccountingPlayer;
        _selectedAuctionRealm = settings.SelectedAuctionRealm;
        _selectedAuctionFaction = settings.SelectedAuctionFaction;
        _selectedAccountingPeriod = AccountingPeriods[^1];
        _syncFreshnessTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _syncFreshnessTimer.Tick += (_, _) => NotifySyncFreshnessChanged();
        _syncFreshnessTimer.Start();
        _localDiscoveryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _localDiscoveryTimer.Tick += DetectLocalChanges;
        _localDiscoveryTimer.Start();
        _accountingFileChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _accountingFileChangeTimer.Tick += AccountingFilesChanged;
        _settingsApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _settingsApplyTimer.Tick += ApplyDeferredSettings;
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
        CloseSettingsCommand = new DelegateCommand(CloseSettings);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        FindWowCommand = new DelegateCommand(FindWowPath);
        OnboardingBackCommand = new DelegateCommand(GoBackInOnboarding);
        OnboardingNextCommand = new DelegateCommand(ContinueOnboarding);
        RefreshAccountingStatisticsCommand = new DelegateCommand(() => QueueAccountingStatistics(true));
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
        ShowLogsCommand = new DelegateCommand(() => ShowContentPage(4));
        RefreshLogsCommand = new DelegateCommand(() => RefreshLogs());
        RefreshAuctionIndexCommand = new DelegateCommand(LoadAuctionIndex);
        ToggleAuctionFavoritesFilterCommand = new DelegateCommand(ToggleAuctionFavoritesFilter);
        ToggleAdvancedAuctionFiltersCommand = new DelegateCommand(ToggleAdvancedAuctionFilters);
        ClearAdvancedAuctionFiltersCommand = new DelegateCommand(ClearAdvancedAuctionFilters);
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
        CheckForUpdatesCommand = new AsyncCommand(() => CheckForUpdatesAsync(true),
            () => !IsCheckingForUpdates && !IsInstallingUpdate);
        InstallUpdateCommand = new AsyncCommand(InstallUpdateAsync,
            () => AvailableAppUpdate is not null && !IsCheckingForUpdates && !IsInstallingUpdate);
        RefreshBackupsCommand = new AsyncCommand(RefreshBackupsAsync, () => !IsBusy && IsLoggedIn);
        CreateBackupCommand = new AsyncCommand(CreateBackupsAsync, () => !IsBusy);
        UploadLogCommand = new AsyncCommand(UploadLogAsync,
            () => !IsBusy && IsLoggedIn && Settings.UploadAddonData);

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
            new AppDataFileWriter(appDataParser), accountingService, dataUploader:
            new TsmDataUploadService(apiClient, savedVariablesParser, () => settings.UploadSystemId),
            uploadsEnabled: () => settings.UploadAddonData);
        var itemDatabase = new ItemDatabaseService();
        var backupService = new BackupService(paths, apiClient, () => settings.UploadSystemId);
        var logService = new AppLogService(paths);
        logService.WriteSync("info", "TSM Desktop started.");
        return new MainWindowViewModel(paths, settingsStore, wowService,
            accountingService,
            new RealmDataService(savedVariablesParser, appDataParser),
            new AuctionHouseService(appDataParser, savedVariablesParser), new AuctionPriceHistoryStore(paths),
            itemDatabase, new ItemIconService(paths, itemDatabase: itemDatabase), apiClient, syncService,
            credentialStore, backgroundSync, new StartupRegistrationService(),
            new DesktopNotificationService(), new GitHubUpdateService(), backupService, logService, settings);
    }

    public AppSettings Settings { get; }
    public ObservableCollection<RealmStatusRow> RealmStatuses { get; } = [];
    public ObservableCollection<AddonStatusRow> AddonStatuses { get; } = [];
    public ObservableCollection<TsmAccount> AccountingAccounts { get; } = [];
    public ObservableCollection<BackupEntry> Backups { get; } = [];
    public BackupEntry? SelectedBackup
    {
        get => _selectedBackup;
        set => SetProperty(ref _selectedBackup, value);
    }
    public string BackupStatus { get => _backupStatus; private set => SetProperty(ref _backupStatus, value); }
    public string LogText
    {
        get => _logText;
        private set
        {
            if (SetProperty(ref _logText, value))
                OnPropertyChanged(nameof(HasLogEntries));
        }
    }
    public bool HasLogEntries => LogText.Length > 0;
    public string LogStatus { get => _logStatus; private set => SetProperty(ref _logStatus, value); }
    public bool IsPremium => _apiClient.IsPremium;
    public ObservableCollection<string> AccountingRealms { get; } = [];
    public IReadOnlyList<string> AccountingPlayers
    {
        get => _accountingPlayers;
        private set => SetProperty(ref _accountingPlayers, value);
    }
    public IReadOnlyList<AccountingItemViewModel> AccountingTopItems
    {
        get => _accountingTopItems;
        private set => SetProperty(ref _accountingTopItems, value);
    }
    public AccountingItemViewModel? SelectedAccountingItem
    {
        get => _selectedAccountingItem;
        set
        {
            if (!SetProperty(ref _selectedAccountingItem, value) || value is null)
                return;
            OpenAccountingItem(value);
        }
    }
    public IReadOnlyList<AccountingPeriodOption> AccountingPeriods { get; } =
    [
        new("1D", 1), new("1W", 7), new("1M", 30), new("3M", 90), new("6M", 180),
        new("1Y", 365), new("2Y", 730), new("All", null)
    ];
    public ObservableCollection<TsmRealmChoice> AvailableRealms { get; } = [];
    public ObservableCollection<ManagedRealmSelection> ManagedRealms { get; } = [];
    public ObservableCollection<string> AuctionSubcategoryOptions { get; } = ["All subcategories"];
    public IReadOnlyList<string> AuctionCategoryOptions => WowItemTaxonomy.CategoryOptions;
    public IReadOnlyList<string> AuctionQualityOptions => WowItemTaxonomy.QualityOptions;
    public IReadOnlyList<string> AuctionInventoryTypeOptions => WowItemTaxonomy.InventoryTypeOptions;
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
        new("Microsoft.Data.Sqlite", "10.0.10", "MIT", "Managed access to the bundled item catalog",
            "https://github.com/dotnet/efcore", "https://github.com/dotnet/efcore/blob/main/LICENSE.txt"),
        new("SQLitePCLRaw", "3.0.4", "Apache 2.0", "Cross-platform native SQLite integration",
            "https://github.com/ericsink/SQLitePCL.raw", "https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE"),
        new("SQLite", "3.53.3", "Public Domain", "Embedded item database engine",
            "https://sqlite.org/", "https://sqlite.org/copyright.html"),
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
    public bool IsInstallingUpdate
    {
        get => _isInstallingUpdate;
        private set
        {
            if (!SetProperty(ref _isInstallingUpdate, value))
                return;
            OnPropertyChanged(nameof(IsUpdateProgressVisible));
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
            InstallUpdateCommand.RaiseCanExecuteChanged();
        }
    }
    public bool IsUpdateProgressVisible => IsInstallingUpdate;
    public double UpdateProgressPercent
    {
        get => _updateProgressPercent;
        private set => SetProperty(ref _updateProgressPercent, value);
    }
    public bool IsUpdateProgressIndeterminate
    {
        get => _isUpdateProgressIndeterminate;
        private set => SetProperty(ref _isUpdateProgressIndeterminate, value);
    }
    public string UpdateProgressDetail
    {
        get => _updateProgressDetail;
        private set => SetProperty(ref _updateProgressDetail, value);
    }
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
                RefreshBackupsCommand?.RaiseCanExecuteChanged();
                UploadLogCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsPremium));
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
            OnPropertyChanged(nameof(IsRealmDataSelected));
            OnPropertyChanged(nameof(IsAddonVersionsSelected));
            OnPropertyChanged(nameof(IsAccountingSelected));
            OnPropertyChanged(nameof(IsAuctionHouseSelected));
            OnPropertyChanged(nameof(IsLogsSelected));
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
            RefreshBackupsCommand?.RaiseCanExecuteChanged();
            CreateBackupCommand?.RaiseCanExecuteChanged();
            UploadLogCommand?.RaiseCanExecuteChanged();
        }
    }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string LoginError { get => _loginError; private set => SetProperty(ref _loginError, value); }
    public string LoginStatus { get => _loginStatus; private set => SetProperty(ref _loginStatus, value); }
    public string HeaderText { get => _headerText; private set => SetProperty(ref _headerText, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public string StartupRegistrationStatus
    {
        get => _startupRegistrationStatus;
        private set => SetProperty(ref _startupRegistrationStatus, value);
    }
    public string RenderingBackend => Program.RenderingBackend;
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
            OnPropertyChanged(nameof(IsLogsSelected));
            OnPropertyChanged(nameof(IsSettingsSelected));
            OnPropertyChanged(nameof(CurrentPageTitle));
            if (value == 2)
                QueueAccountingStatistics();
            if (value == 3)
                ResumeAuctionIconLoading();
            else
                SuspendAuctionIconLoading();
            if (value == 4 && !IsSettingsOpen)
                RefreshLogs();
        }
    }
    public bool IsRealmDataSelected => SelectedTab == 0 && !IsSettingsOpen;
    public bool IsAddonVersionsSelected => SelectedTab == 1 && !IsSettingsOpen;
    public bool IsAccountingSelected => SelectedTab == 2 && !IsSettingsOpen;
    public bool IsAuctionHouseSelected => SelectedTab == 3 && !IsSettingsOpen;
    public bool IsLogsSelected => SelectedTab == 4 && !IsSettingsOpen;
    public bool IsSettingsSelected => IsSettingsOpen;
    public string CurrentPageTitle => IsSettingsOpen ? "Settings" : SelectedTab switch
    {
        0 => "Realm data",
        1 => "Addons",
        2 => "Accounting",
        3 => "Auction House",
        4 => "Logs",
        _ => "TSM Desktop"
    };
    public TsmAccount? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
                return;
            Settings.SelectedAccountingAccount = value?.DisplayName ?? string.Empty;
            var wasSuppressed = _suppressAccountingRefresh;
            _suppressAccountingRefresh = true;
            try
            {
                ResetAccountingPlayerSelection();
                LoadAccountingRealms();
            }
            finally
            {
                _suppressAccountingRefresh = wasSuppressed;
            }
            InvalidateAccountingStatistics();
        }
    }

    public string? SelectedRealm
    {
        get => _selectedRealm;
        set
        {
            if (SetProperty(ref _selectedRealm, value))
            {
                Settings.SelectedAccountingRealm = value ?? string.Empty;
                ResetAccountingPlayerSelection();
                InvalidateAccountingStatistics();
            }
        }
    }

    public AccountingStatistics AccountingStatistics
    {
        get => _accountingStatistics;
        private set => SetProperty(ref _accountingStatistics, value);
    }
    public string SelectedAccountingPlayer
    {
        get => _selectedAccountingPlayer;
        set
        {
            if (SetProperty(ref _selectedAccountingPlayer, value ?? "All players"))
            {
                Settings.SelectedAccountingPlayer = _selectedAccountingPlayer;
                OnPropertyChanged(nameof(SelectedAccountingPlayerIndex));
                InvalidateAccountingStatistics();
            }
        }
    }
    public int SelectedAccountingPlayerIndex
    {
        get => Math.Max(0, AccountingPlayers.ToList().IndexOf(SelectedAccountingPlayer));
        set
        {
            if (value >= 0 && value < AccountingPlayers.Count)
                SelectedAccountingPlayer = AccountingPlayers[value];
        }
    }
    public AccountingPeriodOption SelectedAccountingPeriod
    {
        get => _selectedAccountingPeriod;
        set
        {
            if (value is not null && SetProperty(ref _selectedAccountingPeriod, value))
                InvalidateAccountingStatistics();
        }
    }
    public string AccountingStatisticsStatus
    {
        get => _accountingStatisticsStatus;
        private set => SetProperty(ref _accountingStatisticsStatus, value);
    }

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
            {
                Settings.SelectedAuctionRealm = value ?? "All realms";
                QueueAuctionSearch();
            }
        }
    }
    public string SelectedAuctionFaction
    {
        get => _selectedAuctionFaction;
        set
        {
            if (SetProperty(ref _selectedAuctionFaction, value))
            {
                Settings.SelectedAuctionFaction = value;
                QueueAuctionSearch();
            }
        }
    }
    public bool ShowAdvancedAuctionFilters
    {
        get => _showAdvancedAuctionFilters;
        private set => SetProperty(ref _showAdvancedAuctionFilters, value);
    }
    public bool HasAdvancedAuctionFilters =>
        !string.Equals(SelectedAuctionCategory, "All categories", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(SelectedAuctionSubcategory, "All subcategories", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(SelectedAuctionQuality, "All qualities", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(SelectedAuctionInventoryType, "All slots", StringComparison.OrdinalIgnoreCase);
    public string AdvancedAuctionFilterColor => HasAdvancedAuctionFilters ? "#F2A93B" : "#A5ABB1";
    public string SelectedAuctionCategory
    {
        get => _selectedAuctionCategory;
        set
        {
            if (!SetProperty(ref _selectedAuctionCategory, value ?? "All categories"))
                return;
            AuctionSubcategoryOptions.Clear();
            foreach (var option in WowItemTaxonomy.SubcategoryOptions(_selectedAuctionCategory))
                AuctionSubcategoryOptions.Add(option);
            _selectedAuctionSubcategory = "All subcategories";
            OnPropertyChanged(nameof(SelectedAuctionSubcategory));
            AdvancedAuctionFilterChanged();
        }
    }
    public string SelectedAuctionSubcategory
    {
        get => _selectedAuctionSubcategory;
        set
        {
            if (SetProperty(ref _selectedAuctionSubcategory, value ?? "All subcategories"))
                AdvancedAuctionFilterChanged();
        }
    }
    public string SelectedAuctionQuality
    {
        get => _selectedAuctionQuality;
        set
        {
            if (SetProperty(ref _selectedAuctionQuality, value ?? "All qualities"))
                AdvancedAuctionFilterChanged();
        }
    }
    public string SelectedAuctionInventoryType
    {
        get => _selectedAuctionInventoryType;
        set
        {
            if (SetProperty(ref _selectedAuctionInventoryType, value ?? "All slots"))
                AdvancedAuctionFilterChanged();
        }
    }
    public string AuctionResultSummary
    {
        get => _auctionResultSummary;
        private set => SetProperty(ref _auctionResultSummary, value);
    }
    public bool IsAuctionItemInfoStatusVisible => LoadOnlineItemIcons && IsAuctionItemInfoLoading;
    public bool IsAuctionItemInfoLoading => _auctionItemInfoFinished < _auctionItemInfoTotal;
    public double AuctionItemInfoProgress => _auctionItemInfoTotal == 0
        ? 0
        : Math.Clamp((double)_auctionItemInfoFinished / _auctionItemInfoTotal * 100, 0, 100);
    public string AuctionItemInfoStatus
    {
        get
        {
            if (_auctionItemInfoPausedUntil is { } pausedUntil && pausedUntil > DateTimeOffset.UtcNow &&
                IsAuctionItemInfoLoading)
                return $"Item info paused · retrying at {pausedUntil.ToLocalTime():HH:mm}";
            if (IsAuctionItemInfoLoading)
                return $"Downloading item icons · {_auctionItemInfoFinished:N0} of {_auctionItemInfoTotal:N0} visible items";
            return _auctionItemInfoFailed > 0
                ? $"Item info ready · {_auctionItemInfoTotal - _auctionItemInfoFailed:N0} loaded · {_auctionItemInfoFailed:N0} unavailable"
                : $"Item info ready · {_auctionItemInfoTotal:N0} visible items";
        }
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
            CancelActiveTooltipLoad();
            if (previous is not null && !_visibleAuctionItems.Contains(previous))
                previous.UnloadIcon();
            if (value is not null)
                StartAuctionTooltipLoad(value);
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
            if (value)
                StartAccountingIconLoading();
            else
                StopAccountingIconLoading(true);
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
    public AsyncCommand LoginCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }
    public DelegateCommand CloseSettingsCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public DelegateCommand FindWowCommand { get; }
    public DelegateCommand OnboardingBackCommand { get; }
    public DelegateCommand OnboardingNextCommand { get; }
    public DelegateCommand RefreshAccountingStatisticsCommand { get; }
    public DelegateCommand ShowRealmDataCommand { get; }
    public DelegateCommand ShowAddonVersionsCommand { get; }
    public AsyncCommand CheckAddonUpdatesCommand { get; }
    public DelegateCommand ShowAccountingCommand { get; }
    public DelegateCommand ShowAuctionHouseCommand { get; }
    public DelegateCommand ShowLogsCommand { get; }
    public DelegateCommand RefreshLogsCommand { get; }
    public DelegateCommand RefreshAuctionIndexCommand { get; }
    public DelegateCommand ToggleAuctionFavoritesFilterCommand { get; }
    public DelegateCommand ToggleAdvancedAuctionFiltersCommand { get; }
    public DelegateCommand ClearAdvancedAuctionFiltersCommand { get; }
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
    public AsyncCommand RefreshBackupsCommand { get; }
    public AsyncCommand CreateBackupCommand { get; }
    public AsyncCommand UploadLogCommand { get; }

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
        _suppressSettingsPersistence = true;
        try
        {
            Settings.WindowWidth = width;
            Settings.WindowHeight = height;
            Settings.WindowX = x;
            Settings.WindowY = y;
            Settings.WindowMaximized = maximized;
        }
        finally
        {
            _suppressSettingsPersistence = false;
        }
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
            _suppressSettingsPersistence = true;
            try
            {
                Settings.ResetToDefaults();
            }
            finally
            {
                _suppressSettingsPersistence = false;
            }
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
        if (SelectedTab is >= 0 and <= 4)
            _lastContentTab = SelectedTab;
        IsSettingsOpen = true;
        if (IsLoggedIn)
        {
            await LoadAvailableRealmsAsync();
            await RefreshBackupsAsync();
        }
    }

    private async Task RefreshBackupsAsync()
    {
        BackupStatus = "Checking local and cloud backups…";
        try
        {
            var accounts = _accountingService.FindAccounts(_wowService.Discover(Settings.WowPath));
            var entries = await _backupService.SyncAsync(accounts,
                Settings.UploadAddonData && _apiClient.IsPremium);
            Backups.Clear();
            foreach (var entry in entries)
                Backups.Add(entry);
            SelectedBackup = Backups.FirstOrDefault();
            BackupStatus = _apiClient.IsPremium
                ? $"{Backups.Count} backup(s) available. Cloud synchronization is { (Settings.UploadAddonData ? "enabled" : "disabled") }."
                : $"{Backups.Count} local backup(s). Cloud backups require TSM Premium.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BackupStatus = $"Could not refresh backups: {exception.Message}";
        }
    }

    private async Task CreateBackupsAsync()
    {
        IsBusy = true;
        try
        {
            var accounts = _accountingService.FindAccounts(_wowService.Discover(Settings.WowPath));
            _backupService.CreateAllNow(accounts);
            await RefreshBackupsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BackupStatus = $"Could not create backups: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreSelectedBackupAsync()
    {
        if (SelectedBackup is null)
            return;
        IsBusy = true;
        try
        {
            var accounts = _accountingService.FindAccounts(_wowService.Discover(Settings.WowPath));
            await _backupService.RestoreAsync(SelectedBackup, accounts,
                Settings.UploadAddonData && _apiClient.IsPremium);
            BackupStatus = $"Restored {SelectedBackup.Account} from {SelectedBackup.CreatedText}. Restart WoW before using the restored data.";
            RefreshLocalData();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BackupStatus = $"Could not restore backup: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UploadLogAsync()
    {
        if (!Settings.UploadAddonData)
        {
            LogStatus = "Uploads are disabled in Advanced settings.";
            return;
        }
        var log = _logService.ReadSyncLog();
        if (string.IsNullOrWhiteSpace(log))
        {
            LogStatus = "There are no synchronization logs to upload.";
            return;
        }
        IsBusy = true;
        try
        {
            LogStatus = "Uploading synchronization log to TradeSkillMaster…";
            await _apiClient.UploadLogAsync(log);
            _logService.WriteSync("info", "Synchronization log uploaded to TradeSkillMaster.");
            LogStatus = "Synchronization log uploaded. Application errors remained local.";
            RefreshLogs(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logService.WriteSync("error", $"Synchronization log upload failed: {exception.Message}");
            LogStatus = $"Could not upload synchronization log: {exception.Message}";
            RefreshLogs(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string GetLogExportText() => _logService.ReadCombined(false);

    private void RefreshLogs(bool updateStatus = true)
    {
        LogText = _logService.ReadForDisplay();
        if (updateStatus)
            LogStatus = LogText.Length == 0 ? "No log entries yet." : "Showing the latest sync and application logs.";
    }

    private async Task LoadAvailableRealmsAsync()
    {
        RealmManagementStatus = "Loading available realms…";
        try
        {
            if (!_apiClient.IsAuthenticated)
            {
                var restored = await RestoreSavedLoginAsync();
                if (restored is null || !restored.Success)
                    throw new InvalidOperationException(restored?.Message ?? "The saved login could not be restored.");
            }
            var realms = await _apiClient.GetAvailableRealmsAsync();
            var available = realms.Where(realm => !Settings.ManagedRealms.Any(managed =>
                    string.Equals(managed.GameVersion, realm.GameVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(managed.Id, realm.Id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            AvailableRealms.Clear();
            foreach (var realm in available)
                AvailableRealms.Add(realm);
            SelectedAvailableRealm = AvailableRealms.FirstOrDefault();
            _logService.WriteSync("info",
                $"Realm catalog loaded: {realms.Count} returned, {available.Length} available after filtering.");
            RealmManagementStatus = AvailableRealms.Count == 0
                ? $"No additional realms are available ({realms.Count} returned by TSM)."
                : $"{AvailableRealms.Count} realm(s) available.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RealmManagementStatus = $"Could not load realms: {exception.Message}";
            _logService.WriteSync("error", RealmManagementStatus);
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
        if (SelectedTab != index)
        {
            SelectedAuctionItem = null;
            SelectedAccountingItem = null;
        }
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

    private void CloseSettings()
    {
        IsSettingsOpen = false;
        SelectedTab = _lastContentTab;
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_suppressSettingsPersistence)
            return;
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusDetail = $"Could not save settings: {exception.Message}";
            return;
        }

        switch (args.PropertyName)
        {
            case nameof(Settings.RunAtStartup):
                ApplyStartupRegistration();
                break;
            case nameof(Settings.AutomaticUpdateChecks):
            case nameof(Settings.UpdateCheckIntervalHours):
            case nameof(Settings.ReleaseChannel):
                RestartAutomaticUpdateChecks();
                break;
            case nameof(Settings.WowPath):
                _settingsApplyTimer.Stop();
                _settingsApplyTimer.Start();
                break;
        }
    }

    private void ApplyDeferredSettings(object? sender, EventArgs args)
    {
        _settingsApplyTimer.Stop();
        RefreshLocalData();
    }

    private void ApplyStartupRegistration()
    {
        try
        {
            var location = _startupRegistration.Apply(Settings.RunAtStartup);
            StartupRegistrationStatus = Settings.RunAtStartup
                ? $"Registered in {location}."
                : "Startup launch is disabled.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StartupRegistrationStatus = $"Could not update startup registration: {exception.Message}";
            StatusDetail = StartupRegistrationStatus;
        }
    }

    private async Task SyncDataAsync(
        bool allowReauthentication = true,
        CancellationToken cancellationToken = default)
    {
        StatusDetail = "Checking for updated TSM realm data…";
        _logService.WriteSync("info", "Sync started.");
        try
        {
            var installations = _wowService.Discover(Settings.WowPath);
            var accounts = _accountingService.FindAccounts(installations);
            var result = await _syncService.SyncAsync(installations, accounts,
                Settings.ReleaseChannel, cancellationToken);
            try
            {
                var backups = await _backupService.SyncAsync(accounts,
                    Settings.UploadAddonData && _apiClient.IsPremium, cancellationToken);
                Backups.Clear();
                foreach (var backup in backups)
                    Backups.Add(backup);
                SelectedBackup = Backups.FirstOrDefault();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                BackupStatus = $"Backup synchronization failed: {exception.Message}";
                _logService.WriteSync("warning", BackupStatus);
            }
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
                foreach (var error in result.Errors)
                    _logService.WriteSync("warning", error);
            }
            else if (result.WrittenFiles == 0)
            {
                StatusDetail = "Signed in, but TradeSkillMaster_AppHelper was not found in a configured WoW client";
            }
            else
            {
                var uploadStatus = Settings.UploadAddonData
                    ? $"; uploaded {result.UploadedDataSets} data set(s)"
                    : "; addon uploads disabled";
                StatusDetail = result.DownloadedEntries == 0
                    ? $"Realm data is current ({result.WrittenFiles} AppHelper installation(s)){uploadStatus}"
                    : $"Downloaded {result.DownloadedEntries} data set(s) into {result.WrittenFiles} AppHelper installation(s){uploadStatus}";
            }
            _logService.WriteSync(result.Errors.Count == 0 ? "info" : "warning",
                $"Sync finished: {result.DownloadedEntries} downloaded, {result.WrittenFiles} AppHelper file(s) written, " +
                $"{result.UploadedDataSets} uploaded, {result.Errors.Count} issue(s).");
            if (IsLogsSelected)
                RefreshLogs(false);
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
            _logService.WriteSync("error", LoginError);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StatusDetail = $"Data sync failed: {exception.Message}";
            _logService.WriteSync("error", StatusDetail);
            if (IsLogsSelected)
                RefreshLogs(false);
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
        _accountingFileChangeTimer.Stop();
        _accountingFileChangeTimer.Tick -= AccountingFilesChanged;
        _settingsApplyTimer.Stop();
        _settingsApplyTimer.Tick -= ApplyDeferredSettings;
        Settings.PropertyChanged -= SettingsChanged;
        DisposeAccountingFileWatchers();
        if (_auctionSearchCancellation is not null)
            await _auctionSearchCancellation.CancelAsync();
        if (_auctionIconCancellation is not null)
            await _auctionIconCancellation.CancelAsync();
        if (_activeTooltipCancellation is not null)
            await _activeTooltipCancellation.CancelAsync();
        if (_updateCheckCancellation is not null)
            await _updateCheckCancellation.CancelAsync();
        if (_accountingStatisticsCancellation is not null)
            await _accountingStatisticsCancellation.CancelAsync();
        if (_accountingIconCancellation is not null)
            await _accountingIconCancellation.CancelAsync();
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
        if (_accountingStatisticsRunner is not null)
        {
            try
            {
                await _accountingStatisticsRunner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_accountingIconRunner is not null)
        {
            try
            {
                await _accountingIconRunner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        if (_activeTooltipRunner is not null)
        {
            try
            {
                await _activeTooltipRunner;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _updateCheckCancellation?.Dispose();
        _auctionIconCancellation?.Dispose();
        _activeTooltipCancellation?.Dispose();
        _auctionSearchCancellation?.Dispose();
        _accountingStatisticsCancellation?.Dispose();
        _accountingIconCancellation?.Dispose();
        foreach (var item in AccountingTopItems)
            item.Dispose();
        foreach (var item in AuctionResults)
            item.Dispose();
        _itemIconService.DownloadPaused -= ItemInfoDownloadPaused;
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

        IsInstallingUpdate = true;
        UpdateProgressPercent = 0;
        IsUpdateProgressIndeterminate = true;
        UpdateProgressDetail = string.Empty;
        UpdateStatus = $"Starting TSM {update.Version} update…";
        try
        {
            var progress = new Progress<AppUpdateProgress>(value =>
            {
                UpdateStatus = value.Status;
                UpdateProgressPercent = value.Percentage;
                IsUpdateProgressIndeterminate = value.IsIndeterminate;
                UpdateProgressDetail = FormatUpdateProgress(value);
            });
            await _appUpdates.InstallAsync(update, progress);
            UpdateStatus = $"TSM {update.Version} is ready. Restarting…";
            UpdateProgressPercent = 100;
            IsUpdateProgressIndeterminate = false;
            UpdateProgressDetail = "Download complete";
            await Task.Delay(1200);
            if (Application.Current is App app)
                app.Shutdown();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateStatus = $"Could not install the update: {exception.Message}";
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    private static string FormatUpdateProgress(AppUpdateProgress progress)
    {
        if (progress.BytesReceived <= 0)
            return string.Empty;
        var received = FormatBytes(progress.BytesReceived);
        return progress.TotalBytes is > 0
            ? $"{progress.Percentage:0}% · {received} of {FormatBytes(progress.TotalBytes.Value)}"
            : $"{received} downloaded";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
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

    private void InvalidateAccountingStatistics()
    {
        _accountingStatisticsDirty = true;
        if (!_suppressAccountingRefresh)
            QueueAccountingStatistics();
    }

    private void QueueAccountingStatistics(bool force = false)
    {
        if (_suppressAccountingRefresh || !IsAccountingSelected)
            return;
        _accountingStatisticsCancellation?.Cancel();
        _accountingStatisticsCancellation?.Dispose();
        _accountingStatisticsCancellation = null;
        if (SelectedAccount is null || string.IsNullOrWhiteSpace(SelectedRealm))
        {
            AccountingStatistics = AccountingStatistics.Empty;
            ReplaceAccountingTopItems([], string.Empty);
            AccountingStatisticsStatus = "Select an account and realm to view statistics.";
            return;
        }
        var requestKey = string.Join('\n', SelectedAccount.SavedVariablesPath, SelectedRealm,
            SelectedAccountingPlayer, SelectedAccountingPeriod.Days?.ToString() ?? "all");
        if (!force && !_accountingStatisticsDirty &&
            string.Equals(_loadedAccountingStatisticsKey, requestKey, StringComparison.Ordinal))
            return;
        _accountingStatisticsDirty = false;
        var cancellation = new CancellationTokenSource();
        _accountingStatisticsCancellation = cancellation;
        AccountingStatisticsStatus = "Reading local Accounting history…";
        _accountingStatisticsRunner = LoadAccountingStatisticsAsync(
            SelectedAccount,
            SelectedRealm,
            SelectedAccountingPlayer == "All players" ? null : SelectedAccountingPlayer,
            SelectedAccountingPeriod.Days,
            requestKey,
            cancellation);
    }

    private async Task LoadAccountingStatisticsAsync(TsmAccount account, string realm, string? player,
        int? periodDays, string requestKey, CancellationTokenSource cancellation)
    {
        try
        {
            var statistics = await Task.Run(
                () => _accountingService.BuildStatistics(account, realm, player, periodDays, cancellation.Token),
                cancellation.Token);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested || !ReferenceEquals(_accountingStatisticsCancellation, cancellation))
                    return;
                AccountingStatistics = statistics;
                ReplaceAccountingTopItems(statistics.TopItems, account.GameVersion);
                var previousPlayer = SelectedAccountingPlayer;
                var availablePlayers = new[] { "All players" }.Concat(statistics.Players).ToArray();
                if (!AccountingPlayers.SequenceEqual(availablePlayers, StringComparer.OrdinalIgnoreCase))
                {
                    _suppressAccountingRefresh = true;
                    try
                    {
                        AccountingPlayers = availablePlayers;
                        SelectedAccountingPlayer = availablePlayers.Contains(previousPlayer,
                            StringComparer.OrdinalIgnoreCase) ? previousPlayer : "All players";
                        OnPropertyChanged(nameof(SelectedAccountingPlayerIndex));
                    }
                    finally
                    {
                        _suppressAccountingRefresh = false;
                    }
                }
                _loadedAccountingStatisticsKey = requestKey;
                if (!string.Equals(previousPlayer, SelectedAccountingPlayer, StringComparison.OrdinalIgnoreCase))
                {
                    InvalidateAccountingStatistics();
                    return;
                }
                AccountingStatisticsStatus = statistics.FirstTransaction is not null
                    ? statistics.DateRange
                    : statistics.GoldHistory.Count > 0
                        ? $"Recorded balances: {statistics.GoldHistory[0].Timestamp.ToLocalTime():d MMM yyyy} – {statistics.GoldHistory[^1].Timestamp.ToLocalTime():d MMM yyyy}"
                        : "No Accounting transactions were found for this selection.";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException or
                                           FormatException or OverflowException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_accountingStatisticsCancellation, cancellation))
                    return;
                AccountingStatistics = AccountingStatistics.Empty;
                ReplaceAccountingTopItems([], string.Empty);
                AccountingStatisticsStatus = $"Could not read Accounting history: {exception.Message}";
            });
            GlobalErrorService.Report(exception, "Accounting statistics");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_accountingStatisticsCancellation, cancellation))
                    _accountingStatisticsCancellation = null;
                cancellation.Dispose();
            });
        }
    }

    private void ResetAccountingPlayerSelection()
    {
        if (string.Equals(_selectedAccountingPlayer, "All players", StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(SelectedAccountingPlayer));
            OnPropertyChanged(nameof(SelectedAccountingPlayerIndex));
            return;
        }
        _selectedAccountingPlayer = "All players";
        Settings.SelectedAccountingPlayer = _selectedAccountingPlayer;
        OnPropertyChanged(nameof(SelectedAccountingPlayer));
        OnPropertyChanged(nameof(SelectedAccountingPlayerIndex));
    }

    private void ReplaceAccountingTopItems(IReadOnlyList<AccountingItemSummary> items, string flavor)
    {
        StopAccountingIconLoading(false);
        SelectedAccountingItem = null;
        foreach (var item in AccountingTopItems)
            item.Dispose();
        AccountingTopItems = items.Select(item => new AccountingItemViewModel(item, flavor)).ToArray();
        StartAccountingIconLoading();
    }

    private void StartAccountingIconLoading()
    {
        if (!LoadOnlineItemIcons || AccountingTopItems.Count == 0 || _accountingIconCancellation is not null)
            return;
        var cancellation = new CancellationTokenSource();
        _accountingIconCancellation = cancellation;
        var rows = AccountingTopItems.ToArray();
        _accountingIconRunner = LoadAccountingIconsAsync(rows, cancellation);
    }

    private void StopAccountingIconLoading(bool unloadIcons)
    {
        var cancellation = _accountingIconCancellation;
        _accountingIconCancellation = null;
        cancellation?.Cancel();
        if (unloadIcons)
        {
            foreach (var item in AccountingTopItems)
                item.UnloadIcon();
        }
    }

    private async Task LoadAccountingIconsAsync(AccountingItemViewModel[] rows,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.WhenAll(rows.Select(row => LoadAccountingIconAsync(row, cancellation.Token)));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_accountingIconCancellation, cancellation))
                    _accountingIconCancellation = null;
                cancellation.Dispose();
            });
        }
    }

    private async Task LoadAccountingIconAsync(AccountingItemViewModel row, CancellationToken token)
    {
        var slotAcquired = false;
        try
        {
            await _accountingIconSlots.WaitAsync(token);
            slotAcquired = true;
            var result = await _itemIconService.GetIconAsync(row.ItemString, row.Flavor, token);
            if (result is null)
                return;
            var icon = await Task.Run(() => LoadBitmap(result.Path), token);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !AccountingTopItems.Contains(row))
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
                _accountingIconSlots.Release();
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
        ConfigureAccountingFileWatchers();
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
        InvalidateAccountingStatistics();
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

    private void ConfigureAccountingFileWatchers()
    {
        var roots = _installations.Select(installation => Path.GetFullPath(installation.RootPath))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var watchedRoots = _accountingFileWatchers.Select(watcher => Path.GetFullPath(watcher.Path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.SequenceEqual(watchedRoots, StringComparer.OrdinalIgnoreCase))
            return;
        DisposeAccountingFileWatchers();
        foreach (var root in roots)
        {
            try
            {
                var watcher = new FileSystemWatcher(root, "TradeSkillMaster.lua")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size |
                                   NotifyFilters.CreationTime
                };
                watcher.Changed += AccountingFileChanged;
                watcher.Created += AccountingFileChanged;
                watcher.Deleted += AccountingFileChanged;
                watcher.Renamed += AccountingFileChanged;
                watcher.EnableRaisingEvents = true;
                _accountingFileWatchers.Add(watcher);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void AccountingFileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _accountingFileChangeTimer.Stop();
            _accountingFileChangeTimer.Start();
        });
    }

    private void AccountingFilesChanged(object? sender, EventArgs e)
    {
        _accountingFileChangeTimer.Stop();
        LoadAccountingAccounts();
        LoadRealmDataStatuses();
        InvalidateAccountingStatistics();
        StatusDetail = "Local TradeSkillMaster account data updated";
    }

    private void DisposeAccountingFileWatchers()
    {
        foreach (var watcher in _accountingFileWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= AccountingFileChanged;
            watcher.Created -= AccountingFileChanged;
            watcher.Deleted -= AccountingFileChanged;
            watcher.Renamed -= AccountingFileChanged;
            watcher.Dispose();
        }
        _accountingFileWatchers.Clear();
    }

    private void LoadAccountingAccounts()
    {
        var previous = SelectedAccount?.DisplayName;
        if (string.IsNullOrWhiteSpace(previous))
            previous = Settings.SelectedAccountingAccount;
        var previousPlayer = SelectedAccountingPlayer;
        var accounts = _accountingService.FindAccounts(_installations);
        if (!AccountingAccounts.SequenceEqual(accounts))
        {
            _suppressAccountingRefresh = true;
            try
            {
                AccountingAccounts.Clear();
                foreach (var account in accounts)
                    AccountingAccounts.Add(account);
                SelectedAccount = AccountingAccounts.FirstOrDefault(account => account.DisplayName == previous) ??
                                  AccountingAccounts.FirstOrDefault();
            }
            finally
            {
                _suppressAccountingRefresh = false;
            }
            _selectedAccountingPlayer = string.IsNullOrWhiteSpace(previousPlayer)
                ? "All players"
                : previousPlayer;
            Settings.SelectedAccountingPlayer = _selectedAccountingPlayer;
            OnPropertyChanged(nameof(SelectedAccountingPlayer));
            OnPropertyChanged(nameof(SelectedAccountingPlayerIndex));
        }
        if (AccountingAccounts.Count == 0)
            AccountingStatisticsStatus = "No TradeSkillMaster.lua account data was found.";
    }

    private void LoadAuctionIndex()
    {
        var previousRealm = string.IsNullOrWhiteSpace(SelectedAuctionRealm)
            ? Settings.SelectedAuctionRealm
            : SelectedAuctionRealm;
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

    private void ToggleAdvancedAuctionFilters()
    {
        ShowAdvancedAuctionFilters = !ShowAdvancedAuctionFilters;
    }

    private void ClearAdvancedAuctionFilters()
    {
        SetProperty(ref _selectedAuctionCategory, "All categories", nameof(SelectedAuctionCategory));
        SetProperty(ref _selectedAuctionSubcategory, "All subcategories", nameof(SelectedAuctionSubcategory));
        SetProperty(ref _selectedAuctionQuality, "All qualities", nameof(SelectedAuctionQuality));
        SetProperty(ref _selectedAuctionInventoryType, "All slots", nameof(SelectedAuctionInventoryType));
        AuctionSubcategoryOptions.Clear();
        AuctionSubcategoryOptions.Add("All subcategories");
        AdvancedAuctionFilterChanged();
    }

    private void AdvancedAuctionFilterChanged()
    {
        OnPropertyChanged(nameof(HasAdvancedAuctionFilters));
        OnPropertyChanged(nameof(AdvancedAuctionFilterColor));
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
        var advancedFilter = new AdvancedAuctionFilter(
            WowItemTaxonomy.QualityId(SelectedAuctionQuality),
            WowItemTaxonomy.CategoryId(SelectedAuctionCategory),
            WowItemTaxonomy.SubcategoryId(SelectedAuctionCategory, SelectedAuctionSubcategory),
            WowItemTaxonomy.InventoryType(SelectedAuctionInventoryType));
        var favoriteKeys = new HashSet<string>(_auctionFavoriteKeys, StringComparer.OrdinalIgnoreCase);
        var sortColumn = _auctionSortColumn;
        var sortDescending = _auctionSortDescending;
        _auctionSearchRunner = RunAuctionSearchAsync(index, realm, faction, searchText,
            favoritesOnly, favoriteKeys, advancedFilter, sortColumn,
            sortDescending, debounce, cancellation);
    }

    private async Task RunAuctionSearchAsync(IReadOnlyList<AuctionHouseItemRow> index, string? realm,
        string faction, string searchText,
        bool favoritesOnly, IReadOnlySet<string> favoriteKeys,
        AdvancedAuctionFilter advancedFilter,
        AuctionSortColumn sortColumn, bool sortDescending,
        bool debounce, CancellationTokenSource cancellation)
    {
        try
        {
            if (debounce)
                await Task.Delay(140, cancellation.Token);
            var result = await Task.Run(() =>
            {
                var advancedItemKeys = BuildAdvancedAuctionItemKeys(index, advancedFilter, cancellation.Token);
                return FilterAuctionResults(index, realm, faction, searchText,
                    favoritesOnly, favoriteKeys, advancedItemKeys, sortColumn,
                    sortDescending, cancellation.Token);
            },
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
        IReadOnlySet<string>? advancedItemKeys,
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
            if (advancedItemKeys is not null && !advancedItemKeys.Contains(ClassificationKey(item)))
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

    private IReadOnlySet<string>? BuildAdvancedAuctionItemKeys(IReadOnlyList<AuctionHouseItemRow> index,
        AdvancedAuctionFilter filter, CancellationToken cancellationToken)
    {
        if (!filter.IsActive)
            return null;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flavor in index.Select(item => item.Flavor).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = _itemDatabase.QueryIds(new ItemDatabaseQuery(flavor, Quality: filter.Quality,
                ClassId: filter.ClassId, SubclassId: filter.SubclassId, InventoryType: filter.InventoryType));
            foreach (var id in ids)
                keys.Add(ClassificationKey(flavor, $"i:{id}"));
        }
        return keys;
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

    public void OpenAccountingItem(AccountingItemViewModel item)
    {
        var key = ClassificationKey(item.Flavor, item.ItemString);
        var variants = _auctionIndex.Where(row =>
            string.Equals(ClassificationKey(row.Flavor, row.BaseItemString), key,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        var summary = variants.Length > 0
            ? AggregateAuctionRows(variants)
            : new AuctionHouseItemRow(item.ItemString, item.ItemName, string.Empty, item.Flavor,
                null, null, null, null, DateTimeOffset.MinValue);
        SelectedAuctionItem = new AuctionHouseItemViewModel(summary, variants.Length > 0 ? variants : [summary],
            _auctionFavoriteKeys.Contains(key), _auctionPriceHistory.Get(item.Flavor, item.ItemString));
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
    private sealed record AdvancedAuctionFilter(int? Quality, int? ClassId, int? SubclassId, string InventoryType)
    {
        public bool IsActive => Quality is not null || ClassId is not null || SubclassId is not null ||
                                !string.IsNullOrWhiteSpace(InventoryType);
    }

    private void CancelAuctionIconLoading()
    {
        SuspendAuctionIconLoading();
        _visibleAuctionItems.Clear();
        _auctionItemInfoTotal = 0;
        _auctionItemInfoFinished = 0;
        _auctionItemInfoFailed = 0;
        _auctionItemInfoPausedUntil = null;
        NotifyAuctionItemInfoChanged();
    }

    private void SuspendAuctionIconLoading()
    {
        _auctionIconCancellation?.Cancel();
        _auctionIconCancellation?.Dispose();
        _auctionIconCancellation = null;
        foreach (var cancellation in _auctionIconLoads.Values)
            cancellation.Cancel();
        _auctionIconLoads.Clear();
        _queuedAuctionItemKeys.Clear();
        CancelActiveTooltipLoad();
        foreach (var item in AuctionResults)
            item.UnloadIcon();
    }

    private void ResumeAuctionIconLoading()
    {
        if (!LoadOnlineItemIcons || AuctionResults.Count == 0 || _auctionIconCancellation is not null)
            return;
        _auctionIconCancellation = new CancellationTokenSource();
        foreach (var item in _visibleAuctionItems)
            StartAuctionIconLoad(item);
        if (SelectedAuctionItem is not null)
            StartAuctionTooltipLoad(SelectedAuctionItem);
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
        if (ReferenceEquals(SelectedAuctionItem, item))
            return;
        if (_auctionIconLoads.TryGetValue(item, out var cancellation))
            cancellation.Cancel();
        item.UnloadIcon();
    }

    private void CloseAuctionItemDrawer()
    {
        SelectedAuctionItem = null;
        SelectedAccountingItem = null;
    }

    private void StartAuctionIconLoad(AuctionHouseItemViewModel item)
    {
        if (!LoadOnlineItemIcons || _auctionIconCancellation is null || item.Icon is not null)
            return;
        if (_auctionIconLoads.ContainsKey(item))
            return;
        if (_itemIconService.TryGetCachedIconPath(item.ItemString, item.Flavor, out var cachedPath))
        {
            StartCachedAuctionIconLoad(item, cachedPath);
            return;
        }
        var itemKey = ClassificationKey(item.Flavor, item.ItemString);
        if (!_queuedAuctionItemKeys.Add(itemKey))
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_auctionIconCancellation.Token);
        _auctionIconLoads[item] = cancellation;
        _auctionItemInfoTotal++;
        NotifyAuctionItemInfoChanged();
        _ = LoadAuctionIconAsync(item, itemKey, cancellation);
    }

    private void StartAuctionTooltipLoad(AuctionHouseItemViewModel item)
    {
        if (!LoadOnlineItemIcons || item.HasItemDetails && item.Icon is not null)
            return;
        CancelActiveTooltipLoad();
        var cancellation = _auctionIconCancellation is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(_auctionIconCancellation.Token);
        _activeTooltipCancellation = cancellation;
        _activeTooltipItem = item;
        item.SetItemInfoLoading(true, $"Loading {item.ItemName} item details…");
        _activeTooltipRunner = LoadAuctionTooltipAsync(item, cancellation);
    }

    private void CancelActiveTooltipLoad()
    {
        var cancellation = _activeTooltipCancellation;
        _activeTooltipCancellation = null;
        cancellation?.Cancel();
        _activeTooltipItem?.SetItemInfoLoading(false);
        _activeTooltipItem = null;
    }

    private async Task LoadAuctionTooltipAsync(AuctionHouseItemViewModel row,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var slotAcquired = false;
        var failed = false;
        try
        {
            await _tooltipSlot.WaitAsync(token);
            slotAcquired = true;
            var result = await _itemIconService.GetAsync(row.ItemString, row.Flavor, token);
            if (result is null)
            {
                failed = true;
                return;
            }
            var icon = await Task.Run(() => LoadBitmap(result.Path), token);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !ReferenceEquals(SelectedAuctionItem, row))
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
            failed = true;
        }
        finally
        {
            if (slotAcquired)
                _tooltipSlot.Release();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(_activeTooltipCancellation, cancellation))
                {
                    _activeTooltipCancellation = null;
                    _activeTooltipItem = null;
                    row.SetItemInfoLoading(false, failed
                        ? "Item details are temporarily unavailable."
                        : "Item details loaded.");
                }
                cancellation.Dispose();
            });
        }
    }

    private void StartCachedAuctionIconLoad(AuctionHouseItemViewModel item, string path)
    {
        if (_auctionIconCancellation is null || _auctionIconLoads.ContainsKey(item))
            return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_auctionIconCancellation.Token);
        _auctionIconLoads[item] = cancellation;
        _ = LoadCachedAuctionIconAsync(item, path, cancellation);
    }

    private async Task LoadCachedAuctionIconAsync(AuctionHouseItemViewModel row, string path,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var slotAcquired = false;
        try
        {
            await _cachedAuctionIconSlots.WaitAsync(token);
            slotAcquired = true;
            var summary = await Task.Run(() =>
                _itemIconService.GetBundledItemSummary(row.ItemString, row.Flavor), token);
            var icon = await Task.Run(() => LoadBitmap(path), token);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !AuctionResults.Contains(row))
                        return;
                    row.SetPreviewIcon(icon, summary);
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
        catch (Exception exception) when (exception is IOException or ArgumentException or
                                           UnauthorizedAccessException or ObjectDisposedException)
        {
        }
        finally
        {
            if (slotAcquired)
                _cachedAuctionIconSlots.Release();
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

    private async Task LoadAuctionIconAsync(AuctionHouseItemViewModel row, string itemKey,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var slotAcquired = false;
        var outcome = 0;
        try
        {
            await _auctionIconSlots.WaitAsync(token);
            slotAcquired = true;
            var result = await _itemIconService.GetIconAsync(row.ItemString, row.Flavor, token);
            if (result is null)
            {
                outcome = 2;
                return;
            }
            var icon = await Task.Run(() => LoadBitmap(result.Path), token);
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested || !AuctionResults.Contains(row))
                        return;
                    row.SetPreviewIcon(result, icon);
                    icon = null;
                    outcome = 1;
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
            outcome = 2;
        }
        finally
        {
            if (slotAcquired)
                _auctionIconSlots.Release();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_auctionIconLoads.TryGetValue(row, out var current) && ReferenceEquals(current, cancellation))
                {
                    _auctionIconLoads.Remove(row);
                    _queuedAuctionItemKeys.Remove(itemKey);
                    if (outcome == 0)
                        _auctionItemInfoTotal = Math.Max(_auctionItemInfoFinished, _auctionItemInfoTotal - 1);
                    else
                    {
                        _auctionItemInfoFinished++;
                        if (outcome == 2)
                            _auctionItemInfoFailed++;
                    }
                    NotifyAuctionItemInfoChanged();
                }
                var shouldRestart = cancellation.IsCancellationRequested &&
                                    (_visibleAuctionItems.Contains(row) || ReferenceEquals(SelectedAuctionItem, row));
                cancellation.Dispose();
                if (shouldRestart)
                    StartAuctionIconLoad(row);
            });
        }
    }

    private static Bitmap LoadBitmap(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        memory.Position = 0;
        return new Bitmap(memory);
    }

    private void ItemInfoDownloadPaused(DateTimeOffset resumeAt, bool tooltip)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_auctionItemInfoPausedUntil is null || resumeAt > _auctionItemInfoPausedUntil)
                _auctionItemInfoPausedUntil = resumeAt;
            if (tooltip && _activeTooltipItem is { } selected)
                selected.SetItemInfoLoading(true,
                    $"Item info paused · retrying at {resumeAt.ToLocalTime():HH:mm}");
            NotifyAuctionItemInfoChanged();
        });
    }

    private void NotifyAuctionItemInfoChanged()
    {
        OnPropertyChanged(nameof(IsAuctionItemInfoStatusVisible));
        OnPropertyChanged(nameof(IsAuctionItemInfoLoading));
        OnPropertyChanged(nameof(AuctionItemInfoProgress));
        OnPropertyChanged(nameof(AuctionItemInfoStatus));
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
        if (string.IsNullOrWhiteSpace(previous))
            previous = Settings.SelectedAccountingRealm;
        IReadOnlyList<string> realms = [];
        if (SelectedAccount is not null)
        {
            try
            {
                realms = _accountingService.FindRealms(SelectedAccount);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or LuaParseException)
            {
                AccountingStatisticsStatus = $"Could not read SavedVariables: {exception.Message}";
            }
        }
        if (AccountingRealms.SequenceEqual(realms, StringComparer.OrdinalIgnoreCase))
            return;
        AccountingRealms.Clear();
        foreach (var realm in realms)
            AccountingRealms.Add(realm);
        SelectedRealm = AccountingRealms.FirstOrDefault(realm => realm == previous) ??
                        AccountingRealms.FirstOrDefault();
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
