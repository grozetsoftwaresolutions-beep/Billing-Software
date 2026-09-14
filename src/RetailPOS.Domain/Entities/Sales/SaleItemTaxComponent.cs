using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class SaleItemTaxComponent : BaseEntity<long>
{
    public long SaleItemId { get; private set; }
    public string ComponentName { get; private set; } = null!;
    public decimal RatePercentage { get; private set; }
    public Money Amount { get; private set; }

    private SaleItemTaxComponent() { }

    public SaleItemTaxComponent(long id, long saleItemId, string componentName, decimal ratePercentage, Money amount)
    {
        if (string.IsNullOrWhiteSpace(componentName))
            throw new DomainException("Tax component name cannot be empty.", "INVALID_TAX_COMPONENT_NAME");

        var trimmed = componentName.Trim();
        if (trimmed.Length > 50)
            throw new DomainException("Tax component name cannot exceed 50 characters.", "INVALID_TAX_COMPONENT_NAME");

        if (ratePercentage < 0m)
            throw new DomainException("Tax rate percentage cannot be negative.", "INVALID_TAX_RATE");

        if (amount.MinorUnits < 0)
            throw new DomainException("Tax amount cannot be negative.", "INVALID_TAX_AMOUNT");

        Id = id;
        SaleItemId = saleItemId;
        ComponentName = trimmed;
        RatePercentage = ratePercentage;
        Amount = amount;
    }
}
