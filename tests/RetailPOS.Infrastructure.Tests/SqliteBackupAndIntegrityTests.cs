using Microsoft.EntityFrameworkCore;
using RetailPOS.Domain.Entities.Catalog;
using RetailPOS.Domain.ValueObjects;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public class SqliteBackupAndIntegrityTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly DbContextOptions<RetailDbContext> _options;

    public SqliteBackupAndIntegrityTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_pos_backup_src_{id}.db");
        _backupDir = Path.Combine(Path.GetTempPath(), $"test_pos_backups_{id}");

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
        if (File.Exists(_dbPath)) try { File.Delete(_dbPath); } catch { }
        if (Directory.Exists(_backupDir)) try { Directory.Delete(_backupDir, true); } catch { }
    }

    [Fact]
    public async Task CreateOnlineBackup_ShouldProduceVerifiedReadableSnapshot()
    {
        using (var context = new RetailDbContext(_options))
        {
            // Seed a product to verify data inside backup
            var p = new Product(Guid.NewGuid(), "BACKUP-SKU", "Backup Test Item", UnitOfMeasure.Piece, Money.Zero(), Money.FromDecimal(50m), 1);
            context.Products.Add(p);
            await context.SaveChangesAsync();

            var backupService = new SqliteDatabaseBackupService(context);
            var backupPath = await backupService.CreateBackupAsync(_backupDir);

            Assert.True(File.Exists(backupPath));

            // Verify backup file can be opened and read
            var backupOptions = new DbContextOptionsBuilder<RetailDbContext>()
                .UseSqlite($"Data Source={backupPath}")
                .Options;

            using var backupContext = new RetailDbContext(backupOptions);
            var check = await DatabaseIntegrityChecker.CheckIntegrityAsync(backupContext);

            Assert.Equal(DatabaseHealthStatus.Healthy, check.Status);
            Assert.True(check.QuickCheckValid);
            Assert.True(check.ForeignKeysValid);

            var readProduct = await backupContext.Products.FirstAsync(prod => prod.SKU == "BACKUP-SKU");
            Assert.Equal("Backup Test Item", readProduct.Name);
            Assert.Equal(5000, readProduct.SellingPrice.MinorUnits);
        }
    }

    [Fact]
    public async Task PragmaIntegrityAndForeignKeyChecks_ProduceZeroViolationsAndOk()
    {
        using var context = new RetailDbContext(_options);
        await DatabaseInitializer.InitializeDatabaseAsync(context);

        var conn = context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // 1. PRAGMA foreign_key_check; -> Expected: 0 violations
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_key_check;";
            using var reader = await cmd.ExecuteReaderAsync();
            var violations = 0;
            while (await reader.ReadAsync())
            {
                violations++;
            }
            Assert.Equal(0, violations);
        }

        // 2. PRAGMA integrity_check; -> Expected: ok
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = (string?)(await cmd.ExecuteScalarAsync());
            Assert.Equal("ok", result);
        }
    }
}
