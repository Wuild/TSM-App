using System.Net;
using System.Text;
using System.Text.Json;
using ktsu.CredentialCache.Storage;
using TSM.Models;
using TSM.Services;
using TSM.ViewModels;

var tests = new (string Name, Func<Task> Execute)[]
{
    ("parses SavedVariables data without executing Lua", Run(ParseSavedVariables)),
    ("rejects executable Lua expressions", Run(RejectExecutableLua)),
    ("discovers realms and reads Accounting statistics", Run(ReadAccountingStatistics)),
    ("indexes AppData metadata and derives local realm status", Run(IndexAppData)),
    ("persists onboarding and manually managed realm settings", Run(PersistManagedRealms)),
    ("registers Linux startup using the desktop application id", Run(RegisterLinuxStartup)),
    ("restores application settings to their defaults", Run(ResetApplicationSettings)),
    ("round-trips remembered login through a credential store", Run(StoreRememberedLogin)),
    ("redirects second launches to the primary app instance", EnforceSingleInstance),
    ("blocks legacy authentication when its transport is disabled", LegacyAuthenticationCanBeDisabled),
    ("authenticates and differentially syncs AppData atomically", AuthenticateAndSyncAppData),
    ("uploads current TSM SavedVariables data by endpoint", UploadTsmData),
    ("creates synchronizes and restores Premium backups", PremiumBackups),
    ("records and combines sync and application logs", Run(CombineApplicationLogs)),
    ("syncs faction-qualified anniversary realm data", SyncFactionQualifiedRealm),
    ("discovers PTR client variants", Run(DiscoverPtrVariants)),
    ("detects newly installed clients and local accounts", Run(DetectLocalInstallationChanges)),
    ("safely updates, reinstalls, and removes addons", UpdateInstalledAddon),
    ("runs automatic sync callbacks serially on a 30 minute cadence", AutomaticSyncCadence),
    ("selects the native GitHub update package", SelectNativeUpdatePackage),
    ("decodes and indexes local AuctionDB prices", Run(IndexAuctionHouseSnapshot)),
    ("persists favorite item price history without duplicate snapshots", Run(PersistAuctionPriceHistory)),
    ("queries the bundled SQLite item catalog", Run(QueryBundledItemDatabase)),
    ("caches Wowhead item details and icons by flavor", CacheWowheadItemDetails),
    ("backs off and retries throttled item detail downloads", RetryThrottledItemDetails),
    ("maps Wowhead item categories and dependent subcategories", Run(MapWowheadItemTaxonomy)),
    ("formats auction currency denominations", Run(FormatAuctionCurrency)),
    ("identifies factions and preserves merged price tooltips", Run(FormatFactionPriceSnapshots))
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Execute();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}");
        Console.Error.WriteLine(failures[^1]);
    }
}

return failures.Count == 0 ? 0 : 1;

static Func<Task> Run(Action action) => () =>
{
    action();
    return Task.CompletedTask;
};

static void ParseSavedVariables()
{
    var parser = new LuaSavedVariablesParser();
    var document = parser.Parse(SavedVariablesFixture());
    Assert(document.TryGetTable("TradeSkillMasterDB", out var database), "TSM database missing");
    Assert(database.TryGetTable("_scopeKeys", out var scopeKeys), "scope keys missing");
    Assert(scopeKeys.TryGetTable("realm", out var realms), "realm scope missing");
    Assert(realms.TryGetValue(1, out var realm) && Equals(realm, "Draenor"), "numeric Lua table key failed");
    Assert(database.TryGetValue("enabled", out var enabled) && Equals(enabled, true), "boolean value failed");
    Assert(!database.TryGetValue("unused", out _), "nil should not create a table entry");
}

static void RejectExecutableLua()
{
    var parser = new LuaSavedVariablesParser();
    try
    {
        parser.Parse("Unsafe = os.execute('never run')");
    }
    catch (LuaParseException)
    {
        return;
    }

    throw new InvalidOperationException("Executable expression was accepted");
}

static void ResetApplicationSettings()
{
    var settings = new AppSettings
    {
        Email = "user@example.com",
        WowPath = "/games/wow",
        RunAtStartup = true,
        MinimizeToTray = false,
        HardwareAcceleration = false,
        PreferNativeWayland = false,
        HasCompletedOnboarding = true,
        WindowWidth = 1777,
        WindowHeight = 999,
        WindowMaximized = true,
        AuctionFavorites = [new AuctionFavorite("Retail", "i:19019")],
        SelectedAccountingAccount = "TBC · Account",
        SelectedAccountingRealm = "Thunderstrike",
        SelectedAccountingPlayer = "Bankalt",
        SelectedAuctionRealm = "Anniversary · Thunderstrike",
        SelectedAuctionFaction = "Alliance",
        ManagedRealms = [new ManagedRealmSelection("retail", "EU", "Draenor", "1")]
    };
    settings.ResetToDefaults();
    var defaults = new AppSettings();
    Assert(settings.Email == defaults.Email && settings.WowPath == defaults.WowPath &&
           settings.RunAtStartup == defaults.RunAtStartup && settings.MinimizeToTray == defaults.MinimizeToTray &&
           settings.HardwareAcceleration == defaults.HardwareAcceleration &&
           settings.PreferNativeWayland == defaults.PreferNativeWayland &&
           settings.HasCompletedOnboarding == defaults.HasCompletedOnboarding &&
           settings.WindowWidth == defaults.WindowWidth && settings.WindowHeight == defaults.WindowHeight &&
           settings.WindowMaximized == defaults.WindowMaximized && settings.AuctionFavorites.Count == 0 &&
           settings.SelectedAccountingAccount == defaults.SelectedAccountingAccount &&
           settings.SelectedAccountingRealm == defaults.SelectedAccountingRealm &&
           settings.SelectedAccountingPlayer == defaults.SelectedAccountingPlayer &&
           settings.SelectedAuctionRealm == defaults.SelectedAuctionRealm &&
           settings.SelectedAuctionFaction == defaults.SelectedAuctionFaction &&
           settings.ManagedRealms.Count == 0,
        "one or more application preferences survived a full reset");
}

static void RegisterLinuxStartup()
{
    if (!OperatingSystem.IsLinux())
        return;
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-autostart-{Guid.NewGuid():N}");
    var previousConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    try
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", temporaryRoot);
        var service = new StartupRegistrationService();
        var registration = service.Apply(true);
        Assert(Path.GetFileName(registration) == "com.wuild.TSMDesktop.desktop" && File.Exists(registration),
            "the canonical Linux autostart entry was not created");
        var contents = File.ReadAllText(registration);
        Assert(contents.Contains("X-GNOME-Autostart-enabled=true", StringComparison.Ordinal) &&
               contents.Contains("Exec=", StringComparison.Ordinal),
            "the Linux autostart entry was incomplete");
        service.Apply(false);
        Assert(!File.Exists(registration), "the Linux autostart entry was not removed");
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfigHome);
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }
}

