using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using Xunit;

namespace RetailPOS.Infrastructure.Tests;

public sealed class RestoreRollbackTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _backupsDir;
    private readonly string _securityDir;
    private readonly string _recoveryMarkerPath;
    private readonly string _dbPath;
    private readonly string _identityPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public RestoreRollbackTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"grozet_restore_rollback_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_testDir, "Data");
        _backupsDir = Path.Combine(_testDir, "Backups");
        _securityDir = Path.Combine(_testDir, "Security");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_backupsDir);
        Directory.CreateDirectory(_securityDir);

        _recoveryMarkerPath = Path.Combine(_testDir, "restore_state.json");
        _dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        _identityPath = Path.Combine(_securityDir, "store_identity.dat");

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
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch { }
    }

    private static void CreateValidSqliteFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE dummy (id INT PRIMARY KEY); INSERT INTO dummy VALUES (1);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task ReconcileStartup_InterruptedMigration_RollsBackBothDbAndIdentity()
    {
        var primaryDbPath = _dbPath;
        var safetySnapshotPath = Path.Combine(_backupsDir, "safety_snapshot_db.db");
        var identitySnapshotPath = Path.Combine(_backupsDir, "store_identity_snapshot.bak");

        SqliteConnection.ClearAllPools();
        CreateValidSqliteFile(safetySnapshotPath);

        // Generation B is active in current state
        var storeIdentity = new StoreIdentityService(_securityDir);
        var storeGuid = Guid.NewGuid();
        var signingKey = RandomNumberGenerator.GetBytes(32);
        var keyB = new string('B', 64).ToCharArray();
        await storeIdentity.InitializeNewStoreIdentityAsync(storeGuid, "Active Gen B", signingKey, keyB);
        Assert.True(storeIdentity.ValidateMasterRecoveryKey(new string('B', 64).ToCharArray()));

        // Identity safety snapshot holds Gen B
        storeIdentity.CreateIdentitySafetySnapshot(identitySnapshotPath);
        Assert.True(File.Exists(identitySnapshotPath));

        // Simulate restore attempted to change identity to Gen A, but crashed during MigrationStarted!
        var keyA = new string('A', 64).ToCharArray();
        await storeIdentity.InitializeNewStoreIdentityAsync(storeGuid, "Interrupted Gen A", signingKey, keyA);
        Assert.True(storeIdentity.ValidateMasterRecoveryKey(new string('A', 64).ToCharArray()));

        // Write marker with IdentitySafetySnapshotPath: MigrationStarted
        var marker = new
        {
            RestoreOperationId = Guid.NewGuid(),
            State = "MigrationStarted",
            SafetySnapshotPath = safetySnapshotPath,
            IdentitySafetySnapshotPath = identitySnapshotPath,
            TargetBackupPath = "target.db",
            TimestampUtc = DateTime.UtcNow,
            Mac = "valid_mac"
        };
        File.WriteAllText(_recoveryMarkerPath, JsonSerializer.Serialize(marker));

        var gate = new DatabaseOperationGate();
        var manifestService = new BackupManifestService();
        using var context = new RetailDbContext(_options);
        var backupRepo = new EfBackupRepository(context);
        var unitOfWork = new EfUnitOfWork(context);

        var reconciler = new FilesystemReconciliationEngine(
            gate,
            storeIdentity,
            manifestService,
            backupRepo,
            unitOfWork,
            customDataDir: _dataDir,
            customBackupsDir: _backupsDir,
            customRecoveryMarkerPath: _recoveryMarkerPath);

        await reconciler.ReconcileStartupAsync();

        // Both DB and identity must have been rolled back
        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);
        Assert.False(File.Exists(_recoveryMarkerPath));

        // Re-load identity and verify it was rolled back to Gen B
        await storeIdentity.ReloadActiveIdentityAsync();
        Assert.True(storeIdentity.ValidateMasterRecoveryKey(new string('B', 64).ToCharArray()));
    }

    [Fact]
    public async Task ReconcileStartup_InterruptedVerification_RollsBackBothDbAndIdentity()
    {
        var primaryDbPath = _dbPath;
        var safetySnapshotPath = Path.Combine(_backupsDir, "safety_snapshot_db.db");
        var identitySnapshotPath = Path.Combine(_backupsDir, "store_identity_snapshot.bak");

        SqliteConnection.ClearAllPools();
        CreateValidSqliteFile(safetySnapshotPath);

        var storeIdentity = new StoreIdentityService(_securityDir);
        var storeGuid = Guid.NewGuid();
        var signingKey = RandomNumberGenerator.GetBytes(32);
        var keyB = new string('B', 64).ToCharArray();
        await storeIdentity.InitializeNewStoreIdentityAsync(storeGuid, "Active Gen B", signingKey, keyB);

        storeIdentity.CreateIdentitySafetySnapshot(identitySnapshotPath);

        var keyA = new string('A', 64).ToCharArray();
        await storeIdentity.InitializeNewStoreIdentityAsync(storeGuid, "Interrupted Gen A", signingKey, keyA);

        var marker = new
        {
            RestoreOperationId = Guid.NewGuid(),
            State = "VerificationStarted",
            SafetySnapshotPath = safetySnapshotPath,
            IdentitySafetySnapshotPath = identitySnapshotPath,
            TargetBackupPath = "target.db",
            TimestampUtc = DateTime.UtcNow,
            Mac = "valid_mac"
        };
        File.WriteAllText(_recoveryMarkerPath, JsonSerializer.Serialize(marker));

        var gate = new DatabaseOperationGate();
        var manifestService = new BackupManifestService();
        using var context = new RetailDbContext(_options);
        var backupRepo = new EfBackupRepository(context);
        var unitOfWork = new EfUnitOfWork(context);

        var reconciler = new FilesystemReconciliationEngine(
            gate,
            storeIdentity,
            manifestService,
            backupRepo,
            unitOfWork,
            customDataDir: _dataDir,
            customBackupsDir: _backupsDir,
            customRecoveryMarkerPath: _recoveryMarkerPath);

        await reconciler.ReconcileStartupAsync();

        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);
        Assert.False(File.Exists(_recoveryMarkerPath));

        await storeIdentity.ReloadActiveIdentityAsync();
        Assert.True(storeIdentity.ValidateMasterRecoveryKey(new string('B', 64).ToCharArray()));
    }
}
