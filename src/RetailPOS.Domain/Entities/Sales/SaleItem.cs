using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

/// <summary>
/// Immutable line item in a sales invoice.
/// Captures an exact historical snapshot of product, price, tax, and discount at sale time.
/// </summary>
public sealed class SaleItem : BaseEntity<long>
{
    public Guid SaleId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductSKU { get; private set; }
    public string ProductName { get; private set; }
    public Quantity Quantity { get; private set; }
    public Money UnitSellingPrice { get; private set; }
    public Money UnitCostPrice { get; private set; }
    public Money GrossAmount { get; private set; }
    public Money DiscountAmount { get; private set; }
    public decimal TaxRatePercentage { get; private set; }
    public Money TaxableAmount { get; private set; }
    public Money TaxAmount { get; private set; }
    public Money LineTotal { get; private set; }

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
        Money lineTotal)
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
    }
}