static void ReadAccountingStatistics()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-parser-tests-{Guid.NewGuid():N}");
    var accountRoot = Path.Combine(temporaryRoot, "_retail_", "WTF", "Account", "ACCOUNT#1");
    var savedVariables = Path.Combine(accountRoot, "SavedVariables");
    var addons = Path.Combine(temporaryRoot, "_retail_", "Interface", "AddOns");
    Directory.CreateDirectory(savedVariables);
    Directory.CreateDirectory(addons);
    var sourcePath = Path.Combine(savedVariables, "TradeSkillMaster.lua");
    File.WriteAllText(sourcePath, SavedVariablesFixture());

    try
    {
        var service = new AccountingService(new LuaSavedVariablesParser());
        var installation = new WowInstallation("Retail", Path.Combine(temporaryRoot, "_retail_"), addons,
            Path.Combine(temporaryRoot, "_retail_", "WTF", "Account"));
        var account = service.FindAccounts([installation]).Single();
        Assert(service.FindRealms(account).SequenceEqual(["Draenor"]), "realm discovery failed");

        var statistics = service.BuildStatistics(account, "Draenor", null, null);
        Assert(statistics.Players.SequenceEqual(["Test"]), "Accounting player discovery failed");
        Assert(statistics.TotalEarned == 5000, "sale total did not use per-item price and quantity");
        Assert(statistics.TotalSpent == 0 && statistics.TotalProfit == 5000, "Accounting totals were incorrect");
        Assert(statistics.TopItems.Single().SoldQuantity == 1, "sale quantity was incorrect");
        Assert(statistics.TopEarnedItem == "Copper, Bar", "top sale item lookup failed");
        Assert(statistics.GoldHistory.Count == 2 && statistics.GoldHistory[^1].BalanceCopper == 25000,
            "recorded player gold history was not loaded");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static void IndexAppData()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-appdata-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);
    var tsmPath = Path.Combine(temporaryRoot, "TradeSkillMaster.lua");
    var helperPath = Path.Combine(temporaryRoot, "TradeSkillMaster_AppHelper.lua");
    var appDataPath = Path.Combine(temporaryRoot, "AppData.lua");
    File.WriteAllText(tsmPath, SavedVariablesFixture());
    File.WriteAllText(helperPath, "TradeSkillMaster_AppHelperDB = { [\"region\"] = \"us-retail\", }");
    File.WriteAllText(appDataPath,
        "select(2, ...).LoadData(\"AUCTIONDB_NON_COMMODITY_DATA\",\"Draenor-Alliance\",[[return {}]]) --<AUCTIONDB_NON_COMMODITY_DATA,Draenor-Alliance,1700000000>\n" +
        "select(2, ...).LoadData(\"SHOPPING_SEARCHES\",\"Draenor-Alliance\",[[return {}]]) --<SHOPPING_SEARCHES,Draenor-Alliance,1700000100>\n" +
        "ignored() --<UNSUPPORTED,Draenor,1700000200>\n");

    try
    {
        var catalog = new AppDataCatalogParser().ParseFile(appDataPath);
        Assert(catalog.Entries.Count == 2, "AppData metadata filtering failed");
        Assert(catalog.LastUpdate("SHOPPING_SEARCHES", "Draenor-Alliance") == 1700000100, "AppData timestamp failed");

        var account = new TsmAccount("ACCOUNT#1 · Retail", "ACCOUNT#1", "Retail", tsmPath, helperPath, appDataPath);
        var service = new RealmDataService(new LuaSavedVariablesParser(), new AppDataCatalogParser());
        var status = service.GetRealmStatuses([account]).Single();
        Assert(status.RegionRealm == "US-Draenor", "AppHelper region normalization failed");
        Assert(status.AuctionDb == "Available" && status.GreatDeals == "Available",
            "faction-qualified AppData was not matched to the local realm");
        Assert(status.GameVersion == "Retail", "realm row did not retain its WoW flavor");
        Assert(status.LastUpdated != "Never", "local update timestamp failed");

        var addonsPath = Path.Combine(temporaryRoot, "Interface", "AddOns");
        var installedHelper = Path.Combine(addonsPath, "TradeSkillMaster_AppHelper");
        Directory.CreateDirectory(installedHelper);
        File.Copy(appDataPath, Path.Combine(installedHelper, "AppData.lua"));
        var installation = new WowInstallation("Retail", temporaryRoot, addonsPath,
            Path.Combine(temporaryRoot, "WTF", "Account"));
        var unmanagedStatus = service.GetRealmStatuses([], [installation]).Single();
        Assert(unmanagedStatus.RegionRealm == "Draenor" && unmanagedStatus.AuctionDb == "Available",
            "AppData-only managed realm was not discovered");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static void PersistManagedRealms()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-managed-realms-{Guid.NewGuid():N}");
    try
    {
        var store = new SettingsStore(new AppPaths(temporaryRoot));
        var settings = new AppSettings
        {
            HasCompletedOnboarding = true,
            RunAtStartup = true,
            MinimizeToTray = false,
            HardwareAcceleration = false,
            PreferNativeWayland = false,
            AuctionSortColumn = "MinBuyout",
            AuctionSortDescending = true,
            AuctionFavorites = [new AuctionFavorite("Retail", "i:19019")],
            SelectedAccountingAccount = "TBC · Account",
            SelectedAccountingRealm = "Thunderstrike",
            SelectedAccountingPlayer = "Bankalt",
            SelectedAuctionRealm = "Anniversary · Thunderstrike",
            SelectedAuctionFaction = "Horde",
            WindowWidth = 1440,
            WindowHeight = 900,
            WindowX = 120,
            WindowY = 80,
            WindowMaximized = true,
            ManagedRealms = [new ManagedRealmSelection("retail", "US", "Aegwynn", "123")],
            HiddenLocalRealms = [new ManagedRealmSelection("classic", "EU", "Firemaw", "456")]
        };
        store.Save(settings);
        var loaded = store.Load();
        Assert(loaded.ManagedRealms.Single() == settings.ManagedRealms.Single(),
            "managed realm selection did not survive settings reload");
        Assert(loaded.HiddenLocalRealms.Single() == settings.HiddenLocalRealms.Single(),
            "removed local realm tombstone did not survive settings reload");
        Assert(loaded.HasCompletedOnboarding && loaded.RunAtStartup && !loaded.MinimizeToTray,
            "onboarding desktop preferences did not survive settings reload");
        Assert(!loaded.HardwareAcceleration, "rendering preference did not survive settings reload");
        Assert(!loaded.PreferNativeWayland, "Wayland preference did not survive settings reload");
        Assert(loaded.AuctionSortColumn == "MinBuyout" && loaded.AuctionSortDescending,
            "auction sorting preference did not survive settings reload");
        Assert(loaded.AuctionFavorites.Single() == settings.AuctionFavorites.Single(),
            "auction favorite did not survive settings reload");
        Assert(loaded.SelectedAccountingAccount == settings.SelectedAccountingAccount &&
               loaded.SelectedAccountingRealm == settings.SelectedAccountingRealm &&
               loaded.SelectedAccountingPlayer == settings.SelectedAccountingPlayer,
            "accounting selections did not survive settings reload");
        Assert(loaded.SelectedAuctionRealm == settings.SelectedAuctionRealm &&
               loaded.SelectedAuctionFaction == settings.SelectedAuctionFaction,
            "auction selections did not survive settings reload");
        Assert(loaded.WindowWidth == 1440 && loaded.WindowHeight == 900 && loaded.WindowX == 120 &&
               loaded.WindowY == 80 && loaded.WindowMaximized,
            "window placement did not survive settings reload");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }
}

