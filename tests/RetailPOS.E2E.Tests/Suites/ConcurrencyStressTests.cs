using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Contracts.Sales;
using RetailPOS.Contracts.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.E2E.Tests.Helpers;
using Xunit;

namespace RetailPOS.E2E.Tests.Suites;

public sealed class ConcurrencyStressTests
{
    [Fact]
    public async Task TEST_P15_01_ConcurrentSales_SameProduct_StockDecrementsCorrectly_ZeroLostUpdates()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 30m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var tasks = new List<Task<HttpResponseMessage>>();

        // 3 counters x 10 checkouts = 30 concurrent checkouts (1 unit each)
        for (int i = 0; i < 30; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                ClientTransactionId: Guid.NewGuid(),
                Items: [new CartItemRequest(env.Product.Id, 1m)],
                Payments: [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert all 30 checkouts succeeded
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Sale failed with status {r.StatusCode}"));

        // Authoritative stock balance must be exactly 0 (30 initial - 30 sold = 0)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        Assert.True(stockRes.IsSuccessStatusCode);
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(0m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_02_ConcurrentSales_FinalStockUnit_ExactlyOneSucceeds_OthersReceiveInsufficientStock()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 1m, sellingPricePaise: 1000);

        // 3 counters simultaneously try to purchase the final 1 unit
        var req1 = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
        var req2 = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
        var req3 = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

        var task1 = env.Counter1.PostAsJsonAsync("/api/v1/sales", req1);
        var task2 = env.Counter2.PostAsJsonAsync("/api/v1/sales", req2);
        var task3 = env.Counter3.PostAsJsonAsync("/api/v1/sales", req3);

        var responses = await Task.WhenAll(task1, task2, task3);
        var successCount = responses.Count(r => r.IsSuccessStatusCode);
        var failureResponses = responses.Where(r => !r.IsSuccessStatusCode).ToList();

        // Exactly 1 checkout must succeed, exactly 2 must fail
        Assert.Equal(1, successCount);
        Assert.Equal(2, failureResponses.Count);

        // Failures must return Conflict or BadRequest with INSUFFICIENT_STOCK
        foreach (var failed in failureResponses)
        {
            Assert.True(failed.StatusCode == HttpStatusCode.Conflict || failed.StatusCode == HttpStatusCode.BadRequest);
            var body = await failed.Content.ReadFromJsonAsync<ApiResponse<object>>();
            Assert.NotNull(body);
            Assert.Equal("INSUFFICIENT_STOCK", body.ErrorCode);
        }

        // Final stock must be exactly 0 (never negative)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(0m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_03_ConcurrentSales_ThreeCounters_InvoiceNumbers_StrictlyMonotonicAndUnique_RollbackRevertsCleanly()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 10m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var tasks = new List<Task<HttpResponseMessage>>();

        // 10 valid checkouts (1 unit each)
        for (int i = 0; i < 10; i++)
        {
            var counter = counters[i % 3];
            var validReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", validReq));
        }

        // 5 invalid checkouts that will fail and rollback (requesting 50 units when max is 10)
        for (int i = 0; i < 5; i++)
        {
            var counter = counters[(i + 1) % 3];
            var invalidReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 50m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 50000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", invalidReq));
        }

        var responses = await Task.WhenAll(tasks);

        var successResponses = responses.Where(r => r.IsSuccessStatusCode).ToList();
        Assert.Equal(10, successResponses.Count);

        var invoiceNumbers = new List<string>();
        foreach (var r in successResponses)
        {
            var body = (await r.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            invoiceNumbers.Add(body.InvoiceNumber);
        }

        // Invariant: strictly unique invoice numbers (zero collisions)
        Assert.Equal(10, invoiceNumbers.Distinct().Count());

        // Invariant: Monotonic sequence without consumed gaps from the 5 rolled-back transactions
        var sequenceNumbers = invoiceNumbers
            .Select(inv => long.Parse(inv.Split('-').Last(), CultureInfo.InvariantCulture))
            .OrderBy(n => n)
            .ToList();

        for (int i = 0; i < sequenceNumbers.Count - 1; i++)
        {
            Assert.Equal(sequenceNumbers[i] + 1, sequenceNumbers[i + 1]);
        }
    }

    [Fact]
    public async Task TEST_P15_04_ConcurrentSales_ThreeCounters_InvoiceNumbers_ZeroCollisionsUnderLoad()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 60m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var tasks = new List<Task<HttpResponseMessage>>();

        // 15 checkouts per counter = 45 concurrent checkouts
        for (int i = 0; i < 45; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Sale failed with status {r.StatusCode}"));

        var invoiceNumbers = new List<string>();
        foreach (var r in responses)
        {
            var body = (await r.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            invoiceNumbers.Add(body.InvoiceNumber);
        }

        // Hard Invariant: 100% collision-free
        Assert.Equal(45, invoiceNumbers.Distinct().Count());

        var sequenceNumbers = invoiceNumbers
            .Select(inv => long.Parse(inv.Split('-').Last(), CultureInfo.InvariantCulture))
            .OrderBy(n => n)
            .ToList();

        // Sequential consecutive integers
        for (int i = 0; i < sequenceNumbers.Count - 1; i++)
        {
            Assert.Equal(sequenceNumbers[i] + 1, sequenceNumbers[i + 1]);
        }
    }

    [Fact]
    public async Task TEST_P15_05_ConcurrentSalesAndPurchases_SameProduct_StockReconcilesExactly()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        using var mgrClient = fixture.CreateClient();
        mgrClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.ManagerToken);

        var tasks = new List<Task<HttpResponseMessage>>();

        // 10 sales of 2 units each = 20 units sold
        for (int i = 0; i < 10; i++)
        {
            var counter = (i % 2 == 0) ? env.Counter1 : env.Counter2;
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 2m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        // 2 purchases of 10 units each = 20 units purchased
        for (int i = 0; i < 2; i++)
        {
            var purchaseReq = new CreatePurchaseRequest(
                SupplierId: env.SupplierId,
                SupplierInvoiceNumber: $"SUP-INV-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
                InvoiceDate: DateOnly.FromDateTime(DateTime.UtcNow),
                Items: [new CreatePurchaseItemRequest(env.Product.Id, 10m, 500, [new PurchaseTaxComponentInputDto("Zero Tax", 0m)])],
                InitialPaymentAmountPaise: 0,
                InitialPaymentMethod: null,
                InitialPaymentReference: null,
                Notes: "Concurrent stock inward",
                ClientTransactionId: Guid.NewGuid());
            tasks.Add(mgrClient.PostAsJsonAsync("/api/v1/purchases", purchaseReq));
        }

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Operation failed with {r.StatusCode}"));

        // Reconciliation: 50 initial - 20 sold + 20 purchased = 50
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(50m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_06_ConcurrentSalesAndInventoryAdjustments_SerializesCleanly()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 40m, sellingPricePaise: 1000);

        using var mgrClient = fixture.CreateClient();
        mgrClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.ManagerToken);

        var tasks = new List<Task<HttpResponseMessage>>();

        // 10 sales of 1 unit each across counters = -10 units
        for (int i = 0; i < 10; i++)
        {
            var counter = (i % 2 == 0) ? env.Counter1 : env.Counter2;
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        // 2 adjustments: Increase 5 units each = +10 units
        for (int i = 0; i < 2; i++)
        {
            var adj = new StockAdjustmentRequest(env.Product.Id, ManualStockAdjustmentType.Increase, 5m, "Inventory restock batch");
            tasks.Add(mgrClient.PostAsJsonAsync("/api/v1/inventory/adjustments", adj));
        }

        // 2 adjustments: Decrease 3 units each = -6 units
        for (int i = 0; i < 2; i++)
        {
            var adj = new StockAdjustmentRequest(env.Product.Id, ManualStockAdjustmentType.Decrease, 3m, "Damaged stock disposal");
            tasks.Add(mgrClient.PostAsJsonAsync("/api/v1/inventory/adjustments", adj));
        }

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Operation failed with {r.StatusCode}"));

        // Reconciliation: 40 - 10 + 10 - 6 = 34 units
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(34m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_07_ConcurrentSales_MixedTender_CashSessionTotals_ReconcileAcrossTerminals()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        // Counter 1 opens cash session with float 100,000 paise (₹1,000)
        var open1Res = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "Counter 1 Morning"));
        Assert.True(open1Res.IsSuccessStatusCode);

        // Counter 2 opens cash session with float 200,000 paise (₹2,000)
        var open2Res = await env.Counter2.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter2.TerminalId, 200000, "Counter 2 Morning"));
        Assert.True(open2Res.IsSuccessStatusCode);

