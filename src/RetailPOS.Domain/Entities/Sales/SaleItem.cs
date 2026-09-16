using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class SaleItem : BaseEntity<long>
{
    public Guid SaleId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductSKU { get; private set; } = null!;
    public string ProductName { get; private set; } = null!;
    public Quantity Quantity { get; private set; }
    public Money UnitSellingPrice { get; private set; }
    public Money UnitCostPrice { get; private set; }
    public Money GrossAmount { get; private set; }
    public Money DiscountAmount { get; private set; }
    public decimal TaxRatePercentage { get; private set; }
    public Money TaxableAmount { get; private set; }
    public Money TaxAmount { get; private set; }
    public Money LineTotal { get; private set; }

    // Price override tracking
    public bool IsPriceOverridden { get; private set; }
    public Money? OriginalUnitSellingPrice { get; private set; }
    public string? PriceOverrideReason { get; private set; }
    public Guid? PriceOverrideApprovedByUserId { get; private set; }

    private readonly List<SaleItemTaxComponent> _taxComponents = [];
    public IReadOnlyList<SaleItemTaxComponent> TaxComponents => _taxComponents.AsReadOnly();

    private SaleItem() { }

    public SaleItem(
        long id,
        Guid saleId,
        Guid productId,
        string productSku,
        string productName,
        Quantity quantity,
        Money unitSellingPrice,
        Money unitCostPrice,
        Money grossAmount,
        Money discountAmount,
        decimal taxRatePercentage,
        Money taxableAmount,
        Money taxAmount,
        Money lineTotal,
        bool isPriceOverridden = false,
        Money? originalUnitSellingPrice = null,
        string? priceOverrideReason = null,
        Guid? priceOverrideApprovedByUserId = null)
    {
        Id = id;
        SaleId = saleId;
        ProductId = productId;
        ProductSKU = productSku;
        ProductName = productName;
        Quantity = quantity;
        UnitSellingPrice = unitSellingPrice;
        UnitCostPrice = unitCostPrice;
        GrossAmount = grossAmount;
        DiscountAmount = discountAmount;
        TaxRatePercentage = taxRatePercentage;
        TaxableAmount = taxableAmount;
        TaxAmount = taxAmount;
        LineTotal = lineTotal;
        IsPriceOverridden = isPriceOverridden;
        OriginalUnitSellingPrice = originalUnitSellingPrice;
        PriceOverrideReason = priceOverrideReason;
        PriceOverrideApprovedByUserId = priceOverrideApprovedByUserId;
    }

    public void AddTaxComponent(SaleItemTaxComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _taxComponents.Add(component);
    }

    public void ApplyPriceOverride(Money originalPrice, string reason, Guid approvedByUserId)
    {
        IsPriceOverridden = true;
        OriginalUnitSellingPrice = originalPrice;
        PriceOverrideReason = reason?.Trim();
        PriceOverrideApprovedByUserId = approvedByUserId;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
