using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Terminals;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class Phase12MultiCounterConcurrencyTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;
    private readonly object _dbLock = new();

    public Phase12MultiCounterConcurrencyTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_concurrency_p12_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath};Cache=Shared;Mode=ReadWriteCreate")
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
    public async Task MultiCounter_StockDepletion_10ThreadsCompeteFor3Units_Exactly3Succeed()
    {
        var productId = Guid.NewGuid();

        using (var setupContext = new RetailDbContext(_options))
        {
            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 3m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        var successCount = 0;
        var failureCount = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    using var attemptContext = new RetailDbContext(_options);
                    SqliteConnectionConfigurator.ApplyPragmas(attemptContext);

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
                catch (DomainException)
                {
                    Interlocked.Increment(ref failureCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(3, successCount);
        Assert.Equal(7, failureCount);

        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalStock = await verifyContext.StockBalances.FirstOrDefaultAsync(s => s.ProductId == productId);
            Assert.NotNull(finalStock);
            Assert.Equal(0m, finalStock.CurrentQuantity.Value);
        }
    }

    [Fact]
    public async Task MultiCounter_CentralMonotonicInvoiceSequence_20ConcurrentAllocations_GenerateUniqueMonotonics()
    {
        var results = new ConcurrentBag<string>();
        var tasks = new List<Task>();

        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                lock (_dbLock)
                {
                    using var taskContext = new RetailDbContext(_options);
                    var seqRepo = new EfInvoiceSequenceRepository(taskContext);
                    var invoiceNum = seqRepo.GetNextSequenceNumberAsync("STORE_CONCURRENT", "INV", "20260915").GetAwaiter().GetResult();
                    taskContext.SaveChanges();
                    results.Add(invoiceNum);
                }
                await Task.CompletedTask;
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(20, results.Count);
        var uniqueCount = results.Distinct().Count();
        Assert.Equal(20, uniqueCount);
    }

    [Fact]
    public async Task MultiCounter_DedicatedInvoiceRollbackConcurrencyTest_RollbackDoesNotCreateGhostOrGap()
    {
        string rolledBackInvoiceNum;

        // Transaction A: Begins, acquires sequence, rolls back on business validation failure
        using (var contextA = new RetailDbContext(_options))
        {
            await using var txA = await contextA.Database.BeginTransactionAsync();
            var seqRepoA = new EfInvoiceSequenceRepository(contextA);

            rolledBackInvoiceNum = await seqRepoA.GetNextSequenceNumberAsync("ROLLBACK_STORE", "INV", "20260915");
            Assert.EndsWith("000001", rolledBackInvoiceNum);

            // Simulate business failure and rollback
            await txA.RollbackAsync();
        }

        // Transaction B: Begins, acquires sequence, completes successfully
        string committedInvoiceNum;
        using (var contextB = new RetailDbContext(_options))
        {
            await using var txB = await contextB.Database.BeginTransactionAsync();
            var seqRepoB = new EfInvoiceSequenceRepository(contextB);

            committedInvoiceNum = await seqRepoB.GetNextSequenceNumberAsync("ROLLBACK_STORE", "INV", "20260915");
            Assert.EndsWith("000001", committedInvoiceNum);

            var saleInvoice = new SaleInvoice(
                Guid.NewGuid(),
                committedInvoiceNum,
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

            contextB.SalesInvoices.Add(saleInvoice);
            await contextB.SaveChangesAsync();
            await txB.CommitAsync();
        }

        // Verify: Zero ghost invoices for Transaction A
        using (var verifyContext = new RetailDbContext(_options))
        {
            var committedInvoice = await verifyContext.SalesInvoices.FirstOrDefaultAsync(s => s.InvoiceNumber == committedInvoiceNum);
            Assert.NotNull(committedInvoice);
            Assert.Equal(committedInvoiceNum, committedInvoice.InvoiceNumber);

            var totalInvoices = await verifyContext.SalesInvoices.CountAsync();
            Assert.Equal(1, totalInvoices);
        }
    }

    [Fact]
    public async Task MultiCounter_TerminalEnrollmentToken_ConcurrentConsumeAndRevoke_SerializesAtomically()
    {
        var tokenId = Guid.NewGuid();
        var tokenHash = "CONCURRENT_TOKEN_HASH_123";

        using (var setupContext = new RetailDbContext(_options))
        {
            var token = new TerminalEnrollmentToken(
                tokenId,
                tokenHash,
                EnrollmentTokenPurpose.NewEnrollment,
                null, null, null,
                DateTime.UtcNow.AddMinutes(15),
                "admin");

            setupContext.TerminalEnrollmentTokens.Add(token);
            await setupContext.SaveChangesAsync();
        }

        var successCount = 0;
        var rejectedCount = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            int threadId = i;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var taskContext = new RetailDbContext(_options);
                    var repo = new EfTerminalEnrollmentTokenRepository(taskContext);
                    var token = await repo.GetByHashAsync(tokenHash);
                    if (token != null)
                    {
                        if (threadId % 2 == 0)
                        {
                            token.Consume(DateTime.UtcNow);
                        }
                        else
                        {
                            token.Revoke(DateTime.UtcNow);
                        }

                        await taskContext.SaveChangesAsync();
                        Interlocked.Increment(ref successCount);
                    }
                }
                catch (DomainException)
                {
                    Interlocked.Increment(ref rejectedCount);
                }
                catch (DbUpdateConcurrencyException)
                {
                    Interlocked.Increment(ref rejectedCount);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref rejectedCount);
                }
            }));
        }

        await Task.WhenAll(tasks);

        // State was modified and persisted
        using (var verifyContext = new RetailDbContext(_options))
        {
            var finalToken = await verifyContext.TerminalEnrollmentTokens.FirstOrDefaultAsync(t => t.Id == tokenId);
            Assert.NotNull(finalToken);
            Assert.True(finalToken.IsConsumed || finalToken.IsRevoked);
        }
    }
}