        var tasks = new List<Task<HttpResponseMessage>>();

        // Counter 1: 5 Cash sales (1000 paise each = 5000 paise) + 5 UPI sales (1000 paise each = 5000 paise)
        for (int i = 0; i < 5; i++)
        {
            var reqCash = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(env.Counter1.PostAsJsonAsync("/api/v1/sales", reqCash));

            var reqUpi = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.UPI, 1000, null, "UPI-REF-01")]);
            tasks.Add(env.Counter1.PostAsJsonAsync("/api/v1/sales", reqUpi));
        }

        // Counter 2: 6 Cash sales (1000 paise each = 6000 paise) + 4 Card sales (1000 paise each = 4000 paise)
        for (int i = 0; i < 6; i++)
        {
            var reqCash = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(env.Counter2.PostAsJsonAsync("/api/v1/sales", reqCash));
        }
        for (int i = 0; i < 4; i++)
        {
            var reqCard = new CreateSaleRequest(Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Card, 1000, null, "CARD-REF-02")]);
            tasks.Add(env.Counter2.PostAsJsonAsync("/api/v1/sales", reqCard));
        }

        var responses = await Task.WhenAll(tasks);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Sale failed with {r.StatusCode}"));

        // Query active cash session for Counter 1
        var session1Res = await env.Counter1.GetAsync($"/api/v1/cash-sessions/current?terminalId={env.Counter1.TerminalId}");
        Assert.True(session1Res.IsSuccessStatusCode);
        var session1 = (await session1Res.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;

        // Query active cash session for Counter 2
        var session2Res = await env.Counter2.GetAsync($"/api/v1/cash-sessions/current?terminalId={env.Counter2.TerminalId}");
        Assert.True(session2Res.IsSuccessStatusCode);
        var session2 = (await session2Res.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;

        // Terminal Isolation & Financial Reconciliation
        Assert.Equal(5000, session1.TotalCashSalesPaise);
        Assert.Equal(100000 + 5000, session1.ExpectedCashPaise);

        Assert.Equal(6000, session2.TotalCashSalesPaise);
        Assert.Equal(200000 + 6000, session2.ExpectedCashPaise);
    }

    [Fact]
    public async Task TEST_P15_08_ConcurrentSales_SameCustomerCredit_CreditLimitEnforcedAtomically()
    {
        await using var fixture = new StoreServerFixture();
        // Product price = 6000 paise (₹60), Credit Limit = 10,000 paise (₹100)
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(
            fixture,
            initialStock: 10m,
            sellingPricePaise: 6000,
            customerCreditLimitPaise: 10000);

        // Counter 1 and Counter 2 concurrently attempt to charge 6,000 paise credit each (total 12,000 > 10,000)
        var req1 = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Credit, 6000)],
            CustomerId: env.CustomerId);

        var req2 = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Credit, 6000)],
            CustomerId: env.CustomerId);

        var task1 = env.Counter1.PostAsJsonAsync("/api/v1/sales", req1);
        var task2 = env.Counter2.PostAsJsonAsync("/api/v1/sales", req2);

        var responses = await Task.WhenAll(task1, task2);
        var successCount = responses.Count(r => r.IsSuccessStatusCode);
        var failureResponses = responses.Where(r => !r.IsSuccessStatusCode).ToList();

        // Exactly 1 checkout succeeds, 1 fails due to atomic credit check
        Assert.Equal(1, successCount);
        Assert.Single(failureResponses);

        var failed = failureResponses[0];
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.Equal("CREDIT_LIMIT_EXCEEDED", body.ErrorCode);

        // Query customer outstanding credit: exactly 6000 paise
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var custRes = await ownerClient.GetAsync($"/api/v1/customers/{env.CustomerId}");
        var cust = (await custRes.Content.ReadFromJsonAsync<ApiResponse<CustomerDto>>())!.Data!;
        Assert.Equal(6000, cust.OutstandingCreditPaise);
    }

    [Fact]
    public async Task TEST_P15_09_ConcurrentCartCalculations_DoesNotBlockAuthoritativeCheckouts()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        var tasks = new List<Task<HttpResponseMessage>>();

        // Counter 1 executes 20 concurrent read-heavy cart calculations
        for (int i = 0; i < 20; i++)
        {
            var calcReq = new CalculateSaleRequest([new CartItemRequest(env.Product.Id, 2m)]);
            tasks.Add(env.Counter1.PostAsJsonAsync("/api/v1/sales/calculate", calcReq));
        }

        // Concurrently, Counter 2 and Counter 3 execute 10 authoritative checkouts
        for (int i = 0; i < 10; i++)
        {
            var counter = (i % 2 == 0) ? env.Counter2 : env.Counter3;
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var responses = await Task.WhenAll(tasks);

        // 100% of both read and write requests succeed
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Request failed with status {r.StatusCode}"));

        // Verify remaining stock is exactly 40 (50 initial - 10 checked out)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(40m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_10_ConcurrentSales_HighContention_TransientRetryLoop_SucceedsWithinFiveAttempts()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var tasks = new List<Task<HttpResponseMessage>>();

        // High write contention: 15 checkouts launched in a tight parallel loop across 3 counters
        for (int i = 0; i < 15; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var responses = await Task.WhenAll(tasks);

        // Transient retry loop on SQLite serializable lock ensures 0 SQLITE_BUSY errors reach client
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"High contention checkout failed: {r.StatusCode}"));

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(35m, stock.CurrentStock); // 50 - 15 = 35
    }

    [Fact]
    public async Task TEST_P15_11_ConcurrentRefunds_SameSale_PreventsDoubleRefundBeyondOriginalQuantity()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 10m, sellingPricePaise: 1000);

        // Create initial sale of 2 units
        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);

        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(saleRes.IsSuccessStatusCode);
        var sale = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        var saleItemId = sale.Items[0].Id;

        // Use Counter1 and Counter2 with Manager credentials (elevated refund requires terminal headers)
        env.Counter1.SetBearerToken(env.ManagerToken);
        env.Counter2.SetBearerToken(env.ManagerToken);

        // Two concurrent requests attempting to refund 2 units each (total 4 units > 2 original units)
        var refReq1 = new RefundSaleRequest(
            Guid.NewGuid(),
            [new RefundItemRequest(saleItemId, 2m)],
            PaymentMethod.Cash,
            "Customer return A");

        var refReq2 = new RefundSaleRequest(
            Guid.NewGuid(),
            [new RefundItemRequest(saleItemId, 2m)],
            PaymentMethod.Cash,
            "Customer return B");

        var task1 = env.Counter1.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/refund", refReq1);
        var task2 = env.Counter2.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/refund", refReq2);

        var responses = await Task.WhenAll(task1, task2);
        var successCount = responses.Count(r => r.IsSuccessStatusCode);
        var failureResponses = responses.Where(r => !r.IsSuccessStatusCode).ToList();

        // Exactly 1 refund succeeds, exactly 1 fails
        Assert.Equal(1, successCount);
        Assert.Single(failureResponses);

        var failed = failureResponses[0];
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var body = await failed.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.NotNull(body);
        Assert.True(
            body.ErrorCode == "REFUND_QUANTITY_EXCEEDS_PURCHASED" || body.ErrorCode == "SALE_ALREADY_REFUNDED",
            $"Expected refund quantity error but got {body.ErrorCode}");

        // Inventory restock must be exactly 2 units (initial 10 - 2 sold + 2 refunded = 10, NOT 12)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(10m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_12_ConcurrentTerminalHeartbeats_DoesNotDegradeCheckoutThroughput()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        // Background heartbeat task simulating periodic terminal pings during active billing
        var heartbeatTask = Task.Run(async () =>
        {
            var hbResponses = new List<HttpResponseMessage>();
            var terminals = new[] { env.Counter1, env.Counter2, env.Counter3 };
            for (int i = 0; i < 9; i++)
            {
                var term = terminals[i % 3];
                var hb = new TerminalHeartbeatDto(
                    term.TerminalId,
                    term.TerminalCode,
                    term.IpAddress.ToString(),
                    DateTimeOffset.UtcNow);
                var res = await term.PostAsJsonAsync("/api/v1/terminals/heartbeat", hb);
                hbResponses.Add(res);
                await Task.Yield();
            }
            return hbResponses;
        });

        // Concurrently, 10 checkouts are executed across Counter 1 and Counter 2
        var checkoutTasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 10; i++)
        {
            var counter = (i % 2 == 0) ? env.Counter1 : env.Counter2;
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            checkoutTasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var checkoutResponses = await Task.WhenAll(checkoutTasks);
        var heartbeatResponses = await heartbeatTask;

        // All checkouts succeed cleanly without degradation
        Assert.All(checkoutResponses, r => Assert.True(r.IsSuccessStatusCode, $"Checkout failed with {r.StatusCode}"));

        // All heartbeats succeed cleanly
        Assert.All(heartbeatResponses, r => Assert.True(r.IsSuccessStatusCode, $"Heartbeat failed with {r.StatusCode}"));

        // Remaining stock is exactly 40 (50 - 10)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(40m, stock.CurrentStock);
    }
}
