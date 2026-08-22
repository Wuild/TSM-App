namespace TSM.Services;

public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

        DataDirectory = dataDirectory ?? Path.Combine(localData, "TSM-CrossPlatform");
        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        AuctionPriceHistoryFile = Path.Combine(DataDirectory, "auction-price-history.json");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        ExportDirectory = Directory.Exists(desktop) ? desktop : Path.Combine(DataDirectory, "Exports");
        ItemIconDirectory = Path.Combine(DataDirectory, "ItemIcons");
    }

    public string DataDirectory { get; }
    public string SettingsFile { get; }
    public string AuctionPriceHistoryFile { get; }
    public string ExportDirectory { get; }
    public string ItemIconDirectory { get; }
}
