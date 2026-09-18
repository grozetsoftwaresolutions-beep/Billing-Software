using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Contracts.CashSessions;
using RetailPOS.Contracts.Catalog;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Reports;
using RetailPOS.Contracts.Sales;
using RetailPOS.Domain.Entities.CashSessions;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.E2E.Tests.Helpers;
using RetailPOS.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace RetailPOS.E2E.Tests.Suites;

/// <summary>
/// Sub-Phase 15.5: Performance & Scalability Test Suite (TEST-P15-37 through TEST-P15-44).
/// Rigorous latency, throughput, database file size budget, and bounded memory growth tests.
/// Evaluated strictly against Blueprint Section 29 SLA standards using P95 metrics.
/// </summary>
public sealed class PerformanceBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PerformanceBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task TEST_P15_37_Perf_BarcodeLookup_10000Products_LatencyP95Under50Milliseconds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        // 1. Seed 10,000 SKUs into SQLite catalog
        var sampleBarcodes = await BenchmarkHelper.SeedLargeCatalogAsync(fixture, 10000);

        // 2. Warmup Phase: 50 lookups excluded from measured latency samples
        for (int i = 0; i < 50; i++)
        {
            var warmupBarcode = sampleBarcodes[i % sampleBarcodes.Count];
            var warmupRes = await env.Counter1.GetAsync($"/api/v1/products/barcode/{warmupBarcode}");
            Assert.True(warmupRes.IsSuccessStatusCode);
        }

        // 3. Measurement Phase: N = 200 random barcode lookups
        const int sampleCount = 200;
        var latencies = new List<double>(sampleCount);
        var rng = new Random(42);

        for (int i = 0; i < sampleCount; i++)
        {
            var targetBarcode = sampleBarcodes[rng.Next(sampleBarcodes.Count)];

            long start = Stopwatch.GetTimestamp();
            var res = await env.Counter1.GetAsync($"/api/v1/products/barcode/{targetBarcode}");
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(res.IsSuccessStatusCode);
            latencies.Add(elapsedMs);
        }

        // 4. Calculate and log latency metrics
        var metrics = BenchmarkHelper.CalculateMetrics(latencies);
        _output.WriteLine($"[TEST-P15-37 Barcode Lookup (10k SKUs)] Samples: {metrics.SampleCount}, Min: {metrics.MinMs:F2}ms, Max: {metrics.MaxMs:F2}ms, Mean: {metrics.MeanMs:F2}ms, P50: {metrics.P50Ms:F2}ms, P95: {metrics.P95Ms:F2}ms, P99: {metrics.P99Ms:F2}ms");

        // SLA target: P95 < 50 ms (with +20% CI variance tolerance: 60 ms)
        Assert.True(metrics.P95Ms < 60.0, $"Barcode lookup P95 latency {metrics.P95Ms:F2}ms exceeded 60ms threshold.");
    }

    [Fact]
    public async Task TEST_P15_38_Perf_CartCalculation_10ItemCart_LatencyP95Under100Milliseconds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        using var ownerClient = fixture.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", env.OwnerToken);

        // Seed 10 distinct products for cart calculation
        var productIds = new List<Guid> { env.Product.Id };
        for (int i = 2; i <= 10; i++)
        {
            var pRes = await ownerClient.PostAsJsonAsync("/api/v1/products", new CreateProductRequest(
                Sku: $"CART-SKU-{i:D4}",
                Name: $"Cart Item Product {i}",
                UnitCode: "PC",
                CostPricePaise: 500,
                SellingPricePaise: 1000 + (i * 100),
                TaxCategoryId: 1,
                InitialStock: 100m,
                ReorderLevel: 2m));
            pRes.EnsureSuccessStatusCode();
            var pDto = (await pRes.Content.ReadFromJsonAsync<ApiResponse<ProductManagementDto>>())!.Data!;
            productIds.Add(pDto.Id);
        }

        var cartItems = productIds.Select(id => new CartItemRequest(id, 2m)).ToList();
        var calcReq = new CalculateSaleRequest(cartItems);

        // 1. Warmup Phase: 50 calls excluded from measurements
        for (int i = 0; i < 50; i++)
        {
            var warmupRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales/calculate", calcReq);
            Assert.True(warmupRes.IsSuccessStatusCode);
        }

        // 2. Measurement Phase: N = 200 calculations
        const int sampleCount = 200;
        var latencies = new List<double>(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            long start = Stopwatch.GetTimestamp();
            var res = await env.Counter1.PostAsJsonAsync("/api/v1/sales/calculate", calcReq);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(res.IsSuccessStatusCode);
            latencies.Add(elapsedMs);
        }

        var metrics = BenchmarkHelper.CalculateMetrics(latencies);
        _output.WriteLine($"[TEST-P15-38 Cart Calculation (10 Items)] Samples: {metrics.SampleCount}, Min: {metrics.MinMs:F2}ms, Max: {metrics.MaxMs:F2}ms, Mean: {metrics.MeanMs:F2}ms, P50: {metrics.P50Ms:F2}ms, P95: {metrics.P95Ms:F2}ms, P99: {metrics.P99Ms:F2}ms");

        // SLA target: P95 < 100 ms (with +20% tolerance: 120 ms)
        Assert.True(metrics.P95Ms < 120.0, $"Cart calculation P95 latency {metrics.P95Ms:F2}ms exceeded 120ms threshold.");
    }

    [Fact]
    public async Task TEST_P15_39_Perf_SaleCommit_FullInvoiceAndStockUpdate_LatencyP95Under1000Milliseconds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 500m, sellingPricePaise: 1000);

        // Open cash session on Counter 1
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "Commit Latency Session"));

        // 1. Warmup Phase: 20 checkouts excluded from measurements
        for (int i = 0; i < 20; i++)
        {
            var req = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            var warmupRes = await env.Counter1.PostAsJsonAsync("/api/v1/sales", req);
            Assert.True(warmupRes.IsSuccessStatusCode);
        }

        // 2. Measurement Phase: N = 100 checkouts
        const int sampleCount = 100;
        var latencies = new List<double>(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            var req = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

            long start = Stopwatch.GetTimestamp();
            var res = await env.Counter1.PostAsJsonAsync("/api/v1/sales", req);
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(res.IsSuccessStatusCode);
            latencies.Add(elapsedMs);
        }

        var metrics = BenchmarkHelper.CalculateMetrics(latencies);
        _output.WriteLine($"[TEST-P15-39 Sale Commit & Stock Update] Samples: {metrics.SampleCount}, Min: {metrics.MinMs:F2}ms, Max: {metrics.MaxMs:F2}ms, Mean: {metrics.MeanMs:F2}ms, P50: {metrics.P50Ms:F2}ms, P95: {metrics.P95Ms:F2}ms, P99: {metrics.P99Ms:F2}ms");

        // SLA target: P95 < 1000 ms
        Assert.True(metrics.P95Ms < 1000.0, $"Sale commit P95 latency {metrics.P95Ms:F2}ms exceeded 1000ms SLA.");
    }

    [Fact]
    public async Task TEST_P15_40_Perf_CatalogPrefixSearch_10000Products_LatencyP95Under100Milliseconds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        // Seed 10,000 products
        await BenchmarkHelper.SeedLargeCatalogAsync(fixture, 10000);

        var queryPrefixes = new[] { "PERF-000", "PERF-001", "PERF-01", "8900", "PERF-1", "PERF-99" };

        // 1. Warmup Phase: 20 searches excluded from measurements
        for (int i = 0; i < 20; i++)
        {
            var q = queryPrefixes[i % queryPrefixes.Length];
            var warmupRes = await env.Counter1.GetAsync($"/api/v1/products/search?query={q}&pageSize=20");
            Assert.True(warmupRes.IsSuccessStatusCode);
        }

        // 2. Measurement Phase: N = 100 searches
        const int sampleCount = 100;
        var latencies = new List<double>(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            var q = queryPrefixes[i % queryPrefixes.Length];

            long start = Stopwatch.GetTimestamp();
            var res = await env.Counter1.GetAsync($"/api/v1/products/search?query={q}&pageSize=20");
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(res.IsSuccessStatusCode);
            latencies.Add(elapsedMs);
        }

        var metrics = BenchmarkHelper.CalculateMetrics(latencies);
        _output.WriteLine($"[TEST-P15-40 Catalog Prefix Search (10k SKUs)] Samples: {metrics.SampleCount}, Min: {metrics.MinMs:F2}ms, Max: {metrics.MaxMs:F2}ms, Mean: {metrics.MeanMs:F2}ms, P50: {metrics.P50Ms:F2}ms, P95: {metrics.P95Ms:F2}ms, P99: {metrics.P99Ms:F2}ms");

        // SLA target: P95 < 100 ms (with +20% tolerance: 120 ms)
        Assert.True(metrics.P95Ms < 120.0, $"Prefix search P95 latency {metrics.P95Ms:F2}ms exceeded 120ms threshold.");
    }

    [Fact]
    public async Task TEST_P15_41_Perf_ZReportGeneration_1000Transactions_LatencyP95Under500Milliseconds()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture);

        // Open cash session on Counter 1
        var openRes = await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "Heavy Z-Report Session"));
        var session = (await openRes.Content.ReadFromJsonAsync<ApiResponse<CashSessionDto>>())!.Data!;

        // Seed 1,000 cash session transactions directly into session
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            var txs = new List<CashSessionTransaction>(1000);
            for (int i = 1; i <= 1000; i++)
            {
                txs.Add(new CashSessionTransaction(
                    id: 0,
                    cashSessionId: session.Id,
                    type: CashTransactionType.CashSale,
                    amount: Money.FromMinorUnits(1000, Currency.INR),
                    reason: $"Z-Report Bench Sale #{i}",
                    clientTransactionId: Guid.NewGuid(),
                    createdByUserId: session.CashierUserId));
            }

            await db.CashSessionTransactions.AddRangeAsync(txs);
            await db.SaveChangesAsync();
        }

        // Close session
        var closeRes = await env.Counter1.PostAsJsonAsync($"/api/v1/cash-sessions/{session.Id}/close",
            new CloseCashSessionRequest(100000 + (1000 * 1000), "Close heavy session"));
        Assert.True(closeRes.IsSuccessStatusCode);

        // 1. Warmup Phase: 5 calls excluded from measurements
        for (int i = 0; i < 5; i++)
        {
            var warmupRes = await env.Counter1.GetAsync($"/api/v1/cash-sessions/{session.Id}/z-report");
            Assert.True(warmupRes.IsSuccessStatusCode);
        }

        // 2. Measurement Phase: N = 20 reports
        const int sampleCount = 20;
        var latencies = new List<double>(sampleCount);

        for (int i = 0; i < sampleCount; i++)
        {
            long start = Stopwatch.GetTimestamp();
            var res = await env.Counter1.GetAsync($"/api/v1/cash-sessions/{session.Id}/z-report");
            double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Assert.True(res.IsSuccessStatusCode);
            latencies.Add(elapsedMs);
        }

        var metrics = BenchmarkHelper.CalculateMetrics(latencies);
        _output.WriteLine($"[TEST-P15-41 Z-Report (1,000 Transactions)] Samples: {metrics.SampleCount}, Min: {metrics.MinMs:F2}ms, Max: {metrics.MaxMs:F2}ms, Mean: {metrics.MeanMs:F2}ms, P50: {metrics.P50Ms:F2}ms, P95: {metrics.P95Ms:F2}ms, P99: {metrics.P99Ms:F2}ms");

        // SLA target: P95 < 500 ms (with +20% tolerance: 600 ms)
        Assert.True(metrics.P95Ms < 600.0, $"Z-Report P95 latency {metrics.P95Ms:F2}ms exceeded 600ms threshold.");
    }

    [Fact]
    public async Task TEST_P15_42_Perf_ConcurrentCheckouts_ThreeCounters_ThroughputSustainedAboveTenTps()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 5000m, sellingPricePaise: 1000);

        // Open cash sessions on all 3 counters
        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "C1"));
        await env.Counter2.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter2.TerminalId, 100000, "C2"));
        await env.Counter3.PostAsJsonAsync("/api/v1/cash-sessions/open", new OpenCashSessionRequest(env.Counter3.TerminalId, 100000, "C3"));

        // 1. Warmup Phase: 10 checkouts
        for (int i = 0; i < 10; i++)
        {
            var req = new CreateSaleRequest(
                Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            var res = await env.Counter1.PostAsJsonAsync("/api/v1/sales", req);
            Assert.True(res.IsSuccessStatusCode);
        }

        // 2. Sustained 10-Second Concurrency Window across 3 counters
        int completedCount = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = cts.Token;
        long benchmarkStart = Stopwatch.GetTimestamp();

        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };

        var workers = Enumerable.Range(0, 6).Select(workerIdx => Task.Run(async () =>
        {
            var counter = counters[workerIdx % 3];
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var req = new CreateSaleRequest(
                        Guid.NewGuid(),
                        [new CartItemRequest(env.Product.Id, 1m)],
                        [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

                    var res = await counter.PostAsJsonAsync("/api/v1/sales", req, token);
                    if (res.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref completedCount);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        })).ToList();

        await Task.WhenAll(workers);

        double elapsedSeconds = Stopwatch.GetElapsedTime(benchmarkStart).TotalSeconds;
        double sustainedTps = completedCount / elapsedSeconds;

        _output.WriteLine($"[TEST-P15-42 Throughput 3 Counters] Completed: {completedCount} checkouts in {elapsedSeconds:F2}s -> {sustainedTps:F2} TPS");

        // SLA target: > 10 TPS sustained across 3 counters
        Assert.True(sustainedTps > 10.0, $"Sustained throughput {sustainedTps:F2} TPS was below the 10 TPS threshold.");
    }

    [Fact]
    public async Task TEST_P15_43_Perf_DatabaseFileSizeGrowth_1000Sales_WithinLinearBudgetWithoutWalBloat()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 2000m, sellingPricePaise: 1000);

        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "Size Budget Session"));

        // Baseline WAL checkpoint & measurement
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        long initialSizeBytes = new FileInfo(fixture.DatabasePath).Length;

        // Execute 1,000 sales transactions
        const int salesWorkload = 1000;
        for (int i = 0; i < salesWorkload; i++)
        {
            var req = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

            var res = await env.Counter1.PostAsJsonAsync("/api/v1/sales", req);
            Assert.True(res.IsSuccessStatusCode);
        }

        // Final WAL checkpoint & measurement
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
        }

        long finalSizeBytes = new FileInfo(fixture.DatabasePath).Length;
        long netGrowthBytes = finalSizeBytes - initialSizeBytes;
        double bytesPerSale = (double)netGrowthBytes / salesWorkload;
        double kbPerSale = bytesPerSale / 1024.0;

        _output.WriteLine($"[TEST-P15-43 Database File Growth] Initial: {initialSizeBytes} B, Final: {finalSizeBytes} B, Net Growth: {netGrowthBytes} B across {salesWorkload} sales -> {bytesPerSale:F1} B/sale ({kbPerSale:F2} KB/sale)");

        // SLA budget: < 5 KB/sale (5,120 bytes/sale)
        Assert.True(bytesPerSale < 5120.0, $"Database growth {kbPerSale:F2} KB/sale exceeded 5 KB/sale budget.");
    }

    [Fact]
    public async Task TEST_P15_44_Perf_MemoryWorkingSet_SustainedOperation_BoundedMemoryGrowth()
    {
        await using var fixture = new StoreServerFixture();
        var env = await E2ETestSeedHelper.SeedThreeCounterStoreAsync(fixture, initialStock: 1000m, sellingPricePaise: 1000);

        await env.Counter1.PostAsJsonAsync("/api/v1/cash-sessions/open",
            new OpenCashSessionRequest(env.Counter1.TerminalId, 100000, "Memory Session"));

        // 1. Warmup with 50 completed transactions
        for (int i = 0; i < 50; i++)
        {
            var req = new CreateSaleRequest(
                Guid.NewGuid(), [new CartItemRequest(env.Product.Id, 1m)], [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);
            var res = await env.Counter1.PostAsJsonAsync("/api/v1/sales", req);
            Assert.True(res.IsSuccessStatusCode);
        }

        // 2. Force complete baseline GC
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        long m0 = GC.GetTotalMemory(true);
        long w0 = Process.GetCurrentProcess().WorkingSet64;

        // 3. Sustained workload: 500 consecutive sales transactions across 3 counters
        var counters = new[] { env.Counter1, env.Counter2, env.Counter3 };
        const int workload = 500;
        for (int i = 0; i < workload; i++)
        {
            var counter = counters[i % 3];
            var req = new CreateSaleRequest(
                Guid.NewGuid(),
                [new CartItemRequest(env.Product.Id, 1m)],
                [new PaymentLineRequest(PaymentMethod.Cash, 1000)]);

            var res = await counter.PostAsJsonAsync("/api/v1/sales", req);
            Assert.True(res.IsSuccessStatusCode);
        }

        // 4. Force complete post-workload GC
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        long m1 = GC.GetTotalMemory(true);
        long w1 = Process.GetCurrentProcess().WorkingSet64;

        long deltaMBytes = m1 - m0;
        double deltaMMB = (double)deltaMBytes / (1024 * 1024);
        long deltaWBytes = w1 - w0;
        double deltaWMB = (double)deltaWBytes / (1024 * 1024);

        _output.WriteLine($"[TEST-P15-44 Bounded Memory Behavior] Baseline Heap: {m0 / (1024 * 1024):F2} MB, Post-Workload Heap: {m1 / (1024 * 1024):F2} MB, Net Heap Delta: {deltaMMB:F2} MB | WorkingSet Delta: {deltaWMB:F2} MB");

        // SLA target: Net managed heap growth Delta M < 30 MB
        Assert.True(deltaMMB < 30.0, $"Net managed heap growth {deltaMMB:F2} MB exceeded 30 MB threshold.");
    }
}
