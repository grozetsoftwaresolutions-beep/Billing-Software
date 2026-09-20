using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Contracts.Common;
using RetailPOS.Contracts.Deployment;
using RetailPOS.Domain.Entities.Configuration;
using RetailPOS.Domain.Entities.Taxes;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Deployment;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class SafeMigrationLifecycleTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _stagingDir;
    private readonly string _securityDir;
    private readonly string _markerPath;
    private readonly string _journalPath;
    private readonly byte[] _hmacKey;
    private readonly FileMigrationAuditJournal _journal;
    private readonly IBackupManifestService _backupManifestService;
    private readonly StoreIdentityService _identityService;

    public SafeMigrationLifecycleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"safe_migration_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_testDir, "Data");
        _stagingDir = Path.Combine(_testDir, "Staging");
        _securityDir = Path.Combine(_testDir, "Security");
        _markerPath = Path.Combine(_stagingDir, "migration_state.json");
        _journalPath = Path.Combine(_testDir, "migration_audit.journal");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_securityDir);

        _hmacKey = RandomNumberGenerator.GetBytes(32);
        _journal = new FileMigrationAuditJournal(_journalPath, _hmacKey);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IBackupManifestService, BackupManifestService>();
        var provider = services.BuildServiceProvider();
        _backupManifestService = provider.GetRequiredService<IBackupManifestService>();

        _identityService = new StoreIdentityService(_securityDir);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { /* ignored */ }
    }

    private static RetailDbContext CreateDbContext(string dbPath)
    {
        var options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Shared")
            .Options;
        return new RetailDbContext(options);
    }

    [Fact]
    public async Task Baseline14_FreshDatabase_EstablishesBaselineAndStep15()
    {
        string dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await using var db = CreateDbContext(dbPath);

        // Ensure fresh DB created
        await DatabaseInitializer.InitializeDatabaseAsync(db);

        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            storeIdentityService: _identityService);

        var result = await runner.RunMigrationLifecycleAsync(db);
        Assert.True(result.IsSuccess);

        int version = await SafeMigrationRunner.GetCurrentSchemaVersionAsync(db);
        Assert.Equal(15, version);

        // Verify schema_migrations table exists and has baseline 14 and step 15
        var conn = db.Database.GetDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version IN (14, 15);";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Baseline14_LegacyIncompleteSchema_FailsClosed()
    {
        string dbPath = Path.Combine(_dataDir, "incomplete_legacy.db");
        // Create an incomplete database with user_version = 0 and missing security fields
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE stores (
    id TEXT PRIMARY KEY,
    store_name TEXT NOT NULL
);
PRAGMA user_version = 0;
";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = CreateDbContext(dbPath);
        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            databaseFileName: "incomplete_legacy.db");

        // Must fail closed because required tables/fields are missing
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunMigrationLifecycleAsync(db));
    }

    [Fact]
    public async Task Migration_Idempotency_MultipleExecutionsAreSafe()
    {
        string dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await using var db = CreateDbContext(dbPath);
        await DatabaseInitializer.InitializeDatabaseAsync(db);

        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            storeIdentityService: _identityService);

        // First run applies step 15
        var res1 = await runner.RunMigrationLifecycleAsync(db);
        Assert.True(res1.IsSuccess);
        Assert.Equal("STATE_G_COMPLETED", res1.CurrentState);

        // Second run is a no-op
        var res2 = await runner.RunMigrationLifecycleAsync(db);
        Assert.True(res2.IsSuccess);
        Assert.Equal("UP_TO_DATE", res2.CurrentState);

        int version = await SafeMigrationRunner.GetCurrentSchemaVersionAsync(db);
        Assert.Equal(15, version);
    }

    [Fact]
    public async Task Migration_Rejects_NewerSchema()
    {
        string dbPath = Path.Combine(_dataDir, "newer.db");
        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = CreateDbContext(dbPath);
        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            databaseFileName: "newer.db");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunMigrationLifecycleAsync(db));
        Assert.Contains("DATABASE_TOO_NEW", ex.Message);
    }

    [Fact]
    public async Task CrashDuringMigration_StateD_TriggersAutomaticDualRollback()
    {
        string dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await using (var db = CreateDbContext(dbPath))
        {
            await DatabaseInitializer.InitializeDatabaseAsync(db);
        }
        SqliteConnection.ClearAllPools();

        // Initialize store identity
        await _identityService.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Test Store");
        string identityPath = Path.Combine(_securityDir, "store_identity.dat");
        string originalIdentityContent = await File.ReadAllTextAsync(identityPath);

        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);

        // Backup DB and identity
        string dbBackup = Path.Combine(opStaging, "pre_migration.db");
        File.Copy(dbPath, dbBackup);
        string idBackup = Path.Combine(opStaging, "pre_migration_identity.dat");
        File.Copy(identityPath, idBackup);

        // Create marker in STATE_D_MIGRATING
        var marker = new MigrationMarker
        {
            State = "STATE_D_MIGRATING",
            OperationId = opId,
            PreMigrationBackupPath = dbBackup,
            PreMigrationBackupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dbBackup))),
            PreMigrationIdentityBackupPath = idBackup,
            PreMigrationIdentityBackupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(idBackup))),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        // Corrupt or alter DB and identity to simulate interrupted migration
        await File.WriteAllTextAsync(dbPath, "CORRUPTED_IN_FLIGHT_DB");
        await File.WriteAllTextAsync(identityPath, "CORRUPTED_IDENTITY");

        // Also create dirty WAL and SHM files to test quarantine
        await File.WriteAllTextAsync(dbPath + "-wal", "DIRTY_WAL_DATA");
        await File.WriteAllTextAsync(dbPath + "-shm", "DIRTY_SHM_DATA");

        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            storeIdentityService: _identityService);

        // Perform recovery
        var rollbackResult = await runner.PerformStartupRecoveryIfNeededAsync();
        Assert.True(rollbackResult.IsSuccess);
        Assert.True(rollbackResult.RollbackPerformed);

        // Verify WAL and SHM were quarantined
        Assert.False(File.Exists(dbPath + "-wal"));
        Assert.False(File.Exists(dbPath + "-shm"));

        // Verify database is functional again
        await using (var dbRecovered = CreateDbContext(dbPath))
        {
            int ver = await SafeMigrationRunner.GetCurrentSchemaVersionAsync(dbRecovered);
            Assert.True(ver >= 14);
        }

        // Verify identity was restored
        string restoredIdentityContent = await File.ReadAllTextAsync(identityPath);
        Assert.Equal(originalIdentityContent, restoredIdentityContent);

        // Verify marker was cleaned up
        Assert.False(File.Exists(_markerPath));
    }

    [Fact]
    public async Task PreUpgradeBackup_BindsDatabaseAndIdentityManifest_WithStableSigningKey()
    {
        string dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await using (var db = CreateDbContext(dbPath))
        {
            await DatabaseInitializer.InitializeDatabaseAsync(db);
        }
        SqliteConnection.ClearAllPools();

        var storeGuid = Guid.NewGuid();
        await _identityService.InitializeOrLoadAsync(storeGuid, "Binding Test Store");
        byte[] expectedSigningKey = _identityService.GetStoreSigningKey();

        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            storeIdentityService: _identityService);

        string opId = Guid.NewGuid().ToString("N");
        var result = await runner.RunSafeMigrationAsync(opId, async () =>
        {
            // Verify inside migration action that staging contains valid pre_migration.db, identity, and signed manifest
            string opStaging = Path.Combine(_stagingDir, opId);
            string backupDb = Path.Combine(opStaging, "pre_migration.db");
            string backupId = Path.Combine(opStaging, "pre_migration_identity.dat");
            string manifestPath = $"{backupDb}.manifest";

            Assert.True(File.Exists(backupDb));
            Assert.True(File.Exists(backupId));
            Assert.True(File.Exists(manifestPath));

            // Verify manifest signature against stable StoreSigningKey
            var manifest = await _backupManifestService.LoadManifestAsync(manifestPath);
            Assert.NotNull(manifest);
            Assert.Equal("PreMigration", manifest.Type);
            Assert.Equal(storeGuid, manifest.StoreId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupDb))), manifest.DatabaseSha256);

            bool sigValid = _backupManifestService.VerifySignature(manifest, expectedSigningKey);
            Assert.True(sigValid, "Pre-upgrade backup manifest signature must be valid against stable StoreSigningKey.");
        });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CrashDuringRollback_StateE_RecoversDeterministicallyOnRestart()
    {
        string dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await using (var db = CreateDbContext(dbPath))
        {
            await DatabaseInitializer.InitializeDatabaseAsync(db);
        }
        SqliteConnection.ClearAllPools();

        await _identityService.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Rollback Store");
        string identityPath = Path.Combine(_securityDir, "store_identity.dat");
        string originalIdentityContent = await File.ReadAllTextAsync(identityPath);

        string opId = Guid.NewGuid().ToString("N");
        string opStaging = Path.Combine(_stagingDir, opId);
        Directory.CreateDirectory(opStaging);

        string dbBackup = Path.Combine(opStaging, "pre_migration.db");
        File.Copy(dbPath, dbBackup);
        string idBackup = Path.Combine(opStaging, "pre_migration_identity.dat");
        File.Copy(identityPath, idBackup);

        // Simulate crash during State E (e.g. process died midway through rollback)
        var marker = new MigrationMarker
        {
            State = "STATE_E_ROLLBACK",
            OperationId = opId,
            PreMigrationBackupPath = dbBackup,
            PreMigrationBackupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dbBackup))),
            PreMigrationIdentityBackupPath = idBackup,
            PreMigrationIdentityBackupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(idBackup))),
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_markerPath, JsonSerializer.Serialize(marker));

        var runner = new SafeMigrationRunner(
            _dataDir, _stagingDir, _markerPath, _journal, _backupManifestService,
            storeIdentityService: _identityService);

        // Perform restart recovery
        var rollbackResult = await runner.PerformStartupRecoveryIfNeededAsync();
        Assert.True(rollbackResult.IsSuccess);
        Assert.True(rollbackResult.RollbackPerformed);

        // Verify DB is valid SQLite
        await using (var dbRecovered = CreateDbContext(dbPath))
        {
            int ver = await SafeMigrationRunner.GetCurrentSchemaVersionAsync(dbRecovered);
            Assert.True(ver >= 14);
        }

        // Verify identity restored
        string restoredIdentityContent = await File.ReadAllTextAsync(identityPath);
        Assert.Equal(originalIdentityContent, restoredIdentityContent);

        // Verify marker cleaned up
        Assert.False(File.Exists(_markerPath));
    }
}
