using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class Supplier : BaseEntity<Guid>
{
    public string Name { get; private set; }
    public string? ContactPerson { get; private set; }
    public string? Phone { get; private set; }
    public string? TaxNumber { get; private set; }
    public Money BalanceDue { get; private set; }
    public bool IsActive { get; private set; }

    public Supplier(Guid id, string name, string? contactPerson = null, string? phone = null, string? taxNumber = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Supplier name cannot be empty.", "INVALID_SUPPLIER_NAME");

        Id = id;
        Name = name.Trim();
        ContactPerson = contactPerson?.Trim();
        Phone = phone?.Trim();
        TaxNumber = taxNumber?.Trim();
        BalanceDue = Money.Zero();
        IsActive = true;
    }
}