static async Task CacheWowheadItemDetails()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-wowhead-cache-{Guid.NewGuid():N}");
    try
    {
        var handler = new WowheadCacheHandler();
        using var client = new HttpClient(handler);
        using var service = new ItemIconService(new AppPaths(temporaryRoot), client);

        var preview = await service.GetIconAsync("i:3041", "Classic Era");
        Assert(preview is not null && handler.Requests.Count == 1 &&
               handler.Requests[0].Host == "render.worldofwarcraft.com",
            "bundled icon mapping did not bypass the tooltip endpoint");
        var previewResult = preview!;
        Assert(service.TryGetCachedIconPath("i:3041", "Classic Era", out var cachedPreviewPath) &&
               cachedPreviewPath == previewResult.Path,
            "an existing icon was not resolved from disk without entering the download queue");

        var first = await service.GetAsync("i:19019", "Classic Era");
        var cached = await service.GetAsync("i:19019", "Classic Era");

        Assert(first is not null && cached is not null, "Wowhead item result was missing");
        var firstResult = first!;
        var cachedResult = cached!;
        Assert(firstResult.Path == cachedResult.Path && File.Exists(firstResult.Path), "cached icon path was not reused");
        Assert(firstResult.Name == "Thunderfury" && firstResult.Quality == 5, "cached item metadata was incomplete");
        Assert(firstResult.Classification is { QualityName: "Legendary", Category: "Weapon", Subcategory: "One-Handed Sword" },
            "Wowhead item class and subclass metadata were not classified");
        Assert(firstResult.TooltipText.Contains("Binds when picked up", StringComparison.Ordinal),
            "HTML tooltip was not converted to native text");
        Assert(firstResult.TooltipLines.All(line => !line.Text.Contains("Thunderfury", StringComparison.Ordinal) &&
                                                    !line.Text.Contains("Phase", StringComparison.OrdinalIgnoreCase)),
            "duplicated item name or phase metadata leaked into item details");
        Assert(firstResult.TooltipLines.Any(line => line.Text == "Binds when picked up") &&
               firstResult.TooltipLines.Any(line => line.Text == "Main Hand · Sword"),
            "Wowhead equipment table fields were flattened together");
        Assert(firstResult.TooltipLines.Any(line => line.HasSocket && line.SocketColor == "#2878D0") &&
               firstResult.TooltipLines.Any(line => line.Text.StartsWith("Equip:", StringComparison.Ordinal) &&
                                                    line.Color == "#1EFF00") &&
               firstResult.TooltipLines.Any(line => line.IsMoney && line.Text == "Sell Price:" &&
                                                    line.Money.Gold == "1,234g " &&
                                                    line.Money.Silver == "08s " &&
                                                    line.Money.Copper == "01c"),
            "structured Wowhead colors, socket icon, or sell-price denominations were lost");
        Assert(firstResult.TooltipLines.All(line =>
                !line.Text.StartsWith("Dropped by", StringComparison.OrdinalIgnoreCase) &&
                !line.Text.StartsWith("Drop chance", StringComparison.OrdinalIgnoreCase) &&
                !line.Text.StartsWith("Max Stack:", StringComparison.OrdinalIgnoreCase) &&
                !line.Text.StartsWith("Full Stack Sell Price", StringComparison.OrdinalIgnoreCase)),
            "unreliable maximum-stack metadata leaked into item details");
        Assert(handler.Requests.Count == 3, "a cached lookup unexpectedly contacted Wowhead again");
        Assert(handler.Requests[1].AbsolutePath.Contains("/classic/tooltip/item/19019", StringComparison.Ordinal),
            "Classic Era used the wrong Wowhead flavor endpoint");
        Assert(firstResult.Path.Contains(Path.Combine("ItemIcons", "classic"), StringComparison.Ordinal),
            "item cache was not isolated by flavor");

        var retail = await service.GetAsync("i:19019", "Retail");
        Assert(retail is not null && retail.Path != firstResult.Path,
            "Retail and Classic item cache entries collided");
        Assert(handler.Requests.Count == 5 && handler.Requests[3].AbsolutePath == "/tooltip/item/19019",
            "Retail did not use its own Wowhead endpoint and cache namespace");

        var anniversary = await service.GetAsync("i:30038", "Anniversary");
        Assert(anniversary is not null &&
               anniversary.Path.Contains(Path.Combine("ItemIcons", "tbc"), StringComparison.Ordinal),
            "Anniversary item cache did not use its TBC namespace");
        Assert(handler.Requests.Count == 6 && handler.Requests[5].Host == "render.worldofwarcraft.com" &&
               anniversary!.Name == "Belt of Blasting" && anniversary.Quality == 4 &&
               anniversary.TooltipLines.Any(line => line.Text.Contains("spell hit rating by 23",
                   StringComparison.OrdinalIgnoreCase)) &&
               anniversary.TooltipLines.Any(line => line.IsMoney && line.Money.Gold == "2g " &&
                                                     line.Money.Silver == "68s " &&
                                                     line.Money.Copper == "81c"),
            "Anniversary item did not use the bundled database before downloading its icon");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }
}

static void QueryBundledItemDatabase()
{
    var service = new ItemDatabaseService();
    Assert(service.IsAvailable, "bundled SQLite item database was not copied to the application output");
    var belt = service.Get("i:30038", "Anniversary");
    Assert(belt is
        {
            Name: "Belt of Blasting", Quality: 4, Level: 128, RequiredLevel: 70,
            ClassName: "Armor", SubclassName: "Cloth", InventoryName: "Waist", SellPrice: 26881
        }, "bundled TBC item fields were incomplete");
    Assert(service.Get("i:30038", "bcc")?.Name == "Belt of Blasting",
        "the bcc flavor did not resolve to the TBC database partition");
    var mopMount = service.Get("i:95416", "Progression");
    Assert(mopMount is { Name: "Sky Golem", Quality: 4, RequiredLevel: 80 },
        "the bundled MoP database partition was missing or incomplete");
    Assert(service.Get("i:95416", "Anniversary") is null,
        "a MoP-only item leaked into the TBC database partition");
    Assert(service.Get("i:76061", "Progression") is null,
        "a bind-on-pickup MoP item was included in the auction catalog");
    Assert(service.Get("i:25", "Retail") is
        { Name: "Worn Shortsword", Quality: 1, ClassName: "Weapon", InventoryName: "Main Hand" },
        "the bundled Retail database partition was missing or incomplete");
    Assert(ItemIconService.BuildItemPageUri("i:21943", "bcc")?.AbsoluteUri ==
           "https://www.wowhead.com/tbc/item=21943",
        "the bcc flavor did not resolve to TBC Wowhead");
    Assert(belt!.PreviewJson.Contains("Improves spell hit rating by 23", StringComparison.Ordinal),
        "compressed Blizzard preview data did not round-trip");
    var results = service.Query(new ItemDatabaseQuery("Anniversary", "Belt of Blasting", 4, 4, 1, "WAIST", 10));
    Assert(results.Count == 1 && results[0].Id == 30038,
        "indexed SQLite category, quality, slot, and name query failed");
    var filteredIds = service.QueryIds(new ItemDatabaseQuery("Anniversary", Quality: 4, ClassId: 4,
        SubclassId: 1, InventoryType: "WAIST"));
    Assert(filteredIds.Contains(30038), "lightweight advanced-search ID query omitted a matching item");
    Assert(service.Get("i:5108", "Anniversary") is null,
        "unavailable Blizzard item was returned as usable catalog data");
}

static async Task RetryThrottledItemDetails()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-wowhead-retry-{Guid.NewGuid():N}");
    try
    {
        var handler = new ThrottledWowheadHandler();
        using var client = new HttpClient(handler);
        using var service = new ItemIconService(new AppPaths(temporaryRoot), client, TimeSpan.FromMilliseconds(5));
        var pauseEvents = 0;
        service.DownloadPaused += (_, _) => pauseEvents++;

        var result = await service.GetAsync("i:19019", "Retail");

        Assert(result is not null && handler.TooltipRequests == 2 && handler.IconRequests == 2 && pauseEvents == 2,
            "throttled item details were not paused and retried");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }
}

static void FormatAuctionCurrency()
{
    var full = AuctionMoneyParts.FromCopper(1_020_304);
    Assert(full.Gold == "102g " && full.Silver == "03s " && full.Copper == "04c",
        "gold, silver, and copper were not split correctly");
    var copperOnly = AuctionMoneyParts.FromCopper(42);
    Assert(copperOnly.Gold.Length == 0 && copperOnly.Silver.Length == 0 && copperOnly.Copper == "42c",
        "copper-only price included empty denominations");
    Assert(AuctionMoneyParts.FromCopper(null).Empty == "—", "missing price did not retain its placeholder");
}

