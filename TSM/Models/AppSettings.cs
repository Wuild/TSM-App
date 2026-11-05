using TSM.Infrastructure;

namespace TSM.Models;

public sealed class AppSettings : ObservableObject
{
    private string _email = string.Empty;
    private string _wowPath = string.Empty;
    private string _releaseChannel = "Release";
    private bool _runAtStartup;
    private bool _startMinimized;
    private bool _minimizeToTray = true;
    private bool _confirmExit = true;
    private bool _hardwareAcceleration = true;
    private bool _realmDataNotification = true;
    private bool _addonNotification = true;
    private bool _newsNotification = true;
    private bool _appUpdateNotification = true;
    private bool _automaticUpdateChecks = true;
    private bool _automaticallyInstallUpdates;
    private int _updateCheckIntervalHours = 6;
    private bool _rememberLogin = true;
    private string _credentialKey = string.Empty;
    private int _lastNotifiedNewsId;
    private int _lastNotifiedAppVersion;
    private string _lastNotifiedGitHubVersion = string.Empty;
    private DateTimeOffset? _lastSuccessfulSyncUtc;
    private bool _loadOnlineItemIcons = true;
    private bool _uploadAddonData = true;
    private string _uploadSystemId = Guid.NewGuid().ToString("N");
    private bool _hasCompletedOnboarding;
    private string _auctionSortColumn = "Item";
    private bool _auctionSortDescending;
    private string _selectedAccountingAccount = string.Empty;
    private string _selectedAccountingRealm = string.Empty;
    private string _selectedAccountingPlayer = "All players";
    internal const string AllAuctionMarkets = "All markets";
    private string _selectedAuctionRealm = AllAuctionMarkets;
    private string _selectedAuctionFaction = "Both";
    private List<ManagedRealmSelection> _managedRealms = [];
    private List<ManagedRealmSelection> _hiddenLocalRealms = [];
    private List<AuctionFavorite> _auctionFavorites = [];
    private double _windowWidth = 1200;
    private double _windowHeight = 780;
    private int? _windowX;
    private int? _windowY;
    private bool _windowMaximized;

    public string Email { get => _email; set => SetProperty(ref _email, value); }
    public string WowPath { get => _wowPath; set => SetProperty(ref _wowPath, value); }
    public string ReleaseChannel { get => _releaseChannel; set => SetProperty(ref _releaseChannel, value); }
    public bool RunAtStartup { get => _runAtStartup; set => SetProperty(ref _runAtStartup, value); }
    public bool StartMinimized { get => _startMinimized; set => SetProperty(ref _startMinimized, value); }
    public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
    public bool ConfirmExit { get => _confirmExit; set => SetProperty(ref _confirmExit, value); }
    public bool HardwareAcceleration { get => _hardwareAcceleration; set => SetProperty(ref _hardwareAcceleration, value); }
    public bool RealmDataNotification { get => _realmDataNotification; set => SetProperty(ref _realmDataNotification, value); }
    public bool AddonNotification { get => _addonNotification; set => SetProperty(ref _addonNotification, value); }
    public bool NewsNotification { get => _newsNotification; set => SetProperty(ref _newsNotification, value); }
    public bool AppUpdateNotification { get => _appUpdateNotification; set => SetProperty(ref _appUpdateNotification, value); }
    public bool AutomaticUpdateChecks { get => _automaticUpdateChecks; set => SetProperty(ref _automaticUpdateChecks, value); }
    public bool AutomaticallyInstallUpdates { get => _automaticallyInstallUpdates; set => SetProperty(ref _automaticallyInstallUpdates, value); }
    public int UpdateCheckIntervalHours
    {
        get => _updateCheckIntervalHours;
        set => SetProperty(ref _updateCheckIntervalHours, Math.Clamp(value, 1, 168));
    }
    public bool RememberLogin { get => _rememberLogin; set => SetProperty(ref _rememberLogin, value); }
    public string CredentialKey { get => _credentialKey; set => SetProperty(ref _credentialKey, value); }
    public int LastNotifiedNewsId { get => _lastNotifiedNewsId; set => SetProperty(ref _lastNotifiedNewsId, value); }
    public int LastNotifiedAppVersion { get => _lastNotifiedAppVersion; set => SetProperty(ref _lastNotifiedAppVersion, value); }
    public string LastNotifiedGitHubVersion { get => _lastNotifiedGitHubVersion; set => SetProperty(ref _lastNotifiedGitHubVersion, value ?? string.Empty); }
    public DateTimeOffset? LastSuccessfulSyncUtc { get => _lastSuccessfulSyncUtc; set => SetProperty(ref _lastSuccessfulSyncUtc, value); }
    public bool LoadOnlineItemIcons { get => _loadOnlineItemIcons; set => SetProperty(ref _loadOnlineItemIcons, value); }
    public bool UploadAddonData { get => _uploadAddonData; set => SetProperty(ref _uploadAddonData, value); }
    public string UploadSystemId
    {
        get => _uploadSystemId;
        set => SetProperty(ref _uploadSystemId,
            string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value);
    }
    public bool HasCompletedOnboarding { get => _hasCompletedOnboarding; set => SetProperty(ref _hasCompletedOnboarding, value); }
    public string AuctionSortColumn
    {
        get => _auctionSortColumn;
        set => SetProperty(ref _auctionSortColumn, string.IsNullOrWhiteSpace(value) ? "Item" : value);
    }
    public bool AuctionSortDescending
    {
        get => _auctionSortDescending;
        set => SetProperty(ref _auctionSortDescending, value);
    }
    public string SelectedAccountingAccount
    {
        get => _selectedAccountingAccount;
        set => SetProperty(ref _selectedAccountingAccount, value ?? string.Empty);
    }
    public string SelectedAccountingRealm
    {
        get => _selectedAccountingRealm;
        set => SetProperty(ref _selectedAccountingRealm, value ?? string.Empty);
    }
    public string SelectedAccountingPlayer
    {
        get => _selectedAccountingPlayer;
        set => SetProperty(ref _selectedAccountingPlayer,
            string.IsNullOrWhiteSpace(value) ? "All players" : value);
    }
    public string SelectedAuctionRealm
    {
        get => _selectedAuctionRealm;
        set => SetProperty(ref _selectedAuctionRealm,
            string.IsNullOrWhiteSpace(value) ? AllAuctionMarkets : value);
    }
    public string SelectedAuctionFaction
    {
        get => _selectedAuctionFaction;
        set => SetProperty(ref _selectedAuctionFaction,
            value is "Alliance" or "Horde" or "Both" ? value : "Both");
    }
    public List<ManagedRealmSelection> ManagedRealms { get => _managedRealms; set => SetProperty(ref _managedRealms, value ?? []); }
    public List<ManagedRealmSelection> HiddenLocalRealms { get => _hiddenLocalRealms; set => SetProperty(ref _hiddenLocalRealms, value ?? []); }
    public List<AuctionFavorite> AuctionFavorites { get => _auctionFavorites; set => SetProperty(ref _auctionFavorites, value ?? []); }
    public double WindowWidth
    {
        get => _windowWidth;
        set => SetProperty(ref _windowWidth, double.IsFinite(value) ? Math.Clamp(value, 1000, 10000) : 1200);
    }
    public double WindowHeight
    {
        get => _windowHeight;
        set => SetProperty(ref _windowHeight, double.IsFinite(value) ? Math.Clamp(value, 680, 10000) : 780);
    }
    public int? WindowX { get => _windowX; set => SetProperty(ref _windowX, value); }
    public int? WindowY { get => _windowY; set => SetProperty(ref _windowY, value); }
    public bool WindowMaximized { get => _windowMaximized; set => SetProperty(ref _windowMaximized, value); }

