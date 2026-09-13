using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class PaymentRecord : BaseEntity<Guid>
{
    public Guid SaleId { get; private set; }
    public PaymentMethod Method { get; private set; }
    public Money Amount { get; private set; }
    public Money TenderedAmount { get; private set; }
    public Money ChangeAmount { get; private set; }
    public string? ReferenceNumber { get; private set; }

    private PaymentRecord() { }

    public PaymentRecord(
        Guid id,
        Guid saleId,
        PaymentMethod method,
        Money amount,
        Money? tenderedAmount = null,
        Money? changeAmount = null,
        string? referenceNumber = null)
    {
        Id = id;
        SaleId = saleId;
        Method = method;
        Amount = amount;
        TenderedAmount = tenderedAmount ?? amount;
        ChangeAmount = changeAmount ?? Money.Zero(amount.Currency);
        ReferenceNumber = referenceNumber?.Trim();
    }
}
