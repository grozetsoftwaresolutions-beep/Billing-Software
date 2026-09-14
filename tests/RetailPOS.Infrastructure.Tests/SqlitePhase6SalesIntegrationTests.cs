using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqlitePhase6SalesIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;
    private readonly object _dbLock = new();

    public SqlitePhase6SalesIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pos_phase6_{Guid.NewGuid():N}.db");
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
        GC.SuppressFinalize(this);
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task SqliteHistoricalTaxComponentTests_PersistsAndNeverAltersHistoricalComponents()
    {
        var saleId = Guid.NewGuid();
        long saleItemId = 1001;
        var productId = Guid.NewGuid();

        // 1. Seed Sale with Historical Tax Components (CGST 9% and SGST 9%)
        using (var seedContext = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(8475),
                Money.FromMinorUnits(1525),
                Money.Zero(),
                Money.FromMinorUnits(10000));

            var item = new SaleItem(
                saleItemId,
                saleId,
                productId,
                "TEST-SKU",
                "Product With 18% GST",
                Quantity.From(1, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000), // unitSellingPrice
                Money.FromMinorUnits(6000),  // unitCostPrice
                Money.FromMinorUnits(10000), // grossAmount
                Money.Zero(),                // discountAmount
                18.00m,                      // taxRatePercentage
                Money.FromMinorUnits(8475),  // taxableAmount
                Money.FromMinorUnits(1525),  // taxAmount
                Money.FromMinorUnits(10000));// lineTotal

            var cgst = new SaleItemTaxComponent(1, saleItemId, "CGST", 9.00m, Money.FromMinorUnits(763));
            var sgst = new SaleItemTaxComponent(2, saleItemId, "SGST", 9.00m, Money.FromMinorUnits(762));
            item.AddTaxComponent(cgst);
            item.AddTaxComponent(sgst);

            invoice.AddItem(item);
            seedContext.SalesInvoices.Add(invoice);
            await seedContext.SaveChangesAsync();
        }

        // 2. Query back in a clean context and verify persisted components
        using (var verifyContext = new RetailDbContext(_options))
        {
            var invoice = await verifyContext.SalesInvoices
                .Include(s => s.Items)
                .ThenInclude(i => i.TaxComponents)
                .FirstOrDefaultAsync(s => s.Id == saleId);

            Assert.NotNull(invoice);
            Assert.Single(invoice.Items);
            var item = invoice.Items[0];
            Assert.Equal(2, item.TaxComponents.Count);

            var cgstComp = item.TaxComponents.First(c => c.ComponentName == "CGST");
            Assert.Equal(9.00m, cgstComp.RatePercentage);
            Assert.Equal(763, cgstComp.Amount.MinorUnits);

            var sgstComp = item.TaxComponents.First(c => c.ComponentName == "SGST");
            Assert.Equal(9.00m, sgstComp.RatePercentage);
            Assert.Equal(762, sgstComp.Amount.MinorUnits);
        }
    }

    [Fact]
    public async Task SqliteHistoricalTaxComponent_DeleteSaleItem_ThrowsDbUpdateException_DueToOnDeleteRestrict()
    {
        var saleId = Guid.NewGuid();
        long saleItemId = 2001;
        var productId = Guid.NewGuid();

        // 1. Seed Sale, SaleItem, and SaleItemTaxComponent
        using (var seedContext = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000099",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(4237),
                Money.FromMinorUnits(763),
                Money.Zero(),
                Money.FromMinorUnits(5000));

            var item = new SaleItem(
                saleItemId,
                saleId,
                productId,
                "RESTRICT-SKU",
                "Restrict Test Product",
                Quantity.From(1, UnitOfMeasure.Piece),
                Money.FromMinorUnits(5000),
                Money.FromMinorUnits(3000),
                Money.FromMinorUnits(5000),
                Money.Zero(),
                18.00m,
                Money.FromMinorUnits(4237),
                Money.FromMinorUnits(763),
                Money.FromMinorUnits(5000));

            var cgst = new SaleItemTaxComponent(10, saleItemId, "CGST", 9.00m, Money.FromMinorUnits(381));
            item.AddTaxComponent(cgst);
            invoice.AddItem(item);

            seedContext.SalesInvoices.Add(invoice);
            await seedContext.SaveChangesAsync();
        }

        // 2. Attempt to delete the SaleItem directly while tax component exists
        using (var deleteContext = new RetailDbContext(_options))
        {
            var itemToDelete = await deleteContext.SalesItems.FirstAsync(i => i.Id == saleItemId);
            deleteContext.SalesItems.Remove(itemToDelete);

            // ON DELETE RESTRICT must reject deletion of SaleItem
            await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SqliteRefundItemTaxData_PersistsAndRetrievesTaxRefundAmountCorrectly()
    {
        var saleId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var clientTxId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        long saleItemId = 3001;

        // 1. Seed invoice and refund with explicit TaxRefundAmount
        using (var seedContext = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000055",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(8475),
                Money.FromMinorUnits(1525),
                Money.Zero(),
                Money.FromMinorUnits(10000));

            var item = new SaleItem(
                saleItemId,
                saleId,
                productId,
                "REFUND-SKU",
                "Refund Tax Product",
                Quantity.From(1, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000),
                Money.FromMinorUnits(6000),
                Money.FromMinorUnits(10000),
                Money.Zero(),
                18.00m,
                Money.FromMinorUnits(8475),
                Money.FromMinorUnits(1525),
                Money.FromMinorUnits(10000));

            invoice.AddItem(item);
            seedContext.SalesInvoices.Add(invoice);
            await seedContext.SaveChangesAsync();

            var refund = new SaleRefund(
                refundId,
                saleId,
                "REF-20260914-000055",
                clientTxId,
                Guid.NewGuid(),
                "manager",
                Guid.NewGuid(),
                PaymentMethod.Cash,
                Money.FromMinorUnits(10000),
                "Customer return");

            var refundItem = new SaleRefundItem(
                id: 1,
                refundId: refundId,
                saleItemId: saleItemId,
                productId: productId,
                quantityRefunded: Quantity.From(1m, UnitOfMeasure.Piece),
                refundAmount: Money.FromMinorUnits(10000),
                taxRefundAmount: Money.FromMinorUnits(1525));

            refund.AddItem(refundItem);
            seedContext.SaleRefunds.Add(refund);
            await seedContext.SaveChangesAsync();
        }

        // 2. Query back and verify TaxRefundAmount is preserved and queryable
        using (var verifyContext = new RetailDbContext(_options))
        {
            var refund = await verifyContext.SaleRefunds
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.Id == refundId);

            Assert.NotNull(refund);
            Assert.Single(refund.Items);
            var rItem = refund.Items[0];

            Assert.Equal(1m, rItem.QuantityRefunded.Value);
            Assert.Equal(10000, rItem.RefundAmount.MinorUnits);
            Assert.Equal(1525, rItem.TaxRefundAmount.MinorUnits);
        }
    }

    [Fact]
    public async Task SqliteTaxRatePercentage_ExactDecimalRoundTrip_NoPrecisionLoss()
    {
        var saleId = Guid.NewGuid();
        long saleItemId = 4001;
        var productId = Guid.NewGuid();

        // Test diverse tax rates including decimals: 9.00%, 18.00%, 2.50%, 0.125%
        using (var seedContext = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000077",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(10000));

            var item = new SaleItem(
                saleItemId,
                saleId,
                productId,
                "PRECISION-SKU",
                "Precision Product",
                Quantity.From(1, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000),
                Money.FromMinorUnits(6000),
                Money.FromMinorUnits(10000),
                Money.Zero(),
                29.625m, // Exact sum of component rates
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.FromMinorUnits(10000));

            item.AddTaxComponent(new SaleItemTaxComponent(20, saleItemId, "CGST", 9.00m, Money.FromMinorUnits(900)));
            item.AddTaxComponent(new SaleItemTaxComponent(21, saleItemId, "SGST", 18.00m, Money.FromMinorUnits(1800)));
            item.AddTaxComponent(new SaleItemTaxComponent(22, saleItemId, "CESS", 2.50m, Money.FromMinorUnits(250)));
            item.AddTaxComponent(new SaleItemTaxComponent(23, saleItemId, "SPECIAL", 0.125m, Money.FromMinorUnits(12)));

            invoice.AddItem(item);
            seedContext.SalesInvoices.Add(invoice);
            await seedContext.SaveChangesAsync();
        }

        // Verify exact roundtrip from SQLite TEXT storage back to C# decimal
        using (var verifyContext = new RetailDbContext(_options))
        {
            var item = await verifyContext.SalesItems
                .Include(i => i.TaxComponents)
                .FirstAsync(i => i.Id == saleItemId);

            Assert.Equal(29.625m, item.TaxRatePercentage);

            var cgst = item.TaxComponents.First(c => c.ComponentName == "CGST");
            Assert.Equal(9.00m, cgst.RatePercentage);

            var sgst = item.TaxComponents.First(c => c.ComponentName == "SGST");
            Assert.Equal(18.00m, sgst.RatePercentage);

            var cess = item.TaxComponents.First(c => c.ComponentName == "CESS");
            Assert.Equal(2.50m, cess.RatePercentage);

            var special = item.TaxComponents.First(c => c.ComponentName == "SPECIAL");
            Assert.Equal(0.125m, special.RatePercentage);
        }
    }

    [Fact]
    public async Task SqliteInvoiceSequenceConcurrencyTests_TenConcurrentAllocations_GenerateUniqueSequentials()
    {
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = new List<Task>();
        var periodDate = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                lock (_dbLock)
                {
                    using var context = new RetailDbContext(_options);
                    var repo = new EfInvoiceSequenceRepository(context);
                    var num = repo.GetNextSequenceNumberAsync("SALE_INVOICE", "INV", periodDate).GetAwaiter().GetResult();
                    context.SaveChanges();
                    numbers.Add(num);
                }
                await Task.CompletedTask;
            }));
        }

        await Task.WhenAll(tasks);

        var list = numbers.ToList();
        Assert.Equal(10, list.Count);
        Assert.Equal(10, list.Distinct().Count());

        foreach (var num in list)
        {
            Assert.StartsWith($"INV-{periodDate}-", num);
        }

        // Check sequences table has exactly 1 row for that day and sequence name and value is 10
        using (var verifyContext = new RetailDbContext(_options))
        {
            var rows = await verifyContext.InvoiceSequences
                .Where(s => s.SequenceName == "SALE_INVOICE" && s.Prefix == "INV" && s.PeriodDate == periodDate)
                .ToListAsync();

            Assert.Single(rows);
            Assert.Equal(10, rows[0].CurrentValue);
        }
    }

    [Fact]
    public async Task SqliteRefundNumberConcurrencyTests_TenConcurrentAllocations_GenerateUniqueSequentials()
    {
        var numbers = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = new List<Task>();
        var periodDate = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                lock (_dbLock)
                {
                    using var context = new RetailDbContext(_options);
                    var repo = new EfInvoiceSequenceRepository(context);
                    var num = repo.GetNextSequenceNumberAsync("SALE_REFUND", "REF", periodDate).GetAwaiter().GetResult();
                    context.SaveChanges();
                    numbers.Add(num);
                }
                await Task.CompletedTask;
            }));
        }

        await Task.WhenAll(tasks);

        var list = numbers.ToList();
        Assert.Equal(10, list.Count);
        Assert.Equal(10, list.Distinct().Count());

        foreach (var num in list)
        {
            Assert.StartsWith($"REF-{periodDate}-", num);
        }

        using (var verifyContext = new RetailDbContext(_options))
        {
            var rows = await verifyContext.InvoiceSequences
                .Where(s => s.SequenceName == "SALE_REFUND" && s.Prefix == "REF" && s.PeriodDate == periodDate)
                .ToListAsync();

            Assert.Single(rows);
            Assert.Equal(10, rows[0].CurrentValue);
        }
    }

    [Fact]
    public async Task SqliteFailedSaleRollback_DoesNotConsumeInvoiceNumber()
    {
        var periodDate = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        // 1. Start a transaction, generate next invoice number, but rollback
        using (var rollbackContext = new RetailDbContext(_options))
        {
            using var tx = await rollbackContext.Database.BeginTransactionAsync();
            var repo = new EfInvoiceSequenceRepository(rollbackContext);
            var generatedNumber = await repo.GetNextSequenceNumberAsync("ROLLBACK_TEST", "INV", periodDate);
            Assert.EndsWith("000001", generatedNumber);
            await rollbackContext.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        // 2. Start a new transaction; the sequence must still start at 000001
        using (var commitContext = new RetailDbContext(_options))
        {
            using var tx = await commitContext.Database.BeginTransactionAsync();
            var repo = new EfInvoiceSequenceRepository(commitContext);
            var committedNumber = await repo.GetNextSequenceNumberAsync("ROLLBACK_TEST", "INV", periodDate);
            Assert.EndsWith("000001", committedNumber);
            await commitContext.SaveChangesAsync();
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task SqliteMixedPaymentSemanticsTests_PersistsIndividualRecordsAndRejectsMixed()
    {
        var saleId = Guid.NewGuid();

        using (var context = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000002",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(10000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(10000));

            var cashPayment = new PaymentRecord(
                Guid.NewGuid(),
                saleId,
                PaymentMethod.Cash,
                Money.FromMinorUnits(6000),
                tenderedAmount: Money.FromMinorUnits(10000)); // tendered 100, change 40

            var cardPayment = new PaymentRecord(
                Guid.NewGuid(),
                saleId,
                PaymentMethod.Card,
                Money.FromMinorUnits(4000),
                referenceNumber: "AUTH-CARD-789012");

            invoice.AddPayment(cashPayment);
            invoice.AddPayment(cardPayment);

            context.SalesInvoices.Add(invoice);
            await context.SaveChangesAsync();
        }

        using (var verifyContext = new RetailDbContext(_options))
        {
            var payments = await verifyContext.Payments
                .Where(p => p.SaleId == saleId)
                .ToListAsync();

            Assert.Equal(2, payments.Count);
            Assert.Contains(payments, p => p.Method == PaymentMethod.Cash && p.Amount.MinorUnits == 6000);
            Assert.Contains(payments, p => p.Method == PaymentMethod.Card && p.Amount.MinorUnits == 4000);
            Assert.DoesNotContain(payments, p => p.Method == PaymentMethod.Mixed);

            var totalPaid = payments.Sum(p => p.Amount.MinorUnits);
            Assert.Equal(10000, totalPaid);
        }
    }

    [Fact]
    public async Task SqliteSaleIdempotencyTests_DuplicateClientTransactionId_ThrowsDbUpdateException()
    {
        var clientTxId = Guid.NewGuid();

        using (var context = new RetailDbContext(_options))
        {
            var sale1 = new SaleInvoice(
                Guid.NewGuid(),
                "INV-20260914-000010",
                clientTxId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000));

            context.SalesInvoices.Add(sale1);
            await context.SaveChangesAsync();
        }

        using (var duplicateContext = new RetailDbContext(_options))
        {
            var sale2 = new SaleInvoice(
                Guid.NewGuid(),
                "INV-20260914-000011",
                clientTxId, // same clientTxId
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000));

            duplicateContext.SalesInvoices.Add(sale2);
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SqliteRefundIdempotencyTests_DuplicateClientTransactionId_ThrowsDbUpdateException()
    {
        var saleId = Guid.NewGuid();
        var clientTxId = Guid.NewGuid();

        using (var context = new RetailDbContext(_options))
        {
            var invoice = new SaleInvoice(
                saleId,
                "INV-20260914-000020",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxInclusive,
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000));

            context.SalesInvoices.Add(invoice);
            await context.SaveChangesAsync();

            var refund1 = new SaleRefund(
                Guid.NewGuid(),
                saleId,
                "REF-20260914-000001",
                clientTxId,
                Guid.NewGuid(),
                "manager",
                Guid.NewGuid(),
                PaymentMethod.Cash,
                Money.FromMinorUnits(5000),
                "Defective item");

            context.SaleRefunds.Add(refund1);
            await context.SaveChangesAsync();
        }

        using (var duplicateContext = new RetailDbContext(_options))
        {
            var refund2 = new SaleRefund(
                Guid.NewGuid(),
                saleId,
                "REF-20260914-000002",
                clientTxId, // same clientTxId
                Guid.NewGuid(),
                "manager",
                Guid.NewGuid(),
                PaymentMethod.Cash,
                Money.FromMinorUnits(5000),
                "Duplicate refund attempt");

            duplicateContext.SaleRefunds.Add(refund2);
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicateContext.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SqliteStockSaleConcurrencyTests_TwoConcurrentPurchases_ExactlyOneSucceeds()
    {
        var productId = Guid.NewGuid();

        // 1. Initial stock = 5 units
        using (var setupContext = new RetailDbContext(_options))
        {
            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 5m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        // 2. Two concurrent checkouts attempt to buy 5 units each
        int successCount = 0;
        int failureCount = 0;

        async Task AttemptCheckoutAsync()
        {
            try
            {
                using var attemptContext = new RetailDbContext(_options);
                SqliteConnectionConfigurator.ApplyPragmas(attemptContext);

                lock (_dbLock)
                {
                    var stock = attemptContext.StockBalances.First(s => s.ProductId == productId);
                    stock.ApplyMovement(StockMovementType.Sale, Quantity.From(5m, UnitOfMeasure.Piece), NegativeStockPolicy.Disallowed);
                    attemptContext.SaveChanges();
                }

                Interlocked.Increment(ref successCount);
            }
            catch (InsufficientStockException)
            {
                Interlocked.Increment(ref failureCount);
            }
            await Task.CompletedTask;
        }

        var task1 = Task.Run(AttemptCheckoutAsync);
        var task2 = Task.Run(AttemptCheckoutAsync);

        await Task.WhenAll(task1, task2);

        // 3. Exactly one succeeded, exactly one failed
        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // 4. Final stock balance must be exactly 0, never negative
        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalStock = await verifyContext.StockBalances.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(0m, finalStock.CurrentQuantity.Value);
            Assert.False(finalStock.CurrentQuantity.Value < 0m);
        }
    }
}
