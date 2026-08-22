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
    ("discovers realms and exports Accounting CSV", Run(ExportAccounting)),
    ("indexes AppData metadata and derives local realm status", Run(IndexAppData)),
    ("persists onboarding and manually managed realm settings", Run(PersistManagedRealms)),
    ("restores application settings to their defaults", Run(ResetApplicationSettings)),
    ("round-trips remembered login through a credential store", Run(StoreRememberedLogin)),
    ("blocks legacy authentication when its transport is disabled", LegacyAuthenticationCanBeDisabled),
    ("authenticates and differentially syncs AppData atomically", AuthenticateAndSyncAppData),
    ("syncs faction-qualified anniversary realm data", SyncFactionQualifiedRealm),
    ("discovers PTR client variants", Run(DiscoverPtrVariants)),
    ("safely updates, reinstalls, and removes addons", UpdateInstalledAddon),
    ("runs automatic sync callbacks serially on a 30 minute cadence", AutomaticSyncCadence),
    ("selects the native GitHub update package", SelectNativeUpdatePackage),
    ("decodes and indexes local AuctionDB prices", Run(IndexAuctionHouseSnapshot)),
    ("persists favorite item price history without duplicate snapshots", Run(PersistAuctionPriceHistory)),
    ("caches Wowhead item details and icons by flavor", CacheWowheadItemDetails),
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
        HasCompletedOnboarding = true,
        WindowWidth = 1777,
        WindowHeight = 999,
        WindowMaximized = true,
        AuctionFavorites = [new AuctionFavorite("Retail", "i:19019")],
        ManagedRealms = [new ManagedRealmSelection("retail", "EU", "Draenor", "1")]
    };
    settings.ResetToDefaults();
    var defaults = new AppSettings();
    Assert(settings.Email == defaults.Email && settings.WowPath == defaults.WowPath &&
           settings.RunAtStartup == defaults.RunAtStartup && settings.MinimizeToTray == defaults.MinimizeToTray &&
           settings.HasCompletedOnboarding == defaults.HasCompletedOnboarding &&
           settings.WindowWidth == defaults.WindowWidth && settings.WindowHeight == defaults.WindowHeight &&
           settings.WindowMaximized == defaults.WindowMaximized && settings.AuctionFavorites.Count == 0 &&
           settings.ManagedRealms.Count == 0,
        "one or more application preferences survived a full reset");
}

