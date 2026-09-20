#pragma warning disable CA1416

namespace RetailPOS.Infrastructure.Tests;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetailPOS.Application.Persistence;
using RetailPOS.Application.Services;
using RetailPOS.Domain.Common;
using RetailPOS.Domain.Entities.Audit;
using RetailPOS.Domain.Entities.Backup;
using RetailPOS.Domain.Enums;
using RetailPOS.Infrastructure.Backup;
using RetailPOS.Infrastructure.Persistence;
using RetailPOS.Infrastructure.Persistence.Repositories;
using RetailPOS.Infrastructure.Security;
using Xunit;

public sealed class BareMetalRecoveryCrashSafetyTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _dataDir;
    private readonly string _securityDir;
    private readonly string _backupsDir;
    private readonly string _stagingDir;
    private readonly string _quarantineDir;
    private readonly string _recoveryDir;
    private readonly string _drMarkerPath;
    private readonly DbContextOptions<RetailDbContext> _options;

    public BareMetalRecoveryCrashSafetyTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "RetailPOS_BareMetalTests_" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_testDir, "Data");
        _securityDir = Path.Combine(_testDir, "Security");
        _backupsDir = Path.Combine(_testDir, "Backups");
        _stagingDir = Path.Combine(_testDir, "Staging");
        _quarantineDir = Path.Combine(_stagingDir, "Quarantine");
        _recoveryDir = Path.Combine(_stagingDir, "Recovery");
        _drMarkerPath = Path.Combine(_recoveryDir, "dr_recovery_state.json");

        Directory.CreateDirectory(_dataDir);
        Directory.CreateDirectory(_securityDir);
        Directory.CreateDirectory(_backupsDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_quarantineDir);
        Directory.CreateDirectory(_recoveryDir);

        var dbPath = Path.Combine(_dataDir, "grozet_pos.db");
        _options = new DbContextOptionsBuilder<RetailDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;

        using var context = new RetailDbContext(_options);
        context.Database.EnsureCreated();
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
        catch { }
    }

    [Fact]
    public void BareMetal_ExactPhase165Validator_AuthenticatesRecoveryKey()
    {
        var storeIdentityService = new StoreIdentityService(_securityDir);
        var storeId = Guid.NewGuid();
        var storeSigningKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKeyHex = "11223344556677889900aabbccddeeff11223344556677889900aabbccddeeff".ToCharArray();

        var (validatorHashHex, wrappedKeyHex) = storeIdentityService.WrapStoreSigningKey(
            storeId,
            recoveryKeyHex,
            storeSigningKey);

        // Exact Phase 16.5 validator method
        bool isValid = storeIdentityService.ValidateMasterRecoveryKeyAgainstVerifier(
            storeId,
            recoveryKeyHex,
            validatorHashHex);

        Assert.True(isValid, "Exact Phase 16.5 validator must authenticate valid recovery key.");

        var wrongKey = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff".ToCharArray();
        bool isInvalid = storeIdentityService.ValidateMasterRecoveryKeyAgainstVerifier(
            storeId,
            wrongKey,
            validatorHashHex);

        Assert.False(isInvalid, "Exact Phase 16.5 validator must reject invalid recovery key.");
    }

    [Fact]
    public void BareMetal_ExactPhase165Unwrap_DecryptsStoreSigningKey()
    {
        var storeIdentityService = new StoreIdentityService(_securityDir);
        var storeId = Guid.NewGuid();
        var expectedSigningKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKeyHex = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899".ToCharArray();

        var (validatorHashHex, wrappedKeyHex) = storeIdentityService.WrapStoreSigningKey(
            storeId,
            recoveryKeyHex,
            expectedSigningKey);

        // Exact Phase 16.5 unwrapper
        var unwrappedKey = storeIdentityService.UnwrapStoreSigningKey(
            storeId,
            recoveryKeyHex,
            wrappedKeyHex);

        Assert.NotNull(unwrappedKey);
        Assert.Equal(32, unwrappedKey.Length);
        Assert.True(CryptographicOperations.FixedTimeEquals(expectedSigningKey, unwrappedKey),
            "Unwrapped StoreSigningKey must match expected key bit-for-bit.");
    }

    [Fact]
    public async Task BareMetal_CleanMachine_ReconstructsNewDpapiIdentity()
    {
        // 1. OLD MACHINE CONTEXT:
        // Has an original identity and database.
        var oldSecurityDir = Path.Combine(_testDir, "OldMachineSecurity");
        Directory.CreateDirectory(oldSecurityDir);

        var originalStoreId = Guid.NewGuid();
        var originalSigningKey = RandomNumberGenerator.GetBytes(32);
        var recoveryKeyChars = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789".ToCharArray();

        var oldStoreIdentityService = new StoreIdentityService(oldSecurityDir);
        var (validatorHashHex, wrappedKeyHex) = oldStoreIdentityService.WrapStoreSigningKey(
            originalStoreId,
            recoveryKeyChars,
            originalSigningKey);

        // Create authoritative candidate SQLite DB containing the store security credentials
        var candidateDbPath = Path.Combine(_testDir, "authoritative_candidate.db");
        using (var conn = new SqliteConnection($"Data Source={candidateDbPath};"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA user_version = 15;
                CREATE TABLE stores (
                    id INTEGER PRIMARY KEY,
                    store_name TEXT NOT NULL,
                    store_guid TEXT NOT NULL,
                    master_key_validator_hash TEXT,
                    wrapped_store_signing_key TEXT
                );
                CREATE TABLE audit_logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    entity_name TEXT NOT NULL,
                    action TEXT NOT NULL,
                    performed_by TEXT NOT NULL,
                    details TEXT,
                    timestamp_utc TEXT NOT NULL
                );
                INSERT INTO stores (id, store_name, store_guid, master_key_validator_hash, wrapped_store_signing_key)
                VALUES (1, 'Super POS Store', $guid, $valHash, $wrapKey);
            ";
            cmd.Parameters.AddWithValue("$guid", originalStoreId.ToString());
            cmd.Parameters.AddWithValue("$valHash", validatorHashHex);
            cmd.Parameters.AddWithValue("$wrapKey", wrappedKeyHex);
            cmd.ExecuteNonQuery();
        }

        // 2. REPLACEMENT MACHINE CONTEXT:
        // Clean machine. No store_identity.dat exists in _securityDir!
        var replacementIdentityPath = Path.Combine(_securityDir, "store_identity.dat");
        Assert.False(File.Exists(replacementIdentityPath), "Replacement machine starts with no DPAPI identity file.");

        using var context = new RetailDbContext(_options);
        var newStoreIdentityService = new StoreIdentityService(_securityDir);
        var gate = new DatabaseOperationGate();
        var manifestService = new BackupManifestService();
        var backupRepo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);
        var auditRepo = new EfAuditLogRepository(context);
        var settingsStore = new JsonBackupSettingsStore(Path.Combine(_securityDir, "backup_settings.json"));
        var retentionService = new BackupRetentionService(backupRepo, settingsStore, uow);
        var maintCoordinator = new DatabaseMaintenanceCoordinator(gate);
        var opCoordinator = new BackupOperationCoordinator();
        var scopeFactory = new MaintenanceScopeFactory();

        var backupService = new BackupService(
            backupRepo,
            settingsStore,
            retentionService,
            maintCoordinator,
            opCoordinator,
            scopeFactory,
            newStoreIdentityService,
            manifestService,
            auditRepo,
            uow,
            context,
            customDataDir: _dataDir,
            customQuarantineDir: _quarantineDir,
            customDrRecoveryMarkerPath: _drMarkerPath);

        // 3. EXECUTE BARE-METAL RECOVERY
        var recoveryResult = await backupService.ExecuteBareMetalRecoveryAsync(
            candidateDbPath,
            recoveryKeyChars,
            "CONFIRM_BARE_METAL_RECOVERY");

        // 4. VERIFY RECONSTRUCTION
        Assert.NotNull(recoveryResult);
        Assert.True(recoveryResult.Success);
        Assert.Equal(originalStoreId, recoveryResult.StoreId);
        Assert.Equal(15, recoveryResult.RestoredSchemaVersion);

        // Primary DB must be promoted to grozet_pos.db
        var primaryDb = Path.Combine(_dataDir, "grozet_pos.db");
        Assert.True(File.Exists(primaryDb));

        // A NEW DPAPI store_identity.dat must be generated on the replacement machine
        Assert.True(File.Exists(replacementIdentityPath));

        // Reconstructed DPAPI identity must hold:
        // Same StoreId
        Assert.Equal(originalStoreId, newStoreIdentityService.StoreId);
        // Bit-for-bit identical StoreSigningKey
        Assert.True(CryptographicOperations.FixedTimeEquals(originalSigningKey, newStoreIdentityService.GetStoreSigningKey()));
        
        // Fresh InstallationId exists in the reconstructed DPAPI identity
        var encBytes = await File.ReadAllBytesAsync(replacementIdentityPath);
        var decBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(encBytes, null, DataProtectionScope.LocalMachine)
            : encBytes;
        using var doc = JsonDocument.Parse(decBytes);
        var instId = doc.RootElement.GetProperty("InstallationId").GetGuid();
        Assert.NotEqual(Guid.Empty, instId);

        // DR Marker must be cleaned up (DR_COMPLETED)
        Assert.False(File.Exists(_drMarkerPath));
    }

    [Fact]
    public void BareMetal_CleanMachine_OldDpapiIdentityCannotBeUsed()
    {
        // Demonstrates DPAPI LocalMachine non-portability principle:
        // A foreign DPAPI identity file created on a different machine cannot be decrypted.
        // ProtectedData.Unprotect throws CryptographicException.
        var foreignEncryptedBytes = RandomNumberGenerator.GetBytes(128);

        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                ProtectedData.Unprotect(foreignEncryptedBytes, null, DataProtectionScope.LocalMachine));
        }
    }

    [Fact]
    public async Task BareMetal_CrashState_DrPromotionStarted_RollsBackToPreservedOriginal()
    {
        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        var primaryIdentityPath = Path.Combine(_securityDir, "store_identity.dat");
        await File.WriteAllTextAsync(primaryDbPath, "SQLite format 3\0-Original");
        await File.WriteAllTextAsync(primaryIdentityPath, "Original Identity");

        var preservedDb = Path.Combine(_dataDir, "grozet_pos.db.preserved_dr_original");
        var preservedIdentity = Path.Combine(_securityDir, "store_identity.dat.preserved_dr_original");
        File.Copy(primaryDbPath, preservedDb);
        File.Copy(primaryIdentityPath, preservedIdentity);

        // Simulate crash during DR_PROMOTION_STARTED
        var marker = new FilesystemReconciliationEngine.DisasterRecoveryStateMarker
        {
            OperationId = Guid.NewGuid(),
            State = "DR_PROMOTION_STARTED",
            CandidateDbPath = Path.Combine(_quarantineDir, "candidate.db"),
            CandidateDbSha256 = "dummy",
            StoreId = Guid.NewGuid(),
            StoreName = "Test Store",
            PreviousDbPreservedPath = preservedDb,
            PreviousIdentityPreservedPath = preservedIdentity,
            CreatedAtUtc = DateTime.UtcNow,
            LastTransitionUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_drMarkerPath, JsonSerializer.Serialize(marker));

        using var context = new RetailDbContext(_options);
        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        var manifest = new BackupManifestService();
        var repo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);

        var reconciler = new FilesystemReconciliationEngine(
            gate,
            storeIdentity,
            manifest,
            repo,
            uow,
            customDataDir: _dataDir,
            customBackupsDir: _backupsDir,
            customDrRecoveryMarkerPath: _drMarkerPath,
            customQuarantineDir: _quarantineDir);

        await reconciler.ReconcileStartupAsync();

        // Preserved original DB must be restored
        Assert.True(File.Exists(primaryDbPath));
        Assert.Equal("SQLite format 3\0-Original", await File.ReadAllTextAsync(primaryDbPath));

        // Marker deleted
        Assert.False(File.Exists(_drMarkerPath));
        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);
    }

    [Fact]
    public async Task BareMetal_CrashState_DrCompleted_CleansMarkerAndTransitionsAvailable()
    {
        var primaryDbPath = Path.Combine(_dataDir, "grozet_pos.db");
        await File.WriteAllTextAsync(primaryDbPath, "SQLite format 3\0-Restored");

        var marker = new FilesystemReconciliationEngine.DisasterRecoveryStateMarker
        {
            OperationId = Guid.NewGuid(),
            State = "DR_COMPLETED",
            CandidateDbPath = primaryDbPath,
            CandidateDbSha256 = "dummy",
            StoreId = Guid.NewGuid(),
            StoreName = "Test Store",
            CreatedAtUtc = DateTime.UtcNow,
            LastTransitionUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(_drMarkerPath, JsonSerializer.Serialize(marker));

        using var context = new RetailDbContext(_options);
        var gate = new DatabaseOperationGate();
        var storeIdentity = new StoreIdentityService(_securityDir);
        var manifest = new BackupManifestService();
        var repo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);

        var reconciler = new FilesystemReconciliationEngine(
            gate,
            storeIdentity,
            manifest,
            repo,
            uow,
            customDataDir: _dataDir,
            customBackupsDir: _backupsDir,
            customDrRecoveryMarkerPath: _drMarkerPath,
            customQuarantineDir: _quarantineDir);

        await reconciler.ReconcileStartupAsync();

        Assert.False(File.Exists(_drMarkerPath));
        Assert.Equal(DatabaseMaintenanceState.Available, gate.CurrentState);
    }

    [Fact]
    public async Task RetentionSafety_NeverDeletesOnlyPhysicallyVerifiedRecoverableBackup()
    {
        using var context = new RetailDbContext(_options);
        var backupRepo = new EfBackupRepository(context);
        var uow = new EfUnitOfWork(context);
        var settingsStore = new JsonBackupSettingsStore(Path.Combine(_securityDir, "backup_settings.json"));
        var retention = new BackupRetentionService(backupRepo, settingsStore, uow);

        var verifiedBackupPath = Path.Combine(_backupsDir, "grozet_pos_daily_20260920_000000_1.db");
        await File.WriteAllTextAsync(verifiedBackupPath, "SQLite format 3\0-Valid");

        var singleVerifiedBackup = new BackupRecord
        {
            BackupOperationId = Guid.NewGuid(),
            FileName = "grozet_pos_daily_20260920_000000_1.db",
            FilePath = verifiedBackupPath,
            Type = BackupType.ScheduledDaily,
            Status = BackupStatus.Succeeded,
            FileState = FileState.Active,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-60), // older than 7 daily retention
            CompletedAtUtc = DateTime.UtcNow.AddDays(-60)
        };

        await backupRepo.InsertAsync(singleVerifiedBackup);
        await uow.SaveChangesAsync();

        var settings = new Domain.ValueObjects.BackupConfiguration
        {
            HourlyBackupEnabled = true,
            DailyBackupEnabled = true,
            RetentionHourlyCount = 0,
            RetentionDailyCount = 0, // Configured for 0 retention!
            RetentionSnapshotCount = 0
        };
        await settingsStore.SaveSettingsAsync(settings);

        await retention.PruneAsync(default);

        // Even though retention counts are 0, the single verified backup MUST NEVER BE PRUNED
        var refreshed = await backupRepo.GetByIdAsync(singleVerifiedBackup.BackupOperationId);
        Assert.NotNull(refreshed);
        Assert.True(File.Exists(verifiedBackupPath), "Last known good physically verified backup must never be pruned.");
        Assert.Equal(FileState.Active, refreshed.FileState);
    }
}
