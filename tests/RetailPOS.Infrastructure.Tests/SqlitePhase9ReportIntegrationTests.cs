using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Contracts.Reports;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SqlitePhase9ReportIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqlitePhase9ReportIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_phase9_test_{Guid.NewGuid():N}.db");
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

    private static EfReportRepository CreateRepo(RetailDbContext context) => new(context);

    [Fact]
    public async Task HistoricalCOGS_ProductMasterPriceChange_DoesNotAffectHistoricalReport()
    {
        var productId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        var today = DateTime.UtcNow;

        using (var context = new RetailDbContext(_options))
        {
            var product = new Product(
                productId,
                "SKU-COGS-01",
                "Historical Item",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(5000), // Original Cost: Rs 50
                Money.FromMinorUnits(10000), // Original Selling Price: Rs 100
                taxCategoryId: 1);

            var sale = new SaleInvoice(
                saleId,
                "INV-COGS-001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxExclusive,
                Money.FromMinorUnits(20000), // 2 units @ Rs 100 = Rs 200
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(20000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(20000));

            var saleItem = new SaleItem(
                101,
                saleId,
                productId,
                "SKU-COGS-01",
                "Historical Item",
                Quantity.From(2m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000),
                Money.FromMinorUnits(5000), // UnitCostPrice snapshot = Rs 50
                Money.FromMinorUnits(20000),
                Money.Zero(),
                0m,
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(20000));

            sale.AddItem(saleItem);
            sale.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Cash, Money.FromMinorUnits(20000)));

            context.Products.Add(product);
            context.SalesInvoices.Add(sale);
            await context.SaveChangesAsync();
        }

        // 1. Run profit report before mutating product master
        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var profitReport = await repo.GetProfitEstimateAsync(today.AddDays(-1), today.AddDays(1));

            Assert.Equal(20000, profitReport.NetSalesPaise);
            Assert.Equal(10000, profitReport.NetCostOfGoodsSoldPaise);
            Assert.Equal(10000, profitReport.EstimatedGrossProfitPaise);
            Assert.Equal(50.00m, profitReport.MarginPercentage);
        }

        // 2. Mutate current Product Master cost and selling price
        using (var context = new RetailDbContext(_options))
        {
            var p = await context.Products.FindAsync(productId);
            Assert.NotNull(p);
            p!.UpdatePrices(Money.FromMinorUnits(8000), Money.FromMinorUnits(15000));
            await context.SaveChangesAsync();
        }

        // 3. Run profit report again - historical figures MUST remain identical!
        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var profitReportAgain = await repo.GetProfitEstimateAsync(today.AddDays(-1), today.AddDays(1));

            Assert.Equal(20000, profitReportAgain.NetSalesPaise);
            Assert.Equal(10000, profitReportAgain.NetCostOfGoodsSoldPaise); // Still Rs 100, NOT Rs 160!
            Assert.Equal(10000, profitReportAgain.EstimatedGrossProfitPaise);
            Assert.Equal(50.00m, profitReportAgain.MarginPercentage);
        }
    }

    [Fact]
    public async Task DailySalesSummary_CompletedSalesWithDiscountsTaxesAndTenders_CalculatesCorrectly()
    {
        var saleId1 = Guid.NewGuid();
        var saleId2 = Guid.NewGuid();
        var today = DateTime.UtcNow;

        using (var context = new RetailDbContext(_options))
        {
            var sale1 = new SaleInvoice(
                saleId1,
                "INV-DAILY-001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxExclusive,
                Money.FromMinorUnits(10000),
                Money.FromMinorUnits(500),  // Line discount: Rs 5
                Money.FromMinorUnits(500),  // Invoice discount: Rs 5
                Money.FromMinorUnits(9000), // Taxable: Rs 90
                Money.FromMinorUnits(1620), // Tax 18%: Rs 16.20
                Money.FromMinorUnits(-20),  // Rounding: -0.20
                Money.FromMinorUnits(10600)); // Final payable: Rs 106.00

            var item1 = new SaleItem(
                201, saleId1, Guid.NewGuid(), "SKU-D1", "Item D1",
                Quantity.From(1m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000), Money.FromMinorUnits(6000),
                Money.FromMinorUnits(10000), Money.FromMinorUnits(500),
                18m, Money.FromMinorUnits(9000), Money.FromMinorUnits(1620), Money.FromMinorUnits(10620));

            item1.AddTaxComponent(new SaleItemTaxComponent(1, 201, "CGST", 9m, Money.FromMinorUnits(810)));
            item1.AddTaxComponent(new SaleItemTaxComponent(2, 201, "SGST", 9m, Money.FromMinorUnits(810)));
            sale1.AddItem(item1);

            // Paid Rs 50 Cash, Rs 56 Card
            sale1.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId1, PaymentMethod.Cash, Money.FromMinorUnits(5000)));
            sale1.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId1, PaymentMethod.Card, Money.FromMinorUnits(5600), referenceNumber: "CARD-1234"));

            var sale2 = new SaleInvoice(
                saleId2,
                "INV-DAILY-002",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxExclusive,
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(5000));

            var item2 = new SaleItem(
                202, saleId2, Guid.NewGuid(), "SKU-D2", "Item D2",
                Quantity.From(1m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(5000), Money.FromMinorUnits(3000),
                Money.FromMinorUnits(5000), Money.Zero(),
                0m, Money.FromMinorUnits(5000), Money.Zero(), Money.FromMinorUnits(5000));
            sale2.AddItem(item2);
            sale2.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId2, PaymentMethod.UPI, Money.FromMinorUnits(5000), referenceNumber: "UPI123456789"));

            context.SalesInvoices.AddRange(sale1, sale2);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var summary = await repo.GetDailySalesSummaryAsync(today.AddDays(-1), today.AddDays(1), null);

            Assert.Equal(2, summary.TotalInvoicesCount);
            Assert.Equal(15000, summary.GrossSubtotalPaise);
            Assert.Equal(500, summary.TotalLineDiscountsPaise);
            Assert.Equal(500, summary.TotalInvoiceDiscountsPaise);
            Assert.Equal(14000, summary.TaxableSubtotalPaise);
            Assert.Equal(1620, summary.TotalTaxPaise);
            Assert.Equal(-20, summary.RoundingAdjustmentPaise);
            Assert.Equal(15600, summary.NetRevenuePaise);

            // Tenders
            Assert.Equal(5000, summary.PaymentTenders.CashPaise);
            Assert.Equal(5600, summary.PaymentTenders.CardPaise);
            Assert.Equal(5000, summary.PaymentTenders.UpiPaise);
            Assert.Equal(15600, summary.PaymentTenders.TotalPaise);

            // Tax Summary verification
            var taxSummary = await repo.GetTaxSummaryAsync(today.AddDays(-1), today.AddDays(1));
            var cgst = taxSummary.Components.FirstOrDefault(c => c.ComponentName == "CGST");
            var sgst = taxSummary.Components.FirstOrDefault(c => c.ComponentName == "SGST");
            Assert.NotNull(cgst);
            Assert.Equal(810, cgst!.TaxCollectedPaise);
            Assert.NotNull(sgst);
            Assert.Equal(810, sgst!.TaxCollectedPaise);
        }
    }

    [Fact]
    public async Task VoidedInvoices_ExcludedFromSalesTotals_ReportedInVoidMetrics()
    {
        var saleId = Guid.NewGuid();
        var today = DateTime.UtcNow;

        using (var context = new RetailDbContext(_options))
        {
            var sale = new SaleInvoice(
                saleId,
                "INV-VOID-001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxExclusive,
                Money.FromMinorUnits(25000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(25000),
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(25000));

            var item = new SaleItem(
                301, saleId, Guid.NewGuid(), "SKU-V1", "Void Item",
                Quantity.From(1m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(25000), Money.FromMinorUnits(15000),
                Money.FromMinorUnits(25000), Money.Zero(),
                0m, Money.FromMinorUnits(25000), Money.Zero(), Money.FromMinorUnits(25000));
            sale.AddItem(item);
            sale.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Cash, Money.FromMinorUnits(25000)));

            // Set to Voided
            typeof(SaleInvoice).GetProperty("Status")!.SetValue(sale, SaleStatus.Voided);

            context.SalesInvoices.Add(sale);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var summary = await repo.GetDailySalesSummaryAsync(today.AddDays(-1), today.AddDays(1), null);

            Assert.Equal(0, summary.TotalInvoicesCount);
            Assert.Equal(0, summary.GrossSubtotalPaise);
            Assert.Equal(0, summary.NetRevenuePaise);
            Assert.Equal(0, summary.PaymentTenders.TotalPaise);

            // Void metrics
            Assert.Equal(1, summary.VoidedInvoicesCount);
            Assert.Equal(25000, summary.VoidedAmountPaise);
        }
    }

    [Fact]
    public async Task RefundAccounting_PartialRefundReconciles_COGSAndTaxDeductedWithoutDoubleCount()
    {
        var saleId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var today = DateTime.UtcNow;

        using (var context = new RetailDbContext(_options))
        {
            var sale = new SaleInvoice(
                saleId,
                "INV-REF-001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                PricingModel.TaxExclusive,
                Money.FromMinorUnits(30000), // 3 @ Rs 100
                Money.Zero(),
                Money.Zero(),
                Money.FromMinorUnits(30000),
                Money.FromMinorUnits(5400),  // 18% tax
                Money.Zero(),
                Money.FromMinorUnits(35400));

            var item = new SaleItem(
                401, saleId, productId, "SKU-REF-1", "Refundable Item",
                Quantity.From(3m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000), Money.FromMinorUnits(5000), // Cost: Rs 50
                Money.FromMinorUnits(30000), Money.Zero(),
                18m, Money.FromMinorUnits(30000), Money.FromMinorUnits(5400), Money.FromMinorUnits(35400));
            item.AddTaxComponent(new SaleItemTaxComponent(10, 401, "CGST", 9m, Money.FromMinorUnits(2700)));
            item.AddTaxComponent(new SaleItemTaxComponent(11, 401, "SGST", 9m, Money.FromMinorUnits(2700)));
            sale.AddItem(item);
            sale.AddPayment(new PaymentRecord(Guid.NewGuid(), saleId, PaymentMethod.Cash, Money.FromMinorUnits(35400)));
            sale.MarkPartiallyRefunded();

            // Create Refund for 1 unit: Rs 100 refund, Rs 18 tax refund -> Total Rs 118
            var refund = new SaleRefund(
                refundId,
                saleId,
                "REF-001",
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Cashier 1",
                Guid.NewGuid(),
                PaymentMethod.Cash,
                Money.FromMinorUnits(11800),
                "Damaged item");

            var refundItem = new SaleRefundItem(
                501, refundId, 401, productId,
                Quantity.From(1m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000),
                Money.FromMinorUnits(1800));
            refund.AddItem(refundItem);

            context.SalesInvoices.Add(sale);
            context.SaleRefunds.Add(refund);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);

            // Daily Sales Summary
            var summary = await repo.GetDailySalesSummaryAsync(today.AddDays(-1), today.AddDays(1), null);
            Assert.Equal(1, summary.TotalInvoicesCount);
            Assert.Equal(30000, summary.GrossSubtotalPaise);
            Assert.Equal(1, summary.TotalRefundsCount);
            Assert.Equal(11800, summary.TotalRefundsPaise);
            Assert.Equal(35400 - 11800, summary.NetRevenuePaise);

            // Payment Summary Breakdown
            var paymentSummary = await repo.GetPaymentSummaryAsync(today.AddDays(-1), today.AddDays(1));
            Assert.Equal(11800, paymentSummary.RefundTenders.TotalPaise);
            Assert.Equal(23600, paymentSummary.NetTenders.TotalPaise);

            // Profit Estimate Report
            var profit = await repo.GetProfitEstimateAsync(today.AddDays(-1), today.AddDays(1));
            // Gross sales = 35400, Refunds = 11800, NetSales = 23600
            // Gross COGS = 3 * 5000 = 15000. Refunded COGS = 1 * 5000 = 5000. Net COGS = 10000
            // Estimated Gross Profit = 23600 - 10000 = 13600 paise
            // Margin = 13600 / 23600 * 100 = 57.63%
            Assert.Equal(10000, profit.NetCostOfGoodsSoldPaise);
            Assert.Equal(23600, profit.NetSalesPaise);
            Assert.Equal(13600, profit.EstimatedGrossProfitPaise);
            Assert.Equal(57.63m, profit.MarginPercentage);
        }
    }

    [Fact]
    public async Task StockMovementLedger_OpeningBalanceAndAllMovementsReconcileExact()
    {
        var productId = Guid.NewGuid();
        var baselineTime = DateTime.UtcNow.AddDays(-5);
        var periodStart = DateTime.UtcNow.AddDays(-2);
        var periodEnd = DateTime.UtcNow.AddDays(1);

        using (var context = new RetailDbContext(_options))
        {
            var product = new Product(productId, "SKU-LEDGER-1", "Ledger Item", UnitOfMeasure.Piece, Money.FromMinorUnits(1000), Money.FromMinorUnits(2000), 1);
            context.Products.Add(product);

            // Movement 1: Baseline Purchase before period (100 units)
            var m1 = new StockMovement(1, productId, StockMovementType.Purchase, Quantity.From(100m, UnitOfMeasure.Piece), Quantity.From(100m, UnitOfMeasure.Piece), "PO-01", "Initial stock", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m1, baselineTime);

            // Movement 2: Sale during period (-5 units) -> Resulting 95
            var m2 = new StockMovement(2, productId, StockMovementType.Sale, Quantity.From(-5m, UnitOfMeasure.Piece), Quantity.From(95m, UnitOfMeasure.Piece), "INV-01", "Sale", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m2, periodStart.AddHours(2));

            // Movement 3: SaleReturn (+2 units) -> Resulting 97
            var m3 = new StockMovement(3, productId, StockMovementType.SaleReturn, Quantity.From(2m, UnitOfMeasure.Piece), Quantity.From(97m, UnitOfMeasure.Piece), "REF-01", "Customer return", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m3, periodStart.AddHours(4));

            // Movement 4: Wastage (-1 unit) -> Resulting 96
            var m4 = new StockMovement(4, productId, StockMovementType.Wastage, Quantity.From(-1m, UnitOfMeasure.Piece), Quantity.From(96m, UnitOfMeasure.Piece), "WST-01", "Damaged box", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m4, periodStart.AddHours(6));

            // Movement 5: AdjustmentIncrease (+3 units) -> Resulting 99
            var m5 = new StockMovement(5, productId, StockMovementType.AdjustmentIncrease, Quantity.From(3m, UnitOfMeasure.Piece), Quantity.From(99m, UnitOfMeasure.Piece), "ADJ-01", "Found stock", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m5, periodStart.AddHours(8));

            // Movement 6: AdjustmentDecrease (-2 units) -> Resulting 97
            var m6 = new StockMovement(6, productId, StockMovementType.AdjustmentDecrease, Quantity.From(-2m, UnitOfMeasure.Piece), Quantity.From(97m, UnitOfMeasure.Piece), "ADJ-02", "Count correction", null, null);
            typeof(StockMovement).GetProperty("CreatedAtUtc")!.SetValue(m6, periodStart.AddHours(10));

            context.StockMovements.AddRange(m1, m2, m3, m4, m5, m6);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var ledger = await repo.GetStockMovementLedgerAsync(productId, periodStart, periodEnd, 1, 50);

            Assert.NotNull(ledger);
            Assert.Equal("SKU-LEDGER-1", ledger!.Sku);
            Assert.Equal(100m, ledger.OpeningQuantity);
            Assert.Equal(5, ledger.Movements.Items.Count);
            Assert.Equal(97m, ledger.ClosingQuantity);

            // Reconcile: Opening (100) - 5 + 2 - 1 + 3 - 2 = 97
            var netChange = ledger.Movements.Items.Sum(m => m.QuantityChanged);
            Assert.Equal(ledger.ClosingQuantity, ledger.OpeningQuantity + netChange);
        }
    }

    [Fact]
    public async Task TaxSummary_Gstr1Aligned_B2B_B2C_And_CreditNotes()
    {
        var b2bCustomerId = Guid.NewGuid();
        var b2cCustomerId = Guid.NewGuid();
        var saleB2bId = Guid.NewGuid();
        var saleB2cId = Guid.NewGuid();
        var refundId = Guid.NewGuid();
        var today = DateTime.UtcNow;

        using (var context = new RetailDbContext(_options))
        {
            var b2bCustomer = new Customer(b2bCustomerId, "Enterprise Corp", phone: "9876543210", gstin: "29ABCDE1234F1Z5");
            var b2cCustomer = new Customer(b2cCustomerId, "Retail Buyer", phone: "9876543211", gstin: null);

            var saleB2b = new SaleInvoice(
                saleB2bId, "INV-B2B-01", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                PricingModel.TaxExclusive, Money.FromMinorUnits(50000), Money.Zero(), Money.Zero(),
                Money.FromMinorUnits(50000), Money.FromMinorUnits(9000), Money.Zero(), Money.FromMinorUnits(59000), b2bCustomerId);

            var itemB2b = new SaleItem(
                601, saleB2bId, Guid.NewGuid(), "SKU-B2B", "Enterprise Server",
                Quantity.From(1m, UnitOfMeasure.Piece), Money.FromMinorUnits(50000), Money.FromMinorUnits(30000),
                Money.FromMinorUnits(50000), Money.Zero(), 18m, Money.FromMinorUnits(50000), Money.FromMinorUnits(9000), Money.FromMinorUnits(59000));
            itemB2b.AddTaxComponent(new SaleItemTaxComponent(61, 601, "IGST", 18m, Money.FromMinorUnits(9000)));
            saleB2b.AddItem(itemB2b);
            saleB2b.AddPayment(new PaymentRecord(Guid.NewGuid(), saleB2bId, PaymentMethod.BankTransfer, Money.FromMinorUnits(59000)));

            var saleB2c = new SaleInvoice(
                saleB2cId, "INV-B2C-01", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                PricingModel.TaxExclusive, Money.FromMinorUnits(20000), Money.Zero(), Money.Zero(),
                Money.FromMinorUnits(20000), Money.FromMinorUnits(3600), Money.Zero(), Money.FromMinorUnits(23600), b2cCustomerId);

            var itemB2c = new SaleItem(
                602, saleB2cId, Guid.NewGuid(), "SKU-B2C", "Consumer Item",
                Quantity.From(2m, UnitOfMeasure.Piece), Money.FromMinorUnits(10000), Money.FromMinorUnits(5000),
                Money.FromMinorUnits(20000), Money.Zero(), 18m, Money.FromMinorUnits(20000), Money.FromMinorUnits(3600), Money.FromMinorUnits(23600));
            itemB2c.AddTaxComponent(new SaleItemTaxComponent(62, 602, "CGST", 9m, Money.FromMinorUnits(1800)));
            itemB2c.AddTaxComponent(new SaleItemTaxComponent(63, 602, "SGST", 9m, Money.FromMinorUnits(1800)));
            saleB2c.AddItem(itemB2c);
            saleB2c.AddPayment(new PaymentRecord(Guid.NewGuid(), saleB2cId, PaymentMethod.Cash, Money.FromMinorUnits(23600)));

            // Credit Note / Refund on B2C item (1 unit refunded)
            var refund = new SaleRefund(
                refundId, saleB2cId, "CRN-001", Guid.NewGuid(), Guid.NewGuid(), "Manager", Guid.NewGuid(),
                PaymentMethod.Cash, Money.FromMinorUnits(11800), "Returned 1 unit");
            var refundItem = new SaleRefundItem(
                701, refundId, 602, itemB2c.ProductId, Quantity.From(1m, UnitOfMeasure.Piece),
                Money.FromMinorUnits(10000), Money.FromMinorUnits(1800));
            refund.AddItem(refundItem);

            context.Customers.AddRange(b2bCustomer, b2cCustomer);
            context.SalesInvoices.AddRange(saleB2b, saleB2c);
            context.SaleRefunds.Add(refund);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);
            var taxSummary = await repo.GetTaxSummaryAsync(today.AddDays(-1), today.AddDays(1));

            // B2B & B2C
            Assert.Equal(50000, taxSummary.B2BTaxableSubtotalPaise);
            Assert.Equal(20000, taxSummary.B2CTaxableSubtotalPaise);
            Assert.Equal(70000, taxSummary.TotalTaxableSubtotalPaise);
            Assert.Equal(12600, taxSummary.TotalTaxCollectedPaise); // 9000 + 3600
            Assert.Equal(1800, taxSummary.TotalTaxRefundedPaise);

            // Net liability = TotalTaxCollected (12600) - TotalTaxRefunded (1800) = 10800
            Assert.Equal(10800, taxSummary.NetTaxPayablePaise);
        }
    }

    [Fact]
    public async Task RealSqliteWal_ConcurrentWriteTransaction_ReportQueriesSucceedWithoutBusy()
    {
        var today = DateTime.UtcNow;

        // Seed some data first
        using (var context = new RetailDbContext(_options))
        {
            var prod = new Product(Guid.NewGuid(), "SKU-CONC-01", "Concurrent Item", UnitOfMeasure.Piece, Money.FromMinorUnits(2000), Money.FromMinorUnits(4000), 1);
            context.Products.Add(prod);
            await context.SaveChangesAsync();
        }

        var writeStarted = new TaskCompletionSource<bool>();
        var readFinished = new TaskCompletionSource<bool>();

        // Background write transaction that acquires write lock on the real SQLite database
        var writeTask = Task.Run(async () =>
        {
            using var writeContext = new RetailDbContext(_options);
            await using var tx = await writeContext.Database.BeginTransactionAsync();

            var newProd = new Product(Guid.NewGuid(), "SKU-CONC-02", "Concurrent Item 2", UnitOfMeasure.Piece, Money.FromMinorUnits(3000), Money.FromMinorUnits(6000), 1);
            writeContext.Products.Add(newProd);
            await writeContext.SaveChangesAsync();

            // Signal reader that write tx is active
            writeStarted.SetResult(true);

            // Wait for reader to complete reading
            await readFinished.Task;

            await tx.CommitAsync();
        });

        await writeStarted.Task;

        // Concurrently run report queries on a separate context
        using (var readContext = new RetailDbContext(_options))
        {
            var repo = CreateRepo(readContext);

            // These queries use AsNoTracking() and SQLite WAL multi-reader concurrency
            var dailySummary = await repo.GetDailySalesSummaryAsync(today.AddDays(-1), today.AddDays(1), null);
            Assert.NotNull(dailySummary);

            var profit = await repo.GetProfitEstimateAsync(today.AddDays(-1), today.AddDays(1));
            Assert.NotNull(profit);

            var valuation = await repo.GetStockValuationAsync(null, lowStockOnly: false, null, 1, 50);
            Assert.NotNull(valuation);
        }

        readFinished.SetResult(true);
        await writeTask;
    }

    [Fact]
    public async Task LowStockAndValuationReports_ProduceAccurateResults()
    {
        using (var context = new RetailDbContext(_options))
        {
            var p1 = new Product(Guid.NewGuid(), "SKU-LOW-1", "Low Stock Item", UnitOfMeasure.Piece, Money.FromMinorUnits(1000), Money.FromMinorUnits(2000), 1);
            var p2 = new Product(Guid.NewGuid(), "SKU-HIGH-1", "Normal Item", UnitOfMeasure.Piece, Money.FromMinorUnits(2000), Money.FromMinorUnits(4000), 1);

            var b1 = new StockBalance(p1.Id, UnitOfMeasure.Piece, 2m, 5m); // 2 on hand, reorder at 5 -> LOW STOCK
            var b2 = new StockBalance(p2.Id, UnitOfMeasure.Piece, 50m, 10m); // 50 on hand, reorder at 10 -> NORMAL

            context.Products.AddRange(p1, p2);
            context.StockBalances.AddRange(b1, b2);
            await context.SaveChangesAsync();
        }

        using (var context = new RetailDbContext(_options))
        {
            var repo = CreateRepo(context);

            var lowStock = await repo.GetStockValuationAsync(null, lowStockOnly: true, null, 1, 50);
            Assert.Equal(1, lowStock.TotalProductsCount);
            Assert.Equal("SKU-LOW-1", lowStock.Items.Items[0].Sku);

            var valuation = await repo.GetStockValuationAsync(null, lowStockOnly: false, null, 1, 50);
            Assert.Equal(2, valuation.TotalProductsCount);
            Assert.Equal(2 * 1000 + 50 * 2000, valuation.TotalValuationAtCostPaise);
            Assert.Equal(2 * 2000 + 50 * 4000, valuation.TotalValuationAtRetailPaise);
        }
    }
}
