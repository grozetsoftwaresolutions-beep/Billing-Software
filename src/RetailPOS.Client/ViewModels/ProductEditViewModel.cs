using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Catalog;

namespace RetailPOS.Client.ViewModels;

public sealed partial class TaxCategoryOption
{
    public int Id { get; }
    public string DisplayName { get; }

    public TaxCategoryOption(int id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }
}

public sealed partial class ProductEditViewModel : ObservableObject
{
    private static readonly Regex SkuPattern = new(@"^[A-Z0-9_-]+$", RegexOptions.Compiled);

    public static readonly IReadOnlyList<string> SupportedUnits = ["PC", "KG", "G", "L", "ML"];

    public static readonly IReadOnlyList<TaxCategoryOption> SupportedTaxCategories =
    [
        new(1, "GST 0% (GST_0)"),
        new(2, "GST 5% (GST_5)"),
        new(3, "GST 18% (GST_18)"),
        new(4, "Tax Exempt (EXEMPT)")
    ];

    [ObservableProperty]
    public partial bool IsCreateMode { get; set; } = true;

    [ObservableProperty]
    public partial Guid Id { get; set; } = Guid.Empty;

    [ObservableProperty]
    public partial string Sku { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedUnitCode { get; set; } = "PC";

    [ObservableProperty]
    public partial bool IsUnitEditable { get; set; } = true;

    [ObservableProperty]
    public partial string UnitImmutabilityReason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CostPriceText { get; set; } = "0.00";

    [ObservableProperty]
    public partial string SellingPriceText { get; set; } = "0.00";

    [ObservableProperty]
    public partial int SelectedTaxCategoryId { get; set; } = 1;

    [ObservableProperty]
    public partial int? SelectedCategoryId { get; set; }

    [ObservableProperty]
    public partial int? SelectedBrandId { get; set; }

    [ObservableProperty]
    public partial string PrimaryBarcode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InitialStockText { get; set; } = "0";

    [ObservableProperty]
    public partial string ReorderLevelText { get; set; } = "5";

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewBarcodeInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NewBarcodeIsPrimary { get; set; }

    public ObservableCollection<ProductBarcodeDto> Barcodes { get; } = [];

    public ProductEditViewModel()
    {
    }

    public void InitializeForCreate()
    {
        IsCreateMode = true;
        Id = Guid.Empty;
        Sku = string.Empty;
        Name = string.Empty;
        SelectedUnitCode = "PC";
        IsUnitEditable = true;
        UnitImmutabilityReason = string.Empty;
        CostPriceText = "0.00";
        SellingPriceText = "0.00";
        SelectedTaxCategoryId = 1;
        SelectedCategoryId = null;
        SelectedBrandId = null;
        PrimaryBarcode = string.Empty;
        InitialStockText = "0";
        ReorderLevelText = "5";
        Notes = string.Empty;
        ErrorMessage = string.Empty;
        Barcodes.Clear();
    }

    public void InitializeForEdit(ProductManagementDto dto, bool hasTransactionHistory = false, bool historyLookupFailed = false)
    {
        ArgumentNullException.ThrowIfNull(dto);

        IsCreateMode = false;
        Id = dto.Id;
        Sku = dto.Sku;
        Name = dto.Name;
        SelectedUnitCode = dto.UnitCode;
        CostPriceText = (dto.CostPricePaise / 100.0m).ToString("0.00", CultureInfo.InvariantCulture);
        SellingPriceText = (dto.SellingPricePaise / 100.0m).ToString("0.00", CultureInfo.InvariantCulture);
        SelectedTaxCategoryId = dto.TaxCategoryId;
        SelectedCategoryId = dto.CategoryId;
        SelectedBrandId = dto.BrandId;
        ReorderLevelText = ProductItemViewModel.FormatQuantity(dto.ReorderLevel, dto.UnitCode).Replace($" {dto.UnitCode}", "");
        Notes = dto.Notes ?? string.Empty;
        ErrorMessage = string.Empty;

        // Unit of Measure becomes immutable when the product has historical transaction/activity history
        // (sales, purchases, or inventory movements).
        // If the authoritative history lookup failed, fail-safe/closed: do not present Unit as safely editable.
        if (historyLookupFailed)
        {
            IsUnitEditable = false;
            UnitImmutabilityReason = "Unit of Measure cannot be modified because transaction history could not be verified from the server.";
        }
        else if (hasTransactionHistory)
        {
            IsUnitEditable = false;
            UnitImmutabilityReason = "Unit of Measure cannot be modified because historical transaction, sales, purchase, or inventory activity exists.";
        }
        else
        {
            // No historical transaction/activity confirmed
            IsUnitEditable = true;
            UnitImmutabilityReason = string.Empty;
        }

        Barcodes.Clear();
        foreach (var b in dto.Barcodes)
        {
            Barcodes.Add(b);
        }
    }

    public void InitializeForEdit(ProductManagementDto dto, bool? hasTransactionHistory)
    {
        var lookupFailed = !hasTransactionHistory.HasValue;
        var hasHistory = hasTransactionHistory.GetValueOrDefault();
        InitializeForEdit(dto, hasHistory, lookupFailed);
    }

    public static bool IsValidQuantityForUnit(decimal quantity, string? unitCode)
    {
        if (quantity < 0m) return false;
        var normalizedUnit = unitCode?.Trim().ToUpperInvariant() ?? "PC";
        return normalizedUnit switch
        {
            "PC" or "G" or "ML" => decimal.Floor(quantity) == quantity,
            "KG" or "L" => decimal.Round(quantity, 3) == quantity,
            _ => decimal.Round(quantity, 3) == quantity
        };
    }

    public bool Validate(out string error)
    {
        error = string.Empty;

        if (IsCreateMode)
        {
            var rawSku = Sku.Trim();
            if (string.IsNullOrWhiteSpace(rawSku))
            {
                error = "Product SKU cannot be empty.";
                ErrorMessage = error;
                return false;
            }

            if (rawSku.Length > 50 || !SkuPattern.IsMatch(rawSku))
            {
                error = "Product SKU must be uppercase alphanumeric, hyphen (-), or underscore (_) and at most 50 characters.";
                ErrorMessage = error;
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            error = "Product Name cannot be empty.";
            ErrorMessage = error;
            return false;
        }

        if (Name.Trim().Length > 200)
        {
            error = "Product Name cannot exceed 200 characters.";
            ErrorMessage = error;
            return false;
        }

        if (!decimal.TryParse(CostPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var costPrice) &&
            !decimal.TryParse(CostPriceText, NumberStyles.Number, CultureInfo.CurrentCulture, out costPrice))
        {
            error = "Cost price must be a valid decimal amount in INR.";
            ErrorMessage = error;
            return false;
        }

        if (costPrice < 0m || decimal.Round(costPrice, 2) != costPrice)
        {
            error = "Cost price cannot be negative and must have at most 2 decimal places.";
            ErrorMessage = error;
            return false;
        }

        if (!decimal.TryParse(SellingPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sellingPrice) &&
            !decimal.TryParse(SellingPriceText, NumberStyles.Number, CultureInfo.CurrentCulture, out sellingPrice))
        {
            error = "Selling price must be a valid decimal amount in INR.";
            ErrorMessage = error;
            return false;
        }

        if (sellingPrice < 0m || decimal.Round(sellingPrice, 2) != sellingPrice)
        {
            error = "Selling price cannot be negative and must have at most 2 decimal places.";
            ErrorMessage = error;
            return false;
        }

        if (!decimal.TryParse(ReorderLevelText, NumberStyles.Number, CultureInfo.InvariantCulture, out var reorderLevel) &&
            !decimal.TryParse(ReorderLevelText, NumberStyles.Number, CultureInfo.CurrentCulture, out reorderLevel))
        {
            error = "Reorder level must be a valid decimal number.";
            ErrorMessage = error;
            return false;
        }

        if (!IsValidQuantityForUnit(reorderLevel, SelectedUnitCode))
        {
            error = $"Reorder level precision invalid for unit {SelectedUnitCode}. Whole numbers required for PC, G, and ML; up to 3 decimals allowed for KG and L.";
            ErrorMessage = error;
            return false;
        }

        if (IsCreateMode)
        {
            if (!decimal.TryParse(InitialStockText, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStock) &&
                !decimal.TryParse(InitialStockText, NumberStyles.Number, CultureInfo.CurrentCulture, out initialStock))
            {
                error = "Initial stock must be a valid decimal number.";
                ErrorMessage = error;
                return false;
            }

            if (!IsValidQuantityForUnit(initialStock, SelectedUnitCode))
            {
                error = $"Initial stock precision invalid for unit {SelectedUnitCode}. Whole numbers required for PC, G, and ML; up to 3 decimals allowed for KG and L.";
                ErrorMessage = error;
                return false;
            }
        }

        ErrorMessage = string.Empty;
        return true;
    }

    public CreateProductRequest ToCreateRequest()
    {
        if (!decimal.TryParse(CostPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var costPrice))
            costPrice = decimal.Parse(CostPriceText, CultureInfo.CurrentCulture);

        if (!decimal.TryParse(SellingPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sellingPrice))
            sellingPrice = decimal.Parse(SellingPriceText, CultureInfo.CurrentCulture);

        if (!decimal.TryParse(ReorderLevelText, NumberStyles.Number, CultureInfo.InvariantCulture, out var reorderLevel))
            reorderLevel = decimal.Parse(ReorderLevelText, CultureInfo.CurrentCulture);

        if (!decimal.TryParse(InitialStockText, NumberStyles.Number, CultureInfo.InvariantCulture, out var initialStock))
            initialStock = decimal.Parse(InitialStockText, CultureInfo.CurrentCulture);

        return new CreateProductRequest(
            Sku: Sku.Trim().ToUpperInvariant(),
            Name: Name.Trim(),
            UnitCode: SelectedUnitCode,
            CostPricePaise: checked((long)(costPrice * 100m)),
            SellingPricePaise: checked((long)(sellingPrice * 100m)),
            TaxCategoryId: SelectedTaxCategoryId,
            CategoryId: SelectedCategoryId,
            BrandId: SelectedBrandId,
            PrimaryBarcode: string.IsNullOrWhiteSpace(PrimaryBarcode) ? null : PrimaryBarcode.Trim(),
            InitialStock: initialStock,
            ReorderLevel: reorderLevel,
            Notes: string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
    }

    public UpdateProductRequest ToUpdateRequest()
    {
        if (!decimal.TryParse(CostPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var costPrice))
            costPrice = decimal.Parse(CostPriceText, CultureInfo.CurrentCulture);

        if (!decimal.TryParse(SellingPriceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var sellingPrice))
            sellingPrice = decimal.Parse(SellingPriceText, CultureInfo.CurrentCulture);

        if (!decimal.TryParse(ReorderLevelText, NumberStyles.Number, CultureInfo.InvariantCulture, out var reorderLevel))
            reorderLevel = decimal.Parse(ReorderLevelText, CultureInfo.CurrentCulture);

        return new UpdateProductRequest(
            Name: Name.Trim(),
            UnitCode: SelectedUnitCode,
            CostPricePaise: checked((long)(costPrice * 100m)),
            SellingPricePaise: checked((long)(sellingPrice * 100m)),
            TaxCategoryId: SelectedTaxCategoryId,
            CategoryId: SelectedCategoryId,
            BrandId: SelectedBrandId,
            ReorderLevel: reorderLevel,
            Notes: string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim());
    }
}
