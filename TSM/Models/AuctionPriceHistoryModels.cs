namespace TSM.Models;

public sealed record AuctionPriceHistoryPoint(
    DateTimeOffset Timestamp,
    long? MinBuyout,
    long? MarketValue);

public sealed record AuctionPriceHistorySeries(
    string Flavor,
    string ItemString,
    List<AuctionPriceHistoryPoint> Points);