static void MapWowheadItemTaxonomy()
{
    var classification = WowItemTaxonomy.Classify(4, 4, 1);
    Assert(classification is { QualityName: "Epic", Category: "Armor", Subcategory: "Cloth" },
        "armor taxonomy IDs were mapped incorrectly");
    var weaponOptions = WowItemTaxonomy.SubcategoryOptions("Weapon");
    Assert(weaponOptions[0] == "All subcategories" && weaponOptions.Contains("Dagger") &&
           !weaponOptions.Contains("Cloth"), "subcategory choices did not depend on category");
}

static void FormatFactionPriceSnapshots()
{
    var updated = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var alliance = new AuctionHouseItemRow("i:19019", "Thunderfury", "Draenor-Alliance", "Retail",
        100_000, 2, 120_000, 130_000, updated);
    var horde = alliance with
    {
        Realm = "Draenor-Horde", MinBuyout = 110_000, NumAuctions = 3,
        MarketValueRecent = 125_000, Historical = 135_000
    };
    using var item = new AuctionHouseItemViewModel(alliance, [alliance, horde]);
    item.IsFavorite = true;
    Assert(item.FavoriteColor == "#F2A93B" && item.FavoriteToolTip == "Remove from favorites",
        "favorited auction item did not expose its active star state");

    Assert(alliance.BaseRealm == "Draenor" && alliance.Faction == "Alliance" && horde.Faction == "Horde",
        "faction-qualified realms were not normalized");
    Assert((alliance with { ItemString = "i:19019::5:7" }).BaseItemString == "i:19019",
        "item variants would not collapse into one result");
    Assert(item.TooltipText.Contains("Draenor · Alliance", StringComparison.Ordinal) &&
           item.TooltipText.Contains("Draenor · Horde", StringComparison.Ordinal) &&
           item.TooltipText.Contains("11g 00s 00c", StringComparison.Ordinal),
        "merged item tooltip did not retain both faction price snapshots");
    Assert(item.PriceSnapshots.Count == 2 &&
           item.PriceSnapshots[0].FactionColor == "#4A9EFF" &&
           item.PriceSnapshots[1].FactionColor == "#F06464" &&
           item.PriceSnapshots[1].MinBuyout.Gold == "11g ",
        "structured tooltip snapshots did not retain faction or denomination styling data");
    Assert(item.WowheadUri?.AbsoluteUri == "https://www.wowhead.com/item=19019",
        "retail item did not produce the expected Wowhead page link");
    var flavorRoutes = new Dictionary<string, string>
    {
        ["Retail"] = "https://www.wowhead.com/item=19019",
        ["Retail PTR"] = "https://www.wowhead.com/ptr/item=19019",
        ["PTR"] = "https://www.wowhead.com/ptr/item=19019",
        ["Retail Beta"] = "https://www.wowhead.com/beta/item=19019",
        ["Classic"] = "https://www.wowhead.com/mop-classic/item=19019",
        ["Progression"] = "https://www.wowhead.com/mop-classic/item=19019",
        ["Classic Beta"] = "https://www.wowhead.com/mop-classic/item=19019",
        ["Classic Era"] = "https://www.wowhead.com/classic/item=19019",
        ["Classic PTR"] = "https://www.wowhead.com/classic-ptr/item=19019",
        ["Classic Era PTR"] = "https://www.wowhead.com/classic-ptr/item=19019",
        ["Anniversary"] = "https://www.wowhead.com/tbc/item=19019",
        ["Anniversary PTR"] = "https://www.wowhead.com/tbc/item=19019"
    };
    foreach (var (flavor, expectedUri) in flavorRoutes)
    {
        using var flavorItem = new AuctionHouseItemViewModel(alliance with { Flavor = flavor });
        Assert(flavorItem.WowheadUri?.AbsoluteUri == expectedUri,
            $"{flavor} item did not produce the expected Wowhead page link");
    }
}

static void StoreRememberedLogin()
{
    var service = new NativeLoginCredentialStore(new InMemoryCredentialStore());
    var key = service.Save(null, "test@example.com", "keychain-only-password");
    Assert(Guid.TryParse(key, out _), "credential key was not an opaque GUID");
    var saved = service.Load(key);
    Assert(saved is not null && saved.Email == "test@example.com" &&
           saved.Password == "keychain-only-password", "saved login did not round-trip");
    service.Delete(key);
    Assert(service.Load(key) is null, "logout did not delete the saved login");
}

static async Task EnforceSingleInstance()
{
    var identifier = $"com.wuild.tsm-test-{Guid.NewGuid():N}";
    using var primary = new SingleInstanceCoordinator(identifier);
    Assert(primary.IsPrimary, "the first app launch did not become the primary instance");
    var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    primary.ActivationRequested += (_, _) => activated.TrySetResult();

    using var secondary = new SingleInstanceCoordinator(identifier);
    Assert(!secondary.IsPrimary, "a second app launch was incorrectly allowed to become primary");
    await Task.Run(secondary.NotifyPrimaryInstance);
    await activated.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(primary.ConsumeActivationRequest(), "the primary app did not retain the activation request");
}

