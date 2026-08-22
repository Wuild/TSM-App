using Avalonia.Media.Imaging;
using TSM.Infrastructure;
using TSM.Models;
using TSM.Services;

namespace TSM.ViewModels;

public sealed class AuctionHouseItemViewModel : ObservableObject, IDisposable
{
    private readonly AuctionHouseItemRow _item;
    private readonly IReadOnlyList<AuctionHouseItemRow> _variants;
    private Bitmap? _icon;
    private string _qualityColor = "#52575C";
    private string _itemName;
    private bool _isFavorite;
    private bool _isItemInfoLoading;
    private bool _hasItemDetails;
    private string _itemInfoLoadingStatus = "Loading item details…";
    private IReadOnlyList<AuctionPriceHistoryPoint> _priceHistory;
    private string _wowheadDetails = string.Empty;
    private IReadOnlyList<ItemTooltipLine> _itemDetails =
    [
        new ItemTooltipLine("Additional Wowhead item details have not loaded.", "#8F8F8F", string.Empty,
            false, false, new AuctionMoneyParts(string.Empty, string.Empty, string.Empty, string.Empty))
    ];

    public AuctionHouseItemViewModel(AuctionHouseItemRow item, IReadOnlyList<AuctionHouseItemRow>? variants = null,
        bool isFavorite = false, IReadOnlyList<AuctionPriceHistoryPoint>? priceHistory = null)
    {
        _item = item;
        _variants = variants ?? [item];
        _itemName = item.ItemName;
        _isFavorite = isFavorite;
        _priceHistory = priceHistory ?? [];
        PriceSnapshots = BuildPriceSnapshots();
    }

