using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Customers;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Entities.Users;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.Services;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SqlitePhase8CustomerIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqlitePhase8CustomerIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_phase8_test_{Guid.NewGuid():N}.db");
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

    private static (CustomerService CustomerService, CashSessionService CashSessionService, SalesService SalesService, RetailDbContext Context) CreateServices(RetailDbContext context)
    {
        var uow = new EfUnitOfWork(context);
        var customerRepo = new EfCustomerRepository(context);
        var cashSessionRepo = new EfCashSessionRepository(context);
        var storeRepo = new EfStoreRepository(context);
        var auditRepo = new EfAuditLogRepository(context);
        var saleRepo = new EfSaleRepository(context);
        var refundRepo = new EfSaleRefundRepository(context);
        var productRepo = new EfProductRepository(context);
        var inventoryRepo = new EfInventoryRepository(context);
        var categoryRepo = new EfCategoryRepository(context);
        var sequenceRepo = new EfInvoiceSequenceRepository(context);

        var customerService = new CustomerService(customerRepo, cashSessionRepo, storeRepo, auditRepo, uow);
        var cashSessionService = new CashSessionService(cashSessionRepo, storeRepo, auditRepo, uow);
        var salesService = new SalesService(saleRepo, refundRepo, productRepo, inventoryRepo, categoryRepo, storeRepo, sequenceRepo, auditRepo, uow, new PricingCalculator(), customerRepo, cashSessionRepo);

        return (customerService, cashSessionService, salesService, context);
    }

    private static async Task<(Store Store, Terminal Terminal, User Cashier, User Manager, Product Product)> SeedStandardTestDataAsync(RetailDbContext context)
    {
        var store = new Store(1, "Supermarket 1", "29AAAAA0000A1Z5", "123 Main Road", "9876543210");
        var terminal = new Terminal(Guid.NewGuid(), "POS-01", "Main Terminal");
        var cashier = new User(Guid.NewGuid(), "cashier1", "Cashier One", "1234", "hash", "salt", RoleType.Cashier);
        var manager = new User(Guid.NewGuid(), "manager1", "Manager One", "5678", "hash", "salt", RoleType.Manager);

        var product = new Product(
            Guid.NewGuid(),
            "ITEM-P8-01",
            "Atta 10kg",
            UnitOfMeasure.Piece,
            Money.FromMinorUnits(35000, Currency.INR),
            Money.FromMinorUnits(42000, Currency.INR),
            taxCategoryId: 1); // 0% tax

        var balance = new StockBalance(product.Id, UnitOfMeasure.Piece, 100m, 5m);

        context.Stores.Add(store);
        context.Terminals.Add(terminal);
        context.Users.AddRange(cashier, manager);
        context.Products.Add(product);
        context.StockBalances.Add(balance);
        await context.SaveChangesAsync();

        return (store, terminal, cashier, manager, product);
    }

    [Fact]
    public async Task CustomerCrudAndSoftDelete_WorksAndProtectsHistory()
    {
        using var context = new RetailDbContext(_options);
        var (customerService, _, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, manager, product) = await SeedStandardTestDataAsync(context);

        // 1. Create customer
        var createReq = new CreateCustomerRequest(
            "Anand Sharma",
            "+91 98765 43210",
            "anand@example.com",
            "Flat 402, MG Road",
            "29ABCDE1234F1Z5",
            CreditLimitPaise: 50000,
            Notes: "Regular customer");

        var createRes = await customerService.CreateCustomerAsync(createReq, manager.Id, manager.Username);
        Assert.True(createRes.Success);
        var customerId = createRes.Data!.Id;
        Assert.Equal("9876543210", createRes.Data.Phone);
        Assert.Equal(50000, createRes.Data.CreditLimitPaise);

        // 2. Search by phone
        var byPhoneRes = await customerService.GetByPhoneAsync("9876543210");
        Assert.True(byPhoneRes.Success);
        Assert.Equal("Anand Sharma", byPhoneRes.Data!.Name);

        // 3. Update profile
        var updateReq = new UpdateCustomerRequest(
            "Anand K Sharma",
            "9876543210",
            "anand.k@example.com",
            "Flat 502, MG Road",
            "29ABCDE1234F1Z5",
            "Updated notes");
        var updateRes = await customerService.UpdateCustomerAsync(customerId, updateReq, manager.Id, manager.Username);
        Assert.True(updateRes.Success);
        Assert.Equal("Anand K Sharma", updateRes.Data!.Name);

        // 4. Update credit limit
        var limitRes = await customerService.UpdateCreditLimitAsync(customerId, new UpdateCreditLimitRequest(100000, "Loyal customer limit increase"), manager.Id, manager.Username);
        Assert.True(limitRes.Success);
        Assert.Equal(100000, limitRes.Data!.CreditLimitPaise);

        // 5. Deactivate customer
        var statusRes = await customerService.SetStatusAsync(customerId, new SetCustomerStatusRequest(false, "Customer requested deactivation"), manager.Id, manager.Username);
        Assert.True(statusRes.Success);
        Assert.False(statusRes.Data!.IsActive);

        // 6. Attempt credit sale on inactive customer should fail
        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 1m, DiscountType.None, 0m) },
            new List<PaymentLineRequest> { new(PaymentMethod.Credit, 42000) },
            DiscountType.None,
            0m,
            customerId);

        var saleEx = await Assert.ThrowsAsync<DomainException>(() => salesService.CreateSaleAsync(saleReq, cashier.Id, cashier.Username, terminal.Id));
        Assert.Equal("CUSTOMER_INACTIVE", saleEx.ErrorCode);
    }

    [Fact]
    public async Task DeterministicCreditLimitConcurrency_TenSimultaneousSales_ExactlyTwoSucceed()
    {
        // Specification:
        // Customer CreditLimit = 10,000 paise (₹100)
        // Initial OutstandingCredit = 8,000 paise (₹80)
        // Remaining available limit = 2,000 paise (₹20)
        // 10 simultaneous checkout requests for 1,000 paise (₹10) each
        // Exactly 2 succeed, exactly 8 fail with CREDIT_LIMIT_EXCEEDED
        // Final OutstandingCredit = 10,000 paise
        // Stock deduction = exactly 2 units

        Guid customerId = Guid.NewGuid();
        Guid terminalId;
        Guid cashierId;
        Guid productId;

        using (var setupContext = new RetailDbContext(_options))
        {
            var (_, terminal, cashier, _, product) = await SeedStandardTestDataAsync(setupContext);
            terminalId = terminal.Id;
            cashierId = cashier.Id;
            productId = product.Id;

            // Product price ₹10 (1000 paise)
            product.UpdatePrices(Money.FromMinorUnits(500, Currency.INR), Money.FromMinorUnits(1000, Currency.INR));

            // Customer with 10,000 limit and 8,000 existing debt
            var customer = new Customer(customerId, "Concurrency Tester", "9876543210", creditLimit: 100m); // 10,000 paise
            customer.AddCredit(Money.FromMinorUnits(8000, Currency.INR));
            setupContext.Customers.Add(customer);
            await setupContext.SaveChangesAsync();
        }

        // Launch 10 simultaneous checkout requests
        int totalRequests = 10;
        var tasks = Enumerable.Range(0, totalRequests).Select(async i =>
        {
            using var taskContext = new RetailDbContext(_options);
            var (_, _, salesService, _) = CreateServices(taskContext);

            var req = new CreateSaleRequest(
                Guid.NewGuid(),
                new List<CartItemRequest> { new(productId, 1m, DiscountType.None, 0m) },
                new List<PaymentLineRequest> { new(PaymentMethod.Credit, 1000) },
                DiscountType.None,
                0m,
                customerId);

            try
            {
                var res = await salesService.CreateSaleAsync(req, cashierId, "Cashier", terminalId);
                return res.Success ? "SUCCESS" : res.ErrorCode ?? "FAIL";
            }
            catch (DomainException dex)
            {
                return dex.ErrorCode;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name;
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        int successes = results.Count(r => r == "SUCCESS");
        int creditLimitFails = results.Count(r => r == "CREDIT_LIMIT_EXCEEDED");

        Assert.Equal(2, successes);
        Assert.Equal(8, creditLimitFails);

        // Verify database state
        using (var verifyContext = new RetailDbContext(_options))
        {
            var customer = await verifyContext.Customers.FindAsync(customerId);
            Assert.NotNull(customer);
            Assert.Equal(10000, customer.OutstandingCredit.MinorUnits); // Exactly 10,000 paise

            var stock = await verifyContext.StockBalances.FirstAsync(b => b.ProductId == productId);
            Assert.Equal(98m, stock.CurrentQuantity.Value); // Deducted exactly 2 units (100 - 2)
        }
    }

    [Fact]
    public async Task StoreCreditPriorityOverCreditLimit_SucceedsEvenWhenCreditLimitExhausted()
    {
        // Customer has CreditLimit = 5,000 paise, OutstandingCredit = 5,000 paise (0 available credit limit!)
        // Customer has CreditBalance = 2,000 paise (store credit)
        // Checkout ₹20 (2,000 paise) using Credit payment method (auto-consumes store credit first)
        // Succeeds because store credit does NOT consume credit limit

        using var context = new RetailDbContext(_options);
        var (_, _, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, _, product) = await SeedStandardTestDataAsync(context);

        product.UpdatePrices(Money.FromMinorUnits(1000, Currency.INR), Money.FromMinorUnits(2000, Currency.INR));
        await context.SaveChangesAsync();

        var customer = new Customer(Guid.NewGuid(), "Store Credit Holder", "9876543210", creditLimit: 50m);
        customer.AddCredit(Money.FromMinorUnits(5000, Currency.INR)); // Debt = 5000
        customer.IncreaseCreditBalance(Money.FromMinorUnits(2000, Currency.INR)); // Store credit = 2000
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 1m, DiscountType.None, 0m) },
            new List<PaymentLineRequest> { new(PaymentMethod.Credit, 2000) },
            DiscountType.None,
            0m,
            customer.Id);

        var saleRes = await salesService.CreateSaleAsync(saleReq, cashier.Id, cashier.Username, terminal.Id);
        Assert.True(saleRes.Success);

        // Verify customer balances
        using var verifyContext = new RetailDbContext(_options);
        var updatedCustomer = await verifyContext.Customers.FindAsync(customer.Id);
        Assert.NotNull(updatedCustomer);
        Assert.Equal(5000, updatedCustomer.OutstandingCredit.MinorUnits); // Debt untouched
        Assert.Equal(0, updatedCustomer.CreditBalance.MinorUnits);         // Store credit consumed
    }

    [Fact]
    public async Task MixedPayment_CashSessionTracking_OnlyCashComponentAffectsDrawer()
    {
        // ₹1,000 bill: Cash ₹400, UPI ₹300, Credit ₹300
        // Active CashSession on terminal: TotalCashSales increases by exactly ₹400

        using var context = new RetailDbContext(_options);
        var (_, cashSessionService, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, manager, product) = await SeedStandardTestDataAsync(context);

        // Open session with ₹1,000 float
        var sessionRes = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 100000, "Morning Shift"), cashier.Id, cashier.Username);
        Assert.True(sessionRes.Success);
        var sessionId = sessionRes.Data!.Id;

        // Customer with sufficient limit
        var customer = new Customer(Guid.NewGuid(), "Mixed Buyer", "9876543210", creditLimit: 1000m);
        context.Customers.Add(customer);

        // Product price ₹1,000 (100,000 paise)
        product.UpdatePrices(Money.FromMinorUnits(50000, Currency.INR), Money.FromMinorUnits(100000, Currency.INR));
        await context.SaveChangesAsync();

        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 1m, DiscountType.None, 0m) },
            new List<PaymentLineRequest>
            {
                new(PaymentMethod.Cash, 40000, 40000),
                new(PaymentMethod.UPI, 30000, ReferenceNumber: "UPI-REF-123"),
                new(PaymentMethod.Credit, 30000)
            },
            DiscountType.None,
            0m,
            customer.Id);

        var saleRes = await salesService.CreateSaleAsync(saleReq, cashier.Id, cashier.Username, terminal.Id);
        Assert.True(saleRes.Success);

        // Verify cash session
        using var verifyContext = new RetailDbContext(_options);
        var session = await verifyContext.CashSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(40000, session.TotalCashSales.MinorUnits); // Only Cash component recorded!
        Assert.Equal(140000, session.ExpectedCash.MinorUnits);  // Float (100,000) + CashSales (40,000)
    }

    [Fact]
    public async Task Phase6RefundIntegration_CashRefundUpdatesActiveCashSession()
    {
        // Sale of ₹500 cash on Terminal A
        // Active CashSession on Terminal A
        // Refund ₹200 cash -> TotalCashRefunds increases by 20,000 paise

        using var context = new RetailDbContext(_options);
        var (_, cashSessionService, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, manager, product) = await SeedStandardTestDataAsync(context);

        // Open session
        var sessionRes = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 50000, "Shift 1"), cashier.Id, cashier.Username);
        Assert.True(sessionRes.Success);
        var sessionId = sessionRes.Data!.Id;

        // Sale ₹500 cash: 5 pieces @ ₹100 each = ₹500 (50,000 paise)
        product.UpdatePrices(Money.FromMinorUnits(5000, Currency.INR), Money.FromMinorUnits(10000, Currency.INR));
        await context.SaveChangesAsync();

        var saleRes = await salesService.CreateSaleAsync(new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 5m, DiscountType.None, 0m) },
            new List<PaymentLineRequest> { new(PaymentMethod.Cash, 50000, 50000) }),
            cashier.Id,
            cashier.Username,
            terminal.Id);
        Assert.True(saleRes.Success);
        var saleId = saleRes.Data!.Id;
        var saleItemId = saleRes.Data.Items[0].Id;

        // Refund ₹200 cash: 2 pieces @ ₹100 = ₹200 (20,000 paise)
        var refundReq = new RefundSaleRequest(
            Guid.NewGuid(),
            new List<RefundItemRequest> { new(saleItemId, 2m) },
            PaymentMethod.Cash,
            "Damaged packaging");

        var refundRes = await salesService.RefundSaleAsync(saleId, refundReq, manager.Id, manager.Username, terminal.Id);
        Assert.True(refundRes.Success);

        // Verify session updated
        using var verifyContext = new RetailDbContext(_options);
        var session = await verifyContext.CashSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(50000, session.TotalCashSales.MinorUnits);
        Assert.Equal(20000, session.TotalCashRefunds.MinorUnits);
        // Expected cash: Float (50,000) + Sales (50,000) - Refund (20,000) = 80,000 paise
        Assert.Equal(80000, session.ExpectedCash.MinorUnits);
    }

    [Fact]
    public async Task CashSessionLifecycle_WithCashDropAndDiscrepancy()
    {
        // Lifecycle test:
        // Float = ₹1,000 (100,000)
        // PayIn = ₹500 (50,000)
        // PayOut = ₹300 (30,000)
        // CashDrop = ₹1,500 (150,000)
        // Cash Repayment = ₹500 (50,000)
        // Cash Sales = ₹2,500 (250,000)
        // ExpectedCash = 1000 + 2500 + 500 + 500 - 300 - 1500 = ₹2,700 (270,000 paise)
        // Counted = ₹2,650 (265,000 paise) -> Discrepancy = -₹50 (-5,000 paise)

        using var context = new RetailDbContext(_options);
        var (customerService, cashSessionService, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, manager, product) = await SeedStandardTestDataAsync(context);

        // 1. Open session
        var openRes = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 100000, "Morning Shift"), cashier.Id, cashier.Username);
        Assert.True(openRes.Success);
        var sessionId = openRes.Data!.Id;

        // 2. PayIn
        var payInRes = await cashSessionService.AddTransactionAsync(sessionId, new AddCashTransactionRequest(CashTransactionType.PayIn, 50000, "Float topup", Guid.NewGuid()), cashier.Id, cashier.Username, null);
        Assert.True(payInRes.Success);

        // 3. PayOut (authorized by manager)
        var payOutRes = await cashSessionService.AddTransactionAsync(sessionId, new AddCashTransactionRequest(CashTransactionType.PayOut, 30000, "Cleaning supplies", Guid.NewGuid()), cashier.Id, cashier.Username, manager.Id);
        Assert.True(payOutRes.Success);

        // 4. CashDrop (authorized by manager)
        var dropRes = await cashSessionService.AddTransactionAsync(sessionId, new AddCashTransactionRequest(CashTransactionType.CashDrop, 150000, "Safe drop midday", Guid.NewGuid()), cashier.Id, cashier.Username, manager.Id);
        Assert.True(dropRes.Success);

        // 5. Cash Sale ₹2,500
        product.UpdatePrices(Money.FromMinorUnits(100000, Currency.INR), Money.FromMinorUnits(250000, Currency.INR));
        await context.SaveChangesAsync();
        var saleRes = await salesService.CreateSaleAsync(new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 1m, DiscountType.None, 0m) },
            new List<PaymentLineRequest> { new(PaymentMethod.Cash, 250000, 250000) }),
            cashier.Id,
            cashier.Username,
            terminal.Id);
        Assert.True(saleRes.Success);

        // 6. Cash Repayment ₹500
        var customer = new Customer(Guid.NewGuid(), "Repaying Customer", "9876543210", creditLimit: 500m);
        customer.AddCredit(Money.FromMinorUnits(50000, Currency.INR));
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var repayRes = await customerService.RecordPaymentAsync(customer.Id, new RecordCustomerPaymentRequest(50000, PaymentMethod.Cash, null, "Repay debt", Guid.NewGuid()), cashier.Id, cashier.Username, terminal.Id);
        Assert.True(repayRes.Success);

        // 7. Check X-Report
        var xReportRes = await cashSessionService.GetXReportAsync(sessionId);
        Assert.True(xReportRes.Success);
        Assert.Equal(270000, xReportRes.Data!.Session.ExpectedCashPaise);

        // 8. Close session with ₹2,650 counted
        var closeRes = await cashSessionService.CloseSessionAsync(sessionId, new CloseCashSessionRequest(265000, "₹50 short due to change variance"), cashier.Id, cashier.Username);
        Assert.True(closeRes.Success);
        Assert.Equal(CashSessionStatus.Closed, closeRes.Data!.Status);
        Assert.Equal(-5000, closeRes.Data.DiscrepancyPaise);

        // 9. Check Z-Report
        var zReportRes = await cashSessionService.GetZReportAsync(sessionId);
        Assert.True(zReportRes.Success);
        Assert.Equal(265000, zReportRes.Data!.Session.ClosingCashPaise);
        Assert.Equal(-5000, zReportRes.Data!.Session.DiscrepancyPaise);
    }

    [Fact]
    public async Task DatabaseEnforced_SingleOpenSessionPerTerminal()
    {
        using var context = new RetailDbContext(_options);
        var (_, cashSessionService, _, _) = CreateServices(context);
        var (_, terminal, cashier, _, _) = await SeedStandardTestDataAsync(context);

        // First session opens fine
        var res1 = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 10000, null), cashier.Id, cashier.Username);
        Assert.True(res1.Success);

        // Second session on same terminal rejected by service
        var res2 = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 10000, null), cashier.Id, cashier.Username);
        Assert.False(res2.Success);
        Assert.Equal("ACTIVE_SESSION_EXISTS", res2.ErrorCode);

        // Direct DB insert of second open session violates partial unique index UK_cash_sessions_open_terminal
        using var rawContext = new RetailDbContext(_options);
        var secondSession = new CashSession(Guid.NewGuid(), 1, terminal.Id, cashier.Id, Money.FromMinorUnits(10000, Currency.INR));
        rawContext.CashSessions.Add(secondSession);

        var dbEx = await Assert.ThrowsAsync<DbUpdateException>(() => rawContext.SaveChangesAsync());
        Assert.Contains("UNIQUE constraint failed", dbEx.InnerException?.Message ?? dbEx.Message);
    }

    [Fact]
    public async Task AdministrativeForceClose_ClosesSessionAndAudits()
    {
        using var context = new RetailDbContext(_options);
        var (_, cashSessionService, _, _) = CreateServices(context);
        var (_, terminal, cashier, manager, _) = await SeedStandardTestDataAsync(context);

        var openRes = await cashSessionService.OpenSessionAsync(new OpenCashSessionRequest(terminal.Id, 50000, null), cashier.Id, cashier.Username);
        var sessionId = openRes.Data!.Id;

        // Force close by manager
        var forceReq = new ForceCloseCashSessionRequest(50000, "Cashier forgot to close terminal before emergency leave");
        var forceRes = await cashSessionService.ForceCloseSessionAsync(sessionId, forceReq, manager.Id, manager.Username);
        Assert.True(forceRes.Success);
        Assert.Equal(CashSessionStatus.Closed, forceRes.Data!.Status);
        Assert.True(forceRes.Data.IsForceClosed);
        Assert.Equal(0, forceRes.Data.DiscrepancyPaise);
        Assert.Equal("Cashier forgot to close terminal before emergency leave", forceRes.Data.ForceCloseReason);

        // Verify audit log
        using var verifyContext = new RetailDbContext(_options);
        var audit = await verifyContext.AuditLogs.FirstOrDefaultAsync(a => a.Action == "CASH_SESSION_FORCE_CLOSED");
        Assert.NotNull(audit);
        Assert.Equal("CashSession", audit.EntityName);
        Assert.Equal("CASH_SESSION_FORCE_CLOSED", audit.Action);
        Assert.Equal(manager.Username, audit.EntityId);
        Assert.Equal(manager.Id, forceRes.Data.ClosedByUserId);
    }

    [Fact]
    public async Task CustomerHistoryProtection_CannotPhysicallyDeleteCustomerWithHistory()
    {
        using var context = new RetailDbContext(_options);
        var (customerService, _, salesService, _) = CreateServices(context);
        var (_, terminal, cashier, _, product) = await SeedStandardTestDataAsync(context);

        var customer = new Customer(Guid.NewGuid(), "Protected Customer", "9876543210", creditLimit: 1000m);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        // Make a sale: product selling price is 42,000 paise
        var saleRes = await salesService.CreateSaleAsync(new CreateSaleRequest(
            Guid.NewGuid(),
            new List<CartItemRequest> { new(product.Id, 1m, DiscountType.None, 0m) },
            new List<PaymentLineRequest> { new(PaymentMethod.Credit, 42000) },
            DiscountType.None,
            0m,
            customer.Id),
            cashier.Id,
            cashier.Username,
            terminal.Id);
        Assert.True(saleRes.Success);

        // Attempt direct physical delete
        using var deleteContext = new RetailDbContext(_options);
        var trackedCust = await deleteContext.Customers.FirstOrDefaultAsync(c => c.Id == customer.Id);
        Assert.NotNull(trackedCust);
        deleteContext.Customers.Remove(trackedCust);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => deleteContext.SaveChangesAsync());
        Assert.Contains("FOREIGN KEY constraint failed", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task DatabaseIntegrityAndPragmas_VerifyZeroErrors()
    {
        using var context = new RetailDbContext(_options);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var cmdFk = connection.CreateCommand();
        cmdFk.CommandText = "PRAGMA foreign_key_check;";
        await using var readerFk = await cmdFk.ExecuteReaderAsync();
        Assert.False(await readerFk.ReadAsync(), "PRAGMA foreign_key_check reported integrity errors!");

        await using var cmdIntegrity = connection.CreateCommand();
        cmdIntegrity.CommandText = "PRAGMA integrity_check;";
        var integrityResult = (string?)(await cmdIntegrity.ExecuteScalarAsync());
        Assert.Equal("ok", integrityResult);
    }

    [Fact]
    public async Task SqlitePhase8Migration_ExistingPhase7Database_UpgradesSuccessfully()
    {
        var migrationDbPath = Path.Combine(Path.GetTempPath(), $"retailpos_phase8_mig_test_{Guid.NewGuid():N}.db");
        var migrationOptions = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={migrationDbPath}")
            .Options;

        try
        {
            // 1. Create a simulated Phase 7 database (legacy customers table with only Phase 1 columns)
            using (var connection = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();

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
                    INSERT INTO stores (Id, StoreName, CreatedAtUtc, UpdatedAtUtc) VALUES (1, 'Store 1', '2026-01-01', '2026-01-01');

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

                    CREATE TABLE customers (
                        Id TEXT PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Phone TEXT,
                        Email TEXT,
                        OutstandingCredit INTEGER NOT NULL DEFAULT 0,
                        CreditLimit INTEGER NOT NULL DEFAULT 0,
                        IsActive INTEGER NOT NULL DEFAULT 1
                    );
                    INSERT INTO customers (Id, Name, Phone, OutstandingCredit, CreditLimit, IsActive)
                    VALUES ('44444444-4444-4444-4444-444444444444', 'Pre-Phase 8 Customer', '9876543210', 5000, 20000, 1);
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Run DatabaseInitializer migration
            using (var context = new RetailDbContext(migrationOptions))
            {
                await DatabaseInitializer.UpgradeSchemaIfNeededAsync(context);
            }

            // 3. Verify upgraded schema and data
            using (var connection = new SqliteConnection($"Data Source={migrationDbPath}"))
            {
                await connection.OpenAsync();
                using var cmd = connection.CreateCommand();

                // Verify customers table has CreditBalance, Address, Gstin, Notes
                cmd.CommandText = "PRAGMA table_info(customers);";
                var custCols = new List<string>();
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync()) custCols.Add(reader.GetString(1));
                }
                Assert.Contains("CreditBalance", custCols);
                Assert.Contains("Address", custCols);
                Assert.Contains("Gstin", custCols);
                Assert.Contains("Notes", custCols);

                // Verify customer row preserved
                cmd.CommandText = "SELECT Name, OutstandingCredit, CreditLimit, CreditBalance FROM customers WHERE Id = '44444444-4444-4444-4444-444444444444';";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal("Pre-Phase 8 Customer", reader.GetString(0));
                    Assert.Equal(5000, reader.GetInt64(1));
                    Assert.Equal(20000, reader.GetInt64(2));
                    Assert.Equal(0, reader.GetInt64(3)); // default credit balance 0
                }

                // Verify customer_payments table exists
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='customer_payments';";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);

                // Verify cash_sessions and partial unique index exist
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='UK_cash_sessions_open_terminal';";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);

                // Verify foreign key and integrity pragmas report 0 errors
                cmd.CommandText = "PRAGMA foreign_key_check;";
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    Assert.False(await reader.ReadAsync(), "Foreign key violations found!");
                }

                cmd.CommandText = "PRAGMA integrity_check;";
                var integrity = (string?)(await cmd.ExecuteScalarAsync());
                Assert.Equal("ok", integrity);
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
}
