using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Inventory;
using RetailPOS.Domain.Entities.Purchases;
using RetailPOS.Domain.Entities.Sales;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SqlitePhase5MigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqlitePhase5MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retailpos_migration_test_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public async Task ExistingPhase2Database_UpgradesSuccessfullyToPhase5_PreservingAllDataAndEnforcingConstraints()
    {
        var prod1Id = Guid.NewGuid();
        var prod2Id = Guid.NewGuid();

        // 1. Create full initial database schema, seed initial Phase 2 records, then drop Phase 5 additions to simulate Phase 2 state
        using (var context = new RetailDbContext(_options))
        {
            await context.Database.EnsureCreatedAsync();

            // Populate Phase 2 data
            var store = new Store(1, "Phase 2 Legacy Store");
            var gst0 = new TaxCategory(1, "GST_0", "GST 0%", false, [new TaxComponent("Zero Tax", 0m)]);
            var p1 = new Product(prod1Id, "P2-ITEM-01", "Legacy Product 1", UnitOfMeasure.Piece, Money.FromMinorUnits(1000), Money.FromMinorUnits(1500), 1);
            var p2 = new Product(prod2Id, "P2-ITEM-02", "Legacy Product 2", UnitOfMeasure.Piece, Money.FromMinorUnits(2000), Money.FromMinorUnits(2500), 1);
            var b1 = new StockBalance(prod1Id, UnitOfMeasure.Piece, initialQuantity: 10m, reorderLevel: 5m);
            var b2 = new StockBalance(prod2Id, UnitOfMeasure.Piece, initialQuantity: 2m, reorderLevel: 5m);

            context.Stores.Add(store);
            context.TaxCategories.Add(gst0);
            context.Products.AddRange(p1, p2);
            context.StockBalances.AddRange(b1, b2);

            await context.SaveChangesAsync();
        }

        // 2. Simulate Phase 2 by removing the Phase 5 schema additions (is_low_stock and NOCASE unique indexes)
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                DROP INDEX IF EXISTS UK_categories_name;
                DROP INDEX IF EXISTS UK_brands_name;
                DROP INDEX IF EXISTS IX_stock_balances_is_low_stock;
                ALTER TABLE stock_balances DROP COLUMN is_low_stock;
            ";
            await cmd.ExecuteNonQueryAsync();

            // Verify is_low_stock is actually gone from Phase 2 simulation
            cmd.CommandText = "PRAGMA table_info(stock_balances);";
            var colNames = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    colNames.Add(reader.GetString(1));
                }
            }
            Assert.DoesNotContain("is_low_stock", colNames);
        }

        // 3. Run DatabaseInitializer (representing the server booting up with Phase 5 code against an existing Phase 2 database)
        using (var context = new RetailDbContext(_options))
        {
            await DatabaseInitializer.InitializeDatabaseAsync(context);
        }

        // 4. Verify that Phase 5 schema upgrades were successfully applied
        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();

            // Verify is_low_stock column exists in stock_balances
            cmd.CommandText = "PRAGMA table_info(stock_balances);";
            var colNames = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    colNames.Add(reader.GetString(1));
                }
            }
            Assert.Contains("is_low_stock", colNames);

            // Verify index IX_stock_balances_is_low_stock exists
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_stock_balances_is_low_stock';";
            var ixLowStock = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("IX_stock_balances_is_low_stock", ixLowStock);

            // Verify UK_categories_name exists
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='UK_categories_name';";
            var ukCat = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("UK_categories_name", ukCat);

            // Verify UK_brands_name exists
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='UK_brands_name';";
            var ukBrand = (string?)await cmd.ExecuteScalarAsync();
            Assert.Equal("UK_brands_name", ukBrand);

            // Verify backfilled is_low_stock values for existing Phase 2 products
            cmd.CommandText = $"SELECT current_quantity, is_low_stock FROM stock_balances WHERE ProductId = '{prod1Id}' COLLATE NOCASE;";
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt32(1)); // 10 > 5 -> is_low_stock = 0 (false)
            }

            cmd.CommandText = $"SELECT current_quantity, is_low_stock FROM stock_balances WHERE ProductId = '{prod2Id}' COLLATE NOCASE;";
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt32(1)); // 2 <= 5 -> is_low_stock = 1 (true)
            }

            // Verify NOCASE behavior on categories
            cmd.CommandText = "INSERT INTO categories (Name, IsActive, CreatedAtUtc, UpdatedAtUtc) VALUES ('Beverages', 1, '2026-01-01', '2026-01-01');";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "INSERT INTO categories (Name, IsActive, CreatedAtUtc, UpdatedAtUtc) VALUES ('beverages', 1, '2026-01-01', '2026-01-01');";
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());

            // Verify NOCASE behavior on brands
            cmd.CommandText = "INSERT INTO brands (Name, IsActive, CreatedAtUtc, UpdatedAtUtc) VALUES ('Britannia', 1, '2026-01-01', '2026-01-01');";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "INSERT INTO brands (Name, IsActive, CreatedAtUtc, UpdatedAtUtc) VALUES ('BRITANNIA', 1, '2026-01-01', '2026-01-01');";
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteNonQueryAsync());
        }

        // 5. Verify that all existing Phase 2 entities remain intact and can be loaded via EF Core
        using (var context = new RetailDbContext(_options))
        {
            var store = await context.Stores.FirstOrDefaultAsync();
            Assert.NotNull(store);
            Assert.Equal("Phase 2 Legacy Store", store.StoreName);

            var products = await context.Products.OrderBy(p => p.SKU).ToListAsync();
            Assert.Equal(2, products.Count);
            Assert.Equal("P2-ITEM-01", products[0].SKU);
            Assert.Equal("P2-ITEM-02", products[1].SKU);

            var balances = (await context.StockBalances.ToListAsync()).OrderBy(s => s.CurrentQuantity.Value).ToList();
            Assert.Equal(2, balances.Count);
            Assert.True(balances[0].IsLowStock); // 2 PC
            Assert.False(balances[1].IsLowStock); // 10 PC
        }
    }
}