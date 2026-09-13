using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Inventory;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqliteRetryAtomicityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqliteRetryAtomicityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), string.Format(CultureInfo.InvariantCulture, "test_retry_atomicity_{0:N}.db", Guid.NewGuid()));
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite(string.Format(CultureInfo.InvariantCulture, "Data Source={0}", _dbPath))
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
    public async Task TransientSqliteBusyOnSaveChanges_WhenRetried_AppliesStockAdjustmentExactlyOnce()
    {
        var productId = Guid.NewGuid();

        // 1. Seed product and initial stock balance of 50
        using (var setupContext = new RetailDbContext(_options))
        {
            var product = new Product(
                productId,
                "ATOM-SKU-1",
                "Retry Atomicity Product 1",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(1000),
                Money.FromMinorUnits(1500),
                taxCategoryId: 1);
            setupContext.Products.Add(product);

            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 50m, reorderLevel: 20m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        // 2. Setup service with fault injection on SaveChangesAsync (first call throws SQLITE_BUSY)
        using var testContext = new RetailDbContext(_options);
        var productRepo = new EfProductRepository(testContext);
        var inventoryRepo = new EfInventoryRepository(testContext);
        var auditRepo = new EfAuditLogRepository(testContext);
        var innerUow = new EfUnitOfWork(testContext);
        var faultUow = new FaultInjectingUnitOfWork(innerUow)
        {
            FailSaveChangesCount = 1 // Fail first attempt only
        };

        var service = new InventoryService(productRepo, inventoryRepo, auditRepo, faultUow);

        // 3. Perform manual adjustment of +10
        var request = new StockAdjustmentRequest(
            ProductId: productId,
            Quantity: 10m,
            AdjustmentType: ManualStockAdjustmentType.Increase,
            Reason: "Physical recount verification");

        var response = await service.AdjustStockAsync(
            request,
            userId: Guid.NewGuid(),
            terminalId: Guid.NewGuid(),
            executingUser: "manager_admin");

        // 4. Assert service returned success
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(60m, response.Data.ResultingQuantity);
        Assert.Equal(10m, response.Data.QuantityChanged);
        Assert.Equal(2, faultUow.SaveChangesAttempts); // Proves retry occurred

        // 5. Query fresh context against SQLite file to verify atomic persistence
        using var verifyContext = new RetailDbContext(_options);

        // Verify exactly one StockMovement
        var movements = await verifyContext.StockMovements
            .Where(m => m.ProductId == productId)
            .ToListAsync();
        Assert.Single(movements);
        var movement = movements[0];
        Assert.Equal(10m, movement.QuantityChanged.Value);
        Assert.Equal(60m, movement.ResultingQuantity.Value);
        Assert.Equal(StockMovementType.AdjustmentIncrease, movement.MovementType);

        // Verify exactly one STOCK_ADJUSTED AuditLog
        var auditLogs = await verifyContext.AuditLogs
            .Where(a => a.Action == "STOCK_ADJUSTED")
            .ToListAsync();
        Assert.Single(auditLogs);
        Assert.Contains("60", auditLogs[0].Details, StringComparison.Ordinal);

        // Verify exactly one stock quantity change
        var persistedBalance = await verifyContext.StockBalances
            .SingleAsync(b => b.ProductId == productId);
        Assert.Equal(60m, persistedBalance.CurrentQuantity.Value);
        Assert.False(persistedBalance.IsLowStock);
    }

    [Fact]
    public async Task TransientSqliteBusyOnCommit_WhenRetried_AppliesStockAdjustmentExactlyOnce()
    {
        var productId = Guid.NewGuid();

        // 1. Seed product and initial stock balance of 50
        using (var setupContext = new RetailDbContext(_options))
        {
            var product = new Product(
                productId,
                "ATOM-SKU-2",
                "Retry Atomicity Product 2",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(1000),
                Money.FromMinorUnits(1500),
                taxCategoryId: 1);
            setupContext.Products.Add(product);

            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 50m, reorderLevel: 20m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        // 2. Setup service with fault injection on CommitAsync (first call throws SQLITE_BUSY)
        using var testContext = new RetailDbContext(_options);
        var productRepo = new EfProductRepository(testContext);
        var inventoryRepo = new EfInventoryRepository(testContext);
        var auditRepo = new EfAuditLogRepository(testContext);
        var innerUow = new EfUnitOfWork(testContext);
        var faultUow = new FaultInjectingUnitOfWork(innerUow)
        {
            FailCommitCount = 1 // Fail first commit only
        };

        var service = new InventoryService(productRepo, inventoryRepo, auditRepo, faultUow);

        // 3. Perform manual adjustment of -15 (Decrease from 50 to 35)
        var request = new StockAdjustmentRequest(
            ProductId: productId,
            Quantity: 15m,
            AdjustmentType: ManualStockAdjustmentType.Decrease,
            Reason: "Damaged packaging write-off");

        var response = await service.AdjustStockAsync(
            request,
            userId: Guid.NewGuid(),
            terminalId: Guid.NewGuid(),
            executingUser: "manager_admin");

        // 4. Assert service returned success
        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal(35m, response.Data.ResultingQuantity);
        Assert.Equal(15m, response.Data.QuantityChanged);
        Assert.Equal(2, faultUow.CommitAttempts); // Proves commit retry occurred

        // 5. Query fresh context against SQLite file to verify atomic persistence
        using var verifyContext = new RetailDbContext(_options);

        // Verify exactly one StockMovement
        var movements = await verifyContext.StockMovements
            .Where(m => m.ProductId == productId)
            .ToListAsync();
        Assert.Single(movements);
        var movement = movements[0];
        Assert.Equal(15m, movement.QuantityChanged.Value);
        Assert.Equal(35m, movement.ResultingQuantity.Value);
        Assert.Equal(StockMovementType.AdjustmentDecrease, movement.MovementType);

        // Verify exactly one STOCK_ADJUSTED AuditLog
        var auditLogs = await verifyContext.AuditLogs
            .Where(a => a.Action == "STOCK_ADJUSTED")
            .ToListAsync();
        Assert.Single(auditLogs);
        Assert.Contains("35", auditLogs[0].Details, StringComparison.Ordinal);

        // Verify exactly one stock quantity change
        var persistedBalance = await verifyContext.StockBalances
            .SingleAsync(b => b.ProductId == productId);
        Assert.Equal(35m, persistedBalance.CurrentQuantity.Value);
        Assert.False(persistedBalance.IsLowStock);
    }

    [Fact]
    public async Task PersistentSqliteBusy_ExceedingMaxRetries_RollsBackCompletelyWithoutDuplicateOrPartialEffects()
    {
        var productId = Guid.NewGuid();

        // 1. Seed product and initial stock balance of 50
        using (var setupContext = new RetailDbContext(_options))
        {
            var product = new Product(
                productId,
                "ATOM-SKU-3",
                "Retry Atomicity Product 3",
                UnitOfMeasure.Piece,
                Money.FromMinorUnits(1000),
                Money.FromMinorUnits(1500),
                taxCategoryId: 1);
            setupContext.Products.Add(product);

            var stock = new StockBalance(productId, UnitOfMeasure.Piece, initialQuantity: 50m, reorderLevel: 20m);
            setupContext.StockBalances.Add(stock);
            await setupContext.SaveChangesAsync();
        }

        // 2. Setup service with persistent failure (exceeds max 3 retries)
        using var testContext = new RetailDbContext(_options);
        var productRepo = new EfProductRepository(testContext);
        var inventoryRepo = new EfInventoryRepository(testContext);
        var auditRepo = new EfAuditLogRepository(testContext);
        var innerUow = new EfUnitOfWork(testContext);
        var faultUow = new FaultInjectingUnitOfWork(innerUow)
        {
            FailSaveChangesCount = 10 // Always fail
        };

        var service = new InventoryService(productRepo, inventoryRepo, auditRepo, faultUow);

        var request = new StockAdjustmentRequest(
            ProductId: productId,
            Quantity: 10m,
            AdjustmentType: ManualStockAdjustmentType.Increase,
            Reason: "Adjustment under permanent lock");

        // 3. Expect DomainException DATABASE_BUSY
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.AdjustStockAsync(
                request,
                userId: Guid.NewGuid(),
                terminalId: Guid.NewGuid(),
                executingUser: "manager_admin"));

        Assert.Equal("DATABASE_BUSY", ex.ErrorCode);
        Assert.Equal(3, faultUow.SaveChangesAttempts); // 3 attempts made

        // 4. Verify SQLite database contains NO movements, NO audit logs, and stock balance remains untouched
        using var verifyContext = new RetailDbContext(_options);

        var movements = await verifyContext.StockMovements
            .Where(m => m.ProductId == productId)
            .ToListAsync();
        Assert.Empty(movements);

        var auditLogs = await verifyContext.AuditLogs
            .Where(a => a.Action == "STOCK_ADJUSTED")
            .ToListAsync();
        Assert.Empty(auditLogs);

        var persistedBalance = await verifyContext.StockBalances
            .SingleAsync(b => b.ProductId == productId);
        Assert.Equal(50m, persistedBalance.CurrentQuantity.Value);
    }

    private sealed class FaultInjectingUnitOfWork : IUnitOfWork
    {
        private readonly EfUnitOfWork _inner;

        public int FailSaveChangesCount { get; set; }
        public int SaveChangesAttempts { get; private set; }

        public int FailCommitCount { get; set; }
        public int CommitAttempts { get; private set; }

        public FaultInjectingUnitOfWork(EfUnitOfWork inner)
        {
            _inner = inner;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveChangesAttempts++;
            if (SaveChangesAttempts <= FailSaveChangesCount)
            {
                // SQLite error code 5 is SQLITE_BUSY
                throw new SqliteException("database is locked (injected SQLITE_BUSY)", 5);
            }
            return await _inner.SaveChangesAsync(cancellationToken);
        }

        public async Task<IDbTransactionScope> BeginSerializableTransactionAsync(CancellationToken cancellationToken = default)
        {
            var innerScope = await _inner.BeginSerializableTransactionAsync(cancellationToken);
            return new FaultInjectingTransactionScope(innerScope, this);
        }

        public bool IsTransient(Exception ex) => _inner.IsTransient(ex);

        public void ResetChangeTracker() => _inner.ResetChangeTracker();

        private sealed class FaultInjectingTransactionScope : IDbTransactionScope
        {
            private readonly IDbTransactionScope _innerScope;
            private readonly FaultInjectingUnitOfWork _parent;

            public FaultInjectingTransactionScope(IDbTransactionScope innerScope, FaultInjectingUnitOfWork parent)
            {
                _innerScope = innerScope;
                _parent = parent;
            }

            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                _parent.CommitAttempts++;
                if (_parent.CommitAttempts <= _parent.FailCommitCount)
                {
                    throw new SqliteException("database is locked on commit (injected SQLITE_BUSY)", 5);
                }
                await _innerScope.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                _innerScope.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() =>
                _innerScope.DisposeAsync();
        }
    }
}
