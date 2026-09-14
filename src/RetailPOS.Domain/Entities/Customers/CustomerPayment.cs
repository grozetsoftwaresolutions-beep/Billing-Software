using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Customers;

public sealed class CustomerPayment : BaseEntity<Guid>
{
    public int StoreId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Money Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string? ReferenceNumber { get; private set; }
    public DateTime PaymentDateUtc { get; private set; }
    public Guid ClientTransactionId { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public string? Notes { get; private set; }

    private CustomerPayment() { }

    public CustomerPayment(
        Guid id,
        int storeId,
        Guid customerId,
        Money amount,
        PaymentMethod method,
        string? referenceNumber,
        DateTime paymentDateUtc,
        Guid clientTransactionId,
        Guid createdByUserId,
        string? notes = null)
    {
        if (storeId <= 0)
            throw new DomainException("Store ID must be positive.", "INVALID_STORE_ID");

        if (customerId == Guid.Empty)
            throw new DomainException("Customer ID is required.", "INVALID_CUSTOMER_ID");

        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Payment amount must be positive.", "INVALID_PAYMENT_AMOUNT");

        if (method == PaymentMethod.Credit || method == PaymentMethod.Mixed)
            throw new DomainException($"Payment method '{method}' cannot be used for customer repayments.", "INVALID_REPAYMENT_METHOD");

        var trimmedRef = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim();
        if (method != PaymentMethod.Cash && trimmedRef == null)
            throw new DomainException($"Reference number is mandatory for {method} customer payments.", "PAYMENT_REFERENCE_REQUIRED");

        if (clientTransactionId == Guid.Empty)
            throw new DomainException("Client transaction ID is required for idempotency.", "CLIENT_TX_REQUIRED");

        if (createdByUserId == Guid.Empty)
            throw new DomainException("Created by user ID is required.", "INVALID_USER_ID");

        Id = id;
        StoreId = storeId;
        CustomerId = customerId;
        Amount = amount;
        Method = method;
        ReferenceNumber = trimmedRef;
        PaymentDateUtc = paymentDateUtc;
        ClientTransactionId = clientTransactionId;
        CreatedByUserId = createdByUserId;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
