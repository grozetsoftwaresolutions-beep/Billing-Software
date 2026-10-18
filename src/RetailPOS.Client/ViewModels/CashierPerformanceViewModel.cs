using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Reports;

namespace RetailPOS.Client.ViewModels;

public sealed partial class CashierPerformanceLineItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial Guid CashierUserId { get; set; }

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FullName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ShiftsWorked { get; set; }

    [ObservableProperty]
    public partial int BillsCount { get; set; }

    [ObservableProperty]
    public partial long GrossSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalDiscountsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalDiscounts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedNetSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long CashCollectedPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCashCollected { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NonCashCollectedPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedNonCashCollected { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalDiscrepanciesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalDiscrepancies { get; set; } = "₹0.00";
}

public sealed partial class CashierPerformanceViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int TotalBillsCount { get; set; }

    [ObservableProperty]
    public partial long TotalGrossSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalNetSalesPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalNetSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalDiscountsPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalDiscounts { get; set; } = "₹0.00";

    public ObservableCollection<CashierPerformanceLineItemViewModel> Cashiers { get; } = new();

    public void PopulateFromDto(CashierPerformanceReportDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Cashiers.Clear();
        int totalBills = 0;
        long totalGross = 0;
        long totalNet = 0;
        long totalDisc = 0;

        if (dto.Cashiers != null)
        {
            foreach (var c in dto.Cashiers)
            {
                totalBills += c.BillsCount;
                totalGross += c.GrossSalesPaise;
                totalNet += c.NetSalesPaise;
                totalDisc += c.TotalDiscountsPaise;

                Cashiers.Add(new CashierPerformanceLineItemViewModel
                {
                    CashierUserId = c.CashierUserId,
                    Username = c.Username,
                    FullName = c.FullName,
                    ShiftsWorked = c.ShiftsWorked,
                    BillsCount = c.BillsCount,
                    GrossSalesPaise = c.GrossSalesPaise,
                    FormattedGrossSales = FormatCurrency(c.GrossSalesPaise),
                    TotalDiscountsPaise = c.TotalDiscountsPaise,
                    FormattedTotalDiscounts = FormatCurrency(c.TotalDiscountsPaise),
                    NetSalesPaise = c.NetSalesPaise,
                    FormattedNetSales = FormatCurrency(c.NetSalesPaise),
                    CashCollectedPaise = c.CashCollectedPaise,
                    FormattedCashCollected = FormatCurrency(c.CashCollectedPaise),
                    NonCashCollectedPaise = c.NonCashCollectedPaise,
                    FormattedNonCashCollected = FormatCurrency(c.NonCashCollectedPaise),
                    TotalDiscrepanciesPaise = c.TotalDiscrepanciesPaise,
                    FormattedTotalDiscrepancies = FormatCurrency(c.TotalDiscrepanciesPaise)
                });
            }
        }

        TotalBillsCount = totalBills;
        TotalGrossSalesPaise = totalGross;
        FormattedTotalGrossSales = FormatCurrency(totalGross);
        TotalNetSalesPaise = totalNet;
        FormattedTotalNetSales = FormatCurrency(totalNet);
        TotalDiscountsPaise = totalDisc;
        FormattedTotalDiscounts = FormatCurrency(totalDisc);
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
