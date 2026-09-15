namespace RetailPOS.Infrastructure.Tests;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Contracts.Backup;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using Xunit;

public class Phase11CrashMatrixAndRecoveryTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _backupsDir;
    private readonly string _recoveryMarkerPath;
    private readonly string _securityDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public Phase11CrashMatrixAndRecoveryTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"grozet_p11_crash_{Guid.NewGuid():N}");
        _dataDir = Path.Combine(_testDir, "Data");
        _backupsDir = Path.Combine(_testDir, "Backups");
        _securityDir = Path.Combine(_testDir, "Security");
        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_backupsDir);
        Directory.CreateDirectory(_securityDir);

        _recoveryMarkerPath = Path.Combine(_testDir, "restore_state.json");
        _dbPath = Path.Combine(_dataDir, "grozet_pos.db");

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
    public async Task CrashMatrix_Row3_SplitState_ExplicitlyEliminatesOrphanWalShmAndStaging_RestoresSafetySnapshot()
    {
        // Setup:
        // Crash after DB move, before WAL move:
        // Rollback DB exists; Primary WAL exists; Primary SHM exists; Primary DB is missing!
        var primaryDbPath = _dbPath;
        var primaryWalPath = $"{_dbPath}-wal";
        var primaryShmPath = $"{_dbPath}-shm";
        var rollbackDbPath = $"{_dbPath}.rollback_staging";
        var rollbackWalPath = $"{_dbPath}.rollback_staging-wal";
        var tmpInstallPath = $"{_dbPath}.tmp_install";
        var safetySnapshotPath = Path.Combine(_backupsDir, "safety_snapshot_row3.db");

        // Close pools so files can be moved
        SqliteConnection.ClearAllPools();

        // 1. Create verified safety snapshot
        CreateValidSqliteFile(safetySnapshotPath);

        // 2. Simulate split state
        if (File.Exists(primaryDbPath)) File.Delete(primaryDbPath);
        CreateValidSqliteFile(rollbackDbPath);
        File.WriteAllText(primaryWalPath, "fake orphan primary wal contents");
        File.WriteAllText(primaryShmPath, "fake orphan primary shm contents");
        File.WriteAllText(tmpInstallPath, "partial tmp install");

        // 3. Write marker: SafetySnapshotCreated
        var marker = new
        {
            RestoreOperationId = Guid.NewGuid(),
            State = "SafetySnapshotCreated",
            SafetySnapshotPath = safetySnapshotPath,
            TargetBackupPath = "target.db",
            TimestampUtc = DateTime.UtcNow,
            Mac = "valid_mac"
        };
        File.WriteAllText(_recoveryMarkerPath, JsonSerializer.Serialize(marker));

        // 4. Initialize services
        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
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

        // ACT: Run Startup Reconciliation
        await reconciler.ReconcileStartupAsync();

        // ASSERT:
        // Gate is Available
        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);

        // Primary DB restored from safety snapshot
        Assert.True(File.Exists(primaryDbPath));

        // CRITICAL INVARIANT: Row 3 explicitly eliminates orphan Primary WAL/SHM and all staging sidecars!
        Assert.False(File.Exists(primaryWalPath), "Orphan Primary WAL was NOT eliminated!");
        Assert.False(File.Exists(primaryShmPath), "Orphan Primary SHM was NOT eliminated!");
        Assert.False(File.Exists(rollbackDbPath), "Rollback DB was NOT eliminated!");
        Assert.False(File.Exists(rollbackWalPath), "Rollback WAL was NOT eliminated!");
        Assert.False(File.Exists(tmpInstallPath), "Temporary staging file was NOT eliminated!");
        Assert.False(File.Exists(_recoveryMarkerPath), "Marker was NOT deleted!");

        // Verify SQLite can open primary DB cleanly and run quick_check
        using var checkConn = new SqliteConnection($"Data Source={primaryDbPath}");
        checkConn.Open();
        using var checkCmd = checkConn.CreateCommand();
        checkCmd.CommandText = "PRAGMA quick_check;";
        var checkRes = (string?)checkCmd.ExecuteScalar();
        Assert.Equal("ok", checkRes);
    }

    [Fact]
    public async Task CrashMatrix_Row4_PairedRollbackSet_MovesBothBackToPrimary_AndResumesAvailable()
    {
        SqliteConnection.ClearAllPools();

        var primaryDbPath = _dbPath;
        var primaryWalPath = $"{_dbPath}-wal";
        var rollbackDbPath = $"{_dbPath}.rollback_staging";
        var rollbackWalPath = $"{_dbPath}-wal.rollback_staging";

        if (File.Exists(primaryDbPath)) File.Delete(primaryDbPath);
        CreateValidSqliteFile(rollbackDbPath);
        File.WriteAllText(rollbackWalPath, "paired rollback wal");

        var marker = new
        {
            RestoreOperationId = Guid.NewGuid(),
            State = "SafetySnapshotCreated",
            SafetySnapshotPath = Path.Combine(_backupsDir, "unused.db"),
            TargetBackupPath = "target.db",
            TimestampUtc = DateTime.UtcNow,
            Mac = "valid_mac"
        };
        File.WriteAllText(_recoveryMarkerPath, JsonSerializer.Serialize(marker));

        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
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
        Assert.True(File.Exists(primaryDbPath));
        Assert.True(File.Exists(primaryWalPath));
        Assert.False(File.Exists(rollbackDbPath));
        Assert.False(File.Exists(rollbackWalPath));
        Assert.False(File.Exists(_recoveryMarkerPath));
    }

    [Fact]
    public async Task CrashMatrix_Row8_MigrationStarted_OverwritesWithSafetySnapshot_AndDeletesMarker()
    {
        SqliteConnection.ClearAllPools();

        var primaryDbPath = _dbPath;
        var rollbackDbPath = $"{_dbPath}.rollback_staging";
        var safetySnapshotPath = Path.Combine(_backupsDir, "safety_snapshot_row8.db");

        CreateValidSqliteFile(safetySnapshotPath);
        CreateValidSqliteFile(rollbackDbPath);
        File.WriteAllText(primaryDbPath, "corrupted half-migrated db");

        var marker = new
        {
            RestoreOperationId = Guid.NewGuid(),
            State = "MigrationStarted",
            SafetySnapshotPath = safetySnapshotPath,
            TargetBackupPath = "target.db",
            TimestampUtc = DateTime.UtcNow,
            Mac = "valid_mac"
        };
        File.WriteAllText(_recoveryMarkerPath, JsonSerializer.Serialize(marker));

        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
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
        Assert.True(File.Exists(primaryDbPath));
        Assert.False(File.Exists(rollbackDbPath));
        Assert.False(File.Exists(_recoveryMarkerPath));
    }

    [Fact]
    public async Task CrashMatrix_Row14_CorruptedMarker_TransitionsToEmergencyLockdown()
    {
        File.WriteAllText(_recoveryMarkerPath, "{ invalid json corrupt content");

        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
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

        Assert.Equal(DatabaseMaintenanceState.EmergencyLockdown, gate.CurrentState);
    }

    [Fact]
    public async Task TwoFilePublication_StateA_OrphanDbWithoutManifest_IsDeletedAndMarkedFailed()
    {
        var orphanDbPath = Path.Combine(_backupsDir, "grozet_pos_orphan.db");
        CreateValidSqliteFile(orphanDbPath);

        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
        var manifestService = new BackupManifestService();

        using var context = new RetailDbContext(_options);
        var backupRepo = new EfBackupRepository(context);
        var unitOfWork = new EfUnitOfWork(context);

        var record = new BackupRecord
        {
            BackupOperationId = Guid.NewGuid(),
            FileName = "grozet_pos_orphan.db",
            FilePath = orphanDbPath,
            SizeBytes = 1000,
            DatabaseSha256 = "FAKE",
            Type = BackupType.Manual,
            Status = BackupStatus.Pending,
            FileState = FileState.Active,
            Origin = BackupOrigin.LocalGenerated,
            StoreId = storeIdentity.StoreId,
            StoreName = storeIdentity.StoreName,
            AppVersion = "1.0.0",
            SchemaVersion = 11,
            CreatedAtUtc = DateTime.UtcNow,
            TriggeredByUserId = "U1",
            TriggeredByUserName = "Admin"
        };
        await backupRepo.InsertAsync(record);
        await unitOfWork.SaveChangesAsync();
        SqliteConnection.ClearAllPools();

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

        Assert.False(File.Exists(orphanDbPath), "Orphan DB was not deleted!");
        var updated = await backupRepo.GetByFileNameAsync("grozet_pos_orphan.db");
        Assert.NotNull(updated);
        Assert.Equal(BackupStatus.Failed, updated.Status);
    }

    [Fact]
    public async Task TwoFilePublication_StateB_OrphanManifestWithoutDb_IsDeleted()
    {
        var orphanManifestPath = Path.Combine(_backupsDir, "grozet_pos_ghost.db.manifest");
        File.WriteAllText(orphanManifestPath, "{\"FileName\": \"grozet_pos_ghost.db\"}");

        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        await storeIdentity.InitializeOrLoadAsync(Guid.NewGuid(), "Crash Store");
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

        Assert.False(File.Exists(orphanManifestPath), "Orphan manifest was not deleted!");
    }

    [Fact]
    public async Task TwoFilePublication_StateD_VerifiedPairWithoutDbRecord_ReconciledOnStartup()
    {
        var validDbPath = Path.Combine(_backupsDir, "grozet_pos_verified_unregistered.db");
        var manifestPath = $"{validDbPath}.manifest";
        CreateValidSqliteFile(validDbPath);

        var storeIdentity = new StoreIdentityService(_securityDir);
        var storeId = Guid.NewGuid();
        await storeIdentity.InitializeOrLoadAsync(storeId, "Crash Store");

        var manifestService = new BackupManifestService();
        var manifest = await manifestService.GenerateManifestAsync(
            validDbPath,
            Guid.NewGuid(),
            BackupType.Manual,
            storeId,
            "Crash Store",
            storeIdentity.GetStoreSigningKey());

        await manifestService.SaveManifestAsync(manifestPath, manifest);

        var gate = new DatabaseOperationGate();
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

        var record = await backupRepo.GetByFileNameAsync("grozet_pos_verified_unregistered.db");
        Assert.NotNull(record);
        Assert.Equal(BackupStatus.Succeeded, record.Status);
        Assert.Equal(manifest.BackupOperationId, record.BackupOperationId);
    }
}
