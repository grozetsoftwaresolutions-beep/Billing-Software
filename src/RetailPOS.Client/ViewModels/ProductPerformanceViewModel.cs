using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Reports;

namespace RetailPOS.Client.ViewModels;

public sealed partial class ProductPerformanceLineItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial Guid ProductId { get; set; }

    [ObservableProperty]
    public partial string Sku { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProductName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CategoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UnitOfMeasure { get; set; } = string.Empty;

    [ObservableProperty]
    public partial decimal QuantitySold { get; set; }

    [ObservableProperty]
    public partial decimal QuantityRefunded { get; set; }

    [ObservableProperty]
    public partial decimal NetQuantitySold { get; set; }

    [ObservableProperty]
    public partial long GrossSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long LineDiscountsPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedLineDiscounts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long RefundAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedRefundAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedNetSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long CostOfGoodsSoldPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedCostOfGoodsSold { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long EstimatedGrossProfitPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedEstimatedGrossProfit { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial decimal MarginPercentage { get; set; }

    [ObservableProperty]
    public partial string FormattedMargin { get; set; } = "0.0%";
}

public sealed partial class ProductPerformanceViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int TotalProductsCount { get; set; }

    [ObservableProperty]
    public partial decimal TotalQuantitySold { get; set; }

    [ObservableProperty]
    public partial decimal TotalQuantityRefunded { get; set; }

    [ObservableProperty]
    public partial decimal TotalNetQuantitySold { get; set; }

    [ObservableProperty]
    public partial long TotalGrossSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalLineDiscountsPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalLineDiscounts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalRefundAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalRefundAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalNetSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalNetSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalCostOfGoodsSoldPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalCostOfGoodsSold { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalEstimatedGrossProfitPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalEstimatedGrossProfit { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial decimal OverallMarginPercentage { get; set; }

    [ObservableProperty]
    public partial string FormattedOverallMargin { get; set; } = "0.0%";

    public ObservableCollection<ProductPerformanceLineItemViewModel> Items { get; } = new();

    public void PopulateFromDto(ProductPerformanceReportDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        TotalProductsCount = dto.TotalProductsCount;
        TotalQuantitySold = dto.TotalQuantitySold;
        TotalQuantityRefunded = dto.TotalQuantityRefunded;
        TotalNetQuantitySold = dto.TotalNetQuantitySold;

        TotalGrossSalesPaise = dto.TotalGrossSalesPaise;
        FormattedTotalGrossSales = FormatCurrency(dto.TotalGrossSalesPaise);

        TotalLineDiscountsPaise = dto.TotalLineDiscountsPaise;
        FormattedTotalLineDiscounts = FormatCurrency(dto.TotalLineDiscountsPaise);

        TotalRefundAmountPaise = dto.TotalRefundAmountPaise;
        FormattedTotalRefundAmount = FormatCurrency(dto.TotalRefundAmountPaise);

        TotalNetSalesPaise = dto.TotalNetSalesPaise;
        FormattedTotalNetSales = FormatCurrency(dto.TotalNetSalesPaise);

        TotalCostOfGoodsSoldPaise = dto.TotalCostOfGoodsSoldPaise;
        FormattedTotalCostOfGoodsSold = FormatCurrency(dto.TotalCostOfGoodsSoldPaise);

        TotalEstimatedGrossProfitPaise = dto.TotalEstimatedGrossProfitPaise;
        FormattedTotalEstimatedGrossProfit = FormatCurrency(dto.TotalEstimatedGrossProfitPaise);

        OverallMarginPercentage = dto.OverallMarginPercentage;
        FormattedOverallMargin = string.Format(CultureInfo.InvariantCulture, "{0:F1}%", dto.OverallMarginPercentage);

        Items.Clear();
        if (dto.Items?.Items != null)
        {
            foreach (var item in dto.Items.Items)
            {
                Items.Add(new ProductPerformanceLineItemViewModel
                {
                    ProductId = item.ProductId,
                    Sku = item.Sku ?? string.Empty,
                    ProductName = item.ProductName ?? string.Empty,
                    CategoryName = item.CategoryName ?? string.Empty,
                    UnitOfMeasure = item.UnitOfMeasure ?? string.Empty,
                    QuantitySold = item.QuantitySold,
                    QuantityRefunded = item.QuantityRefunded,
                    NetQuantitySold = item.NetQuantitySold,
                    GrossSalesPaise = item.GrossSalesPaise,
                    FormattedGrossSales = FormatCurrency(item.GrossSalesPaise),
                    LineDiscountsPaise = item.LineDiscountsPaise,
                    FormattedLineDiscounts = FormatCurrency(item.LineDiscountsPaise),
                    RefundAmountPaise = item.RefundAmountPaise,
                    FormattedRefundAmount = FormatCurrency(item.RefundAmountPaise),
                    NetSalesPaise = item.NetSalesPaise,
                    FormattedNetSales = FormatCurrency(item.NetSalesPaise),
                    CostOfGoodsSoldPaise = item.CostOfGoodsSoldPaise,
                    FormattedCostOfGoodsSold = FormatCurrency(item.CostOfGoodsSoldPaise),
                    EstimatedGrossProfitPaise = item.EstimatedGrossProfitPaise,
                    FormattedEstimatedGrossProfit = FormatCurrency(item.EstimatedGrossProfitPaise),
                    MarginPercentage = item.MarginPercentage,
                    FormattedMargin = string.Format(CultureInfo.InvariantCulture, "{0:F1}%", item.MarginPercentage)
                });
            }
        }
    }

    public static string FormatCurrency(long paise)
    {
        if (paise < 0)
        {
            return string.Format(CultureInfo.InvariantCulture, "-₹{0:N2}", Math.Abs(paise) / 100.0m);
        }
        return string.Format(CultureInfo.InvariantCulture, "₹{0:N2}", paise / 100.0m);
    }
}