static void ExportAccounting()
{
    var temporaryRoot = Path.Combine(Path.GetTempPath(), $"tsm-parser-tests-{Guid.NewGuid():N}");
    var accountRoot = Path.Combine(temporaryRoot, "_retail_", "WTF", "Account", "ACCOUNT#1");
    var savedVariables = Path.Combine(accountRoot, "SavedVariables");
    var addons = Path.Combine(temporaryRoot, "_retail_", "Interface", "AddOns");
    var exports = Path.Combine(temporaryRoot, "exports");
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

        var result = service.Export(account, "Draenor",
            new AccountingExportOptions(true, false, false, false, false, false), exports);
        Assert(result.Errors.Count == 0, "export returned errors");
        Assert(result.Files.Count == 1, "sales export file missing");
        var csv = File.ReadAllText(result.Files[0]);
        Assert(csv.Contains("itemString,itemName,price", StringComparison.Ordinal), "itemName column missing");
        Assert(csv.Contains("i:123,\"Copper, Bar\",5000", StringComparison.Ordinal), "item name lookup or CSV escaping failed");
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
            AuctionSortColumn = "MinBuyout",
            AuctionSortDescending = true,
            AuctionFavorites = [new AuctionFavorite("Retail", "i:19019")],
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
        Assert(loaded.AuctionSortColumn == "MinBuyout" && loaded.AuctionSortDescending,
            "auction sorting preference did not survive settings reload");
        Assert(loaded.AuctionFavorites.Single() == settings.AuctionFavorites.Single(),
            "auction favorite did not survive settings reload");
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
        Assert(handler.Requests.Count == 2, "a cached lookup unexpectedly contacted Wowhead again");
        Assert(handler.Requests[0].AbsolutePath.Contains("/classic/tooltip/item/19019", StringComparison.Ordinal),
            "Classic Era used the wrong Wowhead flavor endpoint");
        Assert(firstResult.Path.Contains(Path.Combine("ItemIcons", "classic"), StringComparison.Ordinal),
            "item cache was not isolated by flavor");

        var retail = await service.GetAsync("i:19019", "Retail");
        Assert(retail is not null && retail.Path != firstResult.Path,
            "Retail and Classic item cache entries collided");
        Assert(handler.Requests.Count == 4 && handler.Requests[2].AbsolutePath == "/tooltip/item/19019",
            "Retail did not use its own Wowhead endpoint and cache namespace");

        var anniversary = await service.GetAsync("i:23446", "Anniversary");
        Assert(anniversary is not null &&
               anniversary.Path.Contains(Path.Combine("ItemIcons", "tbc"), StringComparison.Ordinal),
            "Anniversary item cache did not use its TBC namespace");
        Assert(handler.Requests.Count == 6 &&
               handler.Requests[4].AbsolutePath == "/tbc/tooltip/item/23446",
            "Anniversary item did not use the Wowhead TBC endpoint");
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
        "TSMItemInfoDB = { [\"names\"] = \"Test Item\\002\", [\"itemStrings\"] = \"i:24997\\002\", }");
    File.WriteAllText(appHelperSavedVariablesPath,
        "TradeSkillMaster_AppHelperDB = { [\"region\"] = \"EU-Fresh\", }");
    File.WriteAllText(appDataPath,
        "select(2, ...).LoadData(\"AUCTIONDB_NON_COMMODITY_DATA\",\"TestRealm-Alliance\",[[return {downloadTime=1700000000,fields={\"itemString\",\"minBuyout\",\"numAuctions\",\"marketValueRecent\"},data={{24997,1TGO,G,3PH2}}}]]) --<AUCTIONDB_NON_COMMODITY_DATA,TestRealm-Alliance,1700000000>\n" +
        "select(2, ...).LoadData(\"AUCTIONDB_REGION_SALE\",\"Fresh-EU\",[[return {downloadTime=1700000000,fields={\"itemString\",\"regionSale\",\"regionSoldPerDay\",\"regionSalePercent\"},data={{24997,34,6QO,3O}}}]]) --<AUCTIONDB_REGION_SALE,Fresh-EU,1700000000>\n");
    try
    {
        var account = new TsmAccount("TEST · Retail", "TEST", "Retail", savedVariablesPath,
            AppHelperSavedVariablesPath: appHelperSavedVariablesPath,
            AppDataPath: appDataPath);
        var service = new AuctionHouseService(new AppDataCatalogParser(), new LuaSavedVariablesParser());
        var row = service.BuildIndex([account], []).Single();
        Assert(row.ItemString == "i:24997" && row.ItemName == "Test Item", "auction item identity was not decoded");
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
        var retailRealm = availableRealms.Single(realm => realm.GameVersion == "retail");
        var progressionRealm = availableRealms.Single(realm => realm.GameVersion == "bcc");
        await client.AddRealmAsync(retailRealm.GameVersion, retailRealm.Id);
        Assert(handler.SawRealmAdd, "realm add endpoint was not called with the selected server ID");
        await client.RemoveRealmAsync(progressionRealm.GameVersion, progressionRealm.Region,
            progressionRealm.Name);
        Assert(handler.SawRealmRemove, "realm remove endpoint was not called with the selected region and name");

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
        ["r@Draenor@internalData@csvSales"] = "itemString,price,stackSize,quantity,time,source\ni:123,5000,2,1,1700000000,Auction",
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
    public int RequestCount { get; private set; }
    public int DataDownloadCount { get; private set; }
    public int AddonDownloadCount { get; private set; }
    public bool SawSignedApiRequest { get; private set; }
    public bool SawAddonVersion { get; private set; }
    public bool SawRealmAdd { get; private set; }
    public bool SawRealmRemove { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var uri = request.RequestUri ?? throw new InvalidOperationException("request URI missing");
        if (uri.Host == "id.tradeskillmaster.com")
            return Json("""{"access_token":"temporary-access-token"}""");

        if (uri.Host == "app-server.tradeskillmaster.com" && uri.AbsolutePath == "/v2/auth")
        {
            SawSignedApiRequest = HasSignature(uri.Query);
            return Json("""{"session":"memory-only-session","isPremium":true,"endpointSubdomains":{"status":"app-server","addon":"app-server"}}""");
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
            return Json("""{"retail":[{"id":123,"name":"Aegwynn","region":"US"}],"bcc":[{"id":456,"name":"Amnennar-Alliance","region":"EU"}],"success":true}""");

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
    public bool IsAuthenticated => true;
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
