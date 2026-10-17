using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed class CustomerSaleItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public string InvoiceNumber { get; }
    public DateTime CreatedAtUtc { get; }
    public string CreatedAtLocal => CreatedAtUtc.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture);

    public long FinalPayablePaise { get; }
    public string FinalPayableFormatted => CustomerSummaryItemViewModel.FormatCurrency(FinalPayablePaise);

    public SaleStatus Status { get; }
    public string StatusDisplay => Status.ToString();

    public string CashierName { get; }

    public CustomerSaleItemViewModel(SaleInvoiceDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        InvoiceNumber = dto.InvoiceNumber;
        CreatedAtUtc = dto.CreatedAtUtc;
        FinalPayablePaise = dto.FinalPayablePaise;
        Status = dto.Status;
        CashierName = dto.CashierName;
    }
}
