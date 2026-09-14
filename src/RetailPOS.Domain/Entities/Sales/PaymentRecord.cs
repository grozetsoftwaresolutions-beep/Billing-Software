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
        if (method == PaymentMethod.Mixed)
            throw new DomainException("PaymentMethod.Mixed cannot be persisted as an individual payment record.", "MIXED_PAYMENT_PERSISTENCE_INVALID");

        if (amount.MinorUnits <= 0)
            throw new DomainException("Payment amount must be greater than zero.", "INVALID_PAYMENT_AMOUNT");

        var currency = amount.Currency;
        var trimmedRef = referenceNumber?.Trim();

        Money finalTendered;
        Money finalChange;

        switch (method)
        {
            case PaymentMethod.Cash:
                finalTendered = tenderedAmount ?? amount;
                if (finalTendered.MinorUnits < amount.MinorUnits)
                    throw new DomainException("Cash tendered amount cannot be less than the payment amount.", "TENDERED_AMOUNT_INSUFFICIENT");

                finalChange = finalTendered - amount;
                break;

            case PaymentMethod.Card:
                if (string.IsNullOrWhiteSpace(trimmedRef) || trimmedRef.Length < 4 || trimmedRef.Length > 50)
                    throw new DomainException("Card reference number is mandatory (4-50 characters).", "CARD_REFERENCE_REQUIRED");

                if (tenderedAmount.HasValue && tenderedAmount.Value.MinorUnits != amount.MinorUnits)
                    throw new DomainException("Card tendered amount must exactly match the payment amount.", "CARD_TENDERED_MUST_MATCH_AMOUNT");

                if (changeAmount.HasValue && changeAmount.Value.MinorUnits != 0)
                    throw new DomainException("Card payment cannot have change amount.", "CARD_CANNOT_HAVE_CHANGE");

                finalTendered = amount;
                finalChange = Money.Zero(currency);
                break;

            case PaymentMethod.UPI:
                if (string.IsNullOrWhiteSpace(trimmedRef) || trimmedRef.Length < 6 || trimmedRef.Length > 50)
                    throw new DomainException("UPI transaction reference/UTR is mandatory (6-50 characters).", "UPI_REFERENCE_REQUIRED");

                if (tenderedAmount.HasValue && tenderedAmount.Value.MinorUnits != amount.MinorUnits)
                    throw new DomainException("UPI tendered amount must exactly match the payment amount.", "UPI_TENDERED_MUST_MATCH_AMOUNT");

                if (changeAmount.HasValue && changeAmount.Value.MinorUnits != 0)
                    throw new DomainException("UPI payment cannot have change amount.", "UPI_CANNOT_HAVE_CHANGE");

                finalTendered = amount;
                finalChange = Money.Zero(currency);
                break;

            default:
                finalTendered = tenderedAmount ?? amount;
                finalChange = changeAmount ?? Money.Zero(currency);
                break;
        }

        Id = id;
        SaleId = saleId;
        Method = method;
        Amount = amount;
        TenderedAmount = finalTendered;
        ChangeAmount = finalChange;
        ReferenceNumber = trimmedRef;
    }
}
