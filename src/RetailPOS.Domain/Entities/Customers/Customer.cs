using System.Text.RegularExpressions;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Customers;

public sealed class Customer : BaseEntity<Guid>
{
    private static readonly Regex PhoneRegex = new(@"^[6-9]\d{9}$", RegexOptions.Compiled);
    private static readonly Regex GstinRegex = new(@"^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z]{1}[1-9A-Z]{1}Z[0-9A-Z]{1}$", RegexOptions.Compiled);

    public string Name { get; private set; } = null!;
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public string? Address { get; private set; }
    public string? Gstin { get; private set; }
    public Money OutstandingCredit { get; private set; }
    public Money CreditBalance { get; private set; }
    public Money CreditLimit { get; private set; }
    public bool IsActive { get; private set; }
    public string? Notes { get; private set; }

    private Customer() { }

    public Customer(
        Guid id,
        string name,
        string? phone = null,
        string? email = null,
        string? address = null,
        string? gstin = null,
        decimal creditLimit = 0m,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Customer name cannot be empty.", "INVALID_CUSTOMER_NAME");

        var canonicalPhone = CanonicalizePhone(phone);
        var canonicalGstin = ValidateAndNormalizeGstin(gstin);

        var limitMoney = Money.FromDecimal(creditLimit);
        if (limitMoney.IsNegative)
            throw new DomainException("Credit limit cannot be negative.", "INVALID_CREDIT_LIMIT");

        Id = id;
        Name = name.Trim();
        Phone = canonicalPhone;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Gstin = canonicalGstin;
        OutstandingCredit = Money.Zero();
        CreditBalance = Money.Zero();
        CreditLimit = limitMoney;
        IsActive = true;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public static string? CanonicalizePhone(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone))
            return null;

        if (rawPhone.Any(char.IsLetter))
            throw new DomainException("Phone number cannot contain letters.", "INVALID_PHONE_NUMBER");

        // Strip non-digit characters
        var digits = Regex.Replace(rawPhone, @"[^\d]", "");

        if (digits.StartsWith("0091", StringComparison.Ordinal) && digits.Length == 14)
            digits = digits[4..];
        else if (digits.StartsWith("91", StringComparison.Ordinal) && digits.Length == 12)
            digits = digits[2..];
        else if (digits.StartsWith('0') && digits.Length == 11)
            digits = digits[1..];

        if (!PhoneRegex.IsMatch(digits))
            throw new DomainException("Invalid Indian mobile number format (must be 10 digits starting with 6, 7, 8, or 9).", "INVALID_PHONE_NUMBER");

        return digits;
    }

    public static string? ValidateAndNormalizeGstin(string? rawGstin)
    {
        if (string.IsNullOrWhiteSpace(rawGstin))
            return null;

        var normalized = rawGstin.Trim().ToUpperInvariant();
        if (!GstinRegex.IsMatch(normalized))
            throw new DomainException("Invalid GSTIN format (must be 15 alphanumeric characters matching state and PAN structure).", "INVALID_GSTIN");

        return normalized;
    }

    public void UpdateProfile(string name, string? phone, string? email, string? address, string? gstin, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Customer name cannot be empty.", "INVALID_CUSTOMER_NAME");

        Name = name.Trim();
        Phone = CanonicalizePhone(phone);
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        Gstin = ValidateAndNormalizeGstin(gstin);
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void UpdateCreditLimit(Money newLimit)
    {
        if (newLimit.IsNegative)
            throw new DomainException("Credit limit cannot be negative.", "INVALID_CREDIT_LIMIT");

        CreditLimit = newLimit;
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

    public void AddCredit(Money amount)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Credit amount to add must be positive.", "INVALID_CREDIT_AMOUNT");

        if (OutstandingCredit + amount > CreditLimit)
            throw new DomainException("Transaction exceeds customer credit limit.", "CREDIT_LIMIT_EXCEEDED");

        OutstandingCredit += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordRepayment(Money amount)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Repayment amount must be positive.", "INVALID_REPAYMENT_AMOUNT");

        if (amount <= OutstandingCredit)
        {
            OutstandingCredit -= amount;
        }
        else
        {
            var excess = amount - OutstandingCredit;
            OutstandingCredit = Money.Zero(amount.Currency);
            CreditBalance += excess;
        }

        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void IncreaseCreditBalance(Money amount)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Credit balance increase amount must be positive.", "INVALID_AMOUNT");

        CreditBalance += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void DecreaseCreditBalance(Money amount)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Credit balance decrease amount must be positive.", "INVALID_AMOUNT");

        if (amount > CreditBalance)
            throw new DomainException("Insufficient customer credit balance.", "INSUFFICIENT_CREDIT_BALANCE");

        CreditBalance -= amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
