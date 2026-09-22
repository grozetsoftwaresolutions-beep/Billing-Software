using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Catalog;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ProductSearchResultViewModel : ObservableObject
{
    private static readonly CultureInfo IndianCulture = CultureInfo.GetCultureInfo("en-IN");

    public Guid Id { get; }
    public string Sku { get; }
    public string Name { get; }
    public string UnitCode { get; }
    public long SellingPricePaise { get; }
    public int TaxCategoryId { get; }
    public string? PrimaryBarcode { get; }
    public bool InStock { get; }
    public bool IsActive { get; }

    public string FormattedPrice => $"₹{(SellingPricePaise / 100.0m).ToString("N2", IndianCulture)}";

    public ProductSearchResultViewModel(ProductPosDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        Sku = dto.Sku;
        Name = dto.Name;
        UnitCode = dto.UnitCode;
        SellingPricePaise = dto.SellingPricePaise;
        TaxCategoryId = dto.TaxCategoryId;
        PrimaryBarcode = dto.PrimaryBarcode;
        InStock = dto.InStock;
        IsActive = dto.IsActive;
    }
}
