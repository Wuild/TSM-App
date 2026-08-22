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
    string InstallationColor = "#96999D");
public sealed record AddonStatusRow(
    string Name,
    string Version,
    string Status,
    string StatusColor,
    string AddonName = "",
    string ClientName = "",
    string AddOnsPath = "")
{
    public string DisplayName => AddonName.Length > 0 ? AddonName : Name;
    public bool CanManage => AddonName.Length > 0 && ClientName.Length > 0 && AddOnsPath.Length > 0;
}
public sealed record WowInstallation(string Name, string RootPath, string AddOnsPath, string SavedVariablesRoot);