static void DiscoverPtrVariants()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-ptr-discovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "_retail_ptr_", "Interface", "AddOns"));
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "_classic_ptr_", "Interface", "AddOns"));

    try
    {
        var installations = new WowInstallationService().Discover(temporaryRoot);
        Assert(installations.Any(value => value.Name == "Retail PTR"), "Retail PTR was not discovered");
        Assert(installations.Any(value => value.Name == "Classic PTR"), "Classic PTR was not discovered");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static void DetectLocalInstallationChanges()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-live-discovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(Path.Combine(temporaryRoot, "_retail_", "Interface", "AddOns"));
    var service = new WowInstallationService();

    try
    {
        var initial = service.GetDiscoveryFingerprint(temporaryRoot);
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "_classic_", "Interface", "AddOns"));
        var withClient = service.GetDiscoveryFingerprint(temporaryRoot);
        Assert(withClient != initial && withClient.Contains("Classic", StringComparison.Ordinal),
            "new WoW client was not detected");

        var savedVariables = Path.Combine(temporaryRoot, "_classic_", "WTF", "Account", "TEST",
            "SavedVariables", "TradeSkillMaster.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(savedVariables)!);
        File.WriteAllText(savedVariables, "TradeSkillMasterDB = { _scopeKeys = { realm = { 'FirstRealm' } } }");
        var withAccount = service.GetDiscoveryFingerprint(temporaryRoot);
        Assert(withAccount != withClient, "new local account and realm data were not detected");

        File.AppendAllText(savedVariables, "\nNewRealmMarker = true");
        var withRealmChange = service.GetDiscoveryFingerprint(temporaryRoot);
        Assert(withRealmChange == withAccount,
            "SavedVariables content changes incorrectly triggered a full client rediscovery");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static async Task UpdateInstalledAddon()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-addon-update-{Guid.NewGuid():N}");
    var gameRoot = Path.Combine(temporaryRoot, "_retail_");
    var addons = Path.Combine(gameRoot, "Interface", "AddOns");
    var addon = Path.Combine(addons, "TradeSkillMaster");
    Directory.CreateDirectory(addon);
    File.WriteAllText(Path.Combine(addon, "TradeSkillMaster.toc"), "## Version: 4.14.1");
    File.WriteAllText(Path.Combine(addon, "old.txt"), "old");

    try
    {
        var api = new FakeApiClient(new TsmStatusResponse(), CreateAddonZip(
            ("TradeSkillMaster/TradeSkillMaster.toc", "## Version: 4.14.2"),
            ("TradeSkillMaster/new.txt", "new")));
        var installation = new WowInstallation("Retail", gameRoot, addons, Path.Combine(gameRoot, "WTF", "Account"));
        var service = new AddonUpdateService(api);
        var updated = await service.UpdateInstalledAsync([installation],
            [new TsmAddonVersion { Name = "TradeSkillMaster", Version = "4.14.2" }], "Release");
        Assert(updated.SequenceEqual(["TradeSkillMaster"]), "addon update was not reported");
        Assert(File.Exists(Path.Combine(addon, "new.txt")) && !File.Exists(Path.Combine(addon, "old.txt")),
            "addon directory was not replaced");

        api.AddonArchive = CreateAddonZip(("../escaped.txt", "unsafe"));
        await AssertThrowsAsync<InvalidDataException>(() => service.UpdateInstalledAsync([installation],
            [new TsmAddonVersion { Name = "TradeSkillMaster", Version = "4.14.3" }], "Release"));
        Assert(File.Exists(Path.Combine(addon, "new.txt")), "failed update did not preserve the installed addon");
        Assert(!File.Exists(Path.Combine(temporaryRoot, "escaped.txt")), "unsafe archive escaped the staging directory");

        api.AddonArchive = CreateAddonZip(
            ("TradeSkillMaster/TradeSkillMaster.toc", "## Version: 4.14.2"),
            ("TradeSkillMaster/reinstalled.txt", "reinstalled"));
        await service.ReinstallAsync(addons, "TradeSkillMaster", "4.14.2", "Release");
        Assert(File.Exists(Path.Combine(addon, "reinstalled.txt")) &&
               !File.Exists(Path.Combine(addon, "new.txt")), "addon reinstall did not replace the installed folder");

        await AddonUpdateService.RemoveAsync(addons, "TradeSkillMaster");
        Assert(!Directory.Exists(addon), "addon removal left the selected folder installed");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static async Task SelectNativeUpdatePackage()
{
    const string releases =
        """
        [{
          "tag_name":"v9.1.0","name":"TSM 9.1.0","html_url":"https://github.com/Wuild/TSM-App/releases/tag/v9.1.0",
          "draft":false,"prerelease":false,
          "assets":[
            {"name":"TSM-9.1.0-x86_64.AppImage","browser_download_url":"https://example.test/linux-x64"},
            {"name":"TSM-9.1.0-windows-x86_64.zip","browser_download_url":"https://example.test/windows-x64"},
            {"name":"TSM-9.1.0-macos-arm64.zip","browser_download_url":"https://example.test/macos-arm64"}
          ]
        }]
        """;
    using var httpClient = new HttpClient(new StaticJsonHandler(releases));
    using var service = new GitHubUpdateService(httpClient);
    var update = await service.CheckAsync(false, "0.1.0")
                 ?? throw new InvalidOperationException("new GitHub release was not detected");
    var expectedKind = OperatingSystem.IsWindows()
        ? UpdatePackageKind.WindowsZip
        : OperatingSystem.IsMacOS() ? UpdatePackageKind.MacAppZip : UpdatePackageKind.AppImage;
    Assert(update.PackageKind == expectedKind && update.PackageDownload is not null,
        "the release package did not match the current platform and architecture");
}

static void IndexAuctionHouseSnapshot()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-auction-index-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);
    var savedVariablesPath = Path.Combine(temporaryRoot, "TradeSkillMaster.lua");
    var appHelperSavedVariablesPath = Path.Combine(temporaryRoot, "TradeSkillMaster_AppHelper.lua");
    var appDataPath = Path.Combine(temporaryRoot, "AppData.lua");
    File.WriteAllText(savedVariablesPath,
        "TSMItemInfoDB = { [\"names\"] = { [1] = \"Test Item\\002\\002Design: Truesilver Crab\\002\" }, " +
        "[\"itemStrings\"] = { [1] = \"i:24997\\002i:12345\\002i:21943\\002\" }, }");
    File.WriteAllText(appHelperSavedVariablesPath,
        "TradeSkillMaster_AppHelperDB = { [\"region\"] = \"EU-Fresh\", }");
    File.WriteAllText(appDataPath,
        "select(2, ...).LoadData(\"AUCTIONDB_NON_COMMODITY_DATA\",\"TestRealm-Alliance\",[[return {downloadTime=1700000000,fields={\"itemString\",\"minBuyout\",\"numAuctions\",\"marketValueRecent\"},data={{21943,1TGO,G,3PH2}}}]]) --<AUCTIONDB_NON_COMMODITY_DATA,TestRealm-Alliance,1700000000>\n" +
        "select(2, ...).LoadData(\"AUCTIONDB_REGION_SALE\",\"Fresh-EU\",[[return {downloadTime=1700000000,fields={\"itemString\",\"regionSale\",\"regionSoldPerDay\",\"regionSalePercent\"},data={{21943,34,6QO,3O}}}]]) --<AUCTIONDB_REGION_SALE,Fresh-EU,1700000000>\n");
    try
    {
        var account = new TsmAccount("TEST · Retail", "TEST", "Retail", savedVariablesPath,
            AppHelperSavedVariablesPath: appHelperSavedVariablesPath,
            AppDataPath: appDataPath);
        var service = new AuctionHouseService(new AppDataCatalogParser(), new LuaSavedVariablesParser());
        var row = service.BuildIndex([account], []).Single();
        Assert(row.ItemString == "i:21943" && row.ItemName == "Design: Truesilver Crab",
            "empty TSM names shifted the auction item identity");
        Assert(row.MinBuyout == 63000 && row.NumAuctions == 16 && row.MarketValueRecent == 124450,
            "base-32 AuctionDB values were not decoded correctly");
        Assert(row.RegionSaleAverage == 100 && row.RegionSoldPerDay == 7000 &&
               row.RegionSaleRatePercent == 120 && row.RegionSoldPerDayDisplay == "7/day" &&
               row.RegionSaleRateDisplay == "12%",
            "regional sale statistics were not attached to the realm auction row");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static void PersistAuctionPriceHistory()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-price-history-{Guid.NewGuid():N}");
    try
    {
        var paths = new AppPaths(temporaryRoot);
        var store = new AuctionPriceHistoryStore(paths);
        var favorite = new AuctionFavorite("Anniversary", "i:1234");
        var firstTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var first = new AuctionHouseItemRow("i:1234", "Test Item", "TestRealm-Alliance", "Anniversary",
            100_000, 2, 110_000, 90_000, firstTime);
        store.Capture([favorite], [first]);
        store.Capture([favorite], [first]);
        var second = first with { MinBuyout = 125_000, UpdatedAt = firstTime.AddHours(1) };
        store.Capture([favorite], [second]);

        var reloaded = new AuctionPriceHistoryStore(paths).Get("Anniversary", "i:1234:0");
        Assert(reloaded.Count == 2, "price snapshots were duplicated or not persisted");
        Assert(reloaded[0].MinBuyout == 100_000 && reloaded[1].MinBuyout == 125_000,
            "saved price history values were not preserved");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, true);
    }
}

static byte[] CreateAddonZip(params (string Path, string Content)[] files)
{
    using var output = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        foreach (var (path, content) in files)
        {
            var entry = archive.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
    }
    return output.ToArray();
}

static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown");
}

static async Task LegacyAuthenticationCanBeDisabled()
{
    var handler = new FakeTsmHandler();
    using var httpClient = new HttpClient(handler);
    using var client = new TsmLegacyApiClient(() => false, httpClient, () => 1700000000);
    var result = await client.LoginAsync("test@example.com", "not-saved");
    Assert(!result.Success, "disabled legacy authentication unexpectedly succeeded");
    Assert(handler.RequestCount == 0, "disabled legacy authentication made a network request");
}

