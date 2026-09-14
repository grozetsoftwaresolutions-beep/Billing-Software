using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class PurchaseInvoice : BaseEntity<Guid>
{
    public int StoreId { get; private set; }
    public string InvoiceNumber { get; private set; } = null!;
    public string SupplierInvoiceNumber { get; private set; } = null!;
    public Guid SupplierId { get; private set; }
    public DateOnly InvoiceDate { get; private set; }
    public DateTime ReceivedDateUtc { get; private set; }
    public Money Subtotal { get; private set; }
    public Money TotalTax { get; private set; }
    public Money GrandTotal { get; private set; }
    public Money CreditOffsetAmount { get; private set; }
    public Money AmountPaid { get; private set; }
    public Money BalanceDue { get; private set; }
    public PurchasePaymentStatus PaymentStatus { get; private set; }
    public PurchaseStatus Status { get; private set; }
    public Guid? ClientTransactionId { get; private set; }
    public Guid? CancellationClientTransactionId { get; private set; }
    public string? CancellationReason { get; private set; }
    public string? Notes { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    private readonly List<PurchaseItem> _items = [];
    public IReadOnlyList<PurchaseItem> Items => _items.AsReadOnly();

    private PurchaseInvoice() { }

    public PurchaseInvoice(
        Guid id,
        int storeId,
        string invoiceNumber,
        string supplierInvoiceNumber,
        Guid supplierId,
        DateOnly invoiceDate,
        DateTime receivedDateUtc,
        Money subtotal,
        Money totalTax,
        Money grandTotal,
        Money creditOffsetAmount,
        Money amountPaid,
        Money balanceDue,
        PurchasePaymentStatus paymentStatus,
        Guid createdByUserId,
        Guid? clientTransactionId = null,
        string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new DomainException("Purchase invoice number cannot be empty.", "INVALID_PURCHASE_INVOICE");

        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber))
            throw new DomainException("Supplier invoice number cannot be empty.", "INVALID_SUPPLIER_INVOICE");

        if (subtotal.IsNegative)
            throw new DomainException("Subtotal cannot be negative.", "INVALID_SUBTOTAL");

        if (totalTax.IsNegative)
            throw new DomainException("Total tax cannot be negative.", "INVALID_TOTAL_TAX");

        if (grandTotal.IsNegative)
            throw new DomainException("Grand total cannot be negative.", "INVALID_GRAND_TOTAL");

        if (creditOffsetAmount.IsNegative)
            throw new DomainException("Credit offset amount cannot be negative.", "INVALID_CREDIT_OFFSET");

        if (amountPaid.IsNegative)
            throw new DomainException("Amount paid cannot be negative.", "INVALID_AMOUNT_PAID");

        if (balanceDue.IsNegative)
            throw new DomainException("Balance due cannot be negative.", "INVALID_BALANCE_DUE");

        Id = id;
        StoreId = storeId;
        InvoiceNumber = invoiceNumber.Trim();
        SupplierInvoiceNumber = supplierInvoiceNumber.Trim();
        SupplierId = supplierId;
        InvoiceDate = invoiceDate;
        ReceivedDateUtc = receivedDateUtc;
        Subtotal = subtotal;
        TotalTax = totalTax;
        GrandTotal = grandTotal;
        CreditOffsetAmount = creditOffsetAmount;
        AmountPaid = amountPaid;
        BalanceDue = balanceDue;
        PaymentStatus = paymentStatus;
        Status = PurchaseStatus.Received;
        CreatedByUserId = createdByUserId;
        ClientTransactionId = clientTransactionId;
        Notes = notes?.Trim();
        CreatedAtUtc = receivedDateUtc;
        UpdatedAtUtc = receivedDateUtc;
    }

    public void AddItem(PurchaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    public void RecordPayment(Money paymentAmount)
    {
        if (paymentAmount.IsNegative || paymentAmount.IsZero)
            throw new DomainException("Payment amount must be positive.", "INVALID_PAYMENT_AMOUNT");

        if (paymentAmount > BalanceDue)
            throw new DomainException($"Payment amount {paymentAmount.MinorUnits} paise exceeds invoice balance due of {BalanceDue.MinorUnits} paise.", "PAYMENT_EXCEEDS_INVOICE_BALANCE");

        AmountPaid += paymentAmount;
        BalanceDue -= paymentAmount;
        PaymentStatus = BalanceDue.IsZero ? PurchasePaymentStatus.Paid : PurchasePaymentStatus.PartiallyPaid;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void ExtinguishBalance()
    {
        BalanceDue = Money.Zero();
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Cancel(string reason, Guid cancellationClientTransactionId)
    {
        if (Status == PurchaseStatus.Cancelled)
            throw new DomainException("Purchase invoice is already cancelled.", "PURCHASE_ALREADY_CANCELLED");

        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("Cancellation reason cannot be empty.", "INVALID_CANCELLATION_REASON");

        Status = PurchaseStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancellationClientTransactionId = cancellationClientTransactionId;
        ExtinguishBalance();
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
