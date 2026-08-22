namespace TSM.Models;

public sealed record TsmAccount(
    string DisplayName,
    string AccountName,
    string GameVersion,
    string SavedVariablesPath,
    string AppHelperSavedVariablesPath = "",
    string AppDataPath = "");

public sealed record AccountingPeriodOption(string Label, int? Days)
{
    public override string ToString() => Label;
}

public sealed record AccountingGoldPoint(DateTimeOffset Timestamp, long BalanceCopper);

public sealed record AccountingItemSummary(
    string ItemString,
    string ItemName,
    long Earned,
    long Spent,
    long Profit,
    long SoldQuantity,
    long PurchasedQuantity)
{
    public AuctionMoneyParts EarnedParts => AuctionMoneyParts.FromCopper(Earned);
    public AuctionMoneyParts SpentParts => AuctionMoneyParts.FromCopper(Spent);
    public AuctionMoneyParts ProfitParts => AuctionMoneyParts.FromCopper(Math.Abs(Profit));
    public string ProfitSign => Profit < 0 ? "−" : Profit > 0 ? "+" : string.Empty;
    public string ProfitColor => Profit < 0 ? "#F06464" : "#69C58F";
}

public sealed record AccountingStatistics(
    IReadOnlyList<string> Players,
    IReadOnlyList<AccountingGoldPoint> GoldHistory,
    IReadOnlyList<AccountingItemSummary> TopItems,
    long TotalEarned,
    long TotalSpent,
    long TotalProfit,
    long HighBalance,
    long LowBalance,
    long TopSale,
    long TopPurchase,
    long DailySales,
    long DailyPurchases,
    long AverageEarnedPerDay,
    long AverageSpentPerDay,
    long AverageProfitPerDay,
    string TopEarnedItem,
    string TopSpentItem,
    string TopProfitItem,
    DateTimeOffset? FirstTransaction,
    DateTimeOffset? LastTransaction)
{
    public static AccountingStatistics Empty { get; } = new([], [], [], 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, "—", "—", "—", null, null);

    public AuctionMoneyParts TotalEarnedParts => AuctionMoneyParts.FromCopper(TotalEarned);
    public AuctionMoneyParts TotalSpentParts => AuctionMoneyParts.FromCopper(TotalSpent);
    public AuctionMoneyParts TotalProfitParts => AuctionMoneyParts.FromCopper(Math.Abs(TotalProfit));
    public AuctionMoneyParts HighBalanceParts => AuctionMoneyParts.FromCopper(HighBalance);
    public AuctionMoneyParts LowBalanceParts => AuctionMoneyParts.FromCopper(LowBalance);
    public AuctionMoneyParts TopSaleParts => AuctionMoneyParts.FromCopper(TopSale);
    public AuctionMoneyParts TopPurchaseParts => AuctionMoneyParts.FromCopper(TopPurchase);
    public AuctionMoneyParts AverageEarnedParts => AuctionMoneyParts.FromCopper(AverageEarnedPerDay);
    public AuctionMoneyParts AverageSpentParts => AuctionMoneyParts.FromCopper(AverageSpentPerDay);
    public AuctionMoneyParts AverageProfitParts => AuctionMoneyParts.FromCopper(Math.Abs(AverageProfitPerDay));
    public string ProfitSign => TotalProfit < 0 ? "−" : TotalProfit > 0 ? "+" : string.Empty;
    public string AverageProfitSign => AverageProfitPerDay < 0 ? "−" : AverageProfitPerDay > 0 ? "+" : string.Empty;
    public string ProfitColor => TotalProfit < 0 ? "#F06464" : "#69C58F";
    public string DateRange => FirstTransaction is null || LastTransaction is null
        ? "No transactions in this period"
        : $"{FirstTransaction.Value.ToLocalTime():d MMM yyyy} – {LastTransaction.Value.ToLocalTime():d MMM yyyy}";
}
