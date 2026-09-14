using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using Xunit;

namespace RetailPOS.Domain.Tests;

public sealed class Phase7DomainTests
{
    [Fact]
    public void Supplier_Invariants_CannotSetNegativeBalanceDue()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies");
        Assert.Equal(0, supplier.BalanceDue.MinorUnits);
        Assert.Equal(0, supplier.CreditBalance.MinorUnits);

        Assert.Throws<DomainException>(() => supplier.IncreaseBalanceDue(Money.FromMinorUnits(-500, Currency.INR)));
        Assert.Throws<DomainException>(() => supplier.DecreaseBalanceDue(Money.FromMinorUnits(-500, Currency.INR)));
    }

    [Fact]
    public void Supplier_Invariants_CannotSetNegativeCreditBalance()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies");

        Assert.Throws<DomainException>(() => supplier.IncreaseCreditBalance(Money.FromMinorUnits(-500, Currency.INR)));
        Assert.Throws<DomainException>(() => supplier.DecreaseCreditBalance(Money.FromMinorUnits(-500, Currency.INR)));
    }

    [Fact]
    public void Supplier_DecreaseBalanceDue_ThrowsWhenAmountExceedsBalanceDue()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies");
        supplier.IncreaseBalanceDue(Money.FromMinorUnits(10000, Currency.INR));
        Assert.Equal(10000, supplier.BalanceDue.MinorUnits);

        var ex = Assert.Throws<DomainException>(() => supplier.DecreaseBalanceDue(Money.FromMinorUnits(15000, Currency.INR)));
        Assert.Equal("BALANCE_DUE_CANNOT_BE_NEGATIVE", ex.ErrorCode);
    }

    [Fact]
    public void Supplier_DecreaseCreditBalance_ThrowsWhenAmountExceedsCreditBalance()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies");
        supplier.IncreaseCreditBalance(Money.FromMinorUnits(5000, Currency.INR));
        Assert.Equal(5000, supplier.CreditBalance.MinorUnits);

        var ex = Assert.Throws<DomainException>(() => supplier.DecreaseCreditBalance(Money.FromMinorUnits(6000, Currency.INR)));
        Assert.Equal("CREDIT_BALANCE_CANNOT_BE_NEGATIVE", ex.ErrorCode);
    }

    [Fact]
    public void Supplier_ProfileUpdate_ValidatesNameAndTerms()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies", paymentTermsDays: 30);
        Assert.Equal("Acme Supplies", supplier.Name);
        Assert.Equal(30, supplier.PaymentTermsDays);

        supplier.UpdateProfile("Acme Global Supplies", "John Doe", "9876543210", "acme@example.com", "123 High St", "29AAAAA0000A1Z5", 45, "Trusted vendor");

        Assert.Equal("Acme Global Supplies", supplier.Name);
        Assert.Equal("John Doe", supplier.ContactPerson);
        Assert.Equal("9876543210", supplier.Phone);
        Assert.Equal("acme@example.com", supplier.Email);
        Assert.Equal(45, supplier.PaymentTermsDays);

        Assert.Throws<DomainException>(() => supplier.UpdateProfile("", null, null, null, null, null, 15, null));
        Assert.Throws<DomainException>(() => supplier.UpdateProfile("Valid Name", null, null, null, null, null, -1, null));
        Assert.Throws<DomainException>(() => supplier.UpdateProfile("Valid Name", null, null, null, null, null, 400, null));
    }

    [Fact]
    public void Supplier_ActivateAndDeactivate_UpdatesStateCorrectly()
    {
        var supplier = new Supplier(Guid.NewGuid(), "Acme Supplies");
        Assert.True(supplier.IsActive);

        supplier.Deactivate();
        Assert.False(supplier.IsActive);

        supplier.Activate();
        Assert.True(supplier.IsActive);
    }

    [Fact]
    public void PurchaseInvoice_Creation_ValidatesFinancialInvariants()
    {
        var supplierId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Negative subtotal
        Assert.Throws<DomainException>(() => new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", supplierId, new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(-100, Currency.INR), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(), Money.Zero(),
            PurchasePaymentStatus.Paid, userId));

        // Negative balance due
        Assert.Throws<DomainException>(() => new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", supplierId, new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(1000, Currency.INR), Money.FromMinorUnits(180, Currency.INR), Money.FromMinorUnits(1180, Currency.INR),
            Money.Zero(), Money.Zero(), Money.FromMinorUnits(-50, Currency.INR),
            PurchasePaymentStatus.Unpaid, userId));

        // Valid creation
        var invoice = new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", supplierId, new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(1000, Currency.INR), Money.FromMinorUnits(180, Currency.INR), Money.FromMinorUnits(1180, Currency.INR),
            Money.Zero(), Money.Zero(), Money.FromMinorUnits(1180, Currency.INR),
            PurchasePaymentStatus.Unpaid, userId);

        Assert.Equal("PI-001", invoice.InvoiceNumber);
        Assert.Equal("SUP-101", invoice.SupplierInvoiceNumber);
        Assert.Equal(PurchaseStatus.Received, invoice.Status);
        Assert.Equal(PurchasePaymentStatus.Unpaid, invoice.PaymentStatus);
        Assert.Equal(1180, invoice.BalanceDue.MinorUnits);
    }

    [Fact]
    public void PurchaseInvoice_RecordPayment_UpdatesBalanceAndPaymentStatusCorrectly()
    {
        var invoice = new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", Guid.NewGuid(), new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(10000, Currency.INR), Money.FromMinorUnits(1800, Currency.INR), Money.FromMinorUnits(11800, Currency.INR),
            Money.Zero(), Money.Zero(), Money.FromMinorUnits(11800, Currency.INR),
            PurchasePaymentStatus.Unpaid, Guid.NewGuid());

        // Partial payment
        invoice.RecordPayment(Money.FromMinorUnits(5000, Currency.INR));
        Assert.Equal(5000, invoice.AmountPaid.MinorUnits);
        Assert.Equal(6800, invoice.BalanceDue.MinorUnits);
        Assert.Equal(PurchasePaymentStatus.PartiallyPaid, invoice.PaymentStatus);

        // Final payment
        invoice.RecordPayment(Money.FromMinorUnits(6800, Currency.INR));
        Assert.Equal(11800, invoice.AmountPaid.MinorUnits);
        Assert.Equal(0, invoice.BalanceDue.MinorUnits);
        Assert.Equal(PurchasePaymentStatus.Paid, invoice.PaymentStatus);
    }

    [Fact]
    public void PurchaseInvoice_RecordPayment_ThrowsWhenExceedingBalanceDue()
    {
        var invoice = new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", Guid.NewGuid(), new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(1000, Currency.INR), Money.Zero(), Money.FromMinorUnits(1000, Currency.INR),
            Money.Zero(), Money.Zero(), Money.FromMinorUnits(1000, Currency.INR),
            PurchasePaymentStatus.Unpaid, Guid.NewGuid());

        var ex = Assert.Throws<DomainException>(() => invoice.RecordPayment(Money.FromMinorUnits(1500, Currency.INR)));
        Assert.Equal("PAYMENT_EXCEEDS_INVOICE_BALANCE", ex.ErrorCode);
    }

    [Fact]
    public void PurchaseInvoice_Cancel_SetsStatusAndExtinguishesBalanceDue()
    {
        var invoice = new PurchaseInvoice(
            Guid.NewGuid(), 1, "PI-001", "SUP-101", Guid.NewGuid(), new DateOnly(2026, 9, 1), DateTime.UtcNow,
            Money.FromMinorUnits(1000, Currency.INR), Money.Zero(), Money.FromMinorUnits(1000, Currency.INR),
            Money.Zero(), Money.Zero(), Money.FromMinorUnits(1000, Currency.INR),
            PurchasePaymentStatus.Unpaid, Guid.NewGuid());

        var cancelTxId = Guid.NewGuid();
        invoice.Cancel("Defective shipment returned", cancelTxId);

        Assert.Equal(PurchaseStatus.Cancelled, invoice.Status);
        Assert.Equal("Defective shipment returned", invoice.CancellationReason);
        Assert.Equal(cancelTxId, invoice.CancellationClientTransactionId);
        Assert.Equal(0, invoice.BalanceDue.MinorUnits);

        // Cannot double cancel
        Assert.Throws<DomainException>(() => invoice.Cancel("Another reason", Guid.NewGuid()));
    }

    [Fact]
    public void PurchaseItem_Creation_RequiresPositiveQuantityAndSnapshots()
    {
        var purchaseId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        // Empty SKU snapshot
        Assert.Throws<DomainException>(() => new PurchaseItem(
            0, purchaseId, productId, "", "Item Name", "PC",
            Quantity.From(10m, UnitOfMeasure.Piece), Money.FromMinorUnits(100, Currency.INR), 0m, Money.Zero(), Money.FromMinorUnits(1000, Currency.INR)));

        // Non-positive quantity
        Assert.Throws<DomainException>(() => new PurchaseItem(
            0, purchaseId, productId, "SKU-01", "Item Name", "PC",
            Quantity.From(0m, UnitOfMeasure.Piece), Money.FromMinorUnits(100, Currency.INR), 0m, Money.Zero(), Money.Zero()));

        // Negative unit cost
        Assert.Throws<DomainException>(() => new PurchaseItem(
            0, purchaseId, productId, "SKU-01", "Item Name", "PC",
            Quantity.From(10m, UnitOfMeasure.Piece), Money.FromMinorUnits(-50, Currency.INR), 0m, Money.Zero(), Money.Zero()));

        // Valid item
        var item = new PurchaseItem(
            1, purchaseId, productId, "SKU-01", "Test Product", "PC",
            Quantity.From(5m, UnitOfMeasure.Piece), Money.FromMinorUnits(1000, Currency.INR),
            18m, Money.FromMinorUnits(900, Currency.INR), Money.FromMinorUnits(5900, Currency.INR),
            "BATCH-01", new DateOnly(2027, 12, 31));

        Assert.Equal("SKU-01", item.ProductSku);
        Assert.Equal("BATCH-01", item.BatchNumber);
        Assert.Equal(new DateOnly(2027, 12, 31), item.ExpiryDate);
    }

    [Fact]
    public void PurchaseItem_AddTaxComponent_PreventsDuplicates()
    {
        var item = new PurchaseItem(
            1, Guid.NewGuid(), Guid.NewGuid(), "SKU-01", "Test Product", "PC",
            Quantity.From(5m, UnitOfMeasure.Piece), Money.FromMinorUnits(1000, Currency.INR),
            18m, Money.FromMinorUnits(900, Currency.INR), Money.FromMinorUnits(5900, Currency.INR));

        var cgst = new PurchaseItemTaxComponent(0, 1, "CGST", 9m, Money.FromMinorUnits(450, Currency.INR));
        var sgst = new PurchaseItemTaxComponent(0, 1, "SGST", 9m, Money.FromMinorUnits(450, Currency.INR));

        item.AddTaxComponent(cgst);
        item.AddTaxComponent(sgst);
        Assert.Equal(2, item.TaxComponents.Count);

        var duplicateCgst = new PurchaseItemTaxComponent(0, 1, "CGST", 9m, Money.FromMinorUnits(450, Currency.INR));
        var ex = Assert.Throws<DomainException>(() => item.AddTaxComponent(duplicateCgst));
        Assert.Equal("DUPLICATE_TAX_COMPONENT", ex.ErrorCode);
    }

    [Fact]
    public void SupplierPayment_Validation_DisallowsDisallowedPaymentMethods()
    {
        var storeId = 1;
        var supplierId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // Card not permitted
        Assert.Throws<DomainException>(() => new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.Card, "CARD-1234",
            DateTime.UtcNow, Guid.NewGuid(), userId));

        // Mixed not permitted
        Assert.Throws<DomainException>(() => new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.Mixed, null,
            DateTime.UtcNow, Guid.NewGuid(), userId));

        // Credit not permitted
        Assert.Throws<DomainException>(() => new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.Credit, null,
            DateTime.UtcNow, Guid.NewGuid(), userId));
    }

    [Fact]
    public void SupplierPayment_Validation_RequiresReferenceForNonCash()
    {
        var storeId = 1;
        var supplierId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // BankTransfer without reference
        var ex1 = Assert.Throws<DomainException>(() => new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.BankTransfer, null,
            DateTime.UtcNow, Guid.NewGuid(), userId));
        Assert.Equal("PAYMENT_REFERENCE_REQUIRED", ex1.ErrorCode);

        // Cheque without reference
        var ex2 = Assert.Throws<DomainException>(() => new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.Cheque, "   ",
            DateTime.UtcNow, Guid.NewGuid(), userId));
        Assert.Equal("PAYMENT_REFERENCE_REQUIRED", ex2.ErrorCode);

        // Cash with no reference is valid
        var cashPayment = new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.Cash, null,
            DateTime.UtcNow, Guid.NewGuid(), userId);
        Assert.Equal(PaymentMethod.Cash, cashPayment.Method);
        Assert.Null(cashPayment.ReferenceNumber);

        // BankTransfer with reference is valid
        var bankPayment = new SupplierPayment(
            Guid.NewGuid(), storeId, supplierId, SupplierPaymentType.Payment,
            Money.FromMinorUnits(5000, Currency.INR), PaymentMethod.BankTransfer, "NEFT-20260914-001",
            DateTime.UtcNow, Guid.NewGuid(), userId);
        Assert.Equal("NEFT-20260914-001", bankPayment.ReferenceNumber);
    }

    [Fact]
    public void SupplierPayment_Allocations_RequiresPositiveAmount()
    {
        var paymentId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        Assert.Throws<DomainException>(() => new SupplierPaymentAllocation(0, paymentId, invoiceId, Money.Zero()));
        Assert.Throws<DomainException>(() => new SupplierPaymentAllocation(0, paymentId, invoiceId, Money.FromMinorUnits(-100, Currency.INR)));

        var alloc = new SupplierPaymentAllocation(1, paymentId, invoiceId, Money.FromMinorUnits(2500, Currency.INR));
        Assert.Equal(2500, alloc.AmountAllocated.MinorUnits);
    }
}