static async Task AuthenticateAndSyncAppData()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-sync-tests-{Guid.NewGuid():N}");
    var gameRoot = Path.Combine(temporaryRoot, "_retail_");
    var addons = Path.Combine(gameRoot, "Interface", "AddOns");
    var helper = Path.Combine(addons, "TradeSkillMaster_AppHelper");
    var accountRoot = Path.Combine(gameRoot, "WTF", "Account", "ACCOUNT#1");
    var savedVariables = Path.Combine(accountRoot, "SavedVariables");
    Directory.CreateDirectory(helper);
    File.WriteAllText(Path.Combine(helper, "TradeSkillMaster_AppHelper.toc"), "## Version: 4.14.1");
    Directory.CreateDirectory(savedVariables);
    var savedVariablesPath = Path.Combine(savedVariables, "TradeSkillMaster.lua");
    File.WriteAllText(savedVariablesPath, SavedVariablesFixture());

    var handler = new FakeTsmHandler();
    using var httpClient = new HttpClient(handler);
    using var client = new TsmLegacyApiClient(() => true, httpClient, () => 1700000000);

    try
    {
        var login = await client.LoginAsync("test@example.com", "not-saved");
        Assert(login.Success && client.IsAuthenticated, "legacy authentication flow failed");
        Assert(handler.SawSignedApiRequest, "API request signature parameters were missing");
        var availableRealms = await client.GetAvailableRealmsAsync();
        Assert(availableRealms.Count == 2 && availableRealms.Any(realm => realm.DisplayName == "Retail-US-Aegwynn") &&
               availableRealms.Any(realm => realm.DisplayName == "Progression-EU-Amnennar-Alliance"),
            "available realm list was not parsed");
        handler.RealmPayload = """{"success":true,"data":[]}""";
        Assert((await client.GetAvailableRealmsAsync()).Count == 0,
            "an empty available-realm response was treated as a service error");
        var retailRealm = availableRealms.Single(realm => realm.GameVersion == "retail");
        var progressionRealm = availableRealms.Single(realm => realm.GameVersion == "bcc");
        await client.AddRealmAsync(retailRealm.GameVersion, retailRealm.Id);
        Assert(handler.SawRealmAdd, "realm add endpoint was not called with the selected server ID");
        await client.RemoveRealmAsync(progressionRealm.GameVersion, progressionRealm.Region,
            progressionRealm.Name);
        Assert(handler.SawRealmRemove, "realm remove endpoint was not called with the selected region and name");
        var uploadPath = new[] { "US", "RHJhZW5vcg==", "QUNDT1VOVCMx" };
        Assert(await client.GetLastUploadAsync("sales", uploadPath) == 0,
            "upload checkpoint was not read");
        await client.UploadAsync(new TsmUploadRequest("sales", uploadPath, new object[] { new[] { 123L, 5000L } }));
        Assert(handler.SawDataUpload, "SavedVariables data was not posted with the legacy upload contract");

        var parser = new AppDataCatalogParser();
        var accounting = new AccountingService(new LuaSavedVariablesParser());
        var installation = new WowInstallation("Retail", gameRoot, addons,
            Path.Combine(gameRoot, "WTF", "Account"));
        var account = new TsmAccount("ACCOUNT#1 · Retail", "ACCOUNT#1", "Retail", savedVariablesPath);
        var service = new AppDataSyncService(client, parser, new AppDataFileWriter(parser), accounting);

        var first = await service.SyncAsync([installation], [account], "Release");
        Assert(first.Errors.Count == 0, "first sync returned errors");
        Assert(first.UpdatedAddons?.SequenceEqual(["TradeSkillMaster_AppHelper"]) == true,
            "status-driven addon update was not installed");
        Assert(handler.AddonDownloadCount == 1 && handler.SawAddonVersion,
            "addon endpoint did not receive the recovered version contract");
        Assert(first.DownloadedEntries == 1 && first.WrittenFiles == 1, "first sync counts were wrong");
        var appDataPath = Path.Combine(helper, "AppData.lua");
        var appData = File.ReadAllText(appDataPath);
        Assert(appData.Contains("--<APP_INFO,Global,", StringComparison.Ordinal), "APP_INFO was not written");
        Assert(appData.Contains("--<AUCTIONDB_NON_COMMODITY_DATA,Draenor,1700000200>",
            StringComparison.Ordinal), "realm data was not written");
        Assert(appData.Contains("[=[return {[1]=123,[2]=\"]] safe\"}]=]", StringComparison.Ordinal),
            "Lua long-bracket delimiter was not made collision-safe");
        Assert(!Directory.EnumerateFiles(helper, ".AppData.*.tmp").Any(), "atomic writer left a temporary file");

        var second = await service.SyncAsync([installation], [account], "Release");
        Assert(second.Errors.Count == 0 && second.DownloadedEntries == 0, "unchanged data was downloaded again");
        Assert(handler.DataDownloadCount == 1, "differential sync made a duplicate blob request");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static async Task SyncFactionQualifiedRealm()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-anniversary-sync-{Guid.NewGuid():N}");
    var gameRoot = Path.Combine(temporaryRoot, "_anniversary_");
    var addons = Path.Combine(gameRoot, "Interface", "AddOns");
    var helper = Path.Combine(addons, "TradeSkillMaster_AppHelper");
    var savedVariables = Path.Combine(gameRoot, "WTF", "Account", "ACCOUNT#1", "SavedVariables");
    Directory.CreateDirectory(helper);
    Directory.CreateDirectory(savedVariables);
    var savedVariablesPath = Path.Combine(savedVariables, "TradeSkillMaster.lua");
    File.WriteAllText(savedVariablesPath, SavedVariablesFixture().Replace("Draenor", "Thunderstrike",
        StringComparison.Ordinal));

    try
    {
        var api = new FakeApiClient(new TsmStatusResponse
        {
            AnniversaryRealms =
            [
                new TsmRealmEntry
                {
                    Name = "Thunderstrike-Alliance",
                    Region = "Fresh-EU",
                    AppDataStrings = new Dictionary<string, TsmDataString>
                    {
                        ["AUCTIONDB_NON_COMMODITY_DATA"] = new()
                        {
                            Url = "https://data.example.test/thunderstrike.gz",
                            LastModified = 1786225795
                        }
                    }
                }
            ]
        });
        var parser = new AppDataCatalogParser();
        var accounting = new AccountingService(new LuaSavedVariablesParser());
        var service = new AppDataSyncService(api, parser, new AppDataFileWriter(parser), accounting);
        var installation = new WowInstallation("Anniversary", gameRoot, addons,
            Path.Combine(gameRoot, "WTF", "Account"));
        var account = new TsmAccount("ACCOUNT#1 · Anniversary", "ACCOUNT#1", "Anniversary",
            savedVariablesPath);

        var result = await service.SyncAsync([installation], [account], "Release");
        Assert(result.Errors.Count == 0 && result.DownloadedEntries == 1,
            "faction-qualified anniversary realm was filtered out");
        var catalog = parser.ParseFile(Path.Combine(helper, "AppData.lua"));
        Assert(catalog.LastUpdate("AUCTIONDB_NON_COMMODITY_DATA", "Thunderstrike-Alliance") == 1786225795,
            "anniversary realm data was not written with the server's canonical name");
    }
    finally
    {
        Directory.Delete(temporaryRoot, true);
    }
}

