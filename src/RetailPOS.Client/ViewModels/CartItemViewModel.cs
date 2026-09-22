using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class CartItemViewModel : ObservableObject
{
    private static readonly CultureInfo IndianCulture = CultureInfo.GetCultureInfo("en-IN");

    public Guid ProductId { get; }

    [ObservableProperty]
    public partial string Sku { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProductName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UnitCode { get; set; } = "PCS";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedUnitPrice))]
    public partial long UnitSellingPricePaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedQuantity))]
    public partial decimal Quantity { get; set; } = 1m;

    [ObservableProperty]
    public partial DiscountType DiscountType { get; set; } = DiscountType.None;

    [ObservableProperty]
    public partial decimal DiscountValue { get; set; } = 0m;

    [ObservableProperty]
    public partial long LineGrossPaise { get; set; }

    [ObservableProperty]
    public partial long LineDiscountPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedTax))]
    public partial long LineTaxPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormattedLineTotal))]
    public partial long LineTotalPaise { get; set; }

    [ObservableProperty]
    public partial bool InStock { get; set; } = true;

    public string FormattedUnitPrice => $"₹{(UnitSellingPricePaise / 100.0m).ToString("N2", IndianCulture)}";

    public string FormattedLineTotal => $"₹{(LineTotalPaise / 100.0m).ToString("N2", IndianCulture)}";

    public string FormattedTax => $"₹{(LineTaxPaise / 100.0m).ToString("N2", IndianCulture)}";

    public string FormattedQuantity => Quantity.ToString("0.###", CultureInfo.InvariantCulture);

    public CartItemViewModel(
        Guid productId,
        string sku,
        string productName,
        string unitCode,
        long unitSellingPricePaise,
        decimal quantity = 1m,
        bool inStock = true)
    {
        ProductId = productId;
        Sku = sku;
        ProductName = productName;
        UnitCode = unitCode;
        UnitSellingPricePaise = unitSellingPricePaise;
        Quantity = quantity;
        InStock = inStock;

        // Initial default line total before server calculation
        LineTotalPaise = (long)(unitSellingPricePaise * quantity);
        LineGrossPaise = LineTotalPaise;
    }
}
