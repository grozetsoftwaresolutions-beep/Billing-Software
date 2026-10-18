using System;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Reports;

namespace RetailPOS.Client.ViewModels;

public sealed partial class HourlySalesItemViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int Hour { get; set; }

    [ObservableProperty]
    public partial string HourDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int InvoiceCount { get; set; }

    [ObservableProperty]
    public partial long NetSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedNetSales { get; set; } = "₹0.00";
}

public sealed partial class DailySalesSummaryViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int TotalInvoicesCount { get; set; }

    [ObservableProperty]
    public partial int TotalItemsSoldCount { get; set; }

    [ObservableProperty]
    public partial int VoidedInvoicesCount { get; set; }

    [ObservableProperty]
    public partial long VoidedAmountPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedVoidedAmount { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long GrossSubtotalPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedGrossSubtotal { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalLineDiscountsPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalLineDiscounts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalInvoiceDiscountsPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalInvoiceDiscounts { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TaxableSubtotalPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTaxableSubtotal { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalTaxPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalTax { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long RoundingAdjustmentPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedRoundingAdjustment { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long GrossSalesPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedGrossSales { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial int TotalRefundsCount { get; set; }

    [ObservableProperty]
    public partial long TotalRefundsPaise { get; set; }

    [ObservableProperty]
    public partial string FormattedTotalRefunds { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long NetRevenuePaise { get; set; }

    [ObservableProperty]
    public partial string FormattedNetRevenue { get; set; } = "₹0.00";

    // Payment Tenders
    [ObservableProperty]
    public partial long CashPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCash { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long CardPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCard { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long UpiPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedUpi { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long BankTransferPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedBankTransfer { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long ChequePaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCheque { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long CreditPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedCredit { get; set; } = "₹0.00";

    [ObservableProperty]
    public partial long TotalTendersPaise { get; set; }
    [ObservableProperty]
    public partial string FormattedTotalTenders { get; set; } = "₹0.00";

    public ObservableCollection<HourlySalesItemViewModel> HourlyDistribution { get; } = new();

    public void PopulateFromDto(DailySalesSummaryReportDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        TotalInvoicesCount = dto.TotalInvoicesCount;
        TotalItemsSoldCount = dto.TotalItemsSoldCount;
        VoidedInvoicesCount = dto.VoidedInvoicesCount;
        VoidedAmountPaise = dto.VoidedAmountPaise;
        FormattedVoidedAmount = FormatCurrency(dto.VoidedAmountPaise);

        GrossSubtotalPaise = dto.GrossSubtotalPaise;
        FormattedGrossSubtotal = FormatCurrency(dto.GrossSubtotalPaise);

        TotalLineDiscountsPaise = dto.TotalLineDiscountsPaise;
        FormattedTotalLineDiscounts = FormatCurrency(dto.TotalLineDiscountsPaise);

        TotalInvoiceDiscountsPaise = dto.TotalInvoiceDiscountsPaise;
        FormattedTotalInvoiceDiscounts = FormatCurrency(dto.TotalInvoiceDiscountsPaise);

        TaxableSubtotalPaise = dto.TaxableSubtotalPaise;
        FormattedTaxableSubtotal = FormatCurrency(dto.TaxableSubtotalPaise);

        TotalTaxPaise = dto.TotalTaxPaise;
        FormattedTotalTax = FormatCurrency(dto.TotalTaxPaise);

        RoundingAdjustmentPaise = dto.RoundingAdjustmentPaise;
        FormattedRoundingAdjustment = FormatCurrency(dto.RoundingAdjustmentPaise);

        GrossSalesPaise = dto.GrossSalesPaise;
        FormattedGrossSales = FormatCurrency(dto.GrossSalesPaise);

        TotalRefundsCount = dto.TotalRefundsCount;
        TotalRefundsPaise = dto.TotalRefundsPaise;
        FormattedTotalRefunds = FormatCurrency(dto.TotalRefundsPaise);

        NetRevenuePaise = dto.NetRevenuePaise;
        FormattedNetRevenue = FormatCurrency(dto.NetRevenuePaise);

        if (dto.PaymentTenders != null)
        {
            CashPaise = dto.PaymentTenders.CashPaise;
            FormattedCash = FormatCurrency(dto.PaymentTenders.CashPaise);

            CardPaise = dto.PaymentTenders.CardPaise;
            FormattedCard = FormatCurrency(dto.PaymentTenders.CardPaise);

            UpiPaise = dto.PaymentTenders.UpiPaise;
            FormattedUpi = FormatCurrency(dto.PaymentTenders.UpiPaise);

            BankTransferPaise = dto.PaymentTenders.BankTransferPaise;
            FormattedBankTransfer = FormatCurrency(dto.PaymentTenders.BankTransferPaise);

            ChequePaise = dto.PaymentTenders.ChequePaise;
            FormattedCheque = FormatCurrency(dto.PaymentTenders.ChequePaise);

            CreditPaise = dto.PaymentTenders.CreditPaise;
            FormattedCredit = FormatCurrency(dto.PaymentTenders.CreditPaise);

            TotalTendersPaise = dto.PaymentTenders.TotalPaise;
            FormattedTotalTenders = FormatCurrency(dto.PaymentTenders.TotalPaise);
        }

        HourlyDistribution.Clear();
        if (dto.HourlyDistribution != null)
        {
            foreach (var h in dto.HourlyDistribution)
            {
                var hourStr = string.Format(CultureInfo.InvariantCulture, "{0:D2}:00 - {1:D2}:00", h.Hour, (h.Hour + 1) % 24);
                HourlyDistribution.Add(new HourlySalesItemViewModel
                {
                    Hour = h.Hour,
                    HourDisplay = hourStr,
                    InvoiceCount = h.InvoiceCount,
                    NetSalesPaise = h.NetSalesPaise,
                    FormattedNetSales = FormatCurrency(h.NetSalesPaise)
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
