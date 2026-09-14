using RetailPOS.Domain.Common;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class SaleRefundItem : BaseEntity<long>
{
    public Guid RefundId { get; private set; }
    public long SaleItemId { get; private set; }
    public Guid ProductId { get; private set; }
    public Quantity QuantityRefunded { get; private set; }
    public Money RefundAmount { get; private set; }
    public Money TaxRefundAmount { get; private set; }

    private SaleRefundItem() { }

    public SaleRefundItem(
        long id,
        Guid refundId,
        long saleItemId,
        Guid productId,
        Quantity quantityRefunded,
        Money refundAmount,
        Money taxRefundAmount)
    {
        if (quantityRefunded.Value <= 0m)
            throw new DomainException("Refunded quantity must be greater than zero.", "INVALID_REFUND_QUANTITY");

        if (refundAmount.MinorUnits <= 0)
            throw new DomainException("Item refund amount must be greater than zero.", "INVALID_REFUND_AMOUNT");

        Id = id;
        RefundId = refundId;
        SaleItemId = saleItemId;
        ProductId = productId;
        QuantityRefunded = quantityRefunded;
        RefundAmount = refundAmount;
        TaxRefundAmount = taxRefundAmount;
    }
}
