using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Customers;

public sealed class Customer : BaseEntity<Guid>
{
    public string Name { get; private set; }
    public string? Phone { get; private set; }
    public string? Email { get; private set; }
    public Money OutstandingCredit { get; private set; }
    public Money CreditLimit { get; private set; }
    public bool IsActive { get; private set; }

    public Customer(Guid id, string name, string? phone = null, string? email = null, decimal creditLimit = 0m)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Customer name cannot be empty.", "INVALID_CUSTOMER_NAME");

        Id = id;
        Name = name.Trim();
        Phone = phone?.Trim();
        Email = email?.Trim();
        OutstandingCredit = Money.Zero();
        CreditLimit = Money.FromDecimal(creditLimit);
        IsActive = true;
    }

    public void AddCredit(Money amount)
    {
        if (OutstandingCredit + amount > CreditLimit)
            throw new DomainException("Transaction exceeds customer credit limit.", "CREDIT_LIMIT_EXCEEDED");
        OutstandingCredit += amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordRepayment(Money amount)
    {
        OutstandingCredit -= amount;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
