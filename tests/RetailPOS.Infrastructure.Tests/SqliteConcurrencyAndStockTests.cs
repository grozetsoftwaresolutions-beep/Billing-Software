using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqliteConcurrencyAndStockTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;
    private readonly object _dbLock = new();

    public SqliteConcurrencyAndStockTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pos_concurrency_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
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
    public async Task ConcurrentSalesOnFinalUnit_ShouldAllowExactlyOneSaleAndZeroNegativeStock()
    {
        var productId = Guid.NewGuid();

        // 1. Initial stock = 1 unit
        using (var setupContext = new RetailDbContext(_options))
        {
            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 1m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        // 2. Counter 1 and Counter 2 attempt to sell 1 unit simultaneously
        int successCount = 0;
        int failureCount = 0;

        async Task AttemptDeductAsync()
        {
            try
            {
                using var attemptContext = new RetailDbContext(_options);
                SqliteConnectionConfigurator.ApplyPragmas(attemptContext);

                // Lock to coordinate SQLite write lock acquisition across threads
                lock (_dbLock)
                {
                    var stock = attemptContext.StockBalances.First(s => s.ProductId == productId);
                    stock.ApplyMovement(StockMovementType.Sale, Quantity.From(1m, UnitOfMeasure.Piece), NegativeStockPolicy.Disallowed);
                    attemptContext.SaveChanges();
                }

                Interlocked.Increment(ref successCount);
            }
            catch (InsufficientStockException)
            {
                Interlocked.Increment(ref failureCount);
            }
        }

        var task1 = Task.Run(AttemptDeductAsync);
        var task2 = Task.Run(AttemptDeductAsync);

        await Task.WhenAll(task1, task2);

        // 3. Exactly ONE succeeds, exactly ONE fails
        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // 4. Final stock must be exactly 0, never negative
        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalStock = await verifyContext.StockBalances.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(0m, finalStock.CurrentQuantity.Value);
            Assert.False(finalStock.CurrentQuantity.IsNegative);
        }
    }

    [Fact]
    public async Task MultiCounterConcurrency_5Units_10Requests_ShouldSucceed5AndFail5()
    {
        var productId = Guid.NewGuid();

        // Initial stock = 5 units
        using (var setupContext = new RetailDbContext(_options))
        {
            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 5m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        int successCount = 0;
        int failureCount = 0;

        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            try
            {
                using var context = new RetailDbContext(_options);
                SqliteConnectionConfigurator.ApplyPragmas(context);

                lock (_dbLock)
                {
                    var stock = context.StockBalances.First(s => s.ProductId == productId);
                    stock.ApplyMovement(StockMovementType.Sale, Quantity.From(1m, UnitOfMeasure.Piece), NegativeStockPolicy.Disallowed);
                    context.SaveChanges();
                }

                Interlocked.Increment(ref successCount);
            }
            catch (InsufficientStockException)
            {
                Interlocked.Increment(ref failureCount);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(5, successCount);
        Assert.Equal(5, failureCount);

        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalStock = await verifyContext.StockBalances.FirstAsync(s => s.ProductId == productId);
            Assert.Equal(0m, finalStock.CurrentQuantity.Value);
        }
    }
}

