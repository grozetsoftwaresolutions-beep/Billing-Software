using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Customers;

namespace RetailPOS.Client.ViewModels;

public sealed class CustomerLedgerItemViewModel : ObservableObject
{
    private static readonly CultureInfo InrCulture = new("en-IN");

    public DateTime TimestampUtc { get; }
    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture);

    public string EntryType { get; }
    public string ReferenceNumber { get; }
    public long DebtChangePaise { get; }
    public long StoreCreditChangePaise { get; }
    public long RunningOutstandingCreditPaise { get; }
    public long RunningCreditBalancePaise { get; }
    public string? Notes { get; }

    public string DebtChangeFormatted => FormatSignedCurrency(DebtChangePaise);
    public string StoreCreditChangeFormatted => FormatSignedCurrency(StoreCreditChangePaise);
    public string RunningDebtFormatted => CustomerSummaryItemViewModel.FormatCurrency(RunningOutstandingCreditPaise);
    public string RunningBalanceFormatted => CustomerSummaryItemViewModel.FormatCurrency(RunningCreditBalancePaise);

    public string DebtChangeForeground => DebtChangePaise > 0 ? "#E11D48" : (DebtChangePaise < 0 ? "#059669" : "#64748B");
    public string StoreCreditChangeForeground => StoreCreditChangePaise > 0 ? "#059669" : (StoreCreditChangePaise < 0 ? "#E11D48" : "#64748B");

    public CustomerLedgerItemViewModel(CustomerLedgerEntryDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        TimestampUtc = dto.TimestampUtc;
        EntryType = dto.EntryType;
        ReferenceNumber = dto.ReferenceNumber;
        DebtChangePaise = dto.DebtChangePaise;
        StoreCreditChangePaise = dto.StoreCreditChangePaise;
        RunningOutstandingCreditPaise = dto.RunningOutstandingCreditPaise;
        RunningCreditBalancePaise = dto.RunningCreditBalancePaise;
        Notes = dto.Notes;
    }

    private static string FormatSignedCurrency(long paise)
    {
        if (paise == 0) return "₹0.00";
        var prefix = paise > 0 ? "+" : "-";
        var absRupees = Math.Abs(paise) / 100.0;
        return string.Format(InrCulture, "{0}₹{1:N2}", prefix, absRupees);
    }
}
