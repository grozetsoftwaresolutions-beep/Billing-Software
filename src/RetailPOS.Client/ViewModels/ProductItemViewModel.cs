using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Catalog;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ProductItemViewModel : ObservableObject
{
    private static readonly CultureInfo IndianCulture = CultureInfo.GetCultureInfo("en-IN");

    public ProductManagementDto Dto { get; }

    public Guid Id => Dto.Id;
    public string Sku => Dto.Sku;
    public string Name => Dto.Name;
    public int? CategoryId => Dto.CategoryId;
    public string CategoryName => Dto.CategoryName ?? "Uncategorized";
    public int? BrandId => Dto.BrandId;
    public string BrandName => Dto.BrandName ?? "Generic";
    public string UnitCode => Dto.UnitCode;
    public long CostPricePaise => Dto.CostPricePaise;
    public long SellingPricePaise => Dto.SellingPricePaise;
    public int TaxCategoryId => Dto.TaxCategoryId;
    public bool IsActive => Dto.IsActive;
    public string? Notes => Dto.Notes;
    public decimal CurrentStock => Dto.CurrentStock;
    public decimal ReorderLevel => Dto.ReorderLevel;
    public bool IsLowStock => Dto.IsLowStock;

    public string FormattedSellingPrice => FormatCurrency(SellingPricePaise);
    public string FormattedCostPrice => FormatCurrency(CostPricePaise);

    public static string FormatCurrency(long paise) => $"₹{(paise / 100.0m).ToString("N2", IndianCulture)}";

    public string FormattedCurrentStock => FormatQuantity(CurrentStock, UnitCode);
    public string FormattedReorderLevel => FormatQuantity(ReorderLevel, UnitCode);

    public string StatusText => IsActive ? "Active" : "Inactive";
    public string StatusColor => IsActive ? "#16A34A" : "#94A3B8";

    public ProductItemViewModel(ProductManagementDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Dto = dto;
    }

    public static string FormatQuantity(decimal quantity, string? unitCode)
    {
        var normalizedUnit = unitCode?.Trim().ToUpperInvariant() ?? "PC";
        return normalizedUnit switch
        {
            "PC" or "G" or "ML" => $"{quantity:0} {normalizedUnit}",
            "KG" or "L" => $"{quantity:0.###} {normalizedUnit}",
            _ => $"{quantity:0.###} {normalizedUnit}"
        };
    }
}
