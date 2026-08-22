namespace TSM.Models;

public sealed record AuctionMoneyParts(string Empty, string Gold, string Silver, string Copper)
{
    public static AuctionMoneyParts FromCopper(long? value)
    {
        if (value is null or <= 0)
            return new AuctionMoneyParts("—", string.Empty, string.Empty, string.Empty);

        var gold = value.Value / 10_000;
        var silver = value.Value / 100 % 100;
        var copper = value.Value % 100;
        return new AuctionMoneyParts(
            string.Empty,
            gold > 0 ? $"{gold:N0}g " : string.Empty,
            gold > 0 ? $"{silver:00}s " : silver > 0 ? $"{silver}s " : string.Empty,
            gold > 0 || silver > 0 ? $"{copper:00}c" : $"{copper}c");
    }
}

public sealed record AuctionHouseItemRow(
    string ItemString,
    string ItemName,
    string Realm,
    string Flavor,
    long? MinBuyout,
    long? NumAuctions,
    long? MarketValueRecent,
    long? Historical,
    DateTimeOffset UpdatedAt,
    long? RegionSaleAverage = null,
    long? RegionSoldPerDay = null,
    long? RegionSaleRatePercent = null)
{
    public string MinBuyoutDisplay => FormatMoney(MinBuyout);
    public string MarketValueDisplay => FormatMoney(MarketValueRecent);
    public string HistoricalDisplay => FormatMoney(Historical);
    public AuctionMoneyParts MinBuyoutParts => AuctionMoneyParts.FromCopper(MinBuyout);
    public AuctionMoneyParts MarketValueParts => AuctionMoneyParts.FromCopper(MarketValueRecent);
    public AuctionMoneyParts HistoricalParts => AuctionMoneyParts.FromCopper(Historical);
    public AuctionMoneyParts RegionSaleAverageParts => AuctionMoneyParts.FromCopper(RegionSaleAverage);
    public string AuctionsDisplay => NumAuctions?.ToString("N0") ?? "—";
    public string RegionSoldPerDayDisplay => RegionSoldPerDay is > 0
        ? $"{RegionSoldPerDay.Value / 1000m:0.###}/day"
        : "—";
    public string RegionSaleRateDisplay => RegionSaleRatePercent is > 0
        ? $"{RegionSaleRatePercent.Value / 10m:0.#}%"
        : "—";
    public string BaseItemString
    {
        get
        {
            if (!ItemString.StartsWith("i:", StringComparison.OrdinalIgnoreCase))
                return ItemString;
            var variantSeparator = ItemString.IndexOf(':', 2);
            return variantSeparator > 2 ? ItemString[..variantSeparator] : ItemString;
        }
    }
    public string BaseRealm => Realm.EndsWith("-Alliance", StringComparison.OrdinalIgnoreCase)
        ? Realm[..^"-Alliance".Length]
        : Realm.EndsWith("-Horde", StringComparison.OrdinalIgnoreCase)
            ? Realm[..^"-Horde".Length]
            : Realm;
    public string Faction => Realm.EndsWith("-Alliance", StringComparison.OrdinalIgnoreCase)
        ? "Alliance"
        : Realm.EndsWith("-Horde", StringComparison.OrdinalIgnoreCase)
            ? "Horde"
            : "Both";
    public string RealmFactionDisplay => Faction == "Both" ? BaseRealm : $"{BaseRealm} · {Faction}";
    public string UpdatedDisplay => UpdatedAt == DateTimeOffset.MinValue
        ? "Unknown"
        : UpdatedAt.ToLocalTime().ToString("g");

    private static string FormatMoney(long? copper)
    {
        if (copper is null or <= 0)
            return "—";
        var gold = copper.Value / 10_000;
        var silver = copper.Value / 100 % 100;
        var remainder = copper.Value % 100;
        return gold > 0 ? $"{gold:N0}g {silver:00}s {remainder:00}c" : $"{silver} s {remainder:00} c";
    }
}
