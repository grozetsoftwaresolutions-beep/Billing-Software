using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed class SaleInvoiceItemLineViewModel
{
    public string ProductName { get; }
    public string Sku { get; }
    public string FormattedQuantity { get; }
    public string FormattedUnitPrice { get; }
    public string FormattedDiscount { get; }
    public string FormattedTax { get; }
    public string FormattedLineTotal { get; }

    public SaleInvoiceItemLineViewModel(
        string productName,
        string sku,
        string formattedQuantity,
        string formattedUnitPrice,
        string formattedDiscount,
        string formattedTax,
        string formattedLineTotal)
    {
        ProductName = productName;
        Sku = sku;
        FormattedQuantity = formattedQuantity;
        FormattedUnitPrice = formattedUnitPrice;
        FormattedDiscount = formattedDiscount;
        FormattedTax = formattedTax;
        FormattedLineTotal = formattedLineTotal;
    }
}

public sealed class SalePaymentLineViewModel
{
    public string Method { get; }
    public string FormattedAmount { get; }
    public string FormattedTendered { get; }
    public string FormattedChange { get; }
    public string? ReferenceNumber { get; }

    public SalePaymentLineViewModel(
        string method,
        string formattedAmount,
        string formattedTendered,
        string formattedChange,
        string? referenceNumber)
    {
        Method = method;
        FormattedAmount = formattedAmount;
        FormattedTendered = formattedTendered;
        FormattedChange = formattedChange;
        ReferenceNumber = referenceNumber;
    }
}

public sealed class SaleTaxComponentViewModel
{
    public string ComponentName { get; }
    public string FormattedRate { get; }
    public string FormattedAmount { get; }

    public SaleTaxComponentViewModel(
        string componentName,
        string formattedRate,
        string formattedAmount)
    {
        ComponentName = componentName;
        FormattedRate = formattedRate;
        FormattedAmount = formattedAmount;
    }
}

public sealed partial class SaleInvoiceDetailViewModel : ObservableObject
{
    private static readonly CultureInfo IndianCulture = new("en-IN");

    public Guid Id { get; }
    public string InvoiceNumber { get; }
    public SaleStatus Status { get; }
    public string StatusDisplay => Status.ToString();
    public DateTime CreatedAtUtc { get; }
    public string FormattedCreatedAt { get; }

    // Authoritative cashier name from server, or fallback if empty
    public string CashierDisplayName { get; }

    [ObservableProperty]
    public partial int ReprintCount { get; set; }

    [ObservableProperty]
    public partial DateTime? LastReprintedAtUtc { get; set; }

    public string FormattedLastReprintedAt =>
        LastReprintedAtUtc.HasValue
            ? FormatDateTime(LastReprintedAtUtc.Value, _timeZone)
            : "Never";

    public string FormattedGrossSubtotal { get; }
    public string FormattedLineDiscounts { get; }
    public string FormattedInvoiceDiscount { get; }
    public string FormattedTaxableSubtotal { get; }
    public string FormattedTotalTax { get; }
    public string FormattedRoundingAdjustment { get; }
    public string FormattedFinalPayable { get; }

    public IReadOnlyList<SaleInvoiceItemLineViewModel> Items { get; }
    public IReadOnlyList<SalePaymentLineViewModel> Payments { get; }
    public IReadOnlyList<SaleTaxComponentViewModel> TaxComponents { get; }

    private readonly TimeZoneInfo _timeZone;

    public SaleInvoiceDetailViewModel(SaleInvoiceDto dto, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(dto);
        _timeZone = timeZone ?? TimeZoneInfo.Utc;

        Id = dto.Id;
        InvoiceNumber = dto.InvoiceNumber;
        Status = dto.Status;
        CreatedAtUtc = dto.CreatedAtUtc;
        FormattedCreatedAt = FormatDateTime(dto.CreatedAtUtc, _timeZone);

        // Display cashier name as provided by authoritative server contract
        CashierDisplayName = string.IsNullOrWhiteSpace(dto.CashierName) ? "Cashier" : dto.CashierName;

        ReprintCount = dto.ReprintCount;
        LastReprintedAtUtc = dto.LastReprintedAtUtc;

        FormattedGrossSubtotal = FormatPaise(dto.GrossSubtotalPaise);
        FormattedLineDiscounts = FormatPaise(dto.TotalLineDiscountsPaise);
        FormattedInvoiceDiscount = FormatPaise(dto.InvoiceDiscountPaise);
        FormattedTaxableSubtotal = FormatPaise(dto.TaxableSubtotalPaise);
        FormattedTotalTax = FormatPaise(dto.TotalTaxPaise);
        FormattedRoundingAdjustment = FormatPaise(dto.RoundingAdjustmentPaise);
        FormattedFinalPayable = FormatPaise(dto.FinalPayablePaise);

        Items = (dto.Items ?? Array.Empty<SaleItemDto>()).Select(i => new SaleInvoiceItemLineViewModel(
            productName: i.ProductName,
            sku: i.ProductSku,
            formattedQuantity: FormatQuantity(i.Quantity, i.UnitCode),
            formattedUnitPrice: FormatPaise(i.UnitSellingPricePaise),
            formattedDiscount: FormatPaise(i.DiscountAmountPaise),
            formattedTax: FormatPaise(i.TaxAmountPaise),
            formattedLineTotal: FormatPaise(i.LineTotalPaise)
        )).ToList();

        Payments = (dto.Payments ?? Array.Empty<PaymentRecordDto>()).Select(p => new SalePaymentLineViewModel(
            method: p.Method.ToString(),
            formattedAmount: FormatPaise(p.AmountPaise),
            formattedTendered: FormatPaise(p.TenderedAmountPaise),
            formattedChange: FormatPaise(p.ChangeAmountPaise),
            referenceNumber: p.ReferenceNumber
        )).ToList();

        // Aggregate tax breakdown across lines
        var taxes = new Dictionary<string, (decimal rate, long amountPaise)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dto.Items ?? Array.Empty<SaleItemDto>())
        {
            if (item.TaxComponents != null)
            {
                foreach (var comp in item.TaxComponents)
                {
                    if (taxes.TryGetValue(comp.ComponentName, out var existing))
                    {
                        taxes[comp.ComponentName] = (comp.RatePercentage, existing.amountPaise + comp.AmountPaise);
                    }
                    else
                    {
                        taxes[comp.ComponentName] = (comp.RatePercentage, comp.AmountPaise);
                    }
                }
            }
        }

        TaxComponents = taxes.Select(kvp => new SaleTaxComponentViewModel(
            componentName: kvp.Key,
            formattedRate: $"{kvp.Value.rate:0.##}%",
            formattedAmount: FormatPaise(kvp.Value.amountPaise)
        )).ToList();
    }

    public void UpdateReprintMetadata(int newReprintCount, DateTime? newReprintedAtUtc)
    {
        ReprintCount = newReprintCount;
        LastReprintedAtUtc = newReprintedAtUtc;
        OnPropertyChanged(nameof(FormattedLastReprintedAt));
    }

    public static string FormatPaise(long paise)
    {
        var rupees = paise / 100.0m;
        return string.Format(IndianCulture, "₹{0:N2}", rupees);
    }

    public static string FormatDateTime(DateTime utc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string FormatQuantity(decimal qty, string unitCode)
    {
        var formattedQty = (qty % 1 == 0) ? $"{qty:0}" : $"{qty:0.###}";
        return string.IsNullOrWhiteSpace(unitCode) ? formattedQty : $"{formattedQty} {unitCode}";
    }
}
