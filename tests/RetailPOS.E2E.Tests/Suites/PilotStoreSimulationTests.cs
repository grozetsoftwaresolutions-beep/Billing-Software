using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Customers;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Contracts.Purchases;
using RetailPOS.Contracts.Reports;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.E2E.Tests.Helpers;
using RetailPOS.E2E.Tests.Simulation;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.E2E.Tests.Suites;

/// <summary>
/// Sub-Phase 15.4: Pilot-Store Simulation & Deterministic Ledger Reconciliation (TEST-P15-27 through TEST-P15-36).
/// Validates full-day retail operating workflows against an independent deterministic mathematical ledger:
///   ExpectedInventory = OpeningInventory + Purchases - Sales + Returns ± Adjustments
///   ExpectedDrawerCash = Float + CashSales + CashRepayments - CashRefunds - PayOuts
///   ExpectedCustomerBalance = Opening + CreditSales - Payments
///   ExpectedSupplierBalance = Opening + Purchases - Payments
/// All expectations are independently derived from pure domain accounting equations.
/// </summary>
public sealed class PilotStoreSimulationTests
{
    [Fact]
    public async Task TEST_P15_27_PilotStore_MorningRoutine_StoreOpenAndFloatDeclaration_AllTerminals()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 100m);

        // Terminal 1 declares ₹2,000 float (200,000 paise)
        // Terminal 2 declares ₹1,500 float (150,000 paise)
        // Terminal 3 declares ₹1,000 float (100,000 paise)
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 200000);
        ledger.DeclareOpeningFloat(env.Counter2.TerminalId, 150000);
        ledger.DeclareOpeningFloat(env.Counter3.TerminalId, 100000);

        var res1 = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 200000, "Counter 1 Morning Float"));
        var res2 = await env.Counter2.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter2.TerminalId, 150000, "Counter 2 Morning Float"));
        var res3 = await env.Counter3.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter3.TerminalId, 100000, "Counter 3 Morning Float"));

        Assert.True(res1.IsSuccessStatusCode, $"Counter 1 open failed: {res1.StatusCode}");
        Assert.True(res2.IsSuccessStatusCode, $"Counter 2 open failed: {res2.StatusCode}");
        Assert.True(res3.IsSuccessStatusCode, $"Counter 3 open failed: {res3.StatusCode}");

        var session1 = (await res1.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        var session2 = (await res2.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        var session3 = (await res3.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;

        Assert.Equal(CashSessionStatus.Open, session1.Status);
        Assert.Equal(CashSessionStatus.Open, session2.Status);
        Assert.Equal(CashSessionStatus.Open, session3.Status);

        ledger.AssertCashDrawerReconciled(env.Counter1.TerminalId, session1);
        ledger.AssertCashDrawerReconciled(env.Counter2.TerminalId, session2);
        ledger.AssertCashDrawerReconciled(env.Counter3.TerminalId, session3);
    }

    [Fact]
    public async Task TEST_P15_28_PilotStore_HighVolumeTransactionStream_300SalesAcrossThreeCounters_Reconciled()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 500m, sellingPricePaise: 1000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 500m);

        // Open cash sessions across all 3 counters
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1 Float"));
        await env.Counter2.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter2.TerminalId, 100000, "C2 Float"));
        await env.Counter3.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter3.TerminalId, 100000, "C3 Float"));

        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);
        ledger.DeclareOpeningFloat(env.Counter2.TerminalId, 100000);
        ledger.DeclareOpeningFloat(env.Counter3.TerminalId, 100000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };

        // High-volume multi-counter simulation: 300 sales transactions distributed across 3 counters
        const int totalSales = 300;
        for (int i = 0; i < totalSales; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                ClientTransactionId: Guid.NewGuid(),
                Items: [new CartItemRequest(env.Product.Id, 1m)],
                Payments: [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

            var saleRes = await counter.PostAsJsonAsync("/api/v1/sales", saleReq);
            Assert.True(saleRes.IsSuccessStatusCode, $"Sale #{i + 1} failed: {saleRes.StatusCode}");

            var saleDto = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            ledger.RecordSale(
                counter.TerminalId,
                [(env.Product.Id, 1m)],
                [(PaymentMethod.Cash, 1000)],
                saleDto.InvoiceNumber);
        }

        // 1. Verify authoritative stock against independent equation:
        // ExpectedStock = 500 initial - 300 sold = 200
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        Assert.True(stockRes.IsSuccessStatusCode);
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        ledger.AssertInventoryReconciled(env.Product.Id, stock);

        // 2. Verify Daily Sales Summary report against independent ledger
        var reportRes = await ownerClient.GetAsync("/api/v1/reports/daily-sales");
        Assert.True(reportRes.IsSuccessStatusCode);
        var report = (await reportRes.Content.ReadFromJsonAsync<ApiResponse<DailySalesSummaryReportDto>>())!.Data!;
        ledger.AssertDailySalesSummaryReconciled(report);

        // 3. Verify contiguous, monotonic invoice sequence across all 300 transactions
        ledger.AssertInvoiceSequenceContiguous();
    }

    [Fact]
    public async Task TEST_P15_29_PilotStore_MixedTenderDistribution_CashCardUpiSplit_AllTotalsMatchDeterministicLedger()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 200m, sellingPricePaise: 1000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 200m);

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        // Create second product (Prod B: ₹2,500 paise, GST 0%)
        var prodBRes = await ownerClient.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
            Sku: $"SKU-B-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name: "Mixed Tender Product B",
            UnitCode: "PC",
            CostPricePaise: 1200,
            SellingPricePaise: 2500,
            TaxCategoryId: 1,
            InitialStock: 100m,
            ReorderLevel: 2m));
        prodBRes.EnsureSuccessStatusCode();
        var prodB = (await prodBRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;
        ledger.RegisterProduct(prodB.Id, prodB.Sku, prodB.Name, 1200, 2500, 1, 0m, 100m);

        // Open Cash Session on Counter 1
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1"));
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);

        // Transaction 1: Pure Cash (1 unit Prod A = 1,000 paise)
        var s1 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]));
        var d1 = (await s1.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 1m)], [(PaymentMethod.Cash, 1000)], d1.InvoiceNumber);

        // Transaction 2: Pure Card (2 units Prod B = 5,000 paise)
        var s2 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(prodB.Id, 2m)], [new PaymentLineRequest(PaymentMethod.Card, 5000, ReferenceNumber: "CARD-AUTH-123")]));
        var d2 = (await s2.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(prodB.Id, 2m)], [(PaymentMethod.Card, 5000)], d2.InvoiceNumber);

        // Transaction 3: Pure UPI (3 units Prod A = 3,000 paise)
        var s3 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 3m)], [new PaymentLineRequest(PaymentMethod.UPI, 3000, ReferenceNumber: "UPI-TXN-456")]));
        var d3 = (await s3.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 3m)], [(PaymentMethod.UPI, 3000)], d3.InvoiceNumber);

        // Transaction 4: Split Cash + Card (1 unit Prod B = 2,500 paise -> Cash 1,000 + Card 1,500)
        var s4 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(prodB.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 1000), new PaymentLineRequest(PaymentMethod.Card, 1500, ReferenceNumber: "CARD-SPLIT-789")]));
        var d4 = (await s4.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(prodB.Id, 1m)], [(PaymentMethod.Cash, 1000), (PaymentMethod.Card, 1500)], d4.InvoiceNumber);

        // Transaction 5: Split Cash + UPI (2 units Prod A = 2,000 paise -> Cash 800 + UPI 1,200)
        var s5 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 800), new PaymentLineRequest(PaymentMethod.UPI, 1200, ReferenceNumber: "UPI-SPLIT-999")]));
        var d5 = (await s5.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 2m)], [(PaymentMethod.Cash, 800), (PaymentMethod.UPI, 1200)], d5.InvoiceNumber);

        // Reconcile Payment Summary Report against Independent Ledger
        var pmtRes = await ownerClient.GetAsync("/api/v1/reports/payment-summary");
        Assert.True(pmtRes.IsSuccessStatusCode);
        var pmt = (await pmtRes.Content.ReadFromJsonAsync<ApiResponse<PaymentSummaryReportDto>>())!.Data!;
        ledger.AssertPaymentSummaryReconciled(pmt);
    }

    [Fact]
    public async Task TEST_P15_30_PilotStore_MidDayReturnsAndRefunds_RestocksInventoryAndReconcilesCashExact()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 2000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 1000, 2000, 1, 0m, 50m);

        // Open Cash Session on Counter 1 (Float = 100,000 paise)
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1"));
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);

        // 1. Initial Sale of 5 units (10,000 paise) for cash
        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 5m)], [new PaymentLineRequest(PaymentMethod.Cash, 10000)]));
        Assert.True(saleRes.IsSuccessStatusCode);
        var sale = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 5m)], [(PaymentMethod.Cash, 10000)], sale.InvoiceNumber);

        // 2. Mid-day Customer Return: Partial return of 2 units (refund 4,000 paise in Cash)
        // Elevate Counter 1 to Manager authorization for refund
        env.Counter1.SetBearerToken(env.ManagerToken);

        var saleItemId = sale.Items[0].Id;
        var refundReq = new RefundSaleRequest(
            ClientTransactionId: Guid.NewGuid(),
            Items: [new RefundItemRequest(saleItemId, 2m)],
            RefundMethod: PaymentMethod.Cash,
            Reason: "Customer purchased wrong size");

        var refundRes = await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{sale.Id}/refund", refundReq);
        Assert.True(refundRes.IsSuccessStatusCode, $"Refund failed: {refundRes.StatusCode}");

        ledger.RecordRefund(env.Counter1.TerminalId, [(env.Product.Id, 2m)], PaymentMethod.Cash, 4000);

        // 3. Verify stock: 50 opening - 5 sold + 2 restocked = 47
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        ledger.AssertInventoryReconciled(env.Product.Id, stock);

        // 4. Verify cash drawer expected cash:
        // ExpectedDrawerCash = Float(100,000) + CashSale(10,000) - CashRefund(4,000) = 106,000 paise
        var sessionRes = await ownerClient.GetAsync($"/api/v1/cash-sessions/current?terminalId={env.Counter1.TerminalId}");
        var session = (await sessionRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        ledger.AssertCashDrawerReconciled(env.Counter1.TerminalId, session);
    }

    [Fact]
    public async Task TEST_P15_31_PilotStore_MidDaySupplierPurchaseReceiving_IncrementsStockAccurately()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, costPricePaise: 400, sellingPricePaise: 800);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 400, 800, 1, 0m, 50m);
        ledger.RegisterSupplier(env.SupplierId, 0);

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        // Create second product for multi-item purchase receiving
        var prodBRes = await ownerClient.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
            Sku: $"SKU-P-{Guid.NewGuid():N}"[..12].ToUpperInvariant(),
            Name: "Purchased Product B",
            UnitCode: "PC",
            CostPricePaise: 600,
            SellingPricePaise: 1200,
            TaxCategoryId: 1,
            InitialStock: 20m,
            ReorderLevel: 5m));
        prodBRes.EnsureSuccessStatusCode();
        var prodB = (await prodBRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;
        ledger.RegisterProduct(prodB.Id, prodB.Sku, prodB.Name, 600, 1200, 1, 0m, 20m);

        // Inward 100 units of Prod A (cost 400 = 40,000) and 50 units of Prod B (cost 600 = 30,000)
        // Total Purchase = 70,000 paise. Initial Payment = 20,000 paise. Balance Due = 50,000 paise.
        var purchaseReq = new CreatePurchaseRequest(
            SupplierId: env.SupplierId,
            SupplierInvoiceNumber: $"SUP-INV-{Guid.NewGuid():N}"[..12],
            InvoiceDate: DateOnly.FromDateTime(DateTime.UtcNow),
            Items:
            [
                new CreatePurchaseItemRequest(env.Product.Id, 100m, 400, []),
                new CreatePurchaseItemRequest(prodB.Id, 50m, 600, [])
            ],
            InitialPaymentAmountPaise: 20000,
            InitialPaymentMethod: PaymentMethod.Cash,
            Notes: "Mid-day pilot store restock shipment",
            ClientTransactionId: Guid.NewGuid());

        var purRes = await ownerClient.PostAsJsonAsync("/api/v1/purchases", purchaseReq);
        Assert.True(purRes.IsSuccessStatusCode, $"Purchase creation failed: {purRes.StatusCode}");

        ledger.RecordPurchase(env.SupplierId, [(env.Product.Id, 100m, 400), (prodB.Id, 50m, 600)], 20000);

        // 1. Verify Prod A stock: 50 opening + 100 received = 150
        var stockARes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stockA = (await stockARes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        ledger.AssertInventoryReconciled(env.Product.Id, stockA);

        // 2. Verify Prod B stock: 20 opening + 50 received = 70
        var stockBRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{prodB.Id}");
        var stockB = (await stockBRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        ledger.AssertInventoryReconciled(prodB.Id, stockB);

        // 3. Verify Supplier Balances report
        var supBalRes = await ownerClient.GetAsync("/api/v1/reports/supplier-balances");
        Assert.True(supBalRes.IsSuccessStatusCode);
        var supBal = (await supBalRes.Content.ReadFromJsonAsync<ApiResponse<SupplierBalanceReportDto>>())!.Data!;
        var supplierLine = supBal.Suppliers.First(s => s.SupplierId == env.SupplierId);
        ledger.AssertSupplierReconciled(env.SupplierId, supplierLine);
    }

    [Fact]
    public async Task TEST_P15_32_PilotStore_CustomerCreditSalesAndSettlements_LedgerBalancesReconciled()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 100m, customerCreditLimitPaise: 50000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 100m);
        ledger.RegisterCustomer(env.CustomerId, 0);

        // Counter 1 opens session (Float = 50,000 paise)
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 50000, "C1"));
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 50000);

        // 1. Credit Sale 1: 15 units of Prod A (15,000 paise) on credit
        var s1 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 15m)], [new PaymentLineRequest(PaymentMethod.Credit, 15000)], CustomerId: env.CustomerId));
        Assert.True(s1.IsSuccessStatusCode);
        var d1 = (await s1.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 15m)], [(PaymentMethod.Credit, 15000)], d1.InvoiceNumber, env.CustomerId);

        // 2. Credit Sale 2: 10 units of Prod A (10,000 paise) on credit
        var s2 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 10m)], [new PaymentLineRequest(PaymentMethod.Credit, 10000)], CustomerId: env.CustomerId));
        Assert.True(s2.IsSuccessStatusCode);
        var d2 = (await s2.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 10m)], [(PaymentMethod.Credit, 10000)], d2.InvoiceNumber, env.CustomerId);

        // 3. Customer visits Counter 1 and makes cash repayment of 12,000 paise
        var payReq = new RecordCustomerPaymentRequest(
            AmountPaise: 12000,
            Method: PaymentMethod.Cash,
            ReferenceNumber: "CUST-REC-001",
            Notes: "Partial credit bill payment",
            ClientTransactionId: Guid.NewGuid());

        var payRes = await env.Counter1.PostAsJsonAsync($"/api/v1/customers/{env.CustomerId}/payments", payReq);
        Assert.True(payRes.IsSuccessStatusCode, $"Customer payment failed: {payRes.StatusCode}");

        ledger.RecordCustomerPayment(env.CustomerId, env.Counter1.TerminalId, 12000, PaymentMethod.Cash);

        // 4. Verify Customer Outstanding Balance: 0 + 15,000 + 10,000 - 12,000 = 13,000 paise
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        var custRes = await ownerClient.GetAsync($"/api/v1/customers/{env.CustomerId}");
        var cust = (await custRes.Content.ReadFromJsonAsync<ApiResponse<CustomerDto>>())!.Data!;
        ledger.AssertCustomerReconciled(env.CustomerId, cust);

        // 5. Verify Counter 1 Drawer Cash:
        // ExpectedDrawerCash = Float(50,000) + CashRepayment(12,000) = 62,000 paise
        var sessionRes = await ownerClient.GetAsync($"/api/v1/cash-sessions/current?terminalId={env.Counter1.TerminalId}");
        var session = (await sessionRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        ledger.AssertCashDrawerReconciled(env.Counter1.TerminalId, session);
    }

    [Fact]
    public async Task TEST_P15_33_PilotStore_EveningRoutine_CashierShiftClose_BlindCountOverShortCalculated()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 100m, sellingPricePaise: 1000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 100m);

        // Open 3 sessions
        var s1Res = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1"));
        var s2Res = await env.Counter2.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter2.TerminalId, 100000, "C2"));
        var s3Res = await env.Counter3.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter3.TerminalId, 100000, "C3"));

        var sess1Id = (await s1Res.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;
        var sess2Id = (await s2Res.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;
        var sess3Id = (await s3Res.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;

        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);
        ledger.DeclareOpeningFloat(env.Counter2.TerminalId, 100000);
        ledger.DeclareOpeningFloat(env.Counter3.TerminalId, 100000);

        // Counter 1: Cash sale 30,000 -> Expected = 130,000
        var sale1 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 30m)], [new PaymentLineRequest(PaymentMethod.Cash, 30000)]));
        var d1 = (await sale1.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 30m)], [(PaymentMethod.Cash, 30000)], d1.InvoiceNumber);

        // Counter 2: Cash sale 40,000 -> Expected = 140,000
        var sale2 = await env.Counter2.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 40m)], [new PaymentLineRequest(PaymentMethod.Cash, 40000)]));
        var d2 = (await sale2.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter2.TerminalId, [(env.Product.Id, 40m)], [(PaymentMethod.Cash, 40000)], d2.InvoiceNumber);

        // Counter 3: Cash sale 20,000 -> Expected = 120,000
        var sale3 = await env.Counter3.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 20m)], [new PaymentLineRequest(PaymentMethod.Cash, 20000)]));
        var d3 = (await sale3.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter3.TerminalId, [(env.Product.Id, 20m)], [(PaymentMethod.Cash, 20000)], d3.InvoiceNumber);

        // Evening Shift Close Declarations:
        // Counter 1: Counted = 130,000 (Exact match -> Discrepancy = 0)
        var close1 = await env.Counter1.PostAsJsonAsync($"/api/v1/cash-sessions/{sess1Id}/close",
            new CloseCashSessionRequest(130000, "Even count"));
        Assert.True(close1.IsSuccessStatusCode);
        var c1Dto = (await close1.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        Assert.Equal(CashSessionStatus.Closed, c1Dto.Status);
        Assert.Equal(0, c1Dto.DiscrepancyPaise);

        // Counter 2: Counted = 142,500 (+2,500 overage)
        var close2 = await env.Counter2.PostAsJsonAsync($"/api/v1/cash-sessions/{sess2Id}/close",
            new CloseCashSessionRequest(142500, "Found loose cash in drawer"));
        Assert.True(close2.IsSuccessStatusCode);
        var c2Dto = (await close2.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        Assert.Equal(CashSessionStatus.Closed, c2Dto.Status);
        Assert.Equal(2500, c2Dto.DiscrepancyPaise);

        // Counter 3: Counted = 118,000 (-2,000 shortage)
        var close3 = await env.Counter3.PostAsJsonAsync($"/api/v1/cash-sessions/{sess3Id}/close",
            new CloseCashSessionRequest(118000, "Shortage notes"));
        Assert.True(close3.IsSuccessStatusCode);
        var c3Dto = (await close3.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        Assert.Equal(CashSessionStatus.Closed, c3Dto.Status);
        Assert.Equal(-2000, c3Dto.DiscrepancyPaise);
    }

    [Fact]
    public async Task TEST_P15_34_PilotStore_StoreZReport_GrossNetTaxAndPayments_MatchDeterministicLedgerExact()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 100m, sellingPricePaise: 1000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 100m);

        // Open session on Counter 1
        var openRes = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1 Z-Report Session"));
        var sessionId = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!.Id;
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);

        // Execute transactions: Cash, Card, UPI
        var s1 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 5m)], [new PaymentLineRequest(PaymentMethod.Cash, 5000)]));
        Assert.True(s1.IsSuccessStatusCode, $"Sale 1 failed: {s1.StatusCode} - {await s1.Content.ReadAsStringAsync()}");
        var d1 = (await s1.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 5m)], [(PaymentMethod.Cash, 5000)], d1.InvoiceNumber);

        var s2 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 3m)], [new PaymentLineRequest(PaymentMethod.Card, 3000, ReferenceNumber: "CARD-REF-001")]));
        Assert.True(s2.IsSuccessStatusCode, $"Sale 2 failed: {s2.StatusCode} - {await s2.Content.ReadAsStringAsync()}");
        var d2 = (await s2.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 3m)], [(PaymentMethod.Card, 3000)], d2.InvoiceNumber);

        // Close session
        var closeRes = await env.Counter1.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionId}/close",
            new CloseCashSessionRequest(105000, "Closing shift"));
        Assert.True(closeRes.IsSuccessStatusCode);

        // Query authoritative Z-Report for closed session
        var zRes = await env.Counter1.GetAsync($"/api/v1/cash-sessions/{sessionId}/z-report");
        Assert.True(zRes.IsSuccessStatusCode, $"Z-Report failed: {zRes.StatusCode}");
        var zReport = (await zRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionReportDto>>())!.Data!;

        Assert.Equal(CashSessionStatus.Closed, zReport.Session.Status);
        Assert.Equal(100000, zReport.Session.OpeningFloatPaise);
        Assert.Equal(5000, zReport.Session.TotalCashSalesPaise);
        Assert.Equal(105000, zReport.Session.ExpectedCashPaise);
        Assert.Equal(0, zReport.Session.DiscrepancyPaise);

        // Query Daily Sales Summary and assert 100% match with independent ledger
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var reportRes = await ownerClient.GetAsync("/api/v1/reports/daily-sales");
        var report = (await reportRes.Content.ReadFromJsonAsync<ApiResponse<DailySalesSummaryReportDto>>())!.Data!;
        ledger.AssertDailySalesSummaryReconciled(report);
    }

    [Fact]
    public async Task TEST_P15_35_PilotStore_NightlyAutomatedBackup_GeneratesSignedManifestAndValidSnapshot()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        // Execute a sale before backup
        var sale = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
            Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 2m)], [new PaymentLineRequest(PaymentMethod.Cash, 2000)]));
        Assert.True(sale.IsSuccessStatusCode);

        // Nightly backup triggered by Store Owner / Scheduled job
        var backupReq = new CreateBackupRequestDto { Reason = "Nightly Pilot Store EOD Backup" };
        var backupRes = await ownerClient.PostAsJsonAsync("/api/v1/backups", backupReq);
        Assert.True(backupRes.IsSuccessStatusCode, $"Backup failed: {backupRes.StatusCode}");

        var backup = (await backupRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>())!.Data!;
        Assert.True(File.Exists(backup.FilePath), "Backup file does not exist on disk.");

        var manifestPath = $"{backup.FilePath}.manifest";
        Assert.True(File.Exists(manifestPath), "Backup manifest file does not exist.");

        // Read manifest and verify cryptographic signature
        using (var scope = fixture.Services.CreateScope())
        {
            var manifestService = scope.ServiceProvider.GetRequiredService<IBackupManifestService>();
            var storeIdentity = scope.ServiceProvider.GetRequiredService<IStoreIdentityService>();
            var manifest = await manifestService.LoadManifestAsync(manifestPath);

            Assert.NotNull(manifest);
            Assert.NotEqual(Guid.Empty, manifest.StoreId);
            Assert.False(string.IsNullOrWhiteSpace(manifest.StoreName));
            Assert.False(string.IsNullOrWhiteSpace(manifest.DatabaseSha256));
            Assert.True(manifestService.VerifySignature(manifest, storeIdentity.GetStoreSigningKey()),
                "Nightly backup manifest signature verification failed.");
        }
    }

    [Fact]
    public async Task TEST_P15_36_PilotStore_EndOfWeekDatabaseIntegrity_ZeroOrphanRecordsZeroCorruption()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 100m, sellingPricePaise: 1000);
        var ledger = new IndependentPilotLedger();

        ledger.RegisterProduct(env.Product.Id, env.Product.Sku, env.Product.Name, 500, 1000, 1, 0m, 100m);

        // Open sessions
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1"));
        ledger.DeclareOpeningFloat(env.Counter1.TerminalId, 100000);

        // Execute cycle of sales
        for (int i = 0; i < 15; i++)
        {
            var sale = await env.Counter1.PostAsJsonAsync("/api/v1/sales", new CreateSaleRequest(
                Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 2m)], [new PaymentLineRequest(PaymentMethod.Cash, 2000)]));
            var d = (await sale.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
            ledger.RecordSale(env.Counter1.TerminalId, [(env.Product.Id, 2m)], [(PaymentMethod.Cash, 2000)], d.InvoiceNumber);
        }

        // Mid-week return
        env.Counter1.SetBearerToken(env.ManagerToken);
        var salesListRes = await env.Counter1.GetAsync("/api/v1/sales?pageSize=1");
        var pagedSales = (await salesListRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<SaleInvoiceDto>>>())!.Data!;
        var firstSaleId = pagedSales.Items[0].Id;

        var firstSaleRes = await env.Counter1.GetAsync($"/api/v1/sales/{firstSaleId}");
        var firstSaleDetails = (await firstSaleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        await env.Counter1.PostAsJsonAsync($"/api/v1/sales/{firstSaleId}/refund", new RefundSaleRequest(
            Guid.NewGuid(), [new RefundItemRequest(firstSaleDetails.Items[0].Id, 1m)], PaymentMethod.Cash, "Customer return"));
        ledger.RecordRefund(env.Counter1.TerminalId, [(env.Product.Id, 1m)], PaymentMethod.Cash, 1000);

        // Database low-level integrity invariants via direct SQLite inspection
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();

        // 1. PRAGMA integrity_check
        var integrityResult = await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToListAsync();
        Assert.Single(integrityResult);
        Assert.Equal("ok", integrityResult[0]);

        // 2. PRAGMA foreign_key_check
        var fkResult = await db.Database.SqlQueryRaw<string>("PRAGMA foreign_key_check;").ToListAsync();
        Assert.Empty(fkResult);

        // 3. Entity relationship integrity: zero orphaned sale items or payment records
        var orphanSaleItems = await db.SalesItems
            .Where(si => !db.SalesInvoices.Any(s => s.Id == si.SaleId))
            .CountAsync();
        Assert.Equal(0, orphanSaleItems);

        var orphanPaymentRecords = await db.Payments
            .Where(pr => !db.SalesInvoices.Any(s => s.Id == pr.SaleId))
            .CountAsync();
        Assert.Equal(0, orphanPaymentRecords);

        var orphanStockMovements = await db.StockMovements
            .Where(sm => !db.Products.Any(p => p.Id == sm.ProductId))
            .CountAsync();
        Assert.Equal(0, orphanStockMovements);

        // 4. Assert stock balances in database match independent ledger closing stock exactly
        var stockRecord = await db.StockBalances.FirstAsync(s => s.ProductId == env.Product.Id);
        Assert.Equal(ledger.Products[env.Product.Id].ExpectedStock, stockRecord.CurrentQuantity.Value);
    }
}