    public void ResetToDefaults()
    {
        var defaults = new AppSettings();
        Email = defaults.Email;
        WowPath = defaults.WowPath;
        ReleaseChannel = defaults.ReleaseChannel;
        RunAtStartup = defaults.RunAtStartup;
        StartMinimized = defaults.StartMinimized;
        MinimizeToTray = defaults.MinimizeToTray;
        ConfirmExit = defaults.ConfirmExit;
        HardwareAcceleration = defaults.HardwareAcceleration;
        RealmDataNotification = defaults.RealmDataNotification;
        AddonNotification = defaults.AddonNotification;
        NewsNotification = defaults.NewsNotification;
        AppUpdateNotification = defaults.AppUpdateNotification;
        AutomaticUpdateChecks = defaults.AutomaticUpdateChecks;
        AutomaticallyInstallUpdates = defaults.AutomaticallyInstallUpdates;
        UpdateCheckIntervalHours = defaults.UpdateCheckIntervalHours;
        RememberLogin = defaults.RememberLogin;
        CredentialKey = defaults.CredentialKey;
        LastNotifiedNewsId = defaults.LastNotifiedNewsId;
        LastNotifiedAppVersion = defaults.LastNotifiedAppVersion;
        LastNotifiedGitHubVersion = defaults.LastNotifiedGitHubVersion;
        LastSuccessfulSyncUtc = defaults.LastSuccessfulSyncUtc;
        LoadOnlineItemIcons = defaults.LoadOnlineItemIcons;
        UploadAddonData = defaults.UploadAddonData;
        UploadSystemId = defaults.UploadSystemId;
        HasCompletedOnboarding = defaults.HasCompletedOnboarding;
        AuctionSortColumn = defaults.AuctionSortColumn;
        AuctionSortDescending = defaults.AuctionSortDescending;
        SelectedAccountingAccount = defaults.SelectedAccountingAccount;
        SelectedAccountingRealm = defaults.SelectedAccountingRealm;
        SelectedAccountingPlayer = defaults.SelectedAccountingPlayer;
        SelectedAuctionRealm = defaults.SelectedAuctionRealm;
        SelectedAuctionFaction = defaults.SelectedAuctionFaction;
        ManagedRealms = [];
        HiddenLocalRealms = [];
        AuctionFavorites = [];
        WindowWidth = defaults.WindowWidth;
        WindowHeight = defaults.WindowHeight;
        WindowX = defaults.WindowX;
        WindowY = defaults.WindowY;
        WindowMaximized = defaults.WindowMaximized;
    }
}

public sealed record AuctionFavorite(string Flavor, string ItemString);

public sealed record ManagedRealmSelection(string GameVersion, string Region, string Name, string Id)
{
    public string DisplayName => ForeverMarket.IsForever(GameVersion) && ForeverMarket.TryParse(Name, out var market)
        ? $"Forever · {market.DisplayName(ForeverMarket.RegionCode(Region))}"
        : string.IsNullOrWhiteSpace(Region) ? $"{Name} · {GameVersion}" : $"{Region.ToUpperInvariant()}-{Name} · {GameVersion}";
}
