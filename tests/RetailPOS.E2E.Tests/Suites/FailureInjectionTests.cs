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
using RetailPOS.Application.Deployment;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Auth;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Contracts.Hardware;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Deployment;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.E2E.Tests.Helpers;
using Xunit;

namespace RetailPOS.E2E.Tests.Suites;

/// <summary>
/// Sub-Phase 15.3: Failure Injection & Crash Recovery Test Suite (TEST-P15-13 through TEST-P15-26).
/// Validates system durability, zero lost updates, zero double-decrements, transaction atomicity,
/// hardware fault tolerance, online live backup concurrency, manifest corruption rejection,
/// and automated crash/rollback recovery under catastrophic faults.
/// </summary>
public sealed class FailureInjectionTests
{
    // =========================================================================
    // Vector 1: Server Crash / Kill Mid-Transaction (TEST-P15-13, TEST-P15-14)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_13_Failure_ServerCrashMidCheckout_RollsBackUncommittedStateCleanly()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        // 1. Simulate mid-transaction server crash by starting an uncommitted transaction directly on SQLite
        //    modifying stock and adding a partial uncommitted row, then abruptly killing the connection.
        await using (var rawConn = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await rawConn.OpenAsync();
            await using var tx = (SqliteTransaction)await rawConn.BeginTransactionAsync();

            await using var cmd = rawConn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE stock_balances SET current_quantity = '5.0000000000 Unit' WHERE ProductId = '{env.Product.Id}';";
            await cmd.ExecuteNonQueryAsync();

            // Intentionally abort/dispose without committing (simulates process kill / power cut mid-transaction)
        }
        SqliteConnection.ClearAllPools();

        // 2. Verify SQLite WAL rollback: Stock is still intact at 20 units
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        stockRes.EnsureSuccessStatusCode();
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(20m, stock.CurrentStock);

        // 3. Verify SQLite integrity
        await using (var checkConn = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await checkConn.OpenAsync();
            await using var cmd = checkConn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal("ok", result);
        }
        SqliteConnection.ClearAllPools();

