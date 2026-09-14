using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SqlitePhase7PurchaseIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqlitePhase7PurchaseIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_phase7_test_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
        DatabaseInitializer.InitializeDatabaseAsync(context).GetAwaiter().GetResult();
        SqliteConnectionConfigurator.ApplyPragmas(context);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private static (PurchaseService PurchaseService, SupplierService SupplierService, RetailDbContext Context) CreateServices(RetailDbContext context)
    {
        var uow = new EfUnitOfWork(context);
        var supplierRepo = new EfSupplierRepository(context);
        var purchaseRepo = new EfPurchaseRepository(context);
        var storeRepo = new EfStoreRepository(context);
        var productRepo = new EfProductRepository(context);
        var inventoryRepo = new EfInventoryRepository(context);
        var sequenceRepo = new EfInvoiceSequenceRepository(context);
        var auditRepo = new EfAuditLogRepository(context);

        var supplierService = new SupplierService(supplierRepo, purchaseRepo, storeRepo, auditRepo, uow);
        var purchaseService = new PurchaseService(purchaseRepo, supplierRepo, productRepo, inventoryRepo, storeRepo, sequenceRepo, auditRepo, uow);

        return (purchaseService, supplierService, context);
    }

    private static async Task<(Store Store, User User, Supplier Supplier, Product Product)> SeedStandardTestDataAsync(RetailDbContext context)
    {
        var store = new Store(1, "Test Store", "GST12345", "123 Market St", "9876543210");
        var user = new User(Guid.NewGuid(), "manager_user", "Manager User", "pin", "hash", "salt", RoleType.Manager);
        var supplier = new Supplier(Guid.NewGuid(), "Super Supplies Ltd", "Alice", "9876543210", "alice@supplies.com", "456 Vendor Road", "29AAAAA1111A1Z1", 30);
        
        var product = new Product(
            Guid.NewGuid(),
            "ITEM-001",
            "Basmati Rice 5kg",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(50000, Currency.INR),
            Money.FromMinorUnits(65000, Currency.INR),
            taxCategoryId: 3); // GST 18% seeded by DatabaseInitializer

        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, 0m, 5m);

        context.Stores.Add(store);
        context.Users.Add(user);
        context.Suppliers.Add(supplier);
        context.Products.Add(product);
        context.StockBalances.Add(balance);
        await context.SaveChangesAsync();

        return (store, user, supplier, product);
    }

    [Fact]
    public async Task CreatePurchase_Credit_IncreasesStockAndUpdatesSupplierBalanceDue()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        var request = new CreatePurchaseRequest(
            supplier.Id,
            "SUP-INV-1001",
            DateOnly.FromDateTime(DateTime.UtcNow),
            [
                new CreatePurchaseItemRequest(product.Id, 10m, 50000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])
            ],
            0,
            null,
            null,
            "First credit purchase inwarding",
            Guid.NewGuid());

        var response = await purchaseService.CreatePurchaseAsync(request, user.Id, user.Username, Guid.NewGuid());
        Assert.True(response.Success);
        var purchase = response.Data!;

        Assert.Equal(500000, purchase.SubtotalPaise);
        Assert.Equal(90000, purchase.TotalTaxPaise); // 18% GST of 500,000 = 90,000 paise
        Assert.Equal(590000, purchase.GrandTotalPaise);
        Assert.Equal(0, purchase.AmountPaidPaise);
        Assert.Equal(590000, purchase.BalanceDuePaise);
        Assert.Equal(PurchasePaymentStatus.Unpaid, purchase.PaymentStatus);

        // Verify stock balance increased by 10
        var stock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(stock);
        Assert.Equal(10m, stock.CurrentQuantity.Value);

        // Verify positive magnitude stock movement
        var movement = await context.StockMovements.FirstOrDefaultAsync(m => m.ProductId == product.Id);
        Assert.NotNull(movement);
        Assert.Equal(10m, movement.QuantityChanged.Value);
        Assert.Equal(StockMovementType.Purchase, movement.MovementType);

        // Verify supplier balance due updated
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(590000, updatedSupplier.BalanceDue.MinorUnits);
        Assert.Equal(0, updatedSupplier.CreditBalance.MinorUnits);

        // Verify historical tax components persisted
        var taxComponents = await context.PurchaseItemTaxComponents.ToListAsync();
        Assert.Equal(2, taxComponents.Count); // CGST 9% and SGST 9%
        Assert.Contains(taxComponents, tc => tc.ComponentName == "CGST" && tc.TaxAmount.MinorUnits == 45000);
        Assert.Contains(taxComponents, tc => tc.ComponentName == "SGST" && tc.TaxAmount.MinorUnits == 45000);
    }

    [Fact]
    public async Task CreatePurchase_Cash_InitialPaymentCreatesUnifiedSupplierPayment()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        var request = new CreatePurchaseRequest(
            supplier.Id,
            "SUP-INV-1002",
            DateOnly.FromDateTime(DateTime.UtcNow),
            [
                new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])
            ],
            23600,
            PaymentMethod.Cash,
            null,
            "Cash purchase with initial payment",
            Guid.NewGuid());

        var response = await purchaseService.CreatePurchaseAsync(request, user.Id, user.Username, Guid.NewGuid());
        Assert.True(response.Success);
        var purchase = response.Data!;

        Assert.Equal(23600, purchase.GrandTotalPaise);
        Assert.Equal(23600, purchase.AmountPaidPaise);
        Assert.Equal(0, purchase.BalanceDuePaise);
        Assert.Equal(PurchasePaymentStatus.Paid, purchase.PaymentStatus);

        // Verify unified supplier payment was recorded
        var payments = await context.SupplierPayments.Include(p => p.Allocations).ToListAsync();
        Assert.Single(payments);
        var payment = payments[0];
        Assert.Equal(23600, payment.Amount.MinorUnits);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Equal(SupplierPaymentType.Payment, payment.Type);
        Assert.Single(payment.Allocations);
        Assert.Equal(23600, payment.Allocations[0].AmountAllocated.MinorUnits);
        Assert.Equal(purchase.Id, payment.Allocations[0].PurchaseInvoiceId);

        // Supplier BalanceDue should remain 0
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(0, updatedSupplier.BalanceDue.MinorUnits);
        Assert.Equal(0, updatedSupplier.CreditBalance.MinorUnits);
    }

    [Fact]
    public async Task CreatePurchase_WithStoreCreditOffset()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // Pre-credit supplier with 10,000 paise credit balance
        supplier.IncreaseCreditBalance(Money.FromMinorUnits(10000, Currency.INR));
        await context.SaveChangesAsync();

        var request = new CreatePurchaseRequest(
            supplier.Id,
            "SUP-INV-1003",
            DateOnly.FromDateTime(DateTime.UtcNow),
            [
                new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])
            ],
            0,
            null,
            null,
            "Credit purchase using credit balance",
            Guid.NewGuid());

        var response = await purchaseService.CreatePurchaseAsync(request, user.Id, user.Username, Guid.NewGuid());
        Assert.True(response.Success);
        var purchase = response.Data!;

        Assert.Equal(23600, purchase.GrandTotalPaise);
        Assert.Equal(10000, purchase.CreditOffsetAmountPaise);
        Assert.Equal(13600, purchase.BalanceDuePaise);
        Assert.Equal(PurchasePaymentStatus.PartiallyPaid, purchase.PaymentStatus);

        // Supplier CreditBalance should be exhausted to 0, BalanceDue should be 13,600
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(0, updatedSupplier.CreditBalance.MinorUnits);
        Assert.Equal(13600, updatedSupplier.BalanceDue.MinorUnits);
    }

    [Fact]
    public async Task SupplierPayment_InvoiceSpecific_ReducesBalanceDueOnInvoiceAndSupplier()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, supplierService, _) = CreateServices(context);

        var purchaseReq = new CreatePurchaseRequest(
            supplier.Id,
            "SUP-INV-1004",
            DateOnly.FromDateTime(DateTime.UtcNow),
            [
                new CreatePurchaseItemRequest(product.Id, 1m, 100000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])
            ],
            0,
            null,
            null,
            "Invoice-specific payment test",
            Guid.NewGuid());

        var purchaseResp = await purchaseService.CreatePurchaseAsync(purchaseReq, user.Id, user.Username, Guid.NewGuid());
        var purchaseId = purchaseResp.Data!.Id;

        // Record partial payment of 50,000 paise via BankTransfer
        var paymentReq = new RecordSupplierPaymentRequest(
            purchaseId,
            50000,
            PaymentMethod.BankTransfer,
            "NEFT-2026-09-001",
            null,
            false,
            "Partial invoice payment",
            Guid.NewGuid());

        var paymentResp = await supplierService.RecordPaymentAsync(supplier.Id, paymentReq, user.Id, user.Username);
        Assert.True(paymentResp.Success);

        // Check invoice
        var invoice = await context.PurchaseInvoices.FindAsync(purchaseId);
        Assert.NotNull(invoice);
        Assert.Equal(50000, invoice.AmountPaid.MinorUnits);
        Assert.Equal(68000, invoice.BalanceDue.MinorUnits);
        Assert.Equal(PurchasePaymentStatus.PartiallyPaid, invoice.PaymentStatus);

        // Check supplier
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(68000, updatedSupplier.BalanceDue.MinorUnits);
    }

    [Fact]
    public async Task SupplierPayment_FifoAutoAllocation_DistributesAcrossMultipleInvoices()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, supplierService, _) = CreateServices(context);

        // Create 2 credit invoices:
        // Inv 1: Rs 100 + GST = 11,800 paise
        var p1 = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-A", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)),
                [new CreatePurchaseItemRequest(product.Id, 1m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "FIFO 1", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        // Inv 2: Rs 200 + GST = 23,600 paise
        var p2 = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-B", DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
                [new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "FIFO 2", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        // Total outstanding = 35,400 paise.
        // Pay 20,000 paise with FIFO auto-allocation (no PurchaseInvoiceId specified)
        var paymentReq = new RecordSupplierPaymentRequest(
            null,
            20000,
            PaymentMethod.Cash,
            null,
            null,
            false,
            "FIFO lump-sum payment",
            Guid.NewGuid());

        var paymentResp = await supplierService.RecordPaymentAsync(supplier.Id, paymentReq, user.Id, user.Username);
        Assert.True(paymentResp.Success);

        // Inv 1 should be fully paid (11,800 allocated, balance 0)
        var inv1 = await context.PurchaseInvoices.FindAsync(p1.Data!.Id);
        Assert.NotNull(inv1);
        Assert.Equal(0, inv1.BalanceDue.MinorUnits);
        Assert.Equal(PurchasePaymentStatus.Paid, inv1.PaymentStatus);

        // Inv 2 should receive remaining 8,200 paise (balance = 23,600 - 8,200 = 15,400)
        var inv2 = await context.PurchaseInvoices.FindAsync(p2.Data!.Id);
        Assert.NotNull(inv2);
        Assert.Equal(8200, inv2.AmountPaid.MinorUnits);
        Assert.Equal(15400, inv2.BalanceDue.MinorUnits);
        Assert.Equal(PurchasePaymentStatus.PartiallyPaid, inv2.PaymentStatus);

        // Supplier balance due should equal remaining 15,400
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(15400, updatedSupplier.BalanceDue.MinorUnits);
    }

    [Fact]
    public async Task SupplierPayment_AdvancePayment_IncreasesCreditBalance()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, supplierService, _) = CreateServices(context);

        // 1 invoice of 11,800 paise
        var p = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-ADV", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(product.Id, 1m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "Adv test", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        // Pay 20,000 paise with AllowAdvancePayment = true
        var paymentReq = new RecordSupplierPaymentRequest(
            null,
            20000,
            PaymentMethod.Cheque,
            "CHQ-998877",
            null,
            true, // Allow advance
            "Advance payment",
            Guid.NewGuid());

        var paymentResp = await supplierService.RecordPaymentAsync(supplier.Id, paymentReq, user.Id, user.Username);
        Assert.True(paymentResp.Success);

        // Invoice is paid
        var inv = await context.PurchaseInvoices.FindAsync(p.Data!.Id);
        Assert.NotNull(inv);
        Assert.Equal(0, inv.BalanceDue.MinorUnits);

        // Supplier balance due is 0, credit balance is 8,200
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(0, updatedSupplier.BalanceDue.MinorUnits);
        Assert.Equal(8200, updatedSupplier.CreditBalance.MinorUnits);
    }

    [Fact]
    public async Task CancelPurchase_SupplierPayoutMode_ReversesStockAndRecordsRefund()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // Inward and pay cash (23,600 paise)
        var p = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-CANCEL-1", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                23600, PaymentMethod.Cash, null, "To be cancelled", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        var purchaseId = p.Data!.Id;

        // Cancel with SupplierPayout mode
        var cancelReq = new CancelPurchaseRequest(
            "Supplier sent defective batch",
            RefundSettlementMode.SupplierPayout,
            PaymentMethod.Cash,
            null,
            Guid.NewGuid());

        var cancelResp = await purchaseService.CancelPurchaseAsync(purchaseId, cancelReq, user.Id, user.Username);
        Assert.True(cancelResp.Success);

        // Stock reversed back to 0
        var stock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(stock);
        Assert.Equal(0m, stock.CurrentQuantity.Value);

        // Stock movement of type PurchaseReturn recorded
        var movements = await context.StockMovements.Where(m => m.ProductId == product.Id).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Contains(movements, m => m.MovementType == StockMovementType.PurchaseReturn && m.QuantityChanged.Value == 2m);

        // Invoice status is Cancelled
        var inv = await context.PurchaseInvoices.FindAsync(purchaseId);
        Assert.NotNull(inv);
        Assert.Equal(PurchaseStatus.Cancelled, inv.Status);

        // RefundFromSupplier payment created
        var refundPayment = await context.SupplierPayments.FirstOrDefaultAsync(pay => pay.Type == SupplierPaymentType.RefundFromSupplier);
        Assert.NotNull(refundPayment);
        Assert.Equal(23600, refundPayment.Amount.MinorUnits);
    }

    [Fact]
    public async Task CancelPurchase_StoreCreditMode_IncreasesSupplierCreditBalance()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // Inward and pay cash
        var p = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-CANCEL-2", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                23600, PaymentMethod.Cash, null, "Cancel for credit", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        var cancelReq = new CancelPurchaseRequest(
            "Keep as credit for next delivery",
            RefundSettlementMode.StoreCredit,
            null,
            null,
            Guid.NewGuid());

        var cancelResp = await purchaseService.CancelPurchaseAsync(p.Data!.Id, cancelReq, user.Id, user.Username);
        Assert.True(cancelResp.Success);

        // Supplier credit balance increased by 23,600
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(23600, updatedSupplier.CreditBalance.MinorUnits);
        Assert.Equal(0, updatedSupplier.BalanceDue.MinorUnits);
    }

    [Fact]
    public async Task CancelPurchase_UnpaidCreditPurchase_ReducesSupplierBalanceDue()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // Unpaid credit purchase
        var p = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-CANCEL-3", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(product.Id, 2m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "Unpaid credit", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        var cancelReq = new CancelPurchaseRequest(
            "Cancelled before payment",
            RefundSettlementMode.StoreCredit,
            null,
            null,
            Guid.NewGuid());

        var cancelResp = await purchaseService.CancelPurchaseAsync(p.Data!.Id, cancelReq, user.Id, user.Username);
        Assert.True(cancelResp.Success);

        // Supplier BalanceDue reduced back to 0
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(0, updatedSupplier.BalanceDue.MinorUnits);
        Assert.Equal(0, updatedSupplier.CreditBalance.MinorUnits);
    }

    [Fact]
    public async Task Idempotency_PurchaseCreation_DuplicateClientTransactionIdReturnsExisting()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        var clientTxId = Guid.NewGuid();
        var req = new CreatePurchaseRequest(
            supplier.Id, "INV-IDEM", DateOnly.FromDateTime(DateTime.UtcNow),
            [new CreatePurchaseItemRequest(product.Id, 5m, 10000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
            0, null, null, "Idempotent test", clientTxId);

        var resp1 = await purchaseService.CreatePurchaseAsync(req, user.Id, user.Username, Guid.NewGuid());
        var resp2 = await purchaseService.CreatePurchaseAsync(req, user.Id, user.Username, Guid.NewGuid());

        Assert.True(resp1.Success);
        Assert.True(resp2.Success);
        Assert.Equal(resp1.Data!.Id, resp2.Data!.Id);

        // Ensure stock only increased by 5, not 10
        var stock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(stock);
        Assert.Equal(5m, stock.CurrentQuantity.Value);
    }

    [Fact]
    public async Task Idempotency_SupplierPayment_DuplicateClientTransactionIdReturnsExisting()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, supplierService, _) = CreateServices(context);

        var p = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(supplier.Id, "INV-PAY-IDEM", DateOnly.FromDateTime(DateTime.UtcNow),
                [new CreatePurchaseItemRequest(product.Id, 1m, 50000, [new PurchaseTaxComponentInputDto("CGST", 9m), new PurchaseTaxComponentInputDto("SGST", 9m)])],
                0, null, null, "Pay idem", Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());

        var clientTxId = Guid.NewGuid();
        var payReq = new RecordSupplierPaymentRequest(
            p.Data!.Id, 20000, PaymentMethod.Cash, null, null, false, "Idempotent payment", clientTxId);

        var pay1 = await supplierService.RecordPaymentAsync(supplier.Id, payReq, user.Id, user.Username);
        var pay2 = await supplierService.RecordPaymentAsync(supplier.Id, payReq, user.Id, user.Username);

        Assert.True(pay1.Success);
        Assert.True(pay2.Success);
        Assert.Equal(pay1.Data!.Id, pay2.Data!.Id);

        // Ensure supplier balance deducted only once
        var inv = await context.PurchaseInvoices.FindAsync(p.Data!.Id);
        Assert.NotNull(inv);
        Assert.Equal(20000, inv.AmountPaid.MinorUnits);
    }

    [Fact]
    public async Task Concurrency_ConcurrentSupplierPayments_SucceedsWithoutCorruptingBalances()
    {
        var supplierId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var storeId = 1;

        // Initialize shared test store and supplier
        using (var initContext = new RetailDbContext(_options))
        {
            var store = new Store(storeId, "Concurrent Store", "GST", "Address", "Phone");
            var user = new User(userId, "admin", "Admin User", "pin", "hash", "salt", RoleType.Admin);
            var supplier = new Supplier(supplierId, "Concurrent Supplier");
            // Set balance due to 100,000 paise (Rs 1000)
            supplier.IncreaseBalanceDue(Money.FromMinorUnits(100000, Currency.INR));

            initContext.Stores.Add(store);
            initContext.Users.Add(user);
            initContext.Suppliers.Add(supplier);
            await initContext.SaveChangesAsync();
        }

        // Run 10 concurrent payments of 5,000 paise each with allow advance = true
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            using var threadContext = new RetailDbContext(_options);
            var (_, supplierService, _) = CreateServices(threadContext);

            var payReq = new RecordSupplierPaymentRequest(
                null,
                5000,
                PaymentMethod.Cash,
                null,
                null,
                true,
                $"Concurrent payment {i}",
                Guid.NewGuid());

            return await supplierService.RecordPaymentAsync(supplierId, payReq, userId, "admin");
        });

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Success));

        // Total paid: 10 * 5,000 = 50,000 paise.
        // Balance due should be 100,000 - 50,000 = 50,000 paise.
        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalSupplier = await verifyContext.Suppliers.FindAsync(supplierId);
            Assert.NotNull(finalSupplier);
            Assert.Equal(50000, finalSupplier.BalanceDue.MinorUnits);
        }
    }

    [Fact]
    public async Task SqlitePhase7Migration_ExistingPhase6Database_UpgradesSuccessfully()
    {
        var migrationDbPath = Path.Combine(Path.GetTempPath(), $"retailpos_mig_test_{Guid.NewGuid():N}.db");
        var migrationOptions = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={migrationDbPath}")
            .Options;

        try
        {
            // 1. Create a simulated pre-Phase 7 database
            using (var connection = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();

                // Create minimal legacy tables without Phase 7 columns
                cmd.CommandText = @"
                    CREATE TABLE stores (
                        Id INTEGER PRIMARY KEY,
                        StoreName TEXT NOT NULL,
                        TaxRegistrationNumber TEXT,
                        Address TEXT,
                        Phone TEXT,
                        ReceiptHeader TEXT,
                        ReceiptFooter TEXT,
                        ReceiptPrintLogo INTEGER NOT NULL DEFAULT 0,
                        ShowStoreAddress INTEGER NOT NULL DEFAULT 1,
                        ShowStorePhone INTEGER NOT NULL DEFAULT 1,
                        ShowStoreGst INTEGER NOT NULL DEFAULT 1,
                        CashRoundingEnabled INTEGER NOT NULL DEFAULT 1,
                        TimeZoneId TEXT NOT NULL DEFAULT 'India Standard Time',
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    INSERT INTO stores (Id, StoreName, CreatedAtUtc, UpdatedAtUtc) VALUES (1, 'Legacy Store', '2026-01-01', '2026-01-01');

                    CREATE TABLE users (
                        Id TEXT PRIMARY KEY,
                        Username TEXT NOT NULL,
                        FullName TEXT NOT NULL,
                        PinHash TEXT NOT NULL,
                        PasswordHash TEXT NOT NULL,
                        Salt TEXT NOT NULL,
                        Role INTEGER NOT NULL,
                        IsActive INTEGER NOT NULL DEFAULT 1,
                        FailedPinAttempts INTEGER NOT NULL DEFAULT 0,
                        LockoutEndUtc TEXT NULL,
                        CreatedAtUtc TEXT NOT NULL,
                        UpdatedAtUtc TEXT NOT NULL
                    );
                    INSERT INTO users (Id, Username, FullName, PinHash, PasswordHash, Salt, Role, IsActive, CreatedAtUtc, UpdatedAtUtc)
                    VALUES ('33333333-3333-3333-3333-333333333333', 'admin', 'Admin User', 'pin', 'hash', 'salt', 3, 1, '2026-01-01', '2026-01-01');

                    CREATE TABLE suppliers (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        ContactPerson TEXT,
                        Phone TEXT,
                        TaxNumber TEXT,
                        BalanceDue INTEGER NOT NULL DEFAULT 0,
                        IsActive INTEGER NOT NULL DEFAULT 1
                    );
                    INSERT INTO suppliers (Id, Name, BalanceDue, IsActive) VALUES ('11111111-1111-1111-1111-111111111111', 'Legacy Supplier', 1000, 1);

                    CREATE TABLE purchase_invoices (
                        Id TEXT PRIMARY KEY,
                        InvoiceNumber TEXT NOT NULL,
                        SupplierId TEXT NOT NULL,
                        Subtotal INTEGER NOT NULL,
                        TotalTax INTEGER NOT NULL,
                        GrandTotal INTEGER NOT NULL,
                        Status INTEGER NOT NULL
                    );
                    INSERT INTO purchase_invoices (Id, InvoiceNumber, SupplierId, Subtotal, TotalTax, GrandTotal, Status)
                    VALUES ('22222222-2222-2222-2222-222222222222', 'PI-LEGACY-01', '11111111-1111-1111-1111-111111111111', 1000, 180, 1180, 3);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Run DatabaseInitializer against the legacy database
            using (var context = new RetailDbContext(migrationOptions))
            {
                await DatabaseInitializer.UpgradeSchemaIfNeededAsync(context);
            }

            // 3. Verify columns and data in upgraded database
            using (var connection = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();

                // Verify suppliers has CreditBalance and Email
                cmd.CommandText = "PRAGMA table_info(suppliers);";
                var supCols = new List<string>();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync()) supCols.Add(reader.GetString(1));
                }
                Assert.Contains("CreditBalance", supCols);
                Assert.Contains("Email", supCols);

                // Verify purchase_invoices has StoreId, AmountPaid, BalanceDue
                cmd.CommandText = "PRAGMA table_info(purchase_invoices);";
                var piCols = new List<string>();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync()) piCols.Add(reader.GetString(1));
                }
                Assert.Contains("StoreId", piCols);
                Assert.Contains("AmountPaid", piCols);
                Assert.Contains("BalanceDue", piCols);
                Assert.Contains("ClientTransactionId", piCols);

                // Verify migrated purchase_invoices row resolved StoreId to 1 and BalanceDue = GrandTotal (1180)
                cmd.CommandText = "SELECT StoreId, BalanceDue, SupplierInvoiceNumber, CreatedByUserId FROM purchase_invoices WHERE Id = '22222222-2222-2222-2222-222222222222';";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal(1, reader.GetInt32(0));
                    Assert.Equal(1180, reader.GetInt32(1));
                    Assert.Equal("PI-LEGACY-01", reader.GetString(2));
                    Assert.Equal("33333333-3333-3333-3333-333333333333", reader.GetString(3));
                }

                // Verify purchase_item_tax_components table exists and enforces ON DELETE RESTRICT
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='purchase_item_tax_components';";
                var taxCompTableSql = (string?)await cmd.ExecuteScalarAsync();
                Assert.NotNull(taxCompTableSql);
                Assert.Contains("ON DELETE RESTRICT", taxCompTableSql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("CASCADE", taxCompTableSql, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (File.Exists(migrationDbPath))
            {
                try { File.Delete(migrationDbPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task CreatePurchase_CreditBalanceConsumedFirst_ThenInitialPayment_ThenRemainingBecomesBalanceDue()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // 1. Supplier starts with CreditBalance = 3,000 paise (Rs 30.00), BalanceDue = 0
        supplier.IncreaseCreditBalance(Money.FromMinorUnits(3000, Currency.INR));
        await context.SaveChangesAsync();

        Assert.Equal(3000, supplier.CreditBalance.MinorUnits);
        Assert.Equal(0, supplier.BalanceDue.MinorUnits);

        // 2. Inward purchase: 1 unit @ 10,000 paise (Rs 100) + 18% GST (CGST 9% = 900, SGST 9% = 900)
        // Grand Total = 11,800 paise.
        // Initial payment requested = 5,000 paise (Rs 50.00) via Cash.
        var req = new CreatePurchaseRequest(
            supplier.Id,
            "INV-ORDER-001",
            DateOnly.FromDateTime(DateTime.UtcNow),
            [
                new CreatePurchaseItemRequest(
                    product.Id,
                    1m,
                    10000,
                    [
                        new PurchaseTaxComponentInputDto("CGST", 9m),
                        new PurchaseTaxComponentInputDto("SGST", 9m)
                    ])
            ],
            5000,
            PaymentMethod.Cash,
            null,
            "Credit-offset order verification",
            Guid.NewGuid());

        var resp = await purchaseService.CreatePurchaseAsync(req, user.Id, user.Username, Guid.NewGuid());
        Assert.True(resp.Success, resp.ErrorMessage);

        var invoice = resp.Data!;
        // GrandTotal = 10,000 + 1,800 = 11,800 paise
        Assert.Equal(10000, invoice.SubtotalPaise);
        Assert.Equal(1800, invoice.TotalTaxPaise);
        Assert.Equal(11800, invoice.GrandTotalPaise);

        // Step A: Supplier CreditBalance consumed first -> 3,000 paise
        Assert.Equal(3000, invoice.CreditOffsetAmountPaise);

        // Step B: Initial payment applied -> 5,000 paise
        // Total AmountPaid on invoice = CreditOffset (3,000) + InitialPayment (5,000) = 8,000 paise
        Assert.Equal(8000, invoice.AmountPaidPaise);

        // Step C: Remaining becomes BalanceDue -> 11,800 - 8,000 = 3,800 paise
        Assert.Equal(3800, invoice.BalanceDuePaise);
        Assert.Equal(PurchasePaymentStatus.PartiallyPaid, invoice.PaymentStatus);

        // Supplier balances after operation:
        // CreditBalance: 3,000 - 3,000 = 0 paise
        // BalanceDue: 0 + 3,800 = 3,800 paise
        var updatedSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(updatedSupplier);
        Assert.Equal(0, updatedSupplier.CreditBalance.MinorUnits);
        Assert.Equal(3800, updatedSupplier.BalanceDue.MinorUnits);

        // Verify initial payment record
        var payment = await context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.SupplierId == supplier.Id && p.Type == SupplierPaymentType.Payment);
        Assert.NotNull(payment);
        Assert.Equal(5000, payment.Amount.MinorUnits);
        Assert.Equal(PaymentMethod.Cash, payment.Method);
        Assert.Single(payment.Allocations);
        Assert.Equal(invoice.Id, payment.Allocations[0].PurchaseInvoiceId);
        Assert.Equal(5000, payment.Allocations[0].AmountAllocated.MinorUnits);
    }

    [Fact]
    public async Task CancelPurchase_PartiallyPaid_SupplierPayout_ReversesStock_RestoresSupplierBalances_AndPreservesOriginalPaymentsImmutably()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // 1. Inward purchase: 2 units @ 10,000 paise + 18% GST = 23,600 paise
        // Initial payment: 10,000 paise via Cash. Remaining BalanceDue = 13,600 paise.
        var pResp = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(
                supplier.Id,
                "INV-CANCEL-PART-PAYOUT",
                DateOnly.FromDateTime(DateTime.UtcNow),
                [
                    new CreatePurchaseItemRequest(
                        product.Id,
                        2m,
                        10000,
                        [
                            new PurchaseTaxComponentInputDto("CGST", 9m),
                            new PurchaseTaxComponentInputDto("SGST", 9m)
                        ])
                ],
                10000,
                PaymentMethod.Cash,
                null,
                "Partially paid purchase",
                Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());
        Assert.True(pResp.Success);
        var invoiceId = pResp.Data!.Id;

        // Check pre-cancellation state
        var preSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(preSupplier);
        Assert.Equal(13600, preSupplier.BalanceDue.MinorUnits);
        Assert.Equal(0, preSupplier.CreditBalance.MinorUnits);

        var preStock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(preStock);
        Assert.Equal(2m, preStock.CurrentQuantity.Value);

        var originalPayment = await context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.SupplierId == supplier.Id && p.Type == SupplierPaymentType.Payment);
        Assert.NotNull(originalPayment);
        var originalPaymentId = originalPayment.Id;
        var originalAllocId = originalPayment.Allocations[0].Id;
        Assert.Equal(10000, originalPayment.Amount.MinorUnits);

        // 2. Cancel purchase with SupplierPayout (Cash refund from supplier for the 10,000 paise paid)
        var cancelReq = new CancelPurchaseRequest(
            "Batch rejected on inspection",
            RefundSettlementMode.SupplierPayout,
            PaymentMethod.Cash,
            null,
            Guid.NewGuid());

        var cancelResp = await purchaseService.CancelPurchaseAsync(invoiceId, cancelReq, user.Id, user.Username);
        Assert.True(cancelResp.Success);

        // 3. Verify stock reversal: back to 0
        var postStock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(postStock);
        Assert.Equal(0m, postStock.CurrentQuantity.Value);

        // Verify stock movement: PurchaseReturn with positive magnitude 2
        var movements = await context.StockMovements
            .Where(m => m.ProductId == product.Id && m.MovementType == StockMovementType.PurchaseReturn)
            .ToListAsync();
        Assert.Single(movements);
        Assert.Equal(2m, movements[0].QuantityChanged.Value);

        // 4. Verify supplier balances:
        // BalanceDue was 13,600, reduced back to 0.
        // CreditBalance remains 0 (because payout was returned to store).
        var postSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(postSupplier);
        Assert.Equal(0, postSupplier.BalanceDue.MinorUnits);
        Assert.Equal(0, postSupplier.CreditBalance.MinorUnits);

        // 5. Verify refund record created for SupplierPayout:
        var refundPayment = await context.SupplierPayments
            .FirstOrDefaultAsync(p => p.SupplierId == supplier.Id && p.Type == SupplierPaymentType.RefundFromSupplier);
        Assert.NotNull(refundPayment);
        Assert.Equal(10000, refundPayment.Amount.MinorUnits);
        Assert.Equal(PaymentMethod.Cash, refundPayment.Method);

        // 6. Verify IMMUTABILITY of original payment and allocation:
        var reloadedOriginalPayment = await context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == originalPaymentId);
        Assert.NotNull(reloadedOriginalPayment);
        Assert.Equal(10000, reloadedOriginalPayment.Amount.MinorUnits);
        Assert.Equal(SupplierPaymentType.Payment, reloadedOriginalPayment.Type);
        Assert.Single(reloadedOriginalPayment.Allocations);
        Assert.Equal(originalAllocId, reloadedOriginalPayment.Allocations[0].Id);
        Assert.Equal(10000, reloadedOriginalPayment.Allocations[0].AmountAllocated.MinorUnits);
    }

    [Fact]
    public async Task CancelPurchase_PartiallyPaid_StoreCredit_ReversesStock_AdjustsBothBalances_AndPreservesOriginalPaymentsImmutably()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // 1. Inward purchase: 2 units @ 10,000 paise + 18% GST = 23,600 paise
        // Initial payment: 8,000 paise via Cash. Remaining BalanceDue = 15,600 paise.
        var pResp = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(
                supplier.Id,
                "INV-CANCEL-PART-CREDIT",
                DateOnly.FromDateTime(DateTime.UtcNow),
                [
                    new CreatePurchaseItemRequest(
                        product.Id,
                        2m,
                        10000,
                        [
                            new PurchaseTaxComponentInputDto("CGST", 9m),
                            new PurchaseTaxComponentInputDto("SGST", 9m)
                        ])
                ],
                8000,
                PaymentMethod.Cash,
                null,
                "Partially paid for store credit cancellation",
                Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());
        Assert.True(pResp.Success);
        var invoiceId = pResp.Data!.Id;

        var originalPayment = await context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.SupplierId == supplier.Id && p.Type == SupplierPaymentType.Payment);
        Assert.NotNull(originalPayment);
        var originalPaymentId = originalPayment.Id;

        // 2. Cancel purchase with StoreCredit (retained by supplier as credit for future purchases)
        var cancelReq = new CancelPurchaseRequest(
            "Return items for credit note",
            RefundSettlementMode.StoreCredit,
            null,
            null,
            Guid.NewGuid());

        var cancelResp = await purchaseService.CancelPurchaseAsync(invoiceId, cancelReq, user.Id, user.Username);
        Assert.True(cancelResp.Success);

        // 3. Stock reversed to 0
        var postStock = await context.StockBalances.FirstOrDefaultAsync(s => s.ProductId == product.Id);
        Assert.NotNull(postStock);
        Assert.Equal(0m, postStock.CurrentQuantity.Value);

        // 4. Supplier balances:
        // BalanceDue decreased by unpaid invoice balance (15,600) -> 0 paise.
        // CreditBalance increased by paid invoice amount (8,000) -> 8,000 paise.
        var postSupplier = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(postSupplier);
        Assert.Equal(0, postSupplier.BalanceDue.MinorUnits);
        Assert.Equal(8000, postSupplier.CreditBalance.MinorUnits);

        // 5. Original payment is immutable:
        var reloadedPayment = await context.SupplierPayments
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == originalPaymentId);
        Assert.NotNull(reloadedPayment);
        Assert.Equal(8000, reloadedPayment.Amount.MinorUnits);
        Assert.Single(reloadedPayment.Allocations);
        Assert.Equal(8000, reloadedPayment.Allocations[0].AmountAllocated.MinorUnits);
    }

    [Fact]
    public async Task PurchaseItemTaxComponent_RestrictDelete_PreventsSilentDeletionWhenPurchaseItemDeleted()
    {
        using var context = new RetailDbContext(_options);
        var (store, user, supplier, product) = await SeedStandardTestDataAsync(context);
        var (purchaseService, _, _) = CreateServices(context);

        // 1. Inward purchase with tax components
        var pResp = await purchaseService.CreatePurchaseAsync(
            new CreatePurchaseRequest(
                supplier.Id,
                "INV-RESTRICT-TEST",
                DateOnly.FromDateTime(DateTime.UtcNow),
                [
                    new CreatePurchaseItemRequest(
                        product.Id,
                        1m,
                        10000,
                        [
                            new PurchaseTaxComponentInputDto("CGST", 9m),
                            new PurchaseTaxComponentInputDto("SGST", 9m)
                        ])
                ],
                0,
                null,
                null,
                "Restrict FK test",
                Guid.NewGuid()),
            user.Id, user.Username, Guid.NewGuid());
        Assert.True(pResp.Success);

        // 2. Get the purchase item ID and verify tax components exist
        var item = await context.PurchaseItems.Include(i => i.TaxComponents).FirstOrDefaultAsync(i => i.PurchaseId == pResp.Data!.Id);
        Assert.NotNull(item);
        Assert.Equal(2, item.TaxComponents.Count);
        var itemId = item.Id;

        // 3. Attempt to delete purchase_items row directly while purchase_item_tax_components records reference it
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();
        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync();

        using var deleteCmd = connection.CreateCommand();
        deleteCmd.CommandText = $"DELETE FROM purchase_items WHERE Id = {itemId};";

        // Must fail with foreign key constraint violation (SQLITE_CONSTRAINT_FOREIGNKEY / 19)
        var ex = await Assert.ThrowsAsync<SqliteException>(() => deleteCmd.ExecuteNonQueryAsync());
        Assert.Equal(19, ex.SqliteErrorCode);

        // 4. Verify historical tax component records were NOT silently deleted
        using var verifyCmd = connection.CreateCommand();
        verifyCmd.CommandText = $"SELECT COUNT(*) FROM purchase_item_tax_components WHERE PurchaseItemId = {itemId};";
        var count = Convert.ToInt32(await verifyCmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        Assert.Equal(2, count);
    }
}
