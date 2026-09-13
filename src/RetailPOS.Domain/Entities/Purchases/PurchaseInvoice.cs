using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;

namespace RetailPOS.Domain.Entities.Purchases;

public sealed class PurchaseItem : BaseEntity<long>
{
    public Guid PurchaseId { get; private set; }
    public Guid ProductId { get; private set; }
    public Quantity Quantity { get; private set; }
    public Money UnitCost { get; private set; }
    public decimal TaxRatePercentage { get; private set; }
    public Money TaxAmount { get; private set; }
    public Money LineTotal { get; private set; }

    public PurchaseItem(
        long id,
        Guid purchaseId,
        Guid productId,
        Quantity quantity,
        Money unitCost,
        decimal taxRatePercentage,
        Money taxAmount,
        Money lineTotal)
    {
        Id = id;
        PurchaseId = purchaseId;
        ProductId = productId;
        Quantity = quantity;
        UnitCost = unitCost;
        TaxRatePercentage = taxRatePercentage;
        TaxAmount = taxAmount;
        LineTotal = lineTotal;
    }
}

public sealed class PurchaseInvoice : BaseEntity<Guid>
{
    public string InvoiceNumber { get; private set; }
    public Guid SupplierId { get; private set; }
    public Money Subtotal { get; private set; }
    public Money TotalTax { get; private set; }
    public Money GrandTotal { get; private set; }
    public PurchaseStatus Status { get; private set; }

    private readonly List<PurchaseItem> _items = [];
    public IReadOnlyList<PurchaseItem> Items => _items.AsReadOnly();

    public PurchaseInvoice(Guid id, string invoiceNumber, Guid supplierId, Money subtotal, Money totalTax, Money grandTotal)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new DomainException("Purchase invoice number cannot be empty.", "INVALID_PURCHASE_INVOICE");

        Id = id;
        InvoiceNumber = invoiceNumber.Trim();
        SupplierId = supplierId;
        Subtotal = subtotal;
        TotalTax = totalTax;
        GrandTotal = grandTotal;
        Status = PurchaseStatus.Received;
    }

    public void AddItem(PurchaseItem item) => _items.Add(item);
}
