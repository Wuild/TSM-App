using Avalonia.Media.Imaging;
using TSM.Infrastructure;
using TSM.Models;
using TSM.Services;

namespace TSM.ViewModels;

public sealed class AccountingItemViewModel : ObservableObject, IDisposable
{
    private Bitmap? _icon;
    private string _itemName;
    private string _qualityColor = "#52575C";

    public AccountingItemViewModel(AccountingItemSummary item, string flavor)
    {
        Item = item;
        Flavor = flavor;
        _itemName = item.ItemName;
    }

    private AccountingItemSummary Item { get; }
    public string Flavor { get; }
    public string ItemString => Item.ItemString;
    public string ItemName { get => _itemName; private set => SetProperty(ref _itemName, value); }
    public long Earned => Item.Earned;
    public long Spent => Item.Spent;
    public long Profit => Item.Profit;
    public AuctionMoneyParts EarnedParts => Item.EarnedParts;
    public AuctionMoneyParts SpentParts => Item.SpentParts;
    public AuctionMoneyParts ProfitParts => Item.ProfitParts;
    public string ProfitSign => Item.ProfitSign;
    public string ProfitColor => Item.ProfitColor;
    public Bitmap? Icon { get => _icon; private set => SetProperty(ref _icon, value); }
    public string QualityColor { get => _qualityColor; private set => SetProperty(ref _qualityColor, value); }

    public void SetIcon(ItemIconPreviewResult result, Bitmap icon)
    {
        Icon?.Dispose();
        Icon = icon;
        if (!string.IsNullOrWhiteSpace(result.Name))
            ItemName = result.Name;
        QualityColor = result.Quality switch
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
    }

    public void UnloadIcon()
    {
        var icon = Icon;
        Icon = null;
        icon?.Dispose();
    }

    public void Dispose() => UnloadIcon();
}
