using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Enums;

namespace RetailPOS.Client.ViewModels;

public sealed partial class SaleSummaryItemViewModel : ObservableObject
{
    private static readonly CultureInfo IndianCulture = new("en-IN");
    private readonly SaleSummaryDto _dto;
    private readonly TimeZoneInfo _timeZone;

    public Guid Id => _dto.Id;
    public string InvoiceNumber => _dto.InvoiceNumber;
    public long FinalPayablePaise => _dto.FinalPayablePaise;
    public string FormattedTotal => string.Format(IndianCulture, "₹{0:N2}", _dto.FinalPayablePaise / 100.0m);
    public SaleStatus Status => _dto.Status;
    public string StatusDisplay => _dto.Status.ToString().ToUpperInvariant();
    public DateTime CreatedAtUtc => _dto.CreatedAtUtc;
    public int ItemCount => _dto.ItemCount;
    public string ItemCountDisplay => _dto.ItemCount == 1 ? "1 item" : $"{_dto.ItemCount} items";
    public string PaymentSummary => _dto.PaymentSummary;

    public string FormattedDateTime
    {
        get
        {
            try
            {
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(_dto.CreatedAtUtc, DateTimeKind.Utc), _timeZone);
                return localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                return _dto.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC", CultureInfo.InvariantCulture);
            }
        }
    }

    public string StatusBadgeBackground => _dto.Status switch
    {
        SaleStatus.Completed => "#DCFCE7",
        SaleStatus.Voided => "#FEE2E2",
        _ => "#F1F5F9"
    };

    public string StatusBadgeForeground => _dto.Status switch
    {
        SaleStatus.Completed => "#15803D",
        SaleStatus.Voided => "#B91C1C",
        _ => "#475569"
    };

    public SaleSummaryItemViewModel(SaleSummaryDto dto, TimeZoneInfo? timeZone = null)
    {
        _dto = dto ?? throw new ArgumentNullException(nameof(dto));
        _timeZone = timeZone ?? TimeZoneInfo.Utc;
    }
}
