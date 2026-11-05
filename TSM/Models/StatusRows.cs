namespace TSM.Models;

public sealed record RealmStatusRow(
    string RegionRealm,
    string AuctionDb,
    string GreatDeals,
    string LastUpdated,
    string StatusColor,
    string GameVersion = "",
    string RealmId = "",
    bool IsManaged = false,
    string Flavor = "Unknown",
    string InstallationStatus = "Not detected",
    string InstallationColor = "#96999D",
    string MarketRegion = "",
    string MarketKey = "");
public sealed record AddonStatusRow(
    string Name,
    string Version,
    string Status,
    string StatusColor,
    string AddonName = "",
    string ClientName = "",
    string AddOnsPath = "",
    bool IsMissing = false)
{
    public string DisplayName => AddonName.Length > 0 ? AddonName : Name;
    private bool HasAddonPath => AddonName.Length > 0 && ClientName.Length > 0 && AddOnsPath.Length > 0;
    public bool CanManage => HasAddonPath && !IsMissing;
    public bool CanInstall => HasAddonPath && IsMissing;
}
public sealed record WowInstallation(string Name, string RootPath, string AddOnsPath, string SavedVariablesRoot);
