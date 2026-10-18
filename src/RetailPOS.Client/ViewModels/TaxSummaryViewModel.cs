using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Reports;

namespace RetailPOS.Client.ViewModels;

public sealed partial class TaxCategoryItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int TaxCategoryId { get; set; }

    [ObservableProperty]
    public partial string CategoryName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long TaxableAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTaxableAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TaxAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTaxAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long RefundTaxAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedRefundTaxAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetTaxAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedNetTaxAmount { get; set; } = "₹0.00";
}

public sealed partial class TaxComponentItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string ComponentName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial decimal RatePercentage { get; set; }

    [ObservableProperty]
    public partial string FormattedRate { get; set; } = "0%";

    [ObservableProperty]
    public partial long TaxCollectedPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTaxCollected { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TaxRefundedPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTaxRefunded { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetTaxPayablePaise { get; set; }

    [ObservableProperty]
    public partial string FormattedNetTaxPayable { get; set; } = "₹0.00";
}

public sealed partial class TaxSummaryViewModel : ObservableObject
{
    [ObservableProperty]
    public partial long B2BTaxableSubtotalPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedB2BTaxable { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long B2CTaxableSubtotalPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedB2CTaxable { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalTaxableSubtotalPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalTaxable { get; set; } = "₹0.00";
    [ObservableProperty]
    public partial string FormattedTotalTaxableSubtotal { get; set; } = "₹0.00";
    [ObservableProperty]
    public partial string FormattedB2BTaxableSubtotal { get; set; } = "₹0.00";
    [ObservableProperty]
    public partial string FormattedB2CTaxableSubtotal { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalTaxCollectedPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalTaxCollected { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalTaxRefundedPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalTaxRefunded { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetTaxPayablePaise { get; set; }
    [ObservableProperty]
    public partial string FormattedNetTaxPayable { get; set; } = "₹0.00";

    public ObservableCollection<TaxCategoryItemViewModel> Categories { get; } = new();
    public ObservableCollection<TaxComponentItemViewModel> Components { get; } = new();

    public void PopulateFromDto(TaxSummaryReportDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        B2BTaxableSubtotalPaise = dto.B2BTaxableSubtotalPaise;
        FormattedB2BTaxable = FormatCurrency(dto.B2BTaxableSubtotalPaise);

        B2CTaxableSubtotalPaise = dto.B2CTaxableSubtotalPaise;
        FormattedB2CTaxable = FormatCurrency(dto.B2CTaxableSubtotalPaise);

        TotalTaxableSubtotalPaise = dto.TotalTaxableSubtotalPaise;
        FormattedTotalTaxable = FormatCurrency(dto.TotalTaxableSubtotalPaise);
        FormattedTotalTaxableSubtotal = FormattedTotalTaxable;
        FormattedB2BTaxableSubtotal = FormattedB2BTaxable;
        FormattedB2CTaxableSubtotal = FormattedB2CTaxable;

        TotalTaxCollectedPaise = dto.TotalTaxCollectedPaise;
        FormattedTotalTaxCollected = FormatCurrency(dto.TotalTaxCollectedPaise);

        TotalTaxRefundedPaise = dto.TotalTaxRefundedPaise;
        FormattedTotalTaxRefunded = FormatCurrency(dto.TotalTaxRefundedPaise);

        NetTaxPayablePaise = dto.NetTaxPayablePaise;
        FormattedNetTaxPayable = FormatCurrency(dto.NetTaxPayablePaise);

        Categories.Clear();
        if (dto.Categories != null)
        {
            foreach (var cat in dto.Categories)
            {
                Categories.Add(new TaxCategoryItemViewModel
                {
                    TaxCategoryId = cat.TaxCategoryId,
                    CategoryName = cat.CategoryName,
                    TaxableAmountPaise = cat.TaxableAmountPaise,
                    FormattedTaxableAmount = FormatCurrency(cat.TaxableAmountPaise),
                    TaxAmountPaise = cat.TaxAmountPaise,
                    FormattedTaxAmount = FormatCurrency(cat.TaxAmountPaise),
                    RefundTaxAmountPaise = cat.RefundTaxAmountPaise,
                    FormattedRefundTaxAmount = FormatCurrency(cat.RefundTaxAmountPaise),
                    NetTaxAmountPaise = cat.NetTaxAmountPaise,
                    FormattedNetTaxAmount = FormatCurrency(cat.NetTaxAmountPaise)
                });
            }
        }

        Components.Clear();
        if (dto.Components != null)
        {
            foreach (var cmp in dto.Components)
            {
                Components.Add(new TaxComponentItemViewModel
                {
                    ComponentName = cmp.ComponentName,
                    RatePercentage = cmp.RatePercentage,
                    FormattedRate = string.Format(CultureInfo.InvariantCulture, "{0:G}%", cmp.RatePercentage),
                    TaxCollectedPaise = cmp.TaxCollectedPaise,
                    FormattedTaxCollected = FormatCurrency(cmp.TaxCollectedPaise),
                    TaxRefundedPaise = cmp.TaxRefundedPaise,
                    FormattedTaxRefunded = FormatCurrency(cmp.TaxRefundedPaise),
                    NetTaxPayablePaise = cmp.NetTaxPayablePaise,
                    FormattedNetTaxPayable = FormatCurrency(cmp.NetTaxPayablePaise)
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
