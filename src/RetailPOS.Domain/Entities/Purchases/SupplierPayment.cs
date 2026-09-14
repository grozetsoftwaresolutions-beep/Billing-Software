using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class SupplierPayment : BaseEntity<Guid>
{
    public int StoreId { get; private set; }
    public Guid SupplierId { get; private set; }
    public SupplierPaymentType Type { get; private set; }
    public Money Amount { get; private set; }
    public PaymentMethod Method { get; private set; }
    public string? ReferenceNumber { get; private set; }
    public DateTime PaymentDateUtc { get; private set; }
    public string? Notes { get; private set; }
    public Guid ClientTransactionId { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    private readonly List<SupplierPaymentAllocation> _allocations = [];
    public IReadOnlyList<SupplierPaymentAllocation> Allocations => _allocations.AsReadOnly();

    private SupplierPayment() { }

    public SupplierPayment(
        Guid id,
        int storeId,
        Guid supplierId,
        SupplierPaymentType type,
        Money amount,
        PaymentMethod method,
        string? referenceNumber,
        DateTime paymentDateUtc,
        Guid clientTransactionId,
        Guid createdByUserId,
        string? notes = null)
    {
        if (amount.IsNegative || amount.IsZero)
            throw new DomainException("Supplier payment amount must be positive.", "INVALID_PAYMENT_AMOUNT");

        ValidatePaymentMethodAndReference(method, referenceNumber);

        Id = id;
        StoreId = storeId;
        SupplierId = supplierId;
        Type = type;
        Amount = amount;
        Method = method;
        ReferenceNumber = referenceNumber?.Trim();
        PaymentDateUtc = paymentDateUtc;
        ClientTransactionId = clientTransactionId;
        CreatedByUserId = createdByUserId;
        Notes = notes?.Trim();
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void AddAllocation(SupplierPaymentAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        _allocations.Add(allocation);
    }

    private static void ValidatePaymentMethodAndReference(PaymentMethod method, string? reference)
    {
        if (method == PaymentMethod.Card || method == PaymentMethod.Mixed || method == PaymentMethod.Credit)
            throw new DomainException($"Payment method '{method}' is not permitted for supplier transactions.", "INVALID_SUPPLIER_PAYMENT_METHOD");

        if (method != PaymentMethod.Cash)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new DomainException($"Reference number is mandatory for {method} supplier payments.", "PAYMENT_REFERENCE_REQUIRED");

            var trimmed = reference.Trim();
            if (trimmed.Length < 4 || trimmed.Length > 50)
                throw new DomainException("Reference number must be between 4 and 50 characters.", "INVALID_PAYMENT_REFERENCE_LENGTH");
        }
        else if (reference != null && reference.Trim().Length > 50)
        {
            throw new DomainException("Cash reference cannot exceed 50 characters.", "INVALID_PAYMENT_REFERENCE_LENGTH");
        }
    }
}

public sealed class SupplierPaymentAllocation : BaseEntity<long>
{
    public Guid SupplierPaymentId { get; private set; }
    public Guid PurchaseInvoiceId { get; private set; }
    public Money AmountAllocated { get; private set; }

    private SupplierPaymentAllocation() { }

    public SupplierPaymentAllocation(
        long id,
        Guid supplierPaymentId,
        Guid purchaseInvoiceId,
        Money amountAllocated)
    {
        if (amountAllocated.IsNegative || amountAllocated.IsZero)
            throw new DomainException("Allocated amount must be positive.", "INVALID_ALLOCATION_AMOUNT");

        Id = id;
        SupplierPaymentId = supplierPaymentId;
        PurchaseInvoiceId = purchaseInvoiceId;
        AmountAllocated = amountAllocated;
        CreatedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
