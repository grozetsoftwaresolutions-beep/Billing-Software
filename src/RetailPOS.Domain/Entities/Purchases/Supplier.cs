using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class Supplier : BaseEntity<Guid>
{
    public string Name { get; private set; } = null!;
    public string? ContactPerson { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }
    public string? TaxNumber { get; private set; }
    public int PaymentTermsDays { get; private set; }
    public Money BalanceDue { get; private set; }
    public Money CreditBalance { get; private set; }
    public bool IsActive { get; private set; }
    public string? Notes { get; private set; }

    private Supplier() { }

    public Supplier(
        Guid id,
        string name,
        string? contactPerson = null,
        string? phone = null,
        string? email = null,
        string? address = null,
        string? taxNumber = null,
        int paymentTermsDays = 0,
        string? notes = null)
    {
        ValidateName(name);
        ValidatePaymentTerms(paymentTermsDays);

        Id = id;
        Name = name.Trim();
        ContactPerson = contactPerson?.Trim();
        Phone = phone?.Trim();
        Email = email?.Trim();
        Address = address?.Trim();
        TaxNumber = taxNumber?.Trim();
        PaymentTermsDays = paymentTermsDays;
        Notes = notes?.Trim();
        BalanceDue = Money.Zero();
        CreditBalance = Money.Zero();
        IsActive = true;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateProfile(
        string name,
        string? contactPerson,
        string? phone,
        string? email,
        string? address,
        string? taxNumber,
        int paymentTermsDays,
        string? notes)
    {
        ValidateName(name);
        ValidatePaymentTerms(paymentTermsDays);

        Name = name.Trim();
        ContactPerson = contactPerson?.Trim();
        Phone = phone?.Trim();
        Email = email?.Trim();
        Address = address?.Trim();
        TaxNumber = taxNumber?.Trim();
        PaymentTermsDays = paymentTermsDays;
        Notes = notes?.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void IncreaseBalanceDue(Money amount)
    {
        if (amount.IsNegative)
            throw new DomainException("Balance adjustment amount cannot be negative.", "INVALID_BALANCE_AMOUNT");

        BalanceDue += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void DecreaseBalanceDue(Money amount)
    {
        if (amount.IsNegative)
            throw new DomainException("Balance adjustment amount cannot be negative.", "INVALID_BALANCE_AMOUNT");

        if (amount > BalanceDue)
            throw new DomainException($"Cannot decrease balance by {amount.MinorUnits} paise because current balance due is {BalanceDue.MinorUnits} paise.", "BALANCE_DUE_CANNOT_BE_NEGATIVE");

        BalanceDue -= amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void IncreaseCreditBalance(Money amount)
    {
        if (amount.IsNegative)
            throw new DomainException("Credit balance adjustment amount cannot be negative.", "INVALID_BALANCE_AMOUNT");

        CreditBalance += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void DecreaseCreditBalance(Money amount)
    {
        if (amount.IsNegative)
            throw new DomainException("Credit balance adjustment amount cannot be negative.", "INVALID_BALANCE_AMOUNT");

        if (amount > CreditBalance)
            throw new DomainException($"Cannot decrease credit balance by {amount.MinorUnits} paise because current credit balance is {CreditBalance.MinorUnits} paise.", "CREDIT_BALANCE_CANNOT_BE_NEGATIVE");

        CreditBalance -= amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Supplier name cannot be empty.", "INVALID_SUPPLIER_NAME");

        if (name.Trim().Length > 150)
            throw new DomainException("Supplier name cannot exceed 150 characters.", "INVALID_SUPPLIER_NAME");
    }

    private static void ValidatePaymentTerms(int days)
    {
        if (days < 0 || days > 365)
            throw new DomainException("Payment terms days must be between 0 and 365.", "INVALID_PAYMENT_TERMS");
    }
}
