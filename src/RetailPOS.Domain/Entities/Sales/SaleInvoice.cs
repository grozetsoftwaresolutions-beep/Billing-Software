using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Sales;

public sealed class SaleInvoice : BaseEntity<Guid>
{
    public string InvoiceNumber { get; private set; } = null!;
    public Guid ClientTransactionId { get; private set; }
    public Guid TerminalId { get; private set; }
    public Guid CashierId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public PricingModel PricingModel { get; private set; }
    public SaleStatus Status { get; private set; }
    public int ReprintCount { get; private set; }
    public DateTime? LastReprintedAtUtc { get; private set; }

    // Void tracking
    public Guid? VoidedByUserId { get; private set; }
    public string? VoidedByUsername { get; private set; }
    public string? VoidReason { get; private set; }
    public DateTime? VoidedAtUtc { get; private set; }

    public Money GrossSubtotal { get; private set; }
    public Money TotalLineDiscounts { get; private set; }
    public Money InvoiceDiscount { get; private set; }
    public Money TaxableSubtotal { get; private set; }
    public Money TotalTax { get; private set; }
    public Money RoundingAdjustment { get; private set; }
    public Money FinalPayable { get; private set; }

    private readonly List<SaleItem> _items = [];
    public IReadOnlyList<SaleItem> Items => _items.AsReadOnly();

    private readonly List<PaymentRecord> _payments = [];
    public IReadOnlyList<PaymentRecord> Payments => _payments.AsReadOnly();

    private SaleInvoice() { }

    public SaleInvoice(
        Guid id,
        string invoiceNumber,
        Guid clientTransactionId,
        Guid terminalId,
        Guid cashierId,
        PricingModel pricingModel,
        Money grossSubtotal,
        Money totalLineDiscounts,
        Money invoiceDiscount,
        Money taxableSubtotal,
        Money totalTax,
        Money roundingAdjustment,
        Money finalPayable,
        Guid? customerId = null)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new DomainException("Invoice number cannot be empty.", "INVALID_INVOICE_NUMBER");

        Id = id;
        InvoiceNumber = invoiceNumber.Trim().ToUpperInvariant();
        ClientTransactionId = clientTransactionId;
        TerminalId = terminalId;
        CashierId = cashierId;
        PricingModel = pricingModel;
        GrossSubtotal = grossSubtotal;
        TotalLineDiscounts = totalLineDiscounts;
        InvoiceDiscount = invoiceDiscount;
        TaxableSubtotal = taxableSubtotal;
        TotalTax = totalTax;
        RoundingAdjustment = roundingAdjustment;
        FinalPayable = finalPayable;
        CustomerId = customerId;
        Status = SaleStatus.Completed;
    }

    public void AddItem(SaleItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    public void AddPayment(PaymentRecord payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        _payments.Add(payment);
    }

    public void ValidatePaymentsReconcile()
    {
        if (_items.Count == 0)
            throw new DomainException("Sale invoice must contain at least one item.", "SALE_ITEMS_REQUIRED");

        if (_payments.Count == 0)
            throw new DomainException("Sale invoice must contain at least one payment.", "SALE_PAYMENTS_REQUIRED");

        var totalPaidPaise = _payments.Sum(p => p.Amount.MinorUnits);
        if (totalPaidPaise != FinalPayable.MinorUnits)
        {
            throw new DomainException($"Payment total ({totalPaidPaise} paise) does not match invoice payable ({FinalPayable.MinorUnits} paise).", "PAYMENT_TOTAL_MISMATCH");
        }
    }

    public void MarkPartiallyRefunded()
    {
        Status = SaleStatus.PartiallyRefunded;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkFullyRefunded()
    {
        Status = SaleStatus.Refunded;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void MarkRefunded(bool partial = false)
    {
        Status = partial ? SaleStatus.PartiallyRefunded : SaleStatus.Refunded;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void RecordReprint()
    {
        ReprintCount++;
        LastReprintedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public void Void(Guid userId, string username, string reason)
    {
        if (Status == SaleStatus.Voided)
            throw new DomainException($"Sale invoice '{InvoiceNumber}' has already been voided.", "SALE_ALREADY_VOIDED");

        if (Status == SaleStatus.PartiallyRefunded || Status == SaleStatus.Refunded)
            throw new DomainException($"Cannot void sale invoice '{InvoiceNumber}' because refunds have already been issued against it.", "CANNOT_VOID_REFUNDED_SALE");

        Status = SaleStatus.Voided;
        VoidedByUserId = userId;
        VoidedByUsername = username?.Trim();
        VoidReason = reason?.Trim();
        VoidedAtUtc = DateTime.UtcNow;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
