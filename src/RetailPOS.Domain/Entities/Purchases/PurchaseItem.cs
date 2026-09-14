using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class PurchaseItem : BaseEntity<long>
{
    public Guid PurchaseId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductSku { get; private set; } = null!;
    public string ProductName { get; private set; } = null!;
    public string UnitOfMeasure { get; private set; } = null!;
    public Quantity Quantity { get; private set; }
    public Money UnitCost { get; private set; }
    public decimal TaxRatePercentage { get; private set; }
    public Money TaxAmount { get; private set; }
    public Money LineTotal { get; private set; }
    public string? BatchNumber { get; private set; }
    public DateOnly? ExpiryDate { get; private set; }

    private readonly List<PurchaseItemTaxComponent> _taxComponents = [];
    public IReadOnlyList<PurchaseItemTaxComponent> TaxComponents => _taxComponents.AsReadOnly();

    private PurchaseItem() { }

    public PurchaseItem(
        long id,
        Guid purchaseId,
        Guid productId,
        string productSku,
        string productName,
        string unitOfMeasure,
        Quantity quantity,
        Money unitCost,
        decimal taxRatePercentage,
        Money taxAmount,
        Money lineTotal,
        string? batchNumber = null,
        DateOnly? expiryDate = null)
    {
        if (string.IsNullOrWhiteSpace(productSku))
            throw new DomainException("Product SKU snapshot cannot be empty.", "INVALID_PRODUCT_SKU");

        if (string.IsNullOrWhiteSpace(productName))
            throw new DomainException("Product name snapshot cannot be empty.", "INVALID_PRODUCT_NAME");

        if (string.IsNullOrWhiteSpace(unitOfMeasure))
            throw new DomainException("Unit of measure snapshot cannot be empty.", "INVALID_UNIT_OF_MEASURE");

        if (!quantity.IsPositive)
            throw new DomainException("Purchase item quantity must be positive.", "INVALID_QUANTITY");

        if (unitCost.IsNegative)
            throw new DomainException("Unit cost cannot be negative.", "INVALID_UNIT_COST");

        if (taxRatePercentage < 0m || taxRatePercentage > 100m)
            throw new DomainException("Tax rate percentage must be between 0 and 100.", "INVALID_TAX_RATE");

        if (taxAmount.IsNegative)
            throw new DomainException("Tax amount cannot be negative.", "INVALID_TAX_AMOUNT");

        Id = id;
        PurchaseId = purchaseId;
        ProductId = productId;
        ProductSku = productSku.Trim().ToUpperInvariant();
        ProductName = productName.Trim();
        UnitOfMeasure = unitOfMeasure.Trim().ToUpperInvariant();
        Quantity = quantity;
        UnitCost = unitCost;
        TaxRatePercentage = taxRatePercentage;
        TaxAmount = taxAmount;
        LineTotal = lineTotal;
        BatchNumber = batchNumber?.Trim();
        ExpiryDate = expiryDate;
    }

    public void AddTaxComponent(PurchaseItemTaxComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (_taxComponents.Any(c => string.Equals(c.ComponentName, component.ComponentName, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException($"Tax component '{component.ComponentName}' already exists on purchase item.", "DUPLICATE_TAX_COMPONENT");

        _taxComponents.Add(component);
    }
}