    public string ItemString => _item.ItemString;
    public string ItemName { get => _itemName; private set => SetProperty(ref _itemName, value); }
    public string Realm => _item.Realm;
    public string Flavor => _item.Flavor;
    public string MinBuyoutDisplay => _item.MinBuyoutDisplay;
    public string MarketValueDisplay => _item.MarketValueDisplay;
    public string HistoricalDisplay => _item.HistoricalDisplay;
    public long? MinBuyout => _item.MinBuyout;
    public long? NumAuctions => _item.NumAuctions;
    public long? MarketValueRecent => _item.MarketValueRecent;
    public long? Historical => _item.Historical;
    public long? RegionSoldPerDay => _item.RegionSoldPerDay;
    public long? RegionSaleRatePercent => _item.RegionSaleRatePercent;
    public AuctionMoneyParts MinBuyoutParts => _item.MinBuyoutParts;
    public AuctionMoneyParts MarketValueParts => _item.MarketValueParts;
    public AuctionMoneyParts HistoricalParts => _item.HistoricalParts;
    public AuctionMoneyParts RegionSaleAverageParts => _item.RegionSaleAverageParts;
    public string AuctionsDisplay => _item.AuctionsDisplay;
    public string RegionSoldPerDayDisplay => _item.RegionSoldPerDayDisplay;
    public string RegionSaleRateDisplay => _item.RegionSaleRateDisplay;
    public string UpdatedDisplay => _item.UpdatedDisplay;
    public Uri? WowheadUri => ItemIconService.BuildItemPageUri(ItemString, Flavor);
    public Bitmap? Icon { get => _icon; private set => SetProperty(ref _icon, value); }
    public string QualityColor { get => _qualityColor; private set => SetProperty(ref _qualityColor, value); }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (!SetProperty(ref _isFavorite, value))
                return;
            OnPropertyChanged(nameof(FavoriteColor));
            OnPropertyChanged(nameof(FavoriteToolTip));
            OnPropertyChanged(nameof(PriceHistoryStatus));
        }
    }
    public string FavoriteColor => IsFavorite ? "#F2A93B" : "#737A81";
    public string FavoriteToolTip => IsFavorite ? "Remove from favorites" : "Add to favorites";
    public bool IsItemInfoLoading
    {
        get => _isItemInfoLoading;
        private set => SetProperty(ref _isItemInfoLoading, value);
    }
    public string ItemInfoLoadingStatus
    {
        get => _itemInfoLoadingStatus;
        private set => SetProperty(ref _itemInfoLoadingStatus, value);
    }
    public bool HasItemDetails => _hasItemDetails;
    public IReadOnlyList<AuctionPriceHistoryPoint> PriceHistory => _priceHistory;
    public bool HasPriceHistory => PriceHistory.Count > 0;
    public bool HasPriceChart => PriceHistory.Count > 1;
    public AuctionMoneyParts PriceHistoryLowParts => AuctionMoneyParts.FromCopper(PriceHistoryExtrema(false));
    public AuctionMoneyParts PriceHistoryHighParts => AuctionMoneyParts.FromCopper(PriceHistoryExtrema(true));
    public string PriceHistoryRange => PriceHistory.Count switch
    {
        0 => string.Empty,
        1 => PriceHistory[0].Timestamp.ToLocalTime().ToString("g"),
        _ => $"{PriceHistory[0].Timestamp.ToLocalTime():d MMM} – {PriceHistory[^1].Timestamp.ToLocalTime():d MMM}"
    };
    public string PriceHistoryStatus => PriceHistory.Count switch
    {
        > 1 => $"{PriceHistory.Count:N0} synced snapshots · minimum buyout across configured realms",
        1 when IsFavorite => "First snapshot saved · the chart grows after future realm syncs",
        1 => "One previously tracked snapshot",
        _ when IsFavorite => "Waiting for the next AuctionDB snapshot",
        _ => "Favorite this item to start tracking its price"
    };
    public IReadOnlyList<ItemTooltipLine> ItemDetails => _itemDetails;
    public string PriceSnapshotsText => BuildVariantTooltip();
    public IReadOnlyList<AuctionPriceSnapshotViewModel> PriceSnapshots { get; }
    public string AdditionalSnapshotsText => _variants.Count > 12
        ? $"…and {_variants.Count - 12:N0} more snapshots"
        : string.Empty;
    public string TooltipText => string.IsNullOrWhiteSpace(_wowheadDetails)
        ? $"{ItemName}\n{ItemString}\n\n{BuildVariantTooltip()}"
        : $"{_wowheadDetails}\n\n{BuildVariantTooltip()}";

    private string BuildVariantTooltip()
    {
        var lines = new List<string> { "Local AuctionDB price snapshots" };
        var hasMultipleFlavors = _variants.Select(value => value.Flavor)
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        var hasItemVariants = _variants.Select(value => value.ItemString)
            .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
        foreach (var variant in _variants.Take(12))
        {
            var context = hasMultipleFlavors
                ? $"{variant.Flavor} · {variant.RealmFactionDisplay}"
                : variant.RealmFactionDisplay;
            if (hasItemVariants)
                context += $" · {variant.ItemString}";
            lines.Add($"\n{context}");
            lines.Add($"Min buyout: {variant.MinBuyoutDisplay} · Recent: {variant.MarketValueDisplay}");
            lines.Add($"Historical: {variant.HistoricalDisplay} · Auctions: {variant.AuctionsDisplay}");
        }
        if (_variants.Count > 12)
            lines.Add($"\n…and {_variants.Count - 12:N0} more snapshots");
        return string.Join('\n', lines);
    }

    private IReadOnlyList<AuctionPriceSnapshotViewModel> BuildPriceSnapshots()
    {
        return _variants.Take(12).Select(variant =>
        {
            return new AuctionPriceSnapshotViewModel(
                variant.Faction,
                variant.Faction switch
                {
                    "Alliance" => "#4A9EFF",
                    "Horde" => "#F06464",
                    _ => "#D9A441"
                },
                variant.MinBuyoutParts,
                variant.MarketValueParts,
                variant.HistoricalParts,
                variant.AuctionsDisplay);
        }).ToArray();
    }

    public void SetIcon(ItemIconResult result, Bitmap icon)
    {
        Icon?.Dispose();
        Icon = icon;
        if ((ItemName == ItemString || string.IsNullOrWhiteSpace(ItemName)) && !string.IsNullOrWhiteSpace(result.Name))
            ItemName = result.Name;
        _wowheadDetails = result.TooltipText;
        _hasItemDetails = true;
        if (result.TooltipLines.Count > 0)
            _itemDetails = result.TooltipLines;
        QualityColor = QualityColorFor(result.Quality);
        OnPropertyChanged(nameof(ItemDetails));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(HasItemDetails));
    }

    public void SetPreviewIcon(Bitmap icon, BundledItemSummary? item = null)
    {
        Icon?.Dispose();
        Icon = icon;
        if (item is not null)
            SetBundledData(item);
    }

    public void SetPreviewIcon(ItemIconPreviewResult result, Bitmap icon)
    {
        Icon?.Dispose();
        Icon = icon;
        if ((ItemName == ItemString || string.IsNullOrWhiteSpace(ItemName)) && !string.IsNullOrWhiteSpace(result.Name))
            ItemName = result.Name;
        if (result.Quality >= 0)
            QualityColor = QualityColorFor(result.Quality);
    }

    private void SetBundledData(BundledItemSummary item)
    {
        if ((ItemName == ItemString || string.IsNullOrWhiteSpace(ItemName)) && !string.IsNullOrWhiteSpace(item.Name))
            ItemName = item.Name;
        QualityColor = QualityColorFor(item.Quality);
    }

    private static string QualityColorFor(int quality) => quality switch
    {
        0 => "#9D9D9D",
        1 => "#FFFFFF",
        2 => "#1EFF00",
        3 => "#0070DD",
        4 => "#A335EE",
        5 => "#FF8000",
        6 => "#E6CC80",
        7 => "#00CCFF",
        _ => "#52575C"
    };

    public void UnloadIcon()
    {
        var icon = Icon;
        Icon = null;
        icon?.Dispose();
    }

    public void SetItemInfoLoading(bool isLoading, string status = "Loading item details…")
    {
        ItemInfoLoadingStatus = status;
        IsItemInfoLoading = isLoading;
    }

    public void SetPriceHistory(IReadOnlyList<AuctionPriceHistoryPoint> history)
    {
        _priceHistory = history;
        OnPropertyChanged(nameof(PriceHistory));
        OnPropertyChanged(nameof(HasPriceHistory));
        OnPropertyChanged(nameof(HasPriceChart));
        OnPropertyChanged(nameof(PriceHistoryLowParts));
        OnPropertyChanged(nameof(PriceHistoryHighParts));
        OnPropertyChanged(nameof(PriceHistoryRange));
        OnPropertyChanged(nameof(PriceHistoryStatus));
    }

    private long? PriceHistoryExtrema(bool maximum)
    {
        var prices = PriceHistory.Where(point => point.MinBuyout is > 0)
            .Select(point => point.MinBuyout!.Value).ToArray();
        return prices.Length == 0 ? null : maximum ? prices.Max() : prices.Min();
    }

    public void Dispose() => UnloadIcon();
}

public sealed record AuctionPriceSnapshotViewModel(
    string Faction,
    string FactionColor,
    AuctionMoneyParts MinBuyout,
    AuctionMoneyParts Recent,
    AuctionMoneyParts Historical,
    string AuctionsDisplay);