static async Task AutomaticSyncCadence()
{
    Assert(MainWindowViewModel.AutomaticSyncInterval == TimeSpan.FromMinutes(30),
        "application sync interval was not 30 minutes");
    var scheduler = new BackgroundSyncScheduler(TimeSpan.FromMilliseconds(10));
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var active = 0;
    var maximumActive = 0;
    var calls = 0;
    scheduler.Start(async cancellationToken =>
    {
        var current = Interlocked.Increment(ref active);
        maximumActive = Math.Max(maximumActive, current);
        await Task.Delay(25, cancellationToken);
        Interlocked.Decrement(ref active);
        if (Interlocked.Increment(ref calls) >= 2)
            completed.TrySetResult();
    });

    try
    {
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert(maximumActive == 1, "automatic sync callbacks overlapped");
    }
    finally
    {
        await scheduler.DisposeAsync();
    }
}

static async Task UploadTsmData()
{
    var root = Path.Combine(Path.GetTempPath(), $"tsm-upload-tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var tsmPath = Path.Combine(root, "TradeSkillMaster.lua");
    var helperPath = Path.Combine(root, "TradeSkillMaster_AppHelper.lua");
    File.WriteAllText(tsmPath, """
        TradeSkillMasterDB = {
          ["_scopeKeys"] = { ["realm"] = { "Draenor" }, ["factionrealm"] = { "Alliance - Draenor" } },
          ["r@Draenor@internalData@csvSales"] = "itemString,price,stackSize,quantity,time,source\ni:123,5000,2,1,1700000000,Auction",
          ["r@Draenor@internalData@saveTimeSales"] = "1700000010",
          ["f@Alliance - Draenor@internalData@csvAuctionDBScan"] = "itemString,minBuyout,marketValue,numAuctions,quantity\ni:123,4000,4500,3,6",
          ["f@Alliance - Draenor@internalData@auctionDBScanTime"] = 1700000020,
        }
        """);
    File.WriteAllText(helperPath, """
        TradeSkillMaster_AppHelperDB = {
          ["region"] = "US",
          ["blackMarket"] = { ["Draenor"] = { ["updateTime"] = 1700000030, ["data"] = "scan" } },
          ["analytics"] = { ["updateTime"] = 1700000040, ["data"] = { [1] = "event" } },
          ["errorReports"] = { ["updateTime"] = 1700000050, ["data"] = { [1] = "report" } },
          ["shoppingMaxPrices"] = { ["Default"] = { ["updateTime"] = 1700000060, ["Group"] = "[123,5000]" } },
        }
        """);
    var api = new FakeApiClient(new TsmStatusResponse());
    var service = new TsmDataUploadService(api, new LuaSavedVariablesParser(), () => "test-system");
    try
    {
        var account = new TsmAccount("ACCOUNT#1 · Retail", "ACCOUNT#1", "Retail", tsmPath, helperPath);
        var count = await service.UploadAsync([account]);
        var endpoints = api.Uploads.Select(upload => upload.Endpoint).ToHashSet(StringComparer.Ordinal);
        Assert(count == 7 && endpoints.SetEquals(["auctiondb_scan", "sales", "black_market", "analytics",
                "error_report", "groups", "sv"]),
            "SavedVariables upload categories were incomplete");
        api.Uploads.Clear();
        api.UnsupportedEndpoints.Add("sales");
        count = await service.UploadAsync([account]);
        Assert(count == 6 && api.Uploads.All(upload => upload.Endpoint != "sales"),
            "an upload endpoint omitted by the authenticated session was not skipped");
        Assert(new AppSettings().UploadAddonData, "addon uploads should default to enabled");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static async Task PremiumBackups()
{
    var root = Path.Combine(Path.GetTempPath(), $"tsm-backup-tests-{Guid.NewGuid():N}");
    var data = Path.Combine(root, "data");
    var saved = Path.Combine(root, "SavedVariables");
    Directory.CreateDirectory(saved);
    var tsm = Path.Combine(saved, "TradeSkillMaster.lua");
    var helper = Path.Combine(saved, "TradeSkillMaster_AppHelper.lua");
    await File.WriteAllTextAsync(tsm, "TradeSkillMasterDB = { [\"value\"] = 1 }");
    await File.WriteAllTextAsync(helper, "TradeSkillMaster_AppHelperDB = { [\"region\"] = \"US\" }");
    var api = new FakeApiClient(new TsmStatusResponse());
    var service = new BackupService(new AppPaths(data), api, () => "system");
    var account = new TsmAccount("ACCOUNT#1 · Retail", "ACCOUNT#1", "Retail", tsm, helper);
    try
    {
        var legacyCloudBackup = new BackupEntry("old-system", "ACCOUNT#1", 20260809012455446,
            false, true, false);
        Assert(legacyCloudBackup.Created.Year == 2026 && legacyCloudBackup.Created.Month == 8 &&
               legacyCloudBackup.Created.Day == 9,
            "legacy cloud backup timestamps were not parsed as compact dates");
        var entries = await service.SyncAsync([account], true);
        Assert(entries.Count == 1 && entries[0].IsLocal && entries[0].IsRemote && api.RemoteBackups.Count == 1,
            "Premium backup did not synchronize to the cloud API");
        await File.WriteAllTextAsync(tsm, "changed");
        await service.RestoreAsync(entries[0], [account], true);
        Assert((await File.ReadAllTextAsync(tsm)).Contains("TradeSkillMasterDB", StringComparison.Ordinal),
            "local backup restore did not replace SavedVariables");
    }
    finally
    {
        Directory.Delete(root, true);
    }
}

static void CombineApplicationLogs()
{
    var root = Path.Combine(Path.GetTempPath(), $"tsm-log-tests-{Guid.NewGuid():N}");
    var paths = new AppPaths(root);
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(paths.ErrorLogFile, "captured application failure");
        var service = new AppLogService(paths);
        service.WriteSync("info", "Downloaded realm data.");
        var combined = service.ReadCombined();
        Assert(combined.Contains("SYNC LOG", StringComparison.Ordinal) &&
               combined.Contains("Downloaded realm data.", StringComparison.Ordinal) &&
               combined.Contains("APPLICATION ERRORS", StringComparison.Ordinal) &&
               combined.Contains("captured application failure", StringComparison.Ordinal),
            "combined log output omitted sync or application entries");
        var upload = service.ReadSyncLog();
        Assert(upload.Contains("Downloaded realm data.", StringComparison.Ordinal) &&
               !upload.Contains("captured application failure", StringComparison.Ordinal),
            "the synchronization upload payload included local application errors");
    }
    finally
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}

static string SavedVariablesFixture() =>
    """
    -- Representative TSM SavedVariables subset
    TradeSkillMasterDB = {
        ["_scopeKeys"] = {
            ["realm"] = {
                [1] = "Draenor",
            },
        },
        ["enabled"] = true,
        ["unused"] = nil,
        ["r@Draenor@internalData@csvSales"] = "itemString,price,stackSize,quantity,player,time,source\ni:123,5000,2,1,Test,1700000000,Auction",
        ["s@Test - Alliance - Draenor@internalData@goldLog"] = "minute,copper\n28333333,10000\n28334773,25000",
    }
    TSMItemInfoDB = {
        ["names"] = "Copper, Bar\002",
        ["itemStrings"] = "i:123\002",
    }
    """;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class FakeTsmHandler : HttpMessageHandler
{
    public string RealmPayload { get; set; } =
        """{"data":{"retail":{"US":[{"realmId":123,"realmName":"Aegwynn"}]},"bcc":{"EU":[{"serverId":456,"name":"Amnennar-Alliance"}]}},"success":true}""";
    public int RequestCount { get; private set; }
    public int DataDownloadCount { get; private set; }
    public int AddonDownloadCount { get; private set; }
    public bool SawSignedApiRequest { get; private set; }
    public bool SawAddonVersion { get; private set; }
    public bool SawRealmAdd { get; private set; }
    public bool SawRealmRemove { get; private set; }
    public bool SawDataUpload { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var uri = request.RequestUri ?? throw new InvalidOperationException("request URI missing");
        if (uri.Host == "id.tradeskillmaster.com")
            return Json("""{"access_token":"temporary-access-token"}""");

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath == "/v2/auth")
        {
            SawSignedApiRequest = HasSignature(uri.Query);
            return Json("""{"session":"memory-only-session","isPremium":true,"endpointSubdomains":{"status":"app-server","addon":"app-server","sales":"app-server"}}""");
        }

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath == "/v2/status")
        {
            SawSignedApiRequest |= HasSignature(uri.Query);
            return Json("""
                {
                  "appVersion":41402,
                  "addons":[{"name":"TradeSkillMaster_AppHelper","version_str":"4.14.2"}],
                  "addonMessage":{"id":7,"msg":"Sync ready"},
                  "realms":[{
                    "id":1,"ahId":2,"name":"Draenor","region":"US",
                    "appDataStrings":{
                      "AUCTIONDB_NON_COMMODITY_DATA":{
                        "url":"https://data.example.test/draenor.gz","lastModified":1700000200
                      }
                    }
                  }],
                  "regions":[],
                  "realms-Progression":[{
                    "id":"Progression-US-123","ahId":"",
                    "name":"Mankrik","region":"BCC-US","appDataStrings":{}
                  }]
                }
                """);
        }

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath == "/v2/realms2/list")
            return Json(RealmPayload);

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath == "/v2/realms2/add/retail/123")
        {
            SawRealmAdd = HasSignature(uri.Query);
            return Json("""{"success":true}""");
        }

        if (uri.Host == "app-server.tradeskillmaster.com" &&
            uri.AbsolutePath == "/v2/realms2/remove/bcc/EU/Amnennar-Alliance")
        {
            SawRealmRemove = HasSignature(uri.Query);
            return Json("""{"success":true}""");
        }

        if (uri.Host == "app-server.tradeskillmaster.com" &&
            uri.AbsolutePath == "/v2/addon/TradeSkillMaster_AppHelper")
        {
            AddonDownloadCount++;
            SawAddonVersion = uri.Query.Contains("channel=release", StringComparison.Ordinal) &&
                              uri.Query.Contains("tsm_version=4.14.2", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(AddonZip())
            });
        }

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath.StartsWith("/v2/sales/US/",
                StringComparison.Ordinal))
        {
            if (request.Method == HttpMethod.Post)
            {
                SawDataUpload = request.Content?.Headers.ContentEncoding.Contains("gzip") == true &&
                                request.Content.Headers.ContentType?.MediaType == "application/json";
                return Json("""{"success":true}""");
            }
            return Json("""{"lastUpload":0}""");
        }

        if (uri.Host == "data.example.test")
        {
            DataDownloadCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{[1]=123,[2]=\"]] safe\"}", Encoding.UTF8, "text/plain")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static bool HasSignature(string query) =>
        query.Contains("version=41402", StringComparison.Ordinal) &&
        query.Contains("time=1700000000", StringComparison.Ordinal) &&
        query.Contains("token=", StringComparison.Ordinal);

    private static byte[] AddonZip()
    {
        using var output = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(output,
                   System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("TradeSkillMaster_AppHelper/TradeSkillMaster_AppHelper.toc");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write("## Version: 4.14.2");
        }
        return output.ToArray();
    }

    private static Task<HttpResponseMessage> Json(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    });
}

