using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class PurchaseItemTaxComponent : BaseEntity<long>
{
    public long PurchaseItemId { get; private set; }
    public string ComponentName { get; private set; } = null!;
    public decimal RatePercentage { get; private set; }
    public Money TaxAmount { get; private set; }

    private PurchaseItemTaxComponent() { }

    public PurchaseItemTaxComponent(long id, long purchaseItemId, string componentName, decimal ratePercentage, Money taxAmount)
    {
        if (string.IsNullOrWhiteSpace(componentName))
            throw new DomainException("Component name cannot be empty.", "INVALID_TAX_COMPONENT_NAME");

        if (ratePercentage < 0m || ratePercentage > 100m)
            throw new DomainException("Tax rate percentage must be between 0 and 100.", "INVALID_TAX_RATE");

        if (taxAmount.IsNegative)
            throw new DomainException("Tax amount cannot be negative.", "INVALID_TAX_AMOUNT");

        Id = id;
        PurchaseItemId = purchaseItemId;
        ComponentName = componentName.Trim().ToUpperInvariant();
        RatePercentage = ratePercentage;
        TaxAmount = taxAmount;
    }
}
