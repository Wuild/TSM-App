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
        ErrorLogFile = Path.Combine(DataDirectory, "errors.log");
        ItemIconDirectory = Path.Combine(DataDirectory, "ItemIcons");
    }

    public string DataDirectory { get; }
    public string SettingsFile { get; }
    public string AuctionPriceHistoryFile { get; }
    public string ErrorLogFile { get; }
    public string ItemIconDirectory { get; }
}