sealed class FakeApiClient(TsmStatusResponse status, byte[]? addonArchive = null) : ITsmApiClient
{
    public byte[] AddonArchive { get; set; } = addonArchive ?? [];
    public List<TsmUploadRequest> Uploads { get; } = [];
    public Dictionary<string, byte[]> RemoteBackups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnsupportedEndpoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsAuthenticated => true;
    public bool IsPremium => true;
    public bool SupportsEndpoint(string endpoint) => !UnsupportedEndpoints.Contains(endpoint);
    public Task<ApiLoginResult> LoginAsync(string email, string password,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApiLoginResult(true, "Signed in"));

    public Task<TsmStatusResponse> GetStatusAsync(string channel,
        CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<string> DownloadDataAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult("{[1]=123}");

    public Task<byte[]> DownloadAddonAsync(string name, string channel, string version,
        CancellationToken cancellationToken = default) => Task.FromResult(AddonArchive);

    public Task<IReadOnlyList<TsmRealmChoice>> GetAvailableRealmsAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TsmRealmChoice>>([]);

    public Task AddRealmAsync(string gameVersion, string realmId,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveRealmAsync(string gameVersion, string region, string realmName,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<long> GetLastUploadAsync(string endpoint, IReadOnlyList<string> path,
        CancellationToken cancellationToken = default) => Task.FromResult(0L);

    public Task UploadAsync(TsmUploadRequest upload,
        CancellationToken cancellationToken = default)
    {
        Uploads.Add(upload);
        return Task.CompletedTask;
    }

    public Task<TsmBackupCatalog> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var backups = RemoteBackups.Keys.Select(name =>
        {
            var parts = Path.GetFileNameWithoutExtension(name).Split('_');
            return new TsmRemoteBackup(parts[0], parts[1], long.Parse(parts[2]), false);
        }).ToArray();
        return Task.FromResult(new TsmBackupCatalog(backups, new HashSet<string>()));
    }

    public Task<byte[]> DownloadBackupAsync(string remoteName,
        CancellationToken cancellationToken = default) => Task.FromResult(RemoteBackups[remoteName]);

    public Task UploadBackupAsync(string remoteName, byte[] archive,
        CancellationToken cancellationToken = default)
    {
        RemoteBackups[remoteName] = archive;
        return Task.CompletedTask;
    }

    public Task UploadLogAsync(string log, bool crash = false,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Logout() { }
}

sealed class StaticJsonHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
}

sealed class WowheadCacheHandler : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        HttpContent content = request.RequestUri!.Host == "nether.wowhead.com"
            ? new StringContent(JsonSerializer.Serialize(new
                {
                    name = "Thunderfury",
                    quality = 5,
                    icon = "inv_sword_39",
                    tooltip = "<!--nstart--><b class=\"q5\">Thunderfury</b><!--nend-->" +
                              "<b class=\"q0 whtt-extra\">Phase 2</b><br>Item Level 80<br>" +
                              "Binds when picked up<table width=\"100%\"><tr><td>Main Hand</td>" +
                              "<th><!--scstart2:7--><span class=\"q1\">Sword</span><!--scend--></th></tr></table>" +
                              "<a class=\"socket-blue q0\">Blue Socket</a><br>" +
                              "<span class=\"q2\">Equip: Calls down lightning.</span><br>" +
                              "<div class=\"whtt-extra\">Dropped by: Ragnaros</div>" +
                              "<div class=\"whtt-extra\">Drop Chance: 3.2%</div>" +
                              "<div class=\"whtt-extra whtt-maxstack\">Max Stack: 1000</div>" +
                              "<div class=\"whtt-sellprice\">Sell Price: <span class=\"moneygold\">1234</span> " +
                              "<span class=\"moneysilver\">8</span> <span class=\"moneycopper\">1</span></div>"
                }), Encoding.UTF8, "application/json")
            : new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

sealed class ThrottledWowheadHandler : HttpMessageHandler
{
    public int TooltipRequests { get; private set; }
    public int IconRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != "nether.wowhead.com")
        {
            IconRequests++;
            return Task.FromResult(new HttpResponseMessage(IconRequests == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9])
            });
        }

        TooltipRequests++;
        if (TooltipRequests == 1)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                name = "Thunderfury",
                quality = 5,
                icon = "inv_sword_39",
                tooltip = "<!--scstart2:7--><span>Sword</span><!--scend-->"
            }), Encoding.UTF8, "application/json")
        });
    }
}