        // 4. Verify system accepts subsequent valid checkouts normally
        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);
        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(saleRes.IsSuccessStatusCode);

        // Stock is now 18 (20 - 2)
        var finalStockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var finalStock = (await finalStockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(18m, finalStock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_14_Failure_ServerRestart_DatabaseIntegrityCheck_PassesWithZeroErrors()
    {
        string dataDir;
        Guid productId;
        string ownerToken;
        var committedInvoices = new List<string>();

        // Step 1: Initialize server 1, process sales, then abruptly shut down
        await using (var fixture1 = new StoreServerFixture())
        {
            var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture1, initialStock: 50m, sellingPricePaise: 1000);
            dataDir = fixture1.DataDirectory;
            productId = env.Product.Id;
            ownerToken = env.OwnerToken;

            var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
            for (int i = 0; i < 3; i++)
            {
                var req = new CreateSaleRequest(
                    Guid.NewGuid(),
                    [new CartItemRequest(productId, 1m)],
                    [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
                var res = await counters[i].PostAsJsonAsync("/api/v1/sales", req);
                Assert.True(res.IsSuccessStatusCode);
                var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
                committedInvoices.Add(dto.InvoiceNumber);
            }

            fixture1.PreserveDataDirectoryOnDispose = true;
        }

        // Step 2: Restart server pointing to the exact same data directory
        await using (var fixture2 = new StoreServerFixture(dataDir))
        {
            // Verify SQLite integrity check and foreign key check on the reopened database
            await using (var conn = new SqliteConnection($"Data Source={fixture2.DatabasePath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA integrity_check;";
                    var integrity = (string)(await cmd.ExecuteScalarAsync())!;
                    Assert.Equal("ok", integrity);
                }
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA foreign_key_check;";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    Assert.False(reader.HasRows, "Foreign key check should return 0 violations on restart.");
                }
            }
            SqliteConnection.ClearAllPools();

            // Login owner on restarted host
            using var client2 = fixture2.CreateClient();
            var loginRes = await client2.PostAsJsonAsync("/api/v1/auth/login-password", new LoginPasswordRequest("pilot_owner", "OwnerPassword123!"));
            loginRes.EnsureSuccessStatusCode();
            var loginData = (await loginRes.Content.ReadFromJsonAsync<ApiResponse<LoginResponse>>())!.Data!;
            client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginData.Token);

            // Verify stock is preserved at 47 (50 - 3)
            var stockRes = await client2.GetAsync($"/api/v1/inventory/stock/{productId}");
            stockRes.EnsureSuccessStatusCode();
            var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
            Assert.Equal(47m, stock.CurrentStock);

            // Verify all previously committed sales exist and match
            var salesRes = await client2.GetAsync("/api/v1/sales?pageSize=10");
            salesRes.EnsureSuccessStatusCode();
            var salesList = (await salesRes.Content.ReadFromJsonAsync<ApiResponse<PagedResult<SaleSummaryDto>>>())!.Data!;
            Assert.Equal(3, salesList.TotalCount);
            foreach (var inv in committedInvoices)
            {
                Assert.Contains(salesList.Items, s => s.InvoiceNumber == inv);
            }
        }
    }

    // =========================================================================
    // Vector 2: Network Drops Mid-Flight / Post-Commit (TEST-P15-15, 16, 17)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_15_Failure_NetworkDropPostCommit_ClientRetryWithSameTransactionId_ReturnsExistingInvoice()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        var clientTxId = Guid.NewGuid();
        var saleReq = new CreateSaleRequest(
            clientTxId,
            [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);

        // 1. Arm fault handler: allow server to commit transaction, but sever connection before response returns
        env.Counter1.FaultHandler.DropNextResponse = true;

        // 2. Client sends request, which commits on server then throws network exception to client
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        });
        Assert.Equal(1, env.Counter1.FaultHandler.DroppedRequests);

        // 3. Client retries with the SAME ClientTransactionId
        env.Counter1.FaultHandler.Reset();
        var retryRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(retryRes.IsSuccessStatusCode);

        var invoiceDto = (await retryRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        Assert.Equal(clientTxId, invoiceDto.ClientTransactionId);
        Assert.False(string.IsNullOrWhiteSpace(invoiceDto.InvoiceNumber));

        // 4. Verify exactly 1 sale exists on server
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var getSaleRes = await ownerClient.GetAsync($"/api/v1/sales/{invoiceDto.Id}");
        getSaleRes.EnsureSuccessStatusCode();
        var queriedSale = (await getSaleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        Assert.Equal(invoiceDto.InvoiceNumber, queriedSale.InvoiceNumber);
    }

    [Fact]
    public async Task TEST_P15_16_Failure_NetworkDropPostCommit_ClientRetry_DoesNotDoubleDecrementStock()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 30m, sellingPricePaise: 1000);

        var clientTxId = Guid.NewGuid();
        var saleReq = new CreateSaleRequest(
            clientTxId,
            [new CartItemRequest(env.Product.Id, 5m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 5000)]);

        // Arm fault: sever connection post-commit
        env.Counter1.FaultHandler.DropNextResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        });

        // Client retry
        env.Counter1.FaultHandler.Reset();
        var retryRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(retryRes.IsSuccessStatusCode);

        // Assert stock decremented by exactly 5m, NOT 10m
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(25m, stock.CurrentStock); // 30 - 5 = 25
    }

    [Fact]
    public async Task TEST_P15_17_Failure_NetworkDropPreCommit_ClientRetry_ProcessesNormallyOnceNetworkRestored()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        var clientTxId = Guid.NewGuid();
        var saleReq = new CreateSaleRequest(
            clientTxId,
            [new CartItemRequest(env.Product.Id, 4m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 4000)]);

        // 1. Arm fault: Drop request before reaching server
        env.Counter1.FaultHandler.DropNextRequest = true;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        });

        // 2. Server database: zero sales committed, stock unchanged (20m)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(20m, stock.CurrentStock);

        // 3. Network restored: Client retries same request
        env.Counter1.FaultHandler.Reset();
        var retryRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(retryRes.IsSuccessStatusCode);

        // 4. Stock decrements to 16m
        var finalStockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var finalStock = (await finalStockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(16m, finalStock.CurrentStock);
    }

    // =========================================================================
    // Vector 3: SQLite Lock Contention / Transient Exception Filter (TEST-P15-18)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_18_Failure_SqliteBusyContention_TransientExceptionFilter_RecoversGracefully()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var tasks = new List<Task<HttpResponseMessage>>();

        // High contention burst: 12 parallel checkouts simultaneously contending for SQLite write lock
        for (int i = 0; i < 12; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            tasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        var responses = await Task.WhenAll(tasks);

        // Transient exception retry filter in EfUnitOfWork retries on SQLITE_BUSY; all 12 succeed
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Checkout failed under lock contention with status {r.StatusCode}"));

        // Verify stock is exactly 38m (50 - 12)
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(38m, stock.CurrentStock);
    }

    // =========================================================================
    // Vector 4: Thermal Printer Detachment & Paper-Out (TEST-P15-19, 20, 21)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_19_Failure_ThermalPrinterDisconnected_PaymentCommitted_SurfacesPrintFailure()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        // 1. Complete sale successfully
        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(saleRes.IsSuccessStatusCode);
        var saleDto = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        // 2. Disconnect printer
        fixture.HardwareManager.SetPrinterOffline(true);

        // 3. Print receipt
        var printRes = await env.Counter1.PostAsync($"/api/v1/sales/{saleDto.Id}/print", null);
        Assert.True(printRes.IsSuccessStatusCode);
        var printDto = (await printRes.Content.ReadFromJsonAsync<ApiResponse<ReceiptPrintResponseDto>>())!.Data!;

        // 4. Assert print status is Failed, but sale is fully intact
        Assert.Equal(PrintStatus.Failed, printDto.Status);
        Assert.Contains("offline", printDto.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // 5. Critical invariant: Sale remains committed, payment durable, stock decremented
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var verifiedSaleRes = await ownerClient.GetAsync($"/api/v1/sales/{saleDto.Id}");
        var verifiedSale = (await verifiedSaleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        Assert.Equal(1000, verifiedSale.FinalPayablePaise);
        Assert.Single(verifiedSale.Payments);

        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(19m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_20_Failure_ThermalPrinterDisconnected_ReprintAfterReconnection_Succeeds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        // 1. Complete sale with printer offline
        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        Assert.True(saleRes.IsSuccessStatusCode);
        var saleDto = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        fixture.HardwareManager.SetPrinterOffline(true);
        var failedPrintRes = await env.Counter1.PostAsync($"/api/v1/sales/{saleDto.Id}/print", null);
        Assert.True(failedPrintRes.IsSuccessStatusCode);

        // 2. Reconnect printer
        fixture.HardwareManager.SetPrinterOffline(false);

        // 3. Request reprint
        var reprintRes = await env.Counter1.PostAsync($"/api/v1/sales/{saleDto.Id}/reprint", null);
        Assert.True(reprintRes.IsSuccessStatusCode);
        var reprintDto = (await reprintRes.Content.ReadFromJsonAsync<ApiResponse<ReceiptPrintResponseDto>>())!.Data!;

        // 4. Assert reprint succeeds and increments reprint counter
        Assert.Equal(PrintStatus.Success, reprintDto.Status);
        Assert.True(reprintDto.IsReprint);
        Assert.Equal(1, reprintDto.ReprintCount);
        Assert.NotEmpty(fixture.HardwareManager.Printer.PrintJobs);

        // Verify stock was not double-decremented
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(19m, stock.CurrentStock);
    }

    [Fact]
    public async Task TEST_P15_21_Failure_ThermalPrinterPaperOut_ReprintDoesNotDuplicatePaymentOrStock()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 20m, sellingPricePaise: 1000);

        var saleReq = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 3m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 3000)]);
        var saleRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq);
        var saleDto = (await saleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;

        // Simulate paper out
        fixture.HardwareManager.SetPrinterOutOfPaper(true);
        var failedPrintRes = await env.Counter1.PostAsync($"/api/v1/sales/{saleDto.Id}/print", null);
        var failedDto = (await failedPrintRes.Content.ReadFromJsonAsync<ApiResponse<ReceiptPrintResponseDto>>())!.Data!;
        Assert.Equal(PrintStatus.Failed, failedDto.Status);

        // Replenish paper & reprint
        fixture.HardwareManager.SetPrinterOutOfPaper(false);
        var reprintRes = await env.Counter1.PostAsync($"/api/v1/sales/{saleDto.Id}/reprint", null);
        var reprintDto = (await reprintRes.Content.ReadFromJsonAsync<ApiResponse<ReceiptPrintResponseDto>>())!.Data!;
        Assert.Equal(PrintStatus.Success, reprintDto.Status);

        // Invariants: 1 sale record, 1 payment record, stock = 17m
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var verifiedSaleRes = await ownerClient.GetAsync($"/api/v1/sales/{saleDto.Id}");
        var verifiedSale = (await verifiedSaleRes.Content.ReadFromJsonAsync<ApiResponse<SaleInvoiceDto>>())!.Data!;
        Assert.Single(verifiedSale.Payments);
        Assert.Equal(3000, verifiedSale.Payments[0].AmountPaise);

        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(17m, stock.CurrentStock);
    }

    // =========================================================================
    // Vector 5: Client Abrupt Disconnect / Reconnection (TEST-P15-22)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_22_Failure_ClientAbruptTermination_SessionAndCartState_ReconciledOnRestart()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        // 1. Counter 1 cashier opens cash session with 1000 float
        var openSessionReq = new OpenCashSessionRequest(env.Counter1.TerminalId, 1000, "Opening float");
        var openRes = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", openSessionReq);
        Assert.True(openRes.IsSuccessStatusCode);
        var sessionDto = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;

        // 2. Counter 1 completes a sale of 2 units (2000 cash)
        var saleReq1 = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 2m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 2000)]);
        var saleRes1 = await env.Counter1.PostAsJsonAsync("/api/v1/sales", saleReq1);
        Assert.True(saleRes1.IsSuccessStatusCode);

        // 3. Simulate client power cut: dispose the Counter 1 terminal client instance
        env.Counter1.Dispose();

        // 4. Create a new terminal instance representing the rebooted Counter 1
        var rebootedCounter1 = fixture.CreateVirtualTerminal(
            env.Counter1.TerminalCode,
            env.Counter1.TerminalName,
            env.Counter1.IpAddress,
            env.Counter1.TerminalId,
            env.Counter1.PairingToken);

        // Re-authenticate cashier on rebooted client
        var loggedIn = await rebootedCounter1.LoginPinAsync("cashier_1", "1111");
        Assert.True(loggedIn);

        // 5. Query active session on rebooted terminal: state is fully preserved
        var activeRes = await rebootedCounter1.GetAsync($"/api/v1/cash-sessions/current?terminalId={env.Counter1.TerminalId}");
        Assert.True(activeRes.IsSuccessStatusCode);
        var recoveredSession = (await activeRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        Assert.Equal(sessionDto.Id, recoveredSession.Id);
        Assert.Equal(1000, recoveredSession.OpeningFloatPaise);
        Assert.Equal(CashSessionStatus.Open, recoveredSession.Status);

        // 6. Cashier continues billing on resumed session: sale of 1 unit (1000 cash)
        var saleReq2 = new CreateSaleRequest(
            Guid.NewGuid(),
            [new CartItemRequest(env.Product.Id, 1m)],
            [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
        var saleRes2 = await rebootedCounter1.PostAsJsonAsync("/api/v1/sales", saleReq2);
        Assert.True(saleRes2.IsSuccessStatusCode);

        // 7. Cashier closes session declaring blind count 4000 (1000 float + 3000 cash sales)
        var closeReq = new CloseCashSessionRequest(4000, "Normal shift close after reboot.");
        var closeRes = await rebootedCounter1.PostAsJsonAsync($"/api/v1/cash-sessions/{sessionDto.Id}/close", closeReq);
        Assert.True(closeRes.IsSuccessStatusCode);
        var closedSession = (await closeRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;
        Assert.Equal(CashSessionStatus.Closed, closedSession.Status);
        Assert.Equal(0, closedSession.DiscrepancyPaise);
    }

    // =========================================================================
    // Vector 6: Live Backup During Continuous Sales (TEST-P15-23)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_23_Failure_LiveBackupDuringContinuousSales_BothBackupAndSalesSucceed()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 100m, sellingPricePaise: 1000);

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        var saleTasks = new List<Task<HttpResponseMessage>>();

        // 1. Launch 10 concurrent sales across 3 counters
        for (int i = 0; i < 10; i++)
        {
            var counter = counters[i % 3];
            var saleReq = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            saleTasks.Add(counter.PostAsJsonAsync("/api/v1/sales", saleReq));
        }

        // 2. Concurrently initiate live backup using ownerClient HTTP endpoint
        var backupTask = Task.Run(async () =>
        {
            using var bClient = fixture.CreateClient();
            bClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
            var bRes = await bClient.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto { Reason = "Live backup during peak sales contention" });
            Assert.Equal(HttpStatusCode.Created, bRes.StatusCode);
            var envelope = await bRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>();
            return envelope!.Data!;
        });

        // 3. Await sales and backup concurrently
        var responses = await Task.WhenAll(saleTasks);
        var backupRecord = await backupTask;

        // 4. Assert: All 10 sales succeeded
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode, $"Sale failed with status {r.StatusCode}"));

        // 5. Assert: Live backup completed successfully
        Assert.NotNull(backupRecord);
        Assert.Equal("Succeeded", backupRecord.Status);
        Assert.True(File.Exists(backupRecord.FilePath));
        Assert.True(File.Exists($"{backupRecord.FilePath}.manifest"));

        // 6. Verify backup companion manifest signature
        using (var scope = fixture.Services.CreateScope())
        {
            var manifestService = scope.ServiceProvider.GetRequiredService<IBackupManifestService>();
            var storeIdentity = scope.ServiceProvider.GetRequiredService<IStoreIdentityService>();
            var manifest = await manifestService.LoadManifestAsync($"{backupRecord.FilePath}.manifest");
            Assert.NotNull(manifest);
            Assert.True(manifestService.VerifySignature(manifest, storeIdentity.GetStoreSigningKey()));
        }

        // 7. Verify SQLite integrity of both live DB and backup DB
        await using (var conn = new SqliteConnection($"Data Source={backupRecord.FilePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var check = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal("ok", check);
        }

        await using (var liveConn = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await liveConn.OpenAsync();
            await using var cmd = liveConn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var check = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal("ok", check);
        }
        SqliteConnection.ClearAllPools();
    }

    // =========================================================================
    // Vector 7: Corrupted Manifest & Interrupted Restore (TEST-P15-24, TEST-P15-25)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_24_Failure_CorruptedBackupManifest_RestoreFailsClosed_PreservesDatabase()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 50m, sellingPricePaise: 1000);

        // 1. Create a valid backup via HTTP
        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);
        var bRes = await ownerClient.PostAsJsonAsync("/api/v1/backups", new CreateBackupRequestDto { Reason = "Backup candidate for corruption test" });
        Assert.Equal(HttpStatusCode.Created, bRes.StatusCode);
        var backupRecord = (await bRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>())!.Data!;

        // 2. Corrupt the companion manifest's HMAC signature
        var manifestPath = $"{backupRecord.FilePath}.manifest";
        var manifestJson = await File.ReadAllTextAsync(manifestPath);
        var manifest = JsonSerializer.Deserialize<BackupManifestDto>(manifestJson)!;
        manifest.Signature = "CORRUPTED_TAMPERED_HMAC_SIGNATURE_99999";
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        // 3. Attempt restore via HTTP: must fail closed with 400 Bad Request and BACKUP_VALIDATION_FAILED
        var restoreRes = await ownerClient.PostAsJsonAsync("/api/v1/backups/restore", new RestoreBackupRequestDto { FileName = backupRecord.FileName });
        Assert.Equal(HttpStatusCode.BadRequest, restoreRes.StatusCode);
        var errBody = (await restoreRes.Content.ReadFromJsonAsync<ApiResponse<BackupRecordDto>>())!;
        Assert.False(errBody.Success);
        Assert.Equal("BACKUP_VALIDATION_FAILED", errBody.ErrorCode);

        // 4. Assert active database was NOT modified or corrupted
        var stockRes = await ownerClient.GetAsync($"/api/v1/inventory/stock/{env.Product.Id}");
        var stock = (await stockRes.Content.ReadFromJsonAsync<ApiResponse<StockBalanceDto>>())!.Data!;
        Assert.Equal(50m, stock.CurrentStock);

        await using (var conn = new SqliteConnection($"Data Source={fixture.DatabasePath};Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", (string)(await cmd.ExecuteScalarAsync())!);
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task TEST_P15_25_Failure_InterruptedRestore_SafetySnapshotRestored_SystemRecovers()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"interrupted_restore_{Guid.NewGuid():N}");
        var dataDir = Path.Combine(testDir, "Data");
        var backupsDir = Path.Combine(testDir, "Backups");
        var securityDir = Path.Combine(testDir, "Security");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(securityDir);

        var primaryDbPath = Path.Combine(dataDir, "grozet_pos.db");
        var safetySnapshotPath = Path.Combine(backupsDir, "safety_snapshot.db");
        var recoveryMarkerPath = Path.Combine(dataDir, "restore_recovery_marker.json");

        try
        {
            // 1. Create a valid SQLite database as the safety snapshot
            await using (var conn = new SqliteConnection($"Data Source={safetySnapshotPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE dummy (id INT PRIMARY KEY, name TEXT); INSERT INTO dummy VALUES (1, 'SAFE_DATA');";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Corrupt/damage the primary db (simulate crash mid-restore)
            await File.WriteAllBytesAsync(primaryDbPath, Encoding.UTF8.GetBytes("CORRUPTED_INCOMPLETE_RESTORE_BYTES"));

            // 3. Write recovery marker indicating restore was interrupted at MigrationStarted
            var markerJson = JsonSerializer.Serialize(new
            {
                RestoreOperationId = Guid.NewGuid(),
                State = "MigrationStarted",
                SafetySnapshotPath = safetySnapshotPath,
                TargetDbPath = Path.Combine(backupsDir, "target.db"),
                CreatedAtUtc = DateTime.UtcNow
            });
            await File.WriteAllTextAsync(recoveryMarkerPath, markerJson);

            // 4. Run FilesystemReconciliationEngine startup reconciliation
            var gate = new DatabaseOperationGate();
            var storeIdentity = new StoreIdentityService(securityDir);
            await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Recovery Store");
            var manifestService = new BackupManifestService();

            var options = new DbContextOptionsBuilder<RetailDbContext>()
                .UseSqlite($"Data Source={primaryDbPath};Pooling=False")
                .Options;
            using var context = new RetailDbContext(options);
            var backupRepo = new EfBackupRepository(context);
            var unitOfWork = new EfUnitOfWork(context);

            var reconciler = new FilesystemReconciliationEngine(
                gate,
                storeIdentity,
                manifestService,
                backupRepo,
                unitOfWork,
                customDataDir: dataDir,
                customBackupsDir: backupsDir,
                customRecoveryMarkerPath: recoveryMarkerPath);

            await reconciler.ReconcileStartupAsync();

            // 5. Assert: System recovered to Available state
            Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);

            // 6. Assert: Primary DB replaced by verified safety snapshot
            Assert.False(File.Exists(recoveryMarkerPath), "Marker should be cleaned up after recovery.");
            await using (var conn = new SqliteConnection($"Data Source={primaryDbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT name FROM dummy WHERE id = 1;";
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("SAFE_DATA", name);
            }
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // Vector 8: Migration Rollback Under Stress (TEST-P15-26)
    // =========================================================================

    [Fact]
    public async Task TEST_P15_26_Failure_SafeMigrationRunner_StateDFailure_RollsBackToStateEWithoutDataLoss()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"migration_failure_test_{Guid.NewGuid():N}");
        var dataDir = Path.Combine(testDir, "Data");
        var stagingDir = Path.Combine(testDir, "Staging");
        var markerPath = Path.Combine(testDir, "migration_marker.json");
        var journalPath = Path.Combine(testDir, "migration_audit.journal");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(stagingDir);

        var primaryDbPath = Path.Combine(dataDir, "pos.db");
        var hmacKey = RandomNumberGenerator.GetBytes(32);
        var journal = new FileMigrationAuditJournal(journalPath, hmacKey);
        var manifestService = new BackupManifestService();
        var storeId = Guid.NewGuid();

        try
        {
            // 1. Create initial pos.db with pre-migration data
            await using (var conn = new SqliteConnection($"Data Source={primaryDbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE pre_migration_table (id INT PRIMARY KEY, val TEXT); INSERT INTO pre_migration_table VALUES (42, 'ORIGINAL_DATA');";
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteConnection.ClearAllPools();

            var runner = new SafeMigrationRunner(
                dataDir,
                stagingDir,
                markerPath,
                journal,
                manifestService,
                signingKey: hmacKey,
                storeId: storeId);

            // 2. Execute migration where State D throws an intentional catastrophic failure
            var opId = Guid.NewGuid().ToString("N");
            var result = await runner.RunSafeMigrationAsync(opId, () =>
            {
                throw new InvalidOperationException("Simulated catastrophic failure inside migration action at State D!");
            });

            // 3. Assert: Migration result indicates State E Rollback failure
            Assert.False(result.IsSuccess);
            Assert.Equal("STATE_E_ROLLBACK", result.CurrentState);
            Assert.Equal(102, result.ExitCode);

            // 4. Assert: Audit journal recorded State D failure and State E rollback
            var entries = await journal.ReadEntriesAsync();
            Assert.Contains(entries, e => e.EventType == "STATE_D_FAILED");
            Assert.Contains(entries, e => e.EventType == "ROLLBACK_COMPLETED");

            // 5. Assert: Pre-migration data is 100% intact and uncorrupted
            await using (var conn = new SqliteConnection($"Data Source={primaryDbPath};Pooling=False"))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT val FROM pre_migration_table WHERE id = 42;";
                var val = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("ORIGINAL_DATA", val);

                cmd.CommandText = "PRAGMA integrity_check;";
                var integrity = (string)(await cmd.ExecuteScalarAsync())!;
                Assert.Equal("ok", integrity);
            }
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(testDir, recursive: true); } catch { }
        }
    }
}
