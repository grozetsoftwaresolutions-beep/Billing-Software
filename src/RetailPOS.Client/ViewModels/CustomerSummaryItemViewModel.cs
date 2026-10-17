using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RetailPOS.Contracts.Customers;

namespace RetailPOS.Client.ViewModels;

public sealed partial class CustomerSummaryItemViewModel : ObservableObject
{
    private static readonly CultureInfo InrCulture = new("en-IN");

    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InitialLetter))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Phone { get; set; }

    [ObservableProperty]
    public partial string? Email { get; set; }

    [ObservableProperty]
    public partial string? Address { get; set; }

    [ObservableProperty]
    public partial string? Gstin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutstandingCreditFormatted))]
    [NotifyPropertyChangedFor(nameof(HasDebt))]
    public partial long OutstandingCreditPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreditBalanceFormatted))]
    [NotifyPropertyChangedFor(nameof(HasCreditBalance))]
    public partial long CreditBalancePaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreditLimitFormatted))]
    public partial long CreditLimitPaise { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeBackground))]
    [NotifyPropertyChangedFor(nameof(StatusBadgeForeground))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    public partial DateTime CreatedAtUtc { get; set; }

    [ObservableProperty]
    public partial DateTime UpdatedAtUtc { get; set; }

    public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public string OutstandingCreditFormatted => FormatCurrency(OutstandingCreditPaise);
    public string CreditBalanceFormatted => FormatCurrency(CreditBalancePaise);
    public string CreditLimitFormatted => FormatCurrency(CreditLimitPaise);

    public bool HasDebt => OutstandingCreditPaise > 0;
    public bool HasCreditBalance => CreditBalancePaise > 0;

    public string StatusText => IsActive ? "Active" : "Inactive";
    public string StatusBadgeBackground => IsActive ? "#DCFCE7" : "#FEE2E2";
    public string StatusBadgeForeground => IsActive ? "#166534" : "#991B1B";

    public CustomerSummaryItemViewModel(CustomerDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Id = dto.Id;
        Update(dto);
    }

    public void Update(CustomerDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        Name = dto.Name;
        Phone = dto.Phone;
        Email = dto.Email;
        Address = dto.Address;
        Gstin = dto.Gstin;
        OutstandingCreditPaise = dto.OutstandingCreditPaise;
        CreditBalancePaise = dto.CreditBalancePaise;
        CreditLimitPaise = dto.CreditLimitPaise;
        IsActive = dto.IsActive;
        Notes = dto.Notes;
        CreatedAtUtc = dto.CreatedAtUtc;
        UpdatedAtUtc = dto.UpdatedAtUtc;
    }

    public static string FormatCurrency(long paise)
    {
        var rupees = paise / 100.0;
        return string.Format(InrCulture, "₹{0:N2}", rupees);
    }
}
