using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.E2E.Tests.Fixtures;
using RetailPOS.Infrastructure.Persistence;

namespace RetailPOS.E2E.Tests.Helpers;

public sealed record LatencyMetrics(
    int SampleCount,
    double MinMs,
    double MaxMs,
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms);

public static class BenchmarkHelper
{
    public static async Task<List<string>> SeedLargeCatalogAsync(
        StoreServerFixture fixture,
        int count = 10000,
        CancellationToken cancellationToken = default)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RetailDbContext>();
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var sampleBarcodes = new List<string>(count);
        var productBatch = new List<Product>(1000);
        var stockBatch = new List<StockBalance>(1000);

        for (int i = 1; i <= count; i++)
        {
            var id = Guid.NewGuid();
            var sku = $"PERF-{i:D6}";
            var barcodeStr = $"890{i:D9}";
            sampleBarcodes.Add(barcodeStr);

            var product = new Product(
                id: id,
                sku: sku,
                name: $"Performance Test Product {i:D6}",
                unit: UnitOfMeasure.Piece,
                costPrice: Money.FromMinorUnits(500, Currency.INR),
                sellingPrice: Money.FromMinorUnits(1000, Currency.INR),
                taxCategoryId: 1);

            product.AddBarcode(new Barcode(barcodeStr), isPrimary: true);

            var stock = new StockBalance(id, UnitOfMeasure.Piece, initialQuantity: 100m, reorderLevel: 5m);

            productBatch.Add(product);
            stockBatch.Add(stock);

            if (productBatch.Count >= 1000)
            {
                await db.Products.AddRangeAsync(productBatch, cancellationToken);
                await db.StockBalances.AddRangeAsync(stockBatch, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                productBatch.Clear();
                stockBatch.Clear();
            }
        }

        if (productBatch.Count > 0)
        {
            await db.Products.AddRangeAsync(productBatch, cancellationToken);
            await db.StockBalances.AddRangeAsync(stockBatch, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        return sampleBarcodes;
    }

    public static LatencyMetrics CalculateMetrics(IReadOnlyList<double> latenciesMs)
    {
        if (latenciesMs.Count == 0)
            throw new ArgumentException("Latencies list cannot be empty.", nameof(latenciesMs));

        var sorted = latenciesMs.OrderBy(x => x).ToList();
        int count = sorted.Count;
        double min = sorted[0];
        double max = sorted[^1];
        double mean = sorted.Average();
        double p50 = GetPercentile(sorted, 0.50);
        double p95 = GetPercentile(sorted, 0.95);
        double p99 = GetPercentile(sorted, 0.99);

        return new LatencyMetrics(count, min, max, mean, p50, p95, p99);
    }

    private static double GetPercentile(List<double> sorted, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
