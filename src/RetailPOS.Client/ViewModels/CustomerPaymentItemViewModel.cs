using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Customers;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed class CustomerPaymentItemViewModel : ObservableObject
{
    public Guid Id { get; }
    public DateTime PaymentDateUtc { get; }
    public string PaymentDateLocal => PaymentDateUtc.ToLocalTime().ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture);

    public long AmountPaise { get; }
    public string AmountFormatted => CustomerSummaryItemViewModel.FormatCurrency(AmountPaise);

    public PaymentMethod Method { get; }
    public string MethodDisplay => Method.ToString();

    public string? ReferenceNumber { get; }
    public Guid ClientTransactionId { get; }
    public string? Notes { get; }

    public CustomerPaymentItemViewModel(CustomerPaymentDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        PaymentDateUtc = dto.PaymentDateUtc;
        AmountPaise = dto.AmountPaise;
        Method = dto.Method;
        ReferenceNumber = dto.ReferenceNumber;
        ClientTransactionId = dto.ClientTransactionId;
        Notes = dto.Notes;
    }
}
